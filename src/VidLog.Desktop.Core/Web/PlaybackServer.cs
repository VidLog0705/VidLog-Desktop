using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Import;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Live;
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

    /// <summary>
    /// 录像成品的全部落盘位置（设计图 `_43` 的多磁盘）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>不传 = 只有 <see cref="ArchiveRoot"/> 一个根</b>（老装配与测试的用法）。
    /// 传了就以它为准 —— 那时 <see cref="ArchiveRoot"/> 只是兜底根，
    /// 而**回放要在每一个根上找**：一台配了两块盘的机器上，
    /// 只看一个根的话，另一块盘上的录像在网页里会「不存在」。
    /// </remarks>
    public Configuration.StorageLocations? Locations { get; init; }

    /// <summary>
    /// 写进「用手机打开」那个二维码的**主机名 / IP**（设计图 `_38`）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>不传就现挑</b>（<see cref="LanAddress.Discover"/>）。留这个口子只有一个理由：
    /// <b>让那一条测得动</b>。真网卡上挑出哪个地址取决于跑测试的那台机器，
    /// 而「挑出来的地址有没有进二维码」恰恰是那一条要验的东西。
    /// </remarks>
    public string? LanHost { get; init; }

    /// <summary>
    /// 「预计可保留」按最近多少天估（设计图 `_36` 中间那张卡）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 是**最近**多少天，不是全库：全库会把几个月前录得少的那些日子摊进来，
    /// 于是「昨天刚把盘用掉一半」这件事在那张卡上完全看不出来。
    /// </remarks>
    public int RetentionEstimateWindowDays { get; init; } = 7;

    /// <summary>
    /// 归档层的回查实现（规格 §3.5.4）。**手机端「手动删除」要它**（§3.5.6③）。
    /// </summary>
    /// <remarks>
    /// 不传 = 用本机目录那一个（<see cref="ArchiveBackendKind.LocalDisk"/>）——
    /// 那正是「归档层就是这台电脑」的情形，也是这条接口最常见的用法。
    /// </remarks>
    public IArchiveBackend? ArchiveBackend { get; init; }

    /// <summary>
    /// 缩略图缓存（规格 §3.4.3）。不传 = 页面不显示缩略图（老装配 / 测试）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 抽帧要跑一次 ffmpeg（几百毫秒），所以**必须走缓存** ——
    /// 每次刷新页面都重抽十几条会把机器拖住，而规格明确写了
    /// 「不得每次进页面都重新抽帧」。
    /// </remarks>
    public Media.ThumbnailCache? Thumbnails { get; init; }
}

