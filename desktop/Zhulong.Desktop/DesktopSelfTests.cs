using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Zhulong.Desktop;

// 直接调用正式采集与存储路径；合成历史仅写本次独立输出目录。
internal static class DesktopSelfTests
{
    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var assertions = 0;
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
        void Evidence(object value) => File.WriteAllText(Path.Combine(outputDirectory, "desktop-tests.json"),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var database = Path.Combine(outputDirectory, "monitor-test-" + Guid.NewGuid().ToString("N") + ".db");
            using (var store = new MonitorStore(database))
            {
                SystemSnapshot Sample(int minutes, double commit, double? cpu, string? error = null) =>
                    new(now.AddMinutes(minutes), commit, commit * .4, 40, 50, 16, 32, cpu, [], error);
                store.Save(Sample(-3, 80, null));
                store.Save(Sample(-2, 90, 10, "合成采集错误"));
                store.Save(Sample(-1, 91, 20, "合成采集错误"));
                using (var history = JsonDocument.Parse(JsonSerializer.Serialize(store.History("cpu", 1))))
                {
                    Check(history.RootElement.GetArrayLength() == 3, "三条样本应有三个历史点");
                    Check(history.RootElement[0].GetProperty("value").ValueKind == JsonValueKind.Null, "首个CPU未知值不能变成零");
                    Check(history.RootElement[1].GetProperty("value").GetDouble() == 10, "历史应来自真实落库值");
                }
                using (var events = JsonDocument.Parse(JsonSerializer.Serialize(store.Events())))
                {
                    Check(events.RootElement.GetArrayLength() == 2, "高水位跨越与错误应各记一次");
                    Check(events.RootElement.EnumerateArray().Count(row => row.GetProperty("level").GetString() == "warning") == 1, "持续高水位不应重复告警");
                    Check(events.RootElement.EnumerateArray().Count(row => row.GetProperty("level").GetString() == "error") == 1, "相同错误不应重复记事");
                }
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "CREATE TRIGGER fixture_abort BEFORE INSERT ON monitor_events BEGIN SELECT RAISE(ABORT,'fixture event failure'); END";
                    command.ExecuteNonQuery();
                    var rejected = false;
                    try { store.Save(Sample(0, 92, 25, "另一个合成错误")); }
                    catch (SqliteException) { rejected = true; }
                    Check(rejected, "必须实际触发事件写入失败");
                    Check(store.History("commit", 1).Length == 3, "中途失败后样本必须回滚");
                    Check(store.Events().Length == 2, "中途失败后事件必须回滚");
                    command.CommandText = "DROP TRIGGER fixture_abort";
                    command.ExecuteNonQuery();
                    store.Save(Sample(0, 92, 25, "另一个合成错误"));
                    Check(store.History("commit", 1).Length == 4 && store.Events().Length == 3, "失败后重试应完整保存");
                }
                for (var i = 0; i < 600; i++) store.Save(new(now.AddMinutes(-610 + i), 50, 20, 40, 50, 16, 32, null, [], null));
                using var bounded = JsonDocument.Parse(JsonSerializer.Serialize(store.History("physical", 24)));
                Check(bounded.RootElement.GetArrayLength() is > 0 and <= 288, "历史输出必须有界");
                Check(bounded.RootElement.EnumerateArray().All(row => row.GetProperty("value").GetDouble() == 50), "时间桶应保留人工核定的物理内存比例");
            }
            var monitor = new SystemMonitor();
            var first = monitor.Collect();
            Thread.Sleep(150);
            var second = monitor.Collect();
            Check(first.CpuPercent is null, "CPU首采应未知");
            Check(second.CpuPercent is >= 0 and <= 100, "CPU二次差分应在0至100之间");
            Check(second.CommitPercent is >= 0 and <= 100, "提交内存比例应有效");
            Check(second.CommitUsedGb is >= 0 && second.CommitLimitGb is > 0 && second.CommitUsedGb <= second.CommitLimitGb, "提交内存容量应有效");
            Check(second.MemoryPercent is >= 0 and <= 100, "物理内存比例应有效");
            Check(second.MemoryUsedGb is >= 0 && second.MemoryTotalGb is > 0 && second.MemoryUsedGb <= second.MemoryTotalGb, "物理内存容量应有效");
            Check(second.Processes.Count is > 0 and <= 5, "进程榜应最多五项");
            Check(second.Processes.All(row => row.MemoryGb >= 0 && double.IsFinite(row.MemoryGb)), "工作集应有效");
            Check(second.Processes.Zip(second.Processes.Skip(1), (left, right) => left.MemoryGb >= right.MemoryGb).All(value => value), "工作集榜应降序");
            Check(DesktopWindow.IsTrustedSource("https://desktop.zhulong.invalid/index.html"), "应允许固定入口");
            Check(DesktopWindow.IsTrustedSource("https://desktop.zhulong.invalid/index.html#ai"), "应允许固定入口片段");
            Check(!DesktopWindow.IsTrustedSource("http://desktop.zhulong.invalid/index.html"), "应拒绝HTTP来源");
            Check(!DesktopWindow.IsTrustedSource("https://desktop.zhulong.invalid/other.html"), "应拒绝其他路径");
            Check(!DesktopWindow.IsTrustedSource("https://desktop.zhulong.invalid.evil/index.html"), "应拒绝近似域名");
            Check(!DesktopWindow.IsTrustedSource("https://desktop.zhulong.invalid/index.html?other=1"), "应拒绝非固定入口查询串");
            Evidence(new { status = "PASS", assertions, timestamp = DateTimeOffset.UtcNow, database,
                native_first = first, native_second = second, scope = "synthetic storage and read-only native sampling; no UI or tray" });
            return 0;
        }
        catch (Exception error)
        {
            Evidence(new { status = "FAIL", assertions, timestamp = DateTimeOffset.UtcNow, error = error.ToString() });
            return 1;
        }
    }
}
