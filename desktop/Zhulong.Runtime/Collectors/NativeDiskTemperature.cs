using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zhulong.Runtime.Collectors;

internal static class NativeDiskTemperature
{
    const uint IoctlStorageQueryProperty = 0x002D1400;
    const uint StorageDeviceTemperatureProperty = 52;
    const uint StorageAdapterTemperatureProperty = 51;
    const int DescriptorHeaderSize = 24;
    const int TemperatureInfoSize = 16;

    // 查询结构末尾的占位字节按 Windows SDK 的 4 字节对齐。
    [StructLayout(LayoutKind.Sequential)]
    struct StoragePropertyQuery
    {
        public uint Property;
        public uint QueryType;
        public uint AdditionalParameters;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint code, in StoragePropertyQuery input,
        int inputSize, [Out] byte[] output, int outputSize, out uint returned, IntPtr overlapped);

    internal static (bool Present, short? Celsius) Read(int number)
    {
        using var handle = CreateFile(@"\\.\PhysicalDrive" + number, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return (false, null);
        var output = new byte[4096];
        var temperature = Query(handle, StorageDeviceTemperatureProperty, output) ??
            Query(handle, StorageAdapterTemperatureProperty, output);
        return (true, temperature);
    }

    static short? Query(SafeFileHandle handle, uint property, byte[] output)
    {
        var input = new StoragePropertyQuery { Property = property };
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, in input, Marshal.SizeOf<StoragePropertyQuery>(),
            output, output.Length, out var returned, IntPtr.Zero) || returned > output.Length) return null;
        return ReadComposite(output.AsSpan(0, (int)returned));
    }

    internal static short? ReadComposite(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < DescriptorHeaderSize) return null;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[12..]);
        if (size < DescriptorHeaderSize || size > descriptor.Length ||
            count > (size - DescriptorHeaderSize) / TemperatureInfoSize) return null;

        // 设备索引 0 是本指标的口径，其他索引的局部传感器读数独立。
        for (var index = 0; index < count; index++)
        {
            var info = descriptor[(DescriptorHeaderSize + index * TemperatureInfoSize)..];
            if (BinaryPrimitives.ReadUInt16LittleEndian(info) != 0) continue;
            var temperature = BinaryPrimitives.ReadInt16LittleEndian(info[2..]);
            return temperature == short.MinValue ? null : temperature;
        }
        return null;
    }
}
