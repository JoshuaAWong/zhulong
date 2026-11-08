using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Zhulong.Runtime.Actions;

public sealed record ProcessIdentity(int Pid, string Name, long CreatedTicks, double CommitGb,
    double WorkingSetGb, string? ExecutablePath, string? ProtectionReason = null);
public sealed record ProcessLimits(uint Priority, ulong Affinity, ulong SystemAffinity);

// 测试替身只需实现这些边界；规则/安全链测试不操作真实进程。
public interface IProcessBoundary
{
    int CurrentPid { get; }
    int ProcessorCount { get; }
    IReadOnlyList<ProcessIdentity> Find(string name);
    ProcessIdentity? Read(int pid);
    void Terminate(ProcessIdentity expected);
    void Trim(ProcessIdentity expected);
    ProcessLimits ReadLimits(ProcessIdentity expected);
    void SetLimits(ProcessIdentity expected, uint priority, ulong affinity);
}

public sealed class NativeProcessBoundary : IProcessBoundary
{
    const uint Query = 0x1000, QueryMemory = 0x0400 | 0x0010;
    public int CurrentPid => Environment.ProcessId;
    public int ProcessorCount => Environment.ProcessorCount;

    public IReadOnlyList<ProcessIdentity> Find(string name)
    {
        var values = new List<ProcessIdentity>();
        foreach (var process in Process.GetProcessesByName(SafetyPolicy.Normalize(name)))
        {
            using (process)
                try { if (Read(process.Id) is { } value) values.Add(value); }
                catch (Win32Exception) { }
        }
        return values;
    }

    public ProcessIdentity? Read(int pid)
    {
        using var handle = Open(pid, QueryMemory);
        if (handle.IsInvalid) return null;
        if (!GetProcessTimes(handle.DangerousGetHandle(), out var created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        if (!GetProcessMemoryInfo(handle.DangerousGetHandle(), out counters, counters.Size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        string? executable = QueryFullProcessImageName(handle.DangerousGetHandle(), 0, path, ref length) ? path.ToString() : null;
        string? protection = executable is null ? "exe_unreadable" : null;
        if (executable?.Contains("\\Windows\\System32\\", StringComparison.OrdinalIgnoreCase) == true ||
            executable?.Contains("\\Windows\\SysWOW64\\", StringComparison.OrdinalIgnoreCase) == true)
            protection = "system_path";
        if (OpenProcessToken(handle.DangerousGetHandle(), 8, out var token))
        {
            try
            {
                using var identity = new WindowsIdentity(token);
                if (identity.User?.Value is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") protection = "system_account";
            }
            finally { CloseHandle(token); }
        }
        else protection ??= "account_unreadable";
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            GetWindowThreadProcessId(foreground, out var foregroundPid);
            if (foregroundPid == pid) protection = "foreground";
        }
        return new ProcessIdentity(pid, executable is null ? "" : Path.GetFileName(executable),
            DateTime.FromFileTimeUtc(created.Value).Ticks, counters.PrivateUsage.ToUInt64() / 1073741824d,
            counters.WorkingSet.ToUInt64() / 1073741824d, executable, protection);
    }

    public void Terminate(ProcessIdentity expected)
    {
        using var handle = CheckedOpen(expected, Query | 1);
        if (!TerminateProcess(handle.DangerousGetHandle(), 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Trim(ProcessIdentity expected)
    {
        using var handle = CheckedOpen(expected, Query | 0x0100);
        if (!EmptyWorkingSet(handle.DangerousGetHandle())) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public ProcessLimits ReadLimits(ProcessIdentity expected)
    {
        using var handle = CheckedOpen(expected, Query);
        var priority = GetPriorityClass(handle.DangerousGetHandle());
        if (priority == 0 || !GetProcessAffinityMask(handle.DangerousGetHandle(), out var process, out var system))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new ProcessLimits(priority, process.ToUInt64(), system.ToUInt64());
    }

    public void SetLimits(ProcessIdentity expected, uint priority, ulong affinity)
    {
        using var handle = CheckedOpen(expected, Query | 0x0200);
        // 同一进程句柄上复核创建时间再变更，避免检查后PID被复用。
        var previous = GetPriorityClass(handle.DangerousGetHandle());
        if (previous == 0 || !SetPriorityClass(handle.DangerousGetHandle(), priority)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetProcessAffinityMask(handle.DangerousGetHandle(), new UIntPtr(affinity)))
        {
            var error = Marshal.GetLastWin32Error();
            var restored = SetPriorityClass(handle.DangerousGetHandle(), previous);
            throw new Win32Exception(error, restored ? "亲和性设置失败，优先级已还原。" : "亲和性设置失败，原属性恢复待处理。");
        }
    }

    static NativeHandle CheckedOpen(ProcessIdentity expected, uint access)
    {
        var handle = Open(expected.Pid, access);
        if (handle.IsInvalid) { handle.Dispose(); throw new InvalidOperationException("process_gone"); }
        if (!GetProcessTimes(handle.DangerousGetHandle(), out var created, out _, out _, out _))
        { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (DateTime.FromFileTimeUtc(created.Value).Ticks != expected.CreatedTicks)
        { handle.Dispose(); throw new InvalidOperationException("pid_reused"); }
        return handle;
    }

    static NativeHandle Open(int pid, uint access)
    {
        var result = new NativeHandle(OpenProcess(access, false, pid));
        if (result.IsInvalid && Marshal.GetLastWin32Error() is not (87 or 1168))
        { result.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return result;
    }

    sealed class NativeHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] struct FileTime { public uint Low, High; public readonly long Value => ((long)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)] struct MemoryCounters
    {
        public uint Size, PageFaults;
        public UIntPtr PeakWorkingSet, WorkingSet, QuotaPeakPaged, QuotaPaged, QuotaPeakNonpaged,
            QuotaNonpaged, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(IntPtr handle, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
    [DllImport("psapi.dll", SetLastError = true)] static extern bool GetProcessMemoryInfo(IntPtr handle, out MemoryCounters counters, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder name, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("psapi.dll", SetLastError = true)] static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetPriorityClass(IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetPriorityClass(IntPtr process, uint priority);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessAffinityMask(IntPtr process, UIntPtr mask);
}
