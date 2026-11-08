using System.Text.Json;

namespace Zhulong.Runtime.Actions;

internal sealed record ActionStamp(string Action, string Target, DateTimeOffset At);
internal sealed record AppliedLimit(ProcessIdentity Process, ProcessLimits Original);
internal sealed class ActionState
{
    public List<ActionStamp> Attempts { get; set; } = [];
    public List<AppliedLimit> Applied { get; set; } = [];
}

// 状态仅在动作时写；恢复所需的原始属性先持久化，再改变目标进程。
internal sealed class ActionStateFile
{
    readonly string? path;
    public ActionState State { get; }
    public bool Readable { get; private set; } = true;
    public ActionStateFile(string? path)
    {
        this.path = path;
        if (path is null || !File.Exists(path)) { State = new(); return; }
        try
        {
            State = JsonSerializer.Deserialize<ActionState>(File.ReadAllText(path, System.Text.Encoding.UTF8)) ?? throw new JsonException("动作状态为空。");
            if (State.Attempts is null || State.Applied is null) throw new JsonException("动作状态缺少记录。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { State = new(); Readable = false; }
    }
    public void Save()
    {
        if (!Readable) throw new IOException("动作历史不可读取，拒绝重置熔断记录。");
        if (path is null) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(State), System.Text.Encoding.UTF8);
        File.Move(temporary, path, true);
    }
}
