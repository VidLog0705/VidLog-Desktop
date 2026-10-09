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

    /// <summary>上一跳还在录没有（<see cref="RecordingCoordinator.CurrentWaybill"/>）。</summary>
    /// <remarks>只为一件事：认得出「一段收了」那一刻，好把单号框清掉（见 <see cref="UpdateRecordingStatus"/>）。</remarks>
    private bool _wasRecording;

    /// <summary>上一跳还在工作没有（<see cref="RecordingCoordinator.IsWorking"/>）。</summary>
    /// <remarks>只为一件事：认得出「收工」那一刻，好把单号框清掉（见 <see cref="UpdateRecordingStatus"/>）。</remarks>
    private bool _wasWorking;

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

        // 那两处跟着协调器的当前状态重画一遍 —— 计时那一行的节拍也在里面
        // （`SyncTicker`，判据是**在工作**而不是「有当前单号」）。
        UpdateRecordingStatus();
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

            // ⚠️ 计时那一行的起停**不在这里按通知种类挑**（B2，2026-10-09）——
            // 判据在 `SyncTicker`，下面 `UpdateRecordingStatus` 每回都调它。
            // 起先写成「`WorkStarted` 起、`WorkStopped` 停」，而 `WorkStopped`
            // **有两种**：真的收工（`StopWorkAsync`，那时已经不在工作），和
            // **自己收起一段、还在工作**（时长兜底那几条，`IsWorking` 仍是 true）——
            // 后一种会把下一段的计时冻住。
            case CoordinatorNoticeKind.SegmentStopped
                or CoordinatorNoticeKind.SegmentStarted
                or CoordinatorNoticeKind.WorkStarted
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

    /// <summary>
    /// 计时那一行的一秒一跳，跟「在不在工作」对齐。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>判据是 <see cref="RecordingCoordinator.IsWorking"/>，不是「哪一条通知来了」</b>
    /// （B2，2026-10-09）。从前来工作只能点按钮 ⇒ <c>_ticker</c> 在点按钮那一刻起来；
    /// 而工作也可以由**扫码枪**起来（扫 <c>VLREC</c> 那张码进待扫、或直接扫一张面单），
    /// 那条路上它从前**一次都没起来过** —— 右栏那一行就停在 <c>00:00:00</c> 上不动。
    /// </para>
    /// <para>
    /// ⚠️ <b>它由 <see cref="UpdateRecordingStatus"/> 每回都调</b>（每一条通知、
    /// 每一次点按钮、每一跳都会走到）：单开一处、由几条挑出来的通知去调，
    /// 就是上面那个「<c>WorkStopped</c> 两种意思」的来路 —— 而这里没有哪条路能漏掉它。
    /// </para>
    /// </remarks>
    private void SyncTicker()
    {
        if (_host.Coordinator.IsWorking)
        {
            _ticker.Start();
        }
        else
        {
            _ticker.Stop();
        }
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
    /// 顶栏那个按钮此刻是哪一态（<see cref="StartButtonKind"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>一个按钮兼三态是照图来的</b>：设计图上顶栏只有一个绿色「开始录制」，
    /// 没有单独的停止按钮。空闲时绿底【开始录制】，待扫时红底【结束工作】，
    /// 录制中红底【停止录制】—— 「现在到底在录没在录」从颜色上一眼就看得出，
    /// 那是这一版比原来两个按钮更好的地方。
    /// </para>
    /// <para>
    /// ⚠️ 摄像头取 <see cref="AppHost.Camera"/>（启动时定的那个）——
    /// 拆窗之后主窗没有摄像头下拉了，它只能靠这一个属性回答「现在有没有摄像头」。
    /// </para>
    /// <para>
    /// ⚠️ <b>是「哪一态」与「能不能点」都在 <see cref="StartButton"/> 里</b>
    /// （T27② 第 4 批；B1 2026-10-09 加第三态）：那两条的次序都是有讲究的，
    /// 而这里只剩**把三个事实读出来、把态换成字与底色**。
    /// </para>
    /// <para>
    /// ⚠️ <b>「框里有没有单号」问的是 <see cref="WaybillNumber.Normalize"/>，不是
    /// <c>TryParse</c></b>：两者对空框的判断一致（归一化后为空就是没有），
    /// 而这个问法说得出「框里什么都没写」与「写了但认不出」的区别 —— 后者要
    /// **当场报错**，前者是【开始工作】。
    /// </para>
    /// </remarks>
    private void RefreshStartButton()
    {
        var coordinator = _host.Coordinator;

        var kind = StartButton.Kind(
            recording: coordinator.CurrentWaybill is not null,
            working: coordinator.IsWorking,
            hasWaybill: WaybillNumber.Normalize(WaybillBox.Text) is not null);

        StartWorkLabel.Text = kind switch
        {
            StartButtonKind.StopRecording => "停止录制",
            StartButtonKind.EndWork => "结束工作",
            _ => "开始录制",
        };

        // ⚠️ 底色走**样式**，不走本地值 —— 设 `Background` 等于写下一个本地值，
        // 它会压过样式里 `IsEnabled=False` 的触发器，禁用时照样满绿（见
        // `Theme.xaml` 里 `SuccessButton` 那段）。换样式没这个问题。
        StartWorkButton.Style = (Style)FindResource(
            kind is StartButtonKind.StopRecording or StartButtonKind.EndWork
                ? "DangerButton"
                : "SuccessButton");

        StartWorkButton.IsEnabled = StartButton.Enabled(kind, hasCamera: !_host.Camera.IsEmpty);
    }

    private async void OnStartOrStopWork(object sender, RoutedEventArgs e)
    {
        var coordinator = _host.Coordinator;

        // ⚠️ **这里必须重新问一次 `StartButton.Kind`，不能另写一份条件**：
        // 各写一份的话，迟早出现「按钮上写着【结束工作】、按下去在开录」——
        // 而那一刻用户在等着它停。
        var typed = WaybillNumber.Normalize(WaybillBox.Text);
        var kind = StartButton.Kind(
            recording: coordinator.CurrentWaybill is not null,
            working: coordinator.IsWorking,
            hasWaybill: typed is not null);

        // 【停止录制】/【结束工作】—— 两颗都落到同一个结束工作（第三态是新加的：
        // 待扫态从前没有出口，见 `StartButtonKind`）。
        if (kind is StartButtonKind.StopRecording or StartButtonKind.EndWork)
        {
            await StopWorkAsync();
            return;
        }

        WaybillNumber.TryParse(typed, out var waybill, out var error);

        // ⚠️ 框里**写了东西但认不出**要当场说（踩坑 #13：点了没反应最难受）。
        // 而框里**什么都没写**不是错 —— 那是【开始工作】：只把工作开起来，
        // 相机交给取景识码，等扫到面单再开录（扫 VLREC 那张码做的是同一件事）。
        if (typed is not null && waybill is null)
        {
            RecordingStatus.Text = $"单号不能用：{error}";
            return;
        }

        StartWorkButton.IsEnabled = false;

        try
        {
            coordinator.StartWork();

            if (waybill is not null)
            {
                await coordinator.SubmitAsync(waybill, PunchSource.ManualEntry);
            }
            else if (!coordinator.IsWorking)
            {
                // 被校时 / 许可那两道闸拦下了（`StartWork` 里那两道）。
                // 原因已经由 `FinalizeFailed` 通知写进「本机录制动态」并且播报过一次，
                // 这里在**他眼睛正看着的那一行**再指一次路 —— 不然屏幕上只剩一个
                // 「空闲」，看起来就像程序没反应。
                RecordingStatus.Text = "没能开始工作 —— 原因见右边「本机录制动态」。";
            }

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
            // ⚠️ `_ticker` 不在这里停：计时那一行的一秒一跳跟着「在不在工作」走
            // （`SyncTicker`），而下面那次 `UpdateRecordingStatus` 会把它对上。
            var outcome = await _host.Coordinator.StopWorkAsync();

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

    /// <summary>
    /// 没在录的时候那半句状态词。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>右栏那块与底栏共用这一份</b>（B2，2026-10-09）：两处各写一份，
    /// 迟早出现「上面说空闲、下面说待扫」—— 而那两个词在这里是同一件事的两个说法。
    /// 判据是 <see cref="RecordingCoordinator.IsWorking"/>：工作上、还没扫到面单，
    /// 那就是**待扫**，不是空闲（相机开着、取景框活着、扫码枪随时能扫）。
    /// </remarks>
    private string IdleWord() => _host.Coordinator.IsWorking ? "待扫" : "空闲";

    private void UpdateRecordingStatus()
    {
        var coordinator = _host.Coordinator;
        var waybill = coordinator.CurrentWaybill;

        // ⚠️ **一段收了、或者收工了，就把单号框清掉**（2026-10-09 需求方拍的，
        // 照 PackingProof 的做法 —— 它那个框是「下一单的输入」，被消费掉就不再留着，
        // 见 `Scanner.cs` 里那几处 `ScanInputText = ""`）。
        //
        // 不清的话，待扫态里那个框会留着**上一段的单号**，于是顶栏那颗按钮
        // 按下去是「用这个单号再录一段」而不是【结束工作】—— 同一个界面上
        // 两句话有歧义。清掉之后：**待扫态里框总是空的**，那一颗永远是【结束工作】；
        // 而「同一单号再录一段」走重扫一遍（或者记录列表里那条重录）。
        //
        // ⚠️ 判据写成**两件事各自的迁移**（上一跳还在录 / 上一跳还在工作，这一跳都不是了），
        // 不是「哪条通知来了」：收段的路不止一条（点【停止录制】、扫到同一个单号、
        // 时长兜底自己收、收尾失败……），按通知挑迟早漏一条 —— 而漏掉的那条
        // 正好会把歧义放回界面上。
        //
        // ⚠️ 它排在下面两颗**之前**：那颗按钮要不要按得动，问的就是框里有没有单号
        // （`StartButton.Kind`）—— 框没落定就先问，问到的是一句过期的话。
        if (_wasRecording && waybill is null)
        {
            WaybillBox.Clear();
        }

        if (_wasWorking && !coordinator.IsWorking)
        {
            WaybillBox.Clear();
        }

        _wasRecording = waybill is not null;
        _wasWorking = coordinator.IsWorking;

        // ⚠️ 「开始 / 结束」那个按钮的形态也要跟着走 —— 这一行每次重画都得重画它，
        // 因为录制的开始与结束都可能由**扫码枪**触发（那时没有点击事件可挂）。
        RefreshStartButton();
        SyncTicker();

        NavRecordingText.Text = waybill is null ? IdleWord() : $"录制中 · {waybill.Value}";
        NavRecordingText.SetResourceReference(
            TextBlock.ForegroundProperty, waybill is null ? "TextSecondary" : "Success");

        if (waybill is not null)
        {
            RecordingStatus.Text = $"录制中 {Display.Timer(coordinator.Elapsed)} · {waybill.Value}";
            return;
        }

        // ⚠️ <b>待扫态必须**改掉**这一行</b>（B2，2026-10-09 需求方实测到的自相矛盾）：
        // 它从前在「没有单号」时**直接 return**，于是这一行**冻在上一段的
        // 「录制中 00:00:32 · SF122…」上不动** —— 而左边那个大字同时写着「空闲」。
        // 同一个框里摆着两句打架的话，比哪一句错都更让人不敢信这个界面。
        if (coordinator.IsWorking)
        {
            RecordingStatus.Text = "待扫 · 扫到面单就开录。";
        }

        // ⚠️ **没在工作时一个字都不写**（有意不写）：这一行还是那句结果话的家 ——
        // 「已入库 3 段。」「收尾失败：…」都写在这儿，擦成「空闲」等于把用户
        // 刚做完那件事的回执吃掉。
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
            ? IdleWord()
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
