using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using VidLog.Desktop.Core;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.App;

/// <summary>
/// 主窗口。
/// </summary>
/// <remarks>
/// **刻意保持很薄** —— 只做三件事：装配（<see cref="DesktopServices.Create"/>）、
/// 呈现启动报告、把用户操作转成系统动作。
/// 「重启后收尾孤儿」「起回放服务」「分段滚动与收尾」这些行为都在 Core 里，
/// 这样它们才测得到（见 <see cref="DesktopServices.StartAsync"/>、<see cref="RecordingSession"/>）。
/// </remarks>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _recordingTicker;

    private DesktopServices? _services;
    private string? _playbackUrl;
    private RecordingSession? _session;
    private Task? _recordingLoop;
    private CancellationTokenSource? _recordingCancellation;

    /// <summary>探测出来的编码器名。首次开录前探一次，之后复用。</summary>
    private string? _encoder;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        // 界面上的「已录 / 已存」只能靠定时刷新 —— RecordingSession 不推送事件，
        // 它的 Elapsed 是拉取式的。500ms 足够让人看见秒在走，又不会白费 CPU。
        _recordingTicker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _recordingTicker.Tick += (_, _) => UpdateRecordingStatus();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _services = DesktopServices.Create(DataLayout.Default());

            var report = await _services.StartAsync();

            ShowReport(report);
            await LoadCamerasAsync();
        }
        catch (Exception ex)
        {
            // 启动失败必须让用户看见，不能静默退出（I3 的同一条精神）。
            StatusText.Text = $"启动失败：{ex.Message}";
        }
    }

    // ─────────────────────────────────────────────
    // 摄像头与单号
    // ─────────────────────────────────────────────

    private async Task LoadCamerasAsync()
    {
        if (_services?.FfmpegPath is null)
        {
            CameraHint.Text = "没有 FFmpeg，无法采集";
            return;
        }

        var devices = await CameraDevices.ListAsync(_services.FfmpegPath);

        if (devices.Count == 0)
        {
            // 「没有摄像头」是正常的运行环境，不是错误 —— 说清楚就行。
            CameraHint.Text = "没有找到摄像头";
            return;
        }

        foreach (var device in devices)
        {
            CameraCombo.Items.Add(device);
        }

        CameraCombo.SelectedIndex = 0;
        CameraCombo.IsEnabled = true;
        WaybillBox.IsEnabled = true;
    }

    private void OnCameraChanged(object sender, RoutedEventArgs e) => RefreshStartButton();

    private void OnWaybillChanged(object sender, RoutedEventArgs e) => RefreshStartButton();

    /// <summary>
    /// 【开始工作】只在「有摄像头 + 单号能解析成合法单号」时可用。
    /// </summary>
    /// <remarks>
    /// 单号走 <see cref="WaybillNumber.TryParse"/> 而不是非空判断：
    /// 归一化后会变空的输入（全是空白）不该被放行 —— I5 说单号是唯一事实标识。
    /// </remarks>
    private void RefreshStartButton()
    {
        var hasCamera = CameraCombo.SelectedItem is not null;
        var hasWaybill = WaybillNumber.TryParse(WaybillBox.Text, out _, out _);

        StartWorkButton.IsEnabled =
            _session is null && hasCamera && hasWaybill && _services?.FfmpegPath is not null;
    }

    // ─────────────────────────────────────────────
    // 开录 / 停录
    // ─────────────────────────────────────────────

    private async void OnStartWork(object sender, RoutedEventArgs e)
    {
        if (_services is null || CameraCombo.SelectedItem is not string device)
        {
            return;
        }

        if (!WaybillNumber.TryParse(WaybillBox.Text, out var waybill, out var error))
        {
            RecordingStatus.Text = $"单号不能用：{error}";
            return;
        }

        StartWorkButton.IsEnabled = false;
        RecordingStatus.Text = "正在准备…";

        try
        {
            // 规格 §3.1.5：编码能力必须实测，不假定硬件编码可用。
            // 结果缓存起来 —— 探测要真跑几次编码，每件包裹都探一遍太慢。
            _encoder ??= EncoderSelection.Select(await _services.EncoderProbe.ProbeAsync())
                ?? throw new InvalidOperationException("本机没有任何可用的 H.264 编码器。");

            var session = _services.CreateRecordingSession(device);
            await session.StartAsync(waybill!, _encoder);

            _session = session;
            _recordingCancellation = new CancellationTokenSource();
            _recordingLoop = session.RunAsync(_encoder, _recordingCancellation.Token);

            _recordingTicker.Start();
            UpdateRecordingStatus();

            StopWorkButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            // I3：录制起不来必须让用户看见，不能静默失败。
            RecordingStatus.Text = $"开录失败：{ex.Message}";
            _session = null;
            RefreshStartButton();
        }
    }

    private async void OnStopWork(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        StopWorkButton.IsEnabled = false;
        RecordingStatus.Text = "正在收尾…";

        try
        {
            if (_recordingCancellation is not null)
            {
                await _recordingCancellation.CancelAsync();
            }

            // 循环自己也会收尾，这里再调一次是幂等的（TryStopAsync）——
            // 用户点【结束】时不该等循环先发现令牌被取消。
            var outcome = await session.TryStopAsync(StopReason.Manual);
            await session.Completion;

            _recordingTicker.Stop();

            if (outcome is null)
            {
                RecordingStatus.Text = "已结束。";
            }
            else if (outcome.Succeeded)
            {
                var segments = outcome.Segments.Count;
                RecordingStatus.Text = $"已入库 {segments} 段。";
            }
            else
            {
                // 收尾失败**必须**让用户看见（I3）—— 而且要说明东西还在，
                // 否则用户会以为录像丢了。
                RecordingStatus.Text =
                    $"收尾失败：{outcome.FailureReason} 录像仍在工作区，下次启动会自动重试。";
            }
        }
        catch (Exception ex)
        {
            RecordingStatus.Text = $"收尾出错：{ex.Message}";
        }
        finally
        {
            if (_recordingLoop is not null)
            {
                await Task.WhenAny(_recordingLoop, Task.Delay(TimeSpan.FromSeconds(5)));
            }

            await session.DisposeAsync();

            _session = null;
            _recordingLoop = null;
            _recordingCancellation?.Dispose();
            _recordingCancellation = null;

            _recordingTicker.Stop();
            RefreshStartButton();
        }
    }

    private void UpdateRecordingStatus()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        var elapsed = session.Elapsed;
        var clock = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        var problem = string.IsNullOrWhiteSpace(session.LastProblem)
            ? string.Empty
            : $" · {session.LastProblem}";

        RecordingStatus.Text =
            $"录制中 {clock} · 已存 {session.ClosedSegmentCount} 段{problem}";
    }

    private void ShowReport(StartupReport report)
    {
        _playbackUrl = report.PlaybackUrl;

        var recovered = report.RecoveredCount > 0
            ? $"，收尾了 {report.RecoveredCount} 段上次没走完的录像"
            : string.Empty;

        StatusText.Text = _playbackUrl is null
            ? $"服务已就绪{recovered}。回放服务未启动。"
            : $"服务已就绪{recovered}。\n回放地址：{_playbackUrl}";

        OpenPlaybackButton.IsEnabled = _playbackUrl is not null;
        OpenDataFolderButton.IsEnabled = true;

        if (report.Warnings.Count > 0)
        {
            WarningsHeader.Visibility = Visibility.Visible;
            WarningsList.Visibility = Visibility.Visible;

            foreach (var warning in report.Warnings)
            {
                WarningsList.Items.Add(warning);
            }
        }
    }

    private void OnOpenPlayback(object sender, RoutedEventArgs e)
    {
        OpenInShell(_playbackUrl ?? string.Empty);
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        if (_services is not null)
        {
            OpenInShell(_services.Layout.RootDirectory);
        }
    }

    private static void OpenInShell(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打不开：{ex.Message}", "VidLog", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var services = _services;
        var session = _session;
        _services = null;
        _session = null;

        _recordingTicker.Stop();

        // 录制中关窗口：必须让采集进程停下来，否则它会成为孤儿占着分片文件 ——
        // 那正是「收尾只有一条路径」（I9）要防的。会话的 DisposeAsync 会优雅停采集，
        // 留下一个未收尾的分段，下次启动由孤儿恢复接上。
        if (session is not null)
        {
            _ = session.DisposeAsync();
        }

        if (services is null)
        {
            return;
        }

        // 刻意**不**等它完成：关窗口不该卡住。监听套接字随进程退出一起收掉，
        // 主动停一下只是为了让端口尽快释放、方便马上重启。
        _ = services.DisposeAsync();
    }
}
