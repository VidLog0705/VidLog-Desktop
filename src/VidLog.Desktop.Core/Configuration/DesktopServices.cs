using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.License;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Playback;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Search;
using VidLog.Desktop.Core.Upload;
using VidLog.Desktop.Core.Web;

namespace VidLog.Desktop.Core.Configuration;

/// <summary>启动时发生的事，供界面呈现。</summary>
/// <param name="OrphanOutcomes">本次启动收尾的孤儿会话。空表示没有孤儿。</param>
/// <param name="PlaybackUrl">局域网回放地址；服务没起来时为 <see langword="null"/>。</param>
/// <param name="Warnings">需要让用户看见的问题（比如找不到 FFmpeg）。</param>
public sealed record StartupReport(
    IReadOnlyList<FinalizeOutcome> OrphanOutcomes,
    string? PlaybackUrl,
    IReadOnlyList<string> Warnings)
{
    public int RecoveredCount => OrphanOutcomes.Count(o => o.Succeeded);
    public int FailedCount => OrphanOutcomes.Count(o => !o.Succeeded);
}

/// <summary>
/// 电脑端的对象图装配 —— 应用层只调用这里，不自己 new 一堆东西。
/// </summary>
/// <remarks>
/// 单独抽出来是为了让 <b>启动流程本身可测</b>：
/// 「重启后自动收尾孤儿」（规格 §3.1.1）是发生在启动时的行为，
/// 如果装配逻辑写在 WPF 窗口的构造里，这段就永远测不到。
/// </remarks>
public sealed class DesktopServices : IAsyncDisposable
{
    private DesktopServices(
        DataLayout layout,
        IRecordingIndex index,
        IPunchLog punches,
        ILabelStore labels,
        SessionFinalizer finalizer,
        OrphanRecovery orphanRecovery,
        RecordingSearch search,
        PunchNavigation punchNavigation,
        PlaybackServer? server,
        IReadOnlyList<string> warnings,
        int playbackPort,
        RecordingWorkspace workspace,
        IEncoderProbe encoderProbe,
        string? ffmpegPath,
        UploadReceiver upload,
        DeviceRegistry devices,
        string deviceName)
    {
        PlaybackPort = playbackPort;
        Layout = layout;
        Index = index;
        Punches = punches;
        Labels = labels;
        Finalizer = finalizer;
        OrphanRecovery = orphanRecovery;
        Search = search;
        PunchNavigation = punchNavigation;
        Server = server;
        Warnings = warnings;
        Workspace = workspace;
        EncoderProbe = encoderProbe;
        FfmpegPath = ffmpegPath;
        Upload = upload;
        Devices = devices;
        DeviceName = deviceName;
    }

    public DataLayout Layout { get; }
    public IRecordingIndex Index { get; }
    public IPunchLog Punches { get; }
    public ILabelStore Labels { get; }
    public SessionFinalizer Finalizer { get; }
    public OrphanRecovery OrphanRecovery { get; }
    public RecordingSearch Search { get; }
    public PunchNavigation PunchNavigation { get; }
    public PlaybackServer? Server { get; }

    /// <summary>录制工作区。采集会话把分段落在它的根目录下。</summary>
    public RecordingWorkspace Workspace { get; }

    /// <summary>编码能力探测。规格 §3.1.5：实测，不假定。</summary>
    public IEncoderProbe EncoderProbe { get; }

    /// <summary>本机 FFmpeg 路径；没找到时为 <see langword="null"/>。</summary>
    public string? FfmpegPath { get; }

    /// <summary>回放服务端口。</summary>
    public int PlaybackPort { get; }

    /// <summary>当前用的归档层（规格 §3.4.6）。</summary>
    public ArchiveTarget ArchiveTarget { get; private init; } = ArchiveTarget.Default;

    /// <summary>归档层的回查实现（清理的前置 gates 用它）。</summary>
    public IArchiveBackend ArchiveBackend { get; private init; } = null!;

    /// <summary>
    /// 发布到归档层那一步；<b>归档层就是本机时为 <see langword="null"/></b>。
    /// </summary>
    /// <remarks>
    /// 界面要读它的 <see cref="ArchiveRelay.LastFailure"/> ——
    /// 「归档层那份没发上去」意味着本机这份**只有一份**，用户必须知道，
    /// 否则他可能手动删掉唯一的那一份。
    /// </remarks>
    public ArchiveRelay? ArchiveRelay { get; private init; }

