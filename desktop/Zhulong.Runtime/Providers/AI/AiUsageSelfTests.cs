using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Zhulong.Runtime.Providers.AI;

public static class AiUsageSelfTests
{
    public static int Run(string directory) => RunAsync(directory).GetAwaiter().GetResult();

    public static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var assertions = 0;
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
        var test = Path.GetFullPath(Path.Combine(directory, "ai-" + Guid.NewGuid().ToString("N")));
        var home = Path.Combine(test, "codex");
        var logs = Path.Combine(home, "sessions");
        var now = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(logs);
        string Line(string type, object payload, int seconds = 0) => JsonSerializer.Serialize(new
        { timestamp = now.AddSeconds(seconds).ToString("O"), type, payload }) + "\n";
        var meta = Line("session_meta", new { id = "runtime-fixture" });
        object Usage(int input, int output) => new { input_tokens = input, output_tokens = output, total_tokens = input + output };
        string Count(int input, int output, int totalInput, int totalOutput, int seconds) => Line("event_msg", new
        {
            type = "token_count", info = new { last_token_usage = Usage(input, output), total_token_usage = Usage(totalInput, totalOutput) }
        }, seconds);
        var bad = Path.Combine(logs, "bad.jsonl");
        var normal = Path.Combine(logs, "normal.jsonl");
        File.WriteAllText(bad, meta + new string('x', 4 * 1024 * 1024 + 1024) + "\n", new UTF8Encoding(false));
        File.WriteAllText(normal, meta + Count(10, 2, 10, 2, 1), new UTF8Encoding(false));
        var target = Path.Combine(test, "ai.db");
        await using (var service = new AiUsageService(target, home))
        {
            using (var first = JsonDocument.Parse(await service.RefreshAsync()))
            {
                Check(!string.IsNullOrWhiteSpace(first.RootElement.GetProperty("error").GetString()), "超限来源须产生可见错误");
                Check(!first.RootElement.GetProperty("scanning").GetBoolean(), "异常后刷新锁必须释放");
                Check(first.RootElement.GetProperty("bytes_read").GetInt64() <= 16 * 1024 * 1024, "每轮来源读取应受预算约束");
            }
            using (var second = JsonDocument.Parse(await service.RefreshAsync()))
            {
                Check(second.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null, "后续来源应恢复正常扫描");
                Check(second.RootElement.GetProperty("report").GetProperty("totals").GetProperty("total_tokens").GetInt64() == 12, "坏来源不能阻止正常请求计账");
                Check(second.RootElement.GetProperty("report").GetProperty("tool").GetString() == "codex", "工具与模型维度应分开");
            }
            using (var connection = Open(target))
            {
                using var cursor = connection.CreateCommand();
                cursor.CommandText = "SELECT offset FROM source_cursors WHERE path=$path";
                cursor.Parameters.AddWithValue("$path", bad);
                Check(Convert.ToInt64(cursor.ExecuteScalar()) == Encoding.UTF8.GetByteCount(meta), "超限行不能推进已提交游标");
            }
            File.Delete(bad);
            using (var unchanged = JsonDocument.Parse(await service.RefreshAsync()))
                Check(unchanged.RootElement.GetProperty("bytes_read").GetInt64() == 0, "无变化来源不得重读日志内容");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                var rejected = false;
                try { await service.RefreshAsync(canceled.Token); } catch (OperationCanceledException) { rejected = true; }
                Check(rejected, "显式取消必须生效");
            }
            File.AppendAllText(normal, Count(10, 2, 20, 4, 2), new UTF8Encoding(false));
            using (var connection = Open(target)) Execute(connection,
                "CREATE TRIGGER fixture_abort BEFORE INSERT ON usage_records BEGIN SELECT RAISE(ABORT,'fixture failure'); END");
            using (var failure = JsonDocument.Parse(await service.RefreshAsync()))
                Check(failure.RootElement.GetProperty("error").ValueKind == JsonValueKind.String, "必须实际触发写入失败");
            using (var connection = Open(target))
            {
                Check(Scalar(connection, "SELECT SUM(total_tokens) FROM usage_records") == 12, "写入失败应回滚用量");
                Execute(connection, "DROP TRIGGER fixture_abort");
            }
            for (var i = 0; i < 3; i++) await service.RefreshAsync();
            using (var recovered = JsonDocument.Parse(await service.ReportAsync()))
                Check(recovered.RootElement.GetProperty("report").GetProperty("totals").GetProperty("total_tokens").GetInt64() == 24,
                    "异常和取消后重试不得漏计或重复");
            await service.StopAsync();
        }

        await using (var absent = new AiUsageService(Path.Combine(test, "absent.db"), Path.Combine(test, "missing")))
        using (var missing = JsonDocument.Parse(await absent.RefreshAsync()))
        {
            Check(missing.RootElement.GetProperty("source_state").GetString() == "not_observed", "来源缺失应为未观察");
            Check(missing.RootElement.GetProperty("report").ValueKind == JsonValueKind.Null, "来源缺失不能伪装成零用量");
        }

        var sourcePath = Path.Combine(test, "state.sqlite");
        using (var source = Open(sourcePath))
        using (var store = new UsageStore(Path.Combine(test, "snapshots.db")))
        {
            Execute(source, "PRAGMA journal_mode=WAL;CREATE TABLE threads(id TEXT PRIMARY KEY,tokens_used INTEGER NOT NULL,updated_at INTEGER NOT NULL);CREATE INDEX thread_updated ON threads(updated_at,id)");
            var epoch = now.ToUnixTimeSeconds();
            for (var i = 0; i < 400; i++) Execute(source,
                $"INSERT INTO threads VALUES('t{i:D3}',{i * 10},{epoch + i})");
            Check(File.Exists(sourcePath + "-wal"), "夹具必须保留活跃 WAL");
            var first = CodexSessionSnapshots.ReadBatchInto(store, sourcePath);
            Check(first.Rows == 256 && first.HasMore, "元数据读取须按256行分页");
            var second = CodexSessionSnapshots.ReadBatchInto(store, sourcePath);
            Check(second.Rows == 144 && !second.HasMore, "元数据分页应继续而非重头扫描");
            Check(Scalar(store.Connection, "SELECT COUNT(*) FROM session_snapshots") == 400, "分页元数据应完整保存");
            Check(Scalar(store.Connection, "SELECT total_tokens FROM session_snapshots WHERE session_id='t000'") == 0, "零累计快照必须原样保留");
            Execute(source, $"UPDATE threads SET tokens_used=999,updated_at={epoch + 400} WHERE id='t000'");
            var next = CodexSessionSnapshots.ReadBatchInto(store, sourcePath);
            Check(next.Rows <= 3, "后续元数据只读水位变化与重叠秒");
            Check(Scalar(store.Connection, "SELECT total_tokens FROM session_snapshots WHERE session_id='t000'") == 999, "快照应覆盖而非累计相加");
            Check(Scalar(store.Connection, "SELECT COUNT(*) FROM usage_records") == 0, "状态快照不能贡献请求用量");
            Check(Scalar(source, "SELECT COUNT(*) FROM threads") == 400, "来源表不得被修改");

            using var cancel = new CancellationTokenSource();
            Execute(store.Connection, "DELETE FROM metadata_sync;DELETE FROM session_snapshots");
            store.Connection.CreateFunction("fixture_cancel", () => { cancel.Cancel(); return 0; });
            Execute(store.Connection, "CREATE TRIGGER fixture_cancel_trigger BEFORE INSERT ON session_snapshots WHEN NEW.session_id='t003' BEGIN SELECT fixture_cancel(); END");
            var canceled = false;
            try { CodexSessionSnapshots.ReadBatchInto(store, sourcePath, 256, cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "必须在元数据批次中途真实触发取消");
            Check(Scalar(store.Connection, "SELECT COUNT(*) FROM session_snapshots") == 0, "取消必须回滚已写快照");
            Check(Scalar(store.Connection, "SELECT COUNT(*) FROM metadata_sync") == 0, "取消必须回滚同步游标");
            Execute(store.Connection, "DROP TRIGGER fixture_cancel_trigger");
            Check(CodexSessionSnapshots.ReadBatchInto(store, sourcePath).Rows == 256, "取消后必须可从同一批次重试");
        }
        await CheckStableFileMetadataAsync(test, now, Check);
        return assertions;
    }

    static async Task CheckStableFileMetadataAsync(string test, DateTimeOffset now, Action<bool, string> check)
    {
        var home = Path.Combine(test, "stable-metadata");
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "state_5.sqlite");
        var target = Path.Combine(test, "stable-metadata-target.db");
        using var writer = Open(path);
        Execute(writer, "PRAGMA journal_mode=WAL;PRAGMA wal_autocheckpoint=0;CREATE TABLE threads(id TEXT PRIMARY KEY,tokens_used INTEGER NOT NULL,updated_at INTEGER NOT NULL);CREATE INDEX idx_threads_updated_at ON threads(updated_at DESC,id DESC)");
        Execute(writer, $"INSERT INTO threads VALUES('stable-counter',100,{now.ToUnixTimeSeconds()})");
        Execute(writer, "PRAGMA wal_checkpoint(RESTART);UPDATE threads SET tokens_used=101");
        await using var service = new AiUsageService(target, home);
        using (var initial = JsonDocument.Parse(await service.RefreshAsync()))
            check(initial.RootElement.GetProperty("report").GetProperty("metadata_sessions")[0].GetProperty("total_tokens").GetInt64() == 101, "元数据初次快照必须可用");
        using (var targetWriter = Open(target))
        {
            Execute(targetWriter, "CREATE TABLE fixture_meta_writes(count INTEGER);INSERT INTO fixture_meta_writes VALUES(0);CREATE TRIGGER fixture_meta_write AFTER UPDATE ON session_snapshots BEGIN UPDATE fixture_meta_writes SET count=count+1; END");
            await service.RefreshAsync();
            check(Scalar(targetWriter, "SELECT count FROM fixture_meta_writes") == 0, "没有外部提交时不能重导元数据");
        }
        var database = new FileInfo(path); var wal = new FileInfo(path + "-wal");
        var databaseLength = database.Length; var walLength = wal.Length;
        var databaseTime = database.LastWriteTimeUtc; var walTime = wal.LastWriteTimeUtc;
        using (var checkpoint = writer.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(RESTART)";
            using var reader = checkpoint.ExecuteReader(); reader.Read();
            check(reader.GetInt64(0) == 0, "观察器不能持有长期读事务阻挡WAL重用");
        }
        using (var transaction = writer.BeginTransaction())
        using (var update = writer.CreateCommand())
        {
            update.Transaction = transaction; update.CommandText = "UPDATE threads SET tokens_used=202";
            update.ExecuteNonQuery(); transaction.Commit();
        }
        File.SetLastWriteTimeUtc(path, databaseTime); File.SetLastWriteTimeUtc(path + "-wal", walTime);
        database.Refresh(); wal.Refresh();
        check(database.Length == databaseLength && wal.Length == walLength && database.LastWriteTimeUtc == databaseTime && wal.LastWriteTimeUtc == walTime,
            "夹具必须保证DB/WAL长度和修改时间全部不变");
        using (var targetWriter = Open(target))
            Execute(targetWriter, "CREATE TRIGGER fixture_meta_abort BEFORE INSERT ON session_snapshots BEGIN SELECT RAISE(ABORT,'fixture metadata failure'); END");
        using (var failed = JsonDocument.Parse(await service.RefreshAsync()))
        {
            check(failed.RootElement.GetProperty("error").ValueKind == JsonValueKind.String, "必须真实触发元数据写入失败");
            check(failed.RootElement.GetProperty("report").GetProperty("metadata_sessions")[0].GetProperty("total_tokens").GetInt64() == 101, "失败应保留上次快照而非发布零值");
        }
        using (var targetWriter = Open(target)) Execute(targetWriter, "DROP TRIGGER fixture_meta_abort");
        using (var refreshed = JsonDocument.Parse(await service.RefreshAsync()))
            check(refreshed.RootElement.GetProperty("report").GetProperty("metadata_sessions")[0].GetProperty("total_tokens").GetInt64() == 202,
                "WAL外部提交即使文件签名相同也必须刷新元数据");
        await service.StopAsync();
        check(Scalar(writer, "SELECT tokens_used FROM threads") == 202, "观察和停止均不得改写源计数");
        writer.Dispose();
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        check(exclusive.CanRead, "Stop必须释放持久观察连接的源文件句柄");
    }

    static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
    }
}
