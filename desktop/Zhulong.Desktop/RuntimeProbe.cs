using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Zhulong.Desktop;

// 开发验收专用：独立新目录、无界面、无真实处置；不触碰旧版目录。
internal static class RuntimeProbe
{
    public static int Run(string output,int seconds)=>RunAsync(output,seconds).GetAwaiter().GetResult();
    static async Task<int> RunAsync(string output,int seconds)
    {
        var directory=Path.GetFullPath(output);Directory.CreateDirectory(directory);
        if(File.Exists(Path.Combine(directory,"settings.json")))throw new InvalidOperationException("探测须使用新的独立目录。");
        new SettingsStore(directory).Save(new RuntimeSettings{SampleSeconds=5});
        using var host=new DataHub(directory);using var process=Process.GetCurrentProcess();
        var points=new List<object>();var samples=new System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset>();
        host.Sampled+=value=>samples.Enqueue(value.Timestamp);
        host.Start();var timer=Stopwatch.StartNew();var beforeCpu=process.TotalProcessorTime.TotalSeconds;
        var injected=false;var recovered=false;var stopped=false;
        while(timer.Elapsed.TotalSeconds<Math.Clamp(seconds,30,600))
        {
            await Task.Delay(1000);process.Refresh();
            if((int)timer.Elapsed.TotalSeconds%5==0)
            {
                using var health=JsonDocument.Parse(JsonSerializer.Serialize(await host.QueryAsync("health")));
                points.Add(new {elapsedSeconds=timer.Elapsed.TotalSeconds,privateBytes=process.PrivateMemorySize64,workingSet=process.WorkingSet64,handles=process.HandleCount,cpuSeconds=process.TotalProcessorTime.TotalSeconds-beforeCpu,health=health.RootElement.Clone()});
            }
            if(!injected&&samples.Count>=2)
            {
                using var db=new SqliteConnection("Data Source="+Path.Combine(directory,"monitor.db"));db.Open();using var command=db.CreateCommand();command.CommandText="CREATE TRIGGER probe_failure BEFORE INSERT ON system_samples BEGIN SELECT RAISE(ABORT,'owned probe write failure'); END";command.ExecuteNonQuery();injected=true;
            }
            if(injected&&!recovered&&timer.Elapsed.TotalSeconds>17)
            {
                using var health=JsonDocument.Parse(JsonSerializer.Serialize(await host.QueryAsync("health")));
                if(health.RootElement.GetProperty("failures").TryGetProperty("采样",out _))
                {using var db=new SqliteConnection("Data Source="+Path.Combine(directory,"monitor.db"));db.Open();using var command=db.CreateCommand();command.CommandText="DROP TRIGGER probe_failure";command.ExecuteNonQuery();recovered=true;}
            }
        }
        var stop=Stopwatch.StartNew();await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));stopped=true;
        using var finalHealth=JsonDocument.Parse(JsonSerializer.Serialize(await host.QueryAsync("health")));
        long stored;using(var db=new SqliteConnection("Data Source="+Path.Combine(directory,"monitor.db"))){db.Open();using var command=db.CreateCommand();command.CommandText="SELECT COUNT(*) FROM system_samples";stored=Convert.ToInt64(command.ExecuteScalar());}
        var ok=stored>=5&&injected&&recovered&&samples.Count>=5&&finalHealth.RootElement.GetProperty("failures").EnumerateObject().All(x=>x.Name!="采样")&&stopped;
        var report=new {status=ok?"PASS":"FAIL",scope="independent no-UI host; no automatic actions; real source reads; owned SQLite fault injection",timestamp=DateTimeOffset.UtcNow,durationSeconds=timer.Elapsed.TotalSeconds,injected,recovered,stored,samples=samples.ToArray(),stopSeconds=stop.Elapsed.TotalSeconds,points};
        File.WriteAllText(Path.Combine(directory,"probe.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}),System.Text.Encoding.UTF8);
        return ok?0:1;
    }
}
