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
            var traySample = new SystemSnapshot(now, 75, 30, 40, 50, 16, 32, 0, []);
            using var tooltip = new System.Windows.Forms.NotifyIcon { Text = Program.FormatTrayTooltip(traySample) };
            Check(tooltip.Text.Contains("CPU 0%") &&
                tooltip.Text.Contains($"物理内存 50% ({16d:F1}/{32d:F1} GB)") &&
                tooltip.Text.Contains($"提交内存 75% ({30d:F1}/{40d:F1} GB)"), "托盘应同时显示CPU和两种内存读数，零占用须保留");
            Check(Program.FormatTrayTooltip(traySample with { CpuPercent = null }).Contains("CPU —\n"),
                "首轮CPU未知时应显示破折号，不应显示0%或附加百分号");
            var liveTray = Program.FormatTrayTooltip(traySample, new(now, 21, 60, 19.2, 32));
            Check(liveTray.Contains("CPU 21%") && liveTray.Contains("物理内存 60%") && liveTray.Contains("提交内存 75%"),
                "实时读数仅替换CPU与物理内存，提交内存沿用完整采样");
            using var appMark = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "zhulong-next.ico"), 16, 16);
            using var trayMark = Program.CreateIcon();
            using var traySmall = new System.Drawing.Icon(trayMark, 16, 16);
            using var expectedMark = new MemoryStream();
            using var actualMark = new MemoryStream();
            appMark.Save(expectedMark);
            traySmall.Save(actualMark);
            Check(traySmall.Size == new System.Drawing.Size(16, 16) &&
                expectedMark.ToArray().SequenceEqual(actualMark.ToArray()),
                "系统托盘应加载与程序图标一致的 C 朱印");
            ResourceDockWindow.CheckBehavior(Check);
            var database = Path.Combine(outputDirectory, "monitor-test-" + Guid.NewGuid().ToString("N") + ".db");
            using (var store = new MonitorStore(database))
            {
                SystemSnapshot Sample(int minutes, double commit, double? cpu, string? error = null) =>
                    new(now.AddMinutes(minutes), commit, commit * .4, 40, 50, 16, 32, cpu, [], error);
                store.Save(Sample(-3, 80, null));
                store.Save(Sample(-2, 90, 10, "合成采集错误"),[new(now.AddMinutes(-2),"合成告警","独立规则输出","warning"),new(now.AddMinutes(-2),"合成采集错误","故障注入","error")]);
                store.Save(Sample(-1, 91, 20, "合成采集错误"));
                using (var history = JsonDocument.Parse(JsonSerializer.Serialize(store.History("cpu", 1))))
                {
                    Check(history.RootElement.GetArrayLength() == 3, "三条样本应有三个历史点");
                    Check(history.RootElement[0].GetProperty("value").ValueKind == JsonValueKind.Null, "首个CPU未知值不能变成零");
                    Check(history.RootElement[1].GetProperty("value").GetDouble() == 10, "历史应来自真实落库值");
                }
                using (var events = JsonDocument.Parse(JsonSerializer.Serialize(store.Events())))
                {
                    Check(events.RootElement.GetArrayLength() == 2, "同轮传入的告警与错误应各记一次");
                    Check(events.RootElement.EnumerateArray().Count(row => row.GetProperty("level").GetString() == "warning") == 1, "存储不得自行额外生成高水位告警");
                    Check(events.RootElement.EnumerateArray().Count(row => row.GetProperty("level").GetString() == "error") == 1, "存储仅记录调用方发布的错误事件");
                }
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "CREATE TRIGGER fixture_abort BEFORE INSERT ON monitor_events BEGIN SELECT RAISE(ABORT,'fixture event failure'); END";
                    command.ExecuteNonQuery();
                    var rejected = false;
                    try { store.Save(Sample(0, 92, 25, "另一个合成错误"),[new(now,"另一个合成错误","故障注入","error")]); }
                    catch (SqliteException) { rejected = true; }
                    Check(rejected, "必须实际触发事件写入失败");
                    Check(store.History("commit", 1).Length == 3, "中途失败后样本必须回滚");
                    Check(store.Events().Length == 2, "中途失败后事件必须回滚");
                    command.CommandText = "DROP TRIGGER fixture_abort";
                    command.ExecuteNonQuery();
                    store.Save(Sample(0, 92, 25, "另一个合成错误"),[new(now,"另一个合成错误","故障注入","error")]);
                    Check(store.History("commit", 1).Length == 4 && store.Events().Length == 3, "失败后重试应完整保存");
                }
                for (var i = 0; i < 600; i++) store.Save(new(now.AddMinutes(-610 + i), 50, 20, 40, 50, 16, 32, null, [], null));
                using var bounded = JsonDocument.Parse(JsonSerializer.Serialize(store.History("physical", 24)));
                Check(bounded.RootElement.GetArrayLength() is > 0 and <= 288, "历史输出必须有界");
                Check(bounded.RootElement.EnumerateArray().All(row => row.GetProperty("value").GetDouble() == 50), "时间桶应保留人工核定的物理内存比例");
            }
            using (var frameStore = new MonitorStore(Path.Combine(outputDirectory, "frames-test-" + Guid.NewGuid().ToString("N") + ".db")))
            {
                const string metric = "desktop_frame_gap_ms";
                // 以整桶边界生成样本，确保峰值和随后正常值落进同一桶。
                var bucketStart = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 300 * 300).AddMinutes(-15);
                void SaveFrame(int seconds, double? milliseconds)
                {
                    var sampleAt = bucketStart.AddSeconds(seconds);
                    frameStore.Save(new SystemSnapshot(sampleAt.AddMilliseconds(500), 50, 20, 40, 50, 16, 32, 5, [])
                    {
                        Metrics = new Dictionary<string,MetricValue> { [metric] = new(metric, milliseconds, "ms", State: milliseconds is null ? "unavailable" : "ok", SampledAt: sampleAt) }
                    });
                }
                SaveFrame(15, 85);
                SaveFrame(42, 5);
                SaveFrame(310, null);
                using var history = JsonDocument.Parse(JsonSerializer.Serialize(frameStore.History(metric, 24)));
                Check(history.RootElement.GetArrayLength() == 2, "桌面帧间隔历史应按桶保留峰值和空值");
                var peak = history.RootElement[0];
                Check(peak.GetProperty("value").GetDouble() == 85 &&
                    peak.GetProperty("timestamp").GetDateTimeOffset() == bucketStart.AddSeconds(15), "帧间隔峰值必须保留原发生时间");
                Check(history.RootElement[1].GetProperty("value").ValueKind == JsonValueKind.Null, "缺少桌面更新应留空");
            }
            CheckOversizedLog(outputDirectory, Check);
            CheckSettingsAndImport(outputDirectory,Check);
            assertions += SettingsWindow.RunConversionTests();
            assertions += UpdateDownloadTests.Run();
            UpdateApply.CheckBehavior(Check, outputDirectory);
            assertions += WatchdogRecovery.RunSafetyTests(outputDirectory);
            assertions += StartupTasks.RunSafetyTestsAsync().GetAwaiter().GetResult();
            using(var portalHost=new DataHub(Path.Combine(outputDirectory,"portal-data-"+Guid.NewGuid().ToString("N"))))
                assertions += LocalPortal.RunSafetyTestsAsync(portalHost,Path.Combine(AppContext.BaseDirectory,"Web"),Path.Combine(outputDirectory,"portal")).GetAwaiter().GetResult();
            assertions += Zhulong.Runtime.Actions.RuntimeSafetyTests.RunAsync().GetAwaiter().GetResult();
            assertions += Zhulong.Runtime.Providers.AI.AiUsageSelfTests.Run(Path.Combine(outputDirectory,"ai-service"));
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "zhulong-next.ico");
            using (var icon = new System.Drawing.Icon(iconPath, 16, 16)) Check(icon.Width == 16 && icon.Height == 16, "运行包应包含可加载的16像素托盘图标");
            using (var icon = new System.Drawing.Icon(iconPath, 32, 32)) Check(icon.Width == 32 && icon.Height == 32, "运行包应包含可加载的32像素窗口图标");
            using var monitor = new SystemMonitor();
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
            using(var slowClock=new SystemMonitor(new RuntimeSettings{PingEnabled=false}))
            {
                slowClock.CollectSlowAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                var firstSlow=slowClock.Collect().Metrics!["ping_gw_ms"].SampledAt;
                slowClock.CollectSlowAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                var nextSlow=slowClock.Collect().Metrics!["ping_gw_ms"].SampledAt;
                Check(firstSlow.HasValue&&nextSlow>firstSlow,"慢源不得再次施加60秒节拍，连续调用应更新来源时间；禁用ping不发探测包");
            }
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
    static void CheckSettingsAndImport(string output,Action<bool,string> check)
    {
        var test=Path.Combine(output,"settings-import-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(test);
        var settings=new SettingsStore(test);settings.Save(new RuntimeSettings{SampleSeconds=15});
        check(settings.Load().SampleSeconds==15,"设置应原子保存并可回读");
        var rejected=false;try{settings.Save(new RuntimeSettings{SampleSeconds=0});}catch(ArgumentException){rejected=true;}
        check(rejected&&settings.Load().SampleSeconds==15,"非法配置不能覆盖上次有效配置");
        rejected=false;try{settings.Save(new RuntimeSettings{WatchProcesses=[null!]});}catch(ArgumentException){rejected=true;}
        check(rejected&&settings.Load().SampleSeconds==15,"空规则元素应明确拒绝且不覆盖原配置");
        rejected=false;try{settings.Save(new RuntimeSettings{AutoKill=["MsMpEng.exe"]});}catch(ArgumentException){rejected=true;}
        check(rejected,"安全软件不能进入处置白名单");
        var savedText=File.ReadAllText(settings.PathName);File.WriteAllText(settings.PathName,"{\"UnexpectedSetting\":true}");
        rejected=false;try{settings.Load();}catch(JsonException){rejected=true;}
        check(rejected,"未知配置字段不得静默忽略");File.WriteAllText(settings.PathName,savedText);
        using(var host=new DataHub(Path.Combine(test,"host")))
        {host.PauseFiveMinutes();check(host.Paused&&host.Muted,"暂停自动通道时仍保留监控并临时静音");host.Resume();check(!host.Paused&&!host.Muted,"恢复应解除自动暂停与临时静音");host.StopAsync().GetAwaiter().GetResult();rejected=false;try{host.ExecuteConfirmedAsync("none");}catch(InvalidOperationException){rejected=true;}check(rejected,"退出开始后不能再登记手动动作");}
        var manualDir=Path.Combine(test,"manual-service");new SettingsStore(manualDir).Save(new RuntimeSettings{RestartServices=["FixtureService"]});
        using(var host=new DataHub(manualDir)){using var response=JsonDocument.Parse(JsonSerializer.Serialize(host.QueryAsync("actions").GetAwaiter().GetResult()));var rows=response.RootElement;var row=rows.EnumerateArray().Single(x=>x.GetProperty("target").GetString()=="FixtureService");check(host.FindAction(row.GetProperty("id").GetString()!)?.Target=="FixtureService","显式服务白名单应生成可二次确认的手动入口，不执行真实服务");}
        var failureDir=Path.Combine(test,"settings-failure");new SettingsStore(failureDir).Save(new RuntimeSettings{ThrottleRules=[new("NoSuchZhulongFixture_08231.exe")]});
        using(var host=new DataHub(failureDir)){File.WriteAllText(Path.Combine(failureDir,"settings.json"),"{");rejected=false;try{host.ToggleThrottleAsync().GetAwaiter().GetResult();}catch(JsonException){rejected=true;}check(rejected&&!host.Settings.ThrottleEnabled,"限流设置写入前校验失败不得先改变开关或进程");rejected=false;try{host.ResetThrottleAsync().GetAwaiter().GetResult();}catch(JsonException){rejected=true;}check(rejected&&host.Settings.ThrottleRules[0].Name=="NoSuchZhulongFixture_08231.exe","恢复默认设置失败不得先替换活动规则");}
        var old=Path.Combine(test,"old.db");var stamp=DateTime.Now.AddMinutes(-2).ToString("s");
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=old,Pooling=false}.ToString()))
        {db.Open();using var command=db.CreateCommand();command.CommandText="CREATE TABLE metrics(ts TEXT,collector TEXT,key TEXT,value REAL,label TEXT);CREATE TABLE events(ts TEXT,rule TEXT,action TEXT,detail TEXT);INSERT INTO metrics VALUES($t,'memory','commit_percent',40,'原始标签');INSERT INTO events VALUES($t,'fixture','toast','原始事件')";command.Parameters.AddWithValue("$t",stamp);command.ExecuteNonQuery();}
        var oldBytes=File.ReadAllBytes(old);
        using(var archive=new MonitorStore(Path.Combine(test,"legacy.db")))
        {
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();var interrupted=false;
            try{archive.ImportLegacy(old,cancelled.Token);}catch(OperationCanceledException){interrupted=true;}
            check(interrupted,"历史导入取消必须真实触发");
            using(var db=new SqliteConnection("Data Source="+Path.Combine(test,"legacy.db")))
            {db.Open();using var command=db.CreateCommand();command.CommandText="SELECT (SELECT COUNT(*) FROM imports)+(SELECT COUNT(*) FROM legacy_metrics)+(SELECT COUNT(*) FROM legacy_events)";check(Convert.ToInt64(command.ExecuteScalar())==0,"取消后导入数据和完成标记必须一起回滚");}
            using var result=JsonDocument.Parse(JsonSerializer.Serialize(archive.ImportLegacy(old,CancellationToken.None)));
            check(result.RootElement.GetProperty("metrics").GetInt64()==1&&result.RootElement.GetProperty("events").GetInt64()==1,"应导入人工核定的一条指标与一条事件");
            using var again=JsonDocument.Parse(JsonSerializer.Serialize(archive.ImportLegacy(old,CancellationToken.None)));
            check(again.RootElement.GetProperty("status").GetString()=="already_imported","同一选中路径只能完成一次导入");
        }
        check(oldBytes.SequenceEqual(File.ReadAllBytes(old)),"导入不能改写旧数据库原文件");
        using(var monitor=new MonitorStore(Path.Combine(test,"monitor.db")))
        {
            using var history=JsonDocument.Parse(JsonSerializer.Serialize(monitor.History("commit",24)));
            check(history.RootElement.GetArrayLength()==1&&history.RootElement[0].GetProperty("value").GetDouble()==40,"新历史应读到单独归档的旧提交指标");
            check(monitor.Events().Length==1,"旧事件应在新事件视图中标注展示");
            monitor.Save(new(DateTimeOffset.UtcNow.AddDays(-40),50,20,40,50,16,32,10,[]),[new(DateTimeOffset.UtcNow.AddDays(-40),"过期测试","仅测试新库清理","info")]);
            monitor.Cleanup(30);
            check(monitor.Events().Length==1&&monitor.History("commit",720).Length==1,"清理应删除新过期数据并保留主动导入原始记录");
        }
    }

    static void CheckOversizedLog(string output, Action<bool, string> check)
    {
        var test = Path.GetFullPath(Path.Combine(output, "oversized-" + Guid.NewGuid().ToString("N")));
        var home = Path.Combine(test, "codex");
        var logs = Path.Combine(home, "sessions");
        Directory.CreateDirectory(logs);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var meta = JsonSerializer.Serialize(new { timestamp = now, type = "session_meta", payload = new { id = "oversized-fixture" } }) + "\n";
        var bad = Path.Combine(logs, "z.jsonl");
        File.WriteAllText(bad, meta + new string('x', 4 * 1024 * 1024 + 1024) + "\n");
        var normal = JsonSerializer.Serialize(new { timestamp = now, type = "event_msg", payload = new
        {
            type = "token_count", info = new { total_token_usage = new { input_tokens = 10, output_tokens = 2, total_tokens = 12 },
                last_token_usage = new { input_tokens = 10, output_tokens = 2, total_tokens = 12 } }
        } });
        File.WriteAllText(Path.Combine(logs, "a.jsonl"), meta + normal + "\n");
        var priorHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", home);
        using var hub = new DataHub(Path.Combine(test, "data"));
        try
        {
            var failed = hub.QueryAsync("refresh-usage").WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            using var failure = JsonDocument.Parse(JsonSerializer.Serialize(failed));
            check(!string.IsNullOrWhiteSpace(failure.RootElement.GetProperty("error").GetString()), "超限行必须发布可见错误而非抛到界面");
            check(!failure.RootElement.GetProperty("scanning").GetBoolean(), "失败后必须释放刷新锁");
            using (var connection = new SqliteConnection("Data Source=" + Path.Combine(test, "data", "ai.db")))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT offset FROM source_cursors WHERE path=$path";
                command.Parameters.AddWithValue("$path", bad);
                check(Convert.ToInt64(command.ExecuteScalar()) == System.Text.Encoding.UTF8.GetByteCount(meta), "游标不能越过超限行");
            }
            var retried = hub.QueryAsync("refresh-usage").WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            using var success = JsonDocument.Parse(JsonSerializer.Serialize(retried));
            check(success.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null, "后续正常来源应恢复读取");
            check(success.RootElement.GetProperty("report").GetProperty("totals").GetProperty("total_tokens").GetInt64() == 12, "异常来源不能阻塞下一份正常用量");
        }
        finally
        {
            hub.StopAsync().GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable("CODEX_HOME", priorHome);
        }
    }

}