    /// <summary>
    /// 清理链路（规格 §3.5.4 / §3.5.5）。
    /// </summary>
    /// <remarks>
    /// 用法是**两步**：<see cref="CleanupService.PreviewAsync"/> 算给用户看，
    /// 确认之后才 <see cref="CleanupService.RunAsync"/>。
    /// </remarks>
    public CleanupService Cleanup { get; private init; } = null!;

    /// <summary>
    /// 可信时钟（规格 §3.6.4）。录制的闸门与时间来源都在它身上。
    /// </summary>
    public TrustedClock TrustedClock { get; private init; } = null!;

    /// <summary>公网时间源（`IClockSource`）。校准要用它取一次锚。</summary>
    public IClockSource ClockSource { get; private init; } = new HttpDateClockSource();

    /// <summary>
    /// 导出/交付原视频（规格 §3.7）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它**不写索引、不写标签、不进检索** —— 导出件不是录像（I7 改写后的落点）。
    /// </remarks>
    public Export.EvidenceExporter Exporter { get; private init; } = null!;

    /// <summary>
    /// 许可（`docs/04-许可设计.md`）。为 <see langword="null"/> 表示**软件没配好公钥**。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>L8 红线：它只回答「能不能开始新的录制」</b> ——
    /// 检索、回放、导出、交付、清理**一条都不看它**。
    /// </remarks>
    public License.LicenseService? License { get; private init; }

    /// <summary>远端上传的接收方（M5）。</summary>
    public UploadReceiver Upload { get; }

    /// <summary>
    /// 已入网设备与待批准的入网请求（M5）。
    /// </summary>
    /// <remarks>
    /// 界面用它把**二维码**显示出来给用户扫（原来是配对码，2026-09-24 改的）——
    /// 那是「人工批准」真的挡住东西的那一环：码只在本机屏幕上，
    /// 不显示的话手机永远换不到凭据。
    /// </remarks>
    public DeviceRegistry Devices { get; }

    /// <summary>
    /// 本机在回执里的身份（<c>receiverDeviceId</c> / <c>receiverDeviceName</c>）。
    /// </summary>
    /// <remarks>
    /// 与录制会话的 <c>sourceDeviceId</c> 取同一个值（<see cref="Environment.MachineName"/>），
    /// 沿用既有约定，不另造一套设备命名。
    /// </remarks>
    public string DeviceName { get; }

    /// <summary>装配时发现的问题。界面应当把它们显示出来，而不是悄悄吞掉。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>默认回放端口。</summary>
    public const int DefaultPlaybackPort = 8720;

