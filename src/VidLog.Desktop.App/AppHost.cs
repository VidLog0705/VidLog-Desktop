using System.Windows;
using VidLog.Desktop.Core.Camera;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Scanning;
using VidLog.Desktop.App.Platform;

namespace VidLog.Desktop.App;

/// <summary>
/// 应用级装配 —— 活得比窗口长。
/// </summary>
/// <remarks>
/// <para>
/// 原先这些对象都挂在 <see cref="MainWindow"/> 上，窗口一关就全没了。
/// 但规格 §3.2.1 要求**后台/托盘状态下仍然收码** —— 那就必须有人在窗口之外
/// 持有钩子与录制协调器。所以装配上移到这一层，窗口退化成纯视图。
/// </para>
/// </remarks>
public sealed class AppHost : IAsyncDisposable
{
    private readonly FileLogger _logger;

    /// <summary>
    /// 界面层记一条（`AGENTS.md` §6：**状态变更要留痕**）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>为什么要有这个出口</b>：窗口拿不到 logger，而有些动作是**界面独有的**
    /// —— 最典型的是检索窗里那个「锁定」（规格 §3.6.5）：它写一个标签，
    /// 而那个标签直接决定**那条录像会不会被清理**。事后没人能回答
    /// 「这条是谁、什么时候锁的」（2026-09-29 审计查出来的缺口）。
    /// </para>
    /// <para>
    /// 替代方案是让每个窗口自己持一个 logger，但那要改一串构造函数，
    /// 而 **App 层没有测试工程** —— 改错一个就是启动即崩。
    /// </para>
    /// </remarks>
    public void Log(LogLevel level, string category, string message) =>
        _logger.Log(level, category, message);

    /// <summary>
    /// 界面层要往 Core 的构造点**传** logger 时用它（例：设置窗枚举设备）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 那些构造点的 logger 是**可选**参数（默认 `NullLogger`）⇒ **不传就静默不落盘**，
    /// 而编译器一个字都不会说。所以宁可多开这一个出口 ——
    /// 2026-09-29 就是靠「逐个核对调用点」才发现设置窗那两处漏了。
    /// </remarks>
    public IAppLogger Logger => _logger;

    private AppHost(
        DesktopServices services,
        StartupReport startup,
        AppSettings settings,
        FileLogger logger,
        WindowsKeyboardHook hook,
        KeyboardScanBridge bridge,
        RecordingCoordinator coordinator)
    {
        Services = services;
        Startup = startup;
        Settings = settings;
        _logger = logger;
        Hook = hook;
        Bridge = bridge;
        Coordinator = coordinator;
    }

    public DesktopServices Services { get; }

    /// <summary>
    /// 启动报告：本次收尾了哪些孤儿、回放服务有没有起来。
    /// </summary>
    /// <remarks>
    /// 界面要它来回答「上次崩掉的那段救回来没有」—— 那个问题**只有这里答得了**：
    /// <see cref="DesktopServices.StartAsync"/> 跑完就把结果交出来了，
    /// 之后再没人能重建这个事实。
    /// </remarks>
    public StartupReport Startup { get; }

    public AppSettings Settings { get; private set; }
    public WindowsKeyboardHook Hook { get; }
    public KeyboardScanBridge Bridge { get; }
    public RecordingCoordinator Coordinator { get; }

    /// <summary>
    /// **实际会用**的录制规格 —— **最近一次**真开相机探测的结论。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 它与 <see cref="Settings"/> 里用户选的那两档**可能不一样** ——
    /// 规格 §3.1.7 要求「**回落必须可见**……**不得静默回落**」，
    /// 所以界面必须显示这一个，而不是用户选的那个。
    /// </para>
    /// <para>
    /// ⚠️ 它是**最近一次探测**的结论，不一定是「用户现在选的那一档」的结论：
    /// 探测发生在启动时、以及每一次改了编码 / 分辨率之后的开段之前
    /// （<see cref="PrepareCaptureAsync"/>）。要判断这两者是不是同一件事，
    /// 拿 <see cref="ProbedSpec"/> 比（界面就是这么做的）。
    /// </para>
    /// </remarks>
    public RecordingSpec EffectiveSpec { get; private set; } = RecordingSpec.Default;

    /// <summary>回落的原因；没回落过时为 <see langword="null"/>。</summary>
    public string? SpecFallbackReason { get; private set; }

    /// <summary>
    /// 开录时用的编码器名（`-c:v` 要的值）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它是**实测编出过片子**的那个（<see cref="SpecSelection.EncoderName"/>），
    /// 不是「这台机器上存在的编码器」里挑的一个。见 §79。
    /// </remarks>
    public string EncoderName { get; private set; } = "libx264";

