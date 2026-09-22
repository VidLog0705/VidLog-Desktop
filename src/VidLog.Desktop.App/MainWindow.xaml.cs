using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VidLog.Desktop.Core;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Scanning;
using VidLog.Desktop.Core.Search;

namespace VidLog.Desktop.App;

/// <summary>
/// 主窗口 —— 纯视图。
/// </summary>
/// <remarks>
/// 装配与生命周期都在 <see cref="AppHost"/> 里：规格 §3.2.1 要求后台仍能收码，
/// 而那要求钩子与录制协调器活得比窗口长。这里只做呈现与把操作转成 Core 调用。
/// </remarks>
public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _ticker;
    private bool _suppressSettingsEvents;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        // 界面上「已录 / 已存」只能靠定时刷新 —— 协调器不推送进度，Elapsed 是拉取式的。
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _ticker.Tick += (_, _) => UpdateRecordingStatus();

        Loaded += OnLoaded;
        Closed += (_, _) => _ticker.Stop();

        _host.Notice += OnNotice;
        _host.Scanned += OnScanned;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowWarnings();

        SearchButton.IsEnabled = true;
        OpenDataFolderButton.IsEnabled = true;

        // 装钩子 —— 装不上要给用户看见（I3），不能让他一直奇怪扫码枪怎么没反应。
        _host.StartKeyboardHook();

        LoadSettingsIntoUi();
        await LoadCamerasAsync();

        // 回放服务起没起来，在启动报告里。
        OpenPlaybackButton.IsEnabled = _host.Services.Server?.BaseUrl is { Length: > 0 };
        StatusText.Text = _host.Services.Server?.BaseUrl is { Length: > 0 }
            ? $"服务已就绪。回放地址：{_host.Services.Server.BaseUrl}"
            : "服务已就绪。回放服务未启动。";
    }

    private void ShowWarnings()
    {
        if (_host.Warnings.Count == 0)
        {
            return;
        }

        WarningsHeader.Visibility = Visibility.Visible;
        WarningsList.Visibility = Visibility.Visible;

        foreach (var warning in _host.Warnings)
        {
            WarningsList.Items.Add(warning);
        }
    }

    private void LoadSettingsIntoUi()
    {
        _suppressSettingsEvents = true;
        try
        {
            SelectByTag(ModeCombo, _host.Settings.Mode.ToString());
            SelectByTag(StaticCombo, _host.Settings.StaticStop.ToString());
            SelectByTag(DurationCombo, _host.Settings.DurationFallback.ToString());
            SegmentBox.Text = _host.Settings.SegmentMinutes.ToString();
            PortBox.Text = _host.Settings.PlaybackPort.ToString();
        }
        finally
        {
            _suppressSettingsEvents = false;
        }
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static string? TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private async Task LoadCamerasAsync()
    {
        if (_host.Services.FfmpegPath is null)
        {
            CameraHint.Text = "没有 FFmpeg，无法采集";
            return;
        }

        var devices = await CameraDevices.ListAsync(_host.Services.FfmpegPath);

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

        var remembered = _host.Settings.CameraDevice;
        CameraCombo.SelectedIndex =
            remembered is not null && devices.Contains(remembered) ? devices.ToList().IndexOf(remembered) : 0;

        CameraCombo.IsEnabled = true;
        WaybillBox.IsEnabled = true;
        RefreshStartButton();
    }

    // ─────────────────────────────────────────────
    // 工作
    // ─────────────────────────────────────────────

    /// <summary>扫码枪扫到了 —— 界面跟着填，让用户看得见识别到了什么。</summary>
    private void OnScanned(ScanOutcome outcome)
    {
        WaybillBox.Text = outcome.Waybill.Value;
        RefreshStartButton();
    }

    private void OnNotice(CoordinatorNotice notice)
    {
        // 通知可能从钩子线程来，派回 UI。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnNotice(notice));
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss}  {notice.Message}";
        var existing = NoticesText.Text;

        // 只留最近若干条 —— 这是给人看的滚动条，不是日志（日志在文件里）。
        var lines = new[] { line }.Concat(existing.Split('\n').Take(19));
        NoticesText.Text = string.Join('\n', lines.Where(l => l.Length > 0));

        UpdateRecordingStatus();
    }

    private void OnWaybillChanged(object sender, TextChangedEventArgs e) => RefreshStartButton();

    private void RefreshStartButton()
    {
        var hasCamera = CameraCombo.SelectedItem is not null;
        var hasWaybill = WaybillNumber.TryParse(WaybillBox.Text, out _, out _);

        StartWorkButton.IsEnabled = hasCamera && hasWaybill && _host.Coordinator.CurrentWaybill is null;
    }

    private async void OnStartWork(object sender, RoutedEventArgs e)
    {
        if (!WaybillNumber.TryParse(WaybillBox.Text, out var waybill, out var error))
        {
            RecordingStatus.Text = $"单号不能用：{error}";
            return;
        }

        StartWorkButton.IsEnabled = false;

        try
        {
            _host.Coordinator.StartWork();
            await _host.Coordinator.SubmitAsync(waybill!, PunchSource.ManualEntry);

            StopWorkButton.IsEnabled = true;
            _ticker.Start();
            UpdateRecordingStatus();
        }
        catch (Exception ex)
        {
            // I3：开录失败必须让用户看见。
            RecordingStatus.Text = $"开录失败：{ex.Message}";
            RefreshStartButton();
        }
    }

    private async void OnStopWork(object sender, RoutedEventArgs e)
    {
        StopWorkButton.IsEnabled = false;

        try
        {
            var outcome = await _host.Coordinator.StopWorkAsync();
            _ticker.Stop();

            RecordingStatus.Text = outcome is null
                ? "已结束。"
                : outcome.Succeeded
                    ? $"已入库 {outcome.Segments.Count} 段。"
                    : $"收尾失败：{outcome.FailureReason} 录像仍在工作区，下次启动会自动重试。";
        }
        catch (Exception ex)
        {
            RecordingStatus.Text = $"收尾出错：{ex.Message}";
        }
        finally
        {
            RefreshStartButton();
        }
    }

    private void UpdateRecordingStatus()
    {
        if (_host.Coordinator.CurrentWaybill is null)
        {
            return;
        }

        var elapsed = _host.Coordinator.Elapsed;
        var clock = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        RecordingStatus.Text = $"录制中 {clock} · {_host.Coordinator.CurrentWaybill.Value}";
        StopWorkButton.IsEnabled = true;
    }

    // ─────────────────────────────────────────────
    // 检索与回放
    // ─────────────────────────────────────────────

    private async void OnSearch(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SearchAsync();
        }
    }

    private async Task SearchAsync()
    {
        var from = FromDate.SelectedDate;
        var to = ToDate.SelectedDate;

        var query = new RecordingQuery
        {
            WaybillText = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim(),
            MatchMode = TagOf(MatchCombo) switch
            {
                "Prefix" => WaybillMatchMode.Prefix,
                "Fuzzy" => WaybillMatchMode.Contains,
                _ => WaybillMatchMode.Exact,
            },
            // 结束那一天要**整日包含**，所以右边界取次日零点（半开区间）。
            From = from is null ? null : new DateTimeOffset(from.Value.Date, DateTimeOffset.Now.Offset),
            To = to is null ? null : new DateTimeOffset(to.Value.Date.AddDays(1), DateTimeOffset.Now.Offset),
            BusinessType = TagOf(BusinessCombo) switch
            {
                "Outbound" => BusinessType.Outbound,
                "Return" => BusinessType.Return,
                _ => null,
            },
        };

        SearchStatus.Text = "正在检索…";

        try
        {
            var hits = await _host.Services.Search.SearchAsync(query);

            ResultsGrid.ItemsSource = hits.Select(h => new
            {
                StartedAt = h.Entry.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Waybill = h.Entry.Waybill.Value,
                Duration = $"{(int)h.Entry.Duration.TotalMinutes}:{h.Entry.Duration.Seconds:00}",
                Business = h.BusinessType switch
                {
                    BusinessType.Outbound => "发货",
                    BusinessType.Return => "退货",
                    _ => "",
                },
                h.Entry.SessionId,
                Hit = h,
            }).ToList();

            SearchStatus.Text = hits.Count == 0
                ? "没有匹配的录像。"
                : $"找到 {hits.Count} 条。选中一条可播放。";
        }
        catch (Exception ex)
        {
            SearchStatus.Text = $"检索出错：{ex.Message}";
        }
    }

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is null)
        {
            return;
        }

        // 播放交给系统默认播放器 —— 本地文件、离线可用（I10），
        // 而且不引入任何播放器依赖。界面内播放留到需要时再做。
        var hit = ResultsGrid.SelectedItem.GetType().GetProperty("Hit")?.GetValue(ResultsGrid.SelectedItem);
        if (hit is not RecordingHit recording)
        {
            return;
        }

        var path = Path.Combine(_host.Services.Layout.ArchiveRoot, recording.Entry.Location.Value);
        SearchStatus.Text = File.Exists(path)
            ? $"双击可直接播放：{path}"
            : $"成品不在盘上：{path}";

        OpenInShell(path);
    }

    private void OnOpenPlayback(object sender, RoutedEventArgs e)
    {
        if (_host.Services.Server?.BaseUrl is { Length: > 0 })
        {
            OpenInShell(_host.Services.Server.BaseUrl);
        }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) =>
        OpenInShell(_host.Services.Layout.RootDirectory);

    private static void OpenInShell(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target) && !target.StartsWith("http", StringComparison.Ordinal))
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

    // ─────────────────────────────────────────────
    // 设置
    // ─────────────────────────────────────────────

    private async void OnModeChanged(object sender, SelectionChangedEventArgs e) => await SaveUiSettingsAsync();

    private async void OnStaticChanged(object sender, SelectionChangedEventArgs e) => await SaveUiSettingsAsync();

    private async void OnDurationChanged(object sender, SelectionChangedEventArgs e) => await SaveUiSettingsAsync();

    private async void OnSaveSettings(object sender, RoutedEventArgs e) => await SaveUiSettingsAsync();

    /// <summary>
    /// 把界面上的设置存下来。
    /// </summary>
    /// <remarks>
    /// 越界的输入**不静默吞掉** —— 说清楚、并且不保存，而不是存进去一个
    /// 之后会让人莫名其妙的值。
    /// </remarks>
    private async Task SaveUiSettingsAsync()
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        if (!int.TryParse(SegmentBox.Text, out var segment) || segment is < 1 or > 10)
        {
            SettingsStatus.Text = "分段时长要在 1~10 分钟之间，本次未保存。";
            return;
        }

        if (!int.TryParse(PortBox.Text, out var port) || port is < 1024 or > 65535)
        {
            SettingsStatus.Text = "端口要在 1024~65535 之间，本次未保存。";
            return;
        }

        var next = _host.Settings with
        {
            Mode = Enum.TryParse<WorkMode>(TagOf(ModeCombo), out var mode) ? mode : _host.Settings.Mode,
            StaticStop = Enum.TryParse<StaticStopOption>(TagOf(StaticCombo), out var s) ? s : _host.Settings.StaticStop,
            DurationFallback = Enum.TryParse<DurationFallbackOption>(TagOf(DurationCombo), out var d)
                ? d : _host.Settings.DurationFallback,
            SegmentMinutes = segment,
            PlaybackPort = port,
            CameraDevice = CameraCombo.SelectedItem as string,
        };

        try
        {
            await _host.SaveSettingsAsync(next);
            SettingsStatus.Text = "已保存。工作模式与档位下次录段生效；端口要重启。";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"保存失败：{ex.Message}";
        }
    }

    private async void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DiagnosticsPackage(new DiagnosticsSources(
                _host.Services.Layout,
                _host.Settings,
                _host.Warnings,
                _ => Task.FromResult<IReadOnlyList<string>>(BuildEnvironmentLines())));

            var path = await package.ExportAsync(_host.Services.Layout.RootDirectory);

            SettingsStatus.Text = $"诊断包已导出：{path}";
            OpenInShell(_host.Services.Layout.RootDirectory);
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"导出失败：{ex.Message}";
        }
    }

    private IReadOnlyList<string> BuildEnvironmentLines()
    {
        var lines = new List<string>
        {
            $"OS: {Environment.OSVersion}",
            $".NET: {Environment.Version}",
            $"机器名: {Environment.MachineName}",
            $"回放服务: {(_host.Services.Server?.BaseUrl is { Length: > 0 } ? _host.Services.Server.BaseUrl : "未启动")}",
        };

        if (_host.Services.FfmpegPath is { } ffmpeg)
        {
            lines.Add($"FFmpeg: {ffmpeg}");
        }

        lines.AddRange(_host.Warnings.Select(w => $"警告: {w}"));
        return lines;
    }

    // ─────────────────────────────────────────────
    // 关闭
    // ─────────────────────────────────────────────

    /// <summary>
    /// 关窗口不等于退出 —— 规格 §3.2.1 要求后台仍能收码。
    /// </summary>
    /// <remarks>
    /// 有在录的段时先收尾：直接退会留下一个未收尾的分段，
    /// 虽然下次启动的孤儿恢复能接上，但**当场收掉**对用户更清楚。
    /// </remarks>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_host.Coordinator.CurrentWaybill is not null)
        {
            e.Cancel = true;

            var answer = MessageBox.Show(
                $"「{_host.Coordinator.CurrentWaybill.Value}」还在录。要结束它并退出吗？",
                "VidLog", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            await _host.Coordinator.StopWorkAsync();
        }

        _ticker.Stop();
        await _host.DisposeAsync();
        Application.Current.Shutdown();
    }
}
