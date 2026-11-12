using Microsoft.Data.Sqlite;

namespace Zhulong.Runtime.Providers.AI;

// 本地账本只读取 ZCode 的结构化用量列，SQLite 的 WAL 快照纳入尚未 checkpoint 的记录。
internal sealed class ZCodeScanner(string sourcePath)
{
    readonly string path = Path.GetFullPath(sourcePath);
    const int PendingChecks = 64;
    const string Projection = """
        SELECT m.rowid,m.id,m.session_id,s.parent_id,m.model_id,m.status,m.completed_at,
               m.input_tokens,m.output_tokens,m.cache_read_input_tokens,m.cache_creation_input_tokens,m.reasoning_tokens
        FROM model_usage m LEFT JOIN session s ON s.id=m.session_id
        """;

    internal ScanResult ScanBatch(UsageStore store, int maxRows, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return new(0, 0, 0, false);
        if (string.Equals(path, Path.GetFullPath(store.DatabasePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("ZCode 来源库与统计库路径必须不同。");
        var limit = Math.Clamp(maxRows, 1, 4096);
        using (var initialize = store.Command("""
            CREATE TABLE IF NOT EXISTS zcode_cursor (
                path TEXT PRIMARY KEY,tail INTEGER NOT NULL,tail_id TEXT,backfill INTEGER);
            CREATE TABLE IF NOT EXISTS zcode_pending (
                path TEXT NOT NULL,source_row INTEGER NOT NULL,source_id TEXT NOT NULL,checked_at INTEGER NOT NULL,
                PRIMARY KEY(path,source_row));
            CREATE INDEX IF NOT EXISTS zcode_pending_checks ON zcode_pending(path,checked_at,source_row);
            """)) initialize.ExecuteNonQuery();

        var cursor = ReadCursor(store);
        var (next, rows, checkedRows, reset) = ReadSource(store, cursor, limit, token);
        if (rows.Count == 0 && checkedRows.Count == 0 && !reset && cursor == next)
            return new(0, 0, 0, next.Backfill.HasValue);

        using var transaction = store.Connection.BeginTransaction();
        if (reset)
        {
            using var clear = store.Command("DELETE FROM zcode_pending WHERE path=$path", transaction, ("$path", path));
            clear.ExecuteNonQuery();
        }
        var added = 0;
        var checkedAt = DateTimeOffset.UtcNow.UtcTicks;
        foreach (var (pending, row) in checkedRows)
        {
            token.ThrowIfCancellationRequested();
            if (row is { IsPending: true } && row.Id == pending.Id) WritePending(store, row, checkedAt, transaction);
            else
            {
                using var remove = store.Command("DELETE FROM zcode_pending WHERE path=$path AND source_row=$row", transaction,
                    ("$path", path), ("$row", pending.RowId));
                remove.ExecuteNonQuery();
                if (row?.Id == pending.Id && row.ToEvent() is { } value) added += store.Insert(value, transaction);
            }
        }
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.IsPending) WritePending(store, row, checkedAt, transaction);
            else if (row.ToEvent() is { } value) added += store.Insert(value, transaction);
        }
        using var write = store.Command("""
            INSERT INTO zcode_cursor(path,tail,tail_id,backfill) VALUES($path,$tail,$id,$backfill)
            ON CONFLICT(path) DO UPDATE SET tail=excluded.tail,tail_id=excluded.tail_id,backfill=excluded.backfill
            """, transaction, ("$path", path), ("$tail", next.Tail), ("$id", next.Id), ("$backfill", next.Backfill));
        write.ExecuteNonQuery();
        transaction.Commit();
        return new(0, rows.Count + checkedRows.Count, added, rows.Count >= limit || next.Backfill.HasValue);
    }

    (Position Next, List<Row> Rows, List<(Pending Pending, Row? Row)> Checked, bool Reset) ReadSource(
        UsageStore store, Position? cursor, int limit, CancellationToken token)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2
        }.ToString());
        source.Open();
        using var read = source.BeginTransaction(deferred: true);
        using var command = source.CreateCommand();
        command.Transaction = read;
        command.CommandText = "SELECT id FROM model_usage WHERE rowid=$row";
        var rowParameter = command.Parameters.AddWithValue("$row", cursor?.Tail ?? 0);
        var reset = cursor is null || (cursor.Tail > 0 && !string.Equals(command.ExecuteScalar() as string, cursor.Id, StringComparison.Ordinal));
        if (reset)
        {
            command.CommandText = "SELECT rowid,id FROM model_usage ORDER BY rowid DESC LIMIT 1";
            command.Parameters.Clear();
            using var tail = command.ExecuteReader();
            cursor = tail.Read() ? new(tail.GetInt64(0), tail.GetString(1), tail.GetInt64(0)) : new(0, null, null);
        }
        var position = cursor!;
        var checkedRows = new List<(Pending, Row?)>();
        if (!reset)
        {
            using var pending = store.Command("""
                SELECT source_row,source_id FROM zcode_pending WHERE path=$path ORDER BY checked_at,source_row LIMIT $limit
                """, null, ("$path", path), ("$limit", PendingChecks));
            using var candidates = pending.ExecuteReader();
            command.CommandText = Projection + " WHERE m.rowid=$row";
            while (candidates.Read())
            {
                token.ThrowIfCancellationRequested();
                var item = new Pending(candidates.GetInt64(0), candidates.GetString(1));
                rowParameter.Value = item.RowId;
                using var current = command.ExecuteReader();
                checkedRows.Add((item, current.Read() ? ReadRow(current) : null));
            }
        }
        command.Parameters.Clear();
        var offset = command.Parameters.AddWithValue("$row", position.Tail);
        var cap = command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<Row>();
        command.CommandText = Projection + " WHERE m.rowid>$row ORDER BY m.rowid LIMIT $limit";
        ReadRows();
        if (rows.Count > 0) position = position with { Tail = rows[^1].RowId, Id = rows[^1].Id };
        if (position.Backfill is { } backfill && rows.Count < limit)
        {
            var count = rows.Count;
            offset.Value = backfill;
            cap.Value = limit - count;
            // 首批从最新历史向后补读，最新新增记录每轮仍然优先。
            command.CommandText = Projection + " WHERE m.rowid<=$row ORDER BY m.rowid DESC LIMIT $limit";
            ReadRows();
            position = position with { Backfill = rows.Count == limit && rows.Count > count && rows[^1].RowId > 1 ? rows[^1].RowId - 1 : null };
        }
        return (position, rows, checkedRows, reset);

        void ReadRows()
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                rows.Add(ReadRow(reader));
            }
        }
    }

    Position? ReadCursor(UsageStore store)
    {
        using var command = store.Command("SELECT tail,tail_id,backfill FROM zcode_cursor WHERE path=$path", null, ("$path", path));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), Text(reader, 1), Number(reader, 2)) : null;
    }

    void WritePending(UsageStore store, Row row, long checkedAt, SqliteTransaction transaction)
    {
        using var command = store.Command("""
            INSERT INTO zcode_pending(path,source_row,source_id,checked_at) VALUES($path,$row,$id,$time)
            ON CONFLICT(path,source_row) DO UPDATE SET source_id=excluded.source_id,checked_at=excluded.checked_at
            """, transaction, ("$path", path), ("$row", row.RowId), ("$id", row.Id), ("$time", checkedAt));
        command.ExecuteNonQuery();
    }

    static string? Text(SqliteDataReader reader, int index) => reader.GetValue(index) as string;
    static long? Number(SqliteDataReader reader, int index) => reader.GetValue(index) is long value ? value : null;
    static Row ReadRow(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetString(1), Text(reader, 2),
        Text(reader, 3), Text(reader, 4), Text(reader, 5), Number(reader, 6), Number(reader, 7), Number(reader, 8),
        Number(reader, 9), Number(reader, 10), Number(reader, 11));

    sealed record Position(long Tail, string? Id, long? Backfill);
    sealed record Pending(long RowId, string Id);
    sealed record Row(long RowId, string Id, string? Session, string? Parent, string? Model, string? Status,
        long? CompletedAt, long? Input, long? Output, long? CacheRead, long? CacheWrite, long? Reasoning)
    {
        bool IsGlm => Model is { Length: >= 5 and <= 96 } && Model.StartsWith("glm-", StringComparison.OrdinalIgnoreCase)
            && char.IsAsciiDigit(Model[4]);
        internal bool IsPending => IsGlm && CompletedAt is null && Status is not ("completed" or "error" or "failed" or "cancelled" or "aborted");

        internal UsageEvent? ToEvent()
        {
            // 完成请求使用已归一化的原始计数，缓存已计入输入、推理已计入输出。
            if (!IsGlm || Status != "completed" || string.IsNullOrWhiteSpace(Session) ||
                CompletedAt is not (> 0 and <= 253402300799999) || Input is not >= 0 || Output is not >= 0)
                return null;
            var input = Input.Value;
            var output = Output.Value;
            var cacheRead = CacheRead ?? 0;
            var cacheWrite = CacheWrite ?? 0;
            var reasoning = Reasoning ?? 0;
            if (cacheRead < 0 || cacheWrite < 0 || reasoning < 0 || cacheRead > input || cacheWrite > input - cacheRead ||
                reasoning > output || input > long.MaxValue - output || input + output == 0) return null;
            return new("zcode:" + Id, Session, Parent, Model!, DateTimeOffset.FromUnixTimeMilliseconds(CompletedAt.Value),
                new(input, cacheRead, cacheWrite, output, reasoning, input + output), "request");
        }
    }
}