    /// <summary>
    /// 本次运行真正在用的那一路画面源；<see cref="CameraSource.IsEmpty"/> 表示没找到。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>它是「启动时定下来的那个」，不是用户在设置里刚选的那个</b> ——
    /// 摄像头改了要重启才生效（界面上就是这么写的）。拆窗之后主窗口不再有
    /// 摄像头下拉，它要靠这一个属性回答「现在到底有没有摄像头」。
    /// <para>
    /// ⚠️ 界面上要用 <see cref="CameraSource.Display"/> 或 <see cref="CameraSource.Identity"/>，
    /// **不要**碰 <see cref="CameraSource.Address"/> —— 网络那一路的地址里带凭据。
    /// </para>
    /// </remarks>
    public CameraSource Camera { get; private set; } = CameraSource.None;

    /// <summary>本次运行真正在用的摄像头名字（已抹掉凭据）—— 界面上直接显示这个。</summary>
    public string DeviceName => Camera.Identity;

    /// <summary>
    /// 上一次**真探过**的那一对（编码 + 分辨率）—— 用户改的就是它、
    /// 没改就<b>不重探</b>。见 <see cref="PrepareCaptureAsync"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 缓存键是**编码 + 分辨率**，**不含方向**：方向是滤镜，与「这台机器
    /// 编不编得动这个组合」无关（`RecordingSpec.FallbacksFrom` 也保方向），
    /// 改方向不该真开一次相机。而重探一次的代价是几秒（要真录 1 秒再解码验）。
    /// </para>
    /// <para>
    /// ⚠️ 界面**要拿它来判断「<see cref="EffectiveSpec"/> 是不是已经过期了」**：
    /// 用户刚把编码改掉、但还没开始工作时，<see cref="EffectiveSpec"/> 说的仍然是
    /// **上一次**探测的结论。拿它去跟用户新选的比，会得到一句「这台电脑跑不通」的
    /// **假话** —— 而那一档根本还没测过。见 `SettingsWindow.ShowEffectiveSpec`。
    /// </para>
    /// </remarks>
    public RecordingSpec ProbedSpec { get; private set; } = RecordingSpec.Default;

    public TrayIcon? Tray { get; private set; }

    /// <summary>关窗口时问一句的钩子 —— 由窗口提供（它知道怎么弹对话框）。</summary>
    /// <remarks>
    /// 不给 AppHost 直接引用窗口：那样两者互相依赖，而装配层不该知道界面长什么样。
    /// </remarks>
    public Func<Task<bool>>? ConfirmExitWhileRecording { get; set; }

    /// <summary>
    /// 建托盘并接上「显示窗口 / 退出」。
    /// </summary>
    /// <remarks>
    /// 规格 §3.2.1 要求后台仍能收码 —— 钩子是全局的、与焦点无关，
    /// 所以收进托盘之后扫码照常工作，这正是托盘存在的意义。
    /// </remarks>
    public TrayIcon AttachTray(Action showWindow, Action exitApplication)
    {
        var tray = new TrayIcon("VidLog · 工位录像");
        tray.BuildMenu(showWindow, exitApplication);
        tray.UiRequested += showWindow;
        Tray = tray;

        _logger.Log(LogLevel.Info, "启动", "托盘已就绪");
        return tray;
    }

    /// <summary>
    /// 走完退出流程：收尾在录的段、拆托盘、释放服务。
    /// </summary>
    public async Task<bool> ShutdownAsync()
    {
        if (Coordinator.CurrentWaybill is not null && ConfirmExitWhileRecording is not null)
        {
            if (!await ConfirmExitWhileRecording())
            {
                return false;
            }
        }

        // 收尾在录的段。不收的话会留下一个未收尾的分段 ——
        // 下次启动的孤儿恢复能接上，但当场收掉对用户更清楚。
        await Coordinator.StopWorkAsync();

        Tray?.Dispose();
        Tray = null;

        await DisposeAsync();
        return true;
    }

    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>扫码枪识别到一个单号（回到调用方的线程上）。</summary>
    public event Action<ScanOutcome>? Scanned;

    /// <summary>协调器要告诉用户的事。</summary>
    public event Action<CoordinatorNotice>? Notice;

