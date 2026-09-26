using System.Windows;
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
            _host = await AppHost.StartAsync(logger);

            _window = new MainWindow(_host);
            MainWindow = _window;

            // 关窗口只是收进托盘，退出走托盘菜单 —— 有在录的段时先问一句。
            _host.ConfirmExitWhileRecording = ConfirmExitWhileRecording;

            _host.AttachTray(
                showWindow: ShowWindow,
                exitApplication: () => _ = ExitAsync());

            _host.Notice += OnNotice;

            _window.Show();
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

            case CoordinatorNoticeKind.FinalizeFailed or CoordinatorNoticeKind.WrongWaybill:
                _host?.Tray?.Notify("VidLog", notice.Message);
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
