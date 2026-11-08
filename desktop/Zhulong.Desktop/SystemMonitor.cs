using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Zhulong.Desktop;

public sealed record ProcessRow(int Pid, string Name, double MemoryGb, double? CpuPercent);

public sealed record SystemSnapshot(DateTimeOffset Timestamp, double? CommitPercent,
    double? CommitUsedGb, double? CommitLimitGb, double? MemoryPercent, double? MemoryUsedGb,
    double? MemoryTotalGb, double? CpuPercent, List<ProcessRow> Processes, string? Error = null);

// 仅由桌面后台采样任务调用，不处置进程，也不读取旧烛龙的数据。
public sealed class SystemMonitor
{
    private (ulong Idle, ulong Kernel, ulong User)? previousCpu;
    private const double Gigabyte = 1024d * 1024 * 1024;

    public SystemSnapshot Collect()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        double? commitPercent = null, commitUsed = null, commitLimit = null;
        double? memoryPercent = null, memoryUsed = null, memoryTotal = null;
        double? cpuPercent = null;

        if (GetPerformanceInfo(out var performance, (uint)Marshal.SizeOf<PerformanceInformation>()))
        {
            var pageSize = (double)performance.PageSize.ToUInt64();
            var usedBytes = performance.CommitTotal.ToUInt64() * pageSize;
            var limitBytes = performance.CommitLimit.ToUInt64() * pageSize;
            commitUsed = usedBytes / Gigabyte;
            commitLimit = limitBytes / Gigabyte;
            commitPercent = limitBytes > 0 ? usedBytes * 100 / limitBytes : null;
        }
        else errors.Add($"提交内存采集失败（Win32 {Marshal.GetLastWin32Error()}）");

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            memoryPercent = memory.MemoryLoad;
            memoryTotal = memory.TotalPhysical / Gigabyte;
            memoryUsed = (memory.TotalPhysical - memory.AvailablePhysical) / Gigabyte;
        }
        else errors.Add($"物理内存采集失败（Win32 {Marshal.GetLastWin32Error()}）");

        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var current = (Idle: idle.Value, Kernel: kernel.Value, User: user.Value);
            if (previousCpu is { } previous)
            {
                var total = current.Kernel - previous.Kernel + current.User - previous.User;
                var idleDelta = current.Idle - previous.Idle;
                if (total > 0) cpuPercent = Math.Clamp((total - (double)idleDelta) * 100 / total, 0, 100);
            }
            previousCpu = current;
        }
        else
        {
            previousCpu = null;
            errors.Add($"CPU采集失败（Win32 {Marshal.GetLastWin32Error()}）");
        }

        var processes = new List<ProcessRow>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        processes.Add(new ProcessRow(process.Id, process.ProcessName,
                            process.WorkingSet64 / Gigabyte, null));
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                    {
                        // 采样期间退出或不可读取的进程不进入本次排行。
                    }
                }
            }
        }
        catch (Win32Exception exception) { errors.Add($"进程排行采集失败（Win32 {exception.NativeErrorCode}）"); }

        return new SystemSnapshot(timestamp, commitPercent, commitUsed, commitLimit,
            memoryPercent, memoryUsed, memoryTotal, cpuPercent,
            processes.OrderByDescending(value => value.MemoryGb).Take(5).ToList(),
            errors.Count == 0 ? null : string.Join("；", errors));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
            SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low, High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user);
}
