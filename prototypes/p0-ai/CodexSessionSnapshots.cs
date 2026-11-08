using Microsoft.Data.Sqlite;

namespace Zhulong.P0.AI;

// 线程计数是独立快照，不能与 JSONL 请求账本相加。
public static class CodexSessionSnapshots
{
    public static int ReadInto(UsageStore store, string sourcePath)
    {
        var path = Path.GetFullPath(sourcePath);
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2
        }.ToString());
        source.Open();

        // 使用 SQLite 自身的只读事务读取一致视图，包含现存 WAL 中的已提交记录。
        using var readTransaction = source.BeginTransaction(deferred: true);
        using var transaction = store.Connection.BeginTransaction();
        using var command = source.CreateCommand();
        command.Transaction = readTransaction;
        command.CommandText = "SELECT id,tokens_used,updated_at FROM threads";
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            store.SaveSessionSnapshot(reader.GetString(0), reader.GetInt64(1),
                reader.GetInt64(2), path, transaction);
            count++;
        }
        reader.Close();
        readTransaction.Commit();
        transaction.Commit();
        return count;
    }
}
