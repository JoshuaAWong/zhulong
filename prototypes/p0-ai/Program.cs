using System.Diagnostics;
using System.Text.Json;
using Zhulong.P0.AI;

if (args.Length == 1 && args[0] == "self-test") return Acceptance.Run();
if (args.Length < 2 || args[0] is not ("scan" or "report" or "metadata"))
{
    Console.Error.WriteLine("用法：P0.AI scan <新库路径> <会话目录或JSONL文件> | metadata <新库路径> <Codex状态库> | report <新库路径> | self-test");
    return 2;
}
try
{
    if (args[0] == "metadata" && args.Length == 3 &&
        string.Equals(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("源状态库与新库路径必须不同。");
    using var store = new UsageStore(args[1]);
    if (args[0] == "metadata")
    {
        if (args.Length != 3) throw new ArgumentException("metadata 需要显式指定 Codex 状态库。");
        var count = CodexSessionSnapshots.ReadInto(store, args[2]);
        Console.WriteLine(JsonSerializer.Serialize(new { session_snapshots = count, source_kind = "state_db_cumulative_snapshot" }));
        return 0;
    }
    if (args[0] == "scan")
    {
        if (args.Length != 3) throw new ArgumentException("scan 需要显式指定会话来源。");
        var files = File.Exists(args[2]) ? [args[2]] : Directory.EnumerateFiles(args[2], "*.jsonl", SearchOption.AllDirectories);
        var scanner = new CodexScanner(store);
        var clock = Stopwatch.StartNew();
        long bytes = 0, anchors = 0;
        int events = 0, count = 0;
        const long budget = 16 * 1024 * 1024;
        var budgetExhausted = false;
        foreach (var file in files)
        {
            if (bytes > budget - 4 * 1024 * 1024) { budgetExhausted = true; break; }
            ScanResult result;
            do
            {
                result = scanner.ScanFile(file);
                bytes += result.BytesRead;
                anchors += result.AnchorBytesRead;
                events += result.EventsAdded;
            } while (result.HasMore && bytes <= budget - 4 * 1024 * 1024);
            count++;
            if (result.HasMore || bytes >= budget) { budgetExhausted = true; break; }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { files = count, bytes_read = bytes, anchor_bytes_read = anchors, events_added = events, budget_exhausted = budgetExhausted, elapsed_ms = clock.Elapsed.TotalMilliseconds }));
        return 0;
    }
    Console.WriteLine(store.ReportJson(DateTimeOffset.UtcNow));
    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or ArgumentException)
{
    Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
    return 1;
}
