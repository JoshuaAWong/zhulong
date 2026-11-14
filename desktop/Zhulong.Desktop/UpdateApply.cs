using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace Zhulong.Desktop;

internal static class UpdateApply
{
    const string ManifestName = "runtime-manifest.json";
    const string InstallationName = "installation.json";
    const string PlanName = "plan.json";
    sealed record FileDigest(long Size, string Sha256);
    sealed record RuntimeManifest(string Version, Dictionary<string, FileDigest> Files);
    sealed record Plan(UpdatePackage Package, string DataDirectory, int ParentId, long ParentStartTicks, bool Installed);

    static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    static readonly HashSet<string> DeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", .. Enumerable.Range(0, 10).Select(i => "COM" + i),
         .. Enumerable.Range(0, 10).Select(i => "LPT" + i), "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"], Names);

    internal static bool IsApplying(string programDirectory)
    {
        if (!Mutex.TryOpenExisting(LockName(programDirectory), out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    internal static Task StartAsync(string programDirectory, string dataDirectory, UpdatePackage package,
        string packagePath, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programDirectory));
        var data = Path.GetFullPath(dataDirectory);
        var installed = File.Exists(Path.Combine(root, InstallationName));
        var source = GetPackagePath(root, package, installed);
        if (!Names.Equals(Path.GetFullPath(packagePath), source) || IsApplying(root))
            throw new InvalidOperationException("更新包位置不符或已有更新正在执行。");
        var current = ReadManifest(root);
        CheckUpdate(root, data, package, current, installed);
        var updates = Path.Combine(root, "updates");
        var backup = Path.Combine(updates, $"rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backup);
        using var parent = Process.GetCurrentProcess();
        var planPath = Path.Combine(backup, PlanName);
        foreach (var name in OwnedFiles(current, installed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyFile(CheckedPath(root, name), Path.Combine(backup, name));
        }
        File.WriteAllText(planPath, JsonSerializer.Serialize(
            new Plan(package, data, parent.Id, parent.StartTime.ToUniversalTime().Ticks, installed), Program.Json));

        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyName(backup));
        using var worker = StartProcess(Path.Combine(backup, "Zhulong.exe"), "--apply-update", planPath);
        var timeout = Stopwatch.StartNew();
        try
        {
            while (!ready.WaitOne(100))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (worker.HasExited)
                {
                    var result = Path.Combine(backup, "result.txt");
                    throw new IOException(File.Exists(result) ? File.ReadAllText(result) : "更新程序未能启动。");
                }
                if (timeout.Elapsed > TimeSpan.FromSeconds(60))
                    throw new TimeoutException("验证更新包超时。");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            if (!worker.HasExited) worker.Kill();
            throw;
        }
    }, cancellationToken);

    internal static int Run(string requestedPlanPath)
    {
        var backup = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var updates = Path.GetDirectoryName(backup);
        var root = updates is null ? null : Path.GetDirectoryName(updates);
        var planPath = Path.Combine(backup, PlanName);
        if (root is null || !Names.Equals(Path.GetFileName(updates), "updates") ||
            !Path.GetFileName(backup).StartsWith("rollback-", StringComparison.Ordinal) ||
            !Names.Equals(Path.GetFullPath(requestedPlanPath), planPath)) return 2;

        var resume = false;
        var restart = false;
        Plan? plan = null;
        var result = "更新成功。";
        try
        {
            using var mutex = new Mutex(true, LockName(root), out var first);
            if (!first) throw new IOException("此位置已有更新在执行。");
            plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(planPath), Program.Json)
                ?? throw new InvalidDataException("缺少更新计划。");
            var current = ReadManifest(backup);
            CheckUpdate(root, plan.DataDirectory, plan.Package, current, plan.Installed);
            var packagePath = GetPackagePath(root, plan.Package, plan.Installed);
            using var package = File.OpenRead(packagePath);
            if (package.Length != plan.Package.Size || !Names.Equals(Convert.ToHexString(SHA256.HashData(package)), plan.Package.Sha256))
                throw new InvalidDataException("更新包校验失败。");
            package.Position = 0;
            using var parent = Process.GetProcessById(plan.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTicks ||
                !Names.Equals(parent.MainModule?.FileName, Path.Combine(root, "Zhulong.exe")))
                throw new InvalidOperationException("正在运行的烛龙与更新目标不一致。");
            // 在允许父进程退出前保留句柄，退出后仍能读取退出码。
            _ = parent.SafeHandle;
            using var ready = EventWaitHandle.OpenExisting(ReadyName(backup));
            ready.Set();
            resume = true;
            if (!parent.WaitForExit(90000))
                throw new TimeoutException("烛龙未能及时退出，文件未被替换。");
            restart = true;
            if (parent.ExitCode != 0 || !File.Exists(Path.Combine(plan.DataDirectory, "intentional-exit")))
                throw new IOException("未收到烛龙的正常退出记录，已取消更新。");

            if (plan.Installed)
                ApplyInstaller(root, backup, packagePath, plan, current);
            else
                ApplyPortable(root, backup, package, plan, current, () => VerifyStartup(root, backup));
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            result = error.Message;
            if (error is AggregateException) restart = false;
            try { File.WriteAllText(Path.Combine(backup, "result.txt"), result); } catch (IOException) { }
            if (!resume) return 1;
        }
        if (restart && plan is not null)
        {
            try
            {
                using var next = StartProcess(Path.Combine(root, "Zhulong.exe"),
                    "--background", "--data-dir", plan.DataDirectory);
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
            {
                result += "\n烛龙未能重新启动：" + error.Message;
            }
        }
        File.WriteAllText(Path.Combine(backup, "result.txt"), result);
        if (result != "更新成功。") MessageBox.Show(result, "烛龙 · 版本更新");
        return result == "更新成功。" ? 0 : 1;
    }

    static void ApplyInstaller(string root, string backup, string path, Plan plan, RuntimeManifest current)
    {
        RuntimeManifest? installed = null;
        try
        {
            using var process = StartProcess(path, "/SILENT", "/NORESTART", "/NOCLOSEAPPLICATIONS",
                "/DIR=" + root, "/LOG=" + Path.Combine(backup, "installer.log"));
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException($"安装程序已取消或失败（退出码 {process.ExitCode}）。");
            installed = ReadManifest(root);
            if (!Names.Equals(installed.Version, plan.Package.Version) ||
                !Names.Equals(ReadInstalledVersion(root), plan.Package.Version))
                throw new InvalidDataException("安装后的版本与更新包版本不符。");
            ValidateFiles(root, plan.DataDirectory, installed);
            VerifyStartup(root, backup);
        }
        catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException))
        {
            try
            {
                installed ??= TryReadManifest(root);
                if (installed is not null)
                {
                    ValidatePaths(root, plan.DataDirectory, installed);
                    foreach (var name in installed.Files.Keys.Except(current.Files.Keys, Names))
                        File.Delete(CheckedPath(root, name));
                }
                foreach (var name in OwnedFiles(current, true))
                {
                    var target = CheckedPath(root, name);
                    var source = Path.Combine(backup, name);
                    if (File.Exists(target) && SameFile(source, target)) continue;
                    CopyFile(source, target);
                }
            }
            catch (Exception rollback) when (rollback is not (OutOfMemoryException or StackOverflowException))
            {
                throw new AggregateException($"更新失败，恢复程序文件未完成。恢复副本：{backup}。{failure.Message} {rollback.Message}", failure, rollback);
            }
            throw new IOException("已恢复更新前的程序文件。" + failure.Message, failure);
        }
    }

    static void ApplyPortable(string root, string backup, Stream package, Plan plan, RuntimeManifest current, Action verify)
    {
        var staged = Path.Combine(backup, "prepared");
        Directory.CreateDirectory(staged);
        using var archive = new ZipArchive(package, ZipArchiveMode.Read);
        var entries = archive.Entries.ToDictionary(entry => entry.FullName, Names);
        if (!entries.TryGetValue("Zhulong/" + ManifestName, out var manifestEntry) || manifestEntry.Length > 2_000_000)
            throw new InvalidDataException("压缩包缺少有效的程序清单。");
        using var manifestStream = manifestEntry.Open();
        var next = ReadManifest(manifestStream);
        if (!Names.Equals(next.Version, plan.Package.Version) || entries.Count != next.Files.Count + 1 ||
            archive.Entries.Sum(entry => entry.Length) > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("压缩包内容与版本清单不符。");
        ValidatePaths(root, plan.DataDirectory, next);
        foreach (var (name, digest) in next.Files)
        {
            if (!entries.TryGetValue("Zhulong/" + name, out var entry) || entry.Length != digest.Size)
                throw new InvalidDataException("压缩包文件与清单不符：" + name);
            var target = Path.Combine(staged, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
        manifestEntry.ExtractToFile(Path.Combine(staged, ManifestName));
        ValidateFiles(staged, plan.DataDirectory, next);
        foreach (var name in next.Files.Keys.Except(current.Files.Keys, Names))
        {
            var target = CheckedPath(root, name);
            if (File.Exists(target) || Directory.Exists(target))
                throw new IOException("便携更新与现有文件冲突：" + name);
        }

        var changed = new List<string>();
        var oldFiles = current.Files.Keys.Append(ManifestName).ToHashSet(Names);
        var newFiles = next.Files.Keys.Append(ManifestName).ToHashSet(Names);
        try
        {
            var names = current.Files.Keys.Union(next.Files.Keys, Names).OrderBy(name => name, Names).Append(ManifestName);
            foreach (var name in names)
            {
                var target = CheckedPath(root, name);
                if (newFiles.Contains(name))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(Path.Combine(staged, name), target, oldFiles.Contains(name));
                }
                else File.Delete(target);
                changed.Add(name);
            }
            verify();
        }
        catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException))
        {
            try
            {
                foreach (var name in changed.AsEnumerable().Reverse())
                {
                    var target = CheckedPath(root, name);
                    if (oldFiles.Contains(name)) CopyFile(Path.Combine(backup, name), target);
                    else File.Delete(target);
                }
            }
            catch (Exception rollback) when (rollback is not (OutOfMemoryException or StackOverflowException))
            {
                throw new AggregateException($"更新失败，恢复程序文件未完成。恢复副本：{backup}。{failure.Message} {rollback.Message}", failure, rollback);
            }
            throw new IOException("已恢复更新前的程序文件。" + failure.Message, failure);
        }
    }

    static void CheckUpdate(string root, string data, UpdatePackage package, RuntimeManifest current, bool installed)
    {
        if (!Version.TryParse(current.Version.Split('-', 2)[0], out var oldVersion) ||
            !Version.TryParse(package.Version.Split('-', 2)[0], out var newVersion) || newVersion <= oldVersion ||
            installed != File.Exists(Path.Combine(root, InstallationName)) ||
            (installed && !Names.Equals(ReadInstalledVersion(root), current.Version)))
            throw new InvalidDataException("更新版本或交付形态与当前烛龙不匹配。");
        ValidatePaths(root, data, current);
        foreach (var name in installed ? new[] { ManifestName, InstallationName, "updates" } : [ManifestName, "updates"])
            EnsureOutsideData(CheckedPath(root, name), data);
        if (installed && !Names.Equals(Path.TrimEndingDirectorySeparator(data), Path.Combine(root, "data")))
            EnsureOutsideData(root, Path.TrimEndingDirectorySeparator(data));
    }

    static string GetPackagePath(string root, UpdatePackage package, bool installed)
    {
        var expected = UpdateDownload.PackageFileName(package.Version, installed);
        if (!string.Equals(expected, package.FileName, StringComparison.Ordinal) || !IsFileName(expected))
            throw new InvalidDataException("更新包类型与当前交付形态不匹配。");
        return CheckedPath(root, "updates/" + expected);
    }

    static RuntimeManifest ReadManifest(string root)
    {
        using var stream = File.OpenRead(CheckedPath(root, ManifestName));
        return ReadManifest(stream);
    }

    static RuntimeManifest ReadManifest(Stream stream)
    {
        var manifest = JsonSerializer.Deserialize<RuntimeManifest>(stream, Program.Json);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || manifest.Files is null ||
            !manifest.Files.ContainsKey("Zhulong.exe") || manifest.Files.Count > 4000)
            throw new InvalidDataException("程序清单无效。");
        return manifest;
    }

    static RuntimeManifest? TryReadManifest(string root)
    {
        try { return ReadManifest(root); }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException) { return null; }
    }

    static string? ReadInstalledVersion(string root)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(CheckedPath(root, InstallationName)));
        var item = json.RootElement;
        return item.GetProperty("mode").GetString() == "installed" ? item.GetProperty("version").GetString() : null;
    }

    static IEnumerable<string> OwnedFiles(RuntimeManifest manifest, bool installed) =>
        installed ? manifest.Files.Keys.Append(ManifestName).Append(InstallationName) : manifest.Files.Keys.Append(ManifestName);

    static void ValidatePaths(string root, string data, RuntimeManifest manifest)
    {
        var seen = new HashSet<string>(Names);
        foreach (var (name, digest) in manifest.Files)
        {
            var first = name.Split('/')[0];
            if (!seen.Add(name) || new[] { ManifestName, InstallationName, "data", "updates" }.Contains(first, Names) ||
                digest is null || digest.Size < 0 || digest.Sha256 is not { Length: 64 } || !digest.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("程序清单含有非法文件项：" + name);
            EnsureOutsideData(CheckedPath(root, name), data);
        }
    }

    static void ValidateFiles(string root, string data, RuntimeManifest manifest)
    {
        ValidatePaths(root, data, manifest);
        foreach (var (name, expected) in manifest.Files)
        {
            using var stream = File.OpenRead(CheckedPath(root, name));
            if (stream.Length != expected.Size || !Names.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected.Sha256))
                throw new InvalidDataException("程序文件校验失败：" + name);
        }
    }

    static bool IsFileName(string name) => name.Length > 0 && !name.EndsWith('.') && !name.EndsWith(' ') &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !DeviceNames.Contains(name.Split('.')[0]);

    static string CheckedPath(string root, string name)
    {
        var path = root;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("更新目录不能使用链接。");
        foreach (var component in name.Split('/'))
        {
            if (!IsFileName(component)) throw new InvalidDataException("更新路径无效：" + name);
            path = Path.Combine(path, component);
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("更新路径不能使用链接：" + name);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }

    static void EnsureOutsideData(string path, string data)
    {
        static bool Contains(string folder, string item)
        {
            folder = Path.TrimEndingDirectorySeparator(folder);
            return Names.Equals(folder, item) ||
                item.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        if (Contains(data, path) || Contains(path, data))
            throw new IOException("程序更新路径与当前数据目录重叠。");
    }

    static void VerifyStartup(string root, string backup)
    {
        var report = Path.Combine(backup, "verify.json");
        using var process = StartProcess(Path.Combine(root, "Zhulong.exe"), "--diagnose", report);
        if (!process.WaitForExit(30000))
        {
            process.Kill();
            process.WaitForExit();
            throw new TimeoutException("新版本启动检测超时。");
        }
        if (process.ExitCode != 0 || !File.Exists(report)) throw new IOException("新版本启动检测失败。");
    }

    static Process StartProcess(string path, params string[] arguments)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("无法启动 " + Path.GetFileName(path));
    }

    static void CopyFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, true);
    }

    static bool SameFile(string first, string second)
    {
        using var a = File.OpenRead(first);
        using var b = File.OpenRead(second);
        return a.Length == b.Length && SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }

    static string LockName(string root) => "Local\\ZhulongUpdate-" + PathKey(root);
    static string ReadyName(string backup) => "Local\\ZhulongUpdateReady-" + PathKey(backup);
    static string PathKey(string path) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant())));

    internal static void CheckBehavior(Action<bool, string> check, string outputDirectory)
    {
        var fixture = Path.Combine(outputDirectory, "update-apply-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(fixture, "portable");
        var data = Path.Combine(root, "custom-data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "monitor.db"), "existing sample");
        File.WriteAllText(Path.Combine(root, "private.txt"), "user file");
        var baseline = new Dictionary<string, string> { ["Zhulong.exe"] = "old", ["removed.dll"] = "old child" };
        var changes = new Dictionary<string, string> { ["Zhulong.exe"] = "new", ["new.dll"] = "new child" };
        static RuntimeManifest Manifest(string version, Dictionary<string, string> files) =>
            new(version, files.ToDictionary(item => item.Key, item =>
                new FileDigest(Encoding.UTF8.GetByteCount(item.Value), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Value))))));
        var old = Manifest("1.1.0", baseline);
        foreach (var (name, value) in baseline) File.WriteAllText(Path.Combine(root, name), value);
        File.WriteAllText(Path.Combine(root, ManifestName), JsonSerializer.Serialize(old, Program.Json));
        var plan = new Plan(new UpdatePackage("1.2.0", UpdateDownload.PackageFileName("1.2.0", false), 0, "", "v1.2.0"),
            data, 0, 0, false);
        string CreateBackup()
        {
            var backup = Path.Combine(fixture, Guid.NewGuid().ToString("N"));
            foreach (var name in OwnedFiles(old, false)) CopyFile(Path.Combine(root, name), Path.Combine(backup, name));
            return backup;
        }
        static MemoryStream CreatePackage(Dictionary<string, string> files)
        {
            var package = new MemoryStream();
            using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in files)
                {
                    using var entry = new StreamWriter(archive.CreateEntry("Zhulong/" + name).Open());
                    entry.Write(content);
                }
                using var manifest = new StreamWriter(archive.CreateEntry("Zhulong/" + ManifestName).Open());
                manifest.Write(JsonSerializer.Serialize(Manifest("1.2.0", files), Program.Json));
            }
            package.Position = 0;
            return package;
        }
        foreach (var blocked in new[] { "../outside.txt", "custom-data/monitor.db", "data/settings.json", "updates/payload.dll" })
        {
            using var unsafePackage = CreatePackage(new Dictionary<string, string>(changes) { [blocked] = "unsafe" });
            try
            {
                ApplyPortable(root, CreateBackup(), unsafePackage, plan, old, () => { });
                check(false, "必须拒绝指向数据目录或越界的压缩包更新");
            }
            catch (Exception error) when (error is InvalidDataException or IOException) { }
            check(ReadManifest(root).Version == old.Version, "压缩包校验失败时应保留现版");
        }
        using (var package = CreatePackage(changes))
        {
            try
            {
                ApplyPortable(root, CreateBackup(), package, plan, old, () => throw new IOException("模拟启动失败"));
                check(false, "模拟启动失败必须报告更新失败");
            }
            catch (IOException error) { check(error.Message.Contains("模拟启动失败"), "应报告失败和恢复结果"); }
        }
        check(ReadManifest(root).Version == old.Version &&
              baseline.All(item => File.ReadAllText(Path.Combine(root, item.Key)) == item.Value) &&
              !File.Exists(Path.Combine(root, "new.dll")), "新版本启动失败时应恢复旧文件并移除新增文件");
        using (var package = CreatePackage(changes))
            ApplyPortable(root, CreateBackup(), package, plan, old, () => { });
        check(ReadManifest(root).Version == plan.Package.Version &&
              changes.All(item => File.ReadAllText(Path.Combine(root, item.Key)) == item.Value) &&
              !File.Exists(Path.Combine(root, "removed.dll")), "便携版成功时应替换新清单并删除废弃程序文件");
        check(File.ReadAllText(Path.Combine(data, "monitor.db")) == "existing sample" &&
              File.ReadAllText(Path.Combine(root, "private.txt")) == "user file", "更新及恢复应保留自定义数据和其他文件");
    }
}
