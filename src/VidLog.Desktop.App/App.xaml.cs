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

        // ⚠️ `--smoke`（批次 0 的闸）必须在**最前头**，早于日志器、主题、启动窗：
        // 它要跑在 CI 上、跑几百遍，任何一处副作用（写日志文件、探相机、占端口）
        // 都会让这个闸变得不敢跑。它只把每个窗口的 BAML 载入一遍就退。
        // 判据与「为什么只跑 InitializeComponent」见 `Platform/SmokeCheck` 的类注释。
        if (Platform.SmokeCheck.Requested(e.Args))
        {
            // ⚠️ 走 `Environment.Exit` 而不是 `Shutdown(code)`：`Shutdown` 会去
            // 逐个关掉这一步造出来的窗口，而它们是**揣着 null 协作对象**造的
            // —— `MainWindow.OnClosing` 那类收尾代码当场 NRE，进程以一个
            // 谁也看不懂的退出码（实测 127）结束，正好把这一趟真正想报的那个
            // 退出码盖掉。这一步是**用完就扔**的诊断，没有状态要保存。
            Environment.Exit(Platform.SmokeCheck.Run());
        }

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
            // ── T31：这一份是不是「换完用途自动重启」起来的 ──────────────
            //
            // ⚠️ 要**等旧进程退干净**才能往下走：相机在 dshow 上是独占的、
            // 回放端口（8720）也是，而旧进程是**最后**才把它们放掉的
            // （见 `AppHost.ReleaseAsync`）。抢先去拿 = 启动失败，
            // 而用户刚点的正是【切换用途】。
            //
            // ⚠️ 顺带把「还问不问用途」一起定在这上面：**不问**。
            // 用户刚刚才在用途对话框里选过，结果已经落在设置文件里了，
            // 再弹一次只是让他在启动窗前面多点一下。
            var previousPid = StartupCommand.WaitForPid(e.Args);

            if (previousPid is { } pid)
            {
                if (!await WaitForPreviousProcessAsync(pid, logger))
                {
                    // 旧进程没退（多半卡在放资源那一步）。**这一份不起** ——
                    // 相机与端口还在它手里，硬起只会得到个半坏的，
                    // 而用户手上那一份还好好地开着。
                    splash.Close();
                    Shutdown();
                    return;
                }
            }
            else
            {
                // ⚠️ 必须在 `AppHost.StartAsync` **之前**：用途决定装不装摄像头与麦克风、
                // 探不探录制规格，而那些全发生在装配期。装配完再问等于没问。
                await AskStationRoleAsync(logger);
            }

            _host = await AppHost.StartAsync(logger);

            _window = new MainWindow(_host);
            MainWindow = _window;

            // 关窗口只是收进托盘，退出走托盘菜单 —— 有在录的段时先问一句。
            _host.ConfirmExitWhileRecording = ConfirmExitWhileRecording;

            // 「关闭窗口时」那三档里的两档（直接退出 / 每次询问里选的那一下）
            // 要走**同一条**退出路：托盘菜单那一条（见 `AppHost.RequestExit`）。
            _host.RequestExit = () => _ = ExitAsync();

            // T31：换完用途由**应用自己**收掉再起来（落点在 `RestartAsync`），
            // 不再只弹一句让用户手动关掉重开。
            _host.RequestRestart = () => _ = RestartAsync(logger);

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

            // ── 首次运行的 7 天试用说明（需求方 2026-10-17）──────────────────
            // ⚠️ 排在录制合规告知**之前**：这一张是「点不同意就退出程序」的那一步，
            //    先问它；反过来的话用户会先看到一个关不掉流程的告知框，
            //    再被问「同意还是退出」。
            if (!await ShowLicenseNoticeOnceAsync(logger))
            {
                await ExitAsync();
                return;
            }

            // ── 首次运行的录制合规告知（需求方 2026-10-17）──────────────────
            // ⚠️ 挂在这里而不是「按下【开始录制】那一刻」：录制有两条入口
            //    （主窗按钮、取景识码自动开录），两处各写一份迟早只补了一处 ——
            //    而这条说的是「录别人之前要依法告知」，跟从哪个门进来无关。
            await ShowRecordingNoticeOnceAsync(logger);

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
    /// 首次运行时把「7 天试用 / 机器码 / 联系方式」当面说一次（需求方 2026-10-17）。
    /// </summary>
    /// <returns>
    /// <see langword="true"/> = 用户点了【我已了解，同意】（或本来就同意过）；
    /// <see langword="false"/> = 点了【不同意，退出程序】，调用方应当收掉程序。
    /// </returns>
    /// <remarks>
    /// ⚠️ 这是**告知**，不是许可校验：点同意**不会**开始计时、也不解锁任何功能 ——
    /// 试用窗口从「粘上试用码并激活」那一刻起算（有码试用，需求方 2026-10-17 定）。
    /// 那个标志只用于「别再弹」，不参与许可判断。
    /// </remarks>
    private async Task<bool> ShowLicenseNoticeOnceAsync(FileLogger logger)
    {
        if (_host is null || _window is null || _host.Settings.LicenseNoticeAccepted)
        {
            return true;
        }

        logger.Log(LogLevel.Info, "许可", "首次运行：展示 7 天试用说明");

        var window = new LicenseWindow(_host, LicenseWindowMode.FirstRun) { Owner = _window };

        if (window.ShowDialog() != true)
        {
            logger.Log(LogLevel.Info, "许可", "用户不同意 7 天试用说明，程序退出");

            return false;
        }

        await _host.SaveSettingsAsync(_host.Settings with { LicenseNoticeAccepted = true });

        return true;
    }

    /// <summary>
    /// 首次运行时把「录制前请依法告知被录制人员」当面说一次（需求方 2026-10-17）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这是**告知**，不是许可校验，也不挡任何功能 —— 用户点一下【确定】就过去了。
    /// 那个标志只用于「别再弹」，它不参与许可判断，也不许被别处当成「用户同意了某件事」
    /// 的证据（见 <c>AppSettings.RecordingNoticeAccepted</c>）。
    /// </remarks>
    private async Task ShowRecordingNoticeOnceAsync(FileLogger logger)
    {
        if (_host is null || _window is null || _host.Settings.RecordingNoticeAccepted)
        {
            return;
        }

        logger.Log(LogLevel.Info, "启动", "首次运行：展示录制合规告知");

        MessageBox.Show(
            _window,
            "本软件会录制画面与声音，录像中可能包含他人的人脸与声音。\n\n"
            + "请在录制区域显著位置张贴「视频监控区域」提示标识，向被录制人员告知"
            + "处理目的、方式、范围与保存期限，并依法取得同意。\n\n"
            + "这是您作为录像管理者的法定义务，本软件不能替您履行。"
            + "完整说明见《服务条款》第五条与《隐私政策》第六条（安装目录 licenses 文件夹）。",
            "录制前请依法告知被录制人员",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        await _host.SaveSettingsAsync(_host.Settings with { RecordingNoticeAccepted = true });
        logger.Log(LogLevel.Info, "启动", "录制合规告知已确认，以后不再提示");
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

            case CoordinatorNoticeKind.FinalizeFailed:
                _host?.Tray?.Notify("VidLog", notice.Message);
                break;

            // ── 错码保护（规格 §3.3.2）──────────────────────────────────
            //
            // ⚠️ 需求方 2026-10-10：**动态栏那句与念出来那句从此不一样长**。
            // 动态栏要带单号（事后翻得到是哪一码错了），而**念的时候不带单号**
            // —— 原来连播报都没有，错码只有个托盘气泡。
            case CoordinatorNoticeKind.WrongWaybill:
                _host?.Tray?.Notify("VidLog", notice.Message);
                Platform.Speech.Speak("单号错误");
                break;

            // ── 重复单号检测（规格 §3.2.5）──────────────────────────────
            //
            // ⚠️ 与闲置提醒同一族（气泡 + 语音两条）：这条说的是「这个单号
            // 最近录过」—— 操作员这时手上正拿着包裹，**很可能没看屏幕**，
            // 光弹气泡等于没说。
            //
            // ⚠️ 它**不挡开录**（规格：那三项「全部异步执行，绝不阻塞开录」）
            // —— 这一句只是事后提醒，录制已经开始了。
            //
            // ⚠️ 需求方 2026-10-10：**念的只有四个字**（整句是
            // 「<单号> 在最近 7 天里录过 10 次（最近一次 …）。核对一下…」，
            // 操作员得站着听完）。
            // ⚠️ 动态栏那一行**照旧是整句**（他同日裁定「界面保留重复次数」）——
            // 改的只是播报，不是上屏。改这里只在动**念**的那一路。
            case CoordinatorNoticeKind.DuplicateWaybill:
                _host?.Tray?.Notify("VidLog", notice.Message);
                Platform.Speech.Speak("单号重复");
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

        bool shutdown;

        try
        {
            // ShutdownAsync 会先问一句（有在录的段时），用户说不退就什么都不做。
            shutdown = await _host.ShutdownAsync();
        }
        catch (Exception ex)
        {
            // ⚠️ 必须在这里收住（2026-10-13，与 `RestartAsync` 同一处坑）：
            // 这个方法一进来就是个 fire-and-forget 的 task，抛出去没人接，
            // 而 `_exiting` 已经置上了 —— 那之后连托盘那颗【退出】都会被它挡掉，
            // 用户只能去任务管理器杀进程。放回去，至少还能再试一次。
            _host.Log(LogLevel.Error, "退出", $"退出流程没走到头：{ex.Message}");
            _exiting = false;
            return;
        }

        if (!shutdown)
        {
            _exiting = false;
            return;
        }

        Shutdown();
    }

    /// <summary>
    /// T31：换完用途之后，把这一份收掉、起一个新进程接着跑。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>三步的顺序是「先收尾、再起新的、最后放自己」</b>，一步都不能挪：
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>先收尾</b>：可能要几分钟（封装、算哈希、往归档目标搬一份），
    /// 而新进程等不了那么久 —— 它要是先起来了，就得在这儿干等，
    /// 等到超时退出就会**一个应用都不剩**。
    /// </description></item>
    /// <item><description>
    /// <b>再起新的</b>：它带着 <c>--wait-for-pid &lt;本进程&gt;</c>，
    /// 会自己等到我们退干净为止，所以先起它不会抢相机。
    /// 起不来时**什么都还没放掉**，这一份还能照常跑（只是没在录了）。
    /// </description></item>
    /// <item><description>
    /// <b>最后放自己</b>：托盘、回放服务（8720）、日志器全在这里放掉，
    /// 紧接着退出 —— 新进程正是在等这一刻。
    /// </description></item>
    /// </list>
    /// <para>
    /// ⚠️ 起不来**不退出**：按老用途继续跑着，并把话说给用户听。
    /// 静默关掉自己是最坏的一种 —— 用户手上的窗口没了，而盘上已经存着新用途，
    /// 下次开机才会按它起来，中间这段时间他什么都不知道。
    /// </para>
    /// </remarks>
    private async Task RestartAsync(FileLogger logger)
    {
        if (_exiting || _host is null)
        {
            return;
        }

        _exiting = true;

        var role = StationRoles.Describe(_host.Settings.StationRole).Title;

        // ⚠️ 「为什么要重启」不在这里记：那一条在 `AppHost.RestartForRole`
        // 的「用途已切换」里（带 `生效时机`）。这条记的是**动作本身**。
        logger.Log(LogLevel.Info, "用途", $"正在按「{role}」重新启动进程");

        // ⚠️ 收尾期间要说一句话：那几分钟里窗**看着像没反应** ——
        // 用户在用途对话框上刚按过确认，这正是最容易以为「点了没用」的时候。
        // 说在这里（不是收尾之后）才盖得住那一段。
        _host.Tray?.Notify("VidLog", $"正在按「{role}」重新启动…");

        // ① 收尾（有在录的先收掉）。
        try
        {
            await _host.FinalizeForRestartAsync();

            // ② 起新进程。失败时下面什么都没放掉，这一份还能跑。
            if (!StartReplacementProcess(logger))
            {
                _exiting = false;

                // I3：不许静默 —— 盘上已经存着新用途了，不说明白的话用户会以为
                // 换好了，而它照旧按老用途录（下一次打开才会变）。
                _host.Tray?.Notify(
                    "VidLog", "换用途之后没能自动重启，请手动关掉再打开。");
                return;
            }

            // ③ 放掉独占资源（相机、回放端口、托盘），随后退出。
            await _host.ReleaseAsync();
        }
        catch (Exception ex)
        {
            // ⚠️ 必须在这里收住：这是个 fire-and-forget 的 task，抛出去没人接，
            // 而 `_exiting` 已经置上了 —— 那之后连托盘那颗【退出】都会被它挡掉，
            // 用户只能去任务管理器杀进程。收住并把它放回去，至少还能用。
            logger.Log(LogLevel.Error, "用途", $"重启没走到头：{ex.Message}");
            _exiting = false;
            return;
        }

        Shutdown();
    }

    /// <summary>起一个新进程（T31）；起不来返回 false，且**什么都还没放掉**。</summary>
    private static bool StartReplacementProcess(FileLogger logger)
    {
        var exe = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(exe))
        {
            // ⚠️ 单文件发布等形态下这个可以是 null。不知道自己在哪就**不起** ——
            // 猜一个路径起来的会是别的东西。
            logger.Log(LogLevel.Error, "用途", "拿不到自己的 exe 路径，没法自动重启");
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = StartupCommand.RestartArguments(Environment.ProcessId),

                // ⚠️ 工作目录必须钉在自己的目录上：托盘图标那些相对路径按它算。
                WorkingDirectory = AppContext.BaseDirectory,

                // ⚠️ `false`：`true` 的话参数会被 Shell 按它自己的规矩再切一遍，
                // 而 `--wait-for-pid 1234` 是靠空格分的（与 `StartupCommand`
                // 那头是同一个坑）。
                UseShellExecute = false,
            });

            logger.Log(LogLevel.Info, "用途", "已起新进程（它先等这一份退出）");
            return true;
        }
        catch (Exception ex)
        {
            logger.Log(LogLevel.Error, "用途", $"起新进程失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// T31：等上一个进程退干净。超时返回 false（由调用方决定**不起**这一份）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 正常情况下**它几乎立刻就退了**：调用方是**先收完尾再起我们**的
    /// （见 <see cref="RestartAsync"/>），所以轮到我们时它只剩「放掉资源」那一下。
    /// 60 秒是给那一下留的富余，不是给收尾留的 —— 判据不是「它退得多快」，
    /// 是「别抢它手里的相机」。
    /// </para>
    /// <para>
    /// ⚠️ 等待放在线程池上：`WaitForExit` 是同步阻塞的，直接 await 会把
    /// UI 线程占住 —— 那时启动窗**画不出来**，用户看到的正是他最讨厌的
    /// 「点了没反应」。
    /// </para>
    /// </remarks>
    private static async Task<bool> WaitForPreviousProcessAsync(int pid, FileLogger logger)
    {
        System.Diagnostics.Process previous;

        try
        {
            previous = System.Diagnostics.Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // 已经退了（pid 早就不作数）—— 这正是我们想要的。
            logger.Log(LogLevel.Info, "启动", $"自动重启：上一个进程（{pid}）已经退出");
            return true;
        }

        var exited = await Task.Run(() => previous.WaitForExit(60_000));

        if (exited)
        {
            logger.Log(LogLevel.Info, "启动", $"自动重启：上一个进程（{pid}）已退出");
            return true;
        }

        // I3：不许静默 —— 这一份马上要关掉自己，日志是唯一能解释「刚才那次重启
        // 为什么没成」的地方。
        logger.Log(
            LogLevel.Error, "启动",
            $"自动重启：上一个进程（{pid}）60 秒没退，这一次不启动了。"
            + "它多半还在收尾一场录像 —— 收完那一份就能照常用，"
            + "要换的用途下次打开生效。");
        return false;
    }
}
