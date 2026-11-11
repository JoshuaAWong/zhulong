using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace Zhulong.Desktop;

internal static class StartupTasks
{
    internal enum Kind { Logon, Watchdog }

    static readonly SemaphoreSlim operation = new(1, 1);
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    static readonly IRunner runner = new ProcessRunner();
    static string CurrentSid {get{using var user=WindowsIdentity.GetCurrent();return user.User!.Value;}}
    static string Prefix => "ZhulongNext-" + CurrentSid;
    public static async Task<bool> IsEnabledAsync(Kind kind)
    {
        if (!await operation.WaitAsync(0).ConfigureAwait(false)) throw new IOException("自启设置正在处理中，请稍后再试。");
        try
        {
            var xml = await ReadTaskAsync(runner, Prefix + "-" + kind).ConfigureAwait(false);
            EnsureOwned(xml, Environment.ProcessPath!);
            return xml is not null && TaskIsEnabled(XDocument.Parse(xml), kind);
        }
        finally { operation.Release(); }
    }

    public static async Task SetEnabledAsync(Kind kind, bool enabled, string directory)
    {
        if (!await operation.WaitAsync(0).ConfigureAwait(false)) throw new IOException("自启设置正在处理中，请稍后再试。");
        try
        {
            await ChangeAsync(enabled, Path.GetFullPath(directory), runner, Prefix,
                sid: CurrentSid, executable: Environment.ProcessPath!, workingDirectory: AppContext.BaseDirectory,
                selected: kind).ConfigureAwait(false);
        }
        finally { operation.Release(); }
    }

    public static async Task RemoveOwnedAsync(string directory)
    {
        if (!await operation.WaitAsync(0).ConfigureAwait(false)) throw new IOException("自启设置正在处理中，请稍后再试。");
        try
        {
            await ChangeAsync(false, Path.GetFullPath(directory), runner, Prefix,
                sid: CurrentSid, executable: Environment.ProcessPath!, workingDirectory: AppContext.BaseDirectory,
                removeOwnedExecutable: Environment.ProcessPath!).ConfigureAwait(false);
        }
        finally { operation.Release(); }
    }
    public static async Task WaitForIdleAsync()
    { await operation.WaitAsync().ConfigureAwait(false); operation.Release(); }

