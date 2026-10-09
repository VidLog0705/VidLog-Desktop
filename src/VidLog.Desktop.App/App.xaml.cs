using System.IO;
using System.Windows;

using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Recording;

// 本工程同时开了 UseWPF 与 UseWindowsForms，ImplicitUsings 会把两边的同名类型
// 都带进来。这里钉死成 WPF 的那套 —— 对话框只该有一个来源。
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
// 同上：图标那个类型只取 WPF 那一套（隐式 using 里没有 `System.Windows.Media`，
// 但全局有 `System.Drawing`，一旦 `using System.Windows.Media;` 就会撞 `Color`/`Brush`）。
using ImageSource = System.Windows.Media.ImageSource;

namespace VidLog.Desktop.App;

/// <summary>
/// 应用引导。
/// </summary>
/// <remarks>
/// 启动顺序刻意是「先装配、再开窗口」：装配失败时还没有窗口可以显示错误，
/// 所以失败要用 MessageBox 说出来，而不是静默退出。
/// <para>
/// ⚠️ 但装配期**实测 4–6 秒**（2026-10-02 本机实测，那还是没有摄像头的最佳情况），
/// 那几秒里不能什么都不显示 —— 所以装配之前先弹一个
/// <see cref="StartupWindow"/>（缺陷 2 前半）。
/// </para>
/// </remarks>
public partial class App : System.Windows.Application
{
    private AppHost? _host;
    private MainWindow? _window;
    private bool _exiting;

    private static ImageSource? _appIcon;

    /// <summary>
    /// 给**每一个**窗口挂上标题栏图标。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>这条路是探针实测选出来的，不是随手写的</b>（2026-10-09）。
    /// 先写的那一版是「在 `Theme.xaml` 里放一条隐式样式
    /// <c>&lt;Style TargetType="Window"&gt;</c>」—— 一个窗口都不用改，
    /// 看着最省。**它不生效**：WPF 不给 <see cref="Window"/> 应用隐式样式，
    /// 探针里连 `Window.Style` 都是 `null`，`Icon` / `Background` / `Title`
    /// 三个 Setter 一个都没落地，而且**一声不吭**。
    /// </para>
    /// <para>
    /// 另一条路是在 12 个 `&lt;Window&gt;` 上各抄一行 `Icon="/VidLog.ico"`：
    /// 能生效，但是 12 处将来会漂的重复；而且 WPF 从多尺寸 .ico 里
    /// **只取第一帧**（本仓那份的第一帧是 16），Alt+Tab 那颗 32 的是放大出来的。
    /// </para>
    /// <para>
    /// 所以走**类处理器**：一条盖住所有窗口（含以后新加的），顺带自己挑帧。
    /// 挂在 <c>Loaded</c> 上而不是构造函数里，是因为那一刻窗口才有 HWND，
    /// 换图标才画得上去。
    /// </para>
    /// </remarks>
    static App()
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                var window = (Window)sender;

