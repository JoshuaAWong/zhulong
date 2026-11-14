using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Zhulong.Desktop;

internal sealed record UpdatePackage(string Version, string FileName, long Size, string Sha256, string Tag);

internal static class UpdateDownload
{
    const string Repository = "JoshuaAWong/zhulong";
    const long MaximumPackageSize = 512L * 1024 * 1024;
    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        MaxResponseContentBufferSize = 1024 * 1024,
        Timeout = Timeout.InfiniteTimeSpan
    };

    internal static async Task<UpdatePackage?> CheckAsync(string currentVersion, bool installed,
        CancellationToken cancellationToken, HttpClient? client = null)
    {
        if (!ParseVersion(currentVersion.Split('-', 2)[0], out var current))
            throw new InvalidDataException("无法识别当前烛龙版本。");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = CreateRequest(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await (client ?? Client).SendAsync(request, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub 暂时无法提供更新列表，请稍后再试。");

        EnsureSuccess(response, "获取更新列表失败");
        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
        var metadata = release.RootElement;
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("GitHub 发布信息格式无法识别。");

        if ((metadata.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) ||
            (metadata.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True))
            return null;

        var tag = ReadString(metadata, "tag_name");
        var version = tag.StartsWith('v') ? tag[1..] : tag;
        if (!ParseVersion(version, out var available))
            throw new InvalidDataException("GitHub 最新发布的版本号格式无法识别。");
        if (available <= current) return null;

        var fileName = PackageFileName(version, installed);
        if (metadata.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (ReadString(asset, "name") != fileName) continue;
                var digest = ReadString(asset, "digest");
                if (!asset.TryGetProperty("size", out var length) || length.ValueKind != JsonValueKind.Number ||
                    !length.TryGetInt64(out var size) || size is <= 0 or > MaximumPackageSize ||
                    !digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsSha256(digest[7..]) ||
                    ReadString(asset, "state") != "uploaded")
                    throw new InvalidDataException("发布包缺少有效的大小、SHA-256 或上传状态。");

                return new UpdatePackage(version, fileName, size, digest[7..], tag);
            }
        }
        throw new InvalidDataException("最新版本没有对应的安装版或便携版更新包。");
    }

    internal static async Task<string> StageAsync(UpdatePackage package, string programDirectory,
        CancellationToken cancellationToken, HttpClient? client = null)
    {
        if (!ParseVersion(package.Version, out _) || (package.Tag != package.Version && package.Tag != "v" + package.Version) ||
            (package.FileName != PackageFileName(package.Version, true) && package.FileName != PackageFileName(package.Version, false)) ||
            package.Size is <= 0 or > MaximumPackageSize || !IsSha256(package.Sha256))
            throw new InvalidDataException("更新包元数据无效。");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        var program = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programDirectory));
        var updates = Path.Combine(program, "updates");
        var finalPath = Path.Combine(updates, package.FileName);
        CheckStagePaths(program, updates, finalPath);
        token.ThrowIfCancellationRequested();
        if (File.Exists(finalPath) && await CanReuseAsync(finalPath, package, token).ConfigureAwait(false))
        {
            CheckStagePaths(program, updates, finalPath);
            return finalPath;
        }

        var uri = new Uri($"https://github.com/{Repository}/releases/download/{package.Tag}/{package.FileName}");
        using var response = await OpenPackageAsync(client ?? Client, uri, token).ConfigureAwait(false);
        EnsureSuccess(response, "下载更新包失败");
        if (response.Content.Headers.ContentLength is { } size && size != package.Size)
            throw new InvalidDataException("更新包长度与发布记录不一致。");

        CheckStagePaths(program, updates, finalPath);
        Directory.CreateDirectory(updates);
        RejectReparsePoint(updates);
        var temporaryPath = Path.Combine(updates, package.FileName + "." + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                var buffer = new byte[64 * 1024];
                long written = 0;
                int count;
                while ((count = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                {
                    written += count;
                    if (written > package.Size) throw new InvalidDataException("更新包超过发布记录的长度。");
                    digest.AppendData(buffer.AsSpan(0, count));
                    await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                if (written != package.Size || !Convert.ToHexString(digest.GetHashAndReset()).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包长度或 SHA-256 校验失败。");
            }

            token.ThrowIfCancellationRequested();
            CheckStagePaths(program, updates, finalPath);
            File.Move(temporaryPath, finalPath, overwrite: true);
            return finalPath;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    static async Task<bool> CanReuseAsync(string path, UpdatePackage package, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != package.Size) return false;
        var hash = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    static void CheckStagePaths(string program, string updates, string package)
    {
        RejectReparsePoint(program);
        RejectReparsePoint(updates);
        RejectReparsePoint(package);
    }

    static void RejectReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("程序、updates 或更新包路径不能是链接。");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
    }

    static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Zhulong-Desktop");
        return request;
    }

    static async Task<HttpResponseMessage> OpenPackageAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        using var request = CreateRequest(uri);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect))
            return response;

        using (response)
        {
            var location = response.Headers.Location;
            if (location is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } ||
                location.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("更新下载重定向到了非 GitHub 发布资源地址。");

            using var redirected = CreateRequest(location);
            return await client.SendAsync(redirected, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
    }

    static void EnsureSuccess(HttpResponseMessage response, string message)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"{message}（HTTP {(int)response.StatusCode}）。");
    }

    static string ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString()! : "";

    internal static string PackageFileName(string version, bool installed) =>
        $"Zhulong-{version}-{(installed ? "setup.exe" : "win-x64.zip")}";

    static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    static bool ParseVersion(string text, out Version version)
    {
        if (Version.TryParse(text, out var parsed) && parsed.Build >= 0 && parsed.Revision < 0 &&
            parsed.ToString(3).Equals(text, StringComparison.Ordinal))
        {
            version = parsed;
            return true;
        }
        version = default!;
        return false;
    }
}
