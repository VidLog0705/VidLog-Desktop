using System.ComponentModel;
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
    // 多磁盘（批次 4，设计图 `_43` 的两张表）
    // ─────────────────────────────────────────────

    /// <summary>设计图 `_43` 那两张表里的一行。</summary>
    /// <remarks>
    /// ⚠️ 路径那一格是**只读文字**（图上就是个纯文本，没有框）：路径只能由
    /// 【添加磁盘】挑进来，不能手打 —— 手打出来的路径没人校验，
    /// 而它一旦进了「保存位置」，录像会写到一个可能根本不存在的目录下。
    /// </remarks>
    private sealed class DiskSlotRow
    {
        private readonly Action _onEdited;
        private string _reserved;

        public DiskSlotRow(
            string folder, string reserved, bool reserveEditable, string? reserveHint,
            ListBox owner, Action onEdited)
        {
            Folder = folder;
            _reserved = reserved;
            ReserveEditable = reserveEditable;
            ReserveHint = reserveHint;
            Owner = owner;
            _onEdited = onEdited;
        }

        public string Folder { get; }

        /// <summary>它在哪张表里 —— 点格子时用它来选中行（见 <see cref="OnDiskRowMouseDown"/>）。</summary>
        public ListBox Owner { get; }

        public bool ReserveEditable { get; }

        public string? ReserveHint { get; }

        /// <summary>「预留空间」那一格里的文本。</summary>
        /// <remarks>
        /// ⚠️ <b>写回同一个值不算改动</b>：这个绑定是双向的，而控件生成时
        /// 会把刚读到的值再写回来一遍（窗口刚打开、从配置向导回来重载时都会发生）。
        /// 不挡的话一开窗就是「有改动还没保存」，关窗还要再问一次 ——
        /// 而用户什么都没碰过。
        /// </remarks>
        public string ReservedGb
        {
            get => _reserved;
            set
            {
                if (string.Equals(_reserved, value, StringComparison.Ordinal))
                {
                    return;
                }

                _reserved = value;
                _onEdited();
            }
        }
    }

    /// <summary>把两张表按设置填出来。</summary>
    /// <remarks>
    /// ⚠️ 备份位置那一列**读的是 `BackupDisks`，没有再回落到老的
    /// `ArchiveDirectory`** —— 那件事由 `AppSettings.ArchiveDirectories` 一处做。
    /// 这里也做一遍的话，用户把最后一行删掉之后它会**原地复活**。
    /// </remarks>
    private void LoadDisks()
    {
        _saveDiskRows.Clear();
        foreach (var slot in _host.Settings.SaveDisks)
        {
            _saveDiskRows.Add(new DiskSlotRow(
                slot.Path,
                DiskSpace.EffectiveReservedGb(slot, Volumes.Measure(slot.Path)).ToString(),
                reserveEditable: true, reserveHint: null,
                SaveDiskList, MarkDirty));
        }

        var backupSlots = _host.Settings.BackupDisks.Count > 0
            ? _host.Settings.BackupDisks
            : [.. _host.Settings.ArchiveDirectories.Select(path => new DiskSlot(path))];

        _backupDiskRows.Clear();
        foreach (var slot in backupSlots)
        {
            _backupDiskRows.Add(new DiskSlotRow(
                slot.Path, BackupReserveText, reserveEditable: false,
                reserveHint: BackupReserveHint, BackupDiskList, MarkDirty));
        }

        BindDisks();
    }

    /// <summary>备份位置那一格显示的占位符。</summary>
    private const string BackupReserveText = "—";

    /// <remarks>
    /// ⚠️ 这一列在备份表里**是死的**（踩坑 #13：绝不渲染一个没有任何作用的开关）：
    /// 备份不做「预留空间」—— 一个位置写不进去就自动试下一个，
    /// 预先留出空间在这里没有意义（那件事在录像保存位置上才有意义）。
    /// 但设计图上那一列在，所以留着、禁用、并写明原因。
    /// </remarks>
    private const string BackupReserveHint =
        "备份位置不做预留：一个位置写不进去就自动试下一个，预先留出空间没有意义。";

    /// <summary>两张表重新铺一遍。</summary>
    /// <remarks>
    /// ⚠️ 先设 <see langword="null"/> 再设回同一个列表：不这样，
    /// 上移/下移之后控件**不会重排**（`ItemsSource` 引用的还是同一个对象）。
    /// </remarks>
    private void BindDisks()
    {
        SaveDiskList.ItemsSource = null;
        SaveDiskList.ItemsSource = _saveDiskRows;
        BackupDiskList.ItemsSource = null;
        BackupDiskList.ItemsSource = _backupDiskRows;

        SaveDiskEmptyNote.Visibility =
            _saveDiskRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BackupDiskEmptyNote.Visibility =
            _backupDiskRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SyncDiskButtons(SaveDiskList, SaveRemoveButton, SaveUpButton, SaveDownButton);
        SyncDiskButtons(BackupDiskList, BackupRemoveButton, BackupUpButton, BackupDownButton);
    }

    /// <summary>「移除选中 / 上移 / 下移」能不能点 —— 只看选中在哪。</summary>
    private static void SyncDiskButtons(ListBox list, Button remove, Button up, Button down)
    {
        var index = list.SelectedIndex;

        remove.IsEnabled = index >= 0;
        up.IsEnabled = index > 0;
        down.IsEnabled = index >= 0 && index < list.Items.Count - 1;
    }

    private void OnDiskSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SaveDiskList))
        {
            SyncDiskButtons(SaveDiskList, SaveRemoveButton, SaveUpButton, SaveDownButton);
        }
        else
        {
            SyncDiskButtons(BackupDiskList, BackupRemoveButton, BackupUpButton, BackupDownButton);
        }
    }

    private void OnDiskEdited(object sender, TextChangedEventArgs e) => MarkDirty();

    /// <summary>点在这一行的哪个格子上都算选中这一行。</summary>
    /// <remarks>
    /// ⚠️ 少了这一句，**点「预留空间」那个输入框不会选中行** ——
    /// WPF 的 <c>TextBox</c> 会把鼠标事件吃掉，<c>ListBoxItem</c> 收不到。
    /// 于是【移除选中】【上移】【下移】作用在**上一次**选中的那一行上，
    /// 而那是会挪错甚至删错盘的。
    /// </remarks>
    private void OnDiskRowMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DiskSlotRow row }
            && !ReferenceEquals(row.Owner.SelectedItem, row))
        {
            row.Owner.SelectedItem = row;
        }
    }

    /// <summary>这一格的按钮属于哪张表（XAML 上的 <c>Tag</c>）。</summary>
    private static bool IsSaveTable(object sender) =>
        (sender as FrameworkElement)?.Tag as string == "Save";

    private List<DiskSlotRow> RowsOf(bool save) => save ? _saveDiskRows : _backupDiskRows;

    private ListBox ListOf(bool save) => save ? SaveDiskList : BackupDiskList;

    /// <summary>【添加磁盘】—— 挑一个文件夹进来。</summary>
    /// <remarks>
    /// ⚠️ 用系统的文件夹选择框（<c>Microsoft.Win32.OpenFolderDialog</c>，
    /// .NET 8 起 WPF 自带）而**不是**让用户手打路径：手打的路径没人校验，
    /// 打错一个字母就是「录了一整天，录像写进一个不存在的目录」。
    /// </remarks>
    private void OnAddDisk(object sender, RoutedEventArgs e)
    {
        var save = IsSaveTable(sender);
        var rows = RowsOf(save);
        var list = ListOf(save);

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = save ? "选一个用来放录像的文件夹" : "选一个用来备份录像的文件夹",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var path = dialog.FolderName;

        if (rows.Any(row => string.Equals(row.Folder, path, StringComparison.OrdinalIgnoreCase)))
        {
            SettingsStatus.Text = $"{path} 已经在列表里了，没有重复添加。";
            return;
        }

        rows.Add(save
            ? new DiskSlotRow(path, DefaultReserveText(path), true, null, list, MarkDirty)
            : new DiskSlotRow(path, BackupReserveText, false, BackupReserveHint, list, MarkDirty));

        BindDisks();
        list.SelectedIndex = rows.Count - 1;
        MarkDirty();
    }

    /// <summary>这一格新加进来时预填的预留空间（与挑盘时判的是同一个数）。</summary>
    private static string DefaultReserveText(string path)
    {
        var space = Volumes.Measure(path);

        return DiskSpace
            .DefaultReservedGb(space?.TotalBytes, DiskSpace.IsSystemDrive(path))
            .ToString();
    }

    private void OnRemoveDisk(object sender, RoutedEventArgs e)
    {
        var save = IsSaveTable(sender);
        var rows = RowsOf(save);
        var list = ListOf(save);
        var index = list.SelectedIndex;

        if (index < 0 || index >= rows.Count)
        {
            return;
        }

        rows.RemoveAt(index);
        BindDisks();
        list.SelectedIndex = Math.Min(index, rows.Count - 1);
        MarkDirty();
    }

    private void OnMoveDiskUp(object sender, RoutedEventArgs e) => MoveDisk(sender, -1);

    private void OnMoveDiskDown(object sender, RoutedEventArgs e) => MoveDisk(sender, +1);

    /// <summary>上移 / 下移。</summary>
    /// <remarks>
    /// ⚠️ 顺序**是配置的一部分**，不是好看：写的时候按列表顺序挑第一个还有余量的
    /// （见 <c>StorageLocations.PickActive</c>），所以把 D 盘挪到 C 盘前面
    /// 就是「先写 D 盘」。
    /// </remarks>
    private void MoveDisk(object sender, int delta)
    {
        var save = IsSaveTable(sender);
        var rows = RowsOf(save);
        var list = ListOf(save);

        var from = list.SelectedIndex;
        var to = from + delta;

        if (from < 0 || to < 0 || to >= rows.Count)
        {
            return;
        }

        (rows[from], rows[to]) = (rows[to], rows[from]);
        BindDisks();
        list.SelectedIndex = to;
        MarkDirty();
    }

    /// <summary>把表里的行读成设置里的 <see cref="DiskSlot"/> 串。</summary>
    /// <returns>读不出来时返回 <see langword="false"/>，原因写在 <paramref name="error"/>。</returns>
    /// <remarks>
    /// ⚠️ 预留空间那一格**空着是合法的**（= 按默认值现算，`ReservedGb` 给
    /// <see langword="null"/>），但填了一个读不懂的数就不合法 —— 那种时候
    /// **不保存**并说清楚，而不是静默换成默认值（用户会以为他填的生效了）。
    /// </remarks>
    private static bool TryReadDisks(
        List<DiskSlotRow> rows, string label, out List<DiskSlot> slots, out string error)
    {
        slots = [];
        error = string.Empty;

        if (rows.Count > 32)
        {
            error = $"{label}最多 32 个，本次未保存。";
            return false;
        }

        foreach (var row in rows)
        {
            var text = row.ReservedGb.Trim();

            if (text is "" or BackupReserveText)
            {
                slots.Add(new DiskSlot(row.Folder));
                continue;
            }

            if (!int.TryParse(text, out var reserved) || reserved is < 0 or > 1_000_000)
            {
                error = $"{label}里「{row.Folder}」的预留空间要是 0~1000000 之间的整数"
                    + "（留空 = 用默认值），本次未保存。";
                return false;
            }

            slots.Add(new DiskSlot(row.Folder, reserved));
        }

        return true;
    }

    // ─────────────────────────────────────────────
    // 容量条（设计图 `_43` 顶上那条）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 把「已用 / 共」画出来。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>分子是录像文件的实际字节数，分母是这些位置允许我们写的上限</b>
    /// （总容量扣掉各自的预留空间）。分母这个口径与「什么时候换下一块盘」
    /// 是同一个（<see cref="DiskSpace.EffectiveReservedGb"/>）——
    /// 两处不一样的话，会出现「条子上还有 12%，却已经换了盘」那种自相矛盾。
    /// </para>
    /// <para>
    /// ⚠️ 遍历录像目录是**秒级**的（几万个文件），放到线程池上跑。
    /// </para>
    /// <para>
    /// ⚠️ 读不到的盘**必须说出来**：不说的话那个分母是静默偏小的，
    /// 而条子看起来会更满 —— 用户会据此去删东西。
    /// </para>
    /// </remarks>
    private async Task RefreshCapacityAsync()
    {
        try
        {
            var storage = _host.Services.Storage;

            var places = new List<(VolumeSpace? Space, int ReservedGb)>();

            if (storage.Slots.Count > 0)
            {
                places.AddRange(storage.Describe().Select(one => (one.Space, one.ReservedGb)));
            }
            else
            {
                var root = storage.FallbackRoot;
                var space = Volumes.Measure(root);
                places.Add((space, DiskSpace.EffectiveReservedGb(new DiskSlot(root), space)));
            }

            var roots = storage.ReadRoots;

            var used = await Task.Run(() =>
                roots
                    .Select(one => LibraryFootprintProbe.Measure(one))
                    .Aggregate(new LibraryFootprint(0, 0, 0), (sum, one) => new LibraryFootprint(
                        sum.FileCount + one.FileCount,
                        sum.TotalBytes + one.TotalBytes,
                        sum.UnreadableCount + one.UnreadableCount)));

            long capacity = 0;
            var unreadable = 0;

            foreach (var (space, reserved) in places)
            {
                if (space is null)
                {
                    unreadable++;
                    continue;
                }

                capacity += Math.Max(0, space.TotalBytes - (long)Math.Max(0, reserved) * DiskSpace.Gigabyte);
            }

            CapacityText.Text = $"{Display.Bytes(used.TotalBytes)} / {Display.Bytes(capacity)}";

            var ratio = capacity > 0 ? Math.Clamp(used.TotalBytes / (double)capacity, 0, 1) : 0;

            // ⚠️ 宽度要等布局给出来才知道（条子是拉伸的），所以先把比例记下、
            // 在 `SizeChanged` 里换算 —— 直接写 Width 的话窗口一缩放就错位。
            _capacityRatio = ratio;
            UpdateCapacityFillWidth();

            CapacityNote.Text = "上限 = 保存位置的总容量扣掉各自的预留空间；"
                + "统计的是本机录像文件的字节数，盘上别的文件不算在内。"
                + (unreadable == 0 ? string.Empty : $"⚠️ 有 {unreadable} 处容量读不到，实际上限只会更小。");
        }
        catch (Exception ex)
        {
            // 算不出来不该让整页出错（I4 的同一条精神），但也不许装作是 0。
            CapacityText.Text = "读不出来";
            CapacityNote.Text = $"⚠️ 容量没算完：{ex.Message}";
        }
    }

    private double _capacityRatio;

    private void UpdateCapacityFillWidth() =>
        CapacityFill.Width = Math.Max(0, CapacityTrack.ActualWidth * _capacityRatio);

    private void OnCapacityTrackSized(object sender, System.Windows.SizeChangedEventArgs e) =>
        UpdateCapacityFillWidth();

    // ─────────────────────────────────────────────
    // 录像清理（设计图 `_43` 的两颗按钮）
    // ─────────────────────────────────────────────

    /// <summary>【按时间清理…】—— 按四档保留期算一次，给用户看过才删。</summary>
    private async void OnCleanupByTime(object sender, RoutedEventArgs e)
    {
        CleanupByTimeButton.IsEnabled = false;
        CleanupNote.Text = "正在算该清哪些…";

        try
        {
            var plan = await _host.Services.Cleanup.PreviewAsync(
                _host.Settings.Retention, DateTimeOffset.Now);

            if (plan.Candidates.Count == 0)
            {
                // ⚠️ 与启动时那次**不一样**：这里是用户主动点的，
                // 所以「没有要清的」必须说出来 —— 什么都没发生会让人以为按钮坏了。
                CleanupNote.Text = "按现在的保留期设置，没有到期该清的。";
                return;
            }

            var outcome = await CleanupPrompt.AskAndRunAsync(
                this, _host, plan,
                $"保留期到了的录像有 {plan.Candidates.Count} 条，"
                + $"约 {plan.TotalBytes / 1024 / 1024} MB。");

            CleanupNote.Text = outcome.Message;
        }
        catch (Exception ex)
        {
            CleanupNote.Text = $"清理没能进行：{ex.Message}";
        }
        finally
        {
            SyncCleanupButtons();
        }
    }

    /// <summary>【按空间释放…】—— 把这批录像清到活动那块盘至少还剩它的预留空间。</summary>
    /// <remarks>
    /// ⚠️ <b>它可能释放不出足够空间</b>，而那是**对**的：未归档 / 已锁定 /
    /// 24 小时内的三条豁免照常生效（见 <c>CleanupService.PreviewBySpaceAsync</c>），
    /// 所以「盘满了也清不动」是这个产品的既定取舍 —— 宁可盘满，也不删唯一副本。
    /// 那就得把「没清够」说出来，否则用户会以为程序没干活。
    /// </remarks>
    private async void OnCleanupBySpace(object sender, RoutedEventArgs e)
    {
        CleanupBySpaceButton.IsEnabled = false;
        CleanupNote.Text = "正在算该清哪些…";

        try
        {
            var storage = _host.Services.Storage;
            var root = storage.ActiveRoot;
            var space = Volumes.Measure(root);

            if (space is null)
            {
                CleanupNote.Text = $"读不到 {root} 还剩多少空间，按空间释放算不出来。";
                return;
            }

            // 预留空间取**用户配的那个**（配过才走 Describe），没配过就按默认现算。
            var reserved = storage.Describe()
                .FirstOrDefault(one => string.Equals(
                    one.Slot.Path, root, StringComparison.OrdinalIgnoreCase))
                ?.ReservedGb
                ?? DiskSpace.DefaultReservedGb(space.TotalBytes, DiskSpace.IsSystemDrive(root));

            var plan = await _host.Services.Cleanup.PreviewBySpaceAsync(
                (long)reserved * DiskSpace.Gigabyte, space.FreeBytes, DateTimeOffset.Now);

            if (plan is null)
            {
                // 只可能发生在「归档层是本机磁盘」上 —— 那种时候这一整块是藏着的，
                // 所以走到这里说明设置在这一瞬间被改了。照实说。
                CleanupNote.Text =
                    "归档层是本机磁盘 —— 盘上这份是唯一副本，按空间释放不提供。";
                return;
            }

            if (plan.Candidates.Count == 0)
            {
                CleanupNote.Text =
                    $"{root} 还剩 {Display.Bytes(space.FreeBytes)}（预留线 {reserved} GB），没有要清的。";
                return;
            }

            var outcome = await CleanupPrompt.AskAndRunAsync(
                this, _host, plan,
                $"{root} 还剩 {Display.Bytes(space.FreeBytes)}，预留线是 {reserved} GB。\n"
                + $"拟清 {plan.Candidates.Count} 条，约 {plan.TotalBytes / 1024 / 1024} MB。");

            CleanupNote.Text = outcome.Message
                + (outcome.Ran
                    ? $"豁免没动的那 {plan.Exempted.Count} 条里，未归档的永不自动删（那是唯一副本）—— 所以可能没腾够。"
                    : string.Empty);
        }
        catch (Exception ex)
        {
            CleanupNote.Text = $"清理没能进行：{ex.Message}";
        }
        finally
        {
            SyncCleanupButtons();
        }
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
        // ⚠️ 当前用的是**网络摄像头**时，这个下拉要如实说，且**禁用** ——
        // 列表里只列本机设备，照常填的话会显示成「当前用的是这台本机摄像头」，
        // 而实际在录的是那路 RTSP。那是界面上的一句假话。
        // 地址是用户在**配置向导**第 2 步填的，所以提示指向那里
        // （本仓惯例：配不了的东西禁用 + 写明原因，踩坑 #13）。
        if (_host.Settings.Camera.IsNetwork)
        {
            CameraCombo.IsEnabled = false;
            CameraHint.Text = $"当前用的是网络摄像头（{_host.Settings.Camera.Identity}），"
                + "在【配置向导 → 选择摄像头】里改。";
            return;
        }

        if (_host.Services.FfmpegPath is null)
        {
            CameraHint.Text = "没有 FFmpeg，无法采集";
            return;
        }

        // ⚠️ logger 要传：那些构造点的 logger 是**可选**参数（默认 `NullLogger`），
        // 不传就静默不落盘、而编译器不会说 —— 2026-09-29 逐个核对调用点才发现这里漏了。
        var devices = await DshowDevices.ListVideoAsync(_host.Services.FfmpegPath, _host.Logger);

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
    // 录制声音（规格 §3.1.8）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 把开关拨到已保存的那一档。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这里**不自己刷下面那两行** —— 那是 <see cref="SyncAudioAsync"/> 一处的活
    /// （写两遍就会漏，见那里的说明）。
    /// 赋值时借用 <c>_suppressSettingsEvents</c> 把事件挡回去：不挡的话就会
    /// 「事件里刷一遍、这里再刷一遍」，白枚举两遍麦克风。
    /// </remarks>
    private async void LoadAudio()
    {
        _suppressSettingsEvents = true;
        AudioToggle.IsChecked = _host.Settings.RecordAudio;
        _suppressSettingsEvents = false;

        await SyncAudioAsync();
    }

    private void OnAudioChanged(object sender, RoutedEventArgs e)
    {
        MarkDirty();

        if (_suppressSettingsEvents)
        {
            return;
        }

        // 与 OnArchiveChanged 同一路数：顺手刷新与保存无关的那一半。
        _ = SyncAudioAsync();
    }

    /// <summary>
    /// 把「开关的状态」翻译成下面那两行的样子。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>只此一处</b>。写在两遍（开一次、关一次各写一遍）就会漏 ——
    /// 实测过一次：开关是关的、旁边的字还写着「开」，界面自相矛盾，
    /// 而 App 层没有测试工程，这种东西没有任何测试挡得住。
    /// </para>
    /// <para>
    /// ⚠️ 关着的时候**不去枚举**：那是白起一次 ffmpeg，而结果只为了填一个被禁用的下拉。
    /// </para>
    /// </remarks>
    private async Task SyncAudioAsync()
    {
        // ⚠️ 没得挑的时候**把那个下拉收起来**（2026-10-02 实测：本机没有麦克风时，
        // 它留着一个空的灰色长条，把那一行挤成「[开] [空框] 没有找到麦克风…」——
        // 用户看着像界面坏了）。它禁用是「不能改」，而**空**是「没东西可改」，
        // 后者连摆都不该摆。三处「没法挑」的分支都走这一个函数，免得漏一处。
        void ShowCombo(bool visible) =>
            MicrophoneCombo.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (AudioToggle.IsChecked != true)
        {
            AudioStateText.Text = "关";
            MicrophoneCombo.IsEnabled = false;
            ShowCombo(false);
            MicrophoneHint.Text = "";
            return;
        }

        AudioStateText.Text = "开";
        MicrophoneCombo.Items.Clear();
        MicrophoneCombo.IsEnabled = false;
        ShowCombo(false);

        if (_host.Services.FfmpegPath is null)
        {
            MicrophoneHint.Text = "没有 FFmpeg，无法采集";
            return;
        }

        var devices = await DshowDevices.ListAudioAsync(_host.Services.FfmpegPath, _host.Logger);

        // ⚠️ 枚举是异步的，而用户完全可能在这期间把开关拨掉 —— 那时这批结果已经作废，
        // 写回去会把刚拨出来的那一档盖掉。开窗时那次枚举要好几秒
        // （ffmpeg 枚举 dshow 失败也要走完），这个窗口是真实存在的。
        if (AudioToggle.IsChecked != true)
        {
            return;
        }

        if (devices.Count == 0)
        {
            // 「没有麦克风」是正常的运行环境，不是错误 —— 但**必须说出来**：
            // 开关开着而一个麦克风都没有时，用户会以为录的是有声的。
            MicrophoneHint.Text = "没有找到麦克风，录像不会有声音";
            return;
        }

        foreach (var device in devices)
        {
            MicrophoneCombo.Items.Add(device);
        }

        var remembered = _host.Settings.MicrophoneDevice;
        MicrophoneCombo.SelectedIndex =
            remembered is not null && devices.Contains(remembered) ? devices.ToList().IndexOf(remembered) : 0;

        MicrophoneCombo.IsEnabled = true;
        ShowCombo(true);
        MicrophoneHint.Text = "";
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

        // 两张磁盘表（批次 4，设计图 `_43`）。分开判，好让每一处各自说自己那一句。
        if (!TryReadDisks(_saveDiskRows, "录像保存位置", out var saveDisks, out var saveDiskError))
        {
            SettingsStatus.Text = saveDiskError;
            return false;
        }

        if (!TryReadDisks(_backupDiskRows, "录像备份位置", out var backupDisks, out var backupDiskError))
        {
            SettingsStatus.Text = backupDiskError;
            return false;
        }

        // 自定义分钟数：只在选了「自定义」时才管它，否则保持原值
        // （用户先填了 7 分钟又改回 3 分钟，那 7 不该丢 —— 下次切回自定义还要用）。
        var idleMinutes = int.TryParse(IdleMinutesBox.Text, out var parsedMinutes)
            ? Math.Clamp(parsedMinutes, WorkModeOptions.MinIdleMinutes, WorkModeOptions.MaxIdleMinutes)
            : _host.Settings.IdleReminderMinutes;

        var next = _host.Settings with
        {
            // 电脑用途（批次 4）。认不出来就保持原值 —— 这个下拉只有四项，
            // 认不出来说明界面坏了，而「静默换成默认用途」会让一台备份主机
            // 下次启动突然开始抢摄像头。
            StationRole = Enum.TryParse<StationRole>(TagOf(RoleCombo), out var stationRole)
                ? stationRole : _host.Settings.StationRole,
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
            // 扫码预录缓冲（批次 C，规格 §3.1.3）。四个档位，正常取不到别的值；
            // 越界就保持原值而不是夹一下 —— 与「同时上传数」同一个理由，
            // 猜错一档要么白丢几秒画面、要么白占一份磁盘，不如不动。
            PrerecordSeconds = int.TryParse(TagOf(PrerecordCombo), out var prerecord)
                && prerecord is >= 0 and <= 30
                    ? prerecord : _host.Settings.PrerecordSeconds,
            DuplicateCheckDays = duplicateDays,
            PlaybackPort = port,

            // ── 外观与启动（批次 9，设计图 `_49`）──
            // ⚠️ 「界面语言」与「外观主题」**刻意不在这里读**：它们各自只有一档能选，
            // 落进设置文件就是一个永远为真的假开关（踩坑 #13）。
            RunAtStartup = RunAtStartupToggle.IsChecked == true,
            CloseWindowAction = Enum.TryParse<CloseWindowAction>(TagOf(CloseActionCombo), out var closeAction)
                ? closeAction : _host.Settings.CloseWindowAction,
            CheckForUpdates = CheckUpdateToggle.IsChecked == true,
            // ── 日志（2026-10-01 需求方要「级别可配」）──
            LogMinLevel = SelectedLogLevel(),
            LogRetainDays = ParsedLogRetainDays(),
            // ⚠️ 用着网络摄像头时那个下拉是禁用且空的，直接取 SelectedItem
            // 会把记着的本机设备名抹成 null —— 用户哪天切回本机设备就得重选一遍。
            CameraDevice = CameraCombo.SelectedItem as string ?? _host.Settings.CameraDevice,
            // ⚠️ 关掉时麦克风那一栏**仍然记着**选的是哪个（与归档目录同一个道理）：
            // 用户来回拨开关时不必重选一遍。关着的时候那个下拉根本没被填过，
            // 直接取 SelectedItem 会把记着的名字抹成 null。
            RecordAudio = AudioToggle.IsChecked == true,
            MicrophoneDevice = MicrophoneCombo.SelectedItem as string ?? _host.Settings.MicrophoneDevice,
            ArchiveBackend = Enum.TryParse<ArchiveBackendKind>(TagOf(ArchiveCombo), out var backend)
                ? backend : _host.Settings.ArchiveBackend,
            // 目录型那两档的根，现在是一张表（批次 4）。别的档位下那张表是藏着的，
            // 但行仍然记着 —— 用户在 NAS 与挂载盘之间来回切时不必重填一遍。
            //
            // ⚠️ 老的单个 `ArchiveDirectory` 在这里**清掉**：留着它的话，
            // `AppSettings.ArchiveDirectories` 会在表被清空时回落到它 ——
            // 于是用户删掉的最后一行会**原地复活**。值已经折进表里了（读的时候折的）。
            ArchiveDirectory = null,
            BackupDisks = backupDisks,
            SaveDisks = saveDisks,
            Retention = new RetentionSettings(
                RetentionOf(ArchivedOutboundCombo), RetentionOf(ArchivedReturnCombo),
                RetentionOf(UnarchivedOutboundCombo), RetentionOf(UnarchivedReturnCombo)),

            // ── 百度网盘（批次 5，设计图 `_45` / `_46`）──
            // ⚠️ `AutoUploadSince` **刻意不在这里**：盖章的是
            // `AppHost.SaveSettingsAsync`（拨开那一刻），因为这组设置还有别的入口，
            // 而这条时间戳是「仅此开关开启后**新开始录制**的视频会上传」唯一的判据 ——
            // 少盖一次，打开开关就会把整库历史录像一次全传上去。
            Cloud = _host.Settings.Cloud with
            {
                AutoUpload = CloudAutoUploadToggle.IsChecked == true,
                CompareAndBackfill = CloudCompareToggle.IsChecked == true,
                BackfillScope = Enum.TryParse<BackfillScope>(TagOf(CloudScopeCombo), out var scope)
                    ? scope : _host.Settings.Cloud.BackfillScope,
                // 换回「全部」时**不清**：用户来回切时不必重选一遍（与磁盘表同一个道理）。
                BackfillFrom = CloudFromDatePicker.SelectedDate is { } from
                    ? new DateTimeOffset(from) : _host.Settings.Cloud.BackfillFrom,
                // 界面上是个 1~8 的下拉，正常取不到别的值；越界就保持原值而不是夹一下 ——
                // 静默把「同时上传数」改成 8 的后果不是慢一点，是整库被网盘风控限流。
                ParallelUploads = int.TryParse(CloudParallelCombo.SelectedItem as string, out var parallel)
                    && parallel is >= 1 and <= 8
                        ? parallel : _host.Settings.Cloud.ParallelUploads,
                AppName = string.IsNullOrWhiteSpace(CloudAppNameBox.Text)
                    ? _host.Settings.Cloud.AppName : CloudAppNameBox.Text.Trim(),
            },
        };

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

    // ─────────────────────────────────────────────
    // 录制规格（规格 §3.1.7）
    // ─────────────────────────────────────────────

    private RadioButton[] CodecButtons => [CodecH264, CodecH265];

    private RadioButton[] ResolutionButtons => [Res4K, Res1080, Res720];

    /// <summary>
    /// 显示**实际会用**的录制规格。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规格 §3.1.7：「**回落必须可见**……**不得静默回落**」。
    /// 取值来自**最近一次**真开相机的探测（<c>AppHost.EffectiveSpec</c>）。
    /// 与用户选的不一样时把原因也说出来 —— 只说「实际是 H.264」而不说为什么，
    /// 用户会以为自己选错了。
    /// </para>
    /// <para>
    /// ⚠️ <b>三档情形，别用一句话糊过去</b>（2026-09-30 实测撞到过下面第二种）：
    /// </para>
    /// <list type="number">
    /// <item>用户选的那一对**就是**上次探过的那一对 ⇒ 说结论。</item>
    /// <item>用户刚改过、那一对**还没探过** ⇒ <c>EffectiveSpec</c> 说的是
    /// **上一次**的结论。拿它去跟新选的比会印出一句「这台电脑跑不通」的**假话**
    /// （那一档根本还没测），所以要说清「下次开始工作时才实测」。</item>
    /// <item>探过且回落了 ⇒ 说结论 + 原因（§3.1.7 的「回落必须可见」）。</item>
    /// </list>
    /// <para>
    /// ⚠️ 方向**要一起比**：它也在 spec 里，不带上它的话，一个方向设成「转 180°」的
    /// 机器每次开这一页都会看到那句回落警告（两个 spec 的 `Rotation` 不一样）。
    /// </para>
    /// <para>
    /// ⚠️ 这一句在**主窗口上看不见**（设计图的录制台上没有这个位置）——
    /// 它挪进了设置里。规格要的是「可见」，不是「必须印在首页」，
    /// 但它确实比以前难看见了，这一笔记在 `docs/实现决策.md`。
    /// </para>
    /// </remarks>
    private void ShowEffectiveSpec()
    {
        var effective = _host.EffectiveSpec;
        var wanted = new RecordingSpec(
            _host.Settings.Codec, _host.Settings.Resolution, _host.Settings.Rotation);
        var probed = _host.ProbedSpec;

        EffectiveSpecText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");

        // 情形 2：用户刚改的这一对还没实测过。**先判它**，否则会拿旧结论说新组合的不是。
        if (wanted.Codec != probed.Codec || wanted.Resolution != probed.Resolution)
        {
            EffectiveSpecText.Text =
                $"「{wanted.Label}」还没实测过 —— 下次开始工作时会真开一次相机验一遍，"
                + $"验不过会自动回落并当场告诉你。"
                + $"当前按 {effective.Label} 录制（那是上一次实测的结论）。";
            return;
        }

        if (effective == wanted)
        {
            EffectiveSpecText.Text = $"这台电脑按 {effective.Label} 录制。";
            return;
        }

        // 情形 3：探过，回落了。
        EffectiveSpecText.Text =
            $"⚠️ 你选的是 {wanted.Label}，这台电脑实际按 {effective.Label} 录制。"
            + (string.IsNullOrWhiteSpace(_host.SpecFallbackReason)
                ? string.Empty
                : $"原因：{_host.SpecFallbackReason}");
        EffectiveSpecText.Foreground = (System.Windows.Media.Brush)FindResource("Warning");
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

        // ⚠️ 试用**必须先判**（见 `LicenseStatus` 的注释：试用中 `Activated` 也是 true）。
        //
        // ⚠️ 「购买入口」是**一句话，不是一个按钮** —— 到今天为止没有真实的购买渠道
        // （没有下单页、没有联系方式），摆一颗按钮就是假开关（§63 / 踩坑 #13）。
        // 等到真有渠道了，把这里换成按钮，别在那之前先摆上。
        LicenseNote.Text = status switch
        {
            { IsTrial: true, Activated: true } =>
                $"✅ 试用中：还剩 {status.TrialRemainingText}（试用期 7 天 / 4 机位）。"
                + "试用到期只挡住新的录制与接入 —— 已有的录像照常可以检索、回放、导出。"
                + "要长期用得换一个长期激活码：把上面的机器码给提供方。"
                + degraded,

            { IsTrial: true } =>
                $"⛔ {status.FailureReason}"
                + "试用期里才能录新的、接手机、看手机的实时画面；"
                + "已有的录像照常可以检索、回放、导出。"
                + "要接着用得激活 —— 把上面的机器码给提供方。"
                + degraded,

            { Activated: true } => $"✅ 已激活：允许接入 {status.Slots} 台手机端。{degraded}",

            _ => $"⛔ {status.FailureReason}允许接入 0 台手机端。{degraded}",
        };

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

        // ⚠️ 顶上那两颗清理按钮**跟着一起收**（规格 §3.5.1：那时盘上这份是唯一副本，
        // 清理入口不该摆出来）。收起的原因由下面那句话讲。
        CleanupRow.Visibility =
            target.AllowsCleanup ? Visibility.Visible : Visibility.Collapsed;

        RetentionAbsentNote.Visibility =
            target.AllowsCleanup ? Visibility.Collapsed : Visibility.Visible;
        RetentionAbsentNote.Text =
            "归档层是本机磁盘 —— 盘上这份就是唯一副本，所以不提供保留期设置与清理入口。"
            + "改成 NAS、挂载网络驱动器或百度网盘之后，这里才会出现。";

        // 目录型（NAS / 挂载盘）才要那张备份位置表。⚠️ 这两档**共用一份实现**
        // （规格 §3.4.6），所以界面上也是同一张表。
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
    /// <para>
    /// 认不出的 Tag 一律回落到本机磁盘 —— 与
    /// <see cref="ArchiveTarget.FromConfig"/> 同一个方向（朝**少删**的那头落）。
    /// </para>
    /// <para>
    /// ⚠️ 目录取**表里第一行**：`ArchiveTarget` 只有一个路径字段，而多出来的那几行
    /// 是给归档层用的（<c>AppSettings.ArchiveDirectories</c> 一次给全）。
    /// 这一处在界面上只用来判「能不能跨网 / 要不要摆清理入口」，
    /// 而那两件事只看第一行就够 —— 真正的多位置发布在 Core 里。
    /// </para>
    /// </remarks>
    private ArchiveTarget SelectedArchiveTarget() =>
        TagOf(ArchiveCombo) switch
        {
            "Nas" => new ArchiveTarget(ArchiveBackendKind.Nas, FirstBackupPath),
            "MountedDrive" => new ArchiveTarget(ArchiveBackendKind.MountedDrive, FirstBackupPath),
            "Cloud" => new ArchiveTarget(ArchiveBackendKind.Cloud),
            _ => ArchiveTarget.Default,
        };

    /// <summary>备份位置表里的第一条路径（没有就给 <see langword="null"/>）。</summary>
    private string? FirstBackupPath => _backupDiskRows.FirstOrDefault()?.Folder;

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
    // 外观与启动（批次 9，设计图 `_49`）
    // ─────────────────────────────────────────────

    /// <summary>填「界面语言」与「外观主题」两个下拉。</summary>
    /// <remarks>
    /// ⚠️ <b>这两行现在是**可点**的</b>（需求方 2026-10-01 裁决：「只留窗口，
    /// 用户如点开，显示正在开发中」）。先前是「禁用 + 悬停写明原因」，
    /// 而悬停提示**触屏看不见、不悬停的人也看不见**。
    /// <para>
    /// ⚠️ 选了还没做的那一档：**说一句实话，然后退回**。
    /// 它只有一个真值，而选中的那一档**不会落盘** —— 留在那里就是骗人。
    /// </para>
    /// </remarks>
    private void LoadPreferences()
    {
        FillPreferenceCombo(LanguageCombo, AppPreferences.Languages);
        FillPreferenceCombo(ThemeCombo, AppPreferences.Themes);
    }

    private void FillPreferenceCombo(ComboBox combo, IReadOnlyList<PreferenceOption> options)
    {
        combo.Items.Clear();

        // 真做了的那一档（表里只有一档）。
        var real = 0;

        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];

            combo.Items.Add(new ComboBoxItem
            {
                Content = option.Label,
                Tag = option.Label,
                // ⚠️ **不禁用** —— 每一档都点得动（需求方 2026-10-01 的裁决）。
                // 悬停那句话说给愿意悬停的人听；点下去那句话**谁都看得见**。
                ToolTip = option.Hint,
            });

            if (option.Implemented)
            {
                real = i;
            }
        }

        // 选中真做了的那一档（照实显示现状）。
        combo.SelectedIndex = real;

        // ⚠️ 挂在**这里**而不是 XAML 上：退回用的是 `real`，而它只有这里知道。
        combo.SelectionChanged += (_, _) =>
        {
            // 退回时又会进来一次 —— 那时索引已经对了，直接放行（不会递归）。
            if (combo.SelectedIndex < 0 || combo.SelectedIndex == real)
            {
                return;
            }

            var picked = options[combo.SelectedIndex];

            PreferencesNote.Text = $"「{picked.Label}」还在开发中 —— {picked.Hint}";
            PreferencesNote.Visibility = Visibility.Visible;

            // ⚠️ **退回**：它只有一个真值，而选中的那一档**不落盘** ——
            // 留在那里就是一个骗人的假开关。
            combo.SelectedIndex = real;
        };
    }

    /// <summary>
    /// 「引导式录像」那个入口（`IMPLEMENTATION.md` M8 一行名字，零规格）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它和别的占位一样**只留入口**（需求方 2026-10-01）。
    /// 摆在这一节是**我挑的**（属于录制行为，而这一节就是录制设置所在），设计图上没有它。
    /// </remarks>
    private void OnOpenGuidedRecording(object sender, RoutedEventArgs e) =>
        ShowNotBuilt(
            PreferencesNote,
            "引导式录像还在开发中 —— 它会录的时候给软提示、录完再检测一遍，"
            + "而且只提醒、不打断录制。现在还没有这套逻辑，所以点开只能看到这句话。");

    /// <summary>
    /// 「关闭窗口时」三档（设计图 `_49`）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 三档**都**走同一条退出路（<c>AppHost.RequestExit</c> → <c>ShutdownAsync</c>），
    /// 所以「直接退出」也一样会先问一句在录的那一段 —— 下面那行说明照实写。
    /// </remarks>
    private void LoadCloseActions()
    {
        foreach (var action in Enum.GetValues<CloseWindowAction>())
        {
            CloseActionCombo.Items.Add(new ComboBoxItem
            {
                Content = DescribeCloseAction(action),
                Tag = action.ToString(),
            });
        }

        SelectByTag(CloseActionCombo, _host.Settings.CloseWindowAction.ToString());
    }

    private static string DescribeCloseAction(CloseWindowAction action) => action switch
    {
        CloseWindowAction.AskEveryTime => "每次询问",
        CloseWindowAction.Exit => "直接退出",

        // 兜底给**最保守**的那一档。新增枚举值忘了改这里时，后果只是「写着最小化、
        // 行为也是最小化」，不会丢任何东西。
        _ => "最小化到托盘",
    };

    private void OnCloseActionChanged(object sender, SelectionChangedEventArgs e)
    {
        MarkDirty();
        ShowCloseActionNote();
    }

    /// <summary>把选中那一档**真会发生的**行为写在下面。</summary>
    /// <remarks>
    /// ⚠️ 这三句里最容易写错的是「直接退出」：它<b>不是</b>「在录的段直接丢掉」，
    /// 而是照旧先问一句（`ConfirmExitWhileRecording`）。写成前者的话，
    /// 这行字正好是关于「会不会丢录像」的 —— 一个假警报会让人不敢用这一档。
    /// </remarks>
    private void ShowCloseActionNote()
    {
        CloseActionNote.Text = Enum.TryParse<CloseWindowAction>(TagOf(CloseActionCombo), out var action)
            ? action switch
            {
                CloseWindowAction.AskEveryTime => "关闭窗口时问一句：收进托盘，还是退出程序。",
                CloseWindowAction.Exit => "关闭窗口就退出程序。正在录的那一段会先问一句，不会直接丢掉。",
                _ => "关闭窗口只是收进托盘，录制照常继续。",
            }
            : string.Empty;
    }

    /// <summary>两个只落进设置文件的开关，保存之前什么都不做。</summary>
    /// <remarks>
    /// ⚠️ 开机自启动**不在这里写注册表**，尽管写起来最容易：这一节是「显式保存」
    /// 那一套（改了还能按【取消】反悔），勾一下就把注册表改了的话，
    /// 【取消】又变成骗人的了。真正的落地在保存之后的 `AppHost.SaveSettingsAsync`。
    /// </remarks>
    private void OnRunAtStartupChanged(object sender, RoutedEventArgs e) => MarkDirty();

    private void OnCheckUpdateChanged(object sender, RoutedEventArgs e) => MarkDirty();

    // ── 扩展与联动 / 扩展市场（2026-10-01：**只留入口**）──

    /// <summary>
    /// 「扩展市场」那颗按钮（设计图 `_45` / `_46` 左下角）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>它现在是**可点**的，而不再是灰的。</b>需求方 2026-10-01 裁决：
    /// 没做的功能留入口、点开**如实说一句** —— 这**推翻了**先前那条
    /// 「禁用 + 悬停写明原因」（`实时多画面` / `安装订单联动` 是按那一条办的）。
    /// 两种做法的区别是「点不动」与「点了有实话」，而踩坑 #13 禁的是**第三种**：
    /// 点了没反应、让用户以为是自己那边坏了。
    /// </remarks>
    private void OnOpenExtensionMarket(object sender, RoutedEventArgs e) =>
        ShowNotBuilt(
            ExtensionsNote,
            "扩展市场还在开发中 —— 它会用来浏览和安装别人写好的扩展。"
            + "现在这里还没有可装的东西，所以点开只能看到这句话。");

    /// <summary>「扩展 API」那个入口。同上：只留入口。</summary>
    private void OnOpenExtensionApi(object sender, RoutedEventArgs e) =>
        ShowNotBuilt(
            ExtensionsNote,
            "扩展 API 还在开发中 —— 它会让第三方脚本通过接口读取录像与单号。"
            + "现在还没有这套接口，所以点开只能看到这句话。");

    /// <summary>
    /// 「还没做」那句话 —— 写在**点击处最近的地方**，不弹模态框。
    /// </summary>
    /// <remarks>
    /// ⚠️ 目标那块字**由调用方给**：这一页有不止一处「还没做」的入口，
    /// 写死到其中一处的话，另一处点下去那句话会落在**看不见的另一节里** ——
    /// 表现就是「点了没反应」，正是踩坑 #13 本身。
    /// <para>
    /// ⚠️ 不弹模态框：这一页本来就是静态的，一句话放在按钮下面最好找；
    /// 而模态框会让用户以为「这是个要处理的错」。
    /// </para>
    /// </remarks>
    private static void ShowNotBuilt(TextBlock note, string message)
    {
        note.Text = message;
        note.Visibility = Visibility.Visible;
    }

    // ── 日志（2026-10-01「级别可配」）──────────────

    private void OnLogLevelChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();

    private void OnLogRetainDaysChanged(object sender, RoutedEventArgs e) => MarkDirty();

    /// <summary>把日志那两项填进界面。</summary>
    private void FillLogging(AppSettings settings)
    {
        LogLevelCombo.Items.Clear();

        foreach (var level in LogLevels)
        {
            LogLevelCombo.Items.Add(new ComboBoxItem
            {
                Content = DescribeLogLevel(level),
                Tag = level.ToString(),
            });
        }

        // 认不出来的档（手改坏了）就落到「信息」那一档 —— 取保守的那一头。
        var index = Array.IndexOf(LogLevels, settings.LogMinLevel);
        LogLevelCombo.SelectedIndex = index >= 0 ? index : Array.IndexOf(LogLevels, LogLevel.Info);

        LogRetainDaysBox.Text = settings.LogRetainDays.ToString();
    }

    /// <summary>能选的级别。**顺序即下拉里的顺序**（从最啰嗦到最安静）。</summary>
    private static readonly LogLevel[] LogLevels =
        [LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error];

    /// <summary>
    /// 级别在界面上叫什么。
    /// </summary>
    /// <remarks>
    /// ⚠️ 用中文而不是 `Debug` / `Info` —— 这一格是给用户按的，
    /// 而这个下拉就在「开机自启动」下面。
    /// </remarks>
    private static string DescribeLogLevel(LogLevel level) => level switch
    {
        LogLevel.Debug => "调试（最啰嗦，排查用）",
        LogLevel.Info => "信息（默认）",
        LogLevel.Warn => "警告",
        _ => "错误（最安静）",
    };

    private LogLevel SelectedLogLevel() =>
        Enum.TryParse<LogLevel>(TagOf(LogLevelCombo), out var level) ? level : LogLevel.Info;

    /// <summary>
    /// 保留天数。
    /// </summary>
    /// <remarks>
    /// ⚠️ 认不出来、或者越界（`1..365`，与 `AppSettings` 的校验同一个范围）就**退回原值**：
    /// 写个 0 进去的话，所有日志当场被清掉，而用户只是想改一个数。
    /// </remarks>
    private int ParsedLogRetainDays() =>
        int.TryParse(LogRetainDaysBox.Text.Trim(), out var days) && days is >= 1 and <= 365
            ? days
            : _host.Settings.LogRetainDays;

    // ─────────────────────────────────────────────
    // 关于
    // ─────────────────────────────────────────────

    /// <summary>填「关于」那一页。**只放真读得出来的东西**（规格 §13.1）。</summary>
    private void ShowAbout()
    {
        // ⚠️ 版本号从 `AppHost.CurrentVersion` 取，**不在这里再读一次程序集**：
        // 两处各读一次的话，判「有没有新版本」用的那个串与这里显示的可能不是一个
        // （`1.0.0` 与 `1.0.0+abc123`），而那种不一致没人查得出来。
        AboutVersionText.Text = _host.CurrentVersion;
        ShowUpdateStatus();

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

    /// <summary>「关于」页那一行「更新检查」的结论。</summary>
    /// <remarks>
    /// ⚠️ <b>「没问到」与「已是最新」必须分开说</b>：混起来的话，一个网断了的工位
    /// 会一直显示「已是最新」——而它其实一次都没查过。这句话正是用户判断
    /// 「要不要去发布页看看」的依据，说反了比不说更糟。
    /// </remarks>
    private void ShowUpdateStatus()
    {
        if (!_host.Settings.CheckForUpdates)
        {
            AboutUpdateText.Text = "已关闭（在「高级」里可以打开）。";
            return;
        }

        if (_host.UpdateStatus is not { } status)
        {
            AboutUpdateText.Text = "还没查完 —— 每次启动只查一次。";
            return;
        }

        if (!status.Succeeded)
        {
            AboutUpdateText.Text = $"没查到：{status.FailureReason ?? "原因不明"}（不影响录制与回放）。";
            return;
        }

        if (status.LatestTag is not { Length: > 0 } tag)
        {
            AboutUpdateText.Text = "对端还没有发布过任何版本。";
            return;
        }

        AboutUpdateText.Text = status.SuggestUpdate(_host.CurrentVersion)
            ? $"有新版本 {tag}（当前 {_host.CurrentVersion}），到发布页下载。"
            : $"已是最新（{tag}）。";
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
    // 百度网盘上传（批次 5，设计图 `_45` / `_46`）
    // ─────────────────────────────────────────────

    /// <summary>「补传范围」下拉（设计图 `_46`）。</summary>
    private static readonly (string Tag, string Label)[] CloudScopes =
    [
        ("All", "全部"),
        ("FromDate", "自定起始日"),
    ];

    /// <summary>队列下面那个「筛选」下拉。四档与队列状态一一对应。</summary>
    private static readonly (string Tag, string Label)[] CloudFilters =
    [
        ("All", "全部"),
        ("Pending", "等待中"),
        ("Uploading", "上传中"),
        ("Done", "已完成"),
        ("Failed", "失败"),
    ];

    /// <summary>把设置读进这一页的控件。只在开窗时跑一次（与别的节一样）。</summary>
    /// <remarks>
    /// ⚠️ <b>刻意不在「切到这一页」时重读</b>：那样会把用户还没保存的改动抹掉
    /// （改了补传范围、切走看一眼别处、再切回来 —— 改动没了）。
    /// 别的节也不重读，这里不能例外。
    /// </remarks>
    private void LoadCloud()
    {
        var settings = _host.Settings.Cloud;

        CloudAutoUploadToggle.IsChecked = settings.AutoUpload;
        CloudCompareToggle.IsChecked = settings.CompareAndBackfill;

        foreach (var (tag, label) in CloudScopes)
        {
            CloudScopeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        }

        SelectByTag(CloudScopeCombo, settings.BackfillScope.ToString());
        CloudFromDatePicker.SelectedDate = settings.BackfillFrom?.LocalDateTime;
        SyncCloudScopeControls();

        for (var n = 1; n <= 8; n++)
        {
            CloudParallelCombo.Items.Add(n.ToString());
        }

        CloudParallelCombo.SelectedItem = settings.ParallelUploads.ToString();

        CloudAppNameBox.Text = settings.AppName;

        foreach (var (tag, label) in CloudFilters)
        {
            CloudQueueFilterCombo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        }

        SelectByTag(CloudQueueFilterCombo, "All");
    }

    /// <summary>「全部」时那格日期框是禁用的（见 <see cref="SyncCloudScopeControls"/>）。</summary>
    private void SyncCloudScopeControls() =>
        CloudFromDatePicker.IsEnabled = TagOf(CloudScopeCombo) == "FromDate";

    private void OnCloudAutoUploadChanged(object sender, RoutedEventArgs e) => MarkDirty();
    private void OnCloudCompareChanged(object sender, RoutedEventArgs e) => MarkDirty();
    private void OnCloudFromDateChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();

    /// <summary>补传范围变了 —— 顺手把「起始日」那一格的可用性跟上去。</summary>
    private void OnCloudScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        MarkDirty();
        SyncCloudScopeControls();
    }

    private void OnCloudQueueFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // 换了筛选就回第一页：留在第 3 页而新的筛选只有 1 页，会看到一片空白。
        _cloudQueuePage = 1;
        _ = RefreshCloudPageAsync();
    }

    private void OnCloudQueuePrev(object sender, RoutedEventArgs e)
    {
        _cloudQueuePage--;
        _ = RefreshCloudPageAsync();
    }

    private void OnCloudQueueNext(object sender, RoutedEventArgs e)
    {
        _cloudQueuePage++;
        _ = RefreshCloudPageAsync();
    }

    /// <summary>
    /// 这一页整页刷新（能不能用、账号、状态、队列）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这里一个字都不往「可编辑」的控件里写</b>（开关、下拉、日期、输入框）——
    /// 它每两秒被定时器喊一次，写进去就是在跟用户抢方向盘。
    /// 只写那些**读的**东西：说明、状态、按钮可用性、队列列表。
    /// </remarks>
    private async Task RefreshCloudPageAsync()
    {
        if (_cloudRefreshing)
        {
            // 定时器与「切到这一页」会撞上。**不排队** —— 再跑一遍读的是同一份东西。
            return;
        }

        _cloudRefreshing = true;

        try
        {
            await RefreshCloudCoreAsync();
        }
        catch (Exception ex)
        {
            // 刷不出来**不许把窗口弄崩**（I4 的同一条精神），但也不许装作没事：
            // 这一页上全是「传没传上去」，静默失败等于让用户以为都传好了。
            CloudAccountNote.Text = $"⚠️ 这一页没刷新出来：{ex.Message}";
        }
        finally
        {
            _cloudRefreshing = false;
        }
    }

    private async Task RefreshCloudCoreAsync()
    {
        var target = SelectedArchiveTarget();
        var service = _host.Services.CloudUploads;

        if (target.Kind != ArchiveBackendKind.Cloud || service is null)
        {
            // 整页禁用 + 说清为什么（踩坑 #13：绝不渲染一个按下去没反应的开关）。
            // ⚠️ 队列那张卡**不禁用**：它只是显示，而「队列里积了多少」正是
            // 用户想看的 —— 灰掉它并不能让任何东西更安全。
            CloudAccountCard.IsEnabled = false;
            CloudUploadCard.IsEnabled = false;
            CloudDisabledNote.Visibility = Visibility.Visible;
            CloudDisabledNote.Text = CloudUnavailableReason(target);

            CloudAccountNote.Text = string.Empty;
            CloudQueueNote.Text = "这一页现在不工作";
            CloudQueueList.Items.Clear();
            CloudQueuePageText.Text = string.Empty;
            CloudQueuePrevButton.IsEnabled = false;
            CloudQueueNextButton.IsEnabled = false;

            // 归档层不是网盘（或服务没起来）时这一页整页不工作 —— 那张码也一并收起：
            // 一边写着「这一页不工作」、一边摆一张扫码就能授权的图，是自己打自己。
            ShowCloudLoginQr(null);
            return;
        }

        CloudAccountCard.IsEnabled = true;
        CloudUploadCard.IsEnabled = true;
        CloudDisabledNote.Visibility = Visibility.Collapsed;

        var session = service.Session;

        // 设备码还没批准 → 按网盘要求的最小间隔问一次（不是每两秒问一次：
        // 问得太勤是接口在文档里明说会被拒的行为）。
        if (_cloudLogin is not null && DateTimeOffset.Now >= _cloudNextPollAt)
        {
            await PollCloudLoginAsync(service);
        }

        var status = await service.StatusAsync();

        CloudLoginButton.IsEnabled = !_cloudBusy && !session.IsLoggedIn && _cloudLogin is null;
        CloudLogoutButton.IsEnabled = !_cloudBusy && session.IsLoggedIn;
        CloudSyncButton.IsEnabled = !_cloudBusy;

        // 没有失败的就没什么可重试的 —— 但**不是**禁用到底：`Failed` 会在
        // 这一页开着的时候由后台变出来，所以它每两秒重算一次。
        CloudRetryButton.IsEnabled = !_cloudBusy && status.Failed > 0;

        // 二维码跟着 _cloudLogin 走：它在就画出来，不在就收起。
        // 放在这里（而不是 OnCloudLogin / PollCloudLoginAsync 里各写一次）是刻意的：
        // 显隐只有一个来源，批准、超时、退出登录三条路都不会漏掉收起那一步。
        ShowCloudLoginQr(_cloudLogin);

        CloudAccountNote.Text = DescribeCloudAccount(session, status);

        await RefreshCloudQueueAsync();
    }

    /// <summary>
    /// 把设备码登录的二维码画出来；没有待批准的登录就收起。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 内容是 <see cref="BaiduDeviceCode.QrPayload"/>，照 009 的「二维码内容拼接规则」
    /// 逐字拼出来 —— 手机上扫一下直接落到**已经填好验证码**的授权页上，
    /// 用户只剩「登录 + 同意」两步，不用把码从电脑屏幕手抄进手机。
    /// </para>
    /// <para>
    /// ⚠️ <b>画不出来不许把这一页带下水。</b>这个函数在每两秒一次的刷新节拍里跑，
    /// 异常抛出去会被 <c>RefreshCloudPageAsync</c> 兜住、把整页文字换成一句错误 ——
    /// 而「网址 + 那串码」本来就是能用的（手抄一样办得成）。
    /// 为了一张画不出来的图把那条路也盖掉，是拿能用的换不能用的。
    /// </para>
    /// </remarks>
    private void ShowCloudLoginQr(BaiduDeviceCode? pending)
    {
        if (pending is null)
        {
            CloudQrPanel.Visibility = Visibility.Collapsed;
            CloudQrImage.Source = null;
            _cloudQrUserCode = null;
            return;
        }

        // 码没变就原样放着（理由见 _cloudQrUserCode 的说明）。
        if (CloudQrPanel.Visibility == Visibility.Visible
            && string.Equals(_cloudQrUserCode, pending.UserCode, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var modules = EnrollQr.Modules(pending.QrPayload);

            // 一个模块一个像素，放大交给 XAML 那个 NearestNeighbor（与 EnrollWindow 同一手法）。
            var bitmap = BitmapSource.Create(
                modules.GetLength(0), modules.GetLength(1), 96, 96,
                PixelFormats.Gray8, null, EnrollQr.Pixels(modules), modules.GetLength(0));

            bitmap.Freeze();

            CloudQrImage.Source = bitmap;
            _cloudQrUserCode = pending.UserCode;
            CloudQrPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // 不静默吞掉（§6.1 的 catch 那一条）：走 _cloudNote，
            // 也就是这一页顶上那句用户看得见的话。按【登录百度网盘】会把它清掉。
            CloudQrPanel.Visibility = Visibility.Collapsed;
            CloudQrImage.Source = null;
            _cloudQrUserCode = null;
            _cloudNote = $"二维码没画出来（{ex.Message}），照上面那个网址和验证码手输一样能登录。";
        }
    }

    /// <summary>这一页现在为什么不能用（**两句不同的话，别混**）。</summary>
    private string CloudUnavailableReason(ArchiveTarget target) =>
        target.Kind != ArchiveBackendKind.Cloud
            ? "这一页只在「存储与备份」里的归档层选成**百度网盘**时才有用 —— 现在选的是"
              + $"「{target.Label}」。换过去并保存、重启之后，这里的登录与上传才会真的跑起来。"
            // 档位对了，服务却没起来，只有两个可能：还没重启，或者压根没配凭据。
            : _host.Settings.ArchiveBackend != ArchiveBackendKind.Cloud
                ? "归档层刚改成百度网盘，**保存并重启**之后这一页才会起来。"
                : BaiduPanCredentials.MissingMessage;

    /// <summary>
    /// 「账号与上传状态」那张卡上的一段话。
    /// </summary>
    /// <remarks>
    /// ⚠️ 「已停用」那句是设计图 `_45` 的**原文**，按的是**已保存**的那个开关
    /// （原文写的就是「勾选…**并保存后**」）—— 用界面上还没保存的那个值去判的话，
    /// 这句话会在用户勾上的那一刻就变成「已启用」，而他还没按【应用】。
    /// </remarks>
    private string DescribeCloudAccount(BaiduPanSession session, UploadStatus status)
    {
        var lines = new List<string>();

        if (_cloudNote is { Length: > 0 } note)
        {
            lines.Add(note);
        }

        if (!status.Enabled)
        {
            lines.Add(
                "已停用：勾选「启用自动上传」并保存后，本机新录制的录像才会上传。"
                + (CloudAutoUploadToggle.IsChecked == true
                    ? "（已经勾上了，按【应用】或【确定】之后生效。）"
                    : string.Empty));
        }
        else
        {
            lines.Add("已启用：本机新录制的录像会自动上传到百度网盘。");
        }

        if (_cloudLogin is { } pending)
        {
            var minutes = Math.Max(1, (int)(_cloudLoginExpiresAt - DateTimeOffset.Now).TotalMinutes);

            lines.Add(
                $"请打开 {pending.VerificationUrl} 输入验证码 {pending.UserCode}，批准后这里会自动变成已登录"
                + $"（这串码还有约 {minutes} 分钟有效）。");
        }
        else if (!session.IsLoggedIn)
        {
            lines.Add("还没登录百度网盘。");
        }
        else if (session.Login is { } login)
        {
            lines.Add($"已登录：{login.DisplayName}（登录信息记在本机，过期前会自动续期）。");
        }

        lines.Add(
            $"队列 {status.Total} 条：等待 {status.Pending}、上传中 {status.Uploading}、"
            + $"已完成 {status.Done}、失败 {status.Failed}；此刻正在传 {status.Active} 条。"
            + (status.LastChecked is { } checkedAt
                ? $"最近一次与网盘对比：{checkedAt:yyyy-MM-dd HH:mm:ss}。"
                : "还没与网盘对比过。"));

        if (status.LastError is { Length: > 0 } error)
        {
            lines.Add($"⚠️ 最近一次出错：{error}");
        }

        return string.Join("\n", lines);
    }

    private async Task RefreshCloudQueueAsync()
    {
        if (_cloudQueueReader is null)
        {
            return;
        }

        var all = await _cloudQueueReader.LoadAsync();
        var filter = TagOf(CloudQueueFilterCombo) ?? "All";

        var filtered = all
            .Where(i => filter == "All" || i.State.ToString() == filter)
            // 刚动过的排前面：队列可能有几千条，用户想看的是「现在这条到哪了」。
            .OrderByDescending(i => i.UpdatedAt)
            .ToList();

        var pageSize = int.TryParse(CloudQueuePageSizeBox.Text, out var size)
            ? Math.Clamp(size, 1, 200) : 10;

        var pages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
        _cloudQueuePage = Math.Clamp(_cloudQueuePage, 1, pages);

        CloudQueueList.Items.Clear();

        foreach (var item in filtered.Skip((_cloudQueuePage - 1) * pageSize).Take(pageSize))
        {
            CloudQueueList.Items.Add(DescribeCloudQueueItem(item));
        }

        // 设计图 `_45` 右上角那句（队列空着的时候）。
        CloudQueueNote.Text = filtered.Count == 0 ? "暂无录像" : string.Empty;
        CloudQueuePageText.Text = $"第 {_cloudQueuePage}/{pages} 页 · 共 {filtered.Count} 条";
        CloudQueuePrevButton.IsEnabled = _cloudQueuePage > 1;
        CloudQueueNextButton.IsEnabled = _cloudQueuePage < pages;
    }

    /// <summary>队列里的一行。</summary>
    /// <remarks>
    /// ⚠️ 远端路径**要显示出来**：用户上网页版找不到文件时，
    /// 唯一能把话说清楚的凭据就是它（它记的是上传那一刻的标签，不跟着后来的改动变）。
    /// </remarks>
    private static string DescribeCloudQueueItem(UploadQueueItem item) =>
        $"{CloudStateLabel(item.State)}｜{BaiduPanLayout.FileNameOf(item.RemotePath)}｜"
        + $"{Display.Bytes(item.SizeBytes)}｜开录 {item.StartedAt:MM-dd HH:mm}｜试过 {item.Attempts} 次"
        + (item.LastError is { Length: > 0 } error ? $"\n　　⚠️ {error}" : string.Empty)
        + $"\n　　远端：{item.RemotePath}";

    private static string CloudStateLabel(CloudUploadState state) => state switch
    {
        CloudUploadState.Pending => "等待中",
        CloudUploadState.Uploading => "上传中",
        CloudUploadState.Done => "已完成",
        _ => "失败",
    };

    /// <summary>
    /// 【登录百度网盘】：拿一串设备码，把浏览器打开，然后**不挡着界面**等用户去点同意。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>不做模态对话框。</b>那个框会挡住设置窗，而用户此刻正是要去浏览器里操作 ——
    /// 框杵在那儿只会被当成「卡住了」。状态记在 <see cref="_cloudLogin"/> 上，
    /// 由这一页每两秒的刷新节拍去问一次。
    /// </remarks>
    private async void OnCloudLogin(object sender, RoutedEventArgs e)
    {
        var service = _host.Services.CloudUploads;

        if (service is null)
        {
            return;
        }

        _cloudBusy = true;

        try
        {
            var pending = await service.Session.BeginLoginAsync();

            _cloudLogin = pending;
            _cloudNextPollAt = DateTimeOffset.Now + TimeSpan.FromSeconds(Math.Max(1, pending.IntervalSeconds));
            _cloudLoginExpiresAt = DateTimeOffset.Now + TimeSpan.FromSeconds(Math.Max(60, pending.ExpiresInSeconds));

            // 打不开浏览器**不是失败**：码在界面上写着，用户手打那个网址一样能办成。
            var problem = ShellOpen.Try(pending.VerificationUrl);

            _cloudNote = problem is null
                ? null
                : $"浏览器没自动打开（{problem}），手动打开 {pending.VerificationUrl} 也一样。";
        }
        catch (Exception ex) when (ex is BaiduPanException or HttpRequestException)
        {
            _cloudNote = $"登录没起来：{ex.Message}";
        }
        finally
        {
            _cloudBusy = false;
            await RefreshCloudPageAsync();
        }
    }

    /// <summary>问一次「批准了没有」。批准了就记下来 —— <c>PollLoginAsync</c> 会把令牌落盘。</summary>
    private async Task PollCloudLoginAsync(CloudUploadService service)
    {
        var pending = _cloudLogin!;

        _cloudNextPollAt = DateTimeOffset.Now + TimeSpan.FromSeconds(Math.Max(1, pending.IntervalSeconds));

        if (DateTimeOffset.Now >= _cloudLoginExpiresAt)
        {
            _cloudLogin = null;
            _cloudNote = "那串验证码过期了，请重新点【登录百度网盘】。";
            return;
        }

        try
        {
            if (await service.Session.PollLoginAsync(pending.DeviceCode) is not null)
            {
                _cloudLogin = null;
                _cloudNote = "百度网盘登录成功。";
            }
        }
        catch (Exception ex) when (ex is BaiduPanException or HttpRequestException)
        {
            // ⚠️ 「网络抖了一下」与「用户还没点同意」**不能混成同一句话**：
            // 前者要重试，后者只要等。这里把原因记下来但**不放弃等** ——
            // 那串码还有效，下一拍照样问。
            _cloudNote = $"问授权状态时出错（会继续试）：{ex.Message}";
        }
    }

    /// <summary>【退出登录】。⚠️ 只删本机记着的登录信息，**不碰网盘上的任何文件**。</summary>
    private async void OnCloudLogout(object sender, RoutedEventArgs e)
    {
        var service = _host.Services.CloudUploads;

        if (service is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "退出登录只会删掉本机记着的登录信息，**不会**删除百度网盘上的任何文件。\n\n要继续吗？",
            "退出百度网盘",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _cloudBusy = true;

        try
        {
            await service.Session.LogoutAsync();
            _cloudLogin = null;
            _cloudNote = "已退出登录。网盘上的文件一个都没动。";
        }
        catch (Exception ex)
        {
            _cloudNote = $"退出登录没成功：{ex.Message}";
        }
        finally
        {
            _cloudBusy = false;
            await RefreshCloudPageAsync();
        }
    }

    private async void OnCloudSync(object sender, RoutedEventArgs e) =>
        await RunCloudActionAsync("立即对比同步", (service, ct) => service.SyncNowAsync(ct));

    private async void OnCloudRetry(object sender, RoutedEventArgs e) =>
        await RunCloudActionAsync("立即重试失败上传", (service, ct) => service.RetryFailedAsync(ct));

    /// <summary>【立即对比同步】/【立即重试失败上传】共用的那一圈。</summary>
    /// <remarks>
    /// ⚠️ <b>跑的时候那两个按钮要禁用</b>：一次同步可能要传好久，而重复点它
    /// 只会得到「已经有一轮在跑了，这次什么也没做」—— 那看起来像失败。
    /// 队列那张卡**照旧两秒刷一次**，所以进度是活的。
    /// </remarks>
    private async Task RunCloudActionAsync(
        string title, Func<CloudUploadService, CancellationToken, Task<int>> action)
    {
        var service = _host.Services.CloudUploads;

        if (service is null)
        {
            return;
        }

        _cloudBusy = true;
        _cloudNote = $"{title}：正在跑，进度见下面的队列。";
        await RefreshCloudPageAsync();

        try
        {
            var count = await action(service, CancellationToken.None);

            _cloudNote = count > 0
                ? $"{title}：这次传上去 {count} 条。"
                : $"{title}：这次没有新传上去的 —— 要么网盘上都已经有了，要么都失败了。"
                  + "失败原因写在下面队列的每一行上。";
        }
        catch (Exception ex)
        {
            _cloudNote = $"{title}出错：{ex.Message}";
        }
        finally
        {
            _cloudBusy = false;
            await RefreshCloudPageAsync();
        }
    }

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
