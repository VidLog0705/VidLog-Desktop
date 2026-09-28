using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的命名空间都带进来，于是 MessageBox / Brush 这类
// 同名类型变成「不明确」。这里用**别名钉死成 WPF 的那套** ——
// 这个文件里的控件与画笔全是 WPF 的，WinForms 一个都不该出现。
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using VidLog.Desktop.App.Platform;
using VidLog.Desktop.Core;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Recording;
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

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        // 界面上「已录 / 已存」只能靠定时刷新 —— 协调器不推送进度，Elapsed 是拉取式的。
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _ticker.Tick += (_, _) => UpdateRecordingStatus();

        _clockTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTicker.Tick += (_, _) => UpdatePreviewClock();

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
        Closed += (_, _) =>
        {
            _ticker.Stop();
            _clockTicker.Stop();
            _renameWatch.Stop();
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

        ShowStatusSummaries();

        // 单号框**一直可用**：手动输入单号这件事与摄像头在不在一点关系都没有。
        // （拆窗之前它是跟着摄像头枚举一起解禁的 —— 那是个耦合，顺手拆掉。）
        WaybillBox.IsEnabled = true;
        RefreshStartButton();

        UpdatePreviewClock();
        UpdatePreviewHint();
        _clockTicker.Start();

        StatusText.Text = _host.Services.Server?.BaseUrl is { Length: > 0 } url
            ? $"服务已就绪。回放地址：{url}"
            : "服务已就绪。回放服务未启动。";

        // 上次没走完的录像收回来没有（规格 §3.1.1）。**必须说出来** ——
        // 「悄悄收好了」和「其实什么都没收」在界面上长得一模一样，用户无从分辨。
        // 收尾失败的会由 StartupReport.Warnings 走「需要注意」那一块，不在这里重复。
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

        UpdateRecordingStatus();

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
    /// 预览区中央那行说明。
    /// </summary>
    /// <remarks>
    /// ⚠️ **必须说清楚为什么没有画面**。设计图上那儿是一路真画面，而这一版不是 ——
    /// 一个纯黑的框与「相机坏了」「程序卡住了」「就这样设计的」三种情况长得一模一样。
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

        PreviewHintText.Text =
            "取景画面还没接上：相机是独占设备，录制中要再取一路画面得先真机验一次。"
            + "录制本身不受影响。";
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
                + "· 清理前会逐条回查归档层，查不到或查不了的那条不会删；\n"
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
            var root = _host.Services.Layout.ArchiveRoot;

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
            OvLibraryText.Text = "正在统计…";
            var footprint = await Task.Run(() => LibraryFootprintProbe.Measure(root));

            // ⚠️ 读不到的位置**必须说出来**：不说的话那个字节数是**静默偏小**的，
            // 而用户会拿它判断「盘还够用」。
            OvLibraryText.Text = footprint.UnreadableCount == 0
                ? $"{FormatBytes(footprint.TotalBytes)} · {footprint.FileCount} 个文件"
                : $"{FormatBytes(footprint.TotalBytes)} · {footprint.FileCount} 个文件"
                  + $"（另有 {footprint.UnreadableCount} 处读不到，实际只会更多）";

            OvUpdatedText.Text = $"统计于 {DateTime.Now:HH:mm:ss}";

            // ── ② 状态 ────────────────────────────────────────────────
            //
            // ⚠️ 读的是 `AppHost.DeviceName`（**启动时**定下来的那个），
            // 不是设置里用户刚选的那个 —— 摄像头改了要重启才生效。
            OvCameraText.Text = _host.Services.FfmpegPath is null
                ? "没有 FFmpeg，无法采集"
                : _host.DeviceName is { Length: > 0 } device
                    ? device
                    : "没有找到摄像头";

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

            OvDiskText.Text = FormatFreeSpace(root);

            var devices = await _host.Services.Devices.DevicesAsync();
            OvDevicesText.Text = devices.Count == 0 ? "还没有手机接进来" : $"{devices.Count} 台";

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

    /// <summary>把字节数写成人看的大小。</summary>
    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

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
            return $"可用 {FormatBytes(new DriveSpaceProbe().GetFreeBytes(path))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"读不到可用空间（{ex.Message}）";
        }
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
    /// ⚠️ 摄像头取 <see cref="AppHost.DeviceName"/>（启动时定的那个）——
    /// 拆窗之后主窗没有摄像头下拉了，它只能靠这一个属性回答「现在有没有摄像头」。
    /// </para>
    /// </remarks>
    private void RefreshStartButton()
    {
        var recording = _host.Coordinator.CurrentWaybill is not null;
        var hasCamera = _host.DeviceName.Length > 0;
        var hasWaybill = WaybillNumber.TryParse(WaybillBox.Text, out _, out _);

        StartWorkLabel.Text = recording ? "停止录制" : "开始录制";
        StartWorkButton.Background = (Brush)FindResource(recording ? "Danger" : "Success");
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
        _clockTicker.Stop();
        Hide();

        _host.Tray?.Notify("VidLog 还在后台", "扫码枪照常可用。要退出请右键托盘图标。");
    }
}