/// <summary>
/// 局域网网页回放（规格 §3.8）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ **这不是交付渠道**（规格 §3.7，2026-09-24 改版之后）。
/// 「分享」现在是**把原视频交到用户手上**（不转码、不压缩、不裁剪），
/// **不再有链接、不再有链接页、也不做自证图**。
/// </para>
/// <list type="bullet">
/// <item>本服务是给**操作者自己**在内网看回放用的，直接读本机归档目录。</item>
/// <item>要交给别人 → 用【导出原视频】（`EvidenceExporter`）：导出到用户自选的位置，
/// 然后把**那个文件**发出去。</item>
/// </list>
/// <para>
/// 所以本服务的 URL **不得**被当成「一条能发给客户的链接」——
/// 它指向的是**本机那份副本**，而那份会被生命周期清理（规格 §3.5），
/// 发出去就是一条迟早失效的地址。
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

    /// <summary>借网盘令牌那件事；没配网盘时为 <see langword="null"/>（见构造函数的参数说明）。</summary>
    private readonly NetdiskTokenSource? _netdisk;

    /// <summary>机位发现那张表；没接实时推流时为 <see langword="null"/>。</summary>
    private readonly LiveDirectory? _live;
    private HttpListener _listener = new();

    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;

    /// <param name="logger">
    /// 请求日志。**可选**（默认不记）—— 加可选参数而不是必填，
    /// 是为了让那几十处测试的构造调用一行都不用改。
    /// </param>
    /// <param name="netdisk">
    /// 把网盘令牌借给已入网的手机那一件事（`/api/v1/netdisk/token`）。
    /// **可选，默认 null** —— 与 <paramref name="logger"/> 同一条理由：
    /// 那几十处测试的构造调用一行都不用改，而没传时那条路由会如实回
    /// 「电脑端这边网盘这一档没启用」。
    /// </param>
    /// <param name="live">
    /// 机位发现那张表（`/api/v1/live/announce`）。**可选，默认 null** ——
    /// 同上一条理由；没传时那条路由会如实回「实时推流这一档没启用」。
    /// </param>
    public PlaybackServer(
        PlaybackServerOptions options,
        RecordingSearch search,
        IRecordingIndex index,
        PunchNavigation punches,
        UploadReceiver upload,
        DeviceRegistry devices,
        string deviceName,
        IAppLogger? logger = null,
        NetdiskTokenSource? netdisk = null,
        LiveDirectory? live = null)
    {
        _options = options;
        _search = search;
        _index = index;
        _punches = punches;
        _upload = upload;
        _devices = devices;
        _deviceName = deviceName;
        _logger = logger ?? NullLogger.Instance;
        _netdisk = netdisk;
        _live = live;
    }

    /// <summary>实际绑上的地址（可能不是配置里的首选地址 —— 见 <see cref="PlaybackServerOptions.FallbackPrefix"/>）。</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>是否退回到了备用地址。</summary>
    public bool IsUsingFallback { get; private set; }

    /// <summary>绑不上首选地址时的原因，供界面告知用户。没有回退过时为 <see langword="null"/>。</summary>
    public string? FallbackReason { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // ⚠️ 三条分支**每条都要留痕**（AGENTS.md §5.1「关键操作必须留痕」）：
        // 「绑上了哪个地址」是排查「手机连不上」时第一个要问的问题，而这里
        // 此前**一条日志都没有** —— 于是「退到了 localhost」与「本来就配的是
        // localhost」在日志上长得一模一样（2026-10-02 实测报上来的）。
        if (TryBind(_options.Prefix, out var firstError))
        {
            BaseUrl = _options.Prefix;
            _logger.Log(LogLevel.Info, "回放", $"已绑定回放地址 {BaseUrl}");
        }
        else if (_options.FallbackPrefix is not null && TryBind(_options.FallbackPrefix, out _))
        {
            BaseUrl = _options.FallbackPrefix;
            IsUsingFallback = true;
            FallbackReason = firstError;

            // 回退**不是**错误（局域网那一路可能只是没注册 urlacl），但它必须
            // 说出来：退到 localhost 之后的后果是**手机扫不到这台电脑**，
            // 而界面上那一句「服务已就绪」看不出区别。
            _logger.Log(LogLevel.Warn, "回放",
                $"绑不上 {_options.Prefix}（{firstError}），改用 {BaseUrl} —— "
                + "这个地址只有本机可达，手机连不上");
        }
        else
        {
            _logger.Log(LogLevel.Error, "回放",
                $"绑不上任何回放地址（首选 {_options.Prefix}：{firstError}；备用 {_options.FallbackPrefix ?? "没配"}）");

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
            catch (Exception ex)
            {
                // ⚠️ **监听循环自己怎么结束的，不该让「关服务」这件事失败。**
                // 关停失败比监听循环死掉更糟：调用方（`DesktopServices.DisposeAsync`、
                // 测试清理）会带着异常走掉，进程可能退不干净。
                // 但**不许静默**（I3）—— 记一条，让「为什么关的时候报错」查得到。
                //
                // 这是第二道：`LoopAsync` 已经把已知的几种竞态都吞了，
                // 这里兜的是「将来又出现一种没预料到的异常类型」。
                _logger.Log(LogLevel.Warn, "回放", $"监听循环结束时带出了异常：{ex.Message}");
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
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException
                or ObjectDisposedException or InvalidOperationException
                // ⚠️ 2026-09-29 补第五种表现：`ArgumentException: The handle is invalid`
                // （参数名 `handle`，抛在 `HttpListenerSession.get_RequestQueueBoundHandle`）。
                // 与上面几条是**同一条竞态** —— 关停期间 `GetContextAsync` 撞上已失效的句柄。
                // 漏掉它就会逃出循环 ⇒ `_loopTask` 变成 faulted ⇒ 由 `StopAsync` 抛给
                // 调用方 ⇒ **关回放服务时报错**，而整套是**随机**红的
                // （CI 上实测到；本机串行跑不复现，见 docs/实现决策.md §67）。
                or ArgumentException)
            {
                // ⚠️ `InvalidOperationException`（"Please call the Start() method before
                // calling this method"）是 2026-09-27 补进来的，它是一个**真竞态**：
                //
                // `StopAsync` 先取消令牌、再 `_listener.Stop()`，而挂在
                // `GetContextAsync` 上的这一句在这两者之间**可能以这个异常结束**
                // 而不是以取消结束。那条路原来会**逃出循环** ⇒ `_loopTask` 变成
                // faulted ⇒ `StopAsync` 里 `await _loopTask` 把它抛给调用方。
                // 表现是**关回放服务时报错**（实测：全套并行跑时 1/8 复现，
                // 堆栈就停在 `BeginGetContext`）。
                //
                // 在「正在关闭」这个位置，这几种异常是**同一件事**：循环该退出了。
                // 这个 try 块里只有 `GetContextAsync` 一句，所以 IOE 不可能来自别处。
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

            // 三张统计卡 + 「录像来源」下拉的那一份（设计图 `_36`）。
            if (path == "/api/overview")
            {
                await WriteOverviewAsync(context);
                return;
            }

            // 「用手机打开」的二维码（设计图 `_38`）。
            if (path == "/api/qr")
            {
                await WriteQrAsync(context);
                return;
            }

            // 缩略图（规格 §3.4.3 的列表项之一）。**没有缩略图缓存时不提供** ——
            // 那时页面显示占位方块，而不是一张假的图。
            if (path.StartsWith("/api/thumbnail/", StringComparison.Ordinal)
                && _options.Thumbnails is { } thumbnails)
            {
                await WriteThumbnailAsync(context, thumbnails, path["/api/thumbnail/".Length..]);
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

            case "/api/v1/enroll/rename":
                // 改名（规格 §3.4.5 ③）—— **要凭据**，所以它在上面那道闸之后。
                //
                // ⚠️ `deviceId` 用的是**凭据反查**出来的那个（上面那个局部量），
                // 不是报文里自称的：报文里根本没有 deviceId（见
                // `RenameRequestPayload` 的说明）。信自称的话，任何一台已入网
                // 设备都能改**别人**的名字。
                await GuardAsync(context, () => HandleRenameAsync(context, deviceId, credential));
                return;

            case "/api/v1/live/announce":
                // 手机报到它的实时推流地址（规格 §3.8 的机位发现）。
                //
                // ⚠️ `deviceId` 用的是**凭据反查**出来的那个（上面那个局部量），
                // 地址用的是**请求的来源地址** —— 两样都不许客户端自称，理由见
                // `LiveAnnouncePayload` 的说明。它在这道闸之后，是因为
                // 「哪台手机现在能看」本身就是一份机位名单。
                await GuardAsync(context, () => HandleLiveAnnounceAsync(context, deviceId));
                return;

            case "/api/v1/netdisk/token":
                // 把百度网盘的令牌**借**给手机。手机端不自己登录 —— 它拿不到 AppSecret
                // （见 `NetdiskGrant` 的类注释）。**要凭据**，所以它在这道闸之后。
                //
                // ⚠️ 这一条的份量比别的路由重：拿到令牌的手机能读写这个账号下
                // `/apps/<应用名>/` 里的全部东西。它够格拿，是因为入网那一步
                // **有人在电脑上点过同意**（规格 §3.4.5 ②）。
                // ⚠️ 别把这一条挪到凭据闸前面 —— 那等于对局域网里任何人开放。
                await GuardAsync(context, async () =>
                {
                    await WriteJsonAsync(
                        context,
                        _netdisk is null
                            ? NetdiskGrant.Off(
                                "电脑端这边网盘这一档没启用 —— 归档层不是「百度网盘」，"
                                + "或者还没配应用凭据。去电脑上「设置 → 存储与备份」看一眼。")
                            : await _netdisk.GrantAsync());
                });
                return;

            case "/api/v1/archive/verify":
                await GuardAsync(context, async () =>
                {
                    var request = await ReadJsonAsync<VerifyRequest>(context);
                    if (request is null || string.IsNullOrWhiteSpace(request.Location))
                    {
                        await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "缺少 location");
                        return;
                    }

                    await WriteJsonAsync(context, await VerifyAsync(request.Location));
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

    /// <summary>
    /// 回查归档层：这一份还在不在（规格 §3.5.4）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 手机端**手动删除**的前置闸就是它（§3.5.6③）：用户点删除 → 手机端问电脑端
    /// 「你那份还在吗」→ **查不到或查不了都绝不删**。
    /// </para>
    /// <para>
    /// ⚠️ <b>「查不了」必须与「不存在」分开报。</b>
    /// 对用户是两句话（「归档层上那份被删了」vs「现在问不到」），
    /// 而两者都导致不删 —— 把「查不了」当成「不存在」，删掉的可能就是最后一份（I2）。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 某一段的缩略图（规格 §3.4.3）。**没有就回 404** —— 页面显示占位方块。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这里**不现抽**：抽帧要跑 ffmpeg（几百毫秒），而这是浏览器在渲染列表时
    /// 挨个发的请求。抽帧发生在**用户上一次看过这一条**的时候（或者第一次点开时
    /// 由另一条路补上），这里只负责把已经抽好的那张端出去。
    /// </remarks>
    private async Task WriteThumbnailAsync(
        HttpListenerContext context, Media.ThumbnailCache thumbnails, string evidenceId)
    {
        var id = Uri.UnescapeDataString(evidenceId);
        var path = thumbnails.PathFor(id);

        if (!File.Exists(path))
        {
            context.Response.StatusCode = 404;
            return;
        }

        // 缩略图很小，用不上 Range（原来的整段写文件那条路是给视频的）。
        context.Response.ContentType = "image/jpeg";
        context.Response.ContentLength64 = new FileInfo(path).Length;

        await using var stream = File.OpenRead(path);
        await stream.CopyToAsync(context.Response.OutputStream);
    }

    private async Task<VerifyPayload> VerifyAsync(string location)
    {
        var backend = _options.ArchiveBackend
            ?? new DirectoryArchiveBackend(_options.ArchiveRoot, ArchiveBackendKind.LocalDisk);

        RelativePath relative;
        try
        {
            // ⚠️ `RelativePath.Parse` 会拒绝绝对路径、UNC 与 `..` 越级 ——
            // 这条接口是**远端**调的，那个校验在这里就是安全边界。
            relative = RelativePath.Parse(location);
        }
        catch (Exception ex)
        {
            return new VerifyPayload(false, true, $"这个路径不合规：{ex.Message}");
        }

        var result = await backend.VerifyAsync(relative);

        return new VerifyPayload(result.Exists, result.CouldNotVerify, result.FailureReason);
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

            case EnrollStatus.SeatLimitExceeded:
                // 机位满了。**不是「请求坏了」**：这台手机本身没问题，
                // 是电脑端手上的激活码不够。403 + 自己的码，手机那边照着
                // `seat_limit` 说「去电脑端激活或升级」，而不是「再试一次」。
                await WriteErrorAsync(context, 403, UploadErrors.SeatLimit, result.Detail);
                return;

            default:
                // 屏幕上的码换了、或者已经超时 —— 手机该重新扫一次。
                await WriteErrorAsync(context, 410, UploadErrors.NoPendingRequest, result.Detail);
                return;
        }
    }

    /// <summary>
    /// 改名（规格 §3.4.5 ③）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="HandleEnrollRequestAsync"/> **同形**：手机轮询它，
    /// 一次调用既报上新名字、也问批没批（三个状态都可能回来）。
    /// </para>
    /// <para>
    /// ⚠️ 「还没批 / 被拒了」回 **200 + status**，不是 4xx —— 理由与入网那条一字不差：
    /// 那不是「请求坏了」，是「人还没做决定 / 人做了个决定」。
    /// </para>
    /// </remarks>
    private async Task HandleRenameAsync(
        HttpListenerContext context, string deviceId, string credential)
    {
        var request = await ReadJsonAsync<RenameRequestPayload>(context);

        if (request is null)
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "请求体为空");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.DeviceName))
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "deviceName 不得为空");
            return;
        }

        var result = await _devices.RequestRenameAsync(deviceId, request.DeviceName, credential);

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

            default:
                // 凭据无效 —— 手机端照 `bad_token` 那条提示去重新配对。
                await WriteErrorAsync(context, 403, UploadErrors.BadToken, result.Detail);
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

            case EnrollStatus.SeatLimitExceeded:
                // 留着这条分支**不是**「以防万一」：`EnrollStatus` 是共享的枚举，
                // 掉了分支就会落到下面的 default，把它当 410「重新扫一次码」回给手机
                // —— 那会让用户对着同一个码反复扫，而问题根本不在这张码上。
                await WriteErrorAsync(context, 403, UploadErrors.SeatLimit, result.Detail);
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

    /// <summary>
    /// 手机报到它的实时推流地址（规格 §3.8 的机位发现）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>报到每隔 20 秒来一次，而这里**一条日志都不记**</b> ——
    /// 记的话是每小时一百八十条。留痕全在 <see cref="LiveDirectory"/>：
    /// 它只在「第一次来 / 换了地址 / 过期后又回来 / 过期了」这四种**状态变化**时记。
    /// </remarks>
    private async Task HandleLiveAnnounceAsync(HttpListenerContext context, string deviceId)
    {
        if (_live is not { } live)
        {
            await WriteErrorAsync(
                context, 400, UploadErrors.BadRequest, "电脑端这边实时推流这一档没启用");
            return;
        }

        // ⚠️ 这道闸门问的是「这台机器还许不许手机报到实时画面」（未激活 / 试用已结束
        //    ⇒ 不许，2026-10-03 用户裁定：「电脑端功能均不能使用，观看已存储的视频除外」）。
        //    判断与那句话都在 `DeviceRegistry` 里 —— **这个文件里不许出现「许可」**
        //    （`LicenseIndependenceTests` 那条绊线）：它同时托管着检索 / 回放 / 导出，
        //    而 L8 要求那几条路上一个门禁都没有。闸门只装在这一条路上，
        //    **不装进 `AuthenticateAsync`** —— 手机上已经录下、还没传过来的那一段必须照收。
        if (_devices.LiveAnnounceBlocked() is { } blocked)
        {
            await WriteErrorAsync(context, 403, UploadErrors.SeatLimit, blocked.Detail);
            return;
        }

        var request = await ReadJsonAsync<LiveAnnouncePayload>(context);

        if (request?.Port is not { } port || port is < 1 or > 65535)
        {
            // ⚠️ 端口越界**不当成小事**：`http://主机:0/live` 那种地址会让取流那一步
            // 以一个看不懂的错失败，而那时已经离这里很远了。
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "port 要在 1..65535 之间");
            return;
        }

        var remote = context.Request.RemoteEndPoint?.Address;
        if (remote is null)
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "读不到请求的来源地址");
            return;
        }

        live.Announce(deviceId, HostOf(remote), port);
        await WriteJsonAsync(context, new LiveAnnounceAck(true));
    }

    /// <summary>
    /// 把请求的来源地址写成能塞进 URL 的样子。
    /// </summary>
    /// <remarks>
    /// ⚠️ 双栈监听时，IPv4 的客户端会显示成**映射地址**（`::ffff:192.168.1.5`）。
    /// 不映射回去的话，那个地址塞进 `http://…/live` 是 ffmpeg 连不上的 ——
    /// 而表现只是「这一格黑着」，看不出是地址形状的问题。
    /// </remarks>
    private static string HostOf(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        // IPv6 在 URL 里要方括号。⚠️ 链路本地那串 `%scope`（如 `fe80::1%12`）
        // 是 `ToString()` 自带的，**要留着** —— 去掉它指向的是另一个接口上的同名地址。
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
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
            // 「录像来源」下拉（设计图 `_36`）。空串按「不限」处理 ——
            // 那正是下拉里「全部设备」那一项的值。
            SourceDevice = query["source"] is { Length: > 0 } source ? source : null,
        };

        var hits = await _search.SearchAsync(recordingQuery);

        var payload = hits.Select(hit => new PlaybackSearchItem(
            hit.Entry.EvidenceId,
            hit.Entry.Waybill.Value,
            hit.Entry.StartedAt.ToString("O"),
            hit.Entry.Duration.TotalSeconds,
            hit.BusinessType?.ToString() ?? "unknown",
            // 规格 §3.1.7 的连带项：页面要如实告知「这条能不能在网页里播」。
            hit.Entry.Codec,
            // 规格 §3.4.3 的第 ⑦ 项（归档层这一份在哪儿）。
            // ⚠️ 本机磁盘那一档**要明说「仅本机」**：那时盘上这份是唯一副本（§3.5.1）。
            _options.ArchiveBackend is { Kind: ArchiveBackendKind.LocalDisk }
                ? "仅本机"
                : _options.ArchiveBackend?.Kind.ToString() ?? "仅本机"));

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

    /// <summary>
    /// 三张统计卡 + 「录像来源」下拉（设计图 `_36`）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>卡片上每一个数都能从索引或盘上算出来，一个都不编</b>（规格 §13.1）。
    /// 算不出来的（订单联动、上传成功率那类）<b>不在这里，也不在页面上编一个</b>。
    /// </para>
    /// <para>
    /// ⚠️ 所有字节数走 <see cref="RecordingStats.Summarize"/> ——
    /// 它底下是 <c>CleanupPlanner.EstimateBytes</c>，与「按空间清理」**同一个函数**。
    /// 另写一份求和的话，同一个库会在两个页面上报出两个容量。
    /// 所以**界面上凡是显示它的地方都带「约」**。
    /// </para>
    /// </remarks>
    private async Task WriteOverviewAsync(HttpListenerContext context)
    {
        var entries = await _index.LoadAllAsync();

        // 全库那一份（已用 = 全部录像的估算占用；一个区间都不限）
        var all = RecordingStats.Summarize(entries, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

        var earliest = entries.Count == 0 ? (DateTimeOffset?)null : entries.Min(e => e.StartedAt);

        // ── 盘上的容量 ────────────────────────────────────────────────
        //
        // ⚠️ 按**卷**去重：两个存储目录落在同一块盘上时（完全合法的配置），
        // 各算一遍会让「共 2 个存储目录」与容量条自相矛盾 —— 容量看起来翻了一倍。
        var roots = _options.Locations?.ReadRoots
            ?? (_options.ArchiveRoot.Length > 0 ? [_options.ArchiveRoot] : []);

        var probe = new Configuration.DriveVolumeProbe();
        var volumes = new Dictionary<string, Configuration.VolumeSpace>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (probe.Measure(root) is not { } space)
            {
                continue;
            }

            volumes.TryAdd(VolumeOf(root), space);
        }

        var now = DateTimeOffset.Now;

        // ── 预计可保留 ────────────────────────────────────────────────
        //
        // = 剩余空间 ÷ 最近这几天的平均每日占用。
        //
        // ⚠️ 分母是「**真的有录像的那些天**」摊出来的，不是窗口长度：
        // 一台刚装两天的机器上，拿 7 天当分母会把日均算成实际的 1/3.5，
        // 于是那张卡报出一个**乐观三倍**的天数 —— 用户照着它决定「不用加盘」。
        //
        // 历史不足一天时报「暂无法估算」（照图那句话：历史数据不足或平均每日占用为 0）。
        var windowDays = Math.Max(1, _options.RetentionEstimateWindowDays);
        var historyDays = earliest is { } first ? (now - first).TotalDays : 0;
        var spanDays = Math.Min(windowDays, historyDays);

        double? perDayBytes = null;
        if (spanDays >= 1)
        {
            var recent = RecordingStats.Summarize(entries, now.AddDays(-spanDays), now.AddDays(1));
            if (recent.EstimatedBytes > 0)
            {
                perDayBytes = recent.EstimatedBytes / spanDays;
            }
        }

        var freeBytes = volumes.Values.Sum(v => v.FreeBytes);
        var totalBytes = volumes.Values.Sum(v => v.TotalBytes);

        await WriteJsonAsync(context, new PlaybackOverview(
            Earliest: earliest?.ToString("O"),
            Count: all.Count,
            WaybillCount: all.WaybillCount,
            // 「已用」是**录像的**占用（估），不是整块盘已用：这张卡问的是
            // 「录下来的东西占了多少」，而整盘已用会把 Windows 与别的软件算进来。
            EstimatedUsedBytes: all.EstimatedBytes,
            TotalBytes: totalBytes,
            FreeBytes: freeBytes,
            DirectoryCount: roots.Count,
            VolumeCount: volumes.Count,
            RetentionDays: perDayBytes is > 0 && freeBytes > 0 ? freeBytes / perDayBytes.Value : null,
            // ⚠️ 估不出来时报 **0**，不报那个窗口长度：这个字段说的是「那个日均
            // 是拿几天摊出来的」，而一个没算出来的日均没有摊过任何天。
            // 报 7 的话页面上会出现「按最近 7 天估算」配着「暂无法估算」——
            // 一句自相矛盾的话，用户只能猜哪个是真的。
            RetentionWindowDays: perDayBytes is null ? 0 : (int)Math.Floor(spanDays),
            Sources: [.. entries
                .GroupBy(e => e.SourceDeviceId, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new PlaybackSource(g.Key, DescribeSource(g.Key), g.Count()))]));
    }

    /// <summary>
    /// 「录像来源」下拉里那个名字。
    /// </summary>
    /// <remarks>
    /// ⚠️ 本机录的**就是机器名本身**，不另起一个好听的名字：
    /// 索引里 `SourceDeviceId` 存的是写进去那一刻的机器名，而用户要认的正是那一台。
    /// 唯一特判的是导入进来的那一种 —— 它写的是一句实话（「不知道是哪台录的」），
    /// 直接印 `imported` 给用户看没有意义。
    /// </remarks>
    private static string DescribeSource(string sourceDeviceId) => sourceDeviceId switch
    {
        RecordingImporter.SourceDeviceId => "外部导入",
        "" => "未知来源",
        _ => sourceDeviceId,
    };

    /// <summary>
    /// 「用手机打开」的二维码（设计图 `_38`）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>地址里没有访问密钥</b>。设计图上那一串 <c>?key=…</c> 是那个产品的做法，
    /// 而<b>本仓的回放页本来就没有密钥</b> —— 局域网里谁打开这个地址都能看。
    /// 编一个 key 出来显示，用户会以为「有这个 key 才看得到」，
    /// 于是把它当成可以外发的链接。<b>那是把一句假话印在屏幕上。</b>
    /// 所以这里给的是真地址，而提醒说的是**这件事本身的真实风险**。
    /// </para>
    /// <para>
    /// ⚠️ 二维码只回**模块矩阵**（一行一串 0/1），不在这里生成 PNG：
    /// Core 不该知道怎么画图，而浏览器用 canvas 画几十行方块是白送的。
    /// 不做缩放插值 —— 二维码一糊就「看着正常、扫不出来」。
    /// </para>
    /// </remarks>
    private async Task WriteQrAsync(HttpListenerContext context)
    {
        // ⚠️ 不用 `BaseUrl` 里那个地址：它多半是 `localhost` 或通配地址，
        // 手机连不上（`LanAddress` 的说明里写了这条）。
        var host = _options.LanHost ?? LanAddress.Discover();

        if (string.IsNullOrWhiteSpace(host))
        {
            await WriteJsonAsync(context, new PlaybackQr(
                Url: null,
                Width: 0,
                Height: 0,
                Rows: [],
                Problem: $"没挑到局域网地址。请用这台电脑的 IP 手动拼：http://<电脑的IP>:{BoundPort()}/"));

            return;
        }

        var url = $"http://{host}:{BoundPort()}/";
        var modules = EnrollQr.Modules(url);

        var width = modules.GetLength(0);
        var height = modules.GetLength(1);
        var rows = new string[height];

        for (var y = 0; y < height; y++)
        {
            var line = new char[width];

            for (var x = 0; x < width; x++)
            {
                line[x] = modules[x, y] ? '1' : '0';
            }

            rows[y] = new string(line);
        }

        await WriteJsonAsync(context, new PlaybackQr(url, width, height, rows, null));
    }

    /// <summary>
    /// 实际绑上的那个端口。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不走 <c>new Uri(BaseUrl)</c>：正式装配里的前缀是 <c>http://+:8720/</c> 这种
    /// **通配形式**，而 <see cref="Uri"/> 解析不了 <c>+</c> 当主机名 —— 它会抛。
    /// 从文本里取反而稳。
    /// </remarks>
    private int BoundPort()
    {
        var text = BaseUrl;
        var colon = text.LastIndexOf(':');

        if (colon < 0)
        {
            return 80;
        }

        var end = text.IndexOf('/', colon);
        var digits = end < 0 ? text[(colon + 1)..] : text[(colon + 1)..end];

        return int.TryParse(digits, out var port) ? port : 80;
    }

    /// <summary>一个路径落在哪一块卷上（按卷去重要用它）。</summary>
    private static string VolumeOf(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
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
    /// 解析完还要再确认落在**某一个**归档根之内，挡住索引本身被污染的情况。
    /// </remarks>
    private async Task<string?> ResolveEvidencePathAsync(string evidenceId)
    {
        if (string.IsNullOrWhiteSpace(evidenceId) || _options.ArchiveRoot.Length == 0)
        {
            return null;
        }

        var entries = await _index.LoadAllAsync();
        var entry = entries.FirstOrDefault(e => string.Equals(e.EvidenceId, evidenceId, StringComparison.Ordinal));
        if (entry is null)
        {
            return null;
        }

        // ⚠️ 挨个根试，但**每一个都要过那条包含判定** ——
        // 多根之后这一层更要紧了：找到的第一份不值得信任，
        // 值得信任的是「它确实落在某一个归档根下面」。
        foreach (var root in _options.Locations?.ReadRoots ?? [_options.ArchiveRoot])
        {
            var full = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(Path.Combine(full, entry.Location.Value));

            var rootWithSeparator = full.EndsWith(Path.DirectorySeparatorChar)
                ? full
                : full + Path.DirectorySeparatorChar;

            if (candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
                && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
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
    /// <remarks>
    /// <para>
    /// 外观照需求方的设计图 <c>_36</c> / <c>_37</c> / <c>_38</c> 做（豁免留档见
    /// <c>docs/实现决策.md</c> §86）：浅色单档、三张统计卡、筛选卡、录像列表卡、
    /// 齿轮弹出的「播放兼容」、以及「用手机打开」的二维码。
    /// </para>
    /// <para>
    /// ⚠️ <b>图上能点、而本仓没这个能力的每一颗按钮一律 <c>disabled</c>，
    /// 并且把原因写在悬停提示里</b>（踩坑 #13）。渲染一个按下去什么都不发生的按钮，
    /// 用户会以为是网络卡了，接着反复点 —— 那比干脆没有这颗按钮更糟。
    /// </para>
    /// <para>
    /// ⚠️ <b>不引任何外部资源</b>（图标是内联 SVG，二维码是 canvas 现画的）：
    /// 这一页跑在局域网里，多半连不上外网，一个 CDN 图标就能让它整页错位。
    /// </para>
    /// </remarks>
    private static string BuildPage() => """
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>快递打包录像回放</title>
        <style>
          /* 设计图 `_36` 的令牌（Tailwind 色板）。⚠️ 只做浅色一档 —— 图上没有深色版，
             而 `color-scheme: light dark` 会让浏览器把输入框、滚动条自己刷成深色，
             于是同一张卡上出现两套配色。 */
          :root {
            --page: #F3F4F6; --surface: #FFFFFF; --muted-surface: #F8FAFC;
            --border: #E2E8F0; --border-strong: #CBD5E1;
            --text: #1E293B; --muted: #64748B; --disabled: #94A3B8;
            --accent: #3B82F6; --accent-weak: #DBEAFE; --success: #10B981;
            --video: #0F172A; --warn: #C2410C;
            color-scheme: light;
          }
          * { box-sizing: border-box; }
          body { margin: 0; background: var(--page); color: var(--text);
            font: 14px/1.6 system-ui, "Segoe UI", "Microsoft YaHei", sans-serif; }
          .page { max-width: 1120px; margin: 0 auto; padding: 20px 16px 48px; }

          .topbar { display: flex; align-items: flex-start; gap: 12px; flex-wrap: wrap; margin-bottom: 16px; }
          .topbar h1 { font-size: 22px; margin: 0; flex: 1 1 auto; }
          .tools { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }

          button { font: inherit; cursor: pointer; border-radius: 8px;
            border: 1px solid var(--border-strong); background: var(--surface);
            color: var(--text); padding: 6px 12px; }
          button:hover:not(:disabled) { border-color: var(--accent); color: var(--accent); }
          button:disabled { color: var(--disabled); background: var(--muted-surface);
            border-color: var(--border); cursor: not-allowed; }
          .tool-btn { display: flex; flex-direction: column; align-items: flex-start;
            gap: 1px; padding: 6px 12px; text-align: left; }
          .tool-btn b { font-size: 13px; font-weight: 600; }
          .tool-btn small { font-size: 11px; color: var(--muted); font-weight: 400; }
          .tool-btn:disabled small { color: var(--disabled); }
          .icon-btn { width: 34px; height: 34px; padding: 0; display: grid; place-items: center; }
          .icon-btn svg { width: 18px; height: 18px; }

          input, select { font: inherit; padding: 7px 10px; width: 100%;
            border: 1px solid var(--border-strong); border-radius: 8px;
            background: var(--surface); color: var(--text); }
          input:focus, select:focus { outline: 2px solid var(--accent-weak); border-color: var(--accent); }

          .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
            gap: 12px; margin-bottom: 12px; }
          .card { background: var(--surface); border: 1px solid var(--border);
            border-radius: 12px; padding: 16px; }
          .card h2 { font-size: 13px; font-weight: 600; color: var(--muted); margin: 0 0 6px; }
          .stat { font-size: 24px; font-weight: 700; line-height: 1.3; }
          .stat.weak { font-size: 16px; font-weight: 600; color: var(--muted); }
          .foot { font-size: 12px; color: var(--muted); margin-top: 6px; }
          .bar { height: 8px; border-radius: 999px; background: var(--border);
            margin-top: 12px; overflow: hidden; }
          .bar i { display: block; height: 100%; width: 0; background: var(--accent); }

          .filters { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
            gap: 12px; align-items: end; }
          .field { display: flex; flex-direction: column; gap: 4px; }
          .field label { font-size: 12px; color: var(--muted); }
          .field button { height: 36px; }

          .list-card { padding: 0; overflow: hidden; margin-top: 12px; }
          .card-head { display: flex; align-items: center; gap: 12px;
            padding: 14px 16px; border-bottom: 1px solid var(--border); }
          .card-head h2 { margin: 0; font-size: 15px; font-weight: 600; color: var(--text); }
          .card-head .foot { margin: 0 0 0 auto; }

          table { width: 100%; border-collapse: collapse; }
          th, td { text-align: left; padding: 10px 12px; font-size: 13px;
            border-bottom: 1px solid var(--border); }
          th { color: var(--muted); font-weight: 600; background: var(--muted-surface); }
          tbody tr:last-child td { border-bottom: none; }
          tr.hit { cursor: pointer; }
          tr.hit:hover { background: var(--accent-weak); }
          .thumb { display: block; width: 96px; height: 54px; border-radius: 6px;
            object-fit: cover; background: var(--video); }

          .empty { padding: 40px 16px; text-align: center; }
          .empty b { display: block; font-size: 15px; margin-bottom: 4px; }
          .empty span { font-size: 13px; color: var(--muted); }

          #punches { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 12px; align-items: center; }
          #punches button { padding: 4px 10px; }
          #player { display: none; width: 100%; margin-top: 16px;
            background: #000; border-radius: 12px; }
          .note { margin-top: 24px; font-size: 12px; color: var(--muted); }
          .muted { color: var(--muted); }

          .modal { position: fixed; inset: 0; z-index: 10; padding: 16px;
            background: #0F172A99; display: grid; place-items: center; }
          .modal[hidden] { display: none; }
          .modal-box { width: 100%; max-width: 460px; background: var(--surface);
            border-radius: 12px; padding: 20px; }
          .modal-box h3 { margin: 0 0 6px; font-size: 16px; }
          .radio { display: flex; align-items: center; gap: 8px; padding: 8px 0; }
          .radio input { width: auto; }
          .radio.off { color: var(--disabled); }
          .hint { margin: 0 0 10px; font-size: 12px; color: var(--muted); }
          .warn { margin: 10px 0; padding: 8px 10px; font-size: 12px;
            color: var(--warn); background: #FFF7ED; border: 1px solid #FED7AA; border-radius: 8px; }
          .modal-actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
          #qrCanvas { display: block; margin: 12px auto; background: #FFFFFF;
            image-rendering: pixelated; }
        </style>
        </head>
        <body>
        <div class="page">
        <header class="topbar">
          <h1>快递打包录像回放</h1>
          <div class="tools">
            <!-- ⚠️ 图上这一颗是按得动的，而订单联动要一个**至今未开工的服务端**
                 （母仓 docs/实现决策.md 记着，规格 §3.8 把它挂在 M6 上）。
                 所以这里禁用 + 悬停写明原因：渲染一颗按下去什么都不发生的按钮，
                 用户会以为是网络卡了，接着反复点。 -->
            <button class="tool-btn" disabled
              title="订单联动需要一个订单服务端把订单信息推给录像设备，而那个服务端还没开工。这一颗现在按下去不会有任何反应。">
              <b>安装订单联动</b>
              <small>群发订单信息到录像设备</small>
            </button>

            <!-- 齿轮 = 「播放兼容」（设计图 `_37`）。这一颗是真能打开的。 -->
            <button id="openCompat" class="icon-btn" title="播放兼容" aria-label="播放兼容">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"
                   stroke-linecap="round">
                <circle cx="12" cy="12" r="3.4"/>
                <circle cx="12" cy="12" r="6.8"/>
                <path d="M12 2.6v4.6M12 16.8v4.6M2.6 12h4.6M16.8 12h4.6"/>
                <path d="M5.4 5.4l3.2 3.2M15.4 15.4l3.2 3.2M18.6 5.4l-3.2 3.2M8.6 15.4l-3.2 3.2"/>
              </svg>
            </button>

            <!-- ⚠️ 面板图标在图上对应的是那个产品的「侧栏/分栏」开关。
                 这一页只有一栏，没有可收起的第二栏 —— 禁用。 -->
            <button class="icon-btn" disabled
              title="这一页只有单栏布局，没有可以展开或收起的第二栏。">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"
                   stroke-linejoin="round">
                <rect x="3.5" y="4.5" width="17" height="15" rx="2"/>
                <path d="M9.5 4.5v15"/>
              </svg>
            </button>

            <button id="refresh" class="icon-btn" title="重新查询" aria-label="重新查询">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"
                   stroke-linecap="round" stroke-linejoin="round">
                <path d="M20.2 12a8.2 8.2 0 1 1-2.8-6.1"/>
                <path d="M20.5 4.4v4.2h-4.2"/>
              </svg>
            </button>

            <!-- ⚠️ 地球图标 = 界面语言。本仓只做中文（英文项禁用，见
                 docs/实现决策.md 里那条裁决），所以这一颗禁用 + 写明原因。 -->
            <button class="icon-btn" disabled
              title="这一页现在只有中文。界面语言切换在电脑端的设置里，而且英文还没做。">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"
                   stroke-linecap="round">
                <circle cx="12" cy="12" r="8.5"/>
                <path d="M3.6 12h16.8"/>
                <path d="M12 3.6c2.6 2.7 2.6 14.1 0 16.8M12 3.6c-2.6 2.7-2.6 14.1 0 16.8"/>
              </svg>
            </button>

            <!-- ⚠️ **刻意加的一颗、图上没有入口的按钮**：设计图 `_38` 那个「用手机打开」
                 模态框在 `_36` 上找不到触发它的东西。功能是图上的，入口是我加的，
                 留档在 docs/实现决策.md §86。 -->
            <button id="openQr" class="tool-btn">
              <b>用手机打开</b>
              <small>扫二维码在手机上看</small>
            </button>
          </div>
        </header>

        <section class="cards">
          <div class="card">
            <h2>当前可追溯到</h2>
            <div id="statSpan" class="stat weak">读取中…</div>
            <div id="statSpanFoot" class="foot"></div>
          </div>
          <div class="card">
            <h2>预计可保留</h2>
            <div id="statRetention" class="stat weak">读取中…</div>
            <div id="statRetentionFoot" class="foot"></div>
          </div>
          <div class="card">
            <h2>存储空间</h2>
            <div id="statSpace" class="stat weak">读取中…</div>
            <div id="statSpaceFoot" class="foot"></div>
            <div class="bar"><i id="spaceBar"></i></div>
          </div>
        </section>

        <form class="card filters" id="f">
          <div class="field">
            <label for="from">开始日期</label>
            <input type="date" id="from">
          </div>
          <div class="field">
            <label for="to">结束日期</label>
            <input type="date" id="to">
          </div>
          <div class="field">
            <label for="source">录像来源</label>
            <select id="source"><option value="">全部设备</option></select>
          </div>
          <div class="field">
            <label for="q">订单号或文件名</label>
            <input type="text" id="q" placeholder="输入订单号关键词搜索" autocomplete="off">
          </div>
          <div class="field">
            <label for="mode">匹配方式</label>
            <select id="mode">
              <option value="exact">精确</option>
              <option value="prefix">前缀</option>
              <option value="contains">模糊</option>
            </select>
          </div>
          <div class="field">
            <label for="type">类型</label>
            <select id="type">
              <option value="">全部</option>
              <option value="outbound">发货</option>
              <option value="return">退货</option>
            </select>
          </div>
          <div class="field">
            <button type="submit" id="searchBtn">查询</button>
          </div>
        </form>

        <section class="card list-card">
          <div class="card-head">
            <h2>录像列表</h2>
            <div id="listSummary" class="foot"></div>
          </div>
          <table>
            <thead><tr>
              <th>类型</th><th>画面</th><th>录像</th><th>录制时间</th><th>时长</th><th>归档</th>
            </tr></thead>
            <tbody id="rows"></tbody>
          </table>
          <div id="empty" class="empty" hidden>
            <b>没有找到匹配的录像</b>
            <span>请调整日期范围或订单号关键词</span>
          </div>
        </section>

        <div id="punches"></div>
        <video id="player" controls playsinline></video>
        <!-- 录制规格的如实告知（规格 §3.1.7 的连带项）。默认藏着，点开一条 H.265 的才出现。 -->
        <div id="codecNote" class="note" hidden></div>
        <p class="note">
          本页由 VidLog 电脑端提供，仅供内网回放。<b>它不是交付渠道</b> ——
          要交给别人请用电脑端上的【导出原视频】（导出的是原件本身，不转码、不压缩），
          然后把那个文件发出去。这一页的视频来自本机副本，而本机副本可能已被生命周期清理。
        </p>
        </div>

        <!-- ── 「播放兼容」（设计图 `_37`）─────────────────────────────── -->
        <div id="compatModal" class="modal" hidden>
          <div class="modal-box">
            <h3>播放兼容</h3>

            <label class="radio">
              <input type="radio" name="compat" checked>
              <span>自动（推荐）</span>
            </label>
            <p class="hint">
              现在是这一条路：回放页把归档里的<b>原片</b>直接发给浏览器，<b>不转码</b>。
              H.265 的浏览器支持不一致 —— 放不出来时页面会提示你用系统播放器打开那个文件
              （规格 §3.1.7 的如实告知）。
            </p>

            <!-- ⚠️ 这两档**禁用**：本机不做转码（设计图上那一档要有转码链才有意义），
                 而「始终直连原片」就是上面「自动」现在做的事。按下去什么都不会变的开关，
                 比没有这个开关更糟（踩坑 #13）。 -->
            <label class="radio off">
              <input type="radio" name="compat" disabled>
              <span>始终转码为 H.264</span>
            </label>
            <p class="hint">本机不做转码 —— 库里存的是什么就发什么。这一档要有转码链才有意义，先禁用。</p>

            <label class="radio off">
              <input type="radio" name="compat" disabled>
              <span>始终直连原片</span>
            </label>
            <p class="hint">这一档就是「自动」现在唯一在做的事，选了也看不出区别。</p>

            <div class="modal-actions">
              <button id="closeCompat">关闭</button>
            </div>
          </div>
        </div>

        <!-- ── 「用手机打开」（设计图 `_38`）───────────────────────────── -->
        <div id="qrModal" class="modal" hidden>
          <div class="modal-box">
            <h3>用手机打开</h3>
            <p class="hint">手机与监控端连接同一个局域网后，扫描二维码即可打开录像网页。</p>
            <canvas id="qrCanvas" width="240" height="240"></canvas>
            <input id="qrUrl" type="text" readonly>
            <p id="qrProblem" class="hint" hidden></p>
            <p class="warn">
              这个网址<b>没有密码</b> —— 同一个局域网里谁拿到它，都能看到全部录像。
              请不要发给无关人员，也不要发到群里。它指向的又是这台电脑上的副本，
              副本会被生命周期清理，所以它也不该被当成一条长期有效的链接。
            </p>
            <div class="modal-actions">
              <button id="closeQr">关闭</button>
              <button id="copyUrl">复制网址</button>
            </div>
          </div>
        </div>

        <script>
        const rows = document.getElementById('rows');
        const player = document.getElementById('player');
        const punchBox = document.getElementById('punches');
        const empty = document.getElementById('empty');

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

        // 发货 / 退货那两个字。服务端回的是枚举名（`Outbound` / `Return`），
        // 而界面上要写中文 —— 这个映射**只在这里**，与手机端那两处同一个口径。
        function businessLabel(type) {
          if (type === 'Outbound') return '发货';
          if (type === 'Return') return '退货';
          return '未标注';
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
            note.hidden = false;
          } else {
            note.hidden = true;
          }
        }

        // ── 三张统计卡（设计图 `_36`）──────────────────────────────────

        // 字节 → 「GB」。⚠️ 只取一位小数：这三张卡上的数**都是估算的**
        // （底下是 CleanupPlanner 那套系数），印成 `12.3456789GB`
        // 会让用户以为它是量出来的一个精确值。
        function gb(bytes) {
          const v = (bytes || 0) / (1024 * 1024 * 1024);
          return (v >= 100 ? Math.round(v) : Math.round(v * 10) / 10) + 'GB';
        }

        function dayText(iso) {
          const d = new Date(iso);
          return d.getFullYear() + '-'
            + String(d.getMonth() + 1).padStart(2, '0') + '-'
            + String(d.getDate()).padStart(2, '0');
        }

        async function loadOverview() {
          const o = await (await fetch('/api/overview')).json();

          // ① 当前可追溯到
          const span = document.getElementById('statSpan');
          const spanFoot = document.getElementById('statSpanFoot');
          if (o.earliest) {
            const days = Math.max(0, Math.floor((Date.now() - new Date(o.earliest).getTime()) / 86400000));
            span.textContent = dayText(o.earliest) + ' 起（' + days + ' 天）';
            spanFoot.textContent = '库里一共 ' + o.count + ' 条录像，' + o.waybillCount + ' 个单号';
          } else {
            span.textContent = '暂无录像数据';
            spanFoot.textContent = '当前存储库未找到可用录像';
          }

          // ② 预计可保留 —— **算不出来时报「暂无法估算」，绝不编一个数**
          //（规格 §13.1：不能给用户一个看起来像事实的猜测）。
          const ret = document.getElementById('statRetention');
          const retFoot = document.getElementById('statRetentionFoot');
          if (o.retentionDays === null) {
            ret.textContent = '暂无法估算';
            retFoot.textContent = '历史数据不足或平均每日占用为 0';
          } else {
            ret.textContent = '约 ' + Math.floor(o.retentionDays) + ' 天';
            retFoot.textContent = '按最近 ' + o.retentionWindowDays
              + ' 天的平均占用估算，剩余空间用尽之前';
          }

          // ③ 存储空间
          document.getElementById('statSpace').textContent =
            '已用约 ' + gb(o.estimatedUsedBytes) + ' / ' + gb(o.totalBytes);
          document.getElementById('statSpaceFoot').textContent =
            '共 ' + o.directoryCount + ' 个存储目录（' + o.volumeCount + ' 块盘），剩余 '
            + gb(o.freeBytes);
          const pct = o.totalBytes > 0
            ? Math.min(100, (o.estimatedUsedBytes / o.totalBytes) * 100)
            : 0;
          document.getElementById('spaceBar').style.width = pct.toFixed(1) + '%';

          // ④ 「录像来源」下拉。
          //
          // ⚠️ `option.value` 用的是**原样值**（`source.id`）而不是给人看的那几个字
          // —— 服务端比的是索引里存的那个串，拿「外部导入」去筛会一条都筛不出来。
          const sel = document.getElementById('source');
          const kept = sel.value;
          sel.length = 1;
          for (const s of o.sources) {
            const opt = document.createElement('option');
            opt.value = s.id;
            opt.textContent = s.label + '（' + s.count + '）';
            sel.appendChild(opt);
          }
          sel.value = kept;
          // 只有一处来源时把它禁掉：一颗只有一个可选项的下拉没有信息量，
          // 而它占着筛选区一整格。**不禁用整格**，值仍然会跟着查询发出去。
          sel.disabled = o.sources.length <= 1;
        }

        async function run() {
          const p = new URLSearchParams();
          const q = document.getElementById('q').value.trim();
          if (q) p.set('q', q);
          p.set('mode', document.getElementById('mode').value);
          const t = document.getElementById('type').value;
          if (t) p.set('type', t);
          const src = document.getElementById('source').value;
          if (src) p.set('source', src);
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
            // 规格 §3.4.3 的七项：标签 / 缩略图+播放 / `单号.mp4` / 时间 / 时长 / 归档。
            tr.innerHTML = `<td></td><td></td><td></td><td></td><td></td><td></td>`;

            // ① 标签（发货 / 退货）
            tr.children[0].textContent = businessLabel(it.businessType);

            // ② 缩略图 + ③ 播放按钮（**同一格**：缩略图就是播放入口）
            const thumbCell = tr.children[1];
            const img = document.createElement('img');
            img.className = 'thumb';
            img.alt = '';
            img.src = '/api/thumbnail/' + encodeURIComponent(it.evidenceId);
            // ⚠️ 404（还没抽过帧）时**把那格清空**，而不是显示一个浏览器的破图图标 ——
            // 破图图标看起来像「这一段坏了」，而其实只是还没生成缩略图。
            img.onerror = () => { img.remove(); };
            thumbCell.appendChild(img);

            // ④ 列表里显示的名字：`单号.mp4`
            //
            // ⚠️ **这只是显示名**：磁盘上的文件名与归档路径一律不动
            //（改了会波及索引、检索、归档回查，还要迁移已经录好的那些）。
            tr.children[2].textContent = it.waybill ? (it.waybill + '.mp4') : '（无单号）';

            // ⑤ 录制时间 ⑥ 时长
            tr.children[3].textContent = new Date(it.startedAt).toLocaleString();
            tr.children[4].textContent = Math.round(it.durationSeconds) + ' 秒';

            // ⑦ 归档层这一份在哪儿 —— **本机磁盘那一档要明说「仅本机」**：
            // 那时盘上这份是唯一副本（规格 §3.5.1），用户必须知道。
            tr.children[5].textContent = it.archiveLabel;

            tr.onclick = () => openRecording(it.evidenceId, it.codec);
            tr.title = '点这一行播放';
            rows.appendChild(tr);
          }

          // 空状态照设计图：表格收起来，换成那两句话。
          empty.hidden = items.length > 0;
          document.querySelector('.list-card table').hidden = items.length === 0;
          document.getElementById('listSummary').textContent =
            items.length === 0 ? '没有找到匹配记录' : '共 ' + items.length + ' 条';
        }

        // ── 「播放兼容」弹出框（设计图 `_37`）──────────────────────────
        const compatModal = document.getElementById('compatModal');
        document.getElementById('openCompat').onclick = () => { compatModal.hidden = false; };
        document.getElementById('closeCompat').onclick = () => { compatModal.hidden = true; };
        compatModal.onclick = e => { if (e.target === compatModal) compatModal.hidden = true; };

        // ── 「用手机打开」的二维码（设计图 `_38`）─────────────────────
        const qrModal = document.getElementById('qrModal');
        const qrCanvas = document.getElementById('qrCanvas');
        const qrUrlBox = document.getElementById('qrUrl');
        const copyBtn = document.getElementById('copyUrl');

        document.getElementById('openQr').onclick = async () => {
          qrModal.hidden = false;

          const qr = await (await fetch('/api/qr')).json();
          const problem = document.getElementById('qrProblem');

          if (!qr.url) {
            // ⚠️ 挑不到局域网地址时**画不出来就别画**：
            // 一张扫不出来的假码比一句实话糟糕得多。
            qrCanvas.hidden = true;
            qrUrlBox.value = '';
            copyBtn.disabled = true;
            problem.textContent = qr.problem || '没挑到局域网地址。';
            problem.hidden = false;
            return;
          }

          qrCanvas.hidden = false;
          problem.hidden = true;
          copyBtn.disabled = false;
          qrUrlBox.value = qr.url;

          // ⚠️ 逐模块画方块、**不做任何缩放插值**。二维码一糊就变成
          // 「看着正常、扫不出来」—— 那是最难排查的一种坏法
          //（`EnrollWindow` 的 NearestNeighbor 那条红线是同一个道理）。
          const scale = Math.max(2, Math.floor(280 / qr.width));
          qrCanvas.width = qr.width * scale;
          qrCanvas.height = qr.height * scale;
          qrCanvas.style.width = '240px';
          qrCanvas.style.height = '240px';

          const ctx = qrCanvas.getContext('2d');
          ctx.fillStyle = '#FFFFFF';
          ctx.fillRect(0, 0, qrCanvas.width, qrCanvas.height);
          ctx.fillStyle = '#000000';

          for (let y = 0; y < qr.rows.length; y++) {
            const line = qr.rows[y];
            for (let x = 0; x < line.length; x++) {
              if (line[x] === '1') {
                ctx.fillRect(x * scale, y * scale, scale, scale);
              }
            }
          }
        };

        document.getElementById('closeQr').onclick = () => { qrModal.hidden = true; };
        qrModal.onclick = e => { if (e.target === qrModal) qrModal.hidden = true; };

        copyBtn.onclick = async () => {
          try {
            if (navigator.clipboard) {
              await navigator.clipboard.writeText(qrUrlBox.value);
            } else {
              // ⚠️ 这一页走 http（不是 https），浏览器多半不给 clipboard API ——
              // 退回选中 + execCommand，那条路在 http 下仍然能用。
              qrUrlBox.select();
              document.execCommand('copy');
            }
            copyBtn.textContent = '已复制';
          } catch (err) {
            // 复制不上时**别装作复制上了**（地址就在旁边的框里，让用户自己选中）。
            copyBtn.textContent = '复制不了，请手动选中';
            qrUrlBox.select();
          }
          setTimeout(() => { copyBtn.textContent = '复制网址'; }, 2000);
        };

        document.getElementById('refresh').onclick = () => { loadOverview(); run(); };
        document.getElementById('f').addEventListener('submit', e => { e.preventDefault(); run(); });

        loadOverview();
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
/// <param name="ArchiveLabel">
/// 归档层这一份在哪儿（规格 §3.4.3 的第 ⑦ 项）。⚠️ 本机磁盘那一档是
/// <c>仅本机</c> —— 那时盘上这份是**唯一副本**（§3.5.1），用户必须知道。
/// </param>
public sealed record PlaybackSearchItem(
    string EvidenceId,
    string Waybill,
    string StartedAt,
    double DurationSeconds,
    string BusinessType,
    string? Codec,
    string ArchiveLabel = "仅本机");

/// <summary>「录像来源」下拉里的一项。</summary>
/// <param name="Id">
/// 原样值 —— 它就是 <see cref="RecordingQuery.SourceDevice"/> 要比的那个串
/// （前端拿它当 <c>&lt;option value&gt;</c>）。**不能拿 <paramref name="Label"/> 去比**：
/// 那个是给人看的「外部导入」四个字，拿它当筛选值会一条都筛不出来。
/// </param>
/// <param name="Count">这一台来源上一共有多少条 —— 只有一台来源时页面据此把那颗下拉收起来。</param>
public sealed record PlaybackSource(string Id, string Label, int Count);

/// <summary>
/// 三张统计卡那一份（设计图 `_36`）。
/// </summary>
/// <remarks>
/// ⚠️ <b>每一个数都能从索引或盘上算出来。</b>算不出来的项（订单联动、上传成功率那类）
/// <b>不在这里，也不在页面上编一个</b>（规格 §13.1）。
/// </remarks>
/// <param name="Earliest">
/// 库里最早那一条的录制时间（ISO 8601）；一条都没有时为 <see langword="null"/>。
/// </param>
/// <param name="RetentionDays">
/// 「预计可保留」的天数。⚠️ <b>是估的</b>：剩余空间 ÷ 最近这几天的日均估算占用。
/// 算不出来（历史不足一天 / 平均每日占用为 0 / 探不到盘）时为 <see langword="null"/>，
/// 页面照图上那句话报「暂无法估算」。
/// </param>
/// <param name="RetentionWindowDays">
/// 上面那个日均是拿**实际几天**摊出来的 —— 页面上要如实说出来，
/// 否则「按 7 天估」与「按 1.5 天估」在用户眼里是同一个数。
/// </param>
/// <param name="EstimatedUsedBytes">
/// ⚠️ 是**录像的估算**占用（<c>CleanupPlanner.EstimateBytes</c>），不是整块盘已用 ——
/// 这张卡问的是「录下来的东西占了多少」，整盘已用会把 Windows 与别的软件算进来。
/// 页面显示时必须带「约」。
/// </param>
/// <param name="DirectoryCount">配置里几个存储目录（`_36` 脚注「共 N 个存储目录」）。</param>
/// <param name="VolumeCount">
/// 这些目录落在**几块盘**上。两个目录落在同一块盘时它与
/// <paramref name="DirectoryCount"/> 不相等，而容量只该算一遍。
/// </param>
public sealed record PlaybackOverview(
    string? Earliest,
    int Count,
    int WaybillCount,
    long EstimatedUsedBytes,
    long TotalBytes,
    long FreeBytes,
    int DirectoryCount,
    int VolumeCount,
    double? RetentionDays,
    int RetentionWindowDays,
    IReadOnlyList<PlaybackSource> Sources);

/// <summary>
/// 「用手机打开」那个二维码（设计图 `_38`）。
/// </summary>
/// <param name="Url">
/// 手机该打开的那个地址。⚠️ **它上面没有访问密钥** —— 局域网里谁打开都能看，
/// 页面必须把这件真事说出来（见 <see cref="PlaybackServer"/> 里 `WriteQrAsync` 的说明）。
/// 挑不到局域网地址时为 <see langword="null"/>，那时看 <paramref name="Problem"/>。
/// </param>
/// <param name="Rows">
/// 模块矩阵，一行一个字符串，<c>'1'</c> 深 <c>'0'</c> 浅，**已经含静区**。
/// 前端拿 canvas 逐格画 —— 不传位图是因为 Core 不该知道怎么画图，
/// 而画几十行方块对浏览器是白送的。
/// </param>
/// <param name="Problem">挑不到地址时的原因（给人看的一句话）；正常时为 <see langword="null"/>。</param>
public sealed record PlaybackQr(
    string? Url,
    int Width,
    int Height,
    IReadOnlyList<string> Rows,
    string? Problem);