    /// <param name="playbackPort">
    /// 局域网回放端口。传 <see langword="null"/> 表示不起回放服务。
    /// </param>
    /// <param name="deviceName">
    /// 本机在回执里的身份。默认取 <see cref="Environment.MachineName"/> ——
    /// 与录制会话的 <c>sourceDeviceId</c> 同源（<c>AppHost</c> 用的也是它）。
    /// </param>
    /// <param name="logger">
    /// 日志器。**可选**（默认不记）—— 组合根传真的那个。
    /// 这一层是「把 logger 铺给服务」的唯一入口：回放服务、上传接收那些类
    /// 都在这里 new 出来，所以在这里传一次就够，不必让每个调用点都记得。
    /// </param>
    /// <param name="archive">
    /// 归档层配置（规格 §3.4.6）。不传 = 本机磁盘那一档（出厂默认）。
    /// </param>
    public static DesktopServices Create(
        DataLayout layout,
        string? ffmpegPath = null,
        int? playbackPort = DefaultPlaybackPort,
        string? deviceName = null,
        IAppLogger? logger = null,
        ArchiveTarget? archive = null,
        IClockSource? clockSource = null)
    {
        layout.EnsureCreated();

        var warnings = new List<string>();

        var resolvedFfmpeg = FfmpegLocator.TryFind(ffmpegPath, AppContext.BaseDirectory);
        if (resolvedFfmpeg is null)
        {
            warnings.Add(
                "找不到 FFmpeg —— 录像收尾（remux 与解码校验）无法进行。" +
                $"可在应用目录放置 tools/ffmpeg.exe，或设置环境变量 {FfmpegLocator.EnvironmentVariable}。");
        }

        // ⚠️ 这一处、加上下面 finalizer 那一处，就是「把日志铺给服务」的**全部**：
        // FFmpeg 调用全走 runner（remux / 解码校验 / 编码器探测都是它上面的几行），
        // 收尾只有 finalizer 一条路（I9）。两处挂上，五个类一起有记录。
        var runner = new SystemProcessRunner(logger);
        var toolPath = resolvedFfmpeg ?? "ffmpeg";

        var index = new JsonLinesRecordingIndex(layout.IndexPath);
        var punches = new JsonLinesPunchLog(layout.PunchLogPath);
        var labels = new JsonLinesLabelStore(layout.LabelStorePath);

        // ── 许可（`docs/04-许可设计.md`）────────────────────────────────
        //
        // ⚠️ **校验只在启动时做一次**（L7），运行期间冻结 ——
        // 录到一半许可过期了，不该把这一段掐掉。
        //
        // ⚠️ 公钥从环境变量注入（`VIDLOG_LICENSE_PUBKEY`）：本仓**不含任何密钥**，
        // 连公钥也在构建/部署时给（L1/L2 的边界画得更紧一点，代价是多一步配置）。
        // 没配的话激活一律失败，而失败原因是「这台电脑里的软件没配好」。
        var licenseVerifier = LicenseVerifier.FromEmbeddedKey(
            Environment.GetEnvironmentVariable("VIDLOG_LICENSE_PUBKEY") ?? string.Empty);

        var license = licenseVerifier is null
            ? null
            : new LicenseService(
                licenseVerifier,
                new EntitlementStore(layout.LicensePath),
                MachineIdentity.From(new WmiMachineIdentifiers(logger)),
                logger);

        if (license is null)
        {
            warnings.Add(
                "许可没有配置好（缺少内置公钥），激活会失败。"
                + "这是软件安装的问题，不是你激活码的问题。");
        }

        // ── 可信时钟（规格 §3.6.4：未校准不得开始录制）──────────────────
        //
        // 装配在这里、**核对在启动流程里**（`StartAsync`）：核对要读文件、要判跳变，
        // 而那件事的结果要变成一条用户可见的警告。
        var calibration = new CalibrationStore(layout.CalibrationPath);
        var trustedClock = new TrustedClock(
            calibration.LoadAsync().GetAwaiter().GetResult(), calibration, logger: logger);

        // ── 归档层（规格 §3.4.6 的四种后端）────────────────────────────
        //
        // 一处解析，两处发布（收尾那一条路 + 接收远端上传那一条路）。
        // ⚠️ 本机磁盘那一档**不建 relay**：那时本机这一份就是归档层那一份，
        // 发布是空操作，而「发过没有」这个问题在那一档下没有意义。
        var target = archive ?? ArchiveTarget.Default;
        var archiveBackend = new DirectoryArchiveBackend(layout.ArchiveRoot, target.Kind);
        var relay = target.IsOnThisMachine
            ? null
            : new ArchiveRelay(archiveBackend, target.Label, logger);

        if (target.ConfigurationProblem is { } problem)
        {
            // I3：归档层配错了**必须让用户看见**。看不见的后果很具体：
            // 他以为录像已经双份了，于是手动删掉本机上唯一的那一份。
            warnings.Add($"归档层没配好：{problem}本机这份仍然是好的，但它现在只有一份。");
        }

        var finalizer = new SessionFinalizer(
            new RemuxPipeline(toolPath, runner),
            new DecodeVerifier(toolPath, runner),
            index,
            layout.ArchiveRoot,
            logger,
            relay);

        var workspace = new RecordingWorkspace(layout.WorkspaceRoot);
        var orphanRecovery = new OrphanRecovery(workspace, finalizer);

        var search = new RecordingSearch(index, labels);
        var punchNavigation = new PunchNavigation(index, punches);

        var resolvedDeviceName = deviceName ?? Environment.MachineName;
        // ⚠️ 机位闸门接在**入网**这条路上（`docs/04-许可设计.md` §5.1 点名的落点）——
        // 它只拒绝**新的**手机接进来。已经在录的手机、以及电脑端自己的录制与
        // 检索回放，一条都不看许可（L5 / L8）。
        //
        // 许可没配好时 `license` 是 null ⇒ 机位数取 0 ⇒ 一台都接不进来。
        // 那是**刻意的**：这个档位的全部意义就是「允许接几台手机」，
        // 没有激活就没有机位；而上面那条警告已经把「软件没配好」说给用户了。
        var devices = new DeviceRegistry(
            layout.DevicesPath,
            logger: logger,
            seatLimit: () => license?.Status.Slots ?? 0);
        var upload = new UploadReceiver(
            layout,
            index,
            punches,
            labels,
            new DecodeVerifier(toolPath, runner),
            resolvedDeviceName,
            now: null,
            relay: relay);

        PlaybackServer? server = null;
        if (playbackPort is not null)
        {
            server = new PlaybackServer(
                new PlaybackServerOptions
                {
                    // 首选局域网可达的地址；绑不上（多半是没注册 urlacl）就退回本机，
                    // 由 StartAsync 把原因变成一条用户可见的警告。
                    Prefix = $"http://+:{playbackPort.Value}/",
                    FallbackPrefix = $"http://localhost:{playbackPort.Value}/",
                    ArchiveRoot = layout.ArchiveRoot,
                    // 手机端「手动删除」要回查归档层（§3.5.6③）——
                    // 那一份在哪里由归档层配置说了算，不是写死的本机目录。
                    ArchiveBackend = archiveBackend,
                    // 缩略图（规格 §3.4.3）。抽帧走本机 ffmpeg，**结果落盘缓存** ——
                    // 规格明说不许每次进页面都重抽。
                    Thumbnails = resolvedFfmpeg is null
                        ? null
                        : new ThumbnailCache(
                            resolvedFfmpeg, layout.RootDirectory, runner, logger),
                },
                search,
                index,
                punchNavigation,
                upload,
                devices,
                resolvedDeviceName,
                logger);
        }

        // 清理链路（规格 §3.5.4 / §3.5.5）—— **第一个生产调用点**。
        // 装配在这里，触发在 App 层（启动时算一次、给用户看过才动手）。
        var effectiveLogger = logger ?? NullLogger.Instance;

        var cleanup = new CleanupService(
            index,
            labels,
            new ReceiptStore(layout.ReceiptsPath),
            new CleanupExecutor(
                archiveBackend,
                layout.ArchiveRoot,
                new CleanupAuditLog(layout.CleanupAuditPath),
                effectiveLogger),
            effectiveLogger);

        return new DesktopServices(
            layout, index, punches, labels, finalizer, orphanRecovery,
            search, punchNavigation, server, warnings, playbackPort ?? DefaultPlaybackPort,
            workspace,
            // 编码探测要用真 ffmpeg 串行试跑几个候选，所以只装配、不预热 ——
            // 由调用方在开录前跑一次（规格 §3.1.5「首次录制前实测」）。
            new FfmpegEncoderProbe(toolPath, runner),
            resolvedFfmpeg,
            upload,
            devices,
            resolvedDeviceName)
        {
            ArchiveTarget = target,
            ArchiveBackend = archiveBackend,
            ArchiveRelay = relay,
            Cleanup = cleanup,
            TrustedClock = trustedClock,
            ClockSource = clockSource ?? new HttpDateClockSource(),
            Exporter = new Export.EvidenceExporter(layout.ArchiveRoot, logger),
            License = license,
        };
    }

