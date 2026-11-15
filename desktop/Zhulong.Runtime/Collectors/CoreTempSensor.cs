using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace Zhulong.Runtime.Collectors;

// Core Temp 公开的 CoreTempSharedDataEx 布局。只读取每次采样时存在的映射。
internal static class CoreTempSensor
{
    internal const string MetricName = "cpu_temp_c", Source = "Core Temp · CPU 温度";
    const int MaxCpus = 128, MaxCores = 256, TjMax = MaxCores * sizeof(uint),
        CoreCount = TjMax + MaxCpus * sizeof(uint), CpuCount = CoreCount + sizeof(uint),
        Temperatures = CpuCount + sizeof(uint), Fahrenheit = Temperatures + MaxCores * sizeof(float) + 4 * sizeof(float) + 100,
        DeltaToTjMax = Fahrenheit + 1;
    internal const int ReadingSize = DeltaToTjMax + 1;

    internal static double? Read()
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting("CoreTempMappingObjectEx", MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, ReadingSize, MemoryMappedFileAccess.Read);
            var bytes = new byte[ReadingSize];
            return view.ReadArray(0, bytes, 0, bytes.Length) == bytes.Length ? Decode(bytes) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    internal static double? Decode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ReadingSize || buffer[Fahrenheit] > 1 || buffer[DeltaToTjMax] > 1) return null;
        var cores = BinaryPrimitives.ReadUInt32LittleEndian(buffer[CoreCount..]);
        var cpus = BinaryPrimitives.ReadUInt32LittleEndian(buffer[CpuCount..]);
        if (cores is 0 or > MaxCores || cpus is 0 or > MaxCpus || cores * cpus > MaxCores) return null;
        var fahrenheit = buffer[Fahrenheit] != 0;
        var delta = buffer[DeltaToTjMax] != 0;
        double? hottest = null;
        for (var index = 0; index < cores * cpus; index++)
        {
            var raw = BinaryPrimitives.ReadSingleLittleEndian(buffer[(Temperatures + index * sizeof(float))..]);
            if (!float.IsFinite(raw) || raw == -1 || (!delta && raw == 0)) continue;
            // 华氏温差只做比例转换，绝对温度和 TjMax 还需平移。
            var degrees = fahrenheit ? (raw - (delta ? 0 : 32)) / 1.8 : raw;
            if (delta)
            {
                var rawLimit = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(TjMax + index / (int)cores * sizeof(uint))..]);
                var limit = fahrenheit ? (rawLimit - 32d) / 1.8 : rawLimit;
                if (limit is < 40 or > 150 || raw < 0) continue;
                degrees = limit - degrees;
            }
            if (degrees is >= -50 and <= 150 && (hottest is null || degrees > hottest.Value)) hottest = degrees;
        }
        return hottest;
    }
}
