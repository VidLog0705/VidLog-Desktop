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

namespace VidLog.Desktop.App;

/// <summary>
/// 应用引导。
/// </summary>
/// <remarks>
/// 启动顺序刻意是「先装配、再开窗口」：装配失败时还没有窗口可以显示错误，
/// 所以失败要用 MessageBox 说出来，而不是静默退出。
/// </remarks>
public partial class App : System.Windows.Application
{
    private AppHost? _host;
    private MainWindow? _window;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ⚠️ 日志器与崩溃钩子必须在**任何业务代码之前**：
        // 这个应用是 WinExe（没有控制台），此前未捕获异常的表现是
        // 「窗口直接消失，磁盘上一个字都没有」。三个钩子全无，也没有任何测试会红。
        var logger = AppHost.CreateBootstrapLogger();
        Platform.CrashGuard.Install(logger);

        try
        {
            // ⚠️ 必须在 `AppHost.StartAsync` **之前**：用途决定装不装摄像头与麦克风、
            // 探不探录制规格，而那些全发生在装配期。装配完再问等于没问。
            await AskStationRoleOnFirstRunAsync(logger);

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
    /// 首次运行时先问一句「这台电脑拿来干什么」（设计图 `_11`–`_15`）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 判据是<b>设置文件在不在</b>，不是「用途是不是默认值」——
    /// 后者会把每一个老用户都拦下来问一遍，而他们那台机器早就配好了。
    /// 文件在 = 这台机器以前跑过 VidLog，跳过。
    /// </para>
    /// <para>
    /// 取消**不挡启动**：按默认的「电脑录像并保存在本机」走，与没有这个概念时一样。
    /// </para>
    /// </remarks>
    private static async Task AskStationRoleOnFirstRunAsync(FileLogger logger)
    {
        var layout = DataLayout.Default();

        if (File.Exists(layout.SettingsPath))
        {
            return;
        }

        var dialog = new RoleWindow();

        if (dialog.ShowDialog() != true)
        {
            logger.Log(
                LogLevel.Info, "用途",
                "首次运行没有选用途，按默认的「电脑录像并保存在本机」启动。");
            return;
        }

        await new SettingsStore(layout.SettingsPath)
            .SaveAsync(AppSettings.Default with { StationRole = dialog.SelectedRole });

        var choice = StationRoleChoice.Of(dialog.SelectedRole);

        logger.Log(
            LogLevel.Info, "用途",
            $"首次运行选定了用途：{StationRoles.Describe(dialog.SelectedRole).Title}",
            new Dictionary<string, object?>
            {
                ["是否录像"] = choice.Records,
                ["是否长期保存"] = choice.Keeps,
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
