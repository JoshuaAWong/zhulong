using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace Zhulong.Runtime.Providers.AI;

// data_version只在同一连接前后可比较；每次命令结束就释放读事务，不阻碍WAL重用。
internal sealed class CodexStateObserver : IDisposable
{
    SqliteConnection? connection;
    string? sourcePath, fileId;
    long generation;

    internal SourceVersion Observe(string path)
    {
        path = Path.GetFullPath(path);
        var currentId = Identity(path);
        if (connection is null || sourcePath != path || fileId != currentId)
        {
            Dispose();
            var next = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
            try { next.Open(); }
            catch { next.Dispose(); throw; }
            connection = next;
            sourcePath = path;
            fileId = currentId;
            generation++;
        }
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return new(currentId, generation, Convert.ToInt64(command.ExecuteScalar()));
    }

    static string Identity(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException("无法读取Codex状态库文件身份", new Win32Exception(Marshal.GetLastPInvokeError()));
        return $"{information.Volume:X8}:{information.IndexHigh:X8}{information.IndexLow:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    public void Dispose() { connection?.Dispose(); connection = null; }
}

internal sealed record SourceVersion(string FileId, long ConnectionGeneration, long DataVersion);
