using System.Net;
using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Playback;
using VidLog.Desktop.Core.Search;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Web;

/// <summary>局域网回放服务的配置。</summary>
public sealed record PlaybackServerOptions
{
    /// <summary>
    /// 绑定的 URL 前缀。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本机调试用 <c>http://localhost:8720/</c>（不需要管理员权限）。
    /// 要让**别的设备**能打开就得用 <c>http://+:8720/</c>，
    /// 而 Windows 上绑定通配符前缀需要一次性注册：
    /// </para>
    /// <code>
    /// netsh http add urlacl url=http://+:8720/ user=Everyone
    /// </code>
    /// <para>
    /// 这条命令需要管理员权限，应当由安装程序完成一次 —— 别让用户自己敲。
    /// 选 <see cref="HttpListener"/> 而不是 ASP.NET Core 就是为了这个：
    /// 它用的是系统自带的 http.sys，不给一个 WPF 桌面应用凭空加上
    /// 「必须装 ASP.NET Core 运行时」这种部署要求。
    /// </para>
    /// </remarks>
    public string Prefix { get; init; } = "http://localhost:8720/";

    /// <summary>
    /// <see cref="Prefix"/> 绑不上时的退路。为 <see langword="null"/> 表示不退。
    /// </summary>
    /// <remarks>
    /// 典型用法：<see cref="Prefix"/> 用 <c>http://+:8720/</c>（局域网可达），
    /// 回退到 <c>http://localhost:8720/</c>（一定绑得上）。
    /// <para>
    /// 为什么要退而不是直接失败：没注册 urlacl 时局域网前缀会抛异常，
    /// 而「回放服务起不来」不该让整个应用不可用 —— 用户至少还能在本机看回放，
    /// 界面同时告诉他怎么把局域网打开。
    /// </para>
    /// </remarks>
    public string? FallbackPrefix { get; init; }

    /// <summary>归档根目录 —— 索引里的相对路径相对它解析。</summary>
    public string ArchiveRoot { get; init; } = string.Empty;
}

