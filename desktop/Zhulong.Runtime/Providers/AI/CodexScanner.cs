using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zhulong.Runtime.Providers.AI;

public sealed record ScanResult(long BytesRead, int LinesRead, int EventsAdded, bool HasMore, long AnchorBytesRead = 0);

public sealed class CodexScanner(UsageStore store)
{
    // 一个调用最多分配一批日志；游标只越过完整的换行。
    public ScanResult ScanFile(string path, int maxBytes = 4 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        using var transaction = store.Connection.BeginTransaction();
        var saved = store.ReadCursor(path, transaction);
        var info = new FileInfo(path);
        var length = info.Length;
        var writeTicks = info.LastWriteTimeUtc.Ticks;
        var created = info.CreationTimeUtc.Ticks;
        if (saved is not null && !saved.PendingBatch && saved.ObservedLength == length && saved.WriteTicks == writeTicks && saved.CreationTicks == created)
        {
            transaction.Commit();
            return new ScanResult(0, 0, 0, false);
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        length = Math.Min(length, stream.Length);
        long anchorBytes = 0;
        var offset = saved?.Offset ?? 0;
        if (saved is not null && (length < offset || saved.CreationTicks != created ||
            (length == saved.ObservedLength && writeTicks != saved.WriteTicks) ||
            Anchor(stream, offset, ref anchorBytes) != saved.Anchor))
        {
            saved = null;
            offset = 0;
        }
        var state = saved?.State ?? new ParserState();
        var oldAnchor = Anchor(stream, offset, ref anchorBytes);
        var count = (int)Math.Min(maxBytes, length - offset);
        var buffer = new byte[count];
        stream.Position = offset;
        var read = 0;
        while (read < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = stream.Read(buffer, read, count - read);
            if (next == 0) break;
            read += next;
        }
        var lines = 0;
        var added = 0;
        var start = 0;
        var malformed = saved?.MalformedLines ?? 0;
        for (var i = 0; i < read; i++)
        {
            if ((i & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (buffer[i] != (byte)'\n') continue;
            var line = Encoding.UTF8.GetString(buffer, start, i - start).TrimEnd('\r');
            if (offset + start == 0) line = line.TrimStart('\uFEFF');
            try
            {
                var parsed = CodexParser.Parse(line, state);
                foreach (var value in parsed.Usage) added += store.Insert(value, transaction);
                if (parsed.Quota is not null) store.SaveQuota(parsed.Quota, state.SessionId ?? path, transaction);
            }
            catch (JsonException) { malformed++; }
            lines++;
            start = i + 1;
        }
        // 修改历史内容时不能把旧状态和新字节提交到同一个游标。
        info.Refresh();
        if (stream.Length < length || info.CreationTimeUtc.Ticks != created ||
            (info.Length <= length && info.LastWriteTimeUtc.Ticks != writeTicks) ||
            Anchor(stream, offset, ref anchorBytes) != oldAnchor)
            throw new IOException("日志在读取期间被替换，请重新扫描。");
        var hasMore = read == maxBytes && offset + read < length;
        if (hasMore && start == 0)
            throw new InvalidDataException("单条日志超过本批读取上限，游标未推进。");
        var cursor = new Cursor(offset + start, length, writeTicks, created,
            Anchor(stream, offset + start, ref anchorBytes), state, malformed, hasMore);
        store.SaveCursor(path, cursor, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new ScanResult(read, lines, added, hasMore, anchorBytes);
    }

    static string Anchor(FileStream stream, long offset, ref long bytes)
    {
        if (offset == 0) return "";
        var size = (int)Math.Min(256, offset);
        var data = new byte[size * 2];
        stream.Position = 0;
        stream.ReadExactly(data.AsSpan(0, size));
        stream.Position = offset - size;
        stream.ReadExactly(data.AsSpan(size, size));
        bytes += data.Length;
        return Convert.ToHexString(SHA256.HashData(data));
    }
}
