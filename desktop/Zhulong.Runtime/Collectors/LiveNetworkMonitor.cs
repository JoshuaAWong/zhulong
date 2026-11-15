using System.Diagnostics;
using System.Runtime.InteropServices;
using Zhulong.Core;

namespace Zhulong.Runtime.Collectors;

internal sealed class LiveNetworkMonitor
{
    readonly record struct Octets(ulong In, ulong Out);

    Dictionary<ulong, Octets>? previous;
    long previousTick;

    public LiveNetworkReadout Read()
    {
        var current = ReadCounters();
        var tick = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(previousTick, tick).TotalSeconds;
        var baseline = previous;
        previous = current;
        previousTick = tick;
        var timestamp = DateTimeOffset.UtcNow;

        // 网卡重连、拓扑变化或计数重置后先重建基线，空闲时才返回真零。
        if (current is null || current.Count == 0 || baseline is null || baseline.Count != current.Count || elapsed <= 0)
            return new(timestamp, null, null);
        double down = 0, up = 0;
        foreach (var (luid, counters) in current)
        {
            if (!baseline.TryGetValue(luid, out var prior) || counters.In < prior.In || counters.Out < prior.Out)
                return new(timestamp, null, null);
            down += counters.In - prior.In;
            up += counters.Out - prior.Out;
        }
        return new(timestamp, down / elapsed, up / elapsed);
    }

    static Dictionary<ulong, Octets>? ReadCounters()
    {
        if (GetIfTable2(out var table) != 0 || table == IntPtr.Zero) return null;
        try
        {
            int count = Marshal.ReadInt32(table);
            if (count is < 0 or > 4096) return null;
            var counters = new Dictionary<ulong, Octets>();
            var row = IntPtr.Add(table, TableRowOffset);
            for (int i = 0; i < count; i++, row = IntPtr.Add(row, RowSize))
            {
                const int hardware = 1, filter = 2, up = 1;
                int flags = Marshal.ReadByte(row, FlagsOffset);
                if ((flags & (hardware | filter)) != hardware || Marshal.ReadInt32(row, OperStatusOffset) != up) continue;
                var luid = unchecked((ulong)Marshal.ReadInt64(row));
                counters[luid] = new(unchecked((ulong)Marshal.ReadInt64(row, InOctetsOffset)),
                    unchecked((ulong)Marshal.ReadInt64(row, OutOctetsOffset)));
            }
            return counters;
        }
        finally { FreeMibTable(table); }
    }

    static readonly int TableRowOffset = Marshal.OffsetOf<Table>(nameof(Table.FirstRow)).ToInt32();
    static readonly int RowSize = Marshal.SizeOf<Row>();
    static readonly int FlagsOffset = Marshal.OffsetOf<Row>(nameof(Row.InterfaceAndOperStatusFlags)).ToInt32();
    static readonly int OperStatusOffset = Marshal.OffsetOf<Row>(nameof(Row.OperStatus)).ToInt32();
    static readonly int InOctetsOffset = Marshal.OffsetOf<Row>(nameof(Row.InOctets)).ToInt32();
    static readonly int OutOctetsOffset = Marshal.OffsetOf<Row>(nameof(Row.OutOctets)).ToInt32();

    // 由原生结构计算对齐，只读取四个需要的字段，不封送名称和完整计数器数组。
    [StructLayout(LayoutKind.Sequential)]
    struct Table
    {
        public uint NumEntries;
        public Row FirstRow;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Row
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 257)] public ushort[] Alias;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 257)] public ushort[] Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed;
        public ulong InOctets, InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos,
            InUcastOctets, InMulticastOctets, InBroadcastOctets;
        public ulong OutOctets, OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors,
            OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)] static extern uint GetIfTable2(out IntPtr table);
    [DllImport("iphlpapi.dll", ExactSpelling = true)] static extern void FreeMibTable(IntPtr memory);
}
