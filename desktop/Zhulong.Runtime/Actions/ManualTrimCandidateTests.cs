using System.Diagnostics;
using Zhulong.Core;

namespace Zhulong.Runtime.Actions;

internal static class ManualTrimCandidateTests
{
    internal static void Check(Action<bool, string> check)
    {
        var activity = new ManualTrimActivity(30);
        var time = DateTime.UtcNow;
        var start = Stopwatch.GetTimestamp();
        var app = new ProcessRow(100, "examplehelper.exe", ManualTrimCandidates.MinimumWorkingSetGb,
            0.125, ReadMbS: 0.05, WriteMbS: 0.05, CreatedTicks: time.AddMinutes(-15).Ticks);
        void Observe(int seconds, ProcessRow process) =>
            activity.Observe([process], 8, time.AddSeconds(seconds).Ticks, start + seconds * Stopwatch.Frequency);
        (bool Ready, IReadOnlyList<ProcessRow> Rows) Read(int seconds) =>
            activity.Read(start + seconds * Stopwatch.Frequency);

        Observe(0, app with { CpuPercent = null, ReadMbS = null, WriteMbS = null });
        Observe(30, app);
        check(!Read(30).Ready && Read(30).Rows.Count == 0, "手动整理需等到第二个有效观察间隔");
        Observe(60, app);
        check(Read(60).Ready && Read(60).Rows.SequenceEqual([app]), "连续低活动且达到工作集门槛应进入候选");
        check(!Read(121).Ready && Read(121).Rows.Count == 0, "过期进程样本不得用于一键整理");

        var reused = app with { CreatedTicks = app.CreatedTicks + TimeSpan.TicksPerSecond };
        Observe(90, reused);
        check(Read(90).Ready && Read(90).Rows.Count == 0, "PID 字面相同但创建时间变化后需要重新观察");
        Observe(120, reused with { WriteMbS = null });
        Observe(150, reused);
        check(!Read(150).Ready && Read(150).Rows.Count == 0, "活动读数缺失时重新积累两段观察");
        Observe(180, reused with { CpuPercent = 0.126 });
        check(Read(180).Ready && Read(180).Rows.Count == 0, "有有效负载样本时应区别无合格候选和样本预热");
        Observe(251, reused);
        check(!Read(251).Ready && Read(251).Rows.Count == 0, "采样中断不能将两段闲置拼接");
        check(ManualTrimCandidates.HasBackgroundRole("example-tray.exe", null) &&
              ManualTrimCandidates.HasBackgroundRole("examplename.exe", "Example Update Agent") &&
              !ManualTrimCandidates.HasBackgroundRole("unknown.exe", "Example Desktop App") &&
              !ManualTrimCandidates.HasBackgroundRole("javaw.exe", "Application Background Helper") &&
              !ManualTrimCandidates.HasBackgroundRole("MuMuHelper.exe", "Example Tray") &&
              !ManualTrimCandidates.HasBackgroundRole("HypHelper.exe", "Example Tray"),
            "无明确后台角色或属于通用运行时、模拟器及显式游戏帮手时不进入扩展候选");
    }
}
