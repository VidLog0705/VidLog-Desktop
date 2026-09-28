using System.ComponentModel;
using System.Reflection;
using System.Windows;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的命名空间都带进来，于是 ComboBox / KeyEventArgs
// 这类同名类型变成「不明确」。这里用**别名钉死成 WPF 的那套**。
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using RadioButton = System.Windows.Controls.RadioButton;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using VidLog.Desktop.App.Platform;
using VidLog.Desktop.Core;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.App;

/// <summary>
/// 设置窗口 —— 纯视图（2026-09-28 从 <see cref="MainWindow"/> 拆出来）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>保存方式与拆窗之前不一样</b>：此前是「改一个下拉就立刻存盘」，
/// 现在照设计图改成<b>显式保存</b>（确定 / 应用 / 取消）。这不是顺手改的 ——
/// 设计图上那三个按钮就是要求用户能反悔，而一个「立刻存」的界面里
/// 【取消】是骗人的（改了已经落盘了）。
/// </para>
/// <para>
/// ⚠️ 关窗时若有未保存的改动会问一句：不问的话用户按了右上角的 ✕
/// 就静默丢掉刚改的东西，而那与他按【取消】长得一模一样。
/// </para>
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly AppHost _host;

    /// <summary>载入设置期间把变更事件挡回去（否则载入本身就把界面标成「有改动」）。</summary>
    private bool _suppressSettingsEvents;

    /// <summary>有未保存的改动。</summary>
    private bool _dirty;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        // 这几个是纯文本框，没有「变更」事件可用 —— 挂一个只用来置脏标记的。
        // （下拉与单选各自在 XAML 上接了自己的处理器。）
        foreach (var box in new[]
                 { IdleMinutesBox, SegmentBox, PortBox, DuplicateDaysBox, ArchivePathBox })
        {
            box.TextChanged += MarkDirty;
        }

        LoadSettingsIntoUi();
        ShowWarnings();
    }

    /// <summary>任何一个输入变了就记一笔「有未保存的改动」。</summary>
    private void MarkDirty(object sender, TextChangedEventArgs e)
    {
        if (!_suppressSettingsEvents)
        {
            _dirty = true;
        }
    }

    /// <summary>换了左边那一节 —— 切五块的 <see cref="UIElement.Visibility"/>。</summary>
    /// <remarks>
    /// ⚠️ 构造期间就会触发：`NavDevice` 上写着 `IsChecked="True"`，
    /// 而 `Checked` 在 `InitializeComponent` 解析到那一行时就发了 ——
    /// 那时下面这些面板**还一个都没建出来**。不挡就是 NullReferenceException。
    /// （与 <see cref="MainWindow"/> 里那个一模一样的坑。）
    /// </remarks>
    private void OnSectionChanged(object sender, RoutedEventArgs e)
    {
        if (DeviceSection is null || StorageSection is null)
        {
            return;
        }

        var target = (sender as RadioButton)?.Tag as string;

        DeviceSection.Visibility = target == "Device" ? Visibility.Visible : Visibility.Collapsed;
        StorageSection.Visibility = target == "Storage" ? Visibility.Visible : Visibility.Collapsed;
        NetworkSection.Visibility = target == "Network" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedSection.Visibility = target == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = target == "About" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把设置读进界面。只在构造时跑一次（窗口每次都新建）。</summary>
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
            SegmentBox.Text = _host.Settings.SegmentMinutes.ToString();
            DuplicateDaysBox.Text = _host.Settings.DuplicateCheckDays.ToString();
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

        _dirty = false;

        ShowEffectiveSpec();
        ShowRetention();
        ShowCalibration();
        ShowLicense();
        ShowAbout();
        LoadCameras();
    }

    // ─────────────────────────────────────────────
    // 摄像头
    // ─────────────────────────────────────────────

    /// <summary>
    /// 把枚举到的摄像头填进下拉。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这里**只是让用户选**。真正录哪一个由启动时的
    /// <see cref="AppHost.DeviceName"/> 决定 —— 所以改了要重启才生效，
    /// 界面上那句话不是客套。
    /// </remarks>
    private async void LoadCameras()
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
    }

    // ─────────────────────────────────────────────
    // 保存
    // ─────────────────────────────────────────────

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        if (await SaveAsync())
        {
            Close();
        }
    }

    private async void OnApply(object sender, RoutedEventArgs e) => await SaveAsync();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 关窗时若有未保存的改动，问一句。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不挡「没有改动」那种情况：每次关窗口都弹一个框是噪声，
    /// 而噪声会把真正该看的那一次淹掉。
    /// </remarks>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_dirty)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "有改动还没保存。要放弃它们吗？",
            "设置", MessageBoxButton.YesNo, MessageBoxImage.Question,
            // 默认「否」——不可逆的动作不该让回车键替用户点头（与清理那个框同一个理由）。
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
    }

    // 几个控件的变更处理器。**它们都只置脏标记，不存盘** ——
    // 存盘在【确定】/【应用】。有两个之所以还留着方法体，是因为它们
    // 顺手要做一件与保存无关的界面刷新（见下面各自的名字）。
    private void OnModeChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();
    private void OnIdleReminderChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();
    private void OnIdleMinutesChanged(object sender, RoutedEventArgs e) => MarkDirty();
    private void OnDurationChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();
    private void OnRetentionChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();
    private void OnRecordingSpecChanged(object sender, RoutedEventArgs e) => MarkDirty();

    /// <summary>归档层变了：保留期那一块的可见性跟着变（与存盘无关的那一半）。</summary>
    private void OnArchiveChanged(object sender, SelectionChangedEventArgs e)
    {
        MarkDirty();
        ShowRetention();
    }

    private void MarkDirty()
    {
        if (!_suppressSettingsEvents)
        {
            _dirty = true;
        }
    }

    /// <summary>
    /// 把界面上的设置存下来。
    /// </summary>
    /// <returns>存成功了返回 true。</returns>
    /// <remarks>
    /// 越界的输入**不静默吞掉** —— 说清楚、并且不保存，而不是存进去一个
    /// 之后会让人莫名其妙的值。⚠️ 校验不过时**不关窗**：关掉的话用户
    /// 连自己填错了什么都看不见。
    /// </remarks>
    private async Task<bool> SaveAsync()
    {
        if (!int.TryParse(SegmentBox.Text, out var segment) || segment is < 1 or > 10)
        {
            SettingsStatus.Text = "分段时长要在 1~10 分钟之间，本次未保存。";
            return false;
        }

        if (!int.TryParse(PortBox.Text, out var port) || port is < 1024 or > 65535)
        {
            SettingsStatus.Text = "端口要在 1024~65535 之间，本次未保存。";
            return false;
        }

        // 重复单号检测的天数（规格 §3.2.5「N 可配置」）。**0 = 关闭。**
        // ⚠️ 界面上写清「0 = 关闭」，而这里也接受 0 —— 否则那句话就是空话。
        if (!int.TryParse(DuplicateDaysBox.Text, out var duplicateDays)
            || duplicateDays is < 0 or > 365)
        {
            SettingsStatus.Text = "重复单号检测要填 0~365 天（0 = 关闭），本次未保存。";
            return false;
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
            DuplicateCheckDays = duplicateDays,
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
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"保存失败：{ex.Message}";
            return false;
        }

        _dirty = false;

        // 逐项说清楚，别笼统写「下次生效」——
        // 笼统的话就有一半是假的，而用户没法知道是哪一半。
        SettingsStatus.Text =
            "已保存。工作模式立即生效；时长兜底与分段时长下次开段生效；摄像头与端口要重启。";

        // 保存之后再刷一遍：实际规格那句依赖刚存下的编码/分辨率，
        // 不刷的话它会一直说上一次的那个组合。
        ShowEffectiveSpec();
        return true;
    }

    // ─────────────────────────────────────────────
    // 录制规格（规格 §3.1.7）
    // ─────────────────────────────────────────────

    private RadioButton[] CodecButtons => [CodecH264, CodecH265];

    private RadioButton[] ResolutionButtons => [Res4K, Res1080, Res720];

    /// <summary>
    /// 显示**实际会用**的录制规格。
    /// </summary>
    /// <remarks>
    /// 规格 §3.1.7：「**回落必须可见**……**不得静默回落**」。
    /// 取值来自启动时那次**真开相机**的探测（<c>AppHost.EffectiveSpec</c>）。
    /// 与用户选的不一样时把原因也说出来 —— 只说「实际是 H.264」而不说为什么，
    /// 用户会以为自己选错了。
    /// <para>
    /// ⚠️ 这一句在**主窗口上看不见**（设计图的录制台上没有这个位置）——
    /// 它挪进了设置里。规格要的是「可见」，不是「必须印在首页」，
    /// 但它确实比以前难看见了，这一笔记在 `docs/实现决策.md`。
    /// </para>
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
            ? (System.Windows.Media.Brush)FindResource("TextSecondary")
            : (System.Windows.Media.Brush)FindResource("Warning");
    }

    // ─────────────────────────────────────────────
    // 时间校准（规格 §3.6.3 / §3.6.4）
    // ─────────────────────────────────────────────

    /// <summary>把当前校准状态显示出来。**要能一眼看出「现在录不录得了」**。</summary>
    private void ShowCalibration() => CalibrationNote.Text = StatusSummaries.Calibration(_host);

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

    // ─────────────────────────────────────────────
    // 许可（规格 §3.9 / `docs/04-许可设计.md`）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 把机器码与激活状态显示出来。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>机器码一直显示，激活不激活都显示</b>：它是用户唯一需要抄给提供方的东西，
    /// 而那件事发生在**他还没激活的时候**。藏在「激活之后」的界面里等于没有。
    /// <para>
    /// ⚠️ 降级（某一段 WMI 读不到）也**必须显示出来**：全零段的机器码没有任何区分度，
    /// 同型号的机器会互相匹配 —— 用户拿它去签发，签出来的码在别人的机器上也能用。
    /// </para>
    /// </remarks>
    private void ShowLicense()
    {
        var license = _host.Services.License;

        if (license is null)
        {
            // 公钥没配（部署时漏了环境变量）。**这不是用户的激活码有问题** ——
            // 说准，否则他会一直去找卖家换码，而换了也没用。
            MachineCodeBox.Text = string.Empty;
            ActivationBox.IsEnabled = false;
            ActivateButton.IsEnabled = false;
            LicenseNote.Text =
                "⛔ 本机软件没配好许可公钥（部署时漏了），激活会一律失败。"
                + "这是安装的问题，不是你激活码的问题 —— 请联系提供方重新安装。"
                + "⚠️ 这只影响新的录制与新的手机接入，已有的录像照常可查可导出。";
            return;
        }

        var status = license.Status;
        MachineCodeBox.Text = status.MachineCode;

        var degraded = status.Degraded
            ? "⚠️ 这台机器的部分硬件标识读不到（机器码里有全零段），"
              + "同型号的机器可能算出一样的码 —— 请先查清为什么读不到（常见是 WMI 被禁用了）。"
            : string.Empty;

        LicenseNote.Text = status.Activated
            ? $"✅ 已激活：允许接入 {status.Slots} 台手机端。{degraded}"
            : $"⛔ {status.FailureReason}允许接入 0 台手机端。{degraded}";

        ActivationBox.IsEnabled = true;
        ActivateButton.IsEnabled = true;
    }

    /// <summary>点【复制】：把机器码放进剪贴板。</summary>
    /// <remarks>
    /// 包一层 try：剪贴板是**跨进程共享**的资源，另一个程序正占着它时
    /// <c>SetText</c> 会抛 <c>COMException</c>。为了这个崩掉整个界面不值得，
    /// 但也不能装作复制成功了 —— 所以失败时明说「请手动选中复制」。
    /// </remarks>
    private void OnCopyMachineCode(object sender, RoutedEventArgs e)
    {
        var code = MachineCodeBox.Text;

        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        try
        {
            // 必须限定：本程序同时引了 WinForms（托盘图标），
            // 那边的 Clipboard 与 WPF 的撞名，不限定就编译不过。
            System.Windows.Clipboard.SetText(code);
            SettingsStatus.Text = "机器码已复制。把它发给提供方换取激活码。";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"复制不了（{ex.Message}）—— 请手动选中上面那串机器码复制。";
        }
    }

    /// <summary>点【激活】：校验用户粘进来的码，过了就落盘。</summary>
    /// <remarks>
    /// ⚠️ 校验失败时**不动已经激活的状态**（一次手滑不该把已激活的机器锁掉）——
    /// 那件事在 <c>LicenseService.ActivateAsync</c> 里，这里只负责把结果说出来。
    /// <para>
    /// ⚠️ 激活之后**要重启才生效**吗：不用。本次运行里 <c>LicenseService.Status</c>
    /// 会被更新，而录制闸门读的是它 —— 所以激活之后立刻就能开工。
    /// 反过来（运行中失效）才要重启，那是 L7 的「运行期冻结」。
    /// </para>
    /// </remarks>
    private async void OnActivate(object sender, RoutedEventArgs e)
    {
        var license = _host.Services.License;

        if (license is null)
        {
            return;
        }

        var code = ActivationBox.Text;

        if (string.IsNullOrWhiteSpace(code))
        {
            LicenseNote.Text = "先把你从提供方那里拿到的激活码粘进上面的框。";
            return;
        }

        ActivateButton.IsEnabled = false;

        try
        {
            var status = await license.ActivateAsync(code);

            if (status.Activated)
            {
                ActivationBox.Clear();
            }
            else
            {
                // 把那句原因原样说出来 —— 它已经区分了「码不对」「不是本机的」
                // 「版本要升级」「本机没配好公钥」四种，这里不该再改写一遍。
                LicenseNote.Text = $"⛔ {status.FailureReason}";
                return;
            }
        }
        catch (Exception ex)
        {
            LicenseNote.Text = $"⛔ 激活没能完成：{ex.Message}";
            return;
        }
        finally
        {
            ActivateButton.IsEnabled = true;
        }

        ShowLicense();
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
            + "盘上这一份仍然是好的、也能检索 —— 但它现在只有一份，"
            + "在发上去之前别删它。修好之后下次收尾会自动再发。";
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

    // ─────────────────────────────────────────────
    // 关于
    // ─────────────────────────────────────────────

    /// <summary>填「关于」那一页。**只放真读得出来的东西**（规格 §13.1）。</summary>
    private void ShowAbout()
    {
        var assembly = typeof(SettingsWindow).Assembly;

        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "未知";

        AboutVersionText.Text = version;
        AboutSpecText.Text = _host.EffectiveSpec.Label
            + $"（用户选的是 {new RecordingSpec(_host.Settings.Codec, _host.Settings.Resolution).Label}）";

        var server = _host.Services.Server;
        AboutServerText.Text = server?.BaseUrl is { Length: > 0 } url
            ? url
            : "未启动";
        AboutArchiveText.Text = _host.Services.ArchiveTarget.Label;
        AboutDataDirText.Text = _host.Services.Layout.RootDirectory;

        // 「局域网与网页」那一节里也有一句 —— 两处读的是**同一个** server 对象，
        // 不可能一边说已启动一边说未启动。
        ServerUrlText.Text = server?.BaseUrl is { Length: > 0 } other
            ? $"现在这个地址是：{other}"
              + (server.IsUsingFallback ? "（只绑到本机，别的设备访问不了）" : string.Empty)
            : "回放服务没有起来 —— 手机和别的电脑现在打不开回放页。";
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        if (ShellOpen.Try(_host.Services.Layout.RootDirectory) is { } error)
        {
            SettingsStatus.Text = error;
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
            ShellOpen.Try(_host.Services.Layout.RootDirectory);
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
            $"回放服务: {(_host.Services.Server?.BaseUrl is { Length: > 0 } url ? url : "未启动")}",
        };

        if (_host.Services.FfmpegPath is { } ffmpeg)
        {
            lines.Add($"FFmpeg: {ffmpeg}");
        }

        lines.AddRange(_host.Warnings.Select(w => $"警告: {w}"));
        return lines;
    }

    /// <summary>启动时的问题。**必须看得见** —— 它们发生在用户没盯着屏幕的时候。</summary>
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

    // ─────────────────────────────────────────────
    // 连接手机（规格 §3.4.5）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 【连接电脑/手机】—— 弹出二维码。
    /// </summary>
    /// <remarks>
    /// 模态：一次只有一个。否则用户可以开出两份二维码，而
    /// <c>DeviceRegistry</c> 只留得住**最后一张** —— 屏幕上摆着两张，
    /// 能用的只有一张，那是最难解释的一种「扫了没反应」。
    /// </remarks>
    private void OnEnroll(object sender, RoutedEventArgs e) =>
        new EnrollWindow(_host) { Owner = this }.ShowDialog();

    // ─────────────────────────────────────────────
    // 小的取值助手
    // ─────────────────────────────────────────────

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
}
