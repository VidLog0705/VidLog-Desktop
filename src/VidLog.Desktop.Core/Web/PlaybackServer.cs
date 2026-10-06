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
public sealed partial class PlaybackServer : IAsyncDisposable
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
