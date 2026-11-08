using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Zhulong.Runtime.Providers.AI;

public sealed partial class UsageStore : IDisposable
{
    internal SqliteConnection Connection { get; }
    internal string DatabasePath => Connection.DataSource;
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public UsageStore(string dbPath)
    {
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Pooling = false, DefaultTimeout = 2
        }.ToString());
        Connection.Open();
        using var command = Connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS usage_records (
                event_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, parent_session_id TEXT,
                model TEXT NOT NULL, timestamp TEXT NOT NULL, kind TEXT NOT NULL,
                input_tokens INTEGER NOT NULL, cached_input_tokens INTEGER NOT NULL,
                cache_write_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
                reasoning_tokens INTEGER NOT NULL, total_tokens INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS usage_session ON usage_records(session_id, timestamp);
            CREATE TABLE IF NOT EXISTS usage_sessions (event_id TEXT NOT NULL,session_id TEXT NOT NULL,
                parent_session_id TEXT,PRIMARY KEY(event_id,session_id));
            CREATE TABLE IF NOT EXISTS source_cursors (
                path TEXT PRIMARY KEY, offset INTEGER NOT NULL, observed_length INTEGER NOT NULL,
                write_ticks INTEGER NOT NULL, creation_ticks INTEGER NOT NULL, anchor TEXT NOT NULL,
                parser_state TEXT NOT NULL, scanned_at TEXT NOT NULL, malformed_lines INTEGER NOT NULL, pending_batch INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS session_snapshots (session_id TEXT PRIMARY KEY,total_tokens INTEGER NOT NULL,
                source_updated_at INTEGER NOT NULL,observed_at TEXT NOT NULL,source TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS quota_snapshots (
                limit_id TEXT NOT NULL, source_session TEXT NOT NULL, observed_at TEXT NOT NULL, snapshot TEXT NOT NULL,
                PRIMARY KEY(limit_id,source_session));
            """;
        command.ExecuteNonQuery();
        InitializeMetadataSync();
    }

    internal SqliteCommand Command(string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    internal Cursor? ReadCursor(string path, SqliteTransaction transaction)
    {
        using var command = Command("SELECT offset,observed_length,write_ticks,creation_ticks,anchor,parser_state,malformed_lines,pending_batch FROM source_cursors WHERE path=$path", transaction, ("$path", path));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new Cursor(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetString(4),
            JsonSerializer.Deserialize<ParserState>(reader.GetString(5), Json)!, reader.GetInt64(6), reader.GetBoolean(7));
    }

    internal int Insert(UsageEvent value, SqliteTransaction transaction)
    {
        using var command = Command("""
            INSERT OR IGNORE INTO usage_records VALUES
            ($id,$session,$parent,$model,$time,$kind,$input,$cached,$write,$output,$reasoning,$total)
            """, transaction, ("$id", value.EventId), ("$session", value.SessionId),
            ("$parent", value.ParentSessionId), ("$model", value.Model),
            ("$time", Iso(value.Timestamp)), ("$kind", value.Kind),
            ("$input", value.Usage.Input), ("$cached", value.Usage.CachedInput),
            ("$write", value.Usage.CacheWrite), ("$output", value.Usage.Output),
            ("$reasoning", value.Usage.Reasoning), ("$total", value.Usage.Total));
        var added = command.ExecuteNonQuery();
        using var link = Command("INSERT OR IGNORE INTO usage_sessions VALUES ($id,$session,$parent)", transaction,
            ("$id", value.EventId), ("$session", value.SessionId), ("$parent", value.ParentSessionId));
        link.ExecuteNonQuery();
        return added;
    }

    internal void SaveQuota(QuotaSnapshot value, string sessionId, SqliteTransaction transaction)
    {
        using var command = Command("""
            INSERT INTO quota_snapshots VALUES ($id,$session,$time,$json)
            ON CONFLICT(limit_id,source_session) DO UPDATE SET observed_at=excluded.observed_at,snapshot=excluded.snapshot
            WHERE excluded.observed_at >= quota_snapshots.observed_at
            """, transaction, ("$id", value.LimitId), ("$session", sessionId), ("$time", Iso(value.ObservedAt)),
            ("$json", JsonSerializer.Serialize(value, Json)));
        command.ExecuteNonQuery();
    }

    internal void SaveCursor(string path, Cursor cursor, SqliteTransaction transaction)
    {
        using var command = Command("""
            INSERT INTO source_cursors VALUES ($path,$offset,$length,$write,$created,$anchor,$state,$time,$bad,$pending)
            ON CONFLICT(path) DO UPDATE SET offset=excluded.offset,observed_length=excluded.observed_length,
            write_ticks=excluded.write_ticks,creation_ticks=excluded.creation_ticks,anchor=excluded.anchor,
            parser_state=excluded.parser_state,scanned_at=excluded.scanned_at,malformed_lines=excluded.malformed_lines,pending_batch=excluded.pending_batch
            """, transaction, ("$path", path), ("$offset", cursor.Offset),
            ("$length", cursor.ObservedLength), ("$write", cursor.WriteTicks),
            ("$created", cursor.CreationTicks), ("$anchor", cursor.Anchor),
            ("$state", JsonSerializer.Serialize(cursor.State, Json)),
            ("$time", Iso(DateTimeOffset.UtcNow)), ("$bad", cursor.MalformedLines), ("$pending", cursor.PendingBatch));
        command.ExecuteNonQuery();
    }

    internal void SaveSessionSnapshot(string id, long total, long updated, string source, SqliteTransaction transaction)
    {
        using var command = Command("""
            INSERT INTO session_snapshots VALUES ($id,$total,$updated,$observed,$source)
            ON CONFLICT(session_id) DO UPDATE SET total_tokens=excluded.total_tokens,
                source_updated_at=excluded.source_updated_at,observed_at=excluded.observed_at,source=excluded.source
            WHERE excluded.source_updated_at >= session_snapshots.source_updated_at
            """, transaction, ("$id", id), ("$total", total), ("$updated", updated),
            ("$observed", Iso(DateTimeOffset.UtcNow)), ("$source", source));
        command.ExecuteNonQuery();
    }

    public string ReportJson(DateTimeOffset now)
    {
        // 明细只在显式查询时聚合，倒计时不触发来源扫描。
        var sessions = new List<object>();
        using (var command = Command("""
            SELECT s.session_id,MAX(s.parent_session_id),u.model,MIN(u.timestamp),MAX(u.timestamp),COUNT(*),
                SUM(input_tokens),SUM(cached_input_tokens),SUM(cache_write_tokens),
                SUM(output_tokens),SUM(reasoning_tokens),SUM(total_tokens)
            FROM usage_sessions s JOIN usage_records u ON s.event_id=u.event_id
            GROUP BY s.session_id,u.model ORDER BY MAX(u.timestamp) DESC,s.session_id,u.model LIMIT 100
            """))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) sessions.Add(new
            {
                provider = "openai", tool = "codex", session_id = reader.GetString(0), parent_session_id = reader.IsDBNull(1) ? null : reader.GetString(1),
                model = reader.GetString(2), first_usage_at = reader.GetString(3), last_usage_at = reader.GetString(4),
                event_count = reader.GetInt64(5), input_tokens = reader.GetInt64(6),
                cached_input_tokens = reader.GetInt64(7), cache_write_tokens = reader.GetInt64(8),
                output_tokens = reader.GetInt64(9), reasoning_tokens = reader.GetInt64(10), total_tokens = reader.GetInt64(11)
            });
        object totals;
        using (var command = Command("""
            SELECT COUNT(*),COALESCE(SUM(input_tokens),0),COALESCE(SUM(cached_input_tokens),0),
                COALESCE(SUM(cache_write_tokens),0),COALESCE(SUM(output_tokens),0),
                COALESCE(SUM(reasoning_tokens),0),COALESCE(SUM(total_tokens),0) FROM usage_records
            """))
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            totals = new { event_count = reader.GetInt64(0), input_tokens = reader.GetInt64(1),
                cached_input_tokens = reader.GetInt64(2), cache_write_tokens = reader.GetInt64(3),
                output_tokens = reader.GetInt64(4), reasoning_tokens = reader.GetInt64(5), total_tokens = reader.GetInt64(6) };
        }
        var metadata = new List<object>();
        using (var command = Command("SELECT session_id,total_tokens,source_updated_at,observed_at,source FROM session_snapshots ORDER BY source_updated_at DESC,session_id LIMIT 100"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) metadata.Add(new { provider = "openai", tool = "codex", session_id = reader.GetString(0), total_tokens = reader.GetInt64(1),
                source_updated_at = reader.GetInt64(2), observed_at = reader.GetString(3), source = reader.GetString(4),
                source_kind = "state_db_cumulative_snapshot", token_breakdown_available = false });
        using var countMetadata = Command("SELECT COUNT(*) FROM session_snapshots");
        var metadataCount = Convert.ToInt64(countMetadata.ExecuteScalar());
        var quota = new List<object>();
        using (var command = Command("SELECT source_session,snapshot FROM quota_snapshots ORDER BY observed_at DESC,limit_id LIMIT 100"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var value = JsonSerializer.Deserialize<QuotaSnapshot>(reader.GetString(1), Json)!;
                quota.Add(new { source_session_id = reader.GetString(0), limit_id = value.LimitId, plan_type = value.PlanType,
                    observed_at = Iso(value.ObservedAt), snapshot_age_seconds = Math.Max(0, (now - value.ObservedAt).TotalSeconds),
                    source = "local_rollout_account_unknown", primary = Window(value.Primary, now), secondary = Window(value.Secondary, now) });
            }
        var sources = new List<object>();
        using (var command = Command("SELECT path,offset,observed_length,scanned_at,malformed_lines,parser_state,pending_batch FROM source_cursors ORDER BY scanned_at DESC LIMIT 100"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var state = JsonSerializer.Deserialize<ParserState>(reader.GetString(5), Json)!;
                sources.Add(new { path = reader.GetString(0), offset = reader.GetInt64(1),
                    observed_length = reader.GetInt64(2), scanned_at = reader.GetString(3), malformed_lines = reader.GetInt64(4),
                    pending_batch = reader.GetBoolean(6), usage_status = state.SawTokenCount ? "counted" : state.RecordOnly ? "unsupported_record_only" : "not_observed",
                    parser_state = state });
            }
        object quality;
        using (var command = Command("""
            SELECT COUNT(*),COALESCE(SUM(CASE WHEN json_extract(parser_state,'$.SawTokenCount') = 0 THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN json_extract(parser_state,'$.RecordOnly') = 1 THEN 1 ELSE 0 END),0),
                COALESCE(SUM(malformed_lines),0),COALESCE(SUM(pending_batch),0) FROM source_cursors
            """))
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            quality = new { sources_without_token_usage = reader.GetInt64(1), record_only_sources = reader.GetInt64(2),
                malformed_lines = reader.GetInt64(3), pending_sources = reader.GetInt64(4) };
        }
        using var countSources = Command("SELECT COUNT(*) FROM source_cursors");
        var sourceCount = Convert.ToInt64(countSources.ExecuteScalar());
        return JsonSerializer.Serialize(new { generated_at = Iso(now), provider = "openai", tool = "codex", coverage = "local_rollout_only",
            totals, sessions, metadata_sessions = metadata, metadata_session_count = metadataCount, metadata_rows_limit = 100, quota, quality, session_totals_include_replayed_history = true, source_count = sourceCount, session_rows_limit = 100, source_rows_limit = 100, quota_rows_limit = 100, sources }, new JsonSerializerOptions { WriteIndented = true });
    }

    static object? Window(QuotaWindow? window, DateTimeOffset now)
    {
        if (window is null) return null;
        var remaining = window.ResetsAt is null ? (double?)null : Math.Max(0, Math.Ceiling((window.ResetsAt.Value - now).TotalSeconds));
        return new { name = window.Name, used_percent = window.UsedPercent,
            remaining_percent = window.UsedPercent is null ? (double?)null : Math.Max(0, 100 - window.UsedPercent.Value),
            window_minutes = window.WindowMinutes, resets_at = window.ResetsAt is null ? null : Iso(window.ResetsAt.Value),
            remaining_seconds = remaining, status = remaining is null ? "unknown_reset" : remaining == 0 ? "awaiting_refresh" : "snapshot" };
    }

    internal static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public void Dispose() => Connection.Dispose();
}

internal sealed record Cursor(long Offset, long ObservedLength, long WriteTicks, long CreationTicks,
    string Anchor, ParserState State, long MalformedLines, bool PendingBatch);