    static bool OwnsCommand(string xml, string executable)
    {
        var commands = XDocument.Parse(xml).Descendants(Ns + "Command").ToArray();
        return commands.Length == 1 && string.Equals(Path.GetFullPath(commands[0].Value), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
    }
    static void EnsureOwned(string? xml, string executable)
    {
        if (xml is not null && !OwnsCommand(xml, executable))
            throw new IOException("该任务属于另一安装目录，请在对应程序中修改。");
    }
    static bool TaskIsEnabled(XDocument document, Kind kind)
    {
        var task = document.Root;
        var trigger = task?.Element(Ns + "Triggers")?.Element(Ns + (kind == Kind.Logon ? "LogonTrigger" : "TimeTrigger"));
        return (bool?)task?.Element(Ns + "Settings")?.Element(Ns + "Enabled") != false &&
            trigger is not null && (bool?)trigger.Element(Ns + "Enabled") != false;
    }
    static async Task ChangeAsync(bool enabled, string directory, IRunner commands, string prefix, string sid, string executable, string workingDirectory,
        string? removeOwnedExecutable = null, Kind? selected = null)
    {
        Dictionary<string, string?> original;
        if (selected is { } selection)
        {
            var name = prefix + "-" + selection;
            var xml = await ReadTaskAsync(commands, name).ConfigureAwait(false);
            EnsureOwned(xml, executable);
            original = new(StringComparer.OrdinalIgnoreCase) { [name] = xml };
        }
        else original = await ReadPairAsync(commands, prefix).ConfigureAwait(false);
        if (removeOwnedExecutable is not null) original = original.Where(pair => pair.Value is not null && OwnsCommand(pair.Value, removeOwnedExecutable)).ToDictionary(pair => pair.Key, pair => pair.Value);
        try
        {
            foreach (var kind in new[] { Kind.Logon, Kind.Watchdog })
            {
                var name = prefix + "-" + kind;
                if (!original.ContainsKey(name)) continue;
                if (!enabled)
                {
                    if (original[name] is null) continue;
                    Ensure(await commands.RunAsync(["/Delete", "/TN", name, "/F"]).ConfigureAwait(false), "删除", name);
                    if (await ReadTaskAsync(commands, name).ConfigureAwait(false) is not null) throw new IOException("删除后回读仍存在：" + name);
                    continue;
                }
                var arguments = BuildArguments(directory, kind == Kind.Logon ? "--background" : "--watchdog");
                await CreateAsync(commands, name, Definition(kind.ToString(), sid, executable, arguments, workingDirectory)).ConfigureAwait(false);
                var readback = await ReadTaskAsync(commands, name).ConfigureAwait(false) ?? throw new IOException("创建后回读不存在：" + name);
                var document = XDocument.Parse(readback);
                var principal = document.Descendants(Ns + "Principal").Single();
                // Windows 回读可能省略默认运行级别，登录触发器的用户也可能写成帐户名。
                if (document.Descendants(Ns + "Command").Single().Value != executable ||
                    document.Descendants(Ns + "Arguments").Single().Value != arguments ||
                    document.Descendants(Ns + "WorkingDirectory").Single().Value != workingDirectory ||
                    (string?)principal.Element(Ns + "RunLevel") is not (null or "LeastPrivilege") ||
                    (string?)principal.Element(Ns + "UserId") != sid ||
                    (string?)principal.Element(Ns + "LogonType") != "InteractiveToken" || !TaskIsEnabled(document, kind))
                    throw new IOException("自启任务回读不一致：" + name);
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            var failures = new List<Exception>();
            foreach (var (name, xml) in original)
            {
                try
                {
                    if (xml is not null) await CreateAsync(commands, name, xml).ConfigureAwait(false);
                    else await commands.RunAsync(["/Delete", "/TN", name, "/F"]).ConfigureAwait(false);
                    var restored = await ReadTaskAsync(commands, name).ConfigureAwait(false);
                    if (xml is null ? restored is not null : restored is null || !XNode.DeepEquals(XDocument.Parse(xml).Root, XDocument.Parse(restored).Root))
                        throw new IOException("补偿回读不一致：" + name);
                }
                catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException)) { failures.Add(failure); }
            }
            if (failures.Count > 0) throw new IOException("自启修改失败，部分任务未能恢复，请检查新版任务状态。", new AggregateException(new[] { error }.Concat(failures)));
            throw new IOException("自启修改未完成，受影响的任务已恢复原状态。", error);
        }
    }

