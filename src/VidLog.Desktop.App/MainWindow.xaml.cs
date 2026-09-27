using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的命名空间都带进来，于是 ComboBox / KeyEventArgs
// 这类同名类型变成「不明确」。这里用**别名钉死成 WPF 的那套** ——
// 这个文件里的控件全是 WPF 的，WinForms 一个都不该出现。
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using RadioButton = System.Windows.Controls.RadioButton;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using VidLog.Desktop.Core;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Media;
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

        // 上次没走完的录像收回来没有（规格 §3.1.1）。**必须说出来** ——
        // 「悄悄收好了」和「其实什么都没收」在界面上长得一模一样，用户无从分辨。
        // 收尾失败的会由 StartupReport.Warnings 走上面的「需要注意」区，不在这里重复。
        var recovered = _host.Startup.RecoveredCount;
        if (recovered > 0)
        {
            NoticesText.Text =
                $"{DateTime.Now:HH:mm:ss}  上次有 {recovered} 段录像没走完收尾，已自动收好并入库。";
        }

        // 保留期到了的那些（规格 §3.5.5）。**排在最后**：它是唯一会删东西的一步，
        // 应该发生在「上次的录像收好了」之后 —— 否则刚收进来的那段可能正好在
        // 清理计划里（它确实会被 24 小时豁免挡住，但顺序反了会让人以为是它被删的）。
        await RunStartupCleanupAsync();
    }

    /// <summary>
    /// 启动时算一次清理计划，**给用户看过才动手**（规格 §3.5.5）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规格原话：「**禁止静默清理**」「清理前必须给出预告（将删除多少条、多少容量）」。
    /// 所以这里不是「启动就自动删」——那样用户永远不知道自己丢了什么，
    /// 而且**丢的是不可逆的证据**。
    /// </para>
    /// <para>
    /// ⚠️ 归档层是本机磁盘时<b>连问都不问</b>（规格 §3.5.1：那时盘上那份是唯一副本）。
    /// 判据取自 <see cref="CleanupService.CanCleanup"/>，与执行层那道闸是同一份 ——
    /// 界面上不问、执行层也会拒，两道都在。
    /// </para>
    /// </remarks>
    private async Task RunStartupCleanupAsync()
    {
        var cleanup = _host.Services.Cleanup;

        if (!cleanup.CanCleanup)
        {
            return;
        }

        try
        {
            var plan = await cleanup.PreviewAsync(_host.Settings.Retention, DateTimeOffset.Now);

            if (plan.Candidates.Count == 0)
            {
                // **没有候选就不打扰** —— 每次开机弹一个「没什么要清的」是噪音，
                // 而噪音会把真正该看的那一次淹掉。
                return;
            }

            var megabytes = plan.TotalBytes / 1024 / 1024;
            var answer = MessageBox.Show(
                this,
                $"保留期到了的录像有 {plan.Candidates.Count} 条，约 {megabytes} MB。\n\n"
                + "要现在清理吗？\n"
                + "· 清理前会逐条回查归档层，**查不到或查不了的那条不会删**；\n"
                + "· 删掉的是本机上这一份，归档层上的那份不动；\n"
                + "· 已锁定与最近 24 小时内录的一条都不会动。",
                "清理本地副本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                // 默认是「否」——不可逆的动作不该让回车键替用户点头。
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                NoticesText.Text = $"{DateTime.Now:HH:mm:ss}  这次没清理（{plan.Candidates.Count} 条仍在盘上）。";
                return;
            }

            var report = await cleanup.RunAsync(plan);

            NoticesText.Text =
                $"{DateTime.Now:HH:mm:ss}  清理完成：删了 {report.Deleted.Count} 条"
                + $"（约 {report.FreedBytes / 1024 / 1024} MB），"
                + $"回查没通过、因此保留的有 {report.Refused.Count} 条（明细见清理流水）。";
        }
        catch (Exception ex)
        {
            // 清理失败不该拦住启动（I4 的同一条精神）—— 但要说出来。
            NoticesText.Text = $"{DateTime.Now:HH:mm:ss}  清理没能进行：{ex.Message}";
        }
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
            SelectByTag(IdleCombo, _host.Settings.IdleReminder.ToString());
            IdleMinutesBox.Text = _host.Settings.IdleReminderMinutes.ToString();
            SelectByTag(DurationCombo, _host.Settings.DurationFallback.ToString());
            SelectByTag(ArchiveCombo, _host.Settings.ArchiveBackend.ToString());
            ArchivePathBox.Text = _host.Settings.ArchiveDirectory ?? string.Empty;
            SelectRadio(CodecButtons, _host.Settings.Codec.ToString());
            SelectRadio(ResolutionButtons, _host.Settings.Resolution.ToString());
            ShowEffectiveSpec();
            SegmentBox.Text = _host.Settings.SegmentMinutes.ToString();
            PortBox.Text = _host.Settings.PlaybackPort.ToString();
            SelectRetention(ArchivedOutboundCombo, _host.Settings.Retention.ArchivedOutbound);
            SelectRetention(ArchivedReturnCombo, _host.Settings.Retention.ArchivedReturn);
            SelectRetention(UnarchivedOutboundCombo, _host.Settings.Retention.UnarchivedOutbound);
            SelectRetention(UnarchivedReturnCombo, _host.Settings.Retention.UnarchivedReturn);
        }
        finally
        {
            _suppressSettingsEvents = false;
        }

        ShowRetention();
        ShowCalibration();
    }

    // ─────────────────────────────────────────────
    // 保留期（规格 §3.5.1 / §3.5.2.1）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 归档层是本地时，保留期这块**根本不出现**。
    /// </summary>
    /// <remarks>
    /// 规格 §3.5.1：那时盘上这份是唯一副本，不允许开启清理。
    /// 与其给一个改了也不生效的下拉（踩坑 #13），不如不显示，并说明为什么 ——
    /// 留白会让人以为没做，说清楚才是「不提供」。
    /// </remarks>
    private void ShowRetention()
    {
        var target = SelectedArchiveTarget();

        RetentionPanel.Visibility =
            target.AllowsCleanup ? Visibility.Visible : Visibility.Collapsed;
        RetentionAbsentNote.Visibility =
            target.AllowsCleanup ? Visibility.Collapsed : Visibility.Visible;
        RetentionAbsentNote.Text =
            "归档层是本机磁盘 —— 盘上这份就是唯一副本，所以不提供保留期设置。"
            + "改成 NAS、挂载网络驱动器或百度网盘之后，这里才会出现。";

        // 目录型（NAS / 挂载盘）才要那个路径框。⚠️ 这两档**共用一份实现**
        // （规格 §3.4.6），所以界面上也是同一个框。
        ArchivePathPanel.Visibility =
            target.IsDirectoryType ? Visibility.Visible : Visibility.Collapsed;

        // 「能不能跨网」要如实说（规格 §2.3：**不得承诺做不到的事**）。
        ArchiveReachNote.Text = target.Reachability;

        ShowArchiveRelayFailure();
    }

    /// <summary>
    /// 「归档层那一份没发上去」——**必须说出来**。
    /// </summary>
    /// <remarks>
    /// 后果很具体：盘上这份现在**只有一份**。用户若以为已经双份了，
    /// 就可能手动删掉唯一的那一份（那正是 I2 要防的事）。
    /// <para>
    /// 归档层就是本机磁盘时没有 relay —— 那一档下「发上去」是空操作，
    /// 本来就不存在这个失败。
    /// </para>
    /// </remarks>
    private void ShowArchiveRelayFailure()
    {
        var relay = _host.Services.ArchiveRelay;

        if (relay?.LastFailure is not { Length: > 0 } failure)
        {
            ArchiveRelayNote.Visibility = Visibility.Collapsed;
            return;
        }

        ArchiveRelayNote.Visibility = Visibility.Visible;
        ArchiveRelayNote.Text =
            $"⚠️ 最近一次发布到{relay.Label}没成功：{failure}\n"
            + "盘上这一份仍然是好的、也能检索 —— 但它现在**只有一份**，"
            + "在发上去之前别删它。修好之后下次收尾会自动再发。";
    }

    // ─────────────────────────────────────────────
    // 时间校准（规格 §3.6.3 / §3.6.4）
    // ─────────────────────────────────────────────

    /// <summary>把当前校准状态显示出来。**要能一眼看出「现在录不录得了」**。</summary>
    private void ShowCalibration()
    {
        var clock = _host.Services.TrustedClock;

        if (clock.IsCalibrated)
        {
            var source = clock.State.Source == CalibrationSource.PublicTime ? "公网时间" : "归档回执";
            var at = clock.State.CalibratedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

            CalibrationNote.Text = $"✅ 已校准（来源：{source}，校准于 {at}）。可以录制。";
            return;
        }

        CalibrationNote.Text = $"⛔ {clock.BlockedReason}";
    }

    /// <summary>点【重新校准】：取一次公网时间。</summary>
    /// <remarks>
    /// ⚠️ 失败时**留着「未校准」那个状态不动**（不猜一个时间）——
    /// 猜出来的锚比没有锚更糟：它看起来是校准过的。
    /// </remarks>
    private async void OnCalibrate(object sender, RoutedEventArgs e)
    {
        CalibrateButton.IsEnabled = false;
        CalibrationNote.Text = "正在取公网时间…";

        try
        {
            var anchor = await _host.Services.ClockSource.QueryAsync();
            await _host.Services.TrustedClock.CalibrateAsync(anchor, CalibrationSource.PublicTime);
        }
        catch (Exception ex)
        {
            CalibrationNote.Text = $"⛔ 取不到公网时间：{ex.Message}";
            return;
        }
        finally
        {
            CalibrateButton.IsEnabled = true;
        }

        ShowCalibration();
    }

    /// <summary>界面上当前选中的归档层。</summary>
    /// <remarks>
    /// 认不出的 Tag 一律回落到本机磁盘 —— 与
    /// <see cref="ArchiveTarget.FromConfig"/> 同一个方向（朝**少删**的那头落）。
    /// </remarks>
    private ArchiveTarget SelectedArchiveTarget() =>
        TagOf(ArchiveCombo) switch
        {
            "Nas" => new ArchiveTarget(ArchiveBackendKind.Nas, ArchivePathBox.Text),
            "MountedDrive" => new ArchiveTarget(ArchiveBackendKind.MountedDrive, ArchivePathBox.Text),
            "Cloud" => new ArchiveTarget(ArchiveBackendKind.Cloud),
            _ => ArchiveTarget.Default,
        };

    /// <summary>
    /// 下拉里的项就是 <see cref="RetentionSetting.Standard"/>（8 档）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 第 9 项「自定义」**不是一个列表项，是一个手输的数** ——
    /// 所以这几个下拉是 `IsEditable="True"` 的：用户直接敲「45」就行，
    /// 不必再为「自定义」造一个输入弹窗（WPF 里没有现成的 `InputBox`）。
    /// <para>
    /// 存着的值不在那 8 档里（自定义过，或手改过设置文件）时，把它的
    /// <see cref="RetentionSetting.Label"/>（形如「45 天」）填进文本框 ——
    /// 这也是**看得见的**：用户能看到自己那个数还在。
    /// </para>
    /// </remarks>
    private static void SelectRetention(ComboBox combo, RetentionSetting setting)
    {
        if (combo.Items.Count == 0)
        {
            foreach (var option in RetentionSetting.Standard)
            {
                combo.Items.Add(option.Label);
            }
        }

        combo.Text = setting.Label;
        combo.SelectedIndex = RetentionSetting.Standard.ToList().IndexOf(setting);
    }

    /// <summary>把下拉里的选择读回来。**认不出的写法一律回落「全部保留」**（朝少删的那头落）。</summary>
    private static RetentionSetting RetentionOf(ComboBox combo)
    {
        // ⚠️ 可编辑下拉：用户敲的东西在 `Text` 里，不一定选中了某一项。
        // 先按**选中项**认，认不出再看文本 —— 顺序反了的话，手输过一个数之后
        // 再点列表里的项，读回来的会是旧文本。
        if (combo.SelectedIndex >= 0
            && combo.SelectedIndex < RetentionSetting.Standard.Count
            && string.Equals(
                combo.Text, RetentionSetting.Standard[combo.SelectedIndex].Label, StringComparison.Ordinal))
        {
            return RetentionSetting.Standard[combo.SelectedIndex];
        }

        var text = combo.Text?.Trim() ?? string.Empty;

        if (text is "全部保留" or "") return RetentionSetting.KeepAll;
        if (text == "不保留") return RetentionSetting.Immediate;

        var digits = text.EndsWith('天') ? text[..^1].Trim() : text;

        return int.TryParse(digits, out var days) ? RetentionSetting.FromConfig(days) : RetentionSetting.KeepAll;
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

    /// <summary>把一组横排的单选按 <c>Tag</c> 选中一个。</summary>
    /// <remarks>
    /// 与 <see cref="SelectByTag"/> 同一件事，只是单选按钮不是 <c>Items</c> 集合 ——
    /// 而这几个选项**必须是横排的**（规格 §3.1.7：「横排（**不用下拉**）」）。
    /// </remarks>
    private static void SelectRadio(IEnumerable<RadioButton> group, string tag)
    {
        foreach (var button in group)
        {
            button.IsChecked = string.Equals(button.Tag as string, tag, StringComparison.Ordinal);
        }
    }

    /// <summary>横排单选里被选中的那个的 <c>Tag</c>。</summary>
    private static string? TagOf(IEnumerable<RadioButton> group) =>
        group.FirstOrDefault(b => b.IsChecked == true)?.Tag as string;

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

    /// <summary>
    /// 从托盘恢复时把刷新重新开起来。
    /// </summary>
    /// <remarks>
    /// 关窗口时会停掉它（窗口都看不见了，刷新纯属白费），
    /// 但那期间录制可能一直在进行 —— 恢复时必须接上，否则状态显示会停在旧值。
    /// </remarks>
    public void RestartTicker()
    {
        if (_host.Coordinator.CurrentWaybill is not null)
        {
            _ticker.Start();
            UpdateRecordingStatus();
        }
    }

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

    /// <summary>
    /// 把选中的那一条**原样**交到用户选的位置（规格 §3.7）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规格原话：「改掉分享连接，只分享视频本身无损完整视频」；电脑端导出到
    /// **用户自选路径**，而且**不能是电脑端存放录像的那个路径**。
    /// </para>
    /// <para>
    /// ⚠️ <b>不转码、不压缩、不裁剪、也不打码</b>（§3.7.1 / §3.6.6）——
    /// 所以这一条路就是「另存为」。导出的成品里面单上的收件人信息**会原样跟出去**，
    /// 那是需求方权衡后的选择，界面**不许**暗示做过隐私处理。
    /// </para>
    /// </remarks>
    private async void OnExportResult(object sender, RoutedEventArgs e)
    {
        var hit = SelectedHit();
        if (hit is null)
        {
            SearchStatus.Text = "先在列表里选一条，再点【导出原视频】。";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出原视频（不转码、不压缩）",
            // 默认文件名就是规格里那个显示名：`快递单号.mp4`。
            FileName = $"{hit.Entry.Waybill.Value}.mp4",
            Filter = "视频文件|*.mp4|所有文件|*.*",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var result = await _host.Services.Exporter.ExportAsync(hit.Entry, dialog.FileName);

        if (!result.Exported)
        {
            // I3：导出失败必须说出来 —— 用户以为交付了，而对方什么都没收到。
            MessageBox.Show(
                result.FailureReason ?? "导出失败。",
                "导出原视频", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SearchStatus.Text = $"已导出到：{result.TargetPath}";

        // 顺手把它所在的文件夹打开 —— 「交付」这个动作的下一步通常就是把文件发出去，
        // 而用户不必自己去文件管理器里找。
        RevealInExplorer(result.TargetPath!);
    }

    /// <summary>选中那一行的 <c>RecordingHit</c>；没选返回 null。</summary>
    private RecordingHit? SelectedHit() =>
        ResultsGrid.SelectedItem?.GetType().GetProperty("Hit")?.GetValue(ResultsGrid.SelectedItem)
            as RecordingHit;

    /// <summary>在资源管理器里选中这个文件（不是打开它）。</summary>
    private void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            // 打不开文件夹不是交付失败 —— 文件已经在用户选的位置上了。
            SearchStatus.Text = $"已导出到：{path}（打开文件夹失败：{ex.Message}）";
        }
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

    private async void OnIdleReminderChanged(object sender, SelectionChangedEventArgs e) =>
        await SaveUiSettingsAsync();

    /// <summary>编码 / 分辨率那一组横排单选。</summary>
    private RadioButton[] CodecButtons => [CodecH264, CodecH265];

    private RadioButton[] ResolutionButtons => [Res4K, Res1080, Res720];

    private async void OnRecordingSpecChanged(object sender, RoutedEventArgs e) =>
        await SaveUiSettingsAsync();

    /// <summary>
    /// 显示**实际会用**的录制规格（规格 §3.1.7：「**回落必须可见**……**不得静默回落**」）。
    /// </summary>
    /// <remarks>
    /// 取值来自启动时那次**真开相机**的探测（<c>AppHost.EffectiveSpec</c>）。
    /// 与用户选的不一样时把原因也说出来 —— 只说「实际是 H.264」而不说为什么，
    /// 用户会以为自己选错了。
    /// </remarks>
    private void ShowEffectiveSpec()
    {
        var effective = _host.EffectiveSpec;
        var wanted = new RecordingSpec(_host.Settings.Codec, _host.Settings.Resolution);

        EffectiveSpecText.Text = effective == wanted
            ? $"这台电脑按 {effective.Label} 录制。"
            : $"⚠️ 你选的是 {wanted.Label}，但这台电脑跑不通 —— 实际按 {effective.Label} 录制。"
              + (string.IsNullOrWhiteSpace(_host.SpecFallbackReason)
                  ? string.Empty
                  : $"原因：{_host.SpecFallbackReason}");

        EffectiveSpecText.Foreground = effective == wanted
            ? System.Windows.Media.Brushes.Gray
            : System.Windows.Media.Brushes.OrangeRed;
    }

    /// <summary>自定义分钟数那一格失焦就存（不必等用户去按保存）。</summary>
    private async void OnIdleMinutesChanged(object sender, RoutedEventArgs e) =>
        await SaveUiSettingsAsync();

    private async void OnDurationChanged(object sender, SelectionChangedEventArgs e) => await SaveUiSettingsAsync();

    private async void OnArchiveChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowRetention();
        await SaveUiSettingsAsync();
    }

    private async void OnRetentionChanged(object sender, SelectionChangedEventArgs e) => await SaveUiSettingsAsync();

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

        // 自定义分钟数：只在选了「自定义」时才管它，否则保持原值
        // （用户先填了 7 分钟又改回 3 分钟，那 7 不该丢 —— 下次切回自定义还要用）。
        var idleMinutes = int.TryParse(IdleMinutesBox.Text, out var parsedMinutes)
            ? Math.Clamp(parsedMinutes, WorkModeOptions.MinIdleMinutes, WorkModeOptions.MaxIdleMinutes)
            : _host.Settings.IdleReminderMinutes;

        var next = _host.Settings with
        {
            Mode = Enum.TryParse<WorkMode>(TagOf(ModeCombo), out var mode) ? mode : _host.Settings.Mode,
            Codec = Enum.TryParse<VideoCodec>(TagOf(CodecButtons), out var codec)
                ? codec : _host.Settings.Codec,
            Resolution = Enum.TryParse<VideoResolution>(TagOf(ResolutionButtons), out var resolution)
                ? resolution : _host.Settings.Resolution,
            IdleReminder = Enum.TryParse<IdleReminderOption>(TagOf(IdleCombo), out var idle)
                ? idle : _host.Settings.IdleReminder,
            IdleReminderMinutes = idleMinutes,
            DurationFallback = Enum.TryParse<DurationFallbackOption>(TagOf(DurationCombo), out var d)
                ? d : _host.Settings.DurationFallback,
            SegmentMinutes = segment,
            PlaybackPort = port,
            CameraDevice = CameraCombo.SelectedItem as string,
            ArchiveBackend = Enum.TryParse<ArchiveBackendKind>(TagOf(ArchiveCombo), out var backend)
                ? backend : _host.Settings.ArchiveBackend,
            // 目录型那两档的根。别的档位下这个框是藏着的，但值仍然记着 ——
            // 用户在 NAS 与挂载盘之间来回切时不必重填一遍。
            ArchiveDirectory = string.IsNullOrWhiteSpace(ArchivePathBox.Text)
                ? null : ArchivePathBox.Text.Trim(),
            Retention = new RetentionSettings(
                RetentionOf(ArchivedOutboundCombo), RetentionOf(ArchivedReturnCombo),
                RetentionOf(UnarchivedOutboundCombo), RetentionOf(UnarchivedReturnCombo)),
        };

        try
        {
            await _host.SaveSettingsAsync(next);
            // 逐项说清楚，别笼统写「下次生效」——
            // 笼统的话就有一半是假的，而用户没法知道是哪一半。
            SettingsStatus.Text =
                "已保存。工作模式立即生效；时长兜底与分段时长下次开段生效；"
                + "摄像头与端口要重启。";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 【连接电脑/手机】—— 弹出二维码（规格 §3.4.5）。
    /// </summary>
    /// <remarks>
    /// 模态：一次只有一个。否则用户可以开出两份二维码，而
    /// <see cref="VidLog.Desktop.Core.Upload.DeviceRegistry"/> 只留得住**最后一张** ——
    /// 屏幕上摆着两张，能用的只有一张，那是最难解释的一种「扫了没反应」。
    /// </remarks>
    private void OnEnroll(object sender, RoutedEventArgs e) =>
        new EnrollWindow(_host) { Owner = this }.ShowDialog();

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
    /// 关窗口 = 收进托盘，**不是退出**（规格 §3.2.1：后台仍要收码）。
    /// </summary>
    /// <remarks>
    /// 真正的退出走托盘菜单的「退出」，由 <see cref="AppHost.ShutdownAsync"/> 收尾。
    /// 这里无条件取消关闭，是为了不区分「首次关闭」与「真要退出」——
    /// 那个区分要靠状态位，而状态位最容易写错成「第二次点 X 才退」这种惊喜。
    /// </remarks>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;

        _ticker.Stop();
        Hide();

        _host.Tray?.Notify("VidLog 还在后台", "扫码枪照常可用。要退出请右键托盘图标。");
    }
}