    /// <summary>日志器的参数：生产 INFO、开发 DEBUG，保留天数取用户设置。</summary>
    /// <remarks>
    /// 级别用**构建配置**判断 —— 这是「这份二进制是给谁跑的」唯一一个不用猜的信号。
    /// 生产上开着 DEBUG，日志会被淹掉，而**淹掉的日志等于没有日志**。
    /// </remarks>
    private static FileLogOptions LogOptionsFor(DataLayout layout, AppSettings settings)
    {
#if DEBUG
        const LogLevel minLevel = LogLevel.Debug;
#else
        const LogLevel minLevel = LogLevel.Info;
#endif

        return new FileLogOptions(layout.LogDirectory, "vidlog", settings.LogRetainDays, minLevel);
    }

    /// <summary>
    /// 建一个**先于设置加载**的日志器：崩溃兜底要用它。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它用的是 <see cref="AppSettings.Default"/> 的保留天数，而真正的保留期
    /// 在 <see cref="StartAsync"/> 里按用户设置算 ——
    /// 这不矛盾：<c>RetainDays</c> **只被 <c>PurgeExpired</c> 读**，
    /// 而这个日志器自带的那个副本没人读。文件名与级别都不受此影响。
    /// （写在这里是为了别让下一个人以为这是个 bug。）
    /// </remarks>
    public static FileLogger CreateBootstrapLogger()
    {
        var layout = DataLayout.Default();
        return new FileLogger(LogOptionsFor(layout, AppSettings.Default));
    }

