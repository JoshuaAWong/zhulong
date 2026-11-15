using Zhulong.Core;

namespace Zhulong.Runtime.Collectors;

// 在完整采样已读取的私有提交计数上建立进程实例的短历史，不单独枚举进程。
internal sealed class CommitGrowthTracker(int sampleSeconds)
{
    const int MaxCheckpoints = 64, MaximumProcesses = 4096, ResultLimit = 10;
    const double SlowWindowSeconds = 3600, SlowBinSeconds = 8 * 60, MaxLargestStepShare = .70;
    const double FastGrowthGb = 64d / 1024, FastStepGb = 6d / 1024;
    const double SlowGrowthGb = 256d / 1024, SlowStepGb = 32d / 1024;
    readonly double fastWindowSeconds = Math.Max(600, sampleSeconds * 3d);
    readonly double checkpointSeconds = Math.Max(60, sampleSeconds) - Math.Min(2, sampleSeconds * .2);
    readonly Dictionary<int, ProcessHistory> histories = [];
    double lastSample = double.NaN, lastCheckpoint = double.NaN;
    long sequence;

    internal CommitGrowthReport Observe(IReadOnlyList<ProcessRow> processes, double now)
    {
        if (!(now > lastSample) || now - lastSample > sampleSeconds * 1.75)
        {
            histories.Clear();
            lastCheckpoint = double.NaN;
        }
        lastSample = now;
        var checkpoint = !(now - lastCheckpoint < checkpointSeconds);
        if (checkpoint) lastCheckpoint = now;
        ++sequence;
        var observed = false;
        var fullWindow = false;
        List<CommitGrowthRow>? matches = null;
        foreach (var row in processes)
        {
            if (row.CreatedTicks is not > 0 || row.CommitGb is not double commit || !double.IsFinite(commit) || commit < 0) continue;
            observed = true;
            if (!histories.TryGetValue(row.Pid, out var history) && histories.Count >= MaximumProcesses) continue;
            if (history is null || history.CreatedTicks != row.CreatedTicks.Value ||
                history.LastSequence != sequence - 1)
            {
                history = new ProcessHistory(row.CreatedTicks.Value);
                histories[row.Pid] = history;
            }
            history.LastSequence = sequence;
            var points = history.Points;
            if (checkpoint || points.Count == 0)
            {
                points.Add(new Checkpoint(now, commit));
                while (points.Count > MaxCheckpoints || points.Count > 1 && points[1].Time <= now - SlowWindowSeconds)
                    points.RemoveAt(0);
            }
            if (points.Count < 4 || points[^1].Time - points[0].Time < fastWindowSeconds) continue;
            fullWindow = true;
            var fast = Measure(points, commit, fastWindowSeconds, FastGrowthGb, FastStepGb, 0);
            var slow = Measure(points, commit, SlowWindowSeconds, SlowGrowthGb, SlowStepGb, SlowBinSeconds);
            var evidence = slow is { } longer && (fast is null || longer.Growth > fast.Value.Growth) ? slow : fast;
            if (evidence is { } match)
                (matches ??= []).Add(new(row.Pid, row.Name, history.CreatedTicks, commit, match.Growth,
                    (now - match.BaselineTime) / 60, match.Increasing));
        }
        if (checkpoint) foreach (var entry in histories)
            if (entry.Value.LastSequence != sequence) histories.Remove(entry.Key);
        return new(fullWindow ? "ready" : observed ? "warming" : "unavailable", fastWindowSeconds / 60,
            matches is null ? [] : matches.OrderByDescending(x => x.GrowthGb).ThenBy(x => x.Pid).Take(ResultLimit).ToArray());
    }

    static Evidence? Measure(List<Checkpoint> points, double currentCommit, double window,
        double minimumGrowth, double minimumStep, double binSeconds)
    {
        var latest = points[^1];
        var cutoff = latest.Time - window;
        if (points[0].Time > cutoff) return null;
        var start = points.Count - 2;
        while (points[start].Time > cutoff) start--;
        var baseline = points[start];
        var net = latest.CommitGb - baseline.CommitGb;
        var growth = currentCommit - baseline.CommitGb;
        if (net < minimumGrowth || growth < minimumGrowth) return null;

        var rising = 0;
        double largest = 0, largestCheckpoint = 0;
        var prior = baseline;
        for (var i = start + 1; i < points.Count; i++)
        {
            var point = points[i];
            largestCheckpoint = Math.Max(largestCheckpoint, point.CommitGb - points[i - 1].CommitGb);
            if (point.Time - prior.Time < binSeconds) continue;
            var increment = point.CommitGb - prior.CommitGb;
            if (increment >= minimumStep) rising++;
            largest = Math.Max(largest, increment);
            prior = point;
        }
        if (rising < 3 || Math.Max(largest, largestCheckpoint) > net * MaxLargestStepShare) return null;
        return new(baseline.Time, growth, rising);
    }

    readonly record struct Checkpoint(double Time, double CommitGb);
    readonly record struct Evidence(double BaselineTime, double Growth, int Increasing);
    sealed class ProcessHistory(long createdTicks)
    {
        internal long CreatedTicks { get; } = createdTicks;
        internal long LastSequence;
        internal List<Checkpoint> Points { get; } = [];
    }
}
