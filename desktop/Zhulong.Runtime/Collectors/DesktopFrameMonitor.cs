using System.Diagnostics;
using System.Runtime.InteropServices;
using Zhulong.Core;

namespace Zhulong.Runtime.Collectors;

internal sealed class DesktopFrameMonitor
{
    internal const string MetricName = "desktop_frame_gap_ms";
    const int CompletedFrame = 2;
    const int MaxFramesPerPoll = 256;
    const uint RecentInputMs = 750;
    readonly object windowGate = new();
    readonly DesktopFrameCadence<TargetId> cadence = new();
    readonly TargetId[] targets = new TargetId[16];
    bool nativeAvailable = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
    ulong lastFrameId;
    bool inputWasActive;
    string state = "unavailable";
    Peak? windowPeak, pendingPeak;

    public void Poll()
    {
        if (!nativeAvailable) return;
        try
        {
            if (DCompositionGetFrameId(CompletedFrame, out var frameId) < 0 || frameId == 0)
            {
                Reset("unavailable");
                return;
            }

            var baseline = lastFrameId;
            lastFrameId = frameId;
            var input = new LastInputInfo { Size = 8 };
            if (!GetLastInputInfo(ref input))
            {
                Reset("unavailable");
                return;
            }
            var active = unchecked(GetTickCount() - input.Tick) <= RecentInputMs;
            var continuousInput = active && inputWasActive;
            inputWasActive = active;
            if (!continuousInput || baseline == 0 || baseline > frameId)
            {
                cadence.Reset();
                Publish(null, active ? "warming" : "idle");
                return;
            }

            var windowStart = frameId - Math.Min(frameId - baseline, MaxFramesPerPoll);
            if (windowStart != baseline) cadence.Reset();
            var utc = DateTimeOffset.UtcNow;
            var qpc = Stopwatch.GetTimestamp();
            Peak? peak = null;
            var sawFrame = false;
            for (var id = windowStart; id < frameId;)
            {
                id++;
                if (DCompositionGetStatistics(id, out _, (uint)targets.Length, targets, out var count) < 0 ||
                    count > targets.Length)
                {
                    cadence.Reset();
                    continue;
                }

                sawFrame = true;
                for (var i = 0; i < count; i++)
                {
                    var target = targets[i];
                    if (DCompositionGetTargetStatistics(id, in target, out var stats) < 0)
                    {
                        cadence.Reset(target);
                        continue;
                    }
                    // presentTime 为零时该目标没有呈现新桌面内容。
                    if (stats.PresentTime == 0) continue;
                    if (stats.Completed.Time == 0 || stats.Completed.Time > (ulong)qpc || stats.VblankDuration == 0)
                    {
                        cadence.Reset(target);
                        continue;
                    }

                    var gap = cadence.Observe(target, (long)stats.Completed.Time, stats.VblankDuration);
                    if (gap is { } interval)
                    {
                        var startedAt = utc - Stopwatch.GetElapsedTime(interval.Start, qpc);
                        peak = Larger(peak, new(Stopwatch.GetElapsedTime(interval.Start, interval.End).TotalMilliseconds, startedAt));
                    }
                }
            }

            Publish(peak, sawFrame || windowStart == frameId ? "idle" : "unavailable");
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            nativeAvailable = false;
            Reset("unavailable");
        }
    }

    public MetricValue BeginSave(DateTimeOffset timestamp)
    {
        lock (windowGate)
        {
            // 存储失败保留该批峰值，保存过程中采到的新数据留在下一批。
            pendingPeak = Larger(pendingPeak, windowPeak);
            windowPeak = null;
            return pendingPeak is { } peak
                ? new(MetricName, peak.Milliseconds, "ms", SampledAt: peak.StartedAt)
                : new(MetricName, null, "ms", State: state, SampledAt: timestamp);
        }
    }

    public void CompleteSave()
    {
        lock (windowGate) pendingPeak = null;
    }

    void Reset(string nextState)
    {
        lastFrameId = 0;
        inputWasActive = false;
        cadence.Reset();
        Publish(null, nextState);
    }

    void Publish(Peak? peak, string nextState)
    {
        lock (windowGate)
        {
            windowPeak = Larger(windowPeak, peak);
            state = nextState;
        }
    }

    static Peak? Larger(Peak? left, Peak? right) =>
        right is { } candidate && (left is null || candidate.Milliseconds > left.Value.Milliseconds) ? right : left;

    readonly record struct Peak(double Milliseconds, DateTimeOffset StartedAt);

    [StructLayout(LayoutKind.Sequential)]
    readonly record struct TargetId(uint DisplayLow, int DisplayHigh, uint RenderLow, int RenderHigh,
        uint Source, uint Target, uint Unique);

    [StructLayout(LayoutKind.Sequential)]
    struct FrameStats
    {
        public ulong StartTime, TargetTime, FramePeriod;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CompositionStats
    {
        public uint PresentCount, RefreshCount, VirtualRefreshCount;
        public ulong Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TargetStats
    {
        public uint OutstandingPresents;
        public ulong PresentTime, VblankDuration;
        public CompositionStats Presented, Completed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo
    {
        public uint Size, Tick;
    }

    [DllImport("dcomp.dll")]
    static extern int DCompositionGetFrameId(int frameIdType, out ulong frameId);

    [DllImport("dcomp.dll")]
    static extern int DCompositionGetStatistics(ulong frameId, out FrameStats frameStats, uint targetIdCount,
        [Out] TargetId[] targetIds, out uint actualTargetIdCount);

    [DllImport("dcomp.dll")]
    static extern int DCompositionGetTargetStatistics(ulong frameId, in TargetId targetId, out TargetStats targetStats);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("kernel32.dll")]
    static extern uint GetTickCount();
}

internal sealed class DesktopFrameCadence<T> where T : notnull
{
    const int ActiveIntervals = 2;
    readonly Dictionary<T, (long Time, int Streak, (long Start, long End)? Pending)> previous = [];

    public (long Start, long End)? Observe(T target, long time, ulong vblank)
    {
        if (!previous.TryGetValue(target, out var last) || time < last.Time)
        {
            previous[target] = (time, 0, null);
            return null;
        }
        if (time == last.Time) return null;

        // 连续两个短间隔建立活动状态，高刷屏上的 30/60 FPS 应用也可纳入。
        var shortInterval = Math.Max(vblank * 2d, Stopwatch.Frequency / 30d);
        if (time - last.Time > shortInterval)
        {
            previous[target] = (time, 0, last.Streak == ActiveIntervals ? (last.Time, time) : null);
            return null;
        }

        // 长间隔需等连续呈现恢复，孤立的桌面重绘不会发布峰值。
        var streak = Math.Min(ActiveIntervals, last.Streak + 1);
        var confirmed = streak == ActiveIntervals ? last.Pending : null;
        previous[target] = (time, streak, confirmed is null ? last.Pending : null);
        return confirmed ?? (last.Streak == ActiveIntervals ? (last.Time, time) : null);
    }

    public void Reset(T target) => previous.Remove(target);
    public void Reset() => previous.Clear();
}
