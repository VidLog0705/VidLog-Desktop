using VidLog.Desktop.Core.Index;
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
    public static DesktopServices Create(
        DataLayout layout,
        string? ffmpegPath = null,
        int? playbackPort = DefaultPlaybackPort,
        string? deviceName = null)
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

        var runner = new SystemProcessRunner();
        var toolPath = resolvedFfmpeg ?? "ffmpeg";

        var index = new JsonLinesRecordingIndex(layout.IndexPath);
        var punches = new JsonLinesPunchLog(layout.PunchLogPath);
        var labels = new JsonLinesLabelStore(layout.LabelStorePath);

        var finalizer = new SessionFinalizer(
            new RemuxPipeline(toolPath, runner),
            new DecodeVerifier(toolPath, runner),
            index,
            layout.ArchiveRoot);

        var workspace = new RecordingWorkspace(layout.WorkspaceRoot);
        var orphanRecovery = new OrphanRecovery(workspace, finalizer);

        var search = new RecordingSearch(index, labels);
        var punchNavigation = new PunchNavigation(index, punches);

        var resolvedDeviceName = deviceName ?? Environment.MachineName;
        var devices = new DeviceRegistry(layout.DevicesPath);
        var upload = new UploadReceiver(
            layout,
            index,
            punches,
            labels,
            new DecodeVerifier(toolPath, runner),
            resolvedDeviceName);

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
                },
                search,
                index,
                punchNavigation,
                upload,
                devices,
                resolvedDeviceName);
        }

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
            resolvedDeviceName);
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
