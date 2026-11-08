using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Zhulong.P0.AI;

// 仅使用运行时生成的合成数据，通过实际扫描与存储路径核对人工期望值。
public static class Acceptance
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly string Root = Path.Combine(Directory.GetCurrentDirectory(), "out", "p0-ai", "fixtures-" + Guid.NewGuid().ToString("N"));
    private static int assertions;
    private static string sqliteVersion = "unknown";

    public static int Run()
    {
        Directory.CreateDirectory(Root);
        try
        {
            TestUsageAndResume();
            TestQuota();
            TestRewriteAndMalformed();
            TestTransactionFailure();
            TestRecordState();
            TestInterleavedStreams();
            TestSessionSnapshots();
            var resource = MeasureBoundedScan();
            var evidence = JsonSerializer.Serialize(new { status = "PASS", assertions, sqlite_version = sqliteVersion, synthetic_data = Root, resource });
            File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "out", "p0-ai", "acceptance.json"), evidence, new UTF8Encoding(false));
            Console.WriteLine(evidence);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "FAIL", assertions, synthetic_data = Root, error = error.ToString() }));
            return 1;
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
    private static string At(int seconds) => Now.AddSeconds(seconds).ToString("O");
    private static object U(long input, long output, long cached = 0, long reasoning = 0) => new
    {
        input_tokens = input, cached_input_tokens = cached, output_tokens = output,
        reasoning_output_tokens = reasoning, total_tokens = input + output
    };
    private static string Meta(string id = "session-A") => Json(new { timestamp = At(0), type = "session_meta", payload = new { id } });
    private static string Context() => Json(new { timestamp = At(0), type = "turn_context", payload = new { model = "fixture-model" } });
    private static string Count(object total, object last, int second = 1) => Json(new
    {
        timestamp = At(second), type = "event_msg", payload = new
        {
            type = "token_count", info = new { total_token_usage = total, last_token_usage = last }
        }
    });
    private static string Record(string id, object usage, int second = 1) => Json(new
    {
        timestamp = At(second), type = "token_usage_record", payload = new { response_id = id, usage }
    });
    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";
    private static JsonDocument Report(UsageStore store, DateTimeOffset? now = null) => JsonDocument.Parse(store.ReportJson(now ?? Now));
    private static long Total(UsageStore store, string name)
    {
        using var result = Report(store);
        return result.RootElement.GetProperty("totals").GetProperty(name).GetInt64();
    }
    private static void Check(bool value, string reason)
    {
        assertions++;
        if (!value) throw new InvalidOperationException(reason);
    }
    private static void Expected(UsageStore store, long input, long cached, long output, long reasoning, long total, long events)
    {
        Check(Total(store, "input_tokens") == input, "输入总量不符");
        Check(Total(store, "cached_input_tokens") == cached, "缓存子集不符");
        Check(Total(store, "output_tokens") == output, "输出总量不符");
        Check(Total(store, "reasoning_tokens") == reasoning, "推理子集不符");
        Check(Total(store, "total_tokens") == total, "总量不得重复叠加缓存或推理");
        Check(Total(store, "event_count") == events, "事件数不符");
    }

    private static void TestUsageAndResume()
    {
        var path = Path.Combine(Root, "session.jsonl");
        var db = Path.Combine(Root, "usage.db");
        var first = Count(U(100, 20, 40, 5), U(100, 20, 40, 5));
        var second = Count(U(150, 30, 60, 7), U(50, 10, 20, 2), 2);
        File.WriteAllText(path, Lines(Meta(), Context(), Record("response-1", U(100, 20, 40, 5)), first, first, second), new UTF8Encoding(false));
        using (var store = new UsageStore(db))
        {
            var scanner = new CodexScanner(store);
            scanner.ScanFile(path);
            Expected(store, 150, 60, 30, 7, 180, 2);
            using var report = Report(store);
            var session = report.RootElement.GetProperty("sessions")[0];
            Check(session.GetProperty("session_id").GetString() == "session-A", "会话身份未保留");
            Check(session.GetProperty("total_tokens").GetInt64() == 180, "会话汇总不符");
            var idle = scanner.ScanFile(path);
            Check(idle.BytesRead == 0 && idle.EventsAdded == 0, "未变化文件不应重读或重计");
            var third = Count(U(175, 35, 65, 8), U(25, 5, 5, 1), 3);
            File.AppendAllText(path, third[..(third.Length / 2)], new UTF8Encoding(false));
            var half = scanner.ScanFile(path);
            Check(!half.HasMore && half.EventsAdded == 0, "半行应等待补齐而不是发布或忙循环");
            File.AppendAllText(path, third[(third.Length / 2)..] + "\n", new UTF8Encoding(false));
            scanner.ScanFile(path);
            Expected(store, 175, 65, 35, 8, 210, 3);
            var copy = Path.Combine(Root, "copy.jsonl");
            File.Copy(path, copy);
            scanner.ScanFile(copy);
            Check(Total(store, "total_tokens") == 210, "复制来源重复计账");
            var fork = Path.Combine(Root, "fork.jsonl");
            File.WriteAllText(fork, File.ReadAllText(path).Replace("session-A", "session-B"), new UTF8Encoding(false));
            scanner.ScanFile(fork);
            Check(Total(store, "total_tokens") == 210, "分叉回放重复计账");
            using (var forkReport = Report(store))
            {
                var sessionRows = forkReport.RootElement.GetProperty("sessions").EnumerateArray().ToArray();
                Check(sessionRows.First(x => x.GetProperty("session_id").GetString() == "session-A").GetProperty("total_tokens").GetInt64() == 210, "父会话关联不完整");
                Check(sessionRows.First(x => x.GetProperty("session_id").GetString() == "session-B").GetProperty("total_tokens").GetInt64() == 210, "分叉会话关联受扫描顺序影响");
            }
            using var reverseStore = new UsageStore(Path.Combine(Root, "reverse.db"));
            var reverseScanner = new CodexScanner(reverseStore);
            reverseScanner.ScanFile(fork);
            reverseScanner.ScanFile(path);
            Check(Total(reverseStore, "total_tokens") == 210, "逆序扫描重复记账");
            using var reverseReport = Report(reverseStore);
            var reverseRows = reverseReport.RootElement.GetProperty("sessions").EnumerateArray().ToArray();
            Check(reverseRows.Length == 2 && reverseRows.All(x => x.GetProperty("total_tokens").GetInt64() == 210), "逆序扫描丢失会话关联");
        }
        using (var store = new UsageStore(db))
        {
            var scanner = new CodexScanner(store);
            Check(scanner.ScanFile(path).BytesRead == 0, "重启丢失已提交游标");
            Expected(store, 175, 65, 35, 8, 210, 3);
            File.WriteAllText(path, Lines(Meta(), Context(), first), new UTF8Encoding(false));
            scanner.ScanFile(path);
            Check(Total(store, "total_tokens") == 210, "截断回放重复计账");
            var ignored = Json(new { timestamp = At(4), type = "ignored", padding = "a" });
            File.AppendAllText(path, ignored + "\n", new UTF8Encoding(false));
            scanner.ScanFile(path);
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"padding\":\"a\"", "\"padding\":\"b\""), new UTF8Encoding(false));
            var rewritten = scanner.ScanFile(path);
            Check(rewritten.BytesRead > 0, "尾部同尺寸重写未被识别");
            Check(Total(store, "total_tokens") == 210, "尾部重放重复计账");
            File.WriteAllText(path, File.ReadAllText(path).Replace("session-A", "session-C"), new UTF8Encoding(false));
            Check(scanner.ScanFile(path).BytesRead > 0, "头部同尺寸重写未被识别");
            Check(Total(store, "total_tokens") == 210, "头部重放重复计账");
        }
    }

    private static void TestQuota()
    {
        var path = Path.Combine(Root, "quota.jsonl");
        using var store = new UsageStore(Path.Combine(Root, "quota.db"));
        var scanner = new CodexScanner(store);
        using (var empty = Report(store))
            Check(empty.RootElement.GetProperty("quota").GetArrayLength() == 0, "缺失额度不得伪造为零或满额");
        var reset = Now.ToUnixTimeSeconds() + 3600;
        var line = Json(new
        {
            timestamp = Now.ToString("O"), type = "event_msg", payload = new
            {
                type = "token_count", info = (object?)null, rate_limits = new
                {
                    limit_id = "codex", plan_type = "plus",
                    primary = new { used_percent = 65.0, window_minutes = 300, resets_at = reset },
                    secondary = new { used_percent = 20.0, window_minutes = 10080, resets_at = reset + 3600 }
                }
            }
        });
        File.WriteAllText(path, Lines(Meta("quota-session"), line), new UTF8Encoding(false));
        scanner.ScanFile(path);
        using (var future = Report(store, DateTimeOffset.FromUnixTimeSeconds(reset - 3600)))
        {
            var quota = future.RootElement.GetProperty("quota")[0];
            Check(quota.GetProperty("limit_id").GetString() == "codex", "额度标识不符");
            Check(quota.GetProperty("primary").GetProperty("used_percent").GetDouble() == 65, "info=null 的额度快照丢失");
            Check(quota.GetProperty("primary").GetProperty("remaining_seconds").GetInt64() == 3600, "额度倒计时不符");
            Check(quota.GetProperty("secondary").GetProperty("remaining_seconds").GetInt64() == 7200, "周窗口倒计时不符");
        }
        using (var expired = Report(store, DateTimeOffset.FromUnixTimeSeconds(reset + 1)))
        {
            var primary = expired.RootElement.GetProperty("quota")[0].GetProperty("primary");
            Check(primary.GetProperty("remaining_seconds").GetInt64() == 0, "到期后倒计时应停在零");
            Check(primary.GetProperty("status").GetString() == "awaiting_refresh", "到期不得擅自重置额度百分比");
            Check(primary.GetProperty("used_percent").GetDouble() == 65, "没有新快照却改写了用量");
        }
        Check(Total(store, "total_tokens") == 0, "额度快照不得贡献 token");
        var unknown = Json(new { timestamp = At(10), type = "event_msg", payload = new { type = "token_count", info = (object?)null,
            rate_limits = new { limit_id = "unknown-window", primary = new { used_percent = 10.0, window_minutes = 300 } } } });
        File.AppendAllText(path, unknown + "\n", new UTF8Encoding(false));
        scanner.ScanFile(path);
        using var missing = Report(store);
        var rows = missing.RootElement.GetProperty("quota").EnumerateArray();
        var unknownRow = rows.First(x => x.GetProperty("limit_id").GetString() == "unknown-window");
        Check(unknownRow.GetProperty("primary").GetProperty("remaining_seconds").ValueKind == JsonValueKind.Null, "无重置时间应保持未知");
        var other = Path.Combine(Root, "other-quota.jsonl");
        File.WriteAllText(other, Lines(Meta("other-quota-session"), line.Replace("65", "75")), new UTF8Encoding(false));
        scanner.ScanFile(other);
        using var parallel = Report(store);
        var codexRows = parallel.RootElement.GetProperty("quota").EnumerateArray().Where(x => x.GetProperty("limit_id").GetString() == "codex").ToArray();
        Check(codexRows.Length == 2, "未知账号的多会话额度被互相覆盖");
        Check(codexRows.Select(x => x.GetProperty("source_session_id").GetString()).Distinct().Count() == 2, "额度来源会话丢失");
        Check(codexRows.Select(x => x.GetProperty("primary").GetProperty("used_percent").GetDouble()).Order().SequenceEqual(new[] { 65.0, 75.0 }), "多会话额度数值未独立保留");
    }


    private static void TestRewriteAndMalformed()
    {
        var path = Path.Combine(Root, "middle-rewrite.jsonl");
        using var store = new UsageStore(Path.Combine(Root, "middle-rewrite.db"));
        var scanner = new CodexScanner(store);
        var padding = Json(new { timestamp = At(50), type = "ignored", padding = new string('p', 1024) });
        var original = Lines(Meta("rewrite-session"), Context(), padding,
            Count(U(100, 20), U(100, 20), 51), padding);
        File.WriteAllText(path, original, new UTF8Encoding(false));
        scanner.ScanFile(path);
        var replacement = original.Replace("\"input_tokens\":100", "\"input_tokens\":200")
            .Replace("\"total_tokens\":120", "\"total_tokens\":220");
        Check(replacement.Length == original.Length, "中段重写夹具尺寸变化");
        File.WriteAllText(path, replacement, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        Check(scanner.ScanFile(path).BytesRead > 0, "头尾相同的中段同尺寸重写未重放");
        Check(Total(store, "total_tokens") == 340, "更正重写应保留旧事件并记录新事件，不能静默覆盖账本");
        File.AppendAllText(path, "{invalid-json}\n", new UTF8Encoding(false));
        scanner.ScanFile(path);
        using (var report = Report(store))
            Check(report.RootElement.GetProperty("sources")[0].GetProperty("malformed_lines").GetInt64() == 1, "坏行未显式计数");
        // 尾部只有一个字节时也必须等待完整换行，下一次追加才能计入。
        var next = Count(U(210, 25), U(10, 5), 52);
        File.AppendAllText(path, next[..1], new UTF8Encoding(false));
        Check(scanner.ScanFile(path).EventsAdded == 0, "单字节尾行被提前发布");
        File.AppendAllText(path, next[1..] + "\n", new UTF8Encoding(false));
        scanner.ScanFile(path);
        Check(Total(store, "total_tokens") == 355, "单字节尾行补齐丢计");
    }

    private static void TestTransactionFailure()
    {
        var db = Path.Combine(Root, "failure.db");
        var path = Path.Combine(Root, "failure.jsonl");
        File.WriteAllText(path, Lines(Meta("failure-session"), Context(), Count(U(20, 5), U(20, 5), 20)), new UTF8Encoding(false));
        using var store = new UsageStore(db);
        using var connection = new SqliteConnection("Data Source=" + db);
        connection.Open();
        sqliteVersion = connection.ServerVersion;
        Check(Version.Parse(sqliteVersion) >= new Version(3, 50, 2), "实际原生 SQLite 版本仍受 CVE-2025-6965 影响");
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER acceptance_abort BEFORE INSERT ON usage_records BEGIN SELECT RAISE(ABORT, 'acceptance failure'); END";
        command.ExecuteNonQuery();
        var scanner = new CodexScanner(store);
        var rejected = false;
        try { scanner.ScanFile(path); } catch (SqliteException) { rejected = true; }
        Check(rejected, "未实际触发写入失败");
        Check(Total(store, "event_count") == 0, "事务失败留下部分账本");
        command.CommandText = "DROP TRIGGER acceptance_abort";
        command.ExecuteNonQuery();
        var retry = scanner.ScanFile(path);
        Check(retry.BytesRead > 0, "事务失败却推进了游标");
        Check(Total(store, "total_tokens") == 25 && Total(store, "event_count") == 1, "失败后重试丢计或重复");
    }

    private static void TestRecordState()
    {
        var state = new ParserState();
        CodexParser.Parse(Meta("compaction-session"), state);
        CodexParser.Parse(Context(), state);
        var first = Count(U(100, 20), U(100, 20), 30);
        CodexParser.Parse(first, state);
        CodexParser.Parse(Record("compaction-response", U(10, 2), 31), state);
        CodexParser.Parse(Json(new { timestamp = At(31), type = "compacted", payload = new { } }), state);
        state = JsonSerializer.Deserialize<ParserState>(Json(state))!;
        var compaction = CodexParser.Parse(Count(U(100, 20), U(100, 20), 32), state);
        Check(compaction.Usage.Count == 1 && compaction.Usage[0].Usage.Total == 12, "跨批压缩补计状态丢失");
        var only = new ParserState();
        CodexParser.Parse(Record("record-only", U(10, 2), 33), only);
        CodexParser.Parse(Json(new { timestamp = At(34), type = "event_msg", payload = new { type = "task_complete" } }), only);
        Check(only.RecordOnly, "未支持的 record-only 用量必须显式标记");
    }


    private static void TestInterleavedStreams()
    {
        var path = Path.Combine(Root, "interleaved.jsonl");
        using var store = new UsageStore(Path.Combine(Root, "interleaved.db"));
        var scanner = new CodexScanner(store);
        // A、B 为独立累计流，B 的小值不是 A 的负增量；恢复 A 要找到其历史基线。
        var a = Count(U(100, 20), U(100, 20), 60);
        var b = Count(U(50, 10), U(50, 10), 61);
        var a2 = Count(U(150, 30), U(50, 10), 62);
        File.WriteAllText(path, Lines(Meta("streams-session"), Context(), a, b, a2, b, a2), new UTF8Encoding(false));
        scanner.ScanFile(path);
        Check(Total(store, "total_tokens") == 240 && Total(store, "event_count") == 3, "交错累计流或重复快照发生多计/少计");
        var zero = new { input_tokens = 0, output_tokens = 0, total_tokens = 0 };
        var sentinelLast = new { input_tokens = 0, output_tokens = 0, total_tokens = 180 };
        File.AppendAllText(path, Lines(Count(zero, sentinelLast, 63), Count(U(20, 5), U(20, 5), 64)), new UTF8Encoding(false));
        scanner.ScanFile(path);
        Check(Total(store, "total_tokens") == 265 && Total(store, "event_count") == 4, "计数器重置哨兵被误作真实用量");
    }


    private static void TestSessionSnapshots()
    {
        var sourcePath = Path.Combine(Root, "source-state.db");
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Pooling = false }.ToString());
        source.Open();
        using var command = source.CreateCommand();
        var updated = Now.ToUnixTimeSeconds();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA wal_autocheckpoint=0;
            CREATE TABLE threads(id TEXT PRIMARY KEY,tokens_used INTEGER,updated_at INTEGER);
            INSERT INTO threads VALUES ('metadata-A',120,$updated),('metadata-B',60,$updated);
            """;
        command.Parameters.AddWithValue("$updated", updated);
        command.ExecuteNonQuery();
        Check(File.Exists(sourcePath + "-wal"), "元数据夹具未进入WAL模式");
        using var store = new UsageStore(Path.Combine(Root, "metadata.db"));
        CodexSessionSnapshots.ReadInto(store, sourcePath);
        using (var first = Report(store))
        {
            var rows = first.RootElement.GetProperty("metadata_sessions").EnumerateArray().ToArray();
            Check(rows.Length == 2, "源WAL会话快照未完整读取");
            Check(rows.First(x => x.GetProperty("session_id").GetString() == "metadata-A").GetProperty("total_tokens").GetInt64() == 120, "会话A累计快照不符");
            Check(rows.First(x => x.GetProperty("session_id").GetString() == "metadata-B").GetProperty("total_tokens").GetInt64() == 60, "会话B累计快照不符");
            Check(rows.All(x => x.GetProperty("source_kind").GetString() == "state_db_cumulative_snapshot"), "元数据快照来源口径丢失");
            Check(rows.All(x => x.GetProperty("source_updated_at").GetInt64() == updated), "源更新时间未按原始epoch保留");
        }
        Check(Total(store, "total_tokens") == 0 && Total(store, "event_count") == 0, "累计元数据混入请求事件账本");
        CodexSessionSnapshots.ReadInto(store, sourcePath);
        using (var repeated = Report(store))
            Check(repeated.RootElement.GetProperty("metadata_sessions").GetArrayLength() == 2, "重复导入累计快照翻倍");
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*),SUM(tokens_used) FROM threads";
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            Check(reader.GetInt64(0) == 2 && reader.GetInt64(1) == 180, "读取适配器改写了来源会话记录");
        }
        command.CommandText = "UPDATE threads SET tokens_used=175,updated_at=$updated WHERE id='metadata-A'";
        command.Parameters.AddWithValue("$updated", updated + 1);
        command.ExecuteNonQuery();
        CodexSessionSnapshots.ReadInto(store, sourcePath);
        using (var refreshed = Report(store))
        {
            var rows = refreshed.RootElement.GetProperty("metadata_sessions").EnumerateArray().ToArray();
            Check(rows.Length == 2, "更新累计快照新增了重复会话行");
            Check(rows.First(x => x.GetProperty("session_id").GetString() == "metadata-A").GetProperty("total_tokens").GetInt64() == 175, "累计快照更新被错误相加或漏读WAL");
            Check(rows.First(x => x.GetProperty("session_id").GetString() == "metadata-A").GetProperty("source_updated_at").GetInt64() == updated + 1, "更新后的源时间不符");
        }
        Check(Total(store, "total_tokens") == 0, "更新后的元数据混入事件总量");
        command.Parameters.Clear();
        command.CommandText = "SELECT tokens_used FROM threads WHERE id='metadata-A'";
        Check(Convert.ToInt64(command.ExecuteScalar()) == 175, "读取适配器改写来源累计计数");
        command.CommandText = "INSERT INTO threads VALUES ('metadata-zero',0,$updated)";
        command.Parameters.AddWithValue("$updated", updated);
        command.ExecuteNonQuery();
        CodexSessionSnapshots.ReadInto(store, sourcePath);
        using (var withZero = Report(store))
        {
            var rows = withZero.RootElement.GetProperty("metadata_sessions").EnumerateArray().ToArray();
            Check(rows.Length == 3 && rows.First(x => x.GetProperty("session_id").GetString() == "metadata-zero").GetProperty("total_tokens").GetInt64() == 0, "真实零用量会话被当成缺失丢弃");
        }
        var failureDb = Path.Combine(Root, "metadata-failure.db");
        using var failedStore = new UsageStore(failureDb);
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = failureDb, Pooling = false }.ToString());
        target.Open();
        using var failCommand = target.CreateCommand();
        failCommand.CommandText = "CREATE TRIGGER acceptance_snapshot_abort BEFORE INSERT ON session_snapshots WHEN NEW.session_id='metadata-B' BEGIN SELECT RAISE(ABORT, 'acceptance metadata failure'); END";
        failCommand.ExecuteNonQuery();
        var failed = false;
        try { CodexSessionSnapshots.ReadInto(failedStore, sourcePath); } catch (SqliteException) { failed = true; }
        Check(failed, "未触发元数据整批写入失败");
        using (var rolledBack = Report(failedStore))
            Check(rolledBack.RootElement.GetProperty("metadata_session_count").GetInt64() == 0, "元数据事务失败留下部分快照");
        failCommand.CommandText = "DROP TRIGGER acceptance_snapshot_abort";
        failCommand.ExecuteNonQuery();
        CodexSessionSnapshots.ReadInto(failedStore, sourcePath);
        using var recovered = Report(failedStore);
        Check(recovered.RootElement.GetProperty("metadata_session_count").GetInt64() == 3, "元数据事务失败后重试不完整");
    }

    private static object MeasureBoundedScan()
    {
        var path = Path.Combine(Root, "large.jsonl");
        var db = Path.Combine(Root, "large.db");
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            writer.Write(Lines(Meta("large-session"), Context()));
            var padding = Json(new { timestamp = At(40), type = "ignored", padding = new string('x', 1024) });
            for (var i = 0; i < 12000; i++) writer.WriteLine(padding);
            writer.WriteLine(Count(U(100, 20), U(100, 20), 41));
        }
        var size = new FileInfo(path).Length;
        Check(size > 10 * 1024 * 1024, "性能样本不足十 MiB");
        using var store = new UsageStore(db);
        var scanner = new CodexScanner(store);
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        long bytes = 0, anchors = 0;
        var batches = 0;
        ScanResult result;
        do
        {
            result = scanner.ScanFile(path);
            bytes += result.BytesRead;
            anchors += result.AnchorBytesRead;
            batches++;
            Check(result.BytesRead <= 4 * 1024 * 1024, "单批读取超过预算");
            Check(batches < 20, "有界扫描未向前推进");
        } while (result.HasMore);
        clock.Stop();
        var coldMs = clock.Elapsed.TotalMilliseconds;
        var cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
        Check(bytes >= size && bytes <= size + 8192, "冷扫描重读量异常");
        Check(Total(store, "total_tokens") == 120, "分批扫描统计不符");
        var idle = scanner.ScanFile(path);
        Check(idle.BytesRead == 0, "大文件无变化扫描重新读取正文");
        var append = Count(U(125, 25), U(25, 5), 42) + "\n";
        File.AppendAllText(path, append, new UTF8Encoding(false));
        var changed = scanner.ScanFile(path);
        Check(changed.BytesRead == Encoding.UTF8.GetByteCount(append), "追加扫描读取了历史正文");
        Check(Total(store, "total_tokens") == 150, "追加用量不符");
        process.Refresh();
        return new
        {
            input_bytes = size, batches, cold_read_bytes = bytes, anchor_read_bytes = anchors,
            cold_elapsed_ms = coldMs, cpu_ms = cpuMs, idle_read_bytes = idle.BytesRead,
            idle_anchor_bytes = idle.AnchorBytesRead, append_read_bytes = changed.BytesRead,
            append_anchor_bytes = changed.AnchorBytesRead, private_bytes_at_end = process.PrivateMemorySize64,
            working_set_at_end = process.WorkingSet64,
            scope = "single_process_synthetic_scan; end memory is not peak or steady-state baseline"
        };
    }
}
