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
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Live;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Rendering;
using VidLog.Desktop.Core.Scanning;
using VidLog.Desktop.Core.Search;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.App;

public partial class MainWindow : Window
{
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

}
