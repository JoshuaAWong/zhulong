using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Zhulong.Core;
using Zhulong.Runtime.Collectors;
namespace Zhulong.Runtime.Storage;

// 单个拥有者串行调用；新库独立，采样与同轮事件在同一事务提交。
public sealed class MonitorStore : IDisposable
{
    readonly SqliteConnection connection;
    readonly string legacyPath;
    readonly string databasePath;
    public MonitorStore(string path)
    {
        databasePath=Path.GetFullPath(path);
        legacyPath=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,"legacy.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 2 }.ToString());
        connection.Open();
        using var command = Command("""
            PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS system_samples(timestamp TEXT PRIMARY KEY,commit_percent REAL,physical_percent REAL,cpu_percent REAL,error TEXT,snapshot TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS monitor_events(id INTEGER PRIMARY KEY,timestamp TEXT NOT NULL,title TEXT NOT NULL,text TEXT NOT NULL,level TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_monitor_events_time ON monitor_events(timestamp);
            CREATE TABLE IF NOT EXISTS metric_samples(timestamp TEXT NOT NULL,name TEXT NOT NULL,value REAL,label TEXT,state TEXT,PRIMARY KEY(timestamp,name));
            CREATE INDEX IF NOT EXISTS idx_metric_name_time ON metric_samples(name,timestamp);
            CREATE TABLE IF NOT EXISTS legacy_metrics(source TEXT NOT NULL,row_id INTEGER NOT NULL,timestamp TEXT NOT NULL,original_timestamp TEXT NOT NULL,collector TEXT,name TEXT,value REAL,label TEXT,PRIMARY KEY(source,row_id));
            CREATE INDEX IF NOT EXISTS idx_legacy_name_time ON legacy_metrics(name,timestamp);
            CREATE INDEX IF NOT EXISTS idx_legacy_metric_time ON legacy_metrics(collector,name,timestamp);
            CREATE TABLE IF NOT EXISTS legacy_events(source TEXT NOT NULL,row_id INTEGER NOT NULL,timestamp TEXT NOT NULL,original_timestamp TEXT NOT NULL,rule TEXT,action TEXT,detail TEXT,PRIMARY KEY(source,row_id));
            CREATE TABLE IF NOT EXISTS self_resources(timestamp TEXT PRIMARY KEY,private_bytes INTEGER,working_set INTEGER,handles INTEGER,cpu_seconds REAL);
            CREATE INDEX IF NOT EXISTS idx_legacy_events_time ON legacy_events(timestamp);
            CREATE TABLE IF NOT EXISTS imports(source TEXT PRIMARY KEY,completed_at TEXT NOT NULL,metrics INTEGER,events INTEGER);
            """);
        command.ExecuteNonQuery();
    }
    public void Save(SystemSnapshot value, IReadOnlyList<RuntimeEvent>? events = null)
    {
        using var tx = connection.BeginTransaction();
        // 仅落榜单并集，不保存每轮整机全部进程，限制历史体积。
        var durable = value with { ProcessMetrics = value.ProcessMetrics?.Take(15).ToList() };
        using (var command = Command("INSERT INTO system_samples VALUES($t,$c,$p,$u,$e,$s)",tx,
            ("$t",Iso(value.Timestamp)),("$c",value.CommitPercent),("$p",value.MemoryPercent),("$u",value.CpuPercent),("$e",value.Error),("$s",JsonSerializer.Serialize(durable)))) command.ExecuteNonQuery();
        foreach (var metric in value.Metrics?.Values ?? Enumerable.Empty<MetricValue>())
        {
            using var command = Command("INSERT OR IGNORE INTO metric_samples VALUES($t,$n,$v,$l,$s)",tx,
                ("$t",Iso(metric.SampledAt??value.Timestamp)),("$n",metric.Name),("$v",metric.Value),("$l",metric.Label),("$s",metric.State));
            command.ExecuteNonQuery();
        }
        if(events is not null) foreach(var item in events) AddEvent(item,tx);
        tx.Commit();
    }
    public void AddEvent(RuntimeEvent value) => AddEvent(value,null);
    void AddEvent(RuntimeEvent value,SqliteTransaction? tx)
    {
        using var command = Command("INSERT INTO monitor_events(timestamp,title,text,level) VALUES($t,$a,$b,$c)",tx,
            ("$t",Iso(value.Timestamp)),("$a",value.Title),("$b",value.Text),("$c",value.Level));
        command.ExecuteNonQuery();
    }
    public object[] History(string metric,int hours)
    {
        var known = new Dictionary<string,(string Column,string LegacyCollector,string LegacyName)>
        { ["commit"]=("commit_percent","memory","commit_percent"),["physical"]=("physical_percent","memory","mem_percent"),["cpu"]=("cpu_percent","cpu","cpu_percent") };
        var since=Iso(DateTimeOffset.UtcNow.AddHours(-Math.Clamp(hours,1,8760)));
        var bucket=Math.Max(1,(int)Math.Ceiling(hours*3600d/288));
        if(metric==DesktopFrameMonitor.MetricName)return DesktopFrameHistory(since,bucket);
        string sql;
        if(known.TryGetValue(metric,out var map)) sql=$"SELECT timestamp,value FROM (SELECT timestamp,{map.Column} AS value FROM system_samples WHERE timestamp >= $since UNION ALL SELECT timestamp,value FROM legacy_metrics WHERE collector=$collector AND name=$name AND timestamp >= $since)";
        else sql="SELECT timestamp,value FROM metric_samples WHERE name=$name AND timestamp >= $since UNION ALL SELECT timestamp,value FROM legacy_metrics WHERE name=$name AND timestamp >= $since";
        var buckets=new Dictionary<long,(string Stamp,double Sum,long Count)>();
        void Read(SqliteConnection db,string query)
        {
            using var command=db.CreateCommand();command.CommandText=$"SELECT CAST(strftime('%s',timestamp) AS INTEGER)/$bucket,MAX(timestamp),SUM(value),COUNT(value) FROM ({query}) GROUP BY 1";
            command.Parameters.AddWithValue("$since",since);command.Parameters.AddWithValue("$bucket",bucket);command.Parameters.AddWithValue("$name",known.ContainsKey(metric)?map.LegacyName:metric);command.Parameters.AddWithValue("$collector",known.ContainsKey(metric)?map.LegacyCollector:"");
            using var reader=command.ExecuteReader();while(reader.Read())
            {
                var key=reader.GetInt64(0);var stamp=reader.GetString(1);var sum=reader.IsDBNull(2)?0:reader.GetDouble(2);var count=reader.GetInt64(3);
                if(buckets.TryGetValue(key,out var previous))buckets[key]=(string.CompareOrdinal(stamp,previous.Stamp)>0?stamp:previous.Stamp,sum+previous.Sum,count+previous.Count);
                else buckets[key]=(stamp,sum,count);
            }
        }
        using(var readerDb=OpenReadOnly(databasePath))Read(readerDb,sql);
        if(File.Exists(legacyPath)&&!string.Equals(databasePath,legacyPath,StringComparison.OrdinalIgnoreCase))
        {using var archive=OpenArchive();Read(archive,"SELECT timestamp,value FROM legacy_metrics WHERE ($collector='' OR collector=$collector) AND name=$name AND timestamp >= $since");}
        return buckets.OrderBy(x=>x.Key).TakeLast(288).Select(x=>(object)new {timestamp=x.Value.Stamp,value=x.Value.Count==0?(double?)null:x.Value.Sum/x.Value.Count}).ToArray();
    }
    object[] DesktopFrameHistory(string since,int bucket)
    {
        // 保留桶内峰值发生时刻，避免平均值掩盖短卡、把时间移到下一条样本。
        using var readerDb=OpenReadOnly(databasePath);
        using var command=readerDb.CreateCommand();
        command.CommandText="""
            WITH samples AS (
                SELECT timestamp,value,CAST(strftime('%s',timestamp) AS INTEGER)/$bucket AS bucket
                FROM metric_samples WHERE name=$name AND timestamp >= $since
            ), peaks AS (
                SELECT timestamp,value,bucket,
                    ROW_NUMBER() OVER (PARTITION BY bucket ORDER BY value DESC,timestamp DESC) AS position
                FROM samples
            )
            SELECT timestamp,value FROM peaks WHERE position=1 ORDER BY bucket DESC LIMIT 288
            """;
        command.Parameters.AddWithValue("$name",DesktopFrameMonitor.MetricName);
        command.Parameters.AddWithValue("$since",since);
        command.Parameters.AddWithValue("$bucket",bucket);
        var rows=new List<object>();
        using var reader=command.ExecuteReader();
        while(reader.Read())rows.Add(new {timestamp=reader.GetString(0),value=reader.IsDBNull(1)?(double?)null:reader.GetDouble(1)});
        rows.Reverse();
        return rows.ToArray();
    }
    SqliteConnection OpenArchive()=>OpenReadOnly(legacyPath);
    static SqliteConnection OpenReadOnly(string path)
    {var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=2}.ToString());db.Open();return db;}
    public object[] Events(int hours=720)
    {
        var rows=new List<(string Stamp,string Title,string Text,string Level)>();
        void Read(SqliteConnection db,bool legacyOnly)
        {
            using var command=db.CreateCommand();command.CommandText=legacyOnly?
                "SELECT timestamp,'旧版 · '||rule,action||' · '||detail,'info' FROM legacy_events WHERE timestamp >= $since ORDER BY timestamp DESC LIMIT 100":
                "SELECT timestamp,title,text,level FROM monitor_events WHERE timestamp >= $since ORDER BY timestamp DESC LIMIT 100";
            command.Parameters.AddWithValue("$since",Iso(DateTimeOffset.UtcNow.AddHours(-hours)));using var reader=command.ExecuteReader();
            while(reader.Read())rows.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3)));
        }
        using(var readerDb=OpenReadOnly(databasePath))Read(readerDb,false);
        if(File.Exists(legacyPath)){using var archive=OpenArchive();Read(archive,true);}
        return rows.OrderByDescending(x=>x.Stamp,StringComparer.Ordinal).Take(100).Select(x=>(object)new {timestamp=x.Stamp,title=x.Title,text=x.Text,level=x.Level}).ToArray();
    }
    public void SaveSelf(DateTimeOffset timestamp,long privateBytes,long workingSet,int handles,double cpuSeconds)
    {using var command=Command("INSERT INTO self_resources VALUES($t,$p,$w,$h,$c)",null,("$t",Iso(timestamp)),("$p",privateBytes),("$w",workingSet),("$h",handles),("$c",cpuSeconds));command.ExecuteNonQuery();}
    public void Cleanup(int days)
    {
        using var tx=connection.BeginTransaction();
        foreach(var table in new[]{"system_samples","metric_samples","monitor_events","self_resources"})
        { using var command=Command($"DELETE FROM {table} WHERE timestamp < $cutoff",tx,("$cutoff",Iso(DateTimeOffset.UtcNow.AddDays(-days))));command.ExecuteNonQuery(); }
        tx.Commit();
        using var checkpoint=Command("PRAGMA wal_checkpoint(PASSIVE)");checkpoint.ExecuteNonQuery();
        // 用户主动导入的原始旧记录不参加自动清理。
    }
    public object ImportLegacy(string sourcePath,CancellationToken token)
    {
        var full=Path.GetFullPath(sourcePath);
        if(string.Equals(full,connection.DataSource,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("不能导入当前新库。");
        var source=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full.ToUpperInvariant())));
        using(var exists=Command("SELECT completed_at FROM imports WHERE source=$s",null,("$s",source)))
            if(exists.ExecuteScalar() is string completed) return new {status="already_imported",completed_at=completed};
        using var old=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=full,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=2}.ToString());old.Open();
        using var read=old.BeginTransaction(deferred:true);using var tx=connection.BeginTransaction();
        long metrics=0,events=0;
        foreach(var kind in new[]{"metrics","events"})
        {
            using var query=old.CreateCommand();query.Transaction=read;
            query.CommandText=kind=="metrics"?"SELECT rowid,ts,collector,key,value,label FROM metrics ORDER BY rowid":"SELECT rowid,ts,rule,action,detail FROM events ORDER BY rowid";
            using var reader=query.ExecuteReader();
            while(reader.Read())
            {
                token.ThrowIfCancellationRequested();var original=reader.GetString(1);
                // 旧版记录无时区，按本机本地时间解释，同时完整保留原字符串。
                var stamp=DateTimeOffset.Parse(original,CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal);
                if(kind=="metrics")
                {
                    using var insert=Command("INSERT INTO legacy_metrics VALUES($s,$id,$t,$o,$c,$n,$v,$l)",tx,("$s",source),("$id",reader.GetInt64(0)),("$t",Iso(stamp)),("$o",original),("$c",reader.GetString(2)),("$n",reader.GetString(3)),("$v",reader.IsDBNull(4)?null:reader.GetDouble(4)),("$l",reader.IsDBNull(5)?"":reader.GetString(5)));insert.ExecuteNonQuery();metrics++;
                }
                else
                {
                    using var insert=Command("INSERT INTO legacy_events VALUES($s,$id,$t,$o,$r,$a,$d)",tx,("$s",source),("$id",reader.GetInt64(0)),("$t",Iso(stamp)),("$o",original),("$r",reader.GetString(2)),("$a",reader.GetString(3)),("$d",reader.IsDBNull(4)?"":reader.GetString(4)));insert.ExecuteNonQuery();events++;
                }
            }
        }
        using(var done=Command("INSERT INTO imports VALUES($s,$t,$m,$e)",tx,("$s",source),("$t",Iso(DateTimeOffset.UtcNow)),("$m",metrics),("$e",events)))done.ExecuteNonQuery();
        tx.Commit();return new {status="imported",metrics,events};
    }
    SqliteCommand Command(string sql,SqliteTransaction? tx=null,params (string Name,object? Value)[] parameters)
    {var command=connection.CreateCommand();command.Transaction=tx;command.CommandText=sql;foreach(var (name,value) in parameters)command.Parameters.AddWithValue(name,value??DBNull.Value);return command;}
    static string Iso(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    public void Dispose()=>connection.Dispose();
}