/// <summary>
/// 局域网网页回放（规格 §3.8）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ **这不是规格 §3.7 的「可分享的证据链接」，两者不要混为一谈。**
/// </para>
/// <list type="bullet">
/// <item>本服务是给**操作者自己**在内网看回放用的，直接读本机归档目录。</item>
/// <item>§3.7 的证据链接是发给**外部**（客户、平台、法庭）的，
/// 必须由服务端签发并指向**归档层**（不变量 I7：分享链接永不指向本地副本）。</item>
/// </list>
/// <para>
/// 所以本服务的 URL **不得**被当作证据链接对外发送 —— 本地副本会被生命周期清理
/// （规格 §3.5），发出去就是一条迟早失效的链接。
/// </para>
/// <para>
/// 本服务同样**不依赖许可状态**：许可设计 §5 要求历史录像始终可查看、检索、回放。
/// </para>
/// </remarks>
public sealed class PlaybackServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly PlaybackServerOptions _options;
    private readonly RecordingSearch _search;
    private readonly IRecordingIndex _index;
    private readonly PunchNavigation _punches;
    private readonly UploadReceiver _upload;
    private readonly DeviceRegistry _devices;
    private readonly string _deviceName;
    private readonly IAppLogger _logger;
    private HttpListener _listener = new();

    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;

    /// <param name="logger">
    /// 请求日志。**可选**（默认不记）—— 加可选参数而不是必填，
    /// 是为了让那几十处测试的构造调用一行都不用改。
    /// </param>
    public PlaybackServer(
        PlaybackServerOptions options,
        RecordingSearch search,
        IRecordingIndex index,
        PunchNavigation punches,
        UploadReceiver upload,
        DeviceRegistry devices,
        string deviceName,
        IAppLogger? logger = null)
    {
        _options = options;
        _search = search;
        _index = index;
        _punches = punches;
        _upload = upload;
        _devices = devices;
        _deviceName = deviceName;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>实际绑上的地址（可能不是配置里的首选地址 —— 见 <see cref="PlaybackServerOptions.FallbackPrefix"/>）。</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>是否退回到了备用地址。</summary>
    public bool IsUsingFallback { get; private set; }

    /// <summary>绑不上首选地址时的原因，供界面告知用户。没有回退过时为 <see langword="null"/>。</summary>
    public string? FallbackReason { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (TryBind(_options.Prefix, out var firstError))
        {
            BaseUrl = _options.Prefix;
        }
        else if (_options.FallbackPrefix is not null && TryBind(_options.FallbackPrefix, out _))
        {
            BaseUrl = _options.FallbackPrefix;
            IsUsingFallback = true;
            FallbackReason = firstError;
        }
        else
        {
            throw new InvalidOperationException($"无法绑定回放地址：{firstError}");
        }

        _loopCancellation = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_loopCancellation.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 试着绑一个前缀。成功返回 true。
    /// </summary>
    /// <remarks>
    /// **每次都用全新的 <see cref="HttpListener"/>。** 绑定失败过的实例不能复用：
    /// <c>Start()</c> 抛异常之后，它内部的状态既不是「已启动」也不是「干净」，
    /// 再改前缀或再 Start 都不可靠。踩过一次。
    /// </remarks>
    private bool TryBind(string prefix, out string? error)
    {
        var candidate = new HttpListener();
        candidate.Prefixes.Add(prefix);

        try
        {
            candidate.Start();
            _listener = candidate;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;

            try
            {
                candidate.Close();
            }
            catch (Exception)
            {
                // 关不掉也没关系，它没绑上任何东西。
            }

            return false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_loopCancellation is not null)
        {
            await _loopCancellation.CancelAsync();
        }

        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // 停不下来不该让调用方卡住 —— 进程退出会收掉它。
            }
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    /// <summary>
    /// 一个请求的进与出。**这是全站唯一的单一入口/出口** ——
    /// 请求日志挂在这里，一处就覆盖所有路由。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 2026-09-26 之前这里**一行日志都没有**，而 <c>:catch</c> 是
    /// **静默吞掉**的（只回 500）—— 于是「手机传不上来」这种现象在电脑端
    /// 完全无迹可查，只能靠手机那边的提示猜。
    /// </para>
    /// <para>
    /// <c>Trace.Start()</c> 必须在这里设：AsyncLocal 的流动是向下的，
    /// 在里面设了外面看不见（见 <see cref="Trace"/>）。
    /// </para>
    /// </remarks>
    private async Task HandleAsync(HttpListenerContext context)
    {
        var trace = Trace.Start();
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;

        try
        {
            if (path is "/" or "/index.html")
            {
                await WriteHtmlAsync(context, BuildPage());
                return;
            }

            if (path == "/api/search")
            {
                await WriteSearchAsync(context);
                return;
            }

            if (path == "/api/punches")
            {
                await WritePunchesAsync(context);
                return;
            }

            if (path.StartsWith("/api/v1/", StringComparison.Ordinal))
            {
                await HandleV1Async(context, path);
                return;
            }

            if (path.StartsWith("/media/", StringComparison.Ordinal))
            {
                await WriteMediaAsync(context, Uri.UnescapeDataString(path["/media/".Length..]));
                return;
            }

            context.Response.StatusCode = 404;
        }
        catch (Exception ex)
        {
            // ⚠️ **必须记下来**（含完整堆栈）。这里以前是空 catch：
            // 手机那边只看到「500」，而电脑端连出了什么事都不知道。
            _logger.Log(LogLevel.Error, "回放", $"{method} {path} 处理失败", new Dictionary<string, object?>
            {
                ["method"] = method,
                ["path"] = path,
                ["异常"] = ex.ToString(),
            });

            // 单个请求出错不能拖垮服务；能回 500 就回。
            try
            {
                context.Response.StatusCode = 500;
            }
            catch (Exception)
            {
                // 连接已经断了，忽略。
            }
        }
        finally
        {
            // 出口记一行。**失败的那些也要记**（上面 catch 之后仍会走到这里）——
            // 「哪个路径在报错」正是靠状态码看出来的。
            //
            // ⚠️ 只记出口、不另记一条入口：出口那行已经有方法、路径、状态与耗时，
            // 入口行提供不了新信息，只会把量翻一倍。真要在半路崩掉时看「进没进来」，
            // 还有 `trace` 可以把这一串行串起来。
            _logger.Log(
                context.Response.StatusCode >= 400 ? LogLevel.Warn : LogLevel.Info,
                "回放",
                $"{method} {path} → {context.Response.StatusCode}",
                new Dictionary<string, object?>
                {
                    // 四个**可筛的字段**，而不是把状态码塞在消息里：
                    // 「昨天有没有 5xx」「哪个路径最慢」这种问题要能一句话问出来。
                    ["method"] = method,
                    ["path"] = path,
                    ["status"] = context.Response.StatusCode,
                    ["耗时ms"] = Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 1),
                });

            Trace.Clear();

            try
            {
                context.Response.Close();
            }
            catch (Exception)
            {
                // 同上。
            }
        }
    }

    // ───────────── /api/v1 · 手机端上传（M5） ─────────────

    /// <summary>
    /// M5 的上传与入网接口。形状见母仓 <c>docs/05-上传接口形状.md</c>。
    /// </summary>
    /// <remarks>
    /// 老的三条 GET 路由（<c>/api/search</c> 等）**原样不动** —— 它们服务的网页回放已经验收过，
    /// 「不动它」比「统一它」便宜。版本前缀 <c>v1</c> 只加在新接口上。
    /// </remarks>
    private async Task HandleV1Async(HttpListenerContext context, string path)
    {
        var isPost = string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase);

        if (path == "/api/v1/health" && !isPost)
        {
            await WriteJsonAsync(
                context,
                new HealthPayload(HealthPayload.ServiceName, HealthPayload.Version, _deviceName));
            return;
        }

        if (!isPost)
        {
            await WriteErrorAsync(context, 404, UploadErrors.NotFound, "这个路径只接受 POST");
            return;
        }

        switch (path)
        {
            case "/api/v1/enroll/request":
                await GuardAsync(context, () => HandleEnrollRequestAsync(context));
                return;

            case "/api/v1/enroll/claim":
                await GuardAsync(context, () => HandleEnrollClaimAsync(context));
                return;
        }

        // 入网之外一律要凭据。
        var auth = await AuthenticateAsync(context);
        if (auth is null)
        {
            await WriteErrorAsync(context, 401, UploadErrors.BadCredential, "凭据无效，请在手机端重新配对电脑");
            return;
        }

        // 取成局部量再进 lambda：可空元组在里面不会被收窄，而设备身份**必须**来自凭据。
        var deviceId = auth.Value.Device.DeviceId;
        var credential = auth.Value.Credential;

        switch (path)
        {
            case "/api/v1/upload/probe":
                await GuardAsync(context, async () =>
                {
                    var request = await ReadJsonAsync<ProbeRequest>(context);
                    if (request is null)
                    {
                        await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "请求体为空");
                        return;
                    }

                    await WriteJsonAsync(context, await _upload.ProbeAsync(request));
                });
                return;

            case "/api/v1/upload/commit":
                await GuardAsync(context, async () =>
                {
                    var request = await ReadJsonAsync<CommitRequest>(context);
                    if (request is null)
                    {
                        await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "请求体为空");
                        return;
                    }

                    await WriteJsonAsync(context, await _upload.CommitAsync(request, deviceId, credential));
                });
                return;
        }

        if (path.StartsWith("/api/v1/upload/chunk/", StringComparison.Ordinal))
        {
            await GuardAsync(context, () => HandleChunkAsync(context, path["/api/v1/upload/chunk/".Length..]));
            return;
        }

        await WriteErrorAsync(context, 404, UploadErrors.NotFound, $"电脑端不认识这个路径：{path}");
    }

    private async Task HandleEnrollRequestAsync(HttpListenerContext context)
    {
        var request = await ReadJsonAsync<EnrollRequestPayload>(context);
        if (request is null)
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "请求体为空");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.DeviceName))
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "deviceId / deviceName 不得为空");
            return;
        }

        // ⚠️ **不自动批准**（规格 §3.4.5：入网必须经主机端人工批准）。
        // 这里只记下"谁来了"，并**顺带告诉它现在批没批** ——
        // 手机轮询的就是这个接口，它**只报警不发货**（凭据在 claim 那一步才产出）。
        var result = await _devices.RequestAsync(request.DeviceId, request.DeviceName, request.Token);

        switch (result.Status)
        {
            case EnrollStatus.Pending:
                await WriteJsonAsync(context, new EnrollPendingPayload(EnrollPendingPayload.Pending));
                return;

            case EnrollStatus.Approved:
                await WriteJsonAsync(context, new EnrollPendingPayload(EnrollPendingPayload.Approved));
                return;

            case EnrollStatus.Rejected:
                await WriteJsonAsync(context, new EnrollPendingPayload(EnrollPendingPayload.Rejected));
                return;

            case EnrollStatus.BadToken:
                await WriteErrorAsync(context, 403, UploadErrors.BadToken, result.Detail);
                return;

            default:
                // 屏幕上的码换了、或者已经超时 —— 手机该重新扫一次。
                await WriteErrorAsync(context, 410, UploadErrors.NoPendingRequest, result.Detail);
                return;
        }
    }

    private async Task HandleEnrollClaimAsync(HttpListenerContext context)
    {
        var request = await ReadJsonAsync<EnrollClaimPayload>(context);
        if (request is null)
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "请求体为空");
            return;
        }

        var result = await _devices.ClaimAsync(request.DeviceId, request.Token);

        switch (result.Status)
        {
            case EnrollStatus.Approved:
                await WriteJsonAsync(context, new EnrollCredentialPayload(result.Credential!));
                return;

            case EnrollStatus.Pending:
                // 还没批 —— **不是错误**，是流程里正常的一步。回 200 + pending，
                // 手机照着继续等。回 4xx 的话手机会把它当成"入网失败"。
                await WriteJsonAsync(context, new EnrollPendingPayload(EnrollPendingPayload.Pending));
                return;

            case EnrollStatus.Rejected:
                // 被拒了也要让手机看得见（规格 §3.4.5）。200 + rejected 而不是 4xx：
                // 这不是"请求坏了"，是"人做的决定"，手机那边要显示的是
                // 「电脑端拒绝了这次连接」，不是「网络错误」。
                await WriteJsonAsync(context, new EnrollPendingPayload(EnrollPendingPayload.Rejected));
                return;

            case EnrollStatus.BadToken:
                await WriteErrorAsync(context, 403, UploadErrors.BadToken, result.Detail);
                return;

            default:
                await WriteErrorAsync(context, 410, UploadErrors.NoPendingRequest, result.Detail);
                return;
        }
    }

    private async Task HandleChunkAsync(HttpListenerContext context, string tail)
    {
        var separator = tail.LastIndexOf('/');
        if (separator <= 0)
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "分片路径要是 <evidenceId>/<下标>");
            return;
        }

        var evidenceId = Uri.UnescapeDataString(tail[..separator]);

        if (!int.TryParse(tail[(separator + 1)..], out var index))
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "分片下标不是整数");
            return;
        }

        // ⚠️ 请求体是**裸字节**，不是 JSON —— 这里绝不能去反序列化它。
        var accepted = await _upload.StoreChunkAsync(evidenceId, index, context.Request.InputStream);
        await WriteJsonAsync(context, accepted);
    }

    /// <summary>
    /// 把 <see cref="UploadRejectedException"/> 翻成状态码。
    /// </summary>
    /// <remarks>
    /// 协议码 → 状态码的映射**只有这一处**。手机端按状态码分类重试与否（§3），
    /// 所以这张表改了就是改了协议。
    /// </remarks>
    private async Task GuardAsync(HttpListenerContext context, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (UploadRejectedException ex)
        {
            await WriteErrorAsync(
                context,
                ex.Code switch
                {
                    UploadErrors.BadRequest => 400,
                    UploadErrors.BadCredential => 401,
                    _ => 409,
                },
                ex.Code,
                ex.Message);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, $"报文读不出来：{ex.Message}");
        }
    }

    private async Task<(EnrolledDevice Device, string Credential)?> AuthenticateAsync(HttpListenerContext context)
    {
        const string scheme = "Bearer ";
        var header = context.Request.Headers["Authorization"];

        if (header is null || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var credential = header[scheme.Length..].Trim();
        if (credential.Length == 0)
        {
            return null;
        }

        var device = await _devices.FindByCredentialAsync(credential);

        return device is null ? null : (device, credential);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();

        return string.IsNullOrWhiteSpace(text)
            ? default
            : JsonSerializer.Deserialize<T>(text, JsonOptions);
    }


    private static async Task WriteErrorAsync(
        HttpListenerContext context,
        int status,
        string error,
        string? detail)
    {
        context.Response.StatusCode = status;
        await WriteJsonAsync(context, new ErrorPayload(error, detail));
    }

    private async Task WriteSearchAsync(HttpListenerContext context)
    {
        var query = context.Request.QueryString;

        var recordingQuery = new RecordingQuery
        {
            WaybillText = query["q"],
            MatchMode = query["mode"] switch
            {
                "prefix" => WaybillMatchMode.Prefix,
                "contains" => WaybillMatchMode.Contains,
                _ => WaybillMatchMode.Exact,
            },
            From = ParseInstant(query["from"]),
            To = ParseInstant(query["to"]),
            BusinessType = ParseBusinessType(query["type"]),
        };

        var hits = await _search.SearchAsync(recordingQuery);

        var payload = hits.Select(hit => new PlaybackSearchItem(
            hit.Entry.EvidenceId,
            hit.Entry.Waybill.Value,
            hit.Entry.StartedAt.ToString("O"),
            hit.Entry.Duration.TotalSeconds,
            hit.BusinessType?.ToString() ?? "unknown",
            // 规格 §3.1.7 的连带项：页面要如实告知「这条能不能在网页里播」。
            hit.Entry.Codec));

        await WriteJsonAsync(context, payload);
    }

    /// <summary>
    /// 某条证据所属会话的全部打点，已换算成「哪个文件、第几秒」。
    /// </summary>
    /// <remarks>
    /// 规格 §3.8 要求回放支持「跳转到打点位置」（§3.8）。
    /// 换算之所以要服务端做：打点是**会话内偏移**，而一次打包可能横跨多个分段文件，
    /// 浏览器拿到的只是单个文件的 URL，不知道跨段关系。
    /// </remarks>
    private async Task WritePunchesAsync(HttpListenerContext context)
    {
        var evidenceId = context.Request.QueryString["evidenceId"] ?? string.Empty;

        var targets = await _punches.ForEvidenceAsync(evidenceId);

        await WriteJsonAsync(context, targets);
    }

    private async Task WriteMediaAsync(HttpListenerContext context, string evidenceId)
    {
        var path = await ResolveEvidencePathAsync(evidenceId);

        if (path is null || !File.Exists(path))
        {
            context.Response.StatusCode = 404;
            return;
        }

        await WriteFileWithRangeAsync(context, path);
    }

    /// <summary>
    /// 把证据 id 解析成本机文件路径。
    /// </summary>
    /// <remarks>
    /// **只按索引里的相对路径解析，绝不拼接请求里的字符串** ——
    /// 否则 <c>/media/../../../windows/system32/config/sam</c> 就能读到任何文件。
    /// 解析完还要再确认落在归档根之内，挡住索引本身被污染的情况。
    /// </remarks>
    private async Task<string?> ResolveEvidencePathAsync(string evidenceId)
    {
        if (string.IsNullOrWhiteSpace(evidenceId) || string.IsNullOrEmpty(_options.ArchiveRoot))
        {
            return null;
        }

        var entries = await _index.LoadAllAsync();
        var entry = entries.FirstOrDefault(e => string.Equals(e.EvidenceId, evidenceId, StringComparison.Ordinal));
        if (entry is null)
        {
            return null;
        }

        var root = Path.GetFullPath(_options.ArchiveRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, entry.Location.Value));

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    private static async Task WriteFileWithRangeAsync(HttpListenerContext context, string filePath)
    {
        var file = new FileInfo(filePath);
        var total = file.Length;

        context.Response.ContentType = "video/mp4";
        context.Response.AddHeader("Accept-Ranges", "bytes");

        var rangeHeader = context.Request.Headers["Range"];
        long start = 0;
        var end = total - 1;

        if (!string.IsNullOrEmpty(rangeHeader) && TryParseRange(rangeHeader, total, out var rangeStart, out var rangeEnd))
        {
            start = rangeStart;
            end = rangeEnd;
            context.Response.StatusCode = 206;
            context.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{total}");
        }
        else
        {
            context.Response.StatusCode = 200;
        }

        var length = end - start + 1;
        context.Response.ContentLength64 = length;

        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);

        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        var remaining = length;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, toRead));
            if (read <= 0)
            {
                break;
            }

            await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }

    /// <summary>
    /// 解析单段 Range 请求头（浏览器拖进度条用的就是这种）。
    /// </summary>
    /// <remarks>
    /// 多段 Range（<c>bytes=0-99,200-299</c>）**刻意不支持** —— 浏览器播放视频只用单段。
    /// 不支持时退回整文件（200），仍然能播，只是不省流量。
    /// </remarks>
    private static bool TryParseRange(string header, long total, out long start, out long end)
    {
        start = 0;
        end = total - 1;

        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = header["bytes=".Length..].Trim();
        if (spec.Contains(','))
        {
            return false;
        }

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var startText = spec[..dash].Trim();
        var endText = spec[(dash + 1)..].Trim();

        // "bytes=-500" = 最后 500 字节
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, out var suffix) || suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, total - suffix);
            end = total - 1;
            return true;
        }

        if (!long.TryParse(startText, out start) || start < 0 || start >= total)
        {
            return false;
        }

        if (endText.Length > 0)
        {
            if (!long.TryParse(endText, out end) || end < start)
            {
                return false;
            }

            end = Math.Min(end, total - 1);
        }
        else
        {
            end = total - 1;
        }

        return true;
    }

    private static DateTimeOffset? ParseInstant(string? text) =>
        DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static BusinessType? ParseBusinessType(string? text) =>
        BusinessTypes.TryParse(text, out var parsed) ? parsed : null;

    private static async Task WriteJsonAsync(HttpListenerContext context, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task WriteHtmlAsync(HttpListenerContext context, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    /// <summary>回放页面。刻意保持单文件、零外部依赖 —— 局域网里没有 CDN 可访问。</summary>
    private static string BuildPage() => """
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>VidLog 回放</title>
        <style>
          :root { color-scheme: light dark; }
          body { font: 14px/1.6 system-ui, sans-serif; margin: 0; padding: 16px; max-width: 900px; }
          h1 { font-size: 18px; margin: 0 0 12px; }
          form { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 16px; }
          input, select, button { font: inherit; padding: 6px 8px; }
          input[type=text] { flex: 1 1 200px; }
          table { border-collapse: collapse; width: 100%; }
          th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid #8884; }
          tr.hit { cursor: pointer; }
          tr.hit:hover { background: #8882; }
          #player { width: 100%; background: #000; margin-top: 12px; display: none; }
          #punches { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 12px; align-items: center; }
          #punches button { padding: 4px 10px; }
          .muted { opacity: .7; }
          .note { margin-top: 24px; font-size: 12px; opacity: .6; }
        </style>
        </head>
        <body>
        <h1>VidLog 回放</h1>
        <form id="f">
          <input type="text" id="q" placeholder="单号" autocomplete="off">
          <select id="mode">
            <option value="exact">精确</option>
            <option value="prefix">前缀</option>
            <option value="contains">模糊</option>
          </select>
          <select id="type">
            <option value="">全部</option>
            <option value="outbound">发货</option>
            <option value="return">退货</option>
          </select>
          <input type="date" id="from">
          <input type="date" id="to">
          <button type="submit">查询</button>
        </form>
        <table>
          <thead><tr><th>单号</th><th>录制时间</th><th>时长</th><th>类型</th></tr></thead>
          <tbody id="rows"></tbody>
        </table>
        <div id="punches"></div>
        <video id="player" controls playsinline></video>
        <!-- 录制规格的如实告知（规格 §3.1.7 的连带项）。默认藏着，点开一条 H.265 的才出现。 -->
        <div id="codecNote" class="note" style="display:none"></div>
        <p class="note">
          本页仅供内网回放。它不是证据分享链接 —— 分享链接指向归档层，
          而这里的视频来自本机本地副本，本地副本可能已被生命周期清理。
        </p>
        <script>
        const rows = document.getElementById('rows');
        const player = document.getElementById('player');
        const punchBox = document.getElementById('punches');

        // 打点是**会话内偏移**，一次打包可能横跨多个分段文件 ——
        // 所以「跳到打点」要服务端换算成「哪个文件、第几秒」。
        async function loadPunches(evidenceId) {
          punchBox.innerHTML = '';
          const res = await fetch('/api/punches?evidenceId=' + encodeURIComponent(evidenceId));
          const targets = await res.json();

          if (targets.length === 0) {
            const span = document.createElement('span');
            span.className = 'muted';
            span.textContent = '这段录像没有打点';
            punchBox.appendChild(span);
            return;
          }

          const label = document.createElement('span');
          label.className = 'muted';
          label.textContent = '打点：';
          punchBox.appendChild(label);

          for (const t of targets) {
            const b = document.createElement('button');
            b.textContent = t.waybillNumber + ' · ' + Math.round(t.sessionOffsetSeconds) + 's';
            b.onclick = () => {
              player.style.display = 'block';
              player.src = '/media/' + encodeURIComponent(t.evidenceId);
              player.onloadedmetadata = () => {
                player.currentTime = t.offsetSeconds;
                player.play();
              };
            };
            punchBox.appendChild(b);
          }
        }

        function openRecording(evidenceId, codec) {
          player.style.display = 'block';
          player.src = '/media/' + encodeURIComponent(evidenceId);
          player.play();
          loadPunches(evidenceId);
          showCodecNote(codec);
        }

        // ⚠️ 规格 §3.1.7 的连带项：**浏览器对 H.265 的支持不一致**，
        // 所以网页**可能播不了 H.265 录的那条**。口径是「**如实告知**……**不得承诺
        // 做不到的事**，**不为此砍掉 H.265 选项**」—— 于是这里把话说出来，
        // 而不是让用户对着一个转圈的播放器自己猜。
        //
        // 只提一句「用系统播放器打开」：那是**肯定**能播的路径
        // （文件本来就在这台机器上），不承诺浏览器能播。
        function showCodecNote(codec) {
          const note = document.getElementById('codecNote');
          if (codec === 'H265') {
            note.textContent = '这条录像是 H.265 编码。浏览器对它的支持不一致 —— '
              + '要是这里放不出来，请用系统播放器打开这个文件（在上面那台电脑上，'
              + '或者把它下载下来）。录像本身没问题。';
            note.style.display = 'block';
          } else {
            note.style.display = 'none';
          }
        }

        async function run() {
          const p = new URLSearchParams();
          const q = document.getElementById('q').value.trim();
          if (q) p.set('q', q);
          p.set('mode', document.getElementById('mode').value);
          const t = document.getElementById('type').value;
          if (t) p.set('type', t);
          const from = document.getElementById('from').value;
          if (from) p.set('from', from + 'T00:00:00');
          const to = document.getElementById('to').value;
          if (to) p.set('to', to + 'T23:59:59');

          const res = await fetch('/api/search?' + p);
          const items = await res.json();

          rows.innerHTML = '';
          for (const it of items) {
            const tr = document.createElement('tr');
            tr.className = 'hit';
            tr.innerHTML = `<td></td><td></td><td></td><td></td>`;
            tr.children[0].textContent = it.waybill;
            tr.children[1].textContent = new Date(it.startedAt).toLocaleString();
            tr.children[2].textContent = Math.round(it.durationSeconds) + ' 秒';
            tr.children[3].textContent = it.businessType;
            tr.onclick = () => openRecording(it.evidenceId, it.codec);
            rows.appendChild(tr);
          }

          if (items.length === 0) {
            const tr = document.createElement('tr');
            const td = document.createElement('td');
            td.colSpan = 4;
            td.className = 'muted';
            td.textContent = '没有匹配的录像';
            tr.appendChild(td);
            rows.appendChild(tr);
          }
        }

        document.getElementById('f').addEventListener('submit', e => { e.preventDefault(); run(); });
        run();
        </script>
        </body>
        </html>
        """;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _loopCancellation?.Dispose();
        ((IDisposable)_listener).Dispose();
    }
}

/// <summary>检索接口返回的一项。</summary>
/// <param name="Codec">
/// 这条录像的编码（<c>H264</c> / <c>H265</c>）；老条目没有这个字段时为 <see langword="null"/>。
/// </param>
/// <remarks>
/// ⚠️ 为什么回放页需要它（规格 §3.1.7 的连带项）：**浏览器对 H.265 的支持不一致**，
/// 所以网页回放**可能播不了 H.265 录的那条**。规格给的口径是
/// 「产品必须**如实告知**当前这条录像能不能在网页里播（**不得承诺做不到的事**），
/// **不为此砍掉 H.265 选项**」—— 于是页面按这个字段把话说出来，
/// 而不是让用户对着一个转圈的播放器自己猜。
/// </remarks>
public sealed record PlaybackSearchItem(
    string EvidenceId,
    string Waybill,
    string StartedAt,
    double DurationSeconds,
    string BusinessType,
    string? Codec);
