using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Zhulong.Core;

namespace Zhulong.Runtime.Actions;

// 与进程排行共用原有采样，只保存两段空闲判断和当前候选快照。
internal sealed class ManualTrimActivity(int sampleSeconds)
{
    record Snapshot(long Stamp, bool Ready, IReadOnlyList<ProcessRow> Rows);
    Dictionary<int, long> previousIdle = [];
    long previousStamp;
    bool previousHasRates;
    Snapshot? current;

    static bool HasRates(ProcessRow row) =>
        ValidRate(row.CpuPercent) && ValidRate(row.ReadMbS) && ValidRate(row.WriteMbS);

    static bool ValidRate(double? value) => value is >= 0 and var rate && double.IsFinite(rate);

    static bool LowActivity(ProcessRow row, int processors) =>
        row.CpuPercent is >= 0 and var cpu && cpu * Math.Max(1, processors) <= 1 &&
        row.ReadMbS is >= 0 and var read && row.WriteMbS is >= 0 and var write && read + write <= 0.1;

    internal void Observe(IReadOnlyList<ProcessRow> rows, int processors, long utcTicks, long stamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(previousStamp, stamp).TotalSeconds;
        var consecutive = elapsed > 0 && elapsed <= sampleSeconds * 2;
        var idle = new Dictionary<int, long>();
        var candidates = new List<ProcessRow>();
        var hasRates = false;
        foreach (var row in rows)
        {
            if (row.CreatedTicks is not { } creation || creation <= 0 || !HasRates(row)) continue;
            hasRates = true;
            if (!LowActivity(row, processors)) continue;
            idle[row.Pid] = creation;
            if (consecutive && previousIdle.TryGetValue(row.Pid, out var previous) && previous == creation &&
                double.IsFinite(row.MemoryGb) && row.MemoryGb >= ManualTrimCandidates.MinimumWorkingSetGb &&
                utcTicks - creation >= TimeSpan.FromSeconds(ManualTrimCandidates.MinimumAgeSeconds).Ticks)
                candidates.Add(row);
        }
        var ready = consecutive && previousHasRates && hasRates;
        previousIdle = idle;
        previousStamp = stamp;
        previousHasRates = hasRates;
        Volatile.Write(ref current, new(stamp, ready, candidates.ToArray()));
    }

    internal (bool HasRecentSamples, IReadOnlyList<ProcessRow> Rows) Read(long stamp)
    {
        var value = Volatile.Read(ref current);
        if (value is not { Ready: true }) return (false, []);
        var age = Stopwatch.GetElapsedTime(value.Stamp, stamp).TotalSeconds;
        return age >= 0 && age <= sampleSeconds * 2 ? (true, value.Rows) : (false, []);
    }
}

