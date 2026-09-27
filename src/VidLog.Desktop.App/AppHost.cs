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
    /// **实际会用**的录制规格（启动时那次真开相机的探测结果）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它与 <see cref="Settings"/> 里用户选的那两档**可能不一样** ——
    /// 规格 §3.1.7 要求「**回落必须可见**……**不得静默回落**」，
    /// 所以界面必须显示这一个，而不是用户选的那个。
    /// </remarks>
    public RecordingSpec EffectiveSpec { get; private set; } = RecordingSpec.Default;

    /// <summary>回落的原因；没回落过时为 <see langword="null"/>。</summary>
    public string? SpecFallbackReason { get; private set; }

    /// <summary>开录时用的编码器名（探测挑出来的那个）。</summary>
    public string EncoderName { get; private set; } = "libx264";

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
        var encoder = EncoderSelection.Select(await services.EncoderProbe.ProbeAsync(cancellationToken));
        if (encoder is null)
        {
            warnings.Add("本机没有任何可用的 H.264 编码器，无法录制。");
        }

        var device = await ResolveCameraAsync(services, settings, warnings, cancellationToken);

        // ── 录制规格：**真实**的可用性检查（规格 §3.1.7）──────────────────
        //
        // ⚠️ 为什么要真开一次相机：规格原话「组合是稀疏的……设备能真跑通的**远少于**这个数
        // （4K + H.265 在多数手机上就不行）。所以**录制前要做一次真实的可用性检查**，
        // 而不是假定『列出来了就能用』」。合成源什么尺寸都收，只有真相机能说话。
        //
        // ⚠️ 回落的结论**必须说出来**：规格「**不得静默回落**」。
        var wantedSpec = new RecordingSpec(settings.Codec, settings.Resolution);
        var selection = services.FfmpegPath is null
            ? new SpecSelection(wantedSpec, false, null)
            : await SpecSelectionPolicy.SelectAsync(
                wantedSpec, device, new FfmpegSpecProbe(services.FfmpegPath, new SystemProcessRunner(logger)),
                cancellationToken);

        if (selection.ChangedFromRequested)
        {
            warnings.Add(
                $"录制规格回落到了 {selection.Spec.Label}（你选的是 {wantedSpec.Label}）。"
                + $"原因：{selection.Reason}");
        }

        logger.Log(LogLevel.Info, "启动", $"录制规格 {selection.Spec.Label}", new Dictionary<string, object?>
        {
            ["用户选的"] = wantedSpec.Label,
            ["回落"] = selection.ChangedFromRequested,
        });

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
                device,
                Environment.MachineName,
                encoder ?? "libx264",
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
            SessionOptions = RecordingSessionOptions
                .From(settings.SegmentMinutes, settings.DurationFallback)
                .With(selection.Spec),
        };

        var bridge = new KeyboardScanBridge(settings.Scanner);
        var hook = new WindowsKeyboardHook();

        var host = new AppHost(services, startup, settings, logger, hook, bridge, coordinator)
        {
            Warnings = warnings,
            EffectiveSpec = selection.Spec,
            SpecFallbackReason = selection.Reason,
            EncoderName = encoder ?? "libx264",
        };

        // 清理也留痕（AGENTS.md §6「关键操作必须留痕」）——
        // 一条日志都没有的清理，出事时说不清它到底跑没跑。
        if (purged > 0)
        {
            logger.Log(LogLevel.Info, "启动", $"清掉了 {purged} 个过期日志文件");
        }

        // 摄像头识码（规格 §3.2.1 的第二种入口）。装在协调器上，
        // 【开始工作】时会自动开始取景，扫到单号自动开录。
        if (services.FfmpegPath is { } ffmpegPath && device.Length > 0)
        {
            var scanner = new CameraFrameScanner(
                ffmpegPath, device, new ZXingFrameScanner(), logger);

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
                ["摄像头"] = device,
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

        await new SettingsStore(Services.Layout.SettingsPath).SaveAsync(next);
        Settings = next;

        // 档位立即生效（下次开段时取值）—— 界面上写着「下次录段生效」，
        // 不跟着更新的话那句话就是假的。
        Coordinator.SessionOptions = RecordingSessionOptions.From(
            next.SegmentMinutes, next.DurationFallback);
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

    private static async Task<string> ResolveCameraAsync(
        DesktopServices services, AppSettings settings, List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (services.FfmpegPath is null)
        {
            return settings.CameraDevice ?? string.Empty;
        }

        var devices = await CameraDevices.ListAsync(services.FfmpegPath, cancellationToken);

        if (devices.Count == 0)
        {
            warnings.Add("没有找到摄像头，无法录制。");
            return settings.CameraDevice ?? string.Empty;
        }

        // 之前用过的设备优先 —— 换了 USB 口之后设备名可能变，所以找不到就退回第一个。
        if (settings.CameraDevice is { Length: > 0 } remembered && devices.Contains(remembered))
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
