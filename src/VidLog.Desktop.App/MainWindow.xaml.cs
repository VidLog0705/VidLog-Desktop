using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的命名空间都带进来，于是 MessageBox / Brush 这类
// 同名类型变成「不明确」。这里用**别名钉死成 WPF 的那套** ——
// 这个文件里的控件与画笔全是 WPF 的，WinForms 一个都不该出现。
using Brush = System.Windows.Media.Brush;
// ⚠️ 这两个也必须钉死：WinForms 那一侧有 `System.Drawing.Image`，
// 不钉的话 `Image` 会静默解析成**画图那个**（编译期只报一句「参数不对」，
// 而真正的问题是类型选错了）。别名块存在的理由就在这里。
using Image = System.Windows.Controls.Image;
using PixelFormats = System.Windows.Media.PixelFormats;
using TextBlock = System.Windows.Controls.TextBlock;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
// ⚠️ WinForms 那侧也有一个 `KeyEventArgs`（`MouseEventArgs` 那些倒是不撞，
// 所以只有这一个要钉）。T12 的命令面板开始用 WPF 那个。
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
// ⚠️ 同理：WinForms 那侧也有一个 `Brushes`（`System.Drawing.Brushes`），
// 不钉的话 `Brushes.White` 会解析成**画图那个** —— 它根本不是 `Brush`，
// 报错只说「参数不对」，而真正的问题是类型选错了（与上面 `Image` 同一个坑）。
using Brushes = System.Windows.Media.Brushes;
// ⚠️ 同理：WinForms 那侧也有一个 `Point`（`System.Drawing.Point`）。
// 识别框那几段折线用的是 WPF 的几何类型。
using Point = System.Windows.Point;
using VidLog.Desktop.App.Platform;
using VidLog.Desktop.Core;
using VidLog.Desktop.Core.Camera;
using VidLog.Desktop.Core.Commands;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Live;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Rendering;
using VidLog.Desktop.Core.Scanning;
using VidLog.Desktop.Core.Search;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.App;