    /// <summary>
    /// 启动：先收尾孤儿，再起回放服务。
    /// </summary>
    /// <remarks>
    /// <b>孤儿收尾必须排在起服务之前</b> —— 否则网页上会出现一批
    /// 还没收尾、因而播不了的记录。
    /// <br/>
    /// 回放服务起不来**不该**让整个应用启动失败（I4 的同一条精神）：
    /// 收尾与录制远比回放重要，回放失败只降级成一条警告。
    /// </remarks>
    public async Task<StartupReport> StartAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>(Warnings);

        // ── 校时（规格 §3.6.3 / §3.6.4）──────────────────────────────
        //
        // 顺序在两件事**之前**：先核对「挂钟与上次退出时留下的记录自不自洽」，
        // 再（需要时）取一次公网时间。
        //
        // ⚠️ 取不到公网时间**不是错误**：一台已经校准过的机器断网照样要用
        // （规格 §3.6.4：已校准状态落盘持久化，之后离线照常录制）。
        // 只有「从没校准过」或「检测到跳变」才是一条必须说出来的警告。
        // 许可：**只在启动时校验一次**（L7）。
        if (License is not null)
        {
            var licenseStatus = await License.CheckAtStartupAsync(cancellationToken);

            if (!licenseStatus.Activated)
            {
                // L5：未激活 / 校验失败 **必须明确告知原因并提供重新激活入口**，
                // 不得静默失败 —— 而它挡的是**新录**，已有录像照常（L8）。
                warnings.Add(
                    $"{licenseStatus.FailureReason}未激活时不能开始新的录制；"
                    + "已有的录像照常可以检索、回放、导出、交付。"
                    + $"本机机器码：{licenseStatus.MachineCode}");
            }
        }