    /// <param name="logger">
    /// 已经建好的日志器。<c>App</c> 会先建一个并把崩溃钩子挂上去
    /// （<c>Platform/CrashGuard</c>）—— **不传也能跑**（自己建一个），
    /// 但那样「启动过程中崩掉」就没有记录了。
    /// </param>
    public static async Task<AppHost> StartAsync(
        FileLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var layout = DataLayout.Default();
        layout.EnsureCreated();

        var store = new SettingsStore(layout.SettingsPath);
        var loaded = await store.LoadAsync(cancellationToken);
        var settings = loaded.Settings;

        var logOptions = LogOptionsFor(layout, settings);
        logger ??= new FileLogger(logOptions);

        // 保留期的那一跳（2026-09-26 补）。`LogRetention.SelectExpired` 一直是写对的、
        // 也一直有测试，**只是从来没有人调它** —— 于是「保留 N 天」是个死值，日志只增不减。
        // 与「装配的最后一跳」同一类毛病（见 docs/实现决策.md）：方法写对了、没人调。
        var purged = FileLogger.PurgeExpired(logOptions, DateTimeOffset.Now);

        var warnings = new List<string>(loaded.Warnings);

        var services = DesktopServices.Create(
            layout,
            playbackPort: settings.PlaybackPort,
            logger: logger,
            // 归档层（规格 §3.4.6）。⚠️ 它决定两件事：成品的第二份发到哪儿，
            // 以及**允不允许开本地清理**（§3.5.1）—— 所以必须在装配期就定下来，
            // 而不是等到清理那一刻才读设置。
            archive: settings.Archive);

        // ── 装配的最后一跳（曾经漏掉过，别再删）────────────────────────
        // 两件事都发生在这里：收尾上次没走完的孤儿（规格 §3.1.1），
        // 再起回放服务（M3 的验收项）。
        //
        // ⚠️ 漏掉它**不会有任何测试变红** —— 它落在 App 层，而 App 层没有测试工程。
        // 后果是三个功能静默失效：孤儿永不被收尾（那段录像永远播不了、进不了索引）、
        // 回放服务永不起、「局域网回放页」按钮永久禁用。三者的表现都只是「没反应」。
        // 见 docs/实现决策.md「装配的最后一跳」。
        var startup = await services.StartAsync(cancellationToken);
        warnings.AddRange(startup.Warnings);

        // 编码器：规格 §3.1.5 要求实测，不假定。
        //
        // ⚠️ 这一次探的是**合成源上的 H.264 编码器**（`EncoderProbe` 的候选表全是 H.264），
        // 它只答「这台机器有没有能用的编码器」——**不是**录制要用的那个。
        // 录制要用的编码器是下面按规格探出来的（`selection.EncoderName`），见 §79。
        var encoder = EncoderSelection.Select(await services.EncoderProbe.ProbeAsync(cancellationToken));
        if (encoder is null)
        {
            warnings.Add("本机没有任何可用的 H.264 编码器，无法录制。");
        }

        var camera = await ResolveCameraAsync(services, settings, warnings, logger, cancellationToken);

        // 麦克风（规格 §3.1.8）。⚠️ 与摄像头同一个时机解析：都在**启动时**一次，
        // 而「改了下次开始工作才生效」是靠 `SessionOptions.Microphone` 在
        // 每次开始工作时整份交给会话保证的（见 ApplySettingsAsync）。
        var microphone = await ResolveMicrophoneAsync(services, settings, warnings, logger, cancellationToken);

        // ── 录制规格：**真实**的可用性检查（规格 §3.1.7）──────────────────
        //
        // ⚠️ 为什么要真开一次相机：规格原话「组合是稀疏的……设备能真跑通的**远少于**这个数
        // （4K + H.265 在多数手机上就不行）。所以**录制前要做一次真实的可用性检查**，
        // 而不是假定『列出来了就能用』」。合成源什么尺寸都收，只有真相机能说话。
        //
        // ⚠️ 回落的结论**必须说出来**：规格「**不得静默回落**」。
        //
        // ⚠️ **方向要一起塞进来**：它也在 spec 里，而下面那条 `With(selection.Spec)`
        // 与采集对象读的都是 spec 上的方向。不塞的话，**每次刚启动时方向都是「不转」**
        // —— 一直要等用户在设置里点一次【应用】才会被 `SaveSettingsAsync` 补上。
        // 2026-09-30 实测撞到的正是这个（`docs/实现决策.md` §80 同一批）。
        var wantedSpec = new RecordingSpec(settings.Codec, settings.Resolution, settings.Rotation);
        var selection = services.FfmpegPath is null
            ? new SpecSelection(wantedSpec, false, null)
            : await SpecSelectionPolicy.SelectAsync(
                wantedSpec, camera, new FfmpegSpecProbe(services.FfmpegPath, new SystemProcessRunner(logger)),
                cancellationToken);

        if (selection.ChangedFromRequested)
        {
            // 说法在 Core 那边收口（`SpecSelectionPolicy.Describe`）——
            // 一个标志盖了「真回落」与「探测全没通过」两种情形，句子不一样，
            // 而那句话放在 Core 才**测得到**（App 层没有测试工程）。
            warnings.Add(SpecSelectionPolicy.Describe(selection, wantedSpec));
        }

        logger.Log(LogLevel.Info, "启动", $"录制规格 {selection.Spec.Label}", new Dictionary<string, object?>
        {
            ["用户选的"] = wantedSpec.Label,
            ["回落"] = selection.ChangedFromRequested,
        });

        // ⚠️ **开录要用的编码器是规格探测的结论**，不是上面那个 H.264-only 的
        // `EncoderSelection` —— 那个只用来答「这台机器有没有能用的编码器」
        // （没有就警告一声），拿它当 `-c:v` 正是 §79「选 H.265、录出 H.264」的根因。
        // 只有连规格都一个都没探通时（`EncoderName` 为 null）才退回它。
        var chosenEncoder = selection.EncoderName ?? encoder ?? "libx264";

        var coordinator = new RecordingCoordinator(
            services.Workspace,
            services.FfmpegPath is null
                ? throw new InvalidOperationException("没有可用的 FFmpeg，无法采集。")
                : new FfmpegCameraCapture(services.FfmpegPath, selection.Spec),
            services.Finalizer,
            new DiskSpaceGuard(new DriveSpaceProbe()),
            services.Punches,
            logger,
            new WorkModePolicy(settings.Mode, settings.IdleReminder, settings.IdleReminderMinutes),
            new CoordinatorOptions(
                camera,
                // ⚠️ 本机的**身份**（写进 manifest 与索引的那一个）——
                // 它说的是「这段录像是哪台机器录的」，与摄像头无关，所以是机器名。
                // **不要**换成摄像头的地址：那个带凭据。
                Environment.MachineName,
                chosenEncoder,
                // 重复单号检测回看几天（规格 §3.2.5 的「N 可配置」，0 = 关闭）。
                settings.DuplicateCheckDays),
            // 错误扫描（规格 §6.1「必须保存的事实」）。
            new ScanErrorLog(layout.ScanErrorsPath),
            // ⚠️ 可信时钟 —— **未校准不得开始录制**（规格 §3.6.4）。
            // 传真的那个（不是 null）：`null` 是给测试留的「不设闸」，
            // 而生产路径上一次都不该出现。
            trustedClock: services.TrustedClock,
            // ⚠️ 许可 —— **未激活不得录制**（`04-许可设计.md` L5）。
            // 同样传真的那个；它**只挡新录**，已有录像照常（L8）。
            license: services.License,
            // 重复单号检测（规格 §3.2.5）—— 接**检索**那一层：
            // 它的 `SearchAsync` 已经实现了「按单号精确查」+ 单号归一化
            // （`WaybillNumber.Normalize`），再写一份就会与它走岔。
            // ⚠️ 关了（天数 ≤ 0）时**根本不给这个委托** —— 那样协调器连
            // 一次索引都不用读。
            duplicateProbe: settings.DuplicateCheckDays <= 0
                ? null
                : async (waybill, token) =>
                {
                    var hits = await services.Search.SearchAsync(
                        new Core.Search.RecordingQuery
                        {
                            WaybillText = waybill.Value,
                            MatchMode = Core.Search.WaybillMatchMode.Exact,
                            // 只需要「有没有 / 几次」，50 条足够说明问题。
                            Limit = 50,
                        },
                        token);

                    return hits.Select(h => h.Entry).ToList();
                })
        {
            // 分段时长与时长兜底（规格 §3.1.1 / §3.3.4）。
            // 不填的话用的是硬编码默认（1 分钟 / 30 分钟）——
            // 界面上那两个档位就成了「改了没反应」（踩坑 #13）。
            // 录制规格也在这里交给会话 —— 收尾时它要写进索引（§3.1.7 的连带项）。
            // ⚠️ 方向**在 spec 里**（`RecordingSpec.Rotation`），所以它跟着
            // `selection.Spec` 一起进来 —— 回落表保方向，见 `FallbacksFrom`。
            SessionOptions = RecordingSessionOptions
                .From(settings.SegmentMinutes, settings.DurationFallback)
                .With(selection.Spec)
                .WithMicrophone(microphone),
        };

        var bridge = new KeyboardScanBridge(settings.Scanner);
        var hook = new WindowsKeyboardHook();

        var host = new AppHost(services, startup, settings, logger, hook, bridge, coordinator)
        {
            Warnings = warnings,
            EffectiveSpec = selection.Spec,
            SpecFallbackReason = selection.Reason,
            EncoderName = chosenEncoder,
            Camera = camera,
            // 重探的**基准**：启动时已经真探过 `wantedSpec` 了，
            // 别让第一次开段白探一遍（那是真开一次相机、几秒钟）。
            ProbedSpec = wantedSpec,
        };

        // §79 + §80 的落点：编码 / 分辨率改了 ⇒ **开段之前**重探一次
        // （那一刻相机必然是空的，见 `RecordingCoordinator.PrepareCaptureAsync`）。
        // ⚠️ 必须在 `host` 建好之后挂 —— 重探要把结论写回 host 的那几个属性。
        coordinator.PrepareCaptureAsync = host.PrepareCaptureAsync;

        // 清理也留痕（AGENTS.md §6「关键操作必须留痕」）——
        // 一条日志都没有的清理，出事时说不清它到底跑没跑。
        if (purged > 0)
        {
            logger.Log(LogLevel.Info, "启动", $"清掉了 {purged} 个过期日志文件");
        }

        // 摄像头识码（规格 §3.2.1 的第二种入口）。装在协调器上，
        // 【开始工作】时会自动开始取景，扫到单号自动开录。
        if (services.FfmpegPath is { } ffmpegPath && !camera.IsEmpty)
        {
            var scanner = new CameraFrameScanner(
                ffmpegPath, camera, new ZXingFrameScanner(), logger)
            {
                // ⚠️ 与录制那一档**必须一致**（两边朝向不一致会出现
                // 「录出来是正的、识码却要倒着认」）。它在**每次开始工作**时才被读到，
                // 所以改设置走下面那处同步即可，不需要重启。
                Rotation = settings.Rotation,
            };

            scanner.Scanned += waybill =>
            {
                logger.Log(LogLevel.Info, "识码", $"取景识别到 {waybill.Value}");
                _ = coordinator.SubmitAsync(waybill, PunchSource.CameraDecoder);
            };

            // I3：识码起不来要说出来，否则用户只会觉得「摄像头怎么不好使」。
            scanner.Failed += message => host.RaiseNotice(
                new CoordinatorNotice(CoordinatorNoticeKind.FinalizeFailed, null, message));

            coordinator.Scanner = scanner;
        }

        host.Wire();
        logger.Log(LogLevel.Info, "启动", "应用已启动",
            new Dictionary<string, object?>
            {
                // ⚠️ 取 **Identity**（凭据已抹掉），不是 Address：
                // 网络摄像头的地址里带**用户自己的密码**，而这里是日志文件。
                ["摄像头"] = camera.Identity,
                ["编码器"] = encoder,
                ["工作模式"] = settings.Mode,
            });

        return host;
    }