    static string Definition(string kind, string sid, string executable, string arguments, string workingDirectory)
    {
        var trigger = kind == "Logon"
            ? new XElement(Ns + "LogonTrigger", new XElement(Ns + "Enabled", true), new XElement(Ns + "UserId", sid))
            : new XElement(Ns + "TimeTrigger", new XElement(Ns + "Repetition", new XElement(Ns + "Interval", "PT5M"), new XElement(Ns + "StopAtDurationEnd", false)),
                new XElement(Ns + "StartBoundary", DateTime.Now.AddMinutes(5).ToString("s", CultureInfo.InvariantCulture)), new XElement(Ns + "Enabled", true));
        return new XDocument(new XElement(Ns + "Task", new XAttribute("version", "1.2"), new XElement(Ns + "Triggers", trigger),
            new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "Author"), new XElement(Ns + "UserId", sid),
                new XElement(Ns + "LogonType", "InteractiveToken"), new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings", new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"), new XElement(Ns + "DisallowStartIfOnBatteries", false),
                new XElement(Ns + "StopIfGoingOnBatteries", false), new XElement(Ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"), new XElement(Ns + "Exec", new XElement(Ns + "Command", executable),
                new XElement(Ns + "Arguments", arguments), new XElement(Ns + "WorkingDirectory", workingDirectory))))).ToString();
    }

    // 先成功枚举任务，不能把“查询被拒绝”误判为“不存在”。
    static async Task<Dictionary<string, string?>> ReadPairAsync(IRunner commands, string prefix)
    {
        var names = await NamesAsync(commands).ConfigureAwait(false);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in new[] { "Logon", "Watchdog" })
        {
            var name = prefix + "-" + kind;
            result[name] = names.Contains(name) ? await ReadXmlAsync(commands, name).ConfigureAwait(false) : null;
        }
        return result;
    }
    static async Task<string?> ReadTaskAsync(IRunner commands, string name) =>
        (await NamesAsync(commands).ConfigureAwait(false)).Contains(name) ? await ReadXmlAsync(commands, name).ConfigureAwait(false) : null;
    static async Task<string> ReadXmlAsync(IRunner commands, string name)
    {
        var response = await commands.RunAsync(["/Query", "/TN", name, "/XML"]).ConfigureAwait(false);
        Ensure(response, "回读", name);
        XDocument.Parse(response.Output);
        return response.Output;
    }
    static async Task<HashSet<string>> NamesAsync(IRunner commands)
    {
        var response = await commands.RunAsync(["/Query", "/FO", "CSV", "/NH"]).ConfigureAwait(false);
        Ensure(response, "枚举", "新版任务");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in response.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.TrimStart();
            if (!value.StartsWith('"')) continue;
            var name = new StringBuilder();
            for (var i = 1; i < value.Length; i++)
            {
                if (value[i] != '"') { name.Append(value[i]); continue; }
                if (i + 1 < value.Length && value[i + 1] == '"') { name.Append('"'); i++; continue; }
                break;
            }
            result.Add(name.ToString().TrimStart('\\'));
        }
        return result;
    }
    static void Ensure(CommandResult result, string action, string name)
    {
        if (result.Code == 0) return;
        var detail = (result.Error + " " + result.Output).Trim();
        if (detail.Length > 1024) detail = detail[..1024] + "…";
        throw new IOException($"自启任务{action}失败（{name}）：{detail}");
    }
    static async Task CreateAsync(IRunner commands, string name, string xml)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, xml, Encoding.Unicode).ConfigureAwait(false);
            Ensure(await commands.RunAsync(["/Create", "/TN", name, "/XML", file, "/F"]).ConfigureAwait(false), "创建", name);
        }
        finally { File.Delete(file); }
    }
    internal static string BuildArguments(string directory, string mode) => "--data-dir " + QuoteArgument(directory) + " " + mode;
    internal static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal sealed record CommandResult(int Code, string Output, string Error);
    internal interface IRunner { Task<CommandResult> RunAsync(string[] arguments); }
    sealed class ProcessRunner : IRunner
    {
        public async Task<CommandResult> RunAsync(string[] arguments)
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            process.Start();
            try
            {
                var output = ReadBytesAsync(process.StandardOutput.BaseStream, timeout.Token);
                var error = ReadBytesAsync(process.StandardError.BaseStream, timeout.Token);
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error).ConfigureAwait(false);
                return new(process.ExitCode, Decode(await output.ConfigureAwait(false)), Decode(await error.ConfigureAwait(false)));
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception failure) when (failure is Win32Exception or InvalidOperationException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
                throw new IOException(error is OperationCanceledException ? "任务计划程序响应超时。" : error is InvalidDataException ? "任务计划程序输出超过限制。" : "任务计划程序调用失败。", error);
            }
        }
    }
    static async Task<byte[]> ReadBytesAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("任务输出过大。");
            output.Write(buffer, 0, count);
        }
    }
    static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0) return Encoding.Unicode.GetString(bytes);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException)
        { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding((int)GetOEMCP()).GetString(bytes); }
    }
    [DllImport("kernel32.dll")] static extern uint GetOEMCP();

    public static async Task<int> RunSafetyTestsAsync()
    {
        var assertions = 0;
        void Check(bool condition, string reason) { assertions++; if (!condition) throw new InvalidOperationException(reason); }
        const string prefix = "ZhulongNext-fake", sid = "S-1-5-21-fake", executable = @"C:\新程序\Zhulong.exe", working = @"C:\新程序\";
        string[] paths = [@"C:\目录 有空格\data\", @"C:\", @"\\server\share\目录 空格\", "", "quoted\"value\\", @"C:\目录\"];
        foreach (var path in paths)
        {
            var parsed = ParseArguments("dummy.exe " + BuildArguments(path, "--watchdog"));
            Check(parsed.SequenceEqual(new[] { "dummy.exe", "--data-dir", path, "--watchdog" }), "Windows参数引用不一致");
        }
        var original = new Dictionary<string, string> { [prefix + "-Logon"] = Definition("Logon", sid, executable, "old-logon", working), [prefix + "-Watchdog"] = Definition("Watchdog", sid, executable, "old-watchdog", working) };
        foreach (var enabled in new[] { true, false })
        {
            var fake = new FakeRunner(original) { FailMutation = 2 };
            var failed = false;
            try { await ChangeAsync(enabled, paths[0], fake, prefix, sid, executable, working).ConfigureAwait(false); } catch (IOException) { failed = true; }
            Check(failed, "第二项失败必须拒绝成功");
            Check(fake.Tasks.Count == 2 && original.All(pair => fake.Tasks.GetValueOrDefault(pair.Key) == pair.Value), "补偿必须恢复两份原XML");
        }
        var fresh = new FakeRunner([]) { FailMutation = 2 };
        try { await ChangeAsync(true, paths[0], fresh, prefix, sid, executable, working).ConfigureAwait(false); } catch (IOException) { }
        Check(fresh.Tasks.Count == 0, "首次创建失败不得遗留第一项任务");
        var readback = new FakeRunner(original) { FailReadback = true }; var readFailed = false;
        try { await ChangeAsync(true, paths[0], readback, prefix, sid, executable, working).ConfigureAwait(false); }
        catch (IOException error) { readFailed = error.InnerException is IOException inner && inner.Message.Contains("回读"); }
        Check(readFailed && original.All(pair => readback.Tasks.GetValueOrDefault(pair.Key) == pair.Value), "必须先处理回读失败并补偿");
        var timeout = new FakeRunner(original) { Timeout = true }; var timedOut = false;
        try { await ChangeAsync(true, paths[0], timeout, prefix, sid, executable, working).ConfigureAwait(false); } catch (IOException) { timedOut = true; }
        Check(timedOut && timeout.Mutations == 0, "超时不能继续变更");
        var timeoutDuring = new FakeRunner(original) { TimeoutMutation = 2 }; var duringRejected = false;
        try { await ChangeAsync(true, paths[0], timeoutDuring, prefix, sid, executable, working).ConfigureAwait(false); } catch (IOException) { duringRejected = true; }
        Check(duringRejected && original.All(pair => timeoutDuring.Tasks.GetValueOrDefault(pair.Key) == pair.Value), "变更中途超时必须补偿两项");
        const string encoded = "<?xml version=\"1.0\"?><测试>路径</测试>";
        Check(Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(encoded)).ToArray()) == encoded, "UTF16 BOM解码失败");
        Check(Decode(Encoding.Unicode.GetBytes(encoded)) == encoded, "UTF16无BOM解码失败");
        Check(Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(encoded)).ToArray()) == encoded, "UTF8 BOM解码失败");
        var outputRejected = false;
        try { await ReadBytesAsync(new MemoryStream(new byte[4 * 1024 * 1024 + 1]), CancellationToken.None).ConfigureAwait(false); } catch (InvalidDataException) { outputRejected = true; }
        Check(outputRejected, "命令输出必须有界");
        var success = new FakeRunner(original);
        await ChangeAsync(true, paths[0], success, prefix, sid, executable, working).ConfigureAwait(false);
        Check(success.Tasks.Values.All(xml => XDocument.Parse(xml).Descendants(Ns + "Arguments").Single().Value.Contains(QuoteArgument(paths[0]))), "两个任务都必须保留自定义目录");
        await ChangeAsync(false, paths[0], success, prefix, sid, executable, working).ConfigureAwait(false);
        Check(success.Tasks.Count == 0, "关闭应删除两项任务");
        var mixed = new Dictionary<string, string>(original)
        { [prefix + "-Watchdog"] = Definition("Watchdog", sid, @"D:\另一份\Zhulong.exe", "foreign", @"D:\另一份\") };
        var owned = new FakeRunner(mixed);
        await ChangeAsync(false, paths[0], owned, prefix, sid, executable, working, executable).ConfigureAwait(false);
        Check(owned.Tasks.Count == 1 && owned.Tasks.GetValueOrDefault(prefix + "-Watchdog") == mixed[prefix + "-Watchdog"], "卸载只能移除本EXE任务，外部安装应原样保留");
        Check(owned.Mutations == 1, "卸载不得改写外部安装任务");
        var ownedFailure = new FakeRunner(original) { FailMutation = 2 };
        try { await ChangeAsync(false, paths[0], ownedFailure, prefix, sid, executable, working, executable).ConfigureAwait(false); } catch (IOException) { }
        Check(original.All(pair => ownedFailure.Tasks.GetValueOrDefault(pair.Key) == pair.Value), "卸载第二项失败必须恢复两owned任务");
        var other = new FakeRunner(mixed.Where(pair => pair.Key.EndsWith("Watchdog")).ToDictionary(pair => pair.Key, pair => pair.Value));
        await ChangeAsync(false, paths[0], other, prefix, sid, executable, working, executable).ConfigureAwait(false);
        Check(other.Mutations == 0 && other.Tasks.Count == 1, "只有其他目录任务时不应有任何变更");
        Check(OwnsCommand(original[prefix + "-Logon"], executable.ToUpperInvariant()), "EXE归属应按Windows完整路径比较");

        var logonName = prefix + "-Logon";
        var watchdogName = prefix + "-Watchdog";
        var loginOnly = new FakeRunner([]);
        await ChangeAsync(true, paths[0], loginOnly, prefix, sid, executable, working, selected: Kind.Logon).ConfigureAwait(false);
        Check(loginOnly.Tasks.Count == 1 && loginOnly.Tasks.ContainsKey(logonName), "仅登录自启应只注册登录任务");
        var logonXml = loginOnly.Tasks[logonName];
        Check(TaskIsEnabled(XDocument.Parse(logonXml), Kind.Logon) &&
            XDocument.Parse(logonXml).Descendants(Ns + "Arguments").Single().Value == BuildArguments(paths[0], "--background"),
            "登录任务须处于启用状态并使用后台入口");
        var normalized = new FakeRunner([]) { NormalizeLogonXml = true };
        await ChangeAsync(true, paths[0], normalized, prefix, sid, executable, working, selected: Kind.Logon).ConfigureAwait(false);
        Check(normalized.Tasks.ContainsKey(logonName), "接受 Windows 默认运行级别与规范化后的触发器用户");

        await ChangeAsync(true, paths[0], loginOnly, prefix, sid, executable, working, selected: Kind.Watchdog).ConfigureAwait(false);
        Check(loginOnly.Tasks.Count == 2 && loginOnly.Tasks[logonName] == logonXml && loginOnly.Mutations == 2,
            "开启守护应保留登录任务的原XML");
        await ChangeAsync(false, paths[0], loginOnly, prefix, sid, executable, working, selected: Kind.Watchdog).ConfigureAwait(false);
        Check(loginOnly.Tasks.Count == 1 && loginOnly.Tasks[logonName] == logonXml && loginOnly.Mutations == 3,
            "已有两项任务时可独立关闭守护");

        var watchdogOnly = new FakeRunner(original);
        await ChangeAsync(false, paths[0], watchdogOnly, prefix, sid, executable, working, selected: Kind.Logon).ConfigureAwait(false);
        Check(watchdogOnly.Tasks.Count == 1 && watchdogOnly.Tasks[watchdogName] == original[watchdogName] && watchdogOnly.Mutations == 1,
            "关闭登录自启应保留守护的原XML");

        var selectedFailure = new FakeRunner(original) { FailReadback = true };
        var selectedRejected = false;
        try { await ChangeAsync(true, paths[0], selectedFailure, prefix, sid, executable, working, selected: Kind.Logon).ConfigureAwait(false); }
        catch (IOException) { selectedRejected = true; }
        Check(selectedRejected && selectedFailure.Mutations == 2 && original.All(pair => selectedFailure.Tasks.GetValueOrDefault(pair.Key) == pair.Value),
            "单项回读失败只补偿所选任务，另一任务不应有写入");

        var foreign = new FakeRunner(mixed);
        var foreignRejected = false;
        try { await ChangeAsync(true, paths[0], foreign, prefix, sid, executable, working, selected: Kind.Watchdog).ConfigureAwait(false); }
        catch (IOException) { foreignRejected = true; }
        Check(foreignRejected && foreign.Mutations == 0, "其他安装目录的同名任务不得改写");

        var disabledTask = XDocument.Parse(logonXml);
        disabledTask.Root!.Element(Ns + "Settings")!.Add(new XElement(Ns + "Enabled", false));
        Check(!TaskIsEnabled(disabledTask, Kind.Logon), "存在但已禁用的任务不应显示已启用");
        disabledTask.Root.Element(Ns + "Settings")!.Element(Ns + "Enabled")!.Remove();
        disabledTask.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "Enabled")!.SetValue(false);
        Check(!TaskIsEnabled(disabledTask, Kind.Logon), "触发器已禁用时不应显示已启用");
        return assertions;
    }
    sealed class FakeRunner(Dictionary<string, string> initial) : IRunner
    {
        public Dictionary<string, string> Tasks { get; } = new(initial, StringComparer.OrdinalIgnoreCase);
        public int FailMutation, TimeoutMutation, Mutations; public bool FailReadback, Timeout, NormalizeLogonXml;
        public Task<CommandResult> RunAsync(string[] arguments)
        {
            if (Timeout) return Task.FromException<CommandResult>(new IOException("任务计划程序响应超时。"));
            var name = Array.IndexOf(arguments, "/TN") is var at && at >= 0 ? arguments[at + 1] : "";
            if (arguments[0] == "/Query")
            {
                if (name.Length == 0) return Task.FromResult(new CommandResult(0, string.Join("\n", Tasks.Keys.Select(value => "\"\\" + value + "\",\"fixture\"")), ""));
                if (FailReadback && Mutations > 0) { FailReadback = false; return Task.FromResult(new CommandResult(1, "not XML", "fixture readback failure")); }
                return Task.FromResult(Tasks.TryGetValue(name, out var xml) ? new CommandResult(0, xml, "") : new CommandResult(1, "", "not found"));
            }
            Mutations++;
            if (Mutations == TimeoutMutation) return Task.FromException<CommandResult>(new IOException("任务计划程序响应超时。"));
            if (Mutations == FailMutation) return Task.FromResult(new CommandResult(1, "", "fixture second-task failure"));
            if (arguments[0] == "/Delete") Tasks.Remove(name);
            else
            {
                var xml = File.ReadAllText(arguments[Array.IndexOf(arguments, "/XML") + 1], Encoding.Unicode);
                if (NormalizeLogonXml && name.EndsWith("-Logon", StringComparison.Ordinal))
                {
                    var document = XDocument.Parse(xml);
                    document.Descendants(Ns + "RunLevel").Single().Remove();
                    var trigger = document.Root!.Element(Ns + "Triggers")!;
                    trigger.Element(Ns + "LogonTrigger")!.Element(Ns + "UserId")!.SetValue(@"HOST\User");
                    trigger.Remove();
                    document.Root.Add(trigger);
                    xml = document.ToString();
                }
                Tasks[name] = xml;
            }
            return Task.FromResult(new CommandResult(0, "", ""));
        }
    }
    static string[] ParseArguments(string text)
    {
        var pointer = CommandLineToArgvW(text, out var count);
        if (pointer == IntPtr.Zero) throw new IOException("参数解析失败。");
        try { return Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr value);
}
