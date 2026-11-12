using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zhulong.Runtime.Providers.AI;

public sealed record AiAccountPreferences(string KimiPrimary = "当前登录", string KimiSecondary = "第二账号",
    string Glm = "GLM 套餐", bool GlmDomestic = true, Guid? GlmKeyRevision = null);

public static class AiAccountProfiles
{
    public static string DefaultKimiHome => Path.GetFullPath(Environment.GetEnvironmentVariable("KIMI_CODE_HOME") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code"));
    public static string SecondaryKimiHome(string dataDirectory) => Path.Combine(dataDirectory, "kimi-secondary");

    public static void StartSecondaryKimi(string dataDirectory, bool login)
    {
        var executable = KimiAccountClient.FindExecutable() ?? throw new InvalidOperationException("未找到 Kimi Code CLI。");
        var home = SecondaryKimiHome(dataDirectory);
        if (string.Equals(Path.GetFullPath(home), DefaultKimiHome, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前 Kimi 配置已指向第二账号目录，请从默认环境打开烛龙。");
        Directory.CreateDirectory(home);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        start.Environment["KIMI_CODE_HOME"] = home;
        if (login) start.ArgumentList.Add("login");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("未能打开 Kimi Code CLI。");
    }

    public static AiAccountPreferences Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "ai-profiles.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<AiAccountPreferences>(File.ReadAllText(path, Encoding.UTF8)) ?? new();
    }

    public static void Save(string dataDirectory, AiAccountPreferences value, string? newGlmKey, bool removeGlmKey)
    {
        var path = Path.Combine(dataDirectory, "ai-profiles.json");
        var temporary = path + ".tmp";
        if (newGlmKey is not null || removeGlmKey) value = value with { GlmKeyRevision = Guid.NewGuid() };
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        if (newGlmKey is null && !removeGlmKey) { File.Move(temporary, path, true); return; }
        var previous = ReadGlmKey(dataDirectory);
        SetKey(dataDirectory, "GLM", newGlmKey);
        try { File.Move(temporary, path, true); }
        catch (IOException) { SetKey(dataDirectory, "GLM", previous); throw; }
        catch (UnauthorizedAccessException) { SetKey(dataDirectory, "GLM", previous); throw; }
    }

    public static bool HasGlmKey(string dataDirectory) => HasKey(dataDirectory, "GLM");
    public static bool HasDeepSeekKey(string dataDirectory) => HasKey(dataDirectory, "DeepSeek");

    public static void SaveDeepSeekKey(string dataDirectory, string? key)
    {
        if (key is not null && !DeepSeekAccountClient.IsValidKey(key))
            throw new ArgumentException("DeepSeek API Key 格式不正确。", nameof(key));
        SetKey(dataDirectory, "DeepSeek", key, Guid.NewGuid().ToString("N"));
    }

    static bool HasKey(string dataDirectory, string provider)
    {
        if (!ReadCredential(dataDirectory, provider, out var pointer)) return false;
        CredFree(pointer);
        return true;
    }

    internal static string? ReadGlmKey(string dataDirectory) => ReadKey(dataDirectory, "GLM", out _);
    internal static string? ReadDeepSeekKey(string dataDirectory, out string? version) =>
        ReadKey(dataDirectory, "DeepSeek", out version);

    internal static string? DeepSeekKeyVersion(string dataDirectory)
    {
        if (!ReadCredential(dataDirectory, "DeepSeek", out var pointer)) return null;
        try { return Version(Marshal.PtrToStructure<Credential>(pointer)); }
        finally { CredFree(pointer); }
    }

    static string? ReadKey(string dataDirectory, string provider, out string? version)
    {
        version = null;
        if (!ReadCredential(dataDirectory, provider, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            version = Version(credential);
            return Marshal.PtrToStringUni(credential.Blob, credential.BlobSize / 2);
        }
        finally { CredFree(pointer); }
    }

    static string Version(Credential value) =>
        $"{value.Comment}:{value.LastWritten.dwHighDateTime:x8}{value.LastWritten.dwLowDateTime:x8}";

    static bool ReadCredential(string dataDirectory, string provider, out IntPtr pointer)
    {
        if (CredRead(Target(dataDirectory, provider), Generic, 0, out pointer)) return true;
        var error = Marshal.GetLastWin32Error();
        return error == NotFound ? false : throw new Win32Exception(error);
    }

    static string Target(string directory, string provider) => $"ZhulongNext/{provider}/" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));

    static void SetKey(string directory, string provider, string? key, string? version = null)
    {
        var target = Target(directory, provider);
        if (key is null)
        {
            if (!CredDelete(target, Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }
        var pointer = Marshal.StringToHGlobalUni(key);
        try
        {
            var value = new Credential { Type = Generic, TargetName = target, Comment = version, Persist = 2,
                BlobSize = key.Length * 2, Blob = pointer };
            if (!CredWrite(ref value, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }

    const int Generic = 1, NotFound = 1168;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public int Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int BlobSize;
        public IntPtr Blob;
        public int Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredRead(string name, int type, int flags, out IntPtr value);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredWrite(ref Credential value, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredDelete(string name, int type, int flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr value);
}
