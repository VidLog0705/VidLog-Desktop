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
using VidLog.Desktop.Core.License;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.App;

public partial class SettingsWindow : Window
{
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

        // ⚠️ 骨架那句（含「试用要先判」那条顺序）**不在这里**（T30）——
        // 它在 `LicenseStatus.SummaryText` 上，与主窗那三处**共用同一句**。
        // 这里只补设置页特有的上下文，别把那句再抄一遍。
        //
        // ⚠️ 「购买入口」是**一句话，不是一个按钮** —— 到今天为止没有真实的购买渠道
        // （没有下单页、没有联系方式），摆一颗按钮就是假开关（§63 / 踩坑 #13）。
        // 等到真有渠道了，把这里换成按钮，别在那之前先摆上。
        LicenseNote.Text = status.SummaryText + status switch
        {
            // ⚠️ 「试用期 7 天」与机位数**都得现算**：原来这里写死的是
            // 「试用期 7 天 / 4 机位」（T30）。⚠️ 2026-10-07 核过：**今天这两个数
            // 与权威值正好一致**（`TrialRecordStore.Window` 是 168 小时；试用码的
            // 机位数在码格式层就钉死为 4，见 `LicenseTests` 里
            // 「试用码的机位数不是 4 时回无效」）—— 所以这是**潜在漂移，不是当前说错**，
            // 别把它当「已经显示错了」去修。7 天的权威值是 `TrialRecordStore.Window`（§6.1），
            // 机位的权威值是 `Slots`，都不是这个文件里的字面量。
            { IsTrial: true, Activated: true } =>
                $"（试用期 {TrialRecordStore.Window.TotalDays:0} 天 / {status.Slots} 机位）"
                + "试用到期只挡住新的录制与接入 —— 已有的录像照常可以检索、回放、导出。"
                + "要长期用得换一个长期激活码：把上面的机器码给提供方。"
                + degraded,

            { IsTrial: true } =>
                "试用期里才能录新的、接手机、看手机的实时画面；"
                + "已有的录像照常可以检索、回放、导出。"
                + "要接着用得激活 —— 把上面的机器码给提供方。"
                + degraded,

            _ => degraded,
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
    /// <para>
    /// 后果很具体：盘上这份现在**只有一份**。用户若以为已经双份了，
    /// 就可能手动删掉唯一的那一份（那正是 I2 要防的事）。
    /// </para>
    /// <para>
    /// ⚠️ <b>两个来源都要看</b>（T23-A）：
    /// <see cref="ArchiveRelay.LastFailure"/> 只在内存，答的是「**这一趟运行**里最近那次」；
    /// <see cref="DesktopServices.ArchiveFailures"/> 是落盘的，答的是
    /// 「**重启之前**发生过的那些」。只看前者的话，一重启这句话就没了 ——
    /// 而那正是最容易出事的时候（重启往往就是因为刚出过问题）。
    /// </para>
    /// </remarks>
    private void ShowArchiveRelayFailure()
    {
        var relay = _host.Services.ArchiveRelay;

        // ① 这一趟运行里最近那次失败 —— 最具体（带着原因），优先说它。
        if (relay?.LastFailure is { Length: > 0 } failure)
        {
            ArchiveRelayNote.Visibility = Visibility.Visible;
            ArchiveRelayNote.Text =
                $"⚠️ 最近一次发布到{relay.Label}没成功：{failure}\n"
                + "盘上这一份仍然是好的、也能检索 —— 但它现在只有一份，"
                + "在发上去之前别删它。修好之后下次收尾会自动再发。";
            return;
        }

        // ② 重启之前欠下的那些。⚠️ 归档层是本机时 relay 是 null，但这本账照样要读
        //（用户可能刚从 NAS 改回本机，之前欠着的那几条仍然只有一份）。
        var outstanding = _host.Services.ArchiveFailures.Outstanding;

        if (outstanding.Count == 0)
        {
            ArchiveRelayNote.Visibility = Visibility.Collapsed;
            return;
        }

        // 按时间倒序取最近的那条说细节，其余只报个数 —— 一条提示里塞 N 条原因是
        // 没人读的（界面上是一行 Caption，不是列表）。
        var newest = outstanding.OrderByDescending(r => r.At).First();
        var rest = outstanding.Count - 1;

        ArchiveRelayNote.Visibility = Visibility.Visible;
        ArchiveRelayNote.Text =
            $"⚠️ 有 {outstanding.Count} 条录像没发到归档层（最近一条：{newest.EvidenceId}，"
            + $"{newest.At.LocalDateTime:yyyy-MM-dd HH:mm} —— {newest.Reason}）"
            + (rest > 0 ? $"，另有 {rest} 条更早的。" : "。")
            + "\n盘上这些仍然是好的、也能检索 —— 但它们现在只有一份，"
            + "在发上去之前别删。修好之后下次收尾会自动再发。";
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
}
