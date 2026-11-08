using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Zhulong.Desktop;

// 按用户打开浏览器的动作启动Kestrel；不开放配置写或任意静态目录。
internal sealed class LocalPortal : IAsyncDisposable
{
    static readonly HashSet<string> Files = new(StringComparer.Ordinal)
    { "index.html", "styles.css", "ink-ui.css", "ink-layouts.css", "ink-atmospheres.css", "ink-taiji.css", "ink-unified.css", "ink-tray.css", "app.js", "ink-ui.js", "ink-layouts.js", "ink-atmospheres.js", "ink-taiji.js", "ink-unified.js", "ink-taiji-control.js", "ink-tray.js", "live.js", "live.css", "portal.js", "assets/ink-day-v7.png", "assets/ink-night-v7.png", "assets/ink-header-day-v3.png", "assets/ink-header-night-v3.png", "assets/zhulong-coiled-mark.svg", "assets/zhulong-next.svg" };
    static readonly HashSet<string> Queries = new(StringComparer.Ordinal)
    { "snapshot", "history", "events", "usage", "refresh-usage", "health", "actions" };
    readonly CancellationTokenSource stopped = new();
    readonly SemaphoreSlim slots = new(8, 8);
    readonly object gate = new();
    readonly HashSet<Task> requests = [];
    readonly DataHub host;
    readonly string web;
    readonly Func<string, Task<object?>> confirmAction;
    readonly string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    WebApplication? application;
    CancellationToken serverStopped;
    Task? starting, disposing;
    public string Address { get; } = "http://127.0.0.1:17631/";

    public LocalPortal(DataHub host, string web, Func<string, Task<object?>> confirmAction)
    { this.host = host; this.web = web; this.confirmAction = confirmAction; }

