using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zhulong.Desktop;

internal static class UpdateDownloadTests
{
    internal static int Run() => CheckAsync().GetAwaiter().GetResult();

    static async Task<int> CheckAsync()
    {
        var expected = Encoding.UTF8.GetBytes("烛龙更新包样本");
        var hash = Convert.ToHexString(SHA256.HashData(expected));
        var root = Path.Combine(Path.GetTempPath(), "zhulong-update-test-" + Guid.NewGuid().ToString("N"));
        var assertions = 0;
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
        static async Task<bool> RejectAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action().ConfigureAwait(false); }
            catch (T) { return true; }
            return false;
        }

        const string portableName = "Zhulong-0.2.7-win-x64.zip";
        const string installedName = "Zhulong-0.2.7-setup.exe";
        const string redirect = "https://release-assets.githubusercontent.com/fixture";
        using var portableFeed = new HttpClient(new FixtureHandler(_ => Feed(portableName, "sha256:" + hash, expected.Length)));
        using var installFeed = new HttpClient(new FixtureHandler(_ => Feed(installedName, "sha256:" + hash, expected.Length)));
        using var unknownFeed = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var deniedFeed = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var unsafeFeed = new HttpClient(new FixtureHandler(_ => Feed(portableName, "", expected.Length)));
        try
        {
            var package = await UpdateDownload.CheckAsync("0.2.6-replacement", false, default, portableFeed).ConfigureAwait(false);
            Check(package is { Version: "0.2.7", FileName: portableName, Tag: "v0.2.7" } &&
                package.Size == expected.Length && package.Sha256 == hash, "便携包应使用正式版本和 GitHub 摘要");
            var installed = await UpdateDownload.CheckAsync("0.2.6", true, default, installFeed).ConfigureAwait(false);
            Check(installed?.FileName == installedName, "安装版使用安装包");
            Check(await UpdateDownload.CheckAsync("0.2.7", false, default, portableFeed).ConfigureAwait(false) is null,
                "当前版本相同不应出现更新");
            Check(await UpdateDownload.CheckAsync("0.2.8", false, default, portableFeed).ConfigureAwait(false) is null,
                "不允许降级");
            Check(await UpdateDownload.CheckAsync("0.2.6", false, default, unknownFeed).ConfigureAwait(false) is null,
                "没有发布版本时不生成下载");
            Check(await RejectAsync<HttpRequestException>(() => UpdateDownload.CheckAsync("0.2.6", false, default, deniedFeed)).ConfigureAwait(false),
                "网络限流应提示错误");
            Check(await RejectAsync<InvalidDataException>(() => UpdateDownload.CheckAsync("0.2.6", true, default, portableFeed)).ConfigureAwait(false),
                "安装版没有匹配包时拒绝便携包");
            Check(await RejectAsync<InvalidDataException>(() => UpdateDownload.CheckAsync("0.2.6", false, default, unsafeFeed)).ConfigureAwait(false),
                "没有 SHA-256 的发布包不能使用");

            using var wrongRedirect = new HttpClient(new FixtureHandler(_ => Redirect("https://example.com/fixture")));
            Check(await RejectAsync<InvalidDataException>(() => UpdateDownload.StageAsync(package!, root, default, wrongRedirect)).ConfigureAwait(false),
                "禁止将更新下载重定向至第三方");
            using var wrongContent = new HttpClient(new FixtureHandler(_ => Bytes(new byte[expected.Length])));
            Check(await RejectAsync<InvalidDataException>(() => UpdateDownload.StageAsync(package!, root, default, wrongContent)).ConfigureAwait(false),
                "相同长度的损坏数据也不能通过校验");
            var updates = Path.Combine(root, "updates");
            Check(!Directory.EnumerateFiles(updates).Any(), "损坏的下载不得留下目标或临时文件");

            using var download = new HttpClient(new FixtureHandler(request =>
                request.RequestUri?.AbsoluteUri == $"https://github.com/JoshuaAWong/zhulong/releases/download/v0.2.7/{portableName}"
                    ? Redirect(redirect)
                    : request.RequestUri?.AbsoluteUri == redirect
                        ? Bytes(expected)
                        : throw new InvalidOperationException("更新下载地址不正确")));
            var path = await UpdateDownload.StageAsync(package!, root, default, download).ConfigureAwait(false);
            Check(path == Path.Combine(updates, portableName) && File.ReadAllBytes(path).SequenceEqual(expected),
                "完整校验后，才在 updates 留下对应格式的文件");
            using var unusedDownload = new HttpClient(new FixtureHandler(_ => throw new InvalidOperationException("复用已校验的包时无需联网")));
            Check(await UpdateDownload.StageAsync(package!, root, default, unusedDownload).ConfigureAwait(false) == path,
                "长度和摘要都正确的已暂存包应直接复用");
            File.WriteAllBytes(path, new byte[expected.Length]);
            Check(await RejectAsync<InvalidDataException>(() => UpdateDownload.StageAsync(package!, root, default, wrongContent)).ConfigureAwait(false) &&
                !File.ReadAllBytes(path).SequenceEqual(expected), "应拒绝复用损坏的暂存包");
            Check(await UpdateDownload.StageAsync(package!, root, default, download).ConfigureAwait(false) == path &&
                File.ReadAllBytes(path).SequenceEqual(expected), "重新下载应替换损坏的暂存包");

            // Windows 未授权创建符号链接时，由本机目录联接单独覆盖相同拦截路径。
            var linkedProgram = Path.Combine(root, "linked-program");
            if (CanCreateLink(() => Directory.CreateSymbolicLink(linkedProgram, updates)))
            {
                try
                {
                    Check(await RejectAsync<InvalidDataException>(() => UpdateDownload.StageAsync(package!, linkedProgram, default, unusedDownload)).ConfigureAwait(false),
                        "程序目录为链接时应在联网前拒绝");
                }
                finally { Directory.Delete(linkedProgram); }
            }
            var otherProgram = Directory.CreateDirectory(Path.Combine(root, "other-program")).FullName;
            var linkedUpdates = Path.Combine(otherProgram, "updates");
            if (CanCreateLink(() => Directory.CreateSymbolicLink(linkedUpdates, updates)))
            {
                try
                {
                    Check(await RejectAsync<InvalidDataException>(() => UpdateDownload.StageAsync(package!, otherProgram, default, unusedDownload)).ConfigureAwait(false),
                        "updates 为链接时不得复用其指向的包");
                }
                finally { Directory.Delete(linkedUpdates); }
            }
            var localPackage = Path.Combine(root, portableName);
            File.Move(path, localPackage);
            if (CanCreateLink(() => File.CreateSymbolicLink(path, localPackage)))
            {
                try
                {
                    Check(await RejectAsync<InvalidDataException>(() => UpdateDownload.StageAsync(package!, root, default, unusedDownload)).ConfigureAwait(false),
                        "正好匹配摘要的文件链接同样不能复用");
                }
                finally { File.Delete(path); }
            }
            return assertions;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static bool CanCreateLink(Func<FileSystemInfo> create)
    {
        try { create(); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    static HttpResponseMessage Feed(string name, string digest, int size) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            tag_name = "v0.2.7", prerelease = false, draft = false,
            assets = new[] { new { name, digest, size, state = "uploaded" } }
        }))
    };

    static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }
}
