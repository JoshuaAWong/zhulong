namespace Zhulong.Runtime.Providers.AI;

// 深度优先保留目录游标，每轮只访问有限目录项，不全量排序或缓存所有路径。
internal sealed class IncrementalSources(IEnumerable<string> roots, string? fileName = null) : IDisposable
{
    readonly IEnumerator<string> roots = roots.GetEnumerator();
    readonly Stack<IEnumerator<string>> stack = new();

    internal string? Next(ref int visited, int maxVisited, CancellationToken token, out bool finished)
    {
        finished = false;
        while (visited < maxVisited)
        {
            token.ThrowIfCancellationRequested();
            if (stack.Count == 0)
            {
                if (!roots.MoveNext()) { finished = true; return null; }
                visited++;
                if (Directory.Exists(roots.Current))
                    stack.Push(Directory.EnumerateFileSystemEntries(roots.Current).GetEnumerator());
                continue;
            }
            var current = stack.Peek();
            bool moved;
            try { moved = current.MoveNext(); }
            catch { current.Dispose(); stack.Pop(); throw; }
            if (!moved) { current.Dispose(); stack.Pop(); continue; }
            visited++;
            var entry = current.Current;
            var attributes = File.GetAttributes(entry);
            // 目录联接可能返回祖先目录，跳过可避免无限遍历与跨根扫描。
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            if ((attributes & FileAttributes.Directory) != 0)
                stack.Push(Directory.EnumerateFileSystemEntries(entry).GetEnumerator());
            else if (fileName is null ? string.Equals(Path.GetExtension(entry), ".jsonl", StringComparison.OrdinalIgnoreCase)
                : string.Equals(Path.GetFileName(entry), fileName, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    public void Dispose()
    {
        while (stack.TryPop(out var entries)) entries.Dispose();
        roots.Dispose();
    }
}
