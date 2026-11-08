using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Zhulong.Desktop;

// 调用方用同一把锁串行访问，历史与事件仅写入新桌面版自己的数据库。
public sealed class MonitorStore : IDisposable
{
    private readonly SqliteConnection connection;

    public MonitorStore(string path)
    {
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = absolute, Pooling = false, DefaultTimeout = 2
        }.ToString());
        connection.Open();
        using var command = Command("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS system_samples (
                timestamp TEXT PRIMARY KEY,commit_percent REAL,physical_percent REAL,cpu_percent REAL,
                error TEXT,snapshot TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS monitor_events (
                id INTEGER PRIMARY KEY,timestamp TEXT NOT NULL,title TEXT NOT NULL,text TEXT NOT NULL,level TEXT NOT NULL);
            """);
        command.ExecuteNonQuery();
    }

    public void Save(SystemSnapshot snapshot)
    {
        using var transaction = connection.BeginTransaction();
        double? previousCommit = null;
        string? previousError = null;
        using (var command = Command("SELECT commit_percent,error FROM system_samples ORDER BY timestamp DESC LIMIT 1", transaction))
        using (var reader = command.ExecuteReader())
            if (reader.Read())
            {
                previousCommit = reader.IsDBNull(0) ? null : reader.GetDouble(0);
                previousError = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        using (var command = Command("""
            INSERT INTO system_samples VALUES ($time,$commit,$physical,$cpu,$error,$snapshot)
            """, transaction, ("$time", Iso(snapshot.Timestamp)), ("$commit", snapshot.CommitPercent),
            ("$physical", snapshot.MemoryPercent), ("$cpu", snapshot.CpuPercent),
            ("$error", snapshot.Error), ("$snapshot", JsonSerializer.Serialize(snapshot))))
            command.ExecuteNonQuery();
        if (snapshot.CommitPercent is >= 85 && !(previousCommit is >= 85))
            InsertEvent(snapshot.Timestamp, "提交内存水位偏高",
                $"提交内存使用率 {snapshot.CommitPercent.Value.ToString("F1", CultureInfo.InvariantCulture)}%，请查看占用较高的进程。",
                "warning", transaction);
        if (!string.IsNullOrEmpty(snapshot.Error) && snapshot.Error != previousError)
            InsertEvent(snapshot.Timestamp, "部分指标采集失败", snapshot.Error, "error", transaction);
        transaction.Commit();
    }

    public object[] History(string metric, int hours)
    {
        var column = metric switch
        {
            "commit" => "commit_percent",
            "physical" => "physical_percent",
            "cpu" => "cpu_percent",
            _ => throw new ArgumentException("不支持的历史指标。", nameof(metric))
        };
        hours = Math.Clamp(hours, 1, 744);
        var bucketSeconds = Math.Max(1, (int)Math.Ceiling(hours * 3600d / 288));
        // 列名仅来自上述固定映射；外部值始终是SQL参数。
        using var command = Command($"""
            SELECT MAX(timestamp),AVG({column}) FROM system_samples
            WHERE timestamp >= $since
            GROUP BY CAST(strftime('%s',timestamp) AS INTEGER) / $bucket
            ORDER BY MAX(timestamp) DESC LIMIT 288
            """, null, ("$since", Iso(DateTimeOffset.UtcNow.AddHours(-hours))), ("$bucket", bucketSeconds));
        var rows = new List<object>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(new
        {
            timestamp = reader.GetString(0), value = reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1)
        });
        rows.Reverse();
        return rows.ToArray();
    }

    public object[] Events()
    {
        using var command = Command("SELECT timestamp,title,text,level FROM monitor_events ORDER BY id DESC LIMIT 50");
        using var reader = command.ExecuteReader();
        var rows = new List<object>();
        while (reader.Read()) rows.Add(new
        {
            timestamp = reader.GetString(0), title = reader.GetString(1),
            text = reader.GetString(2), level = reader.GetString(3)
        });
        return rows.ToArray();
    }

    private void InsertEvent(DateTimeOffset timestamp, string title, string text, string level, SqliteTransaction transaction)
    {
        using var command = Command("INSERT INTO monitor_events(timestamp,title,text,level) VALUES ($time,$title,$text,$level)",
            transaction, ("$time", Iso(timestamp)), ("$title", title), ("$text", text), ("$level", level));
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string Iso(DateTimeOffset timestamp) => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public void Dispose() => connection.Dispose();
}
