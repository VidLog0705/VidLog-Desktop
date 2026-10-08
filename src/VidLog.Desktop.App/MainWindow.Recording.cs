using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的命名空间都带进来，于是 MessageBox 这类
// 同名类型变成「不明确」。这里用**别名钉死成 WPF 的那套** ——
// 这个文件里的控件全是 WPF 的，WinForms 一个都不该出现。
// ⚠️ 这里原来还举了 `Brush` 当例子：2026-10-07 那两处颜色改成
// `SetResourceReference` 之后本文件不再出现 `Brush`，那一行别名已删。
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
    /// <para>
    /// ⚠️ 「能不能点」那个判断在 <see cref="StartButton.Enabled"/>（T27② 第 4 批）：
    /// 那一串的**运算次序**是有讲究的（录制中一律能点），而这里只剩
    /// **把三个事实读出来** —— 字与底色是文案/资源名，两样都留在这边。
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

        StartWorkButton.IsEnabled = StartButton.Enabled(recording, hasCamera, hasWaybill);
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
        NavRecordingText.SetResourceReference(
            TextBlock.ForegroundProperty, waybill is null ? "TextSecondary" : "Success");

        if (waybill is null)
        {
            return;
        }

        var elapsed = _host.Coordinator.Elapsed;

        RecordingStatus.Text = $"录制中 {Display.Timer(elapsed)} · {waybill.Value}";
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
            : $"录制中 {Display.Timer(elapsed)}";
        StatusRecordingText.SetResourceReference(
            TextBlock.ForegroundProperty, waybill is null ? "TextSecondary" : "Success");

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
            + "它要服务端先把订单接口做出来（那一头还没开工），"
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

}