    /// <summary>把一条通知发给订阅者（装配期也要能发）。</summary>
    internal void RaiseNotice(CoordinatorNotice notice) => Notice?.Invoke(notice);

    private void Wire()
    {
        Coordinator.Notice += n => Notice?.Invoke(n);

        // 钩子在自己的线程上回调，而消费方（界面）是 UI 线程 —— 派回去。
        Hook.KeyEvent += (raw, timestamp) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                () => Bridge.Accept(raw, timestamp));

        Bridge.Scanned += outcome =>
        {
            _logger.Log(LogLevel.Info, "扫码", $"识别到 {outcome.Waybill.Value}",
                new Dictionary<string, object?> { ["原始"] = outcome.Raw });

            Scanned?.Invoke(outcome);
            _ = SubmitScanAsync(outcome);
        };
    }

    private async Task SubmitScanAsync(ScanOutcome outcome)
    {
        try
        {
            await Coordinator.SubmitAsync(outcome.Waybill, PunchSource.KeyboardScanner);
        }
        catch (Exception ex)
        {
            // I3：识别到但没能开录，必须让用户看见。
            _logger.Log(LogLevel.Error, "扫码", $"处理识别结果失败：{ex.Message}");
            Notice?.Invoke(new CoordinatorNotice(
                CoordinatorNoticeKind.FinalizeFailed, outcome.Waybill, $"开录失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 装上全局钩子。
    /// </summary>
    /// <returns>装上返回 true；装不上返回 false 并给出一条用户可见的警告。</returns>
    public bool StartKeyboardHook()
    {
        Hook.Start();

        if (Hook.IsInstalled)
        {
            _logger.Log(LogLevel.Info, "扫码", "全局键盘钩子已装上");
            return true;
        }

        // I3：装不上是**必须让用户知道**的事 —— 否则他会一直奇怪扫码枪怎么没反应。
        var reason = Hook.LastError ?? "未知原因";
        _logger.Log(LogLevel.Error, "扫码", $"全局键盘钩子装不上：{reason}");
        Notice?.Invoke(new CoordinatorNotice(
            CoordinatorNoticeKind.FinalizeFailed, null,
            $"后台扫码不可用（{reason}）。单号仍可在窗口里手动输入。"));
        return false;
    }

    public async Task SaveSettingsAsync(AppSettings next)
    {
        var changes = SettingsStore.DescribeChanges(Settings, next);

        // ⚠️ 必须在 `Settings = next` **之前**算：赋值之后这两个比较就恒为假了。
        var audioChanged =
            next.RecordAudio != Settings.RecordAudio
            || !string.Equals(next.MicrophoneDevice, Settings.MicrophoneDevice, StringComparison.Ordinal);

        await new SettingsStore(Services.Layout.SettingsPath).SaveAsync(next);
        Settings = next;

        // 档位立即生效（下次开段时取值）—— 界面上写着「下次录段生效」，
        // 不跟着更新的话那句话就是假的。
        //
        // ⚠️ 在**现有值**上改字段，不是重新 `From(...)`：`From` 从 `Default` 起算，
        // 会把已经探好的录制规格抹成 null（水印尺寸跟着掉回 1280×720、
        // 索引里也不再记编码）—— 那种值不报错，只是「有时候对、有时候不对」。
        var options = Coordinator.SessionOptions
            .WithSchedule(next.SegmentMinutes, next.DurationFallback);

        // 方向（规格 §3.1.7 的需求变更）：它在 spec 里，改它**不必重新探测** ——
        // 旋转是滤镜，与「这台机器能不能编这个组合」无关（回落表也保方向，
        // 见 `RecordingSpec.FallbacksFrom`）。所以它在这里直接改掉即可。
        //
        // ⚠️ 编码 / 分辨率**不能**照这样改：那两个变了得**重探**（真开一次相机），
        // 而这里不能探 —— 相机是独占的，此刻多半正被取景识码占着，那时候探
        // 会把能用的组合**误判成跑不通**。所以它们在**开段之前**改，
        // 见 `PrepareCaptureAsync`（§79 / §80 的落点）。
        if (options.Spec is { } spec)
        {
            options = options.With(spec with { Rotation = next.Rotation });
        }

        // ⚠️ 取景识码那一档**也要同步** —— 两边朝向不一致会出现
        // 「录出来是正的、识码却要倒着认」（或者反过来），
        // 而那种毛病看起来像「识码坏了」，不会有人想到是方向设置。
        if (Coordinator.Scanner is { } scanner)
        {
            scanner.Rotation = next.Rotation;
        }

        // 音轨（规格 §3.1.8）：只有音频那两项真变了才重新枚举设备 ——
        // 每次存设置都起一次 ffmpeg 枚举设备是白花 0.3 秒。
        if (audioChanged)
        {
            var picked = await ResolveMicrophoneAsync(Services, next, [], _logger, CancellationToken.None);
            options = options.WithMicrophone(picked);

            // ⚠️ 开着却一个麦克风都没有时要**说出来**：否则用户以为声音打开了，
            // 而录出来的全是默片 —— I3 不允许静默降级。
            if (next.RecordAudio && picked is null)
            {
                Notice?.Invoke(new CoordinatorNotice(
                    CoordinatorNoticeKind.FinalizeFailed, null,
                    "没有找到麦克风，之后录的都不会有声音。"));
            }
        }

        Coordinator.SessionOptions = options;
        Coordinator.Mode = next.Mode;
        Coordinator.IdleReminder = next.IdleReminder;
        Coordinator.IdleReminderMinutes = next.IdleReminderMinutes;

        if (changes.Count > 0)
        {
            // AGENTS.md §6：配置变更要留痕，密钥类字段只记「已修改」。
            _logger.Log(LogLevel.Info, "设置", "设置已变更",
                new Dictionary<string, object?> { ["变更"] = string.Join("；", changes) });
        }
    }

    /// <summary>
    /// **开段之前**把用户现在选的编码 / 分辨率落实下去 —— 改了就在这里重探一次。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规格 §3.1.7 的原话是「录制前可选、录制中不可改、**改了下次开始工作**才生效」，
    /// 而且判据里明写**不用重启程序**。2026-09-30 之前这句话是假的：规格在构造
    /// 协调器那一刻就定死了，改设置只写穿方向 ⇒ 「改成 4K 录出来还是 1080P」。
    /// 见 <c>docs/实现决策.md</c> §80。
    /// </para>
    /// <para>
    /// ⚠️ <b>为什么重探挂在这儿、不挂在保存设置那一刻</b>：重探要**真开一次相机**
    /// （<see cref="FfmpegSpecProbe"/> 真录 1 秒再解码验产物），而相机是独占的、
    /// 保存设置那一刻多半正被取景识码占着 —— 那时候探会把**能用的组合误判成跑不通**，
    /// 那比不探更坏。协调器保证调用这一刻相机是空的（它刚把取景放掉）。
    /// </para>
    /// <para>
    /// ⚠️ <b>三样东西必须一起换</b>，少换一样就是 §79 / §80 那两种毛病：
    /// 采集对象（规格钉在它里面，决定输入侧的 <c>-video_size</c> / <c>-framerate</c>）、
    /// 编码器（按编码选出来的，换了编码不换它就是「选 H.265 录 H.264」）、
    /// 会话选项里的 spec（决定索引里记的编码分辨率与水印尺寸）。
    /// </para>
    /// <para>
    /// ⚠️ 这个方法是**每次开段**都会走的（每一次扫码），所以「没改就立刻返回」
    /// 那一条不是优化，是前提。
    /// </para>
    /// </remarks>
    private async Task PrepareCaptureAsync(CancellationToken cancellationToken)
    {
        var wanted = new RecordingSpec(Settings.Codec, Settings.Resolution, Settings.Rotation);

        // 没改就立刻返回 —— 见上面最后一条备注。比的是编码 + 分辨率，不含方向。
        if (wanted.Codec == ProbedSpec.Codec && wanted.Resolution == ProbedSpec.Resolution)
        {
            return;
        }

        // 启动那次探测没跑的前提（没有 FFmpeg、或者一台摄像头都没有）在这里是同一件事：
        // 没什么可探的，别白开一次相机（那还会白等几秒）。
        if (Services.FfmpegPath is not { } ffmpegPath || Camera.IsEmpty)
        {
            return;
        }

        var selection = await SpecSelectionPolicy.SelectAsync(
            wanted, Camera, new FfmpegSpecProbe(ffmpegPath, new SystemProcessRunner(_logger)),
            cancellationToken);

        // ⚠️ 探完了才记基准：探的过程抛异常（取消、进程起不来）时基准不动，
        // 下次开段会重来一遍 —— 而「记下了却没换上去」会让用户永远停在旧规格上。
        ProbedSpec = wanted;

        Coordinator.Capture = new FfmpegCameraCapture(ffmpegPath, selection.Spec);
        Coordinator.Encoder = selection.EncoderName ?? EncoderName;

        // ⚠️ 方向取**此刻**的设置，不取探测开始时读的那一份：这个方法跑在后台线程上，
        // 而改方向走的是 `SaveSettingsAsync`（UI 线程）**直接写穿**这一条路。
        // 用户恰好在重探这几秒里点了【应用】的话，拿旧方向上写会把刚存的那个抹掉。
        // （方向不参与探测，所以这里换掉它不影响上面刚验过的结论。）
        Coordinator.SessionOptions = Coordinator.SessionOptions
            .With(selection.Spec with { Rotation = Settings.Rotation });

        // 设置窗里那句「你选的是 X，实际按 Y 录」靠的就是这两个属性。
        EffectiveSpec = selection.Spec;
        SpecFallbackReason = selection.Reason;
        if (selection.EncoderName is { } probed)
        {
            EncoderName = probed;
        }

        _logger.Log(LogLevel.Info, "录制", $"录制规格已改为 {selection.Spec.Label}",
            new Dictionary<string, object?>
            {
                ["用户选的"] = wanted.Label,
                ["回落"] = selection.ChangedFromRequested,
                ["编码器"] = Coordinator.Encoder,
            });

        // 规格 §3.1.7：「**不得静默回落**」—— 改完设置探出来的组合与用户选的
        // 不一样，必须**当场**说出来，而不是等他下次开设置窗才看见。
        // 说法在 Core 那边收口（`SpecSelectionPolicy.Describe`）：与启动时那句是同一份。
        if (selection.ChangedFromRequested)
        {
            Notice?.Invoke(new CoordinatorNotice(
                CoordinatorNoticeKind.FinalizeFailed, null,
                SpecSelectionPolicy.Describe(selection, wanted)));
        }
    }

    private static async Task<CameraSource> ResolveCameraAsync(
        DesktopServices services, AppSettings settings, List<string> warnings,
        IAppLogger logger, CancellationToken cancellationToken)
    {
        // ⚠️ 网络摄像头**不枚举本机设备**：地址是用户手填的，
        // 与「本机有几台 USB 摄像头」完全无关。反过来也一样 ——
        // 选了网络摄像头却还去枚举，会让一个地址配错的人先去怀疑他的摄像头。
        if (settings.Camera.IsNetwork)
        {
            if (settings.Camera.ConfigurationProblem is { } problem)
            {
                warnings.Add($"{problem}录像无法开始。");
            }

            return settings.Camera;
        }

        if (services.FfmpegPath is null)
        {
            return CameraSource.Local(settings.CameraDevice);
        }

        var devices = await DshowDevices.ListVideoAsync(services.FfmpegPath, logger, cancellationToken);

        if (devices.Count == 0)
        {
            warnings.Add("没有找到摄像头，无法录制。");
            return CameraSource.Local(settings.CameraDevice);
        }

        // 之前用过的设备优先 —— 换了 USB 口之后设备名可能变，所以找不到就退回第一个。
        if (settings.CameraDevice is { Length: > 0 } remembered && devices.Contains(remembered))
        {
            return CameraSource.Local(remembered);
        }

        return CameraSource.Local(devices[0]);
    }

    /// <summary>
    /// 定下这次用的麦克风（规格 §3.1.8）。<see langword="null"/> = 不录声音。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>「设置里开着」不等于「有麦克风」</b>，更不等于「麦克风接得上」——
    /// 这里只解决前两件事（关着、或者本机一个麦克风都没有 ⇒ 不给设备名，
    /// 于是采集那一路**根本不会去开音频**）。第三件事只能由采集侧判定，
    /// 因为「接得上」只有真开一次才知道，见 <c>FfmpegCameraCapture.ConfirmStartedAsync</c>。
    /// </para>
    /// <para>
    /// ⚠️ 枚举不到麦克风时报一条**用户可见**的话（I3）：那种情况下录像照录，
    /// 只是没有声音 —— 而「事后才发现整批货都没声音」正是静默失败的典型。
    /// </para>
    /// </remarks>
    private static async Task<string?> ResolveMicrophoneAsync(
        DesktopServices services, AppSettings settings, List<string> warnings,
        IAppLogger logger, CancellationToken cancellationToken)
    {
        if (!settings.RecordAudio)
        {
            return null;
        }

        if (services.FfmpegPath is null)
        {
            return null;
        }

        var devices = await DshowDevices.ListAudioAsync(services.FfmpegPath, logger, cancellationToken);

        if (devices.Count == 0)
        {
            warnings.Add("没有找到麦克风，录像不会有声音（设置里的「录制声音」是开着的）。");
            return null;
        }

        // 与摄像头同一条口径：之前用过的优先，找不到就退回第一个。
        if (settings.MicrophoneDevice is { Length: > 0 } remembered && devices.Contains(remembered))
        {
            return remembered;
        }

        return devices[0];
    }

    public async ValueTask DisposeAsync()
    {
        Hook.Dispose();
        await Coordinator.DisposeAsync();
        await Services.DisposeAsync();
        await _logger.DisposeAsync();
    }
}
