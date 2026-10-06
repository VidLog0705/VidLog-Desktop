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

public partial class SettingsWindow : Window
{
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
}
