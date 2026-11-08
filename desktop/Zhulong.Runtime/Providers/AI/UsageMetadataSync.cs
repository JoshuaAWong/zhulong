using Microsoft.Data.Sqlite;

namespace Zhulong.Runtime.Providers.AI;

public sealed partial class UsageStore
{
    void InitializeMetadataSync()
    {
        using var command = Command("""
            CREATE TABLE IF NOT EXISTS metadata_sync (
                source TEXT PRIMARY KEY, completed_watermark INTEGER NOT NULL,
                after_updated INTEGER NOT NULL, after_id TEXT NOT NULL,pending INTEGER NOT NULL)
            """);
        command.ExecuteNonQuery();
    }

    internal MetadataCursor ReadMetadataCursor(string path, SqliteTransaction transaction)
    {
        using var command = Command("SELECT completed_watermark,after_updated,after_id,pending FROM metadata_sync WHERE source=$path",
            transaction, ("$path", path));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetBoolean(3))
            : new(0, 0, "", false);
    }

    internal void SaveMetadataCursor(string path, MetadataCursor cursor, SqliteTransaction transaction)
    {
        using var command = Command("""
            INSERT INTO metadata_sync VALUES($path,$watermark,$updated,$id,$pending)
            ON CONFLICT(source) DO UPDATE SET completed_watermark=excluded.completed_watermark,
                after_updated=excluded.after_updated,after_id=excluded.after_id,pending=excluded.pending
            """, transaction, ("$path", path), ("$watermark", cursor.CompletedWatermark),
            ("$updated", cursor.AfterUpdated), ("$id", cursor.AfterId), ("$pending", cursor.Pending));
        command.ExecuteNonQuery();
    }
}

internal sealed record MetadataCursor(long CompletedWatermark, long AfterUpdated, string AfterId, bool Pending);