    // 启动/停止都在无UI上下文的异步路径执行，允许既有WPF入口同步调用。
    public void Start() => StartAsync().GetAwaiter().GetResult();
    public Task StartAsync()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposing is not null, this);
            return starting ??= Task.Run(StartCoreAsync);
        }
    }
    async Task StartCoreAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        { Args = [], ContentRootPath = Path.GetFullPath(web), EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, 17631, endpoint => endpoint.Protocols = HttpProtocols.Http1);
            options.Limits.MaxRequestBodySize = 16384;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.MaxRequestHeadersTotalSize = 8192;
            options.Limits.MaxRequestLineSize = 2048;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.MinRequestBodyDataRate = new MinDataRate(128, TimeSpan.FromSeconds(5));
            options.Limits.MinResponseDataRate = new MinDataRate(128, TimeSpan.FromSeconds(5));
        });
        var app = builder.Build();
        application = app;
        serverStopped = app.Lifetime.ApplicationStopped;
        app.Run(HandleAsync);
        try { await app.StartAsync(stopped.Token).ConfigureAwait(false); }
        catch { await app.DisposeAsync().ConfigureAwait(false); application = null; throw; }
    }

    async Task HandleAsync(HttpContext context)
    {
        Headers(context.Response);
        if (stopped.IsCancellationRequested) { context.Abort(); return; }
        if (!slots.Wait(0))
        { await ErrorAsync(context, null, "本地请求繁忙，请稍后再试", 429).ConfigureAwait(false); return; }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token, context.RequestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        using var budgetExpired = budget.Token.Register(context.Abort);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) requests.Add(completion.Task);
        try { await ServeAsync(context, budget).ConfigureAwait(false); }
        finally
        {
            try
            {
                using var finish = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                finish.CancelAfter(TimeSpan.FromSeconds(1));
                try { await context.Response.CompleteAsync().WaitAsync(finish.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException) { context.Abort(); }
            }
            finally
            {
                try { slots.Release(); }
                finally { lock (gate) requests.Remove(completion.Task); completion.TrySetResult(); }
            }
        }
    }

    async Task ServeAsync(HttpContext context, CancellationTokenSource budget)
    {
        int? id = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        // 读取/查询14秒，最多再用1秒发错误；到期中止实际连接，避免仅取消等待。
        deadline.CancelAfter(TimeSpan.FromSeconds(14));
        using var expires = deadline.Token.Register(context.Abort);
        try
        {
            var request = context.Request; var response = context.Response;
            if (request.Host.Value != "127.0.0.1:17631" || context.Connection.RemoteIpAddress is not { } remote ||
                !IPAddress.IsLoopback(remote) || request.Headers.TryGetValue("Origin", out var origin) && origin != Address.TrimEnd('/'))
            { await ErrorAsync(context, null, "来源校验未通过", 403).ConfigureAwait(false); return; }
            var path = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "";
            if (path != request.Path.Value || request.QueryString.HasValue)
            { await ErrorAsync(context, null, "路径不在允许范围", 404).ConfigureAwait(false); return; }
            if (request.Method == "GET" && path == "/api/session")
            { await JsonAsync(response, new { kind = "init", token = nonce }, deadline.Token).ConfigureAwait(false); return; }
            if (request.Method == "POST" && path is "/api/query" or "/api/action")
            {
                if (request.Headers["X-Zhulong-CSRF"] != nonce)
                { await ErrorAsync(context, null, "会话校验未通过", 403).ConfigureAwait(false); return; }
                if (request.ContentLength is null or < 0 or > 16384)
                { await ErrorAsync(context, null, "请求体长度不受支持", 413).ConfigureAwait(false); return; }
                var bytes = new byte[(int)request.ContentLength.Value];
                await request.Body.ReadExactlyAsync(bytes, deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes);
                var message = document.RootElement;
                id = message.GetProperty("id").GetInt32();
                var api = message.GetProperty("api").GetString()!;
                var metric = message.TryGetProperty("metric", out var m) ? m.GetString() : null;
                var hours = message.TryGetProperty("hours", out var h) ? h.GetInt32() : 24;
                object? result;
                if (path == "/api/action")
                {
                    if (api != "action" || string.IsNullOrWhiteSpace(metric))
                    { await ErrorAsync(context, id, "动作不在允许范围", 400).ConfigureAwait(false); return; }
                    deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                    budget.CancelAfter(Timeout.InfiniteTimeSpan);
                    result = await confirmAction(metric).WaitAsync(budget.Token).ConfigureAwait(false);
                    budget.CancelAfter(TimeSpan.FromSeconds(15));
                    await JsonAsync(response, new { id, result }, budget.Token).ConfigureAwait(false);
                    return;
                }
                if (!Queries.Contains(api))
                { await ErrorAsync(context, id, "查询不在允许范围", 400).ConfigureAwait(false); return; }
                result = await host.QueryAsync(api, metric, hours).WaitAsync(deadline.Token).ConfigureAwait(false);
                await JsonAsync(response, new { id, result }, deadline.Token).ConfigureAwait(false);
                return;
            }
            var name = path == "/" ? "index.html" : path.StartsWith('/') ? path[1..] : "";
            if (request.Method != "GET" || !Files.Contains(name))
            { await ErrorAsync(context, null, "文件或入口不在允许范围", 404).ConfigureAwait(false); return; }
            response.ContentType = Path.GetExtension(name) switch
            { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8", ".svg" => "image/svg+xml", ".png" => "image/png", _ => "application/octet-stream" };
            await using var file = new FileStream(Path.Combine(web, name), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            response.ContentLength = file.Length;
            await file.CopyToAsync(response.Body, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            await ErrorAsync(context, id, error is OperationCanceledException ?
                stopped.IsCancellationRequested ? "请求已取消" : "请求超时或连接已关闭" : error.Message,
                error is OperationCanceledException ? 504 : 400).ConfigureAwait(false);
        }
    }

    static void Headers(HttpResponse response)
    {
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Cache-Control"] = "no-store";
    }
    static async Task JsonAsync(HttpResponse response, object value, CancellationToken token)
    {
        response.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Program.Json);
        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes, token).AsTask().WaitAsync(token).ConfigureAwait(false);
    }
    async Task ErrorAsync(HttpContext context, int? id, string error, int status)
    {
        using var write = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token, context.RequestAborted);
        write.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.StatusCode = status;
            await JsonAsync(context.Response, new { id, error }, write.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { context.Abort(); }
    }

    public ValueTask DisposeAsync()
    { lock (gate) return new(disposing ??= Task.Run(DisposeCoreAsync)); }
    async Task DisposeCoreAsync()
    {
        stopped.Cancel();
        try
        {
            if (starting is not null)
                try { await starting.ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException) { }
            if (application is { } app)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await app.StopAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            Task[] active; lock (gate) active = requests.ToArray();
            try { await Task.WhenAll(active).ConfigureAwait(false); }
            finally
            {
                if (application is { } app) await app.DisposeAsync().ConfigureAwait(false);
                slots.Dispose(); stopped.Dispose();
            }
        }
    }

    public static async Task<int> RunSafetyTestsAsync(DataHub host, string web, string output)
    {
        Directory.CreateDirectory(output);
        var fakeWeb = Path.Combine(output, "portal-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeWeb);
        await File.WriteAllTextAsync(Path.Combine(fakeWeb, "index.html"), "<!doctype html><title>fixture</title>").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(fakeWeb, "app.js"), "// fixture").ConfigureAwait(false);
        var assertions = 0; var callbacks = 0;
        var held = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(bool ok, string name) { assertions++; if (!ok) throw new InvalidOperationException(name); }
        await using var portal = new LocalPortal(host, fakeWeb, id =>
        { Interlocked.Increment(ref callbacks); return id == "hold" ? held.Task : Task.FromResult<object?>(new { status = "cancelled" }); });
        var clients = new List<TcpClient>();
        try
        {
            Check(portal.application is null && portal.starting is null, "构造不应提前创建WebApplication或后台启动任务");
            var idle = new TcpListener(IPAddress.Loopback, 17631);
            try { idle.Start(); Check(true, "显式Start前固定端口应可绑定"); } finally { idle.Stop(); }
            portal.Start();
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
            http.BaseAddress = new Uri(portal.Address);
            using var session = await http.GetAsync("api/session").ConfigureAwait(false);
            Check(session.StatusCode == HttpStatusCode.OK, "本地会话入口应可访问");
            Check(session.Headers.GetValues("X-Frame-Options").Single() == "DENY", "会话响应必须拒绝框架嵌入");
            using var sessionJson = JsonDocument.Parse(await session.Content.ReadAsStringAsync().ConfigureAwait(false));
            var token = sessionJson.RootElement.GetProperty("token").GetString()!;
            async Task<HttpResponseMessage> Post(string api, string? origin = null, string? csrf = null, string endpoint = "api/query", string metric = "fixture")
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Add("X-Zhulong-CSRF", csrf ?? token);
                if (origin is not null) request.Headers.Add("Origin", origin);
                request.Content = new StringContent(JsonSerializer.Serialize(new { id = 71, api, metric }), Encoding.UTF8, "application/json");
                return await http.SendAsync(request).ConfigureAwait(false);
            }
            using (var valid = await Post("health").ConfigureAwait(false))
            {
                Check(valid.StatusCode == HttpStatusCode.OK, "合法查询应成功");
                using var body = JsonDocument.Parse(await valid.Content.ReadAsStringAsync().ConfigureAwait(false));
                Check(body.RootElement.GetProperty("id").GetInt32() == 71, "成功响应须返回请求标识");
            }
            using (var wrong = await Post("health", "http://evil.invalid").ConfigureAwait(false)) Check(wrong.StatusCode == HttpStatusCode.Forbidden, "错误Origin应拒绝");
            using (var wrong = await Post("health", csrf: "wrong").ConfigureAwait(false)) Check(wrong.StatusCode == HttpStatusCode.Forbidden, "错误nonce应拒绝");
            using (var wrong = await Post("config-write").ConfigureAwait(false))
            {
                Check(wrong.StatusCode == HttpStatusCode.BadRequest, "未授权查询应拒绝");
                using var body = JsonDocument.Parse(await wrong.Content.ReadAsStringAsync().ConfigureAwait(false));
                Check(body.RootElement.GetProperty("id").GetInt32() == 71 && body.RootElement.TryGetProperty("error", out _), "查询错误必须含id和error");
            }
            using (var wrong = await Post("action").ConfigureAwait(false)) Check(wrong.StatusCode == HttpStatusCode.BadRequest && callbacks == 0, "查询通道不能执行动作");
            using (var action = await Post("action", endpoint: "api/action").ConfigureAwait(false))
            {
                using var body = JsonDocument.Parse(await action.Content.ReadAsStringAsync().ConfigureAwait(false));
                Check(body.RootElement.GetProperty("result").GetProperty("status").GetString() == "cancelled" && callbacks == 1, "动作必须经过确认回调并保留取消结果");
            }
            using (var asset = await http.GetAsync("app.js").ConfigureAwait(false))
            {
                Check(asset.IsSuccessStatusCode && await asset.Content.ReadAsStringAsync().ConfigureAwait(false) == "// fixture", "白名单静态文件必须正确返回");
                Check(asset.Headers.GetValues("X-Frame-Options").Single() == "DENY", "静态文件必须包含统一保护头");
            }
            using (var missing = await http.GetAsync("secrets.txt").ConfigureAwait(false))
            {
                Check(missing.StatusCode == HttpStatusCode.NotFound, "未知文件应拒绝");
                Check(missing.Headers.GetValues("X-Frame-Options").Single() == "DENY", "错误响应也必须拒绝框架嵌入");
            }
            async Task<string> Raw(string target, string hostHeader)
            {
                using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, 17631).ConfigureAwait(false);
                var stream = client.GetStream(); var data = Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: {hostHeader}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(data).ConfigureAwait(false);
                using var reader = new StreamReader(stream); return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false) ?? "";
            }
            Check(!(await Raw("/api/session", "evil.invalid:17631").ConfigureAwait(false)).Contains(" 200 "), "错误Host不能取得会话");
            Check(!(await Raw("/assets/../app.js", "127.0.0.1:17631").ConfigureAwait(false)).Contains(" 200 "), "路径遍历不能归一化为白名单文件");
            for (var i = 0; i < 8; i++)
            {
                var client = new TcpClient(); clients.Add(client); await client.ConnectAsync(IPAddress.Loopback, 17631).ConfigureAwait(false);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"POST /api/query HTTP/1.1\r\nHost: 127.0.0.1:17631\r\nX-Zhulong-CSRF: {token}\r\nContent-Length: 100\r\nConnection: close\r\n\r\n{{")).ConfigureAwait(false);
            }
            var until = DateTime.UtcNow.AddSeconds(3);
            while (portal.slots.CurrentCount != 0 && DateTime.UtcNow < until) await Task.Delay(10).ConfigureAwait(false);
            Check(portal.slots.CurrentCount == 0, "八份未完成请求应占满八个槽位");
            using (var busy = await http.GetAsync("api/session").ConfigureAwait(false))
            {
                Check((int)busy.StatusCode == 429, "第九请求必须直接拒绝，不能排队");
                Check(busy.Headers.GetValues("X-Frame-Options").Single() == "DENY", "限流响应必须包含统一保护头");
            }
            until = DateTime.UtcNow.AddSeconds(17);
            while (portal.slots.CurrentCount != 8 && DateTime.UtcNow < until) await Task.Delay(25).ConfigureAwait(false);
            Check(portal.slots.CurrentCount == 8, "超时或断连后所有槽位必须释放");
            using (var recovered = await http.GetAsync("api/session").ConfigureAwait(false)) Check(recovered.IsSuccessStatusCode, "超时后新请求必须恢复服务");
            using (var disconnected = new TcpClient())
            {
                await disconnected.ConnectAsync(IPAddress.Loopback, 17631).ConfigureAwait(false);
                await disconnected.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"POST /api/query HTTP/1.1\r\nHost: 127.0.0.1:17631\r\nX-Zhulong-CSRF: {token}\r\nContent-Length: 100\r\n\r\n{{")).ConfigureAwait(false);
                until = DateTime.UtcNow.AddSeconds(3);
                while (portal.slots.CurrentCount != 7 && DateTime.UtcNow < until) await Task.Delay(10).ConfigureAwait(false);
                Check(portal.slots.CurrentCount == 7, "断连夹具必须实际占用请求槽位");
                disconnected.Dispose();
                until = DateTime.UtcNow.AddSeconds(3);
                while (portal.slots.CurrentCount != 8 && DateTime.UtcNow < until) await Task.Delay(10).ConfigureAwait(false);
                Check(portal.slots.CurrentCount == 8, "客户端主动断连不能跳过槽位释放");
            }
            var holding = Post("action", endpoint: "api/action", metric: "hold");
            until = DateTime.UtcNow.AddSeconds(3);
            while (Volatile.Read(ref callbacks) != 2 && DateTime.UtcNow < until) await Task.Delay(10).ConfigureAwait(false);
            Check(callbacks == 2, "必须实际进入等待中的确认回调");
            await Task.Delay(15050).ConfigureAwait(false);
            Check(!holding.IsCompleted && portal.slots.CurrentCount == 7, "原生确认等待不应被普通读取期限中断");
            await portal.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            try { using var ignored = await holding.ConfigureAwait(false); } catch (HttpRequestException) { }
            lock (portal.gate) Check(portal.requests.Count == 0, "停止必须收束全部请求");
            Check(portal.serverStopped.IsCancellationRequested, "停止必须收束接受循环");
            held.TrySetResult(new { status = "cancelled" });
            await File.WriteAllTextAsync(Path.Combine(output, "portal-safety.json"), JsonSerializer.Serialize(new { status = "PASS", assertions, callbacks, address = portal.Address, timestamp = DateTimeOffset.UtcNow }, Program.Json)).ConfigureAwait(false);
            return assertions;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "portal-safety.json"), JsonSerializer.Serialize(new { status = "FAIL", assertions, error = error.ToString(), timestamp = DateTimeOffset.UtcNow }, Program.Json)).ConfigureAwait(false);
            throw;
        }
        finally { foreach (var client in clients) client.Dispose(); held.TrySetResult(new { status = "cancelled" }); }
    }
}
