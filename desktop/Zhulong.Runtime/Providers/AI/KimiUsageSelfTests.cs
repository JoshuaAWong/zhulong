using System.Text;
using System.Text.Json;

namespace Zhulong.Runtime.Providers.AI;

internal static class KimiUsageSelfTests
{
    internal static int Run(string directory)
    {
        const int batchBytes = 4 * 1024 * 1024;
        var assertions = 0;
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        var fixture = Path.GetFullPath(Path.Combine(directory, "kimi-" + Guid.NewGuid().ToString("N")));
        var home = Path.Combine(fixture, "profiles", "default");
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string Record(long time, string agent, object usage) => JsonSerializer.Serialize(new
        {
            type = "usage.record", time, agentId = agent, model = "kimi-k3", usageScope = "turn", usage
        }) + "\n";
        string Wire(string relative) => Path.Combine(home, "sessions", "workspace", relative, "wire.jsonl");
        void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        var first = Record(stamp, "main", new { inputOther = 10, inputCacheRead = 50, inputCacheCreation = 20, output = 7 });
        var ignored = "{\"type\":\"message\",\"text\":\"usage.record\"}\n";
        var malformed = "{\"type\":\"usage.record\",\n";
        var path = Wire(Path.Combine("original", "agents", "main"));
        Write(path, first + first + ignored + malformed + Record(stamp + 1, "main", new { output = -1 }));
        Write(Wire(Path.Combine("copied", "agents", "main")), first);
        Write(Wire(Path.Combine("original", "agents", "helper")), Record(stamp + 2, "helper", new { output = 3 }));
        using var store = new UsageStore(Path.Combine(fixture, "usage.db"));
        using (var source = new KimiSessionSource(home))
        {
            var result = source.ScanBatch(store, CancellationToken.None);
            Check(result.EventsAdded == 2 && !result.HasMore, "Kimi 同一请求的重复行和跨会话副本只应全局计一次");
            Check(Scalar("SELECT COUNT(*) FROM usage_sessions") == 3, "Kimi 副本保留会话关联，子 Agent 归入原会话");
            Check(Scalar("SELECT COUNT(*) FROM usage_sessions WHERE session_id='original'") == 2, "子 Agent 用量不能计入其目录名");
            Check(Scalar("SELECT SUM(malformed_lines) FROM source_cursors") == 1, "损坏的用量 JSON 行要记录且不阻断扫描");
            using (var report = JsonDocument.Parse(store.ReportJson(DateTimeOffset.UtcNow, "kimi", "moonshot")))
            {
                var totals = report.RootElement.GetProperty("totals");
                Check(totals.GetProperty("input_tokens").GetInt64() == 80 &&
                    totals.GetProperty("cached_input_tokens").GetInt64() == 50 &&
                    totals.GetProperty("cache_write_tokens").GetInt64() == 20,
                    "Kimi 输入为普通、缓存命中与缓存写入之和");
                Check(totals.GetProperty("output_tokens").GetInt64() == 10 &&
                    totals.GetProperty("total_tokens").GetInt64() == 90,
                    "已包含的缓存不得重复加入 Kimi 用量总量");
            }
            var unchanged = source.ScanBatch(store, CancellationToken.None);
            Check(unchanged.BytesRead == 0 && unchanged.EventsAdded == 0, "不变的 Kimi 日志无需再次读内容");
            File.AppendAllText(path, Record(stamp + 3, "main", new { inputOther = 2, output = 1 }), Encoding.UTF8);
            var appended = source.ScanBatch(store, CancellationToken.None);
            Check(appended.BytesRead > 0 && appended.EventsAdded == 1 && Scalar("SELECT SUM(total_tokens) FROM usage_records") == 93,
                "只采集追加用量并保留已去重计数");
        }

        var longWire = Wire("oversized");
        Write(longWire, new string('x', batchBytes + 1024) + "\n" + Record(stamp + 4, "main", new { inputOther = 4, output = 1 }));
        using (var source = new KimiSessionSource(home))
        {
            var result = source.ScanBatch(store, CancellationToken.None);
            Check(result.BytesRead == batchBytes && result.HasMore && result.EventsAdded == 0,
                "Kimi 超限行必须保持批次预算并续读");
            Check(Scalar("SELECT offset FROM source_cursors WHERE path=$path", longWire) == batchBytes,
                "Kimi 超长行的已跳过区段须提交游标");
        }
        using (var reopened = new KimiSessionSource(home))
        {
            var result = reopened.ScanBatch(store, CancellationToken.None);
            Check(result.EventsAdded == 1 && result.BytesRead < batchBytes &&
                Scalar("SELECT SUM(total_tokens) FROM usage_records") == 98,
                "重新打开后应越过超长行余段并统计其后的正常用量");
            Check(Scalar("SELECT malformed_lines FROM source_cursors WHERE path=$path", longWire) == 1 &&
                Scalar("SELECT offset FROM source_cursors WHERE path=$path", longWire) == new FileInfo(longWire).Length,
                "同一超限行只标记一次，游标越过后续合法完整行");
        }
        return assertions;

        long Scalar(string sql, string? source = null)
        {
            using var command = store.Command(sql);
            if (source is not null) command.Parameters.AddWithValue("$path", source);
            return (long)command.ExecuteScalar()!;
        }
    }
}
