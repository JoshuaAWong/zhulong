using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Zhulong.Desktop;

public sealed record WatchdogIdentity(int Pid, long CreatedTicks, string Executable, string UserSid, int SessionId);
public sealed record WatchdogRecoveryResult(string Status, bool Recovered = false, int? Pid = null, string? Detail = null);
public interface IWatchdogTarget : IDisposable
{
    WatchdogIdentity Identity { get; }
    bool TerminateAndWait(WatchdogIdentity expected, TimeSpan timeout, Func<bool> mayTerminate);
}
public interface IWatchdogBoundary
{
    int CurrentPid { get; }
    string CurrentExecutable { get; }
    string CurrentUserSid { get; }
    int CurrentSessionId { get; }
    IWatchdogTarget? Open(int pid);
}

// 只恢复同一发布目录、当前用户/会话的新版实例，不枚举或处理旧Python进程。
public static class WatchdogRecovery
{
    sealed record Heartbeat(int Pid, long CreatedTicks, DateTimeOffset Timestamp, string ManagedExecutable, int? SampleSeconds = null);
    sealed record RecoveryAttempt(DateTimeOffset AttemptedAt, int Pid, string Executable, string Status, DateTimeOffset? FinishedAt = null);
    public static WatchdogRecoveryResult? LastResult { get; private set; }
    public static bool TryRecover(string directory, string expectedExe)
    {
        var result = Recover(directory, expectedExe);
        LastResult = result;
        return result.Recovered;
    }

    public static bool TryReserveStartup(string directory, string expectedExe)
    {
        var result = ReserveStartup(directory, expectedExe);
        LastResult = result;
        return result.Recovered;
    }

    // 仅用于--watchdog且mutex初始不存在的分支；手动/登录启动不调用此入口。
    // Recovered=true在这里表示启动预留已获准，Status明确区别于已终止旧实例。
    public static WatchdogRecoveryResult ReserveStartup(string directory, string expectedExe,
        IWatchdogBoundary? boundary = null, DateTimeOffset? now = null)
    {
        try
        {
            var intent = Path.Combine(directory, "intentional-exit");
            if (File.Exists(intent)) return new("skipped:intentional_exit");
            boundary ??= new NativeWatchdogBoundary();
            var image = Path.GetFullPath(expectedExe);
            var rejection = ExecutableRejection(image, boundary);
            if (rejection is not null) return new(rejection);
            if (boundary.CurrentPid <= 0 || string.IsNullOrWhiteSpace(boundary.CurrentUserSid) || boundary.CurrentSessionId < 0)
                return new("blocked:current_identity_unavailable");
            Directory.CreateDirectory(directory);
            var moment = now ?? DateTimeOffset.UtcNow;
            using var ledger = ReserveAttempt(directory, boundary.CurrentPid, image, moment, out var denied);
            if (ledger is null) return denied!;
            var allowed = !File.Exists(intent);
            var status = allowed ? "reserved:watchdog_startup" : "skipped:intentional_exit";
            WriteAttempt(ledger, new(moment, boundary.CurrentPid, image, status));
            return new(status, allowed, boundary.CurrentPid);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or Win32Exception or System.Security.SecurityException)
        { return new("degraded:startup_accounting_unavailable", Detail: error.Message); }
    }

