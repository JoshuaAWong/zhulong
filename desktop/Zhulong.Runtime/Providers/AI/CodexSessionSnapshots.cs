using Microsoft.Data.Sqlite;

namespace Zhulong.Runtime.Providers.AI;

// 线程计数是独立快照，不能与 JSONL 请求账本相加。
public static class CodexSessionSnapshots
{
    // 常驻调度使用分页增量；同秒重叠读取，覆盖该秒内相同线程的计数更新。
    public static SnapshotBatch ReadBatchInto(UsageStore store, string sourcePath, int maxRows = 256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(sourcePath);
        if (string.Equals(path, Path.GetFullPath(store.DatabasePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("源状态库与统计库路径必须不同。");
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2
        }.ToString());
        source.Open();
        using var readTransaction = source.BeginTransaction(deferred: true);
        using var transaction = store.Connection.BeginTransaction();
        var cursor = store.ReadMetadataCursor(path, transaction);
        var after = cursor.Pending ? cursor.AfterUpdated : Math.Max(0, cursor.CompletedWatermark - 1);
        var id = cursor.Pending ? cursor.AfterId : "";
        using var command = source.CreateCommand();
        command.Transaction = readTransaction;
        command.CommandText = """
            SELECT id,tokens_used,updated_at FROM threads
            WHERE updated_at >= $after AND (updated_at > $after OR id > $id)
            ORDER BY updated_at,id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$limit", maxRows);
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            id = reader.GetString(0);
            after = reader.GetInt64(2);
            store.SaveSessionSnapshot(id, reader.GetInt64(1), after, path, transaction);
            count++;
        }
        reader.Close();
        var pending = count == maxRows;
        var watermark = pending ? cursor.CompletedWatermark : Math.Max(cursor.CompletedWatermark, after);
        store.SaveMetadataCursor(path, new(watermark, after, id, pending), transaction);
        cancellationToken.ThrowIfCancellationRequested();
        readTransaction.Commit();
        transaction.Commit();
        return new(count, pending);
    }

    public static int ReadInto(UsageStore store, string sourcePath)
    {
        var path = Path.GetFullPath(sourcePath);
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2
        }.ToString());
        source.Open();

        // 使用 SQLite 自身的只读事务读取一致视图，包含现存 WAL 中的已提交记录。
        using var readTransaction = source.BeginTransaction(deferred: true);
        using var transaction = store.Connection.BeginTransaction();
        using var command = source.CreateCommand();
        command.Transaction = readTransaction;
        command.CommandText = "SELECT id,tokens_used,updated_at FROM threads";
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            store.SaveSessionSnapshot(reader.GetString(0), reader.GetInt64(1),
                reader.GetInt64(2), path, transaction);
            count++;
        }
        reader.Close();
        readTransaction.Commit();
        transaction.Commit();
        return count;
    }
}

public sealed record SnapshotBatch(int Rows, bool HasMore);