internal static class ManualTrimCandidates
{
    internal const double MinimumWorkingSetGb = 500d / 1024;
    internal const int MinimumAgeSeconds = 300;
    static readonly string WindowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static readonly string[] BackgroundRoles =
        ["tray", "updater", "update", "helper", "agent", "notifier", "background", "托盘", "更新程序", "后台", "助手"];
    static readonly string[] SensitiveMarkers =
    [
        "game", "steam", "battle.net", "riot", "battlEye", "anticheat", "vanguard", "ubisoft",
        "epicgames", "xbox", "gog galaxy", "eadesktop", "originclient", "minecraft", "roblox",
        "mihoyo", "hoyoplay", "honkai", "genshin", "zenless", "hyphelper",
        "emulator", "mumu", "bluestack", "ldplayer", "dnplayer", "nox", "gameloop", "leidian",
        "qemu", "virtualbox", "vbox", "vmware", "vmmem", "vmwp", "wsl", "retroarch",
        "ryujinx", "rpcs3", "pcsx2", "dolphin", "yuzu",
        "antivirus", "endpoint", "firewall", "security", "mcafee", "sophos", "norton",
        "malwarebytes", "avast", "avgsvc", "avira", "eset", "bitdefender", "kaspersky",
        "crowdstrike", "sentinelone", "carbonblack", "trend micro", "火绒", "杀毒", "安全",
        "模拟器", "游戏"
    ];
    static readonly HashSet<string> GenericRuntimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "conhost", "openconsole", "powershell", "pwsh", "rundll32", "wscript", "cscript",
        "java", "javaw", "dotnet", "node", "electron", "bun", "deno", "ruby", "perl", "php", "mono"
    };

    internal static ProcessIdentity[] Scan(IReadOnlyList<ProcessRow> idleRows, IProcessBoundary boundary,
        CancellationToken stop = default)
    {
        if (idleRows.Count == 0 || DesktopProcesses.Capture(boundary) is not { } desktop) return [];
        var candidates = new List<ProcessIdentity>();
        foreach (var row in idleRows)
        {
            stop.ThrowIfCancellationRequested();
            if (desktop.Lookup(row.Pid) is { } current && current.CreatedTicks == row.CreatedTicks &&
                desktop.Eligible(current))
                candidates.Add(current);
        }
        return candidates.OrderByDescending(process => process.WorkingSetGb).ThenBy(process => process.Pid).ToArray();
    }

    internal static bool IsStillBackground(ProcessIdentity candidate)
    {
        var desktop = DesktopProcesses.Capture(new NativeProcessBoundary());
        return desktop?.Lookup(candidate.Pid) is { } current && current.CreatedTicks == candidate.CreatedTicks &&
            string.Equals(current.ExecutablePath, candidate.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            desktop.Eligible(current);
    }

    internal static bool HasBackgroundRole(string name, string? description)
    {
        var normalized = SafetyPolicy.Normalize(name);
        if (GenericRuntimes.Contains(normalized) || Sensitive(normalized)) return false;
        if (BackgroundRoles.Any(role => normalized.EndsWith(role, StringComparison.OrdinalIgnoreCase))) return true;
        if (string.IsNullOrWhiteSpace(description)) return false;
        var words = description.Split([' ', '-', '/', '\\', '(', ')', ':'], StringSplitOptions.RemoveEmptyEntries);
        return BackgroundRoles.Any(role => words.Contains(role, StringComparer.OrdinalIgnoreCase) ||
            (role[0] > 127 && description.Contains(role, StringComparison.Ordinal)));
    }

    static bool Sensitive(string value) =>
        SensitiveMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    static bool LocalPath(string? path) => path is { Length: > 3 } &&
        char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    static bool Under(string path, string directory) =>
        path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    static bool BlockedRole(string name) =>
        SafetyPolicy.NameBlocked(name) || GenericRuntimes.Contains(SafetyPolicy.Normalize(name)) || Sensitive(name);

    record ProcessNode(int Pid, int Parent, string Name);

    sealed class DesktopProcesses(IProcessBoundary boundary, Dictionary<int, ProcessNode> nodes,
        HashSet<int> windows, int shell, uint session, string userSid)
    {
        readonly Dictionary<int, ProcessIdentity?> identities = [];
        readonly ILookup<int, ProcessNode> children = nodes.Values.ToLookup(node => node.Parent);
        string[]? protectedDirectories;

        internal static DesktopProcesses? Capture(IProcessBoundary boundary)
        {
            if (!ProcessIdToSessionId(boundary.CurrentPid, out var session) || session == 0) return null;
            using var user = WindowsIdentity.GetCurrent();
            if (user.User?.Value is not { } sid) return null;
            var shellWindow = GetShellWindow();
            if (shellWindow == IntPtr.Zero ||
                GetWindowThreadProcessId(shellWindow, out var shell) == 0 || shell <= 0) return null;
            if (ReadProcessGraph() is not { } graph || !graph.ContainsKey(shell)) return null;
            var windows = new HashSet<int>();
            if (!EnumWindows((window, _) =>
            {
                if ((IsWindowVisible(window) || IsIconic(window)) &&
                    GetWindowThreadProcessId(window, out var pid) != 0 && pid > 0)
                    windows.Add(pid);
                return true;
            }, IntPtr.Zero)) return null;
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || GetWindowThreadProcessId(foreground, out var foregroundPid) == 0 || foregroundPid <= 0)
                return null;
            windows.Add(foregroundPid);
            return new(boundary, graph, windows, shell, session, sid);
        }

        internal ProcessIdentity? Lookup(int pid)
        {
            if (identities.TryGetValue(pid, out var saved)) return saved;
            ProcessIdentity? value = null;
            try
            {
                if (nodes.TryGetValue(pid, out var node) &&
                    ProcessIdToSessionId(pid, out var targetSession) && targetSession == session &&
                    boundary.Read(pid) is { } read &&
                    string.Equals(read.UserSid, userSid, StringComparison.Ordinal) &&
                    string.Equals(read.Name, node.Name, StringComparison.OrdinalIgnoreCase) &&
                    LocalPath(read.ExecutablePath))
                    value = read;
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException
                or System.Security.SecurityException) { }
            identities[pid] = value;
            return value;
        }

        internal bool Eligible(ProcessIdentity candidate)
        {
            var name = candidate.Name;
            if (!double.IsFinite(candidate.WorkingSetGb) || candidate.WorkingSetGb < MinimumWorkingSetGb ||
                candidate.CreatedTicks <= 0 ||
                DateTime.UtcNow.Ticks - candidate.CreatedTicks < TimeSpan.FromSeconds(MinimumAgeSeconds).Ticks ||
                SafetyPolicy.ProcessBlocked(candidate, boundary.CurrentPid) is not null ||
                BlockedRole(name) || windows.Contains(candidate.Pid) || children.Contains(candidate.Pid))
                return false;
            var path = candidate.ExecutablePath!;
            if (!AllowedUserPath(path) || !RoleMetadata(candidate)) return false;
            if (FindApplicationRoot(candidate) is not { } root || HasProtectedFamily(root)) return false;
            protectedDirectories ??= nodes.Values
                .Where(node => windows.Contains(node.Pid) || Sensitive(node.Name))
                .Select(node => Lookup(node.Pid)?.ExecutablePath)
                .Where(executable => executable is not null && !Under(executable, WindowsFolder))
                .Select(executable => Path.GetDirectoryName(executable)!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return !protectedDirectories.Any(directory => Under(path, directory));
        }

        static bool AllowedUserPath(string path) => !Under(path, WindowsFolder) && !Sensitive(path);

        static bool RoleMetadata(ProcessIdentity process)
        {
            try
            {
                var metadata = FileVersionInfo.GetVersionInfo(process.ExecutablePath!);
                return !Sensitive(metadata.ProductName ?? "") && !Sensitive(metadata.CompanyName ?? "") &&
                    !Sensitive(metadata.FileDescription ?? "") && !Sensitive(metadata.OriginalFilename ?? "") &&
                    HasBackgroundRole(process.Name, metadata.FileDescription);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            { return false; }
        }

        int? FindApplicationRoot(ProcessIdentity process)
        {
            // 父进程必须仍存在且创建时间更早。Shell 用实际窗口 PID 和系统路径确认。
            var visited = new HashSet<int>();
            while (visited.Add(process.Pid) && nodes.TryGetValue(process.Pid, out var node) &&
                Lookup(node.Parent) is { } parent && parent.CreatedTicks <= process.CreatedTicks)
            {
                if (parent.Pid == shell)
                    return string.Equals(parent.ExecutablePath, Path.Combine(WindowsFolder, "explorer.exe"),
                        StringComparison.OrdinalIgnoreCase) ? process.Pid : null;
                if (windows.Contains(parent.Pid) || BlockedRole(parent.Name) ||
                    SafetyPolicy.ProcessBlocked(parent, boundary.CurrentPid) is not null ||
                    !AllowedUserPath(parent.ExecutablePath!))
                    return null;
                process = parent;
            }
            return null;
        }

        bool HasProtectedFamily(int root)
        {
            var pending = new Stack<int>();
            var visited = new HashSet<int>();
            pending.Push(root);
            while (pending.TryPop(out var pid))
            {
                if (!visited.Add(pid) || windows.Contains(pid) || Lookup(pid) is not { } member ||
                    BlockedRole(member.Name) || SafetyPolicy.ProcessBlocked(member, boundary.CurrentPid) is not null ||
                    !AllowedUserPath(member.ExecutablePath!))
                    return true;
                foreach (var child in children[pid])
                {
                    if (Lookup(child.Pid) is not { } identity || identity.CreatedTicks < member.CreatedTicks)
                        return true;
                    pending.Push(child.Pid);
                }
            }
            return false;
        }
    }

    static Dictionary<int, ProcessNode>? ReadProcessGraph()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return null;
        var row = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = "" };
        if (!Process32First(snapshot, ref row)) return null;
        var graph = new Dictionary<int, ProcessNode>();
        do
        {
            if (row.Pid > 0) graph[row.Pid] = new(row.Pid, row.Parent, row.Name);
        }
        while (Process32Next(snapshot, ref row));
        return Marshal.GetLastWin32Error() == 18 ? graph : null;
    }

    delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage;
        public int Pid;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads;
        public int Parent, PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(int pid, out uint session);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
}
