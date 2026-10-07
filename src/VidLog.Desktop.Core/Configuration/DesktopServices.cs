using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.License;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Live;
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

    /// <summary>
    /// 录像成品的落盘位置（设计图 `_43` 的多磁盘）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 界面要读它：两块表的状态、容量条、以及「这一段写到哪块盘上了」
    /// 全从这一个对象来。**别再各算一遍** —— 各算一遍就会出现
    /// 「界面上说 D 盘、实际写在 C 盘」那种没人查得出来的偏差。
    /// </remarks>
    public StorageLocations Storage { get; private init; } = null!;

    /// <summary>归档层的回查实现（清理的前置 gates 用它）。</summary>
    public IArchiveBackend ArchiveBackend { get; private init; } = null!;

    /// <summary>
    /// 百度网盘那一套（队列、对比去重、并发）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>归档层不是「百度网盘」那一档时它是 <see langword="null"/></b> ——
    /// 那时设置页上「百度网盘上传」那一整页的东西全都禁用并写明原因，
    /// 而不是渲染一个按下去没反应的开关（踩坑 #13）。
    /// <para>
    /// ⚠️ 另一种 <see langword="null"/> 是「选了网盘但没配凭据」：那时归档层退回
    /// 目录型那个空根状态（一律拒删），而启动警告把「要配哪两个环境变量」说清楚。
    /// </para>
    /// </remarks>
    public CloudUploadService? CloudUploads { get; private init; }

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
    /// 「哪几条没发到归档层」那本账（T23-A）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 界面要**连同 <see cref="ArchiveRelay"/> 的 <c>LastFailure</c> 一起看**：
    /// <c>LastFailure</c> 只在内存，答的是「**这一趟运行**里最近那次」；
    /// 这个答的是「**重启之前**发生过的那些」。只看前者的话，
    /// 一重启那句话就没了 —— 而那正是最容易出事的时候。
    /// <para>
    /// ⚠️ 归档层是**本机磁盘**时 <see cref="ArchiveRelay"/> 是 <c>null</c>（那时发布是空操作），
    /// 但这本账**照样在**：用户可能刚从 NAS 改回本机，之前欠着的那几条仍然只有一份。
    /// </para>
    /// </remarks>
    public ArchiveFailureLog ArchiveFailures { get; private init; } = null!;

    /// <summary>
    /// 清理链路（规格 §3.5.4 / §3.5.5）。
    /// </summary>
    /// <remarks>
    /// 用法是**两步**：<see cref="CleanupService.PreviewAsync"/> 算给用户看，
    /// 确认之后才 <see cref="CleanupService.RunAsync"/>。
    /// </remarks>
    public CleanupService Cleanup { get; private init; } = null!;

    /// <summary>
    /// 设置页那两颗清理按钮的流程（T26①）—— 「算什么、要不要问、怎么问」。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与 <see cref="Cleanup"/> 的分工：那边管「怎么算、怎么删」，这边管
    /// 「**这一次该算哪一种、算完该跟用户说什么**」。界面拿到结果之后只剩
    /// 「弹框 / 写状态行」两件事，**一个判断都不留**。
    /// </remarks>
    public CleanupFlow CleanupFlow { get; private init; } = null!;

    /// <summary>
    /// 清理审计那本账（T24 的「清理流水」窗口读它）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 界面读的是 <see cref="CleanupAuditLog.LoadPageAsync"/> 而不是
    /// <see cref="CleanupAuditLog.LoadAllAsync"/> —— 后者把坏行**静默跳过**，
    /// 而一个「不许静默」的窗口静默藏几条记录，是这本账最不该有的错。
    /// </remarks>
    public CleanupAuditLog CleanupAudit { get; private init; } = null!;

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
    /// 导入录像（设计图 `_41` 左栏那颗按钮）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与导出**不是一对**：导出的东西**不是录像**（I7，不进索引），
    /// 而导入进来的**就是录像**，与本机录的走同一套检索、回放与清理判定。
    /// </remarks>
    public Import.RecordingImporter Importer { get; private init; } = null!;

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
    /// 机位发现：现在哪几台手机能看（规格 §3.8）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它是**内存里的一张表**，由手机报到喂（`/api/v1/live/announce`），
    /// 界面只读。空表 = 还没有手机报到 —— 那与「手机没开实时共享」是同一件事，
    /// 不是错误。
    /// </remarks>
    public Live.LiveDirectory Live { get; private init; } = null!;

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
    /// <param name="storage">
    /// 录像成品的落盘位置（设计图 `_43` 的多磁盘：保存位置 + 备份位置）。
    /// **不传 = 只有 <c>layout.ArchiveRoot</c> 一个根**，与加多磁盘之前一模一样。
    /// </param>
    /// <param name="archiveDirectories">
    /// 归档层（NAS / 挂载盘）要落的目录，有序。**不传 = 用
    /// <see cref="ArchiveTarget.DirectoryPath"/> 那一个**（老配置的写法）。
    /// </param>
    public static DesktopServices Create(
        DataLayout layout,
        string? ffmpegPath = null,
        int? playbackPort = DefaultPlaybackPort,
        string? deviceName = null,
        IAppLogger? logger = null,
        ArchiveTarget? archive = null,
        IClockSource? clockSource = null,
        StorageLocations? storage = null,
        IReadOnlyList<string>? archiveDirectories = null,
        CloudUploadSettings? cloud = null)
    {
        layout.EnsureCreated();

        // ⚠️ 成品落在哪些盘上，**必须由设置说了算**（设计图 `_43`）。
        // 在它之前这里是一句 `layout.ArchiveRoot`：用户填的「归档目录」
        // 从头到尾没被用过，而清理却是按「归档层不是本机」放开的 ——
        // 于是配了 NAS 的机器上，本机那份**唯一副本**会被当成「已经有备份了」删掉。
        var locations = storage ?? StorageLocations.Single(layout.ArchiveRoot);

        // ⚠️ 不传 = 一个开关都不开的那一组（设计图 `_45` 的「已停用」那一态）。
        // 默认值必须是它：打开「启用自动上传」意味着这台机器上的录像会开始
        // 往公网上传，而那是用户必须自己做的决定。
        var cloudSettings = cloud ?? CloudUploadSettings.Default;

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
        // ⚠️ 公钥**编进程序**（`License/LicensePublicKey.cs`）。环境变量仍然优先 ——
        // 开发和临时换密钥时不用重编程序。
        //
        // ⚠️ 这一行改过一次，改的原因是**原先只有环境变量那条路，而那是条死路**：
        // 客户机上不会有 `VIDLOG_LICENSE_PUBKEY` ⇒ 公钥为空 ⇒ 这里建不出
        // `LicenseService` ⇒ 机位恒为 0 ⇒ 客户粘什么码都激活不了，
        // 而表现是「手机说机位满了、电脑端一个字都不显示」（2026-10-03 报上来的）。
        var overrideKey = Environment.GetEnvironmentVariable("VIDLOG_LICENSE_PUBKEY");

        // ⚠️ **哪把公钥生效**要记一条（§6.1）。2026-10-03 那个事故的全部诊断
        //    就卡在这个问题上：日志里翻不出「这一台到底在拿哪把公钥验」，
        //    于是「码没错、程序不对」和「码是别家的」分不开。
        //    开发机留着 `VIDLOG_LICENSE_PUBKEY` 忘删、结果发出去的包拒收真码，
        //    也是这一行才能一眼看出来的。
        logger?.Log(LogLevel.Info, "许可", string.IsNullOrWhiteSpace(overrideKey)
            ? "激活码公钥：用程序里内置的那把。"
            : "激活码公钥：**被环境变量 VIDLOG_LICENSE_PUBKEY 覆盖了**（开发用的那条路，只有测试机上才该看见这句）。");

        var licenseVerifier = LicenseVerifier.FromEmbeddedKey(
            string.IsNullOrWhiteSpace(overrideKey) ? LicensePublicKey.Base64 : overrideKey);

        var license = licenseVerifier is null
            ? null
            : new LicenseService(
                licenseVerifier,
                new EntitlementStore(layout.LicensePath),
                MachineIdentity.From(new WmiMachineIdentifiers(logger)),
                logger,
                // ⚠️ 必须给：不给的话试用码**一律激活不了**（`EvaluateTrial` 会说
                //    「本机没配上试用记录」）。这个参数是 2026-10-03 加 7 天试用码时加的。
                TrialRecordStore.ForThisMachine(layout.TrialPath, logger));

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

        // ⚠️ 归档层是**本机**那一档时，根只能是 `layout.ArchiveRoot` ——
        // 那一档的语义就是「本机这一份就是归档层那一份」，用户填的目录在这里没有意义
        // （`ArchiveTarget.DirectoryPath` 对本机档永远是 null，见 `FromConfig`）。
        //
        // ⚠️ 其余三档**必须用用户填的那个目录**。给空列表（配了 NAS 却没填目录）时
        // 落到一个空列表上 —— 那样 `VerifyAsync` 一律返回「查不了」⇒ 一律拒删。
        // 那是刻意的：**配了一半的归档层不能反过来变成"可以删"**。
        var archiveRoots = target.IsOnThisMachine
            ? new[] { layout.ArchiveRoot }
            : archiveDirectories is { Count: > 0 }
                ? [.. archiveDirectories]
                : target.DirectoryPath is { Length: > 0 } single
                    ? [single]
                    : [];

        // ── 百度网盘那一档（设计图 `_45` / `_46`）──────────────────────
        //
        // ⚠️ **只有选到网盘那一档才装配它。**凭据（AppKey/AppSecret）走环境变量，
        // 没配就退回目录型那个「根是空的」状态 —— 那个状态一律回「查不了」⇒
        // 一律拒删（§3.5.1 那半句话的同一头），而上面那条归档层警告会把
        // 「这一档要授权」说给用户。**绝不猜一个凭据、也绝不悄悄换成别的档**。
        CloudUploadService? cloudUploads = null;
        IArchiveBackend archiveBackend;

        // 借网盘令牌给手机那件事（`/api/v1/netdisk/token`）。⚠️ 与 `cloudUploads`
        // 同进退 —— 没选网盘那一档、或者没配凭据时是 null，那条路由会如实回
        // 「这一档没启用」，而不是回一个空令牌让手机端自己去猜。
        NetdiskTokenSource? netdisk = null;

        // 归档层的**写入**那一半与回查那一半是两个接口（见 `IArchivePublisher` 的说明），
        // 所以这里分开存：网盘那一档的实现同时是两个，目录型那一档也是。
        IArchivePublisher publisher;

        if (target.Kind == ArchiveBackendKind.Cloud)
        {
            var credentials = BaiduPanCredentials.FromEnvironment();

            if (credentials is null)
            {
                warnings.Add(BaiduPanCredentials.MissingMessage);

                var unreachable = new DirectoryArchiveBackend(archiveRoots, target.Kind);
                archiveBackend = unreachable;
                publisher = unreachable;
            }
            else
            {
                // ⚠️ 超时放大到 10 分钟：默认的 100 秒对一片 4MB 是够的，
                // 但上行慢的工位（网盘限速、手机热点）传一片就能超，
                // 而超时的表现是「上传一直失败」，查起来只会怀疑账号不对。
                var api = new BaiduPanClient(
                    credentials, new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, logger);
                var panLayout = new BaiduPanLayout(cloudSettings.AppName);
                var session = new BaiduPanSession(
                    api, new BaiduPanTokenStore(layout.CloudTokenPath, logger), logger: logger);
                var queue = new UploadQueue(layout.CloudQueuePath, logger);

                netdisk = new NetdiskTokenSource(session, panLayout, logger);

                cloudUploads = new CloudUploadService(
                    session,
                    new BaiduPanUploader(api, logger),
                    api,
                    panLayout,
                    queue,
                    index,
                    labels,
                    locations,
                    cloudSettings,
                    logger);

                var cloudBackend =
                    new CloudArchiveBackend(panLayout, api, session, cloudUploads, logger);

                archiveBackend = cloudBackend;
                publisher = cloudBackend;
            }
        }
        else
        {
            var directory = new DirectoryArchiveBackend(archiveRoots, target.Kind);
            archiveBackend = directory;
            publisher = directory;
        }

        // T18：这本账**发布端写、清理端读**，所以只建一个、两边传同一个。
        // 两条理由：① 只有一个地方决定它是哪个文件，不存在「写的和读的不是同一个」；
        // ② `PublishedStore` 的写互斥是**实例内**的（`SemaphoreSlim`），
        // 建两个实例等于两个闸各管各的，同一行就可能被两个写者交错着追加。
        var published = new PublishedStore(layout.PublishedPath);

        // T23-A：「哪几条没发上去」那本账。**与上面那本分开建**（一个是台账、一个是欠账），
        // 但同样是「发布端写、界面端读」，所以也只建一个。
        var archiveFailures = new ArchiveFailureLog(layout.ArchiveFailurePath, logger);

        var relay = target.IsOnThisMachine
            ? null
            : new ArchiveRelay(publisher, target.Label, published, archiveFailures, logger);

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
            locations,
            logger,
            relay);

        // ⚠️ logger 一定要传：工作区是「读不出来的会话」唯一的留痕出口，
        // 而那一条正是本仓**唯一会丢证据**的方向（见 `ReadManifestAsync`）。
        var workspace = new RecordingWorkspace(layout.WorkspaceRoot, logger);
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
            relay: relay,
            // ⚠️ 传 logger：「清掉暂存区」那一步删的是目录，而失败时原来**没人知道**
            // （见 `CleanupIncomingAsync` 的说明）。
            logger: logger,
            // 手机传上来的录像也落在**用户配的那些盘上**（设计图 `_43`）——
            // 不传的话它会一直写 <root>\archive，而界面上画的是 D 盘。
            storage: locations);

        // 机位发现（规格 §3.8）。⚠️ **无条件建**，即使不起回放服务 ——
        // 界面上那颗「实时多画面」按钮要读它才知道现在有几台手机能看
        // （读一个空表与读一个 null 是两种代码，而它们表达的是同一件事）。
        var live = new LiveDirectory(logger: logger);

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
                    ArchiveRoot = locations.FallbackRoot,
                    // ⚠️ 回放要**在每一个保存位置上找**（多磁盘，设计图 `_43`）——
                    // 只看一个根的话，另一块盘上的录像在网页里会「不存在」。
                    Locations = locations,
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
                logger,
                netdisk,
                // ⚠️ 手机报到的那条路（`/api/v1/live/announce`）挂在**回放服务**上 ——
                // 那台 HTTP 服务本来就无条件起着（L8：检索回放不看许可），
                // 不为推流另开一个端口。
                live);
        }

        // 清理链路（规格 §3.5.4 / §3.5.5）—— **第一个生产调用点**。
        // 装配在这里，触发在 App 层（启动时算一次、给用户看过才动手）。
        var effectiveLogger = logger ?? NullLogger.Instance;

        var cleanupAudit = new CleanupAuditLog(layout.CleanupAuditPath);

        var cleanup = new CleanupService(
            index,
            labels,
            new ReceiptStore(layout.ReceiptsPath),
            published,
            new CleanupExecutor(archiveBackend, locations, cleanupAudit, effectiveLogger),
            effectiveLogger);

        // T26①：【按时间清理…】/【按空间释放…】按下之后该算什么 —— 从 WPF 的
        // 按钮点击事件里搬出来的（母仓 §4「逻辑不许塞进 UI 层」）。
        // ⚠️ 保留期是**每次调用时传进来的**，不在这里存一份：设置页上改完立刻生效，
        // 而这里存的任何快照在保存设置之后就过期了（且过期不会报错，只是清错东西）。
        var cleanupFlow = new CleanupFlow(cleanup, locations);

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
            CloudUploads = cloudUploads,
            Storage = locations,
            ArchiveRelay = relay,
            ArchiveFailures = archiveFailures,
            Live = live,
            Cleanup = cleanup,
            CleanupFlow = cleanupFlow,
            CleanupAudit = cleanupAudit,
            TrustedClock = trustedClock,
            ClockSource = clockSource ?? new HttpDateClockSource(),
            Exporter = new Export.EvidenceExporter(locations, logger),
            // ⚠️ runner 与解码校验器**与收尾用的是同一套**（同一个 ffmpeg、同一个 logger）——
            // 另起一份的话，「导入进来的文件验过没有」这件事就要看是谁验的。
            Importer = new Import.RecordingImporter(
                locations,
                index,
                new DecodeVerifier(toolPath, runner),
                runner,
                toolPath,
                labels,
                relay,
                logger),
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

        // ── 百度网盘（设计图 `_45`）────────────────────────────────────
        //
        // 先恢复上次的登录（令牌文件在应用数据目录里），再把定时检查起起来。
        // ⚠️ **恢复失败绝不能挡住启动**（I4 的同一条精神）：读坏了就当没登录，
        // 用户在设置页点一次「登录百度网盘」就好。所以这里连异常都不往外放。
        if (CloudUploads is not null)
        {
            try
            {
                await CloudUploads.Session.RestoreAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                warnings.Add($"百度网盘的登录信息读不出来，需要重新登录一次（{ex.Message}）。");
            }

            CloudUploads.Start();
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
        CameraSource source,
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
            source,
            // ⚠️ 默认取 **Identity**（凭据已抹掉），不是 Address ——
            // 它会被写进 manifest 与索引，而网络地址里带着摄像头密码。
            sourceDeviceId ?? source.Identity,
            options);
    }

    public async ValueTask DisposeAsync()
    {
        // ⚠️ 先停上传再停回放：上传是**往外发数据**的那一个，
        // 顺序反了的话，关窗的那一瞬间会有一条传到一半的录像 ——
        // 它下次会重传（分片摘要一样，网盘说「这片我有了」），
        // 但队列里会留下一条「上传中」，而重启后它才被翻回「等着传」。
        if (CloudUploads is not null)
        {
            await CloudUploads.DisposeAsync();
        }

        if (Server is not null)
        {
            await Server.DisposeAsync();
        }
    }
}