    public static WatchdogRecoveryResult Recover(string directory, string expectedExe,
        IWatchdogBoundary? boundary = null, DateTimeOffset? now = null)
    {
        try
        {
            if (File.Exists(Path.Combine(directory, "intentional-exit"))) return new("skipped:intentional_exit");
            var path = Path.Combine(directory, "heartbeat.json");
            if (!File.Exists(path)) return new("skipped:heartbeat_missing");
            if (new FileInfo(path).Length > 16384) return new("blocked:invalid_heartbeat");
            var beat = JsonSerializer.Deserialize<Heartbeat>(File.ReadAllText(path, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (beat is null || beat.Pid <= 0 || beat.CreatedTicks <= 0 || beat.Timestamp == default || string.IsNullOrWhiteSpace(beat.ManagedExecutable))
                return new("blocked:invalid_heartbeat");
            var moment = now ?? DateTimeOffset.UtcNow;
            var interval = beat.SampleSeconds ?? 30;
            if (interval is < 5 or > 300) return new("blocked:invalid_sample_interval", Pid: beat.Pid);
            var threshold = Math.Max(120, 4 * interval);
            var age = (moment - beat.Timestamp).TotalSeconds;
            if (age < 0) return new("blocked:future_heartbeat", Pid: beat.Pid);
            if (age <= threshold) return new("skipped:heartbeat_fresh", Pid: beat.Pid);
            boundary ??= new NativeWatchdogBoundary();
            var image = Path.GetFullPath(expectedExe);
            var rejection = ExecutableRejection(image, boundary);
            if (rejection is not null) return new(rejection, Pid: beat.Pid);
            if (!SamePath(image, beat.ManagedExecutable)) return new("blocked:heartbeat_path_mismatch", Pid: beat.Pid);
            if (beat.Pid == boundary.CurrentPid) return new("blocked:self", Pid: beat.Pid);
            using var target = boundary.Open(beat.Pid);
            if (target is null) return new("skipped:process_gone", Pid: beat.Pid);
            var identity = target.Identity;
            if (identity.Pid != beat.Pid || identity.CreatedTicks != beat.CreatedTicks)
                return new("blocked:pid_reused", Pid: beat.Pid);
            if (!SamePath(identity.Executable, image)) return new("blocked:process_path_mismatch", Pid: beat.Pid);
            if (identity.UserSid != boundary.CurrentUserSid) return new("blocked:other_user", Pid: beat.Pid);
            if (identity.SessionId != boundary.CurrentSessionId) return new("blocked:other_session", Pid: beat.Pid);
            // 固定句柄上最后再读一次，替身验收也能触发竞态拒绝。
            if (target.Identity != identity) return new("blocked:identity_changed", Pid: beat.Pid);
            var intent = Path.Combine(directory, "intentional-exit");
            if (File.Exists(intent)) return new("skipped:intentional_exit", Pid: beat.Pid);
            using var ledger = ReserveAttempt(directory, beat.Pid, image, moment, out var denied);
            if (ledger is null) return denied!;
            bool recovered;
            try
            {
                recovered = target.TerminateAndWait(identity, TimeSpan.FromSeconds(8), () => !File.Exists(intent));
            }
            catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
            {
                try { WriteAttempt(ledger, new(moment, beat.Pid, image, "failed:termination", DateTimeOffset.UtcNow)); }
                catch (Exception accounting) when (accounting is IOException or UnauthorizedAccessException)
                { return new("degraded:recovery_accounting_failed", Pid: beat.Pid, Detail: error.Message + "; " + accounting.Message); }
                return new("degraded:termination_failed", Pid: beat.Pid, Detail: error.Message);
            }
            var status = recovered ? "recovered:stale_instance_terminated" :
                File.Exists(intent) ? "skipped:intentional_exit" : "degraded:termination_unconfirmed";
            try { WriteAttempt(ledger, new(moment, beat.Pid, image, status, DateTimeOffset.UtcNow)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return new("degraded:recovery_accounting_failed", recovered, beat.Pid, error.Message); }
            return new(status, recovered, beat.Pid);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or Win32Exception or System.Security.SecurityException)
        { return new("degraded:identity_unavailable", Detail: error.Message); }
    }

    static FileStream? ReserveAttempt(string directory, int pid, string executable, DateTimeOffset now,
        out WatchdogRecoveryResult? denied)
    {
        denied = null;
        FileStream? stream = null;
        var path = Path.Combine(directory, "watchdog-attempt.json");
        var existed = File.Exists(path);
        try
        {
            // 独占文件锁覆盖判断、预记账和实际恢复，阻断并发watchdog双放行。
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (stream.Length > 16384 || (existed && stream.Length == 0)) throw new JsonException("恢复账本为空或过大。");
            if (stream.Length > 0)
            {
                var previous = JsonSerializer.Deserialize<RecoveryAttempt>(stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (previous is null || previous.AttemptedAt == default || previous.Pid <= 0 ||
                    string.IsNullOrWhiteSpace(previous.Executable) || string.IsNullOrWhiteSpace(previous.Status))
                    throw new JsonException("恢复账本缺少字段。");
                var elapsed = (now - previous.AttemptedAt).TotalSeconds;
                if (elapsed < 600)
                {
                    denied = new(elapsed < 0 ? "blocked:future_recovery_attempt" : "skipped:recovery_cooldown", Pid: pid);
                    stream.Dispose();
                    return null;
                }
            }
            // 无法可靠写入这次尝试时，不能先终止再补记账。
            WriteAttempt(stream, new(now, pid, executable, "attempted"));
            return stream;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            stream?.Dispose();
            denied = new("degraded:recovery_accounting_unavailable", Pid: pid, Detail: error.Message);
            return null;
        }
    }

    static void WriteAttempt(FileStream stream, RecoveryAttempt attempt)
    {
        stream.Position = 0;
        stream.SetLength(0);
        JsonSerializer.Serialize(stream, attempt);
        stream.Flush(flushToDisk: true);
    }

    static string? ExecutableRejection(string image, IWatchdogBoundary boundary)
    {
        if (!SamePath(image, boundary.CurrentExecutable)) return "blocked:other_installation";
        if (!Path.GetExtension(image).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(image).ToLowerInvariant() is "dotnet" or "python" or "pythonw" or "cmd" or "powershell" or "pwsh")
            return "blocked:non_published_executable";
        return null;
    }

    static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    public static int RunSafetyTests(string outputDirectory)
    {
        var directory = Path.Combine(Path.GetFullPath(outputDirectory), "watchdog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var assertions = 0;
        var now = DateTimeOffset.UtcNow;
        var identity = new WatchdogIdentity(321321, now.AddMinutes(-10).UtcTicks, "C:/new-runtime/Zhulong.exe", "S-1-5-21-fixture", 1);
        void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
        void Beat(int? pid = null, long? created = null, DateTimeOffset? timestamp = null, string? executable = null) =>
            File.WriteAllText(Path.Combine(directory, "heartbeat.json"), JsonSerializer.Serialize(new
            { pid = pid ?? identity.Pid, createdTicks = created ?? identity.CreatedTicks, timestamp = timestamp ?? now.AddSeconds(-121), managedExecutable = executable ?? identity.Executable }), Encoding.UTF8);
        WatchdogRecoveryResult Run(FakeBoundary boundary) => Recover(directory, boundary.CurrentExecutable, boundary, now);
        var fake = new FakeBoundary(identity);
        Check(Run(fake).Status == "skipped:heartbeat_missing" && fake.Opens == 0, "缺少心跳不终止任何进程");
        Beat(timestamp: now.AddSeconds(-120));
        Check(Run(fake).Status == "skipped:heartbeat_fresh" && fake.Opens == 0, "120秒以内保持原实例");
        Beat(timestamp: now.AddSeconds(1)); Check(Run(fake).Status == "blocked:future_heartbeat", "未来时间不猜测失活");
        Beat(pid: fake.CurrentPid); Check(Run(fake).Status == "blocked:self" && fake.Opens == 0, "不能终止当前PID");
        Beat(created: identity.CreatedTicks + 1); Check(Run(fake).Status == "blocked:pid_reused" && fake.Target.Terminations == 0, "创建时间不符拒绝");
        Beat(executable: "C:/old-runtime/Zhulong.exe"); Check(Run(fake).Status == "blocked:heartbeat_path_mismatch", "另一安装目录拒绝");
        Beat(); fake.Target.Value = identity with { UserSid = "other-user" }; Check(Run(fake).Status == "blocked:other_user", "另一用户拒绝");
        fake.Target.Value = identity with { SessionId = 2 }; Check(Run(fake).Status == "blocked:other_session", "另一会话拒绝");
        fake.Target.Value = identity with { Executable = "C:/Python/pythonw.exe" }; Check(Run(fake).Status == "blocked:process_path_mismatch", "旧Python进程拒绝");
        fake.Target.Value = identity; fake.Target.ChangeOnSecondRead = true; fake.Target.Reads = 0;
        Check(Run(fake).Status == "blocked:identity_changed", "最终复核变化时拒绝");
        fake.Target.ChangeOnSecondRead = false; fake.Target.Reads = 0;
        File.WriteAllText(Path.Combine(directory, "intentional-exit"), "user exit", Encoding.UTF8);
        var opens = fake.Opens; Check(Run(fake).Status == "skipped:intentional_exit" && fake.Opens == opens, "正常退出意图优先于恢复");
        File.Delete(Path.Combine(directory, "intentional-exit"));
        File.WriteAllText(Path.Combine(directory, "heartbeat.json"), "{\"pid\":321321}", Encoding.UTF8);
        Check(Run(fake).Status == "blocked:invalid_heartbeat", "缺字段fail-closed");
        Beat(); Check(Run(fake).Recovered && fake.Target.Terminations == 1, "身份完整匹配后仅终止替身目标一次");
        var count = fake.Target.Terminations;
        Check(Run(fake).Status == "skipped:recovery_cooldown" && fake.Target.Terminations == count,
            "成功恢复后十分钟内不能再次终止");
        Check(Recover(directory, fake.CurrentExecutable, fake, now.AddSeconds(601)).Recovered && fake.Target.Terminations == count + 1,
            "冷却到期后才可再次尝试");
        count = fake.Target.Terminations;
        Check(Run(fake).Status == "blocked:future_recovery_attempt" && fake.Target.Terminations == count,
            "恢复记录时间未来时不猜测额度");
        string Case(string name, int sample = 30, int age = 121)
        {
            var location = Path.Combine(directory, name);
            Directory.CreateDirectory(location);
            File.WriteAllText(Path.Combine(location, "heartbeat.json"), JsonSerializer.Serialize(new
            { pid = identity.Pid, createdTicks = identity.CreatedTicks, timestamp = now.AddSeconds(-age), managedExecutable = identity.Executable, sampleSeconds = sample }), Encoding.UTF8);
            return location;
        }
        var slow = Case("sample-300", 300, 1199);
        var opensBefore = fake.Opens;
        Check(Recover(slow, fake.CurrentExecutable, fake, now).Status == "skipped:heartbeat_fresh" && fake.Opens == opensBefore,
            "300秒采样应允许至少1200秒心跳窗口");
        var slowExpired = Case("sample-300-expired", 300, 1201);
        Check(Recover(slowExpired, fake.CurrentExecutable, fake, now).Recovered, "四倍采样周期超过后才进入恢复");
        Check(Recover(Case("sample-invalid-low", 0), fake.CurrentExecutable, fake, now).Status == "blocked:invalid_sample_interval",
            "采样间隔低于5拒绝");
        Check(Recover(Case("sample-invalid-high", 301), fake.CurrentExecutable, fake, now).Status == "blocked:invalid_sample_interval",
            "采样间隔高于300拒绝");
        var corrupt = Case("corrupt-accounting");
        File.WriteAllText(Path.Combine(corrupt, "watchdog-attempt.json"), "{", Encoding.UTF8);
        count = fake.Target.Terminations;
        Check(Recover(corrupt, fake.CurrentExecutable, fake, now).Status == "degraded:recovery_accounting_unavailable" && fake.Target.Terminations == count,
            "损坏恢复账本不得终止");
        var denied = Case("denied-accounting");
        Directory.CreateDirectory(Path.Combine(denied, "watchdog-attempt.json"));
        Check(Recover(denied, fake.CurrentExecutable, fake, now).Status == "degraded:recovery_accounting_unavailable" && fake.Target.Terminations == count,
            "无法预记账不得终止");
        var locked = Case("locked-accounting");
        using (var exclusive = new FileStream(Path.Combine(locked, "watchdog-attempt.json"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            Check(Recover(locked, fake.CurrentExecutable, fake, now).Status == "degraded:recovery_accounting_unavailable" && fake.Target.Terminations == count,
                "并发恢复不能同时取得账本锁");
        var concurrentExit = Case("intent-before-terminate");
        fake.Target.BeforeTerminate = () => File.WriteAllText(Path.Combine(concurrentExit, "intentional-exit"), "user exit", Encoding.UTF8);
        Check(Recover(concurrentExit, fake.CurrentExecutable, fake, now).Status == "skipped:intentional_exit" && fake.Target.Terminations == count,
            "最终终止边界必须复核刚写入的正常退出意图");
        fake.Target.BeforeTerminate = null;
        var cold = Path.Combine(directory, "cold-start");
        var coldBoundary = new FakeBoundary(identity);
        var startup = ReserveStartup(cold, identity.Executable, coldBoundary, now);
        Check(startup.Recovered && startup.Status == "reserved:watchdog_startup" &&
            File.Exists(Path.Combine(cold, "watchdog-attempt.json")) && !File.Exists(Path.Combine(cold, "heartbeat.json")),
            "首次无心跳冷启动先持久化预留，不伪造心跳");
        Check(ReserveStartup(cold, identity.Executable, coldBoundary, now).Status == "skipped:recovery_cooldown",
            "同一账本约束连续冷启动");
        var staleAtCold = JsonSerializer.Serialize(new { pid = identity.Pid, createdTicks = identity.CreatedTicks,
            timestamp = now.AddSeconds(-121), managedExecutable = identity.Executable });
        File.WriteAllText(Path.Combine(cold, "heartbeat.json"), staleAtCold, Encoding.UTF8);
        Check(Recover(cold, identity.Executable, coldBoundary, now).Status == "skipped:recovery_cooldown" && coldBoundary.Target.Terminations == 0,
            "冷启动与失活恢复共享频次，不各放行一次");
        var coldIntent = Path.Combine(directory, "cold-intent");
        Directory.CreateDirectory(coldIntent);
        File.WriteAllText(Path.Combine(coldIntent, "intentional-exit"), "user exit", Encoding.UTF8);
        Check(ReserveStartup(coldIntent, identity.Executable, coldBoundary, now).Status == "skipped:intentional_exit" &&
            !File.Exists(Path.Combine(coldIntent, "watchdog-attempt.json")), "冷启动尊重正常退出意图");
        Check(ReserveStartup(Path.Combine(directory, "cold-other-install"), "C:/other/Zhulong.exe", coldBoundary, now).Status == "blocked:other_installation",
            "冷启动不能以另一安装路径获得预留");
        var dotnet = new FakeBoundary(identity with { Executable = "C:/sdk/dotnet.exe" });
        Check(ReserveStartup(Path.Combine(directory, "cold-dotnet"), dotnet.CurrentExecutable, dotnet, now).Status == "blocked:non_published_executable",
            "源码dotnet宿主不能走发布程序恢复");
        var python = new FakeBoundary(identity with { Executable = "C:/Python/pythonw.exe" });
        Check(ReserveStartup(Path.Combine(directory, "cold-python"), python.CurrentExecutable, python, now).Status == "blocked:non_published_executable",
            "旧Python宿主不能走新版预留");
        var coldCorrupt = Path.Combine(directory, "cold-corrupt");
        Directory.CreateDirectory(coldCorrupt);
        File.WriteAllText(Path.Combine(coldCorrupt, "watchdog-attempt.json"), "{", Encoding.UTF8);
        Check(!ReserveStartup(coldCorrupt, identity.Executable, coldBoundary, now).Recovered, "损坏账本拒绝冷启动");
        var coldDenied = Path.Combine(directory, "cold-denied");
        Directory.CreateDirectory(Path.Combine(coldDenied, "watchdog-attempt.json"));
        Check(!ReserveStartup(coldDenied, identity.Executable, coldBoundary, now).Recovered, "不能预记账时拒绝冷启动");
        var coldConcurrent = Path.Combine(directory, "cold-concurrent-intent");
        Directory.CreateDirectory(coldConcurrent);
        coldBoundary.BeforeExecutableRead = () => File.WriteAllText(Path.Combine(coldConcurrent, "intentional-exit"), "user exit", Encoding.UTF8);
        Check(ReserveStartup(coldConcurrent, identity.Executable, coldBoundary, now).Status == "skipped:intentional_exit",
            "冷启动预留完成前再次复核退出意图");
        coldBoundary.BeforeExecutableRead = null;
        Check(coldBoundary.Target.Terminations == 0, "冷启动预留不终止任何进程");
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { status = "PASS", assertions, real_processes_terminated = false }), Encoding.UTF8);
        return assertions;
    }

    sealed class FakeBoundary(WatchdogIdentity identity) : IWatchdogBoundary
    {
        public FakeTarget Target = new(identity); public int Opens;
        public int CurrentPid => 999999;
        public Action? BeforeExecutableRead;
        public string CurrentExecutable { get { BeforeExecutableRead?.Invoke(); return identity.Executable; } }
        public string CurrentUserSid => identity.UserSid;
        public int CurrentSessionId => identity.SessionId;
        public IWatchdogTarget? Open(int pid) { Opens++; return Target; }
    }
    sealed class FakeTarget(WatchdogIdentity identity) : IWatchdogTarget
    {
        public WatchdogIdentity Value = identity; public int Terminations, Reads; public bool ChangeOnSecondRead;
        public WatchdogIdentity Identity => ChangeOnSecondRead && ++Reads >= 2 ? Value with { CreatedTicks = Value.CreatedTicks + 1 } : Value;
        public Action? BeforeTerminate;
        public bool TerminateAndWait(WatchdogIdentity expected, TimeSpan timeout, Func<bool> mayTerminate)
        { BeforeTerminate?.Invoke(); if (!mayTerminate()) return false; Terminations++; return true; }
        public void Dispose() { }
    }

    sealed class NativeWatchdogBoundary : IWatchdogBoundary
    {
        public int CurrentPid => Environment.ProcessId;
        public string CurrentExecutable => Environment.ProcessPath ?? "";
        public string CurrentUserSid { get; }
        public int CurrentSessionId { get; }
        public NativeWatchdogBoundary()
        {
            using var user = WindowsIdentity.GetCurrent();
            CurrentUserSid = user.User?.Value ?? "";
            using var process = Process.GetCurrentProcess();
            CurrentSessionId = process.SessionId;
        }
        public IWatchdogTarget? Open(int pid)
        {
            var handle = OpenProcess(0x1000 | 0x100000 | 1, false, pid);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error(); handle.Dispose();
                if (error is 87 or 1168) return null;
                throw new Win32Exception(error);
            }
            return new NativeTarget(handle, pid);
        }
    }
    sealed class NativeTarget(SafeProcessHandle handle, int pid) : IWatchdogTarget
    {
        public WatchdogIdentity Identity => ReadIdentity(handle, pid);
        public bool TerminateAndWait(WatchdogIdentity expected, TimeSpan timeout, Func<bool> mayTerminate)
        {
            if (ReadIdentity(handle, pid) != expected) return false;
            if (!GetExitCodeProcess(handle, out var exitCode) || exitCode != 259) return false;
            if (!mayTerminate()) return false;
            if (!TerminateProcess(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return WaitForSingleObject(handle, (uint)timeout.TotalMilliseconds) == 0;
        }
        public void Dispose() => handle.Dispose();
    }
    static WatchdogIdentity ReadIdentity(SafeProcessHandle handle, int pid)
    {
        if (!GetProcessTimes(handle, out var created, out _, out _, out _) || !ProcessIdToSessionId(pid, out var session))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = new StringBuilder(32768); uint size = (uint)path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref size) || !OpenProcessToken(handle, 8, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        string sid;
        try { using var user = new WindowsIdentity(token); sid = user.User?.Value ?? throw new System.Security.SecurityException("用户SID不可读取。"); }
        finally { CloseHandle(token); }
        return new(pid, DateTime.FromFileTimeUtc(created.Value).Ticks, path.ToString(), sid, (int)session);
    }
    [StructLayout(LayoutKind.Sequential)] struct FileTime { public uint Low, High; public readonly long Value => ((long)High << 32) | Low; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ProcessIdToSessionId(int pid, out uint session);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(SafeProcessHandle process, uint timeout);
}
