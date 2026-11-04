using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace Zhulong.P0;

// 可丢弃的界面实验，不引用生产程序及数据库。
record SampleRow(string Name, double Value);
record Snapshot(int Sequence, double[] Points, SampleRow[] Rows)
{
    public static Snapshot Create(int sequence) => new(sequence,
        Enumerable.Range(0, 300).Select(i => 50 + 30 * Math.Sin((i + sequence) / 18d)).ToArray(),
        Enumerable.Range(1, 50).Select(i => new SampleRow($"测试项目 {i:00}", (i * 7 + sequence) % 100)).ToArray());
}

interface IPanel
{
    Task OpenAsync();
    Task RenderAsync(Snapshot snapshot);
    Task CloseAsync();
    Task CaptureAsync(string path);
}

static class Program
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);
    public static void Emit(object value) { Console.WriteLine(Json(value)); Console.Out.Flush(); }

    [STAThread]
    static int Main(string[] args)
    {
        var options = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1]);
        string mode = options.GetValueOrDefault("--mode", "wpf");
        string assets = options.GetValueOrDefault("--assets", Path.GetFullPath("web-dist"));
        string profile = options.GetValueOrDefault("--profile", Path.GetFullPath("profile"));
        int cycles = int.Parse(options.GetValueOrDefault("--cycles", "0"));
        int seconds = int.Parse(options.GetValueOrDefault("--seconds", "20"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exit = 0;
        app.Startup += async (_, _) =>
        {
            IPanel? panel = null;
            try
            {
                Emit(new { kind = "environment", mode, runtime = Environment.Version.ToString(),
                    os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
                    points = 300, rows = 50, cycles, seconds });
                async Task Phase(string name, int delay)
                {
                    Emit(new { kind = "phase", name, elapsedMs = Clock.Elapsed.TotalMilliseconds });
                    await Task.Delay(TimeSpan.FromSeconds(delay));
                }
                IPanel Create() => mode == "wpf" ? new NativePanel() : new WebPanel(assets, profile);
                async Task Open()
                {
                    var watch = Stopwatch.StartNew();
                    panel = Create();
                    await panel.OpenAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    await panel.RenderAsync(Snapshot.Create(0)).WaitAsync(TimeSpan.FromSeconds(10));
                    Emit(new { kind = "open", ms = watch.Elapsed.TotalMilliseconds });
                }
                async Task Close()
                {
                    var watch = Stopwatch.StartNew();
                    await panel!.CloseAsync();
                    panel = null;
                    Emit(new { kind = "close", ms = watch.Elapsed.TotalMilliseconds });
                }
                await Phase("before", seconds);
                await Phase("opening", 0);
                await Open();
                if (options.TryGetValue("--capture", out string? capture)) await panel!.CaptureAsync(capture);
                await Phase("open", 0);
                for (int i = 1; i <= seconds; i++)
                {
                    var watch = Stopwatch.StartNew();
                    await panel!.RenderAsync(Snapshot.Create(i)).WaitAsync(TimeSpan.FromSeconds(10));
                    Emit(new { kind = "refresh", ms = watch.Elapsed.TotalMilliseconds });
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                await Phase("closing", 0);
                await Close();
                await Phase("closed", seconds);
                await Phase("cycles", 0);
                for (int i = 0; i < cycles; i++)
                {
                    await Open();
                    await Close();
                    Emit(new { kind = "cycle", number = i + 1 });
                }
                await Phase("after", seconds);
                Emit(new { kind = "done", cycles });
            }
            catch (Exception error)
            {
                exit = 1;
                Emit(new { kind = "failure", error = error.ToString() });
            }
            finally
            {
                if (panel is not null)
                {
                    try { await panel.CloseAsync(); }
                    catch (Exception error) { exit = 1; Emit(new { kind = "cleanupFailure", error = error.Message }); }
                }
                app.Shutdown(exit);
            }
        };
        app.Run();
        return exit;
    }
}