/// <summary>
/// 主窗口（「录制台」）—— 纯视图。
/// </summary>
/// <remarks>
/// <para>
/// 装配与生命周期都在 <see cref="AppHost"/> 里：规格 §3.2.1 要求后台仍能收码，
/// 而那要求钩子与录制协调器活得比窗口长。这里只做呈现与把操作转成 Core 调用。
/// </para>
/// <para>
/// ⚠️ <b>2026-09-28 大拆</b>：原先这一个窗口里塞了四个页（概览 / 工作 / 检索 / 设置），
/// 靠左竖导航切 <c>Visibility</c>。照需求方的设计图改成了「录制台」，于是——
/// <list type="bullet">
///   <item>设置 → 独立窗 <see cref="SettingsWindow"/></item>
///   <item>检索与回放 → 独立窗 <see cref="SearchWindow"/></item>
///   <item>概览 → 收进本窗右侧的「本机录制动态」面板（需求方 2026-09-28 裁决）</item>
/// </list>
/// <b>拆窗会动近九十个控件的落点，而这件事没有任何测试挡得住</b>
/// （App 层是 <c>net9.0-windows</c>，<c>Core.Tests</c> 是 <c>net9.0</c>，引用不了）
/// —— 搬错一个就是启动即崩。所以这一批**一次只搬一个窗口、搬完跑一次**。
/// </para>
/// <para>
/// ⚠️ <b>2026-10-06（改造清单 T26④）按原先那几个分节拆成了 partial</b> ——
/// 本文件留着字段、构造器、重命名提醒，以及<b>启动</b>与<b>关闭</b>（窗口的主干与
/// 生命周期），其余三块各自成文件：<c>MainWindow.Overview.cs</c>（本机录制动态 /
/// 本机用途）、<c>MainWindow.Commands.cs</c>（全局热键 / 命令面板）、
/// <c>MainWindow.Recording.cs</c>（录制台上的按钮：录制、发货退货那两张条码、
/// 以及开别的窗）。拆之前 1619 行 —— 母仓 §4「超过 1500 行必须拆」，
/// 验收见 <c>DesktopServicesTests.T26_点名的长文件拆完之后都不超过800行</c>。
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _ticker;

    /// <summary>
    /// 预览区右上角那个「可信时钟」的刷新。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它**自己一个 timer、一直跑**，不搭 <see cref="_ticker"/> 的车：
    /// 那个是录制时按需启停的，而屏幕上的时间不该在空闲时停住 ——
    /// 一个停在 18:19:15 不动的水印，用户会以为程序卡死了。
    /// （本来也不该拿它当表用，但**它是这台机器上唯一说了真话的时间**，
    /// 用户对着它核对快递单上的手写时间，这是设计图里它存在的理由。）
    /// </remarks>
    private readonly DispatcherTimer _clockTicker;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        // ⚠️ 在构造里就切好，不在 `OnLoaded` 里 —— 后者会先画一帧录制台再换成
        // 备份主机面板，看起来像闪了一下。数（备份条数/设备数）要等索引读完，
        // 那一半在 `RefreshOverviewAsync` 里填。
        // （标题也归它管，见 `ApplyStationRole`。）
        ApplyStationRole();

        // 界面上「已录 / 已存」只能靠定时刷新 —— 协调器不推送进度，Elapsed 是拉取式的。
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _ticker.Tick += (_, _) => UpdateRecordingStatus();

        _clockTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTicker.Tick += (_, _) =>
        {
            UpdatePreviewClock();

            // ⚠️ 状态栏搭这一趟车，不搭 `_ticker` —— 那个是**录制时按需启停**的，
            // 而磁盘余量、在线机位数在空闲时照样在变（`_clockTicker` 自己那条
            // 「不该在空闲时停住」的理由，这里同样成立）。
            UpdateStatusBar();

            // ⚠️ 相机回来之后自动重探（补上 §3.1.7「改了下次开始工作才生效」之外的
            // 那一种：用户什么都没改，是相机自己回来了）。**不 await** ——
            // 它可能真要开一次相机、卡住十秒，而这一跳还得去刷屏幕上的时钟。
            // 限流与全部判据都在 `MaybeReprobeAsync` 里面，不回落时它只做一次布尔判断。
            _ = _host.MaybeReprobeAsync();
        };

        _previewTimer.Tick += (_, _) => RefreshPreview();

        // 预览区一改尺寸，识别框就得跟着挪（画面是等比缩进这个格子的，
        // 黑边宽度会变）。⚠️ 尺寸变化**不经过** `RefreshPreview`（那一跳才 30ms 一次），
        // 拖窗口的时候跟不上就会看到框在画面外飘着。
        PreviewArea.SizeChanged += (_, _) => UpdateRecognitionBox();

        // 卡片的高矮也跟着行宽走 —— 画面要正好铺满它，见 `FitPreviewCard`。
        PreviewCard.SizeChanged += (_, _) => FitPreviewCard();

        // 待批准的改名请求（规格 §3.4.5 ③）。
        //
        // ⚠️ 它**不能挂在 `EnrollWindow` 上**：那个窗的轮询是以「屏幕上那张二维码」
        // 为生命周期的（码一换/一过期，它下面的请求就一起作废）。而**改名没有二维码**
        // —— 它靠设备自己的凭据，随时可能来，窗多半还关着。
        // 所以由主窗自己看，用**同一套人工批准交互**（弹窗问同意/拒绝）。
        _renameWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _renameWatch.Tick += async (_, _) => await AskPendingRenameAsync();

        // ⚠️ 与 `_ticker` **不同**：那个是录制时按需启停的，而这个**一直跑** ——
        // 改名请求不挑时候（手机那端用户想改就改），窗关着就收不到。
        // 2 秒一次、每次只读一个内存字典（`PendingRenamesAsync`），代价可忽略。
        _renameWatch.Start();

        Loaded += OnLoaded;

        // 命令面板（T12）—— 焦点在哪个控件上都要收得到，所以挂窗这一层。
        PreviewKeyDown += OnPaletteShortcut;

        // 全屏取景时按 Esc 退出（需求方：全屏后能取消回原窗口）。
        PreviewKeyDown += OnPreviewFullscreenKey;

        Closed += (_, _) =>
        {
            _ticker.Stop();
            _clockTicker.Stop();
            _previewTimer.Stop();
            _renameWatch.Stop();

            // ⚠️ 热键**必须撤**（T15）：`RegisterHotKey` 是系统级的独占注册，
            // 不退的话这台机器上那个组合键从此刻起谁都用不了 —— 而我们的窗口已经没了。
            _hotKeys?.Dispose();
            _hotKeys = null;
        };

        _host.Notice += OnNotice;
        _host.Scanned += OnScanned;
    }

    /// <summary>待批准的改名请求轮询（规格 §3.4.5 ③）。</summary>
    private readonly DispatcherTimer _renameWatch;

    /// <summary>已经**问过**的那些设备 —— 同一个设备只弹一次。</summary>
    /// <remarks>
    /// ⚠️ 与 `EnrollWindow._asked` 同一个理由：手机是轮询的，同一条请求会被反复看到；
    /// 不记的话用户会被同一个弹窗反复打断。
    /// </remarks>
    private readonly HashSet<string> _askedRenames = new(StringComparer.Ordinal);

    /// <summary>
    /// 有人要改名 ⇒ 弹一次（同意 / 拒绝）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规格 §3.4.5 ③：「……如需要再次更改，**需要电脑端同意才能更改**」，
    /// 且这句话属于「走同一套弹窗通道」那一组 —— 所以交互与入网批准同形。
    /// </para>
    /// <para>
    /// ⚠️ 拒绝也要**留着那条请求**（与入网那条同一个理由）：手机在轮询，
    /// 清掉的话它下一轮拿到的是「没有这条请求」，而用户明明刚被拒。
    /// </para>
    /// </remarks>
    private async Task AskPendingRenameAsync()
    {
        var registry = _host.Services.Devices;

        var waiting = (await registry.PendingRenamesAsync())
            .Where(r => r.Decision == EnrollDecision.Pending)
            .FirstOrDefault(r => !_askedRenames.Contains(r.DeviceId));

        if (waiting is null)
        {
            return;
        }

        _askedRenames.Add(waiting.DeviceId);

        var answer = MessageBox.Show(
            this,
            $"「{waiting.DeviceName}」\n\n"
            + "这台手机想把机位名改成上面这个。同意吗？\n"
            + "（机位名是电脑端用来区分设备的，改了之后这里显示的一直是新名字。）",
            "改名请求",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        await registry.DecideRenameAsync(
            waiting.DeviceId, approved: answer == MessageBoxResult.Yes);
    }

    // ─────────────────────────────────────────────
    // 启动
    // ─────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 装钩子 —— 装不上要给用户看见（I3），不能让他一直奇怪扫码枪怎么没反应。
        _host.StartKeyboardHook();

        AttachHotKeys();

        ShowStatusSummaries();

        // 右栏那两张命令码（图 `_35`）。**每次开窗都重画一遍** ——
        // 协调器不持久化这个档，所以启动时它必然是「发货」，
        // 而按钮文字与那两张码必须跟着它。
        RenderScanBarcodes();

        // 单号框**一直可用**：手动输入单号这件事与摄像头在不在一点关系都没有。
        // （拆窗之前它是跟着摄像头枚举一起解禁的 —— 那是个耦合，顺手拆掉。）
        WaybillBox.IsEnabled = true;
        RefreshStartButton();

        UpdatePreviewClock();
        UpdatePreviewHint();
        _clockTicker.Start();
        _previewTimer.Start();

        StatusText.Text = _host.Services.Server?.BaseUrl is { Length: > 0 } url
            ? $"服务已就绪。回放地址：{url}"
            : "服务已就绪。回放服务未启动。";

        // 上次没走完的录像收回来没有（规格 §3.1.1）。**必须说出来** ——
        // 「悄悄收好了」和「其实什么都没收」在界面上长得一模一样，用户无从分辨。
        // 收尾失败的会由 StartupReport.Warnings 走「需要注意」那一块，不在这里重复。
        // ⚠️ 单位是**场（会话）**，不是「段」：`RecoveredCount` 数的是
        // `OrphanOutcomes` 里成功的**会话**，而一场里有几个分段就是几个 ——
        // 日志那边是按段一条条记的。写成「段」的话同一件事会出现
        // 「通知说 1 段、日志写 4 段」（2026-10-02 实测报上来的），
        // 两个数都对，只有那个量词错。
        var recovered = _host.Startup.RecoveredCount;
        if (recovered > 0)
        {
            NoticesText.Text =
                $"{DateTime.Now:HH:mm:ss}  上次有 {recovered} 场录像没走完收尾，已自动收好并入库。";
        }

        // 保留期到了的那些（规格 §3.5.5）。**排在最后**：它是唯一会删东西的一步，
        // 应该发生在「上次的录像收好了」之后 —— 否则刚收进来的那段可能正好在
        // 清理计划里（它确实会被 24 小时豁免挡住，但顺序反了会让人以为是它被删的）。
        await RunStartupCleanupAsync();

        UpdateRecordingStatus();
        UpdateStatusBar();

        // 「本机录制动态」的数要算一次。**排在清理之后** ——
        // 否则刚清完的那批还挂在「待清理」上。
        await RefreshOverviewAsync();
    }

    /// <summary>
    /// 右边那一栏顶部那两行状态（校准 / 许可）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 走 <see cref="StatusSummaries"/> 而**不在这里另写一份**：设置窗也要显示
    /// 同样两句话，各写一份迟早出现「主窗说已校准、设置窗说没校准」——
    /// 那种自相矛盾比哪一边说错都更让人不敢信这个界面。
    /// </remarks>
    private void ShowStatusSummaries()
    {
        NavClockText.Text = StatusSummaries.Calibration(_host);
        NavLicenseText.Text = StatusSummaries.License(_host);

        // ⚠️ 备份主机那套布局**没有**左边那一栏，所以它要自己的一份 —— 但**是同一句话**
        //    （同一次调用产出、同一个来源）。2026-10-03 之前这里没有这一行，
        //    于是「备份主机」形态下**看不到试用剩余 / 已到期**。
        BackupLicenseText.Text = StatusSummaries.License(_host);
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
    /// 判据取自 <see cref="VidLog.Desktop.Core.Cleanup.CleanupService.CanCleanup"/>，
    /// 与执行层那道闸是同一份 —— 界面上不问、执行层也会拒，两道都在。
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
            // ⚠️ 走**同一个**流程：设置页那颗【按时间清理…】算的东西与这里**一模一样**
            // （都是按保留期），开头那句话此前在两处各写了一遍 ——
            // 改一处漏一处的话，用户会看到「开机时说的」与「自己点出来的」对不上。
            var preview = await _host.Services.CleanupFlow.ByTimeAsync(
                _host.Settings.Retention, DateTimeOffset.Now);

            if (preview.Proposal is not { } proposal)
            {
                // **没有候选就不打扰** —— 每次开机弹一个「没什么要清的」是噪音，
                // 而噪音会把真正该看的那一次淹掉。
                // （设置页那两颗按钮不一样：那是用户主动点的，没得清也要说一句。）
                return;
            }

            // 预告框与「删了什么」那句话只有一份实现（`CleanupPrompt`）——
            // 它现在有三个调用点，各写一遍迟早有一处把 I8 那三条说明改漏。
            var outcome = await CleanupPrompt.AskAndRunAsync(this, _host, proposal);

            NoticesText.Text = $"{DateTime.Now:HH:mm:ss}  {outcome.Message}";
        }
        catch (Exception ex)
        {
            // 清理失败不该拦住启动（I4 的同一条精神）—— 但要说出来。
            // ⚠️ 「说出来」有两处：这里，以及日志。只有界面那句话的话，
            // 用户划走就没有了，而清理是**不可逆动作** —— 事后要能查为什么没清成。
            _host.Log(LogLevel.Warn, "清理", $"清理没能进行：{ex.Message}");
            NoticesText.Text = $"{DateTime.Now:HH:mm:ss}  清理没能进行：{ex.Message}";
        }
    }

    // ─────────────────────────────────────────────
    // 关闭
    // ─────────────────────────────────────────────

    /// <summary>
    /// 关窗口怎么办 —— 由设置里那三档说了算（设计图 `_49`「关闭窗口时」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>默认仍是「收进托盘，不是退出」</b>（规格 §3.2.1：后台仍要收码）。
    /// 那一档的注释以前写的是「无条件取消关闭」，2026-09-30 加了另外两档之后
    /// 它变成**三选一**，但默认那一路一个字节都没变。
    /// </para>
    /// <para>
    /// ⚠️ <b>本方法**不**负责「在录的时候问一句」</b>：那是
    /// <see cref="AppHost.ShutdownAsync"/> 里 <see cref="AppHost.ConfirmExitWhileRecording"/>
    /// 的事，走的是托盘菜单退出与这里**同一条**路。在这儿再问一次会变成问两遍。
    /// </para>
    /// <para>
    /// ⚠️ <b>红线（计划 §红线 1）的解释</b>：托盘本体、<c>ShutdownMode</c>、
    /// <c>CrashGuard</c>、录中确认这四样的接线**一个字没动**；
    /// 变的只是「关窗」这一个动作按设置走哪一档。
    /// </para>
    /// </remarks>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        switch (_host.Settings.CloseWindowAction)
        {
            // ── 直接退出 ────────────────────────────────────────────────
            //
            // ⚠️ 仍然 **先取消这一次关闭**，改走 `RequestExit`：那条路会问
            // 「还在录，要结束它并退出吗」，而且退出前的收尾（收段、拆托盘、
            // 释放服务）全在它里面。直接放行 `Close()` 的话，`ShutdownMode` 是
            // `OnExplicitShutdown`，窗口关了进程却还在 —— 那就是「关不掉」。
            case CloseWindowAction.Exit:
                e.Cancel = true;
                _host.RequestExit?.Invoke();
                return;

            // ── 每次询问 ────────────────────────────────────────────────
            case CloseWindowAction.AskEveryTime:
                e.Cancel = true;
                AskThenClose();
                return;

            // ── 收进托盘（默认，也是升级后的行为）──────────────────────
            default:
                e.Cancel = true;
                MinimizeToTray();
                return;
        }
    }

    /// <summary>关窗时问一句：收进托盘 / 直接退出 / 取消（设计图 `_49` 的「每次询问」）。</summary>
    /// <remarks>
    /// ⚠️ 默认按钮是「取消」：关窗这个动作本身不该被回车键替用户决定，
    /// 而三选一里点错最贵的是「直接退出」（在录的时候它会结束这一段）。
    /// </remarks>
    private void AskThenClose()
    {
        var answer = MessageBox.Show(
            this,
            "要把窗口收进托盘，还是直接退出？\n\n"
            + "· 收进托盘：程序继续在后台跑，扫码枪照常可用；\n"
            + "· 直接退出：结束本次使用（还在录的话会先问你一句）。",
            "关闭窗口",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        switch (answer)
        {
            case MessageBoxResult.Yes:
                MinimizeToTray();
                break;

            case MessageBoxResult.No:
                _host.RequestExit?.Invoke();
                break;

            default:
                // 取消：什么都不做，窗口留在屏幕上（连计时器都不用停）。
                break;
        }
    }

    /// <summary>收进托盘：停掉两个计时器、藏窗口、说一句。</summary>
    private void MinimizeToTray()
    {
        _ticker.Stop();
        _clockTicker.Stop();
        _previewTimer.Stop();
        Hide();

        if (_host.Settings.CloseWindowAction == CloseWindowAction.MinimizeToTray)
        {
            _host.Tray?.Notify("VidLog 还在后台", "扫码枪照常可用。要退出请右键托盘图标。");
        }
    }
}
