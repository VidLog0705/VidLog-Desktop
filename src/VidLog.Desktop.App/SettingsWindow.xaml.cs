using System.ComponentModel;
// ⚠️ `System.IO` 要显式写：这台 SDK 一开 `UseWPF` 就不再把 `System.IO` 与
// `System.Net.Http` 放进隐式 using 里了（`Path` 会与 `Shapes.Path` 撞名）——
// 所以这个文件里 `File` / `Path` 不像普通 .NET 工程那样随手可用。
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的命名空间都带进来，于是 ComboBox / KeyEventArgs
// 这类同名类型变成「不明确」。这里用**别名钉死成 WPF 的那套**。
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
// ⚠️ 同 MainWindow：`PixelFormats` 两边都有，不钉死会选错那个（画二维码时表现是编不过）。
using PixelFormats = System.Windows.Media.PixelFormats;
using TextBlock = System.Windows.Controls.TextBlock;
using RadioButton = System.Windows.Controls.RadioButton;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using ToolTipService = System.Windows.Controls.ToolTipService;
using VidLog.Desktop.App.Platform;
using VidLog.Desktop.Core;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Upload;

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
/// <para>
/// ⚠️ <b>2026-10-06（改造清单 T26①）按页签拆成了 partial</b> —— 本文件留着字段、
/// 构造器、载入和<b>保存</b>（那是整个窗口的主干），其余各页签分在
/// <c>SettingsWindow.Disks.cs</c>（多磁盘 / 容量条）、<c>SettingsWindow.Recording.cs</c>
/// （清理按钮 / 摄像头 / 声音 / 录制规格 / 校准）、<c>SettingsWindow.License.cs</c>、
/// <c>SettingsWindow.Preferences.cs</c>（外观 / 启动 / 日志 / 关于 / 诊断）、
/// <c>SettingsWindow.Cloud.cs</c>（百度网盘）。搬家本身是为了让「删录像」的决策
/// 离开按钮点击事件、落进 <c>CleanupFlow</c>；按页签拆是顺带 —— 拆之前 2801 行，
/// 母仓 §4 要求拆（验收见 <c>DesktopServicesTests.T26_点名的长文件拆完之后都不超过800行</c>）。
/// </para>
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly AppHost _host;

    /// <summary>载入设置期间把变更事件挡回去（否则载入本身就把界面标成「有改动」）。</summary>
    private bool _suppressSettingsEvents;

    /// <summary>有未保存的改动。</summary>
    private bool _dirty;

    /// <summary>「录像保存位置」与「录像备份位置」两张表的行。</summary>
    /// <remarks>
    /// ⚠️ 界面状态**只放这里**，不从 <see cref="ListBox"/> 上读回来：
    /// 上移/下移会重设 `ItemsSource`，那一瞬间控件里的东西与列表不是同一份。
    /// </remarks>
    private readonly List<DiskSlotRow> _saveDiskRows = [];
    private readonly List<DiskSlotRow> _backupDiskRows = [];

    /// <summary>探卷容量。无状态，做一次就够。</summary>
    private static readonly IVolumeSpaceProbe Volumes = new DriveVolumeProbe();

    // ─────────────────────────────────────────────
    // 百度网盘那一页的界面状态（批次 5）
    // ─────────────────────────────────────────────

    /// <summary>只在这一页可见时跑：队列进度是**别人**（后台线程）推进的，不刷就是死的。</summary>
    private readonly DispatcherTimer _cloudTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2),
    };

    /// <summary>队列当前在第几页。</summary>
    private int _cloudQueuePage = 1;

    /// <summary>
    /// 正等着用户去授权页点「同意」的那一串设备码（没有就是 <see langword="null"/>）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>不留一个模态对话框去等。</b>那个框会挡住整个设置窗、而且用户
    /// 去浏览器点同意时它还在那儿杵着 —— 而这件事本来就是「你去那边操作，
    /// 这边自己会好」。所以状态记在这里，由那一页的刷新节拍去问。
    /// </remarks>
    private BaiduDeviceCode? _cloudLogin;

    private DateTimeOffset _cloudNextPollAt;
    private DateTimeOffset _cloudLoginExpiresAt;

    /// <summary>屏幕上那张二维码画的是哪一个用户码 —— 没画、或画的是上一个，就重画。</summary>
    /// <remarks>
    /// ⚠️ 这个字段存在的唯一理由是**省掉每两秒一次的白重画**：刷新节拍还兼着轮询进度，
    /// 每跑一次都重画一张一模一样的图，屏幕上那张会一闪一闪的。
    /// </remarks>
    private string? _cloudQrUserCode;

    /// <summary>这一页上要说的一句**临时**话（正在同步、上一步没成…）。空着就按状态自动写。</summary>
    private string? _cloudNote;

    /// <summary>「立即对比同步 / 立即重试失败上传」正在跑。</summary>
    private bool _cloudBusy;

    /// <summary>刷新正在跑（定时器与「切到这一页」会同时触发）。</summary>
    private bool _cloudRefreshing;

    /// <summary>队列的**只读**读法。写永远走服务里那一个（<c>UploadQueue.AppendAsync</c>）。</summary>
    private readonly UploadQueue? _cloudQueueReader;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        // 这几个是纯文本框，没有「变更」事件可用 —— 挂一个只用来置脏标记的。
        // （下拉与单选各自在 XAML 上接了自己的处理器。两张磁盘表的行在模板里接。）
        foreach (var box in new[]
                 { IdleMinutesBox, SegmentBox, PortBox, DuplicateDaysBox, CloudAppNameBox })
        {
            box.TextChanged += MarkDirty;
        }

        // 队列文件是共享的（服务在往里写），所以这里只读、只建一个。
        if (_host.Services.CloudUploads is { } uploads)
        {
            _cloudQueueReader = new UploadQueue(uploads.QueuePath);
        }

        _cloudTimer.Tick += (_, _) => _ = RefreshCloudPageAsync();

        LoadSettingsIntoUi();
        ShowWarnings();

        // 容量条要遍历一遍录像目录（几万个文件时是秒级）⇒ 放最后、异步跑，
        // 不让它挡住窗口弹出来。
        _ = RefreshCapacityAsync();

        // 网盘那一页的「能不能用」是**当下**算出来的（归档层选的是哪一档、
        // 凭据配没配），所以窗口一出来就填一次。
        _ = RefreshCloudPageAsync();
    }

    /// <summary>任何一个输入变了就记一笔「有未保存的改动」。</summary>
    private void MarkDirty(object sender, TextChangedEventArgs e) => MarkDirty();

    /// <summary>换了左边那一节 —— 切五块的 <see cref="UIElement.Visibility"/>。</summary>
    /// <remarks>
    /// ⚠️ 构造期间就会触发：`NavDevice` 上写着 `IsChecked="True"`，
    /// 而 `Checked` 在 `InitializeComponent` 解析到那一行时就发了 ——
    /// 那时下面这些面板**还一个都没建出来**。不挡就是 NullReferenceException。
    /// （与 <see cref="MainWindow"/> 里那个一模一样的坑。）
    /// </remarks>
    private void OnSectionChanged(object sender, RoutedEventArgs e)
    {
        // ⚠️ `AboutVersionText` 也要一起判空：XAML 解析期间这几个单选的 Checked
        // 就会触发（这就是这个守卫存在的原因），而「关于」那一页的控件是最后
        // 才建出来的 —— 只判到 DeviceSection 的话，在这里读它会空引用。
        // ⚠️ 批次 G 新增的三节**也要判到**：它们排在「关于」**前面**（按左导航的次序
        // 写在 XAML 里），所以加载到它们的 `Checked` 时「关于」还没建出来 ——
        // 守卫仍然要认 `AboutVersionText` 这个最靠后的哨兵，三者一起判。
        if (DeviceSection is null || StorageSection is null || AboutVersionText is null
            || ScanSection is null || RecordingSection is null || AudioSection is null)
        {
            return;
        }

        var target = (sender as RadioButton)?.Tag as string;

        DeviceSection.Visibility = target == "Device" ? Visibility.Visible : Visibility.Collapsed;
        ScanSection.Visibility = target == "Scan" ? Visibility.Visible : Visibility.Collapsed;
        StorageSection.Visibility = target == "Storage" ? Visibility.Visible : Visibility.Collapsed;
        CloudSection.Visibility = target == "Cloud" ? Visibility.Visible : Visibility.Collapsed;
        RecordingSection.Visibility = target == "Recording" ? Visibility.Visible : Visibility.Collapsed;
        AudioSection.Visibility = target == "Audio" ? Visibility.Visible : Visibility.Collapsed;
        NetworkSection.Visibility = target == "Network" ? Visibility.Visible : Visibility.Collapsed;
        ExtensionsSection.Visibility = target == "Extensions" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedSection.Visibility = target == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = target == "About" ? Visibility.Visible : Visibility.Collapsed;

        // 网盘那一页的刷新节拍跟着可见性走：看不见的时候不刷 ——
        // 每两秒读一次队列文件，而队列可能有几千行。
        if (target == "Cloud")
        {
            _cloudTimer.Start();
            _ = RefreshCloudPageAsync();
        }
        else
        {
            _cloudTimer.Stop();
        }

        // 「关于」那一页有六行是**会变的**（录制规格、归档目标、更新结论…），
        // 而它只在开窗时填过一次 —— 用户改完设置再切过来看到的就是旧值。
        // 进来一次重填一次，这几行全是只读的，重填没有副作用。
        if (target == "About")
        {
            ShowAbout();
        }
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
            SelectRadio(CodecButtons, _host.Settings.Codec.ToString());
            SelectRadio(ResolutionButtons, _host.Settings.Resolution.ToString());
            SegmentBox.Text = _host.Settings.SegmentMinutes.ToString();
            SelectByTag(PrerecordCombo, _host.Settings.PrerecordSeconds.ToString());
            DuplicateDaysBox.Text = _host.Settings.DuplicateCheckDays.ToString();
            PortBox.Text = _host.Settings.PlaybackPort.ToString();
            SelectRetention(ArchivedOutboundCombo, _host.Settings.Retention.ArchivedOutbound);
            SelectRetention(ArchivedReturnCombo, _host.Settings.Retention.ArchivedReturn);
            SelectRetention(UnarchivedOutboundCombo, _host.Settings.Retention.UnarchivedOutbound);
            SelectRetention(UnarchivedReturnCombo, _host.Settings.Retention.UnarchivedReturn);
            LoadRoles();
            LoadDisks();
            LoadPreferences();
            LoadCloseActions();
            LoadCloud();

            RunAtStartupToggle.IsChecked = _host.Settings.RunAtStartup;
            CheckUpdateToggle.IsChecked = _host.Settings.CheckForUpdates;
            FillLogging(_host.Settings);
        }
        finally
        {
            _suppressSettingsEvents = false;
        }

        _dirty = false;
        SyncCleanupButtons();

        ShowEffectiveSpec();
        ShowRetention();
        ShowCalibration();
        ShowLicense();
        ShowCloseActionNote();
        ShowThemeNote();
        ShowAbout();
        LoadCameras();
        LoadAudio();
    }

    /// <summary>
    /// 把四档用途填进下拉（批次 4，照设计图 `_42`）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 名字从 <see cref="StationRoles.Describe"/> 取，**不在这里再写一份** ——
    /// 写两份的下场是这个下拉说「录像文件备份主机」、而「选择用途」窗口上
    /// 说另一个名字，用户会以为是两件事。
    /// </remarks>
    private void LoadRoles()
    {
        foreach (var role in Enum.GetValues<StationRole>())
        {
            RoleCombo.Items.Add(new ComboBoxItem
            {
                Content = StationRoles.Describe(role).Title,
                Tag = role.ToString(),
            });
        }

        SelectByTag(RoleCombo, _host.Settings.StationRole.ToString());
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
    /// 打开配置向导（批次 3，照设计图 `_16`–`_34`）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>先把没保存的改动处理掉</b>：向导是**直接写盘**的
    /// （<see cref="AppHost.SaveSettingsAsync"/>），而本窗还攥着一份
    /// 「用户改了但没保存」的界面状态 —— 用户从向导回来再按【确定】，
    /// 就会用**打开向导之前**那份值把向导刚存的东西盖掉，而且横竖都不报错。
    /// </para>
    /// <para>
    /// ⚠️ 向导存了要 <see cref="LoadSettingsIntoUi"/> 重载一遍：不重载的话
    /// 这个界面上显示的仍是老值，用户一按【确定】又把它们写回去 —— 同一次覆盖，
    /// 只是晚了一手。
    /// </para>
    /// </remarks>
    private async void OnOpenWizard(object sender, RoutedEventArgs e)
    {
        if (_dirty)
        {
            var answer = MessageBox.Show(
                this,
                "有改动还没保存，而配置向导会直接写盘。\n\n"
                + "点【是】先保存再打开向导；点【否】放弃这些改动再打开；点【取消】先不开。",
                "配置向导", MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
                // 默认「是」：这里三条路都可逆，而「是」是用户多半想要的那条。
                MessageBoxResult.Yes);

            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                // ⚠️ 校验没过就别往下走：带着一份半截的界面状态进向导，
                // 回来时那句「已保存」会是假的。
                if (!await SaveAsync())
                {
                    return;
                }
            }
            else
            {
                // 说了「放弃」就得真丢：只开向导的话它们还留在界面上，
                // 按【确定】照样写回去 —— 那个词就成了假话。
                LoadSettingsIntoUi();
            }
        }

        var wizard = new WizardWindow(_host) { Owner = this };

        // ⚠️ **向导那几步要开相机**（步 2/3 的取景、步 4 的性能检测），
        // 而相机是独占的 —— 待扫那一路在工作时段一直占着它（预录要求，
        // 规格 §3.1.3）。不让开的话向导会拿到 `device already in use`，
        // 步 4 还会把**能用的组合误判成跑不通**。
        // ⚠️ 只在向导活着这段时间让开：它不是「结束工作」，见协调器那边的说明。
        await _host.Coordinator.PausePrerecordAsync();

        try
        {
            wizard.ShowDialog();
        }
        finally
        {
            // 关窗、抛异常、用户按 X —— 三条路都要接回来，否则相机**一直没人用**，
            // 表现是「扫包裹没反应」，而没人会想到是向导没接回来。
            await _host.Coordinator.ResumePrerecordAsync();
        }

        if (wizard.Completed)
        {
            // 向导改的是同一份设置 ⇒ 这里必须重载，不能留着老值。
            LoadSettingsIntoUi();
            SettingsStatus.Text =
                "配置向导已保存。摄像头与「摄像头识别」那两项要重启才生效，其余立即生效。";
        }
    }

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
    private void OnPrerecordChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();
    private void OnRetentionChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();
    private void OnRecordingSpecChanged(object sender, RoutedEventArgs e) => MarkDirty();

    /// <summary>用途变了 —— 只置脏，**不**弹「选择用途」那个窗口。</summary>
    /// <remarks>
    /// ⚠️ 这一档与别的不一样：它改了要**重启**才生效，而且改动幅度最大
    /// （要不要录像、装不装摄像头）。但它仍然走这个窗口的
    /// 「确定 / 应用 / 取消」—— 在这里弹一个模态窗口问一次，等于把
    /// 【取消】变成骗人的（用户点了取消，用途却已经被问过一遍了）。
    /// 想逐步回答那两个问题的走主窗口那颗【切换用途】。
    /// </remarks>
    private void OnRoleChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();

    /// <summary>归档层变了：保留期那一块的可见性跟着变（与存盘无关的那一半）。</summary>
    private void OnArchiveChanged(object sender, SelectionChangedEventArgs e)
    {
        MarkDirty();
        ShowRetention();

        // 「百度网盘上传」那一页整个挂在归档层选的是哪一档上（批次 5）——
        // 换档之后它是不是还能用，这里就得跟着重算。
        _ = RefreshCloudPageAsync();
    }

    private void MarkDirty()
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        _dirty = true;
        SyncCleanupButtons();
    }

    /// <summary>
    /// 有没保存的改动时，两个清理入口先别动。
    /// </summary>
    /// <remarks>
    /// ⚠️ 清理判的是**已保存**的保留期（<c>_host.Settings.Retention</c>），
    /// 而那几个数就在同一节里、还可能是刚改过没按【应用】的。那种时候点
    /// 【按时间清理…】，清掉的是**旧设置**判出来的那批，而界面上写着的是新设置 ——
    /// 用户会以为刚放宽的那 90 天已经生效了。
    /// 这里禁用是唯一说得清的处置（踩坑 #13：不给一个会做错事的按钮）。
    /// </remarks>
    private void SyncCleanupButtons()
    {
        var ready = !_dirty;

        CleanupByTimeButton.IsEnabled = ready;
        CleanupBySpaceButton.IsEnabled = ready;

        const string Hint = "先把上面的改动【应用】或【确定】之后再清理 —— "
            + "清理按已保存的保留期判，不按界面上还没保存的那份。";

        ToolTipService.SetShowOnDisabled(CleanupByTimeButton, true);
        ToolTipService.SetShowOnDisabled(CleanupBySpaceButton, true);
        CleanupByTimeButton.ToolTip = ready ? null : Hint;
        CleanupBySpaceButton.ToolTip = ready ? null : Hint;
    }

    /// <summary>
    /// 把界面上的设置存下来。
    /// </summary>
    /// <returns>存成功了返回 true。</returns>
    /// <remarks>
    /// ⚠️ <b>算设置那一段一个判断都不该有</b>（T27② 第 3 批）：校验、逐项的
    /// 「认不出来就保持原值」、磁盘表怎么读，全在 <see cref="SettingsForm"/> 里 ——
    /// 那个工程有测试工程，而本工程没有（原先这一段有一百八十行，全是注释在解释
    /// 「为什么不能那样写」）。外壳这一层只剩「把控件上的原值读出来、把结果存下去」。
    /// <para>
    /// 越界的输入**不静默吞掉** —— 说清楚、并且不保存。⚠️ 校验不过时**不关窗**：
    /// 关掉的话用户连自己填错了什么都看不见。
    /// </para>
    /// </remarks>
    private async Task<bool> SaveAsync()
    {
        var input = ReadForm();

        var (next, problem) = SettingsForm.Build(
            input, _host.Settings, DiskRowsOf(_saveDiskRows), DiskRowsOf(_backupDiskRows));

        if (next is null)
        {
            SettingsStatus.Text = problem;

            // ⚠️ 说给用户的那一句**同时留痕**（需求方 2026-10-07 指定）。
            // 校验不过时**窗不关**，用户多半当场改一格再存 —— 于是「他第一次
            // 填的是什么」日志里只剩这一行；而这一行里就有那个越界值。
            //
            // ⚠️ 每条 problem 都是一句自足的话（都以「本次未保存。」收尾，
            // 这是 `SettingsForm.Build` 那头的写法），所以这里**不加前缀** ——
            // 加了就是「设置未保存：…，本次未保存。」。分类「设置」给的就是上下文。
            _host.Log(LogLevel.Warn, "设置", problem);

            return false;
        }

        try
        {
            await _host.SaveSettingsAsync(next);

            // ⚠️ **改完立刻生效，不必重启** —— 这正是「级别可配」的目的：
            // 真机上出问题时不用等一个新版本，改一下当场就能看到 DEBUG 那些行。
            // （开机自启那一项落在注册表里，这一项落在**运行中那个 logger** 上。）
            _host.ApplyLogLevel(next.LogMinLevel);
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"保存失败：{ex.Message}";
            return false;
        }

        _dirty = false;
        SyncCleanupButtons();

        // ⚠️ 开机自启动是唯一一件**落到设置文件之外**的事（写 `HKCU` 的 Run 键），
        // 而它可能被安全软件拦掉 —— 那时设置存下来了、开机却不会启动，
        // 界面上那个开关还显示着「开」。这里回读一次注册表，对不上就说一句。
        // （App 层没有测试工程，这一读是这件事唯一挡得住的东西。）
        var startupNote = Platform.StartupRegistration.IsRegistered() == next.RunAtStartup
            ? string.Empty
            : " ⚠️ 开机自启动的实际状态与设置对不上（可能被安全软件拦了），详见日志。";

        // 逐项说清楚，别笼统写「下次生效」——
        // 笼统的话就有一半是假的，而用户没法知道是哪一半。
        //
        // ⚠️ 编码 / 分辨率**不再要重启**（2026-09-30 修，见 §80），但也**不是**立即生效：
        // 它们要真开一次相机重探，而那只在**下次开始工作**时做。少写这一句，
        // 用户改完直接按【开始工作】之外的方式（比如扫码）开录时会以为改了没生效。
        SettingsStatus.Text =
            "已保存。工作模式立即生效；时长兜底、分段时长与录制声音下次开始工作生效；"
            + "编码与分辨率下次开始工作会重新实测（不用重启）；摄像头与端口要重启；"
            + "开机自启动、关闭窗口时立即生效；自动检查更新下次启动生效。"
            + "百度网盘那两个开关立即生效，应用名与同时上传数下次上传时生效。" + startupNote;

        // 保存之后再刷一遍：实际规格那句依赖刚存下的编码/分辨率，
        // 不刷的话它会一直说上一次的那个组合。
        ShowEffectiveSpec();

        // 网盘那一页也要刷：刚勾上的「启用自动上传」与刚存下的补传范围都在这上面，
        // 而这一页的判断（能不能用、已停用那句）读的是**已保存**的那一份。
        await RefreshCloudPageAsync();
        return true;
    }

    /// <summary>
    /// 把设置页上那些格子的**原值**读出来交给 <see cref="SettingsForm"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这里**只读不算**：一个判断都不做（T27② 第 3 批）。连「下拉传 tag 不传文本」
    /// 这条也在 Core 那边写着 —— 这一层唯一的活是把控件的形状翻成数据。
    /// </remarks>
    private SettingsFormInput ReadForm() => new()
    {
        // 数字格
        SegmentMinutes = SegmentBox.Text,
        PlaybackPort = PortBox.Text,
        DuplicateCheckDays = DuplicateDaysBox.Text,
        IdleMinutes = IdleMinutesBox.Text,
        PrerecordTag = TagOf(PrerecordCombo),
        LogRetainDays = LogRetainDaysBox.Text,
        CloudParallelText = CloudParallelCombo.SelectedItem as string,

        // 下拉的 tag
        StationRoleTag = TagOf(RoleCombo),
        ModeTag = TagOf(ModeCombo),
        CodecTag = TagOf(CodecButtons),
        ResolutionTag = TagOf(ResolutionButtons),
        IdleReminderTag = TagOf(IdleCombo),
        DurationFallbackTag = TagOf(DurationCombo),
        CloseActionTag = TagOf(CloseActionCombo),
        ThemeTag = TagOf(ThemeCombo),
        ArchiveBackendTag = TagOf(ArchiveCombo),
        CloudBackfillScopeTag = TagOf(CloudScopeCombo),
        LogLevelTag = TagOf(LogLevelCombo),

        // 开关
        RunAtStartup = RunAtStartupToggle.IsChecked == true,
        CheckForUpdates = CheckUpdateToggle.IsChecked == true,
        RecordAudio = AudioToggle.IsChecked == true,
        CloudAutoUpload = CloudAutoUploadToggle.IsChecked == true,
        CloudCompareAndBackfill = CloudCompareToggle.IsChecked == true,

        // 别的格子
        CameraDevice = CameraCombo.SelectedItem as string,
        MicrophoneDevice = MicrophoneCombo.SelectedItem as string,
        CloudBackfillFrom = CloudFromDatePicker.SelectedDate is { } from ? new DateTimeOffset(from) : null,
        CloudAppName = CloudAppNameBox.Text,

        // 保留期那四个可编辑下拉：选中项与文本都要（用户可能自己敲了个数）。
        ArchivedOutbound = RetentionInputOf(ArchivedOutboundCombo),
        ArchivedReturn = RetentionInputOf(ArchivedReturnCombo),
        UnarchivedOutbound = RetentionInputOf(UnarchivedOutboundCombo),
        UnarchivedReturn = RetentionInputOf(UnarchivedReturnCombo),
    };

    private static RetentionInput RetentionInputOf(ComboBox combo) => new(combo.SelectedIndex, combo.Text);

    /// <summary>
    /// 磁盘表里的行 → Core 要的那种「全 string」形状。
    /// </summary>
    /// <remarks>
    /// ⚠️ 备份表里那一格显示的是占位符「—」（那一列是**死的**，见
    /// <see cref="BackupReserveText"/>）—— 它是个显示上的东西，在这层折成
    /// 「没填」，别让 Core 去认一个界面文案。
    /// </remarks>
    private static (string Folder, string? ReservedText)[] DiskRowsOf(List<DiskSlotRow> rows) =>
        [.. rows.Select(row =>
        {
            var text = row.ReservedGb.Trim();
            return (row.Folder, text is "" or BackupReserveText ? null : text);
        })];
}
