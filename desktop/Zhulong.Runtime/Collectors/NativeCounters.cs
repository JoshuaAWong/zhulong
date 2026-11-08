using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;

namespace Zhulong.Runtime.Collectors;

internal static class NativeCounters
{
    internal const double Gb = 1024d * 1024 * 1024;
    internal const double Mb = 1024d * 1024;
    [StructLayout(LayoutKind.Sequential)] internal struct PerformanceInfo
    {
        public uint Size;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
            SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct CoreTimes
    {
        public long Idle, Kernel, User, Dpc, Interrupt;
        public uint InterruptCount;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct DiskPerformance
    {
        public long BytesRead, BytesWritten, ReadTime, WriteTime, IdleTime;
        public uint ReadCount, WriteCount, QueueDepth, SplitCount;
        public long QueryTime;
        public uint StorageDeviceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string StorageManagerName;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetPerformanceInfo(out PerformanceInfo value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus value);
    [DllImport("kernel32.dll")] static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int type, IntPtr buffer, int size, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        IntPtr input, uint inputSize, out DiskPerformance output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("iphlpapi.dll")] internal static extern uint GetBestInterface(uint destination, out uint index);

    [DllImport("iphlpapi.dll")] static extern uint GetIpForwardTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order);
    internal static string? Gateway(string host)
    {
        uint size = 0;
        GetIpForwardTable(IntPtr.Zero, ref size, false);
        if (size < 4 || size > 1024 * 1024) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetIpForwardTable(buffer, ref size, false) != 0) return null;
            uint? preferred = null;
            if (IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetwork &&
                GetBestInterface(BitConverter.ToUInt32(address.GetAddressBytes()), out var index) == 0) preferred = index;
            var count = Marshal.ReadInt32(buffer);
            uint? selected = null;
            var selectedPreferred = false;
            uint bestMetric = uint.MaxValue;
            for (var i = 0; i < count && 4 + (i + 1) * 56 <= size; i++)
            {
                var row = 4 + i * 56;
                if (Marshal.ReadInt32(buffer, row) != 0 || Marshal.ReadInt32(buffer, row + 4) != 0) continue;
                var nextHop = (uint)Marshal.ReadInt32(buffer, row + 12);
                if (nextHop == 0) continue;
                var matches = (uint)Marshal.ReadInt32(buffer, row + 16) == preferred;
                var metric = (uint)Marshal.ReadInt32(buffer, row + 36);
                if (selected is null || matches && !selectedPreferred || matches == selectedPreferred && metric < bestMetric)
                { selected = nextHop; selectedPreferred = matches; bestMetric = metric; }
            }
            return selected is { } gateway ? new IPAddress(gateway).ToString() : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static CoreTimes[] CpuTimes()
    {
        var size = Marshal.SizeOf<CoreTimes>();
        var count = (int)Math.Clamp(GetActiveProcessorCount(ushort.MaxValue), 1, 1024);
        var buffer = Marshal.AllocHGlobal(count * size);
        try
        {
            var status = NtQuerySystemInformation(8, buffer, count * size, out var returned);
            if (status < 0 || returned <= 0 || returned > count * size) return [];
            var result = new CoreTimes[returned / size];
            for (var i = 0; i < result.Length; i++) result[i] = Marshal.PtrToStructure<CoreTimes>(buffer + i * size);
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static (double UsedGb, double TotalGb)? Pagefile()
    {
        const int capacity = 1024 * 1024;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var status = NtQuerySystemInformation(18, buffer, capacity, out var returned);
            if (status < 0 || returned < 16 || returned > capacity) return null;
            long total = 0, used = 0;
            var offset = 0;
            for (var i = 0; i < 1024 && offset + 16 <= returned; i++)
            {
                total += (uint)Marshal.ReadInt32(buffer, offset + 4);
                used += (uint)Marshal.ReadInt32(buffer, offset + 8);
                var next = Marshal.ReadInt32(buffer, offset);
                if (next == 0) return (used * (double)Environment.SystemPageSize / Gb, total * (double)Environment.SystemPageSize / Gb);
                if (next < 16 || offset + next >= returned) return null;
                offset += next;
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static DiskPerformance? Disk(int number)
    {
        using var handle = CreateFile(@"\\.\PhysicalDrive" + number, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        return DeviceIoControl(handle, 0x00070020, IntPtr.Zero, 0, out var value,
            (uint)Marshal.SizeOf<DiskPerformance>(), out _, IntPtr.Zero) ? value : null;
    }
}
