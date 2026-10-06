using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
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
using VidLog.Desktop.App.Platform;
using VidLog.Desktop.Core;
using VidLog.Desktop.Core.Commands;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Live;
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

    /// <summary>
    /// 取景画面的刷新。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>它是个「拉」的定时器，不是「推」的</b>：帧由 ffmpeg 那两条路投进
    /// <see cref="AppHost.Preview"/> 的单槽，界面在这里取最新一帧。
    /// 反过来的话（拿到帧就 <c>Dispatcher.Invoke</c>）等于**在管道的读线程上
    /// 做界面工作** —— 而那条线程一慢，管道就满，堵住的后果是录制进程被强杀、
    /// MKV 尾部丢掉（§54.2 真机复现过）。
    /// </para>
    /// <para>
    /// 30 fps 的间隔与配置向导预览区用的是同一个数（33ms）。
    /// </para>
    /// </remarks>
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    /// <summary>画面那块位图；尺寸变了就重建（灰度帧与彩色帧的尺寸不一样）。</summary>
    private WriteableBitmap? _frameBitmap;

    /// <summary>上一次真的画上帧的时刻（<see cref="Environment.TickCount64"/>）。</summary>
    /// <remarks>
    /// 判据是「一秒没帧就把说明放回来」而不是「进程还在不在」：
    /// 出画面的那两条路（识码、录制）**都在进程之间交接**，
    /// 而交接期的空档不该在屏幕上闪一下「没有画面」。
    /// </remarks>
    private long _lastFrameAtMs;

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
    /// 预览区右上角那个时间水印。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 它读的是 <b>可信时钟</b>（<c>TrustedClock</c>），不是系统墙钟 ——
    /// 与录像里烧进去的那个时间同源。用户拿它核对快递单上的手写时间，
    /// 所以两者**必须是一个时间**。
    /// </para>
    /// <para>
    /// ⚠️ <b>未校准时不许显示一个时间</b>：那时 <c>Now</c> 会静默回落到墙钟，
    /// 而一个看起来正常、实际上不可信的时间比空着坏得多 ——
    /// 用户会拿它去做判断。未校准就如实说未校准（规格 §3.6.4 的同一精神）。
    /// </para>
    /// </remarks>
    private void UpdatePreviewClock()
    {
        var clock = _host.Services.TrustedClock;

        if (!clock.IsCalibrated)
        {
            PreviewClockText.Text = "时间未校准";
            PreviewClockText.Foreground = (Brush)FindResource("Warning");
            return;
        }

        var now = clock.Now.ToLocalTime();

        PreviewClockText.Text =
            $"UTC{(now.Offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(now.Offset.Hours):00}: "
            + now.ToString("yyyy/MM/dd HH:mm:ss");
    }

    /// <summary>
    /// 把最新一帧画到取景框上；一秒没帧就退回那句说明。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这是**拉**，不是推：帧由出画面的那个进程投进单槽，界面自己来取。
    /// 反过来（读线程上直接 <c>Dispatcher.Invoke</c>）等于在管道的读线程上做界面工作，
    /// 而那条线程一慢，管道就满 —— 录制进程会被强杀、MKV 尾部丢掉（§54.2）。
    /// </remarks>
    private void RefreshPreview()
    {
        var frame = _host.Preview.TakeLatest();

        if (frame is null)
        {
            // 判据是「一秒没有新帧」而不是「进程还在不在」：出画面的那两个
            // （识码、录制）在交接时本来就有空档，空档不该在屏幕上闪成一句话。
            if (PreviewImage.Visibility == Visibility.Visible
                && Environment.TickCount64 - _lastFrameAtMs > 1000)
            {
                PreviewImage.Source = null;
                PreviewImage.Visibility = Visibility.Collapsed;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                UpdatePreviewHint();

                // ⚠️ 这一条是**必须**的（§6.1）：上面那句提示写着「原因会记在通知里」，
                // 不留痕的话那句话就是假的 —— 而这正是 §6.1 点名的那个坑：
                // 「有个用户可见通道」看起来像缺口被满足了，其实没有。
                //
                // ⚠️ 只在**由有到无的那一次**记：判据与提示文字是同一处，
                // 而画面空着的时候这个分支不会再进来（`PreviewImage` 已经不是 Visible），
                // 所以长期没画面不会把日志刷满（§6.1「重复的问题只在变了的时候记」）。
                _host.Logger.Log(
                    VidLog.Desktop.Core.Diagnostics.LogLevel.Warn, "预览",
                    _host.Coordinator.IsWorking
                        ? "工作期间取景画面断了（录制本身不受影响）"
                        : "取景画面断了");
            }

            return;
        }

        // ⚠️ 位图**不能只建一次**：两种画面的尺寸不一样
        // （识码那一路是 640×480 的灰度帧，录制那一路是 640×360 的彩色帧）。
        if (_frameBitmap is null
            || _frameBitmap.PixelWidth != frame.Width
            || _frameBitmap.PixelHeight != frame.Height)
        {
            _frameBitmap = new WriteableBitmap(
                frame.Width, frame.Height, 96, 96, PixelFormats.Rgb24, null);
            PreviewImage.Source = _frameBitmap;
        }

        _frameBitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height), frame.Rgb, frame.Stride, 0);

        _lastFrameAtMs = Environment.TickCount64;

        if (PreviewImage.Visibility != Visibility.Visible)
        {
            PreviewImage.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 预览区中央那行说明。
    /// </summary>
    /// <remarks>
    /// ⚠️ **必须说清楚为什么没有画面**。一个空框与「相机坏了」「还没开始」
    /// 「程序卡住了」三种情况长得一模一样。
    /// </remarks>
    private void UpdatePreviewHint()
    {
        if (_host.Services.FfmpegPath is null)
        {
            PreviewHintText.Text = "本机没有 FFmpeg，无法采集，也没有画面。";
            return;
        }

        if (_host.DeviceName.Length == 0)
        {
            PreviewHintText.Text = "没有找到摄像头，所以这里没有画面。到【设置 → 设备与外观】里看看。";
            return;
        }

        // 走到这里说明「有 FFmpeg、有摄像头，但没有进程在出画面」。只剩两种情形，
        // 而它们对用户的意思完全不同：
        //  · 还没开始工作 —— 相机本来就没被占用，这是**正常的**；
        //  · 工作中却一直没有画面 —— 那是真的掉了，得让他知道这与录制无关。
        // ⚠️ 文案里点名的必须是**界面上真有的那颗按钮**：顶栏那颗叫【开始录制】
        // （`StartWorkLabel`，「开始工作」是代码里的叫法，用户看不见）。
        // 2026-10-02 截图核对时发现的 —— 原来写的是【开始工作】，
        // 用户照着找一个不存在的按钮。
        PreviewHintText.Text = _host.Coordinator.IsWorking
            ? "取景画面没出来。录制本身不受影响，原因会记在通知里。"
            : "还没开始工作。点【开始录制】之后，这里就会显示取景画面。";
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
            NoticesText.Text = $"{DateTime.Now:HH:mm:ss}  清理没能进行：{ex.Message}";
        }
    }

    // ─────────────────────────────────────────────
    // 「本机录制动态」（原「概览」页，2026-09-28 收进右侧面板）
    // ─────────────────────────────────────────────

    /// <summary>正在重算 —— 连点【刷新】不该叠起来。</summary>
    private bool _overviewBusy;

    private async void OnRefreshOverview(object sender, RoutedEventArgs e) =>
        await RefreshOverviewAsync();

    /// <summary>
    /// 重算右侧那一栏。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>只放盘上真能算出来的东西</b>（规格 §13.1）。这里**没有**
    /// 「待上传 N 条」「在线手机 N 台」「归档层现在通不通」这类卡片 ——
    /// 桌面端今天没有上传队列、没有设备心跳、没有 per-录像的归档状态字段。
    /// 算不出来的数编一个上去比空着坏得多：用户会拿它去做判断（删不删盘上的东西）。
    /// </para>
    /// <para>
    /// ⚠️ 它**不挂那 500ms 的 ticker**：下面 <c>LoadAllAsync</c> 是全量读索引文件，
    /// 半秒一次会把界面拖死。只在进窗口时与点【刷新】时算。
    /// </para>
    /// </remarks>
    private async Task RefreshOverviewAsync()
    {
        if (_overviewBusy)
        {
            return;
        }

        _overviewBusy = true;
        OvRefreshButton.IsEnabled = false;

        try
        {
            var storage = _host.Services.Storage;

            // ── ① 今天（底部统计条 + 右栏）──────────────────────────────
            var today = DateTime.Today;
            var from = new DateTimeOffset(today, DateTimeOffset.Now.Offset);
            var to = new DateTimeOffset(today.AddDays(1), DateTimeOffset.Now.Offset);

            // ⚠️ `Limit` 默认只有 200，必须显式顶高：不顶的话「今天录了多少段」
            // 会被静静截断在 200，而**界面上一点都看不出来它是截断的**。
            var hits = await _host.Services.Search.SearchAsync(
                new RecordingQuery { From = from, To = to, Limit = int.MaxValue });

            var known = hits.Aggregate(TimeSpan.Zero, (sum, hit) => sum + hit.Entry.Duration);
            var average = hits.Count == 0
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(known.Ticks / hits.Count);

            TodayCountText.Text = $"今日 {hits.Count} 件";
            TodayAverageText.Text = $"平均 {(int)average.TotalSeconds} 秒";
            TodayTotalText.Text = $"总耗时 {(int)known.TotalSeconds} 秒";

            OvTodayText.Text =
                $"{hits.Count} 段 · 已知时长合计 {(int)known.TotalHours} 小时 {known.Minutes} 分";

            var all = await _host.Services.Index.LoadAllAsync();
            OvIndexText.Text = $"{all.Count} 段";

            // 录像库总容量：全量遍历目录，几万个文件时是秒级 ⇒ 挪出 UI 线程，
            // 否则大库上窗口会僵住（而这正是用户点【刷新】的那一刻）。
            //
            // ⚠️ **每一个保存位置都要量**（多磁盘，设计图 `_43`）：只量第一个根的话
            // 这个数会**静默偏小**，而用户会拿它判断「盘还够不够用」。
            OvLibraryText.Text = "正在统计…";
            var roots = storage.ReadRoots;
            var footprint = await Task.Run(() =>
                roots
                    .Select(one => LibraryFootprintProbe.Measure(one))
                    .Aggregate(new LibraryFootprint(0, 0, 0), (sum, one) => new LibraryFootprint(
                        sum.FileCount + one.FileCount,
                        sum.TotalBytes + one.TotalBytes,
                        sum.UnreadableCount + one.UnreadableCount)));

            // ⚠️ 读不到的位置**必须说出来**：不说的话那个字节数是**静默偏小**的，
            // 而用户会拿它判断「盘还够用」。
            OvLibraryText.Text = footprint.UnreadableCount == 0
                ? $"{Display.Bytes(footprint.TotalBytes)} · {footprint.FileCount} 个文件"
                : $"{Display.Bytes(footprint.TotalBytes)} · {footprint.FileCount} 个文件"
                  + $"（另有 {footprint.UnreadableCount} 处读不到，实际只会更多）";

            OvUpdatedText.Text = $"统计于 {DateTime.Now:HH:mm:ss}";

            // ── ② 状态 ────────────────────────────────────────────────
            //
            // ⚠️ 读的是 `AppHost.Camera`（**启动时**定下来的那个），
            // 不是设置里用户刚选的那个 —— 摄像头改了要重启才生效。
            //
            // ⚠️ 显示用 `Display`（网络那一档会写成「网络摄像头 · <地址>」），
            // 而不是 `Address` —— 后者在网络那一档带着摄像头密码。
            OvCameraText.Text = _host.Services.FfmpegPath is null
                ? "没有 FFmpeg，无法采集"
                : _host.Camera.IsEmpty
                    ? "没有找到摄像头"
                    : _host.Camera.Display;

            var server = _host.Services.Server;
            OvServerText.Text = server?.BaseUrl is { Length: > 0 } url
                ? server.IsUsingFallback
                    ? $"已启动 · {url}（只绑到本机，别的设备访问不了）"
                    : $"已启动 · {url}"
                : "未启动";

            var archive = _host.Services.ArchiveTarget;
            OvArchiveText.Text = archive.ConfigurationProblem is { Length: > 0 } problem
                ? $"⚠️ {archive.Label} —— 没配好：{problem}"
                : archive.Label;

            OvDiskText.Text = FormatFreeSpace(storage.ActiveRoot);

            var devices = await _host.Services.Devices.DevicesAsync();
            OvDevicesText.Text = devices.Count == 0 ? "还没有手机接进来" : $"{devices.Count} 台";

            // ── ④ 备份主机那一屏的四个数（设计图 `_39`）─────────────────
            //
            // ⚠️ 复用上面刚算出来的，**不重算一遍** —— 重算就是又一次
            // 全量读索引 + 又一次 `SearchAsync`，而数据源明明是同一份。
            // 两边要是算岔了，同一块屏幕上会出现两个对不上的「今日」。
            //
            // ⚠️ 「备份 N 个」数的是**本机索引里的录像段**，不是「收了多少个包裹」。
            // 托盘列表上写的就是这个数，而它是盘上真能数出来的。
            if (!_host.Settings.Role.Records)
            {
                BackupTodayText.Text = hits.Count.ToString();
                BackupTotalText.Text = all.Count.ToString();

                BackupDeviceTag.Text = devices.Count == 0 ? "暂无设备" : "已就绪";
                BackupDeviceTag.Foreground =
                    (Brush)FindResource(devices.Count == 0 ? "Warning" : "Success");
                BackupDeviceText.Text = devices.Count == 0
                    ? "还没有手机或电脑接进来。用上面的【连接电脑/手机】把它们加进来。"
                    : $"已接入 {devices.Count} 台设备，录像会存到本机。";
            }

            // ── ③ 待办（没有就不出现）─────────────────────────────────
            var cleanupLine = string.Empty;
            var overdue = 0;

            // ⚠️ 归档层就是本机磁盘时**连算都不算**：那时盘上这份是唯一副本，
            // 清理入口本来就不该出现（规格 §3.5.1），何况这里只是看一眼。
            if (_host.Services.Cleanup.CanCleanup)
            {
                var plan = await _host.Services.Cleanup.PreviewAsync(
                    _host.Settings.Retention, DateTimeOffset.Now);

                if (plan.Candidates.Count > 0)
                {
                    // 「约」不能省：那是估算值，而且这一层自己就承认没标定过。
                    cleanupLine =
                        $"{plan.Candidates.Count} 条 · 约 {plan.TotalBytes / 1024 / 1024} MB"
                        + "（估的，清理前会再算一次）";
                }

                overdue = plan.OverdueUnarchived.Count;
            }

            OvCleanupText.Text = cleanupLine;
            OvOverdueText.Text = overdue == 0
                ? string.Empty
                : $"{overdue} 条未备份的已过保留期（不会被自动删，只是提醒上传）";

            // ⚠️ 启动时的警告**全列出来**，不只第一条。
            // 原先主窗有一块专门的「需要注意」清单（拆窗时删掉了，明细现在在设置窗里），
            // 这里若只写第一条，用户就得去设置窗才知道后面还说了什么 ——
            // 而警告里有「没有摄像头，无法录制」这种**必须当场知道**的。
            OvWarningsText.Text = _host.Warnings.Count == 0
                ? string.Empty
                : "⚠️ 启动时有需要注意的地方：\n"
                  + string.Join('\n', _host.Warnings.Select(w => $"· {w}"));

            OvTodoCard.Visibility = cleanupLine.Length > 0 || overdue > 0 || _host.Warnings.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // 概览算不出来**不该让整个界面出错**（I4 的同一条精神）——
            // 但也不能装作没事：把原因写在该显示数字的那一格上。
            OvUpdatedText.Text = $"⚠️ 统计没算完：{ex.Message}";
        }
        finally
        {
            _overviewBusy = false;
            OvRefreshButton.IsEnabled = true;
        }
    }

    /// <summary>数据目录所在盘的可用空间。</summary>
    /// <remarks>
    /// ⚠️ 读不到时**不许渲染成一个数字**。<see cref="DriveSpaceProbe"/> 是**抛**的
    /// （不是返回 -1），而一个「0 GB」或「-1 GB」会被当成真的 ——
    /// 用户会据此判断「盘满了」。说不出原因也要说「读不到」。
    /// </remarks>
    private static string FormatFreeSpace(string path)
    {
        try
        {
            return $"可用 {Display.Bytes(new DriveSpaceProbe().GetFreeBytes(path))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"读不到可用空间（{ex.Message}）";
        }
    }

    // ─────────────────────────────────────────────
    // 本机用途（设计图 `_11`–`_15` 的选择窗 → `_39` 的备份主机形态）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 按本机用途决定这一屏是录制台还是备份主机面板，并把后者的固定文字填上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据取 <see cref="StationRoleChoice.Records"/>（「这台电脑录不录像」），
    /// **不是**「用途等于哪一档」—— 四档里两档录、两档不录，按档位写死的话
    /// 以后加第五档就会漏掉一处，而漏掉的表现正好是这一屏显示错。
    /// </para>
    /// <para>
    /// ⚠️ 整块**藏起来**而不是禁用：一台不录像的机器上摆着「开始录制」只是噪声，
    /// 而<b>禁用</b>更坏 —— 它会让人以为「本来能录、只是哪儿没配好」，
    /// 于是去翻设置找一个根本不存在的毛病。
    /// </para>
    /// <para>
    /// ⚠️ 换用途之后**不重新渲染**（见 <see cref="OnSwitchRole"/>）：录不录像是在
    /// <c>AppHost.StartAsync</c> 的装配期定死的，当场把录制台亮出来是个谎。
    /// </para>
    /// </remarks>
    private void ApplyStationRole()
    {
        var role = _host.Settings.StationRole;
        var records = _host.Settings.Role.Records;

        // ⚠️ **标题跟着用途走**（对齐前身「一用途一标题」）：同一个可执行文件装出
        // 四种形态（设计图 `_11`–`_15`），而远程支持时第一句要问的就是
        // 「你那台是哪种用途」。用途名取 `StationRoles`（设计图原文）——
        // 与下面 `BackupRoleText` 用的是同一份来源，两处不会走岔。
        //
        // ⚠️ 版本号只取 semver 那一段（`0.2.0`）：`CurrentVersion` 后面还挂着 `+<sha>`，
        // 那 40 位十六进制塞进标题栏会把窗口名撑到没法看。完整串在「关于」页里。
        Title = $"VidLog · {StationRoles.Describe(role).Title} {_host.CurrentVersion.Split('+')[0]}";

        RecordingRoot.Visibility = records ? Visibility.Visible : Visibility.Collapsed;
        BackupRoot.Visibility = records ? Visibility.Collapsed : Visibility.Visible;

        if (records)
        {
            return;
        }

        var described = StationRoles.Describe(role);

        BackupMachineText.Text = Environment.MachineName;
        BackupRoleText.Text = $"本机用途：{described.Title} —— {described.Summary}";

        // ⚠️ 地址用 `LanAddress`（挑一块**别的设备连得上**的网卡），不用
        // `Server.BaseUrl` —— 后者是 `http://+:8720/`，那是个通配地址，
        // 显示出来没人能照着填。
        BackupAddressText.Text = LanAddress.Discover() is { } address
            ? $"{address}:{_host.Services.PlaybackPort}"
            : $"本机地址没读出来（回放端口 {_host.Services.PlaybackPort}）";

        // ⚠️ 订单联动**一个字都不许编**：它要那个至今没开工的服务端接口。
        // 图上这里是「暂无设备」，而真实原因是这边根本没有能装的东西，
        // 所以照实写原因 —— 与旁边那两颗按钮的悬停提示是同一句话。
        BackupOrderText.Text = "订单联动还没开工：要服务端先把订单接口做出来，电脑端这边没有可装的东西。";
    }

    /// <summary>
    /// 【⇄ 切换用途】—— 弹出选择窗，选了就存下来。
    /// </summary>
    /// <remarks>
    /// ⚠️ 存完之后**只提示重启，不当场换这一屏**：新用途要等下次启动
    /// 重新装配（装不装摄像头、起不起回放）才成立，当场把新形态画出来
    /// 就是在展示一个还没生效的状态。<see cref="AppHost.SwitchRoleAsync"/>
    /// 里那个弹窗已经说了「要重启软件才生效」。
    /// </remarks>
    private async void OnSwitchRole(object sender, RoutedEventArgs e) =>
        await _host.SwitchRoleAsync(this);

    /// <summary>
    /// 【网页回放】—— 用系统浏览器打开本机的回放页（设计图 `_39`）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 用 <c>localhost</c> 而**不是** <c>BaseUrl</c>：后者是通配地址，浏览器打不开。
    /// 别的设备要打开走二维码（<see cref="EnrollWindow"/>，规格 §3.4.5）。
    /// </remarks>
    private void OnOpenPlaybackPage(object sender, RoutedEventArgs e)
    {
        if (_host.Services.Server is not { } server || server.BaseUrl.Length == 0)
        {
            // ⚠️ 这一屏上**没有 `StatusText`**（它在录制台那一层里，这时是藏着的），
            // 所以失败必须用弹窗说 —— 写进一个看不见的控件等于没说。
            MessageBox.Show(
                this,
                "回放服务没起来，所以没有网页可以打开。原因多半在设置窗的「关于」那一节里。",
                "网页回放", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ShellOpen.Try($"http://localhost:{_host.Services.PlaybackPort}/") is { } error)
        {
            // I3：点了没反应的按钮是最坏的一种 —— 打不开也要说出来。
            MessageBox.Show(this, error, "网页回放", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>已经开着的那一个多画面窗口。</summary>
    /// <remarks>
    /// ⚠️ 留着它是为了**不让人开出第二个**：每个窗口最多九路 ffmpeg 取流，
    /// 点两下就是十八路，而用户眼里只是「点了两下」。
    /// </remarks>
    private MultiViewWindow? _multiView;

    /// <summary>全局热键（T15）。`Loaded` 上挂，窗口关闭时撤。</summary>
    private GlobalHotKeys? _hotKeys;

    /// <summary>
    /// 命令面板这一次匹配出来的每条候选**该做什么**（T12）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 与候选表**同一个来源、同一次生成**（见 <see cref="MatchPalette"/>）：
    /// 两者分开写的话，加一条候选却忘了加它的动作，症状是「选了没反应」——
    /// 而那正是踩坑 #13 点名的第三种。
    /// </para>
    /// <para>
    /// ⚠️ <b>面板只把「选中了哪一条」还回来，动作在这里查</b> —— 反过来让面板
    /// 直接执行，它就得知道每一个动作要开哪个窗，那是把它变成第二个主窗。
    /// </para>
    /// </remarks>
    private readonly Dictionary<PaletteCandidate, Action> _paletteRuns = [];

    /// <summary>
    /// 【实时多画面】（设计图 `_50`，规格 §3.8）—— 摆开每一台手机现在的画面。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 机位从 <see cref="DesktopServices.Live"/> 那张表来 —— 那是手机自己报到的
    /// （`/api/v1/live/announce`）。**表空不是错误**：那就是「还没有手机打开实时共享」，
    /// 窗口里会摆出几格浅底的「未接入」（T10），而那是设计图里正常的一种样子。
    /// </para>
    /// <para>
    /// ⚠️ <b>非模态</b>（<c>Show()</c> 而不是本仓其它窗口那种 <c>ShowDialog()</c>）：
    /// 这面墙是**一边干活一边看**的，模态会把它变成「要看画面就不能录单」。
    /// 代价是它自己管生命周期（<c>Closed</c> 里收掉那几路 ffmpeg），
    /// 以及上面那个「不许开第二个」的守卫。
    /// </para>
    /// <para>
    /// ⚠️ <b>机位不是开窗那一刻的快照</b>：窗口自己每两秒对一次账 —— 新报到的接上、
    /// 换了端口的指过去、不再报到的不留（见 <c>LiveWall</c>）。
    /// 从这里递进去的是「怎么取机位」与「怎么建一格」，两份**函数**。
    /// </para>
    /// </remarks>
    private void OnOpenMultiView(object sender, RoutedEventArgs e)
    {
        if (_multiView is { IsLoaded: true } already)
        {
            if (already.WindowState == WindowState.Minimized)
            {
                already.WindowState = WindowState.Normal;
            }

            already.Activate();
            return;
        }

        if (_host.Services.FfmpegPath is not { } ffmpeg)
        {
            // ⚠️ 没有 ffmpeg 就**一路画面都拉不出来**，而窗口本身会开得很正常
            // （九格「无信号输入」）—— 那看起来像「手机没开共享」，两件事分不开。
            //
            // ⚠️ 弹框之外还要**留痕**（§6.1：失败不许只说不记）：
            // 用户事后说「当时就是不出画面」时，日志里得答得上来是哪一种不出。
            _host.Logger.Log(
                VidLog.Desktop.Core.Diagnostics.LogLevel.Warn, "多画面",
                "打不开实时多画面：本机没找到 FFmpeg，一路画面都拉不起来");

            MessageBox.Show(
                this,
                "本机没找到 FFmpeg，所以一路画面都拉不起来。原因与找法在设置窗的「关于」那一节里。",
                "实时多画面", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ⚠️ 传的是**函数**不是快照，而且建格那一下才去查一次机位名：
        // 快照的话这面墙就永远停在开窗那一刻的机位上（手机每开一次共享都换端口，
        // 于是「手机在推、电脑端一直黑着」—— 2026-10-03 就是这么报上来的）。
        _multiView = new MultiViewWindow(
            _host.Services.Live.Active,
            endpoint => CreateLiveTileAsync(endpoint, ffmpeg),
            logger: _host.Logger)
        {
            Owner = this,
        };

        _multiView.Closed += (_, _) => _multiView = null;
        _multiView.Show();
    }

    // ─────────────────────────────────────────────
    // 全局热键（T15）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 挂上全局热键。<b>VidLog 不在前台时也收得到。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 选择 <c>Ctrl+Alt+…</c> 而不是 OBS 那样光秃秃的 F9/F10：`RegisterHotKey` 是
    /// <b>系统级独占</b>的，按下去前台程序收不到 —— 而工位电脑上多半同时开着表格、
    /// 浏览器、ERP，F9 在表格里是重算、F10 在老程序里是菜单栏。
    /// 带修饰键才谈得上「不打扰旁边的软件」。
    /// </para>
    /// <para>
    /// ⚠️ <b>清单里那句「开始/停止录制或推流」的「推流」在电脑端没有对应物</b>：
    /// 推流是手机在做，电脑端只是接收方。所以这里只有录制与多画面两个。
    /// </para>
    /// </remarks>
    private void AttachHotKeys()
    {
        var keys = new GlobalHotKeys(this, _host.Logger);
        _hotKeys = keys;

        // ⚠️ 多画面**两种用途下都挂**：那面墙是「看每台手机现在在拍什么」，
        // 与这台电脑录不录像无关（备份主机上也照样看）。
        keys.TryAdd(
            "实时多画面", ModifierKeys.Control | ModifierKeys.Alt, Key.M,
            () => OnOpenMultiView(this, new RoutedEventArgs()));

        // ⚠️ 录像那一个**只在本机真的录像时才挂**：备份主机那两档按下去什么动静
        // 都不会有，而一个按下去没反应的键比没有这个键更让人费解。
        if (_host.Settings.Role.Records)
        {
            keys.TryAdd(
                "开始/停止录像", ModifierKeys.Control | ModifierKeys.Alt, Key.R,
                () => OnStartOrStopWork(this, new RoutedEventArgs()));
        }
    }

    // ─────────────────────────────────────────────
    // 命令面板（T12）
    // ─────────────────────────────────────────────

    /// <summary>
    /// <c>Ctrl+K</c> —— 顺手敲两下就能到任何一个入口，不必先找那颗按钮在哪一屏。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 挂在窗的 <see cref="UIElement.PreviewKeyDown"/> 上，不在某个控件上：
    /// 焦点这会儿可能在单号框里（用户刚扫完一枪），而那个框的普通 <c>KeyDown</c>
    /// 会先把字母吃掉。
    /// </para>
    /// <para>
    /// ⚠️ <b>刻意不做成「可见的入口」</b>（按钮 / 菜单项）：清单没要求，
    /// 凭空在主窗上加一颗按钮是**改设计图**。代价是它只能靠人传人 ——
    /// 见汇报里的「已知上限」。
    /// </para>
    /// </remarks>
    private void OnPaletteShortcut(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.K || Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        // 标成已处理：不然这个 K 还会继续往焦点所在的框里走（单号框会多一个 k）。
        e.Handled = true;

        OpenPalette();
    }

    private void OpenPalette()
    {
        var palette = new CommandPaletteWindow(MatchPalette) { Owner = this };

        // ⚠️ **模态**，与其它几个入口一致：主窗这时是活的，回收站与保留期清理
        // 都在背后跑 —— 用户正盯着一条候选、底下的录像却少了，那更费解。
        palette.ShowDialog();

        if (palette.Chosen is { } chosen)
        {
            // §6.1：面板本身没有资源、没有不可逆动作，但它**替用户按了别处的按钮** ——
            // 用户事后说「我按了 Ctrl+K 选了检索，怎么没反应」时，
            // 「他到底选的是哪一条」只有这里记得下来。
            _host.Logger.Log(
                VidLog.Desktop.Core.Diagnostics.LogLevel.Info, "命令面板", $"选了：{chosen.Label}");

            RunPaletteCommand(chosen);
        }
    }

    /// <summary>
    /// 候选表。<b>每个动作就是主窗上原来那颗按钮</b>，不另写一份实现。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 每次敲字都会重来一遍：表里可能有**跟着输入变**的一条（「检索单号 X」），
    /// 而 <see cref="PaletteMatcher"/> 是纯函数，它只认递进来的这张表。
    /// </para>
    /// <para>
    /// ⚠️ 表是**手写**的、靠前的是更要紧的入口 —— 同分时
    /// <see cref="PaletteMatcher.Rank"/> 保持这个先后（见那边的注释）。
    /// </para>
    /// </remarks>
    private IReadOnlyList<PaletteCandidate> MatchPalette(string typed)
    {
        _paletteRuns.Clear();

        // ⚠️ **候选与它要做的事写在一起**，不分成两张表：分成两张的话，加一条候选
        // 却忘了加动作，症状就是「选了没反应」（踩坑 #13 点名的第三种）。
        var table = new List<(PaletteCandidate Candidate, Action Run)>
        {
            (new("search", "检索录像", "回放,查找,单号,播放"),
                () => OnOpenSearch(this, new RoutedEventArgs())),
            (new("data", "打开数据", "备份,存档,总览,统计"),
                () => OnOpenData(this, new RoutedEventArgs())),
            (new("settings", "打开设置", "配置,选项,许可,激活"),
                () => OnOpenSettings(this, new RoutedEventArgs())),
            (new("wall", "实时多画面", "九格,监控,机位"),
                () => OnOpenMultiView(this, new RoutedEventArgs())),
            (new("enroll", "连接手机", "二维码,扫码,入网"),
                () => OnEnroll(this, new RoutedEventArgs())),
        };

        var candidates = new List<PaletteCandidate>(table.Count);

        foreach (var (candidate, run) in table)
        {
            _paletteRuns[candidate] = run;
            candidates.Add(candidate);
        }

        var hits = PaletteMatcher.Rank(typed, candidates);

        // 打进去的既不是动作名、也不是关键词 —— 那多半就是个**单号**，
        // 于是补一条「去检索它」。这是清单点名要的四种用法里的第四种。
        //
        // ⚠️ **只在一条动作都没命中时才补**。不然「打」这种字（单号里的第一个字，
        // 也正好是「打开…」的第一个字）会同时冒出「打开数据」和「检索单号 打」，
        // 而后者永远不是用户想要的。
        //
        // 已知上限：单号长得**恰好**能子序列命中某个动作名时（比如某个单号里
        // 一个字不差地散落着「实、时、多、画、面」），跳单号那一条就不会出现。
        // 概率极低，代价是用户改用【回放】那颗按钮 —— 不为它加一层启发式。
        if (hits.Count == 0 && !string.IsNullOrWhiteSpace(typed))
        {
            var raw = typed.Trim();
            var jump = new PaletteCandidate(
                "waybill", $"检索单号 {WaybillNumber.Normalize(raw)} 的录像", "跳转,打开,查找");

            _paletteRuns[jump] = () => new SearchWindow(_host, raw) { Owner = this }.ShowDialog();

            return [jump];
        }

        return hits;
    }

    private void RunPaletteCommand(PaletteCandidate chosen)
    {
        // ⚠️ 拿**上一次匹配**留下的那张表来查，而 `chosen` 正是从那张表里选出来的，
        // 所以查得到（`PaletteCandidate` 是 record，比的是值）。
        //
        // 查不到只有一种可能：**这张表在选中之后又被重建过**。重建只发生在面板
        // 那一次 `MatchPalette` 调用里，而面板已经关掉了 —— 现在不会，将来也不会。
        // 真查不到就什么都不做：一个「选了没反应」比**开错窗**好解释得多。
        if (!_paletteRuns.TryGetValue(chosen, out var run))
        {
            _host.Logger.Log(
                VidLog.Desktop.Core.Diagnostics.LogLevel.Warn, "命令面板",
                $"选中了「{chosen.Label}」，但它没有对应的动作 —— 什么也没做");

            return;
        }

        run();
    }

    /// <summary>给多画面建一格：机位名从设备表来（§3.4.5），拿不到就退回设备号。</summary>
    /// <remarks>
    /// 显示一个内部号不好看，但**编一个名字更糟**。
    /// ⚠️ 查询只在**建格那一刻**做一次：机位名在这个窗里不会跟着改（改了名要重开窗口
    /// 才看得到）—— 而每一拍都查一遍设备表，代价比这一点收益大得多。
    /// </remarks>
    private async Task<LiveTile> CreateLiveTileAsync(LiveEndpoint endpoint, string ffmpeg)
    {
        var name = (await _host.Services.Devices.DevicesAsync())
            .FirstOrDefault(d => string.Equals(d.DeviceId, endpoint.DeviceId, StringComparison.Ordinal))
            .DeviceName;

        if (string.IsNullOrWhiteSpace(name)) name = endpoint.DeviceId;

        return LiveTile.Start(ffmpeg, endpoint.BaseUrl, name, logger: _host.Logger);
    }

    // ─────────────────────────────────────────────
    // 录制
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
        _clockTicker.Start();
        _previewTimer.Start();
        UpdatePreviewClock();

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

        // ── 时长兜底的询问（规格 §3.3.4）──────────────────────────────
        //
        // 语音那一半在 `App.OnNotice`（窗口多半收在托盘里，只听得到声音）；
        // **按钮这一半在这里** —— 它得有人点。
        //
        // ⚠️ 停录/结束工作时要**收掉它**：那两个按钮答的是一次已经作废的询问，
        // 留在屏幕上会让用户以为「还在等我决定」。
        switch (notice.Kind)
        {
            case CoordinatorNoticeKind.DurationPrompt:
                DurationPromptPanel.Visibility = Visibility.Visible;
                break;

            case CoordinatorNoticeKind.SegmentStopped
                or CoordinatorNoticeKind.SegmentStarted
                or CoordinatorNoticeKind.WorkStopped:
                HideDurationPrompt();
                break;

            // 发货 / 退货换了（点按钮，或拿扫码枪扫了屏幕上那张码）。
            // ⚠️ **必须重画那两张码**，不能只换标题 —— 见 `RenderScanBarcodes`。
            case CoordinatorNoticeKind.BusinessTypeChanged:
                RenderScanBarcodes();
                break;

            default:
                break;
        }

        UpdateRecordingStatus();
    }

    /// <summary>收起询问条（答完了、或者那一次询问已经作废）。</summary>
    private void HideDurationPrompt() =>
        DurationPromptPanel.Visibility = Visibility.Collapsed;

    /// <summary>点【停止】—— 规格 §3.3.4：立即停。</summary>
    /// <remarks>
    /// ⚠️ **先收起条、再转发**：转发之后会话会在下一圈收尾，
    /// 而收尾期间这一条已经没有任何意义了。
    /// </remarks>
    private void OnDurationStop(object sender, RoutedEventArgs e)
    {
        HideDurationPrompt();
        _host.Coordinator.AnswerDurationPrompt(continueRecording: false);
    }

    /// <summary>点【继续】—— 规格 §3.3.4：取消本轮上限，隔一轮再问。</summary>
    /// <remarks>
    /// ⚠️ 它与「1 分钟没人理」**不是一回事**（规格明令区分）：
    /// 点这个是**用户在场且明确要继续**，所以不会停；
    /// 没人理才是「用户不在场」⇒ 兜底生效。
    /// </remarks>
    private void OnDurationContinue(object sender, RoutedEventArgs e)
    {
        HideDurationPrompt();
        _host.Coordinator.AnswerDurationPrompt(continueRecording: true);
    }

    private void OnWaybillChanged(object sender, TextChangedEventArgs e) => RefreshStartButton();

    /// <summary>点那个 ✕：把单号清掉，重新扫一遍。</summary>
    private void OnClearWaybill(object sender, RoutedEventArgs e)
    {
        WaybillBox.Clear();
        WaybillBox.Focus();
    }

    // ─────────────────────────────────────────────
    // 发货 / 退货 + 屏幕上那两张命令条码（设计图 `_35`）
    // ─────────────────────────────────────────────

    /// <summary>点顶栏那颗按钮：发货 ⇄ 退货。</summary>
    /// <remarks>
    /// ⚠️ 点完**不在这里刷界面** —— 协调器会发一条 `BusinessTypeChanged`，
    /// 由 <see cref="OnNotice"/> 统一重画。界面上那两个入口（这颗按钮、
    /// 扫屏幕上的码）走的是同一条回程；各刷一份迟早出现「按钮变了、码没变」。
    /// </remarks>
    private void OnToggleBusinessType(object sender, RoutedEventArgs e) =>
        _host.Coordinator.ToggleBusinessType();

    /// <summary>把顶栏那个档名与右栏那两张码画成**当前档该有的样子**。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 「切换退货」那张码的载荷是 <see cref="ScanCommand.For"/>（**切到另一档**），
    /// 所以每切一次它就得重画一张 —— 两个档的码**不是同一个图形**。
    /// 只换标题不重画码的话，用户扫下去会切到与标题**相反**的那一档。
    /// </para>
    /// <para>
    /// ⚠️ 码面靠最近邻放大（XAML 里那个 `BitmapScalingMode`），与 `WizardWindow`
    /// 第 6 步那张测试条码**同一个手法**：一个模块 2 个像素，高度交给最近邻纵向拉。
    /// 换成插值会把条空边界糊成灰边，表现是「看着像条码、就是扫不出来」。
    /// </para>
    /// </remarks>
    private void RenderScanBarcodes()
    {
        var current = _host.Coordinator.CurrentBusinessType;
        var other = current == BusinessType.Return ? BusinessType.Outbound : BusinessType.Return;
        var otherName = ScanCommand.Describe(other);

        BusinessLabel.Text = ScanCommand.Describe(current);
        BusinessBarcodeTitle.Text = $"扫码切换{otherName}";
        BusinessBarcodeHint.Text = $"拿扫码枪扫下面这张码，业务类型就切到{otherName}。";

        ShowBarcode(BusinessBarcodeImage, BusinessBarcodePayload, ScanCommand.For(current));
        ShowBarcode(RecordBarcodeImage, RecordBarcodePayload, ScanCommand.StartWork);
    }

    /// <summary>把一段载荷画成 Code 128 条码，并把载荷原文写在下面。</summary>
    /// <remarks>
    /// ⚠️ 载荷照写出来**不是装饰**：码扫不动的时候（枪不认、屏幕反光、
    /// 窗口被缩得很小），用户还能照着它手敲进单号框 —— 那几条命令码本身就是
    /// 「认得出的单号形状」，手敲一样会走 <see cref="RecordingCoordinator.SubmitAsync"/>
    /// 的拦截。
    /// </remarks>
    private static void ShowBarcode(Image image, TextBlock caption, string payload)
    {
        var modules = Code128.Modules(payload);

        // 一个模块 2 个像素：落在整数边界上（最近邻不会出半像素），
        // 宽度也够 —— 一维码靠横向的明暗边界定位，印窄了读不出来。
        image.Width = modules.GetLength(0) * 2;
        image.Source = BitmapSource.Create(
            modules.GetLength(0), modules.GetLength(1), 96, 96,
            PixelFormats.Gray8, null, Code128.Pixels(modules), modules.GetLength(0));

        caption.Text = payload;
    }

    /// <summary>
    /// 顶栏那个按钮的两种形态。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>一个按钮兼两态（开始 / 停止）是照图来的</b>：设计图上顶栏只有
    /// 一个绿色「开始录制」，没有单独的停止按钮。空闲时绿底「开始录制」，
    /// 录制中红底「停止录制」—— 「现在到底在录没在录」从颜色上一眼就看得出，
    /// 那是这一版比原来两个按钮更好的地方。
    /// </para>
    /// <para>
    /// ⚠️ 摄像头取 <see cref="AppHost.Camera"/>（启动时定的那个）——
    /// 拆窗之后主窗没有摄像头下拉了，它只能靠这一个属性回答「现在有没有摄像头」。
    /// </para>
    /// </remarks>
    private void RefreshStartButton()
    {
        var recording = _host.Coordinator.CurrentWaybill is not null;
        var hasCamera = !_host.Camera.IsEmpty;
        var hasWaybill = WaybillNumber.TryParse(WaybillBox.Text, out _, out _);

        StartWorkLabel.Text = recording ? "停止录制" : "开始录制";

        // ⚠️ 底色走**样式**，不走本地值 —— 设 `Background` 等于写下一个本地值，
        // 它会压过样式里 `IsEnabled=False` 的触发器，禁用时照样满绿（见
        // `Theme.xaml` 里 `SuccessButton` 那段）。换样式没这个问题。
        StartWorkButton.Style = (Style)FindResource(recording ? "DangerButton" : "SuccessButton");

        StartWorkButton.IsEnabled = recording || hasCamera && hasWaybill;
    }

    private async void OnStartOrStopWork(object sender, RoutedEventArgs e)
    {
        if (_host.Coordinator.CurrentWaybill is not null)
        {
            await StopWorkAsync();
            return;
        }

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

    private async Task StopWorkAsync()
    {
        StartWorkButton.IsEnabled = false;

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

            // 结束时 ticker 停了 ⇒ 状态那一行不会自己回到「空闲」。
            UpdateRecordingStatus();
        }
    }

    private void UpdateRecordingStatus()
    {
        var waybill = _host.Coordinator.CurrentWaybill;

        // ⚠️ 「开始 / 停止」那个按钮的形态也要跟着走 —— ticker 只在这时跑，
        // 而录制的开始与结束都可能由**扫码枪**触发（那时没有点击事件可挂）。
        RefreshStartButton();

        NavRecordingText.Text = waybill is null ? "空闲" : $"录制中 · {waybill.Value}";
        NavRecordingText.Foreground = waybill is null
            ? (Brush)FindResource("TextSecondary")
            : (Brush)FindResource("Success");

        if (waybill is null)
        {
            return;
        }

        var elapsed = _host.Coordinator.Elapsed;
        var clock = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        RecordingStatus.Text = $"录制中 {clock} · {waybill.Value}";
    }

    /// <summary>
    /// 底部那条常驻状态栏（改造清单 T13）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 五项**全走已有的数据源**，一项都不另算：录制状态读
    /// <see cref="RecordingCoordinator"/>（与右栏同一个）、机位读
    /// <c>Live.Active()</c>（内存里的表，一秒一问不心疼）、磁盘读
    /// <see cref="StorageLocations.ActiveRoot"/> 上那个已经在用的
    /// <see cref="FormatFreeSpace"/>、许可读 <see cref="StatusSummaries"/>。
    /// <b>许可那句是完整的一句，不是另写的短句</b> —— 两句话迟早会打架，
    /// 而「右栏说已激活、状态栏说未激活」比哪一边说错都更让人不敢信这个界面。
    /// </para>
    /// <para>
    /// ⚠️ 机位的分母是**许可允许的机位数**，不是写死 9：只写「在线 2 台」看不出
    /// 「还能接几台」，而 4 机位的许可接第 5 台是会被挡下的 —— 那才是这条栏
    /// 值得一直摆在那儿的理由。许可读不出来（没配公钥）时**不编一个分母**。
    /// </para>
    /// <para>
    /// ⚠️ 磁盘读不到时**如实说读不到**，不显示 0 —— 与
    /// <see cref="FormatFreeSpace"/> 自己的口径一致（「读不到」不是「没有空间」）。
    /// </para>
    /// </remarks>
    private void UpdateStatusBar()
    {
        var waybill = _host.Coordinator.CurrentWaybill;
        var elapsed = _host.Coordinator.Elapsed;

        StatusRecordingText.Text = waybill is null
            ? "空闲"
            : $"录制中 {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        StatusRecordingText.Foreground =
            (Brush)FindResource(waybill is null ? "TextSecondary" : "Success");

        var online = _host.Services.Live.Active().Count;
        StatusSeatsText.Text = _host.Services.License?.Status.Slots is { } limit
            ? $"在线机位 {online}/{limit}"
            : $"在线机位 {online}";

        StatusDiskText.Text = "本机存档盘 " + FormatFreeSpace(_host.Services.Storage.ActiveRoot);
        StatusLicenseText.Text = StatusSummaries.License(_host);
        StatusPortText.Text = $"端口 {_host.Services.PlaybackPort}";
    }

    // ─────────────────────────────────────────────
    // 打开另外两个窗口
    // ─────────────────────────────────────────────

    /// <summary>
    /// 【设置】—— 模态弹出 <see cref="SettingsWindow"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 模态：设置里能改归档层与保留期，而这两样决定**清理会不会删东西**
    /// （规格 §3.5.1）。开着设置窗的同时让主窗还能开录，会出现
    /// 「用户以为已经改成 NAS 了、其实还没保存」的窗口期。
    /// 一次只有一个设置窗，这个窗口期就不存在。
    /// </remarks>
    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_host) { Owner = this }.ShowDialog();

        // 设置里可能改了许可状态那一类东西（激活），回来刷新一下。
        ShowStatusSummaries();
        UpdatePreviewHint();
    }

    /// <summary>
    /// 【回放】—— 模态弹出 <see cref="SearchWindow"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 模态是为了**不给同一条录像开两个 <c>MediaElement</c>**：
    /// 两个窗口同时播同一个文件会各占一个句柄，而这个文件可能正处在
    /// 保留期清理的当口。一次一个，简单且够用。
    /// </remarks>
    private void OnOpenSearch(object sender, RoutedEventArgs e) =>
        new SearchWindow(_host) { Owner = this }.ShowDialog();

    /// <summary>
    /// 【安装订单联动】—— **还没做，如实说一句**（需求方 2026-10-01 裁决）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这两颗按钮原来是**禁用**的（照先前那条「禁用 + 悬停写明原因」）。
    /// 改成可点是因为悬停提示**只有鼠标停上去才看得见** —— 触屏或者不悬停的人
    /// 永远不知道为什么点不动，只会以为程序坏了。点一下弹一句话，
    /// 谁都能看懂。踩坑 #13 禁的是**第三种**：点了没反应。
    /// <para>
    /// ⚠️ 卡的是**服务端 M6**（至今未开工），不是这边少写了几行 —— 所以话里要说清
    /// 是哪一头没到，别让用户在这台机器上白找。
    /// </para>
    /// </remarks>
    private void OnInstallOrderLink(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            this,
            "订单联动还在开发中。\n\n"
            + "它要服务端先把订单接口做出来（规格里是还没开工的 M6），"
            + "电脑端这边没有可装的东西 —— 卡在那一头，不是这台机器上缺了什么。",
            "还在开发中",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    /// <summary>【发送测试订单】—— 同上。</summary>
    private void OnSendTestOrder(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            this,
            "发送测试订单还在开发中。\n\n"
            + "它发的是订单联动那条链路上的东西，而那个服务端还没开工 —— "
            + "现在没有可发的对象。",
            "还在开发中",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    /// <summary>
    /// 【数据】—— 模态弹出 <see cref="DataWindow"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 模态：它是**只读**的，本来不必模态，但开着它的时候底下的主窗是活的 ——
    /// 那边一开录，「今日 N 件」这类数就变了，而这个窗里的数不会跟着动。
    /// 一块屏幕上摆着两份对不上的数，比多按一次【数据】麻烦得多。
    /// </remarks>
    private void OnOpenData(object sender, RoutedEventArgs e) =>
        new DataWindow(_host) { Owner = this }.ShowDialog();

    /// <summary>
    /// 【连接电脑 / 手机】—— 弹出二维码（规格 §3.4.5）。
    /// </summary>
    /// <remarks>
    /// 模态：一次只有一个。否则用户可以开出两份二维码，而
    /// <see cref="VidLog.Desktop.Core.Upload.DeviceRegistry"/> 只留得住**最后一张** ——
    /// 屏幕上摆着两张，能用的只有一张，那是最难解释的一种「扫了没反应」。
    /// </remarks>
    private void OnEnroll(object sender, RoutedEventArgs e) =>
        new EnrollWindow(_host) { Owner = this }.ShowDialog();

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