        var jumped = await TrustedClock.CheckStartupAsync(cancellationToken);

        if (jumped)
        {
            warnings.Add(
                $"{TrustedClock.BlockedReason}"
                + "已校准过的机器不会因为断网失去校准 —— 这一次要重新校准，是因为本机时间被改过。");
        }

        if (!TrustedClock.IsCalibrated)
        {
            try
            {
                var anchor = await ClockSource.QueryAsync(cancellationToken);
                await TrustedClock.CalibrateAsync(anchor, CalibrationSource.PublicTime, cancellationToken);
            }
            catch (Exception ex)
            {
                // 取不到就保持「未校准」—— 而那是**会挡住录制**的状态，
                // 所以必须让用户看见（I3：不存在静默失败）。
                warnings.Add(
                    $"取不到公网时间（{ex.Message}）。"
                    + "这台电脑需要先联一次网校准，之后断网也能照常录制。");
            }
        }

        var orphanOutcomes = await OrphanRecovery.RecoverAsync(cancellationToken);

        foreach (var failed in orphanOutcomes.Where(o => !o.Succeeded))
        {
            warnings.Add($"孤儿会话 {failed.Reason} 收尾失败：{failed.FailureReason}");
        }

        string? playbackUrl = null;
        if (Server is not null)
        {
            try
            {
                await Server.StartAsync(cancellationToken);
                playbackUrl = Server.BaseUrl;

                if (Server.IsUsingFallback)
                {
                    // 退回了本机，别的设备就连不上 —— 必须告诉用户为什么、怎么开。
                    warnings.Add(
                        $"回放服务只绑到了本机（{Server.BaseUrl}），其他设备访问不了。" +
                        $"原因：{Server.FallbackReason} " +
                        $"要让手机或其他电脑打开，请以管理员身份执行一次：" +
                        $"netsh http add urlacl url=http://+:{PlaybackPort}/ user=Everyone");
                }
            }
            catch (Exception ex)
            {
                // 端口被占等 —— 不该让应用起不来。收尾与录制远比回放重要。
                warnings.Add($"回放服务未能启动：{ex.Message}");
            }
        }

        return new StartupReport(orphanOutcomes, playbackUrl, warnings);
    }

    /// <summary>
    /// 开一次录制会话。
    /// </summary>
    /// <remarks>
    /// 装配放在这里而不是让界面自己 new，理由与整个类相同：
    /// 这样「会话拿到的是同一套收尾器」是结构保证的，
    /// 而不是靠每个调用点记得传对 —— 规格 §4.1 要求收尾只有一条路径（I9）。
    /// </remarks>
    public RecordingSession CreateRecordingSession(
        string deviceName,
        string? sourceDeviceId = null,
        RecordingSessionOptions? options = null)
    {
        var capture = FfmpegPath is null
            ? throw new InvalidOperationException(
                "没有可用的 FFmpeg，无法采集。请按提示放置 tools/ffmpeg.exe 或设置环境变量。")
            : new FfmpegCameraCapture(FfmpegPath);

        return new RecordingSession(
            Workspace,
            capture,
            Finalizer,
            new DiskSpaceGuard(new DriveSpaceProbe()),
            deviceName,
            sourceDeviceId ?? deviceName,
            options);
    }

    public async ValueTask DisposeAsync()
    {
        if (Server is not null)
        {
            await Server.DisposeAsync();
        }
    }
}