                // 哪个窗口自己指定了图标就听它的 —— 这条只在没人指定时才补。
                if (window.Icon is null)
                {
                    window.Icon = _appIcon ??= LoadAppIcon();
                }
            }));
    }

    /// <summary>
    /// 从程序集里的 <c>VidLog.ico</c> 取**最接近 32×32** 的那一帧。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不能只写 <c>BitmapFrame.Create(uri)</c>（或 XAML 里的 <c>Icon="/VidLog.ico"</c>）
    /// —— 那两个只取第一帧。挑 32 是因为它两头都够：标题栏往 16 缩是缩下去的，
    /// 不是放上去的。
    /// <para>
    /// 路径走**程序集内的资源**（csproj 里那条 <c>&lt;Resource Include="VidLog.ico"&gt;</c>）。
    /// <c>&lt;ApplicationIcon&gt;</c> 那份只进 exe 的 Win32 图标资源，WPF 这边看不见，
    /// 两份都要有 —— 少哪一份都是「有的地方有图标、有的地方没有」，且都不报错。
    /// </para>
    /// </remarks>
    private static ImageSource LoadAppIcon()
    {
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
            new Uri("pack://application:,,,/VidLog.ico"),
            System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

        var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 32)).First();

        if (frame.CanFreeze)
        {
            frame.Freeze();
        }

        return frame;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ⚠️ 日志器与崩溃钩子必须在**任何业务代码之前**：
        // 这个应用是 WinExe（没有控制台），此前未捕获异常的表现是
        // 「窗口直接消失，磁盘上一个字都没有」。三个钩子全无，也没有任何测试会红。
        var logger = AppHost.CreateBootstrapLogger();
        Platform.CrashGuard.Install(logger);

        // ⚠️ 主题必须在**任何窗口之前**定下来（改造清单第 5 批）：
        // 界面那几百处取色已改成 `{DynamicResource}`，它们**允许**之后再换；
        // 但先定下来能少闪一下亮色（启动窗最显眼）。放这儿连启动窗
        // （下面那行）和用途选择窗都是对的颜色。
        // 机制与「为什么不是改笔刷颜色」见 `Platform/AppTheme.cs` 的类注释。
        Platform.AppTheme.Start(logger);

        // ⚠️ 计时是为了**能回答「启动为什么慢」**：这一条进日志之后，
        // 用户说「打开要等半天」就有数可查，而不是只能靠猜（`AGENTS.md` §6）。
        var startupClock = System.Diagnostics.Stopwatch.StartNew();

        // ⚠️ 装配期先显示这个（缺陷 2 前半，2026-10-02）。
        // 在这之前：双击图标之后**屏幕上什么都没有**，直到 `_window.Show()` ——
        // 本机实测 3.7–6.1 秒，而且那还是枚举不到任何摄像头的最好情况。
        // ⚠️ 它**不是**为了好看：那几秒里用户分不出「在启动」和「点了没反应」，
        // 于是会再点一次。`Show()` 之后窗口就画出来了（消息泵随 OnStartup 返回而转起来）。
        var splash = new StartupWindow();
        splash.Show();

        try
        {
            // ⚠️ 必须在 `AppHost.StartAsync` **之前**：用途决定装不装摄像头与麦克风、
            // 探不探录制规格，而那些全发生在装配期。装配完再问等于没问。
            await AskStationRoleAsync(logger);

            _host = await AppHost.StartAsync(logger);

            _window = new MainWindow(_host);
            MainWindow = _window;

            // 关窗口只是收进托盘，退出走托盘菜单 —— 有在录的段时先问一句。
            _host.ConfirmExitWhileRecording = ConfirmExitWhileRecording;

            // 「关闭窗口时」那三档里的两档（直接退出 / 每次询问里选的那一下）
            // 要走**同一条**退出路：托盘菜单那一条（见 `AppHost.RequestExit`）。
            _host.RequestExit = () => _ = ExitAsync();

            _host.AttachTray(
                showWindow: ShowWindow,
                exitApplication: () => _ = ExitAsync());

            _host.Notice += OnNotice;

            _window.Show();

            // ⚠️ 顺序是「主窗先出来，再收掉启动窗」—— 反过来的话那几毫秒里
            // 一个窗口都没有（`ShutdownMode=OnExplicitShutdown`，所以不会退出，
            // 但屏幕上会闪一下空白）。
            splash.Close();

            logger.Log(
                LogLevel.Info, "启动",
                $"装配完成，耗时 {startupClock.Elapsed.TotalSeconds:0.0} 秒（含设备探测）",
                new Dictionary<string, object?>
                {
                    // ⚠️ 只记**时长**，不记起止时刻：日志行自带 `ts`，再写一个
                    // “这个几点几分起算”只会多一个对不上的数（写这句时先写错过一次：
                    // 那个时刻其实是**结束**时刻）。
                    ["秒"] = Math.Round(startupClock.Elapsed.TotalSeconds, 1),
                    // ⚠️ 用途一起记：不录像的那两档**不解析**摄像头与麦克风，
                    // 快得多 —— 只记秒数的话，「这一台怎么特别慢」会查不出是用途不同。
                    ["用途"] = StationRoles.Describe(_host.Settings.StationRole).Title,
                });

            // 开机自启动：**每次启动都把注册表重写一遍**。
            // ⚠️ 理由是路径会漂移 —— 程序被搬到别的目录之后，注册表里那条指向的是
            // 已经不存在的 exe，而重写是唯一能让它追上来的办法（几毫秒的事）。
            // ⚠️ 只在「用户开过这一项」时才写；关着的时候**一个字节都不碰注册表**。
            if (_host.Settings.RunAtStartup)
            {
                Platform.StartupRegistration.Apply(wanted: true, logger);
            }

            // 检查更新（设计图 `_49`）：**后台跑，绝不挡任何东西**。
            // ⚠️ 不 await：它是可选功能，让一次网络请求把主窗口的显示拖住
            // 是本末倒置；而它自己也不会抛（`UpdateChecker` 把异常收成结果）。
            if (_host.Settings.CheckForUpdates)
            {
                _ = CheckForUpdatesAsync(logger);
            }
        }
        catch (Exception ex)
        {
            // 启动失败必须让用户看见，不能静默退出（I3 的同一条精神）。
            // **同时要留痕**：只弹一个框的话，用户说「它打不开」时没有任何可查的，
            // 而启动失败的原因（端口占了、配置坏了、FFmpeg 找不到）恰恰最需要那份现场。
            Platform.CrashGuard.LogStartupFailure(logger, ex);

            MessageBox.Show(
                $"启动失败：{ex.Message}\n\n（原因已记进日志：{logger.Path}）",
                "VidLog", MessageBoxButton.OK, MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    /// <summary>
    /// 问一次有没有新版本，有就在托盘上说一句（设计图 `_49`「自动检查更新」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>只做检查 + 提示</b>（需求方 2026-09-30 裁决）：这里**不下载、
    /// 不替换自己**，连「要不要现在更新」都不问 —— 结果只写进
    /// <see cref="AppHost.UpdateStatus"/>，由「关于」那一页显示，
    /// 有新版时再补一个托盘气泡。
    /// </para>
    /// <para>
    /// ⚠️ <b>它不许挡住任何东西，也不许把异常抛到外面</b>：
    /// 这个方法是 fire-and-forget 起的，抛出去就是一个未观测的
    /// <c>Task</c> 异常（<c>CrashGuard</c> 会记，但那是「崩溃」级别的噪音）。
    /// <c>UpdateChecker</c> 自己已经把所有异常收成结果了。
    /// </para>
    /// </remarks>
    private async Task CheckForUpdatesAsync(FileLogger logger)
    {
        var checker = new Core.Update.UpdateChecker(Platform.ReleaseFeed.FetchLatestTagAsync, logger);
        var result = await checker.CheckAsync();

        if (_host is null)
        {
            return;
        }

        _host.UpdateStatus = result;

        if (!result.SuggestUpdate(_host.CurrentVersion))
        {
            return;
        }

        // ⚠️ 这是**提示**，不是错误：所以用气泡 + 日志，不弹模态框。
        // 弹框会挡在用户面前，而更新这件事不紧急 —— 更不该在他正要开始干活时挡。
        _host.Tray?.Notify(
            "VidLog 有新版本",
            $"当前 {_host.CurrentVersion}，最新 {result.LatestTag}。到发布页下载。");

        logger.Log(
            LogLevel.Info, "更新检查",
            $"发现新版本 {result.LatestTag}（当前 {_host.CurrentVersion}）。");
    }

    /// <summary>
    /// 打开软件先问一句「这台电脑拿来干什么」（设计图 `_11`–`_15`）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>每次打开都问</b>（2026-10-02 需求方报「打开软件后的功能选择界面没有了，
    /// 严重错误」）。原来的判据是「设置文件在不在」，本意是「老用户别再问一遍」，
    /// 实际后果是：这台机器上只要落过一个 <c>settings.json</c>（改过任何一个设置都会落），
    /// 这一屏就<b>永远</b>不再出现 —— 需求方装完 0.2.0 打开看到的正是这个现象。
    /// </para>
    /// <para>
    /// ⚠️ <b>它不是设置窗里那个「切换用途」</b>：这里发生在 <c>AppHost.StartAsync</c>
    /// 之前（用途决定装不装摄像头与麦克风），所以这里选的用途<b>这一次启动就生效</b>，
    /// 不需要重启 —— 那一处才必须重启。
    /// </para>
    /// <para>
    /// 取消**不挡启动**：按上一次存的用途继续；首次运行时那就是默认的
    /// 「电脑录像并保存在本机」。
    /// </para>
    /// </remarks>
    private static async Task AskStationRoleAsync(FileLogger logger)
    {
        var layout = DataLayout.Default();
        var store = new SettingsStore(layout.SettingsPath);
        var firstRun = !File.Exists(layout.SettingsPath);
        var loaded = await store.LoadAsync();

        // ⚠️ 首次运行**一张卡都不预选**（图上 `_11` 那句「请完成上面两个选择」）；
        // 之后把当前用途预选上，于是「确认用途」一进来就是可点的。
        var dialog = new RoleWindow(firstRun ? null : loaded.Settings.StationRole);

        if (dialog.ShowDialog() != true)
        {
            logger.Log(
                LogLevel.Info, "用途",
                $"打开时没有改用途，按{(firstRun ? "默认的" : "上次的")}"
                + $"「{StationRoles.Describe(loaded.Settings.StationRole).Title}」启动。");
            return;
        }

        if (!firstRun && dialog.SelectedRole == loaded.Settings.StationRole)
        {
            return;
        }

        if (loaded.Warnings.Count > 0)
        {
            // ⚠️ 设置文件读不出来时**不写盘**：那条警告明说「原文件保留在 …」，
            // 用户要拿它去查。这一次启动仍按读出来的那份（默认值）走。
            logger.Log(
                LogLevel.Warn, "用途",
                "设置文件有问题（已在上一条里说明），这次选的用途**没有**写盘 —— "
                + "先把设置文件修好，否则会把出问题的原文件覆盖掉。");
            return;
        }

        // ⚠️ 存的是**读出来的那一份改一个字段**，不是 `AppSettings.Default with { … }`：
        // 后者会把用户其余所有设置一次抹掉（只修一个用途却丢掉全部配置）。
        await store.SaveAsync(loaded.Settings with { StationRole = dialog.SelectedRole });

        var choice = StationRoleChoice.Of(dialog.SelectedRole);

        logger.Log(
            LogLevel.Info, "用途",
            (firstRun ? "首次运行选定了用途" : "打开时改了用途")
            + $"：{StationRoles.Describe(dialog.SelectedRole).Title}",
            new Dictionary<string, object?>
            {
                ["是否录像"] = choice.Records,
                ["是否长期保存"] = choice.Keeps,
                ["生效时机"] = "本次启动",
            });
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.RestartTicker();
    }

    private Task<bool> ConfirmExitWhileRecording()
    {
        var waybill = _host?.Coordinator.CurrentWaybill;
        if (waybill is null)
        {
            return Task.FromResult(true);
        }

        ShowWindow();

        var answer = MessageBox.Show(
            $"「{waybill.Value}」还在录。要结束它并退出吗？",
            "VidLog", MessageBoxButton.YesNo, MessageBoxImage.Question);

        return Task.FromResult(answer == MessageBoxResult.Yes);
    }

    /// <summary>
    /// 把协调器的通知送出去 —— 窗口收起来之后，那是用户唯一看得见的地方。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只弹「需要用户知道」的那几类。每一件包裹都弹一次气泡是噪声，
    /// 用户会开始无视它 —— 那比不弹更糟。
    /// </para>
    /// <para>
    /// ⚠️ <b>闲置提醒（规格 §3.3.3）走的是气泡 + 语音两条</b>，与别的不一样：
    /// 规格原话要的是「**语音播报**提醒」，而这个窗口多半收在托盘里 ——
    /// 只弹气泡的话用户根本看不见，那这条提醒就等于没做。
    /// </para>
    /// </remarks>
    private void OnNotice(CoordinatorNotice notice)
    {
        switch (notice.Kind)
        {
            case CoordinatorNoticeKind.Idle:
                _host?.Tray?.Notify("VidLog", notice.Message);
                // 尽力而为：没装语音、语音被禁用都不该影响录制（I4 的同一条精神）。
                Platform.Speech.Speak(notice.Message);
                break;

            // ── 时长兜底那次**询问**（规格 §3.3.4）────────────────────────
            //
            // 规格原话：「语音提示 + 屏幕按钮」。所以这里与闲置提醒同一个道理：
            // **语音必须出**（窗口多半收在托盘里，只有屏幕上的按钮等于没问），
            // 而按钮在 `MainWindow` 那边（它才是管这个窗口的）。
            //
            // ⚠️ 这条**不是**提醒，是**问**——用户答【停止】或 1 分钟不理，录制就会停。
            // 所以它比闲置提醒更要紧，不能只弹个气泡了事。
            case CoordinatorNoticeKind.DurationPrompt:
                _host?.Tray?.Notify("VidLog", notice.Message);
                Platform.Speech.Speak(notice.Message);
                break;

            case CoordinatorNoticeKind.FinalizeFailed or CoordinatorNoticeKind.WrongWaybill:
                _host?.Tray?.Notify("VidLog", notice.Message);
                break;

            // ── 重复单号检测（规格 §3.2.5）──────────────────────────────
            //
            // ⚠️ 与闲置提醒同一族（气泡 + 语音两条）：这条说的是「这个单号
            // 最近录过」—— 操作员这时手上正拿着包裹，**很可能没看屏幕**，
            // 光弹气泡等于没说。
            //
            // ⚠️ 它**不挡开录**（规格：那三项「全部异步执行，绝不阻塞开录」）
            // —— 这一句只是事后提醒，录制已经开始了。
            case CoordinatorNoticeKind.DuplicateWaybill:
                _host?.Tray?.Notify("VidLog", notice.Message);
                Platform.Speech.Speak(notice.Message);
                break;

            default:
                break;
        }
    }

    private async Task ExitAsync()
    {
        if (_exiting || _host is null)
        {
            return;
        }

        _exiting = true;

        // ShutdownAsync 会先问一句（有在录的段时），用户说不退就什么都不做。
        if (!await _host.ShutdownAsync())
        {
            _exiting = false;
            return;
        }

        Shutdown();
    }
}
