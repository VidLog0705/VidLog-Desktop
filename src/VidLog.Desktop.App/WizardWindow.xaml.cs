using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的命名空间都带进来，于是 MessageBox / RadioButton
// 这类同名类型变成「不明确」。这里用**别名钉死成 WPF 的那套**（与拆窗那几个文件同一条口径）。
// ⚠️ 原来这儿还举了 `Brush` 当例子、也真钉了一行：2026-10-07 那四个笔刷字段删掉
// （改成 `Tint` 走 `SetResourceReference`）之后本文件不再出现 `Brush`，那行别名已删。
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using PixelFormats = System.Windows.Media.PixelFormats;
using RadioButton = System.Windows.Controls.RadioButton;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;

using VidLog.Desktop.Core.Camera;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Rendering;

namespace VidLog.Desktop.App;

/// <summary>
/// 配置向导 7 步（照需求方设计图 `_16`–`_34`）。
/// </summary>
/// <remarks>
/// <para>
/// 版式在 <c>WizardWindow.xaml</c>，这里管**每一步要做的事**。三件事值得先读：
/// </para>
/// <list type="number">
/// <item><b>相机是独占的</b>。预览（步 2 / 步 3）与性能检测（步 4）都要开相机，
/// 所以**同一时刻只能有一个**：进步 4 之前必须先把预览放掉，
/// 否则检测会拿到 <c>device already in use</c> 并把一个能用的组合**误判成跑不通**。</item>
/// <item><b>步 6 的测试框是一个普通输入框</b>。扫码枪的字符不被拦截
/// （<c>KeyboardScanBridge</c> 只吞结束符那一个键），所以它打进来的字
/// 自然落进焦点所在的那个框里 —— 不需要任何新管路。判定靠 <c>TextChanged</c>
/// 看框里的内容，**不靠回车**，所以结束符被吞不影响这一步。</item>
/// <item><b>改了什么、什么时候生效</b>：向导是**改设置**的，
/// 而设置各有各的生效时机（有的立即、有的下次开始工作、有的要重启）。
/// 见 <see cref="FinishAsync"/> 末尾那段说明。</item>
/// </list>
/// </remarks>
public partial class WizardWindow : Window
{
    private const int LastStep = 6;

    // ⚠️ 这两个数**只有一份**，在 Core 里（`WizardPlan`）—— 取景那两条规则要按步号
    // 判断（只有摄像头步与识码步取景），两边各写一个 1 的话，改了一处就是**静默**错。
    private const int CameraStep = WizardPlan.CameraStep;
    private const int RecognitionStep = WizardPlan.RecognitionStep;

    private const int PerformanceStep = 3;
    private const int MicrophoneStep = 4;

    /// <summary>
    /// 步 6 那张测试条码的载荷 —— **逐字照图**（`_33`）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它**长得就像一个真单号**，而 `WaybillNumber.TryParse` 只要求「归一化后非空」
    /// ⇒ 扫它会被全局钩子当成一次真扫码，进而**开录**（`WorkModePolicy.OnScan`
    /// 在没开录时返回 <c>StartSegment</c>）。所以向导活着的时候，
    /// 那条路要被 <see cref="AppHost.SuspendScans"/> 挡掉 —— 见那里的说明。
    /// </remarks>
    private const string TestBarcode = "TEST20260928181639";

    /// <summary>隔几帧喂一次 ZXing（预览 12 fps，识码不需要每帧都做）。</summary>
    private const int DecodeEveryNthFrame = 3;

    /// <summary>开始预览之后多久还没有帧，就把 ffmpeg 说的那句话贴出来。</summary>
    private const int StallReportMs = 3000;

    private readonly AppHost _host;
    private readonly IAppLogger _logger;
    private readonly IFrameScanner _decoder = new ZXingFrameScanner();

    // 预览那一档（步 2 / 步 3 共用一路进程，见 EnsurePreviewAsync）
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private PreviewProcess? _preview;
    private SingleSlotPreviewSink? _sink;
    private WriteableBitmap? _frameBitmap;
    private string? _previewKey;
    private long _previewStartedAtMs;
    private bool _gotFrame;
    private int _frameTick;
    private string? _lastDecoded;

    // 麦克风那一档（步 5）
    private readonly SemaphoreSlim _micGate = new(1, 1);
    private readonly DispatcherTimer _micTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private MicrophoneLevelMonitor? _mic;

    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _work;

    private CameraRotation _rotation = CameraRotation.None;

    /// <summary>
    /// 网络摄像头那一档**连上过没有**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 开着它当预览的条件：图上第 2 步的网络那一档写着
    /// 「请**输入**网络摄像头地址，**然后点击测试连接**」——
    /// 也就是说地址框一改，预览就该退回去等测试，而不是拿一个没验过的地址
    /// 反复开 ffmpeg（那会白等十秒一次，而且失败原因是英文的）。
    /// </remarks>
    private bool _networkVerified;

    private bool _perfRunning;
    private bool _ready;
    private bool _closingNow;
    private int _step;

    // ⚠️ 这里原来有四个 `readonly Brush` 字段（`_warning` / `_textPrimary` /
    // `_textSecondary` / `_success`），构造里 `FindResource` 取一次就焊死。
    // 2026-10-07 全删掉，改在用的地方走 `SetResourceReference`：取一次的做法在
    // 「开机就是暗色」时没问题，但用户**开着程序去改 Windows 主题**时，已经建好的
    // 控件会一直停在亮色 —— 换字典换不掉已经赋给 DP 的那支笔刷（`Brush` 是冻结的）。

    public WizardWindow(AppHost host)
    {
        _host = host;
        _logger = host.Logger;

        InitializeComponent();

        _previewTimer.Tick += OnPreviewTick;
        _micTimer.Tick += OnMicrophoneTick;

        _rotation = host.Settings.Rotation;

        ModeContinuous.IsChecked = host.Settings.Mode == WorkMode.Continuous;
        ModeSameWaybill.IsChecked = host.Settings.Mode != WorkMode.Continuous;
        // ⚠️ 摄像头识别默认开着（规格 §3.2.1 的第二种入口），
        // 而老设置文件里没有这个字段 ⇒ 反序列化取 true，升级后行为不变。
        RecognitionOn.IsChecked = host.Settings.CameraRecognition;
        RecognitionOff.IsChecked = !host.Settings.CameraRecognition;

        NetworkUrlBox.Text = host.Settings.CameraNetworkUrl ?? string.Empty;
        ShowRotation();
        ShowBarcode();

        Loaded += OnLoadedAsync;
    }

    /// <summary>用户走完了最后一步（【完成】并且保存成功）。</summary>
    /// <remarks>
    /// ⚠️ 不用 <c>DialogResult</c>：关窗要先把相机放掉（见 <see cref="OnClosing"/>），
    /// 而那一条路会 <c>e.Cancel = true</c> —— 两者搅在一起时
    /// <c>DialogResult</c> 会被那次取消吃掉。一个 bool 更直白。
    /// </remarks>
    public bool Completed { get; private set; }

    // ─────────────────────────────────────────────
    // 启动与收尾
    // ─────────────────────────────────────────────

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        // 装钩子时那条路会**开录**（见 TestBarcode 的说明）——
        // 向导活着期间，扫码只当测试，不当事。
        _host.SuspendScans = true;

        await LoadCamerasAsync();
        await LoadMicrophonesAsync();

        _ready = true;
        ShowStep(0);
    }

    /// <summary>Esc 关掉向导 —— 与其余几个对话框同一条约定（见 <c>每个对话框都按_Esc_关得掉</c> 那条绊线）。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ Esc 是**放弃**，不是走完：`Completed` 不动，所以调用方仍然按「没配完」处理。
    /// 而**已经点过【下一步】的那几步设置是存了的**（`SaveSettingsAsync` 逐步落盘），
    /// 这里不做回滚 —— 回滚要在打开向导时先快照整份设置，为一条 Esc 不值当。
    /// </para>
    /// <para>
    /// ⚠️ 只调 <see cref="Window.Close"/>，**不要在这里自己放相机 / 取消** ——
    /// 那条路全在 <see cref="OnClosing"/> 里，而且它会把这次关闭取消掉再异步收尾
    /// （它上面那一大段说明了为什么不能绕开）。按 X 和按 Esc 走的是同一条。
    /// </para>
    /// <para>
    /// ⚠️ 挂窗口级的 <see cref="UIElement.PreviewKeyDown"/>（隧道）：焦点这会儿可能在
    /// 步 6 那个测试框里（用户刚拿扫码枪打了一枪），普通 <c>KeyDown</c> 收不到。
    /// </para>
    /// </remarks>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        Close();
    }

    /// <summary>
    /// 关窗之前把设备放干净。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>必须等进程真的退出</b>：相机是独占的，没释放干净的话紧接着的录制
    /// 会拿到 <c>device already in use</c>。而向导是从设置窗弹出来的模态窗，
    /// 用户关掉它之后**下一步动作很可能就是按【开始工作】**。
    /// </para>
    /// <para>
    /// 所以这里取消掉这一次关闭、异步收完尾再真关 —— 而不是在
    /// <c>Closed</c> 里 fire-and-forget（那会留下一个「相机还没放开就开录」的窗口）。
    /// </para>
    /// </remarks>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingNow)
        {
            return;
        }

        e.Cancel = true;
        _closingNow = true;

        try
        {
            _work?.Cancel();
            await StopPreviewAsync();
            await StopMicrophoneAsync();
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warn, "向导", $"收尾时出错（照常关窗）：{ex.Message}");
        }
        finally
        {
            _host.SuspendScans = false;
            _closing.Dispose();

            // ⚠️ **不能在这里直接 `Close()`。** 此刻还在 `Closing` 的事件栈里 ——
            // `e.Cancel = true` 之后 WPF 还没从这个事件返回，`_isClosing` 仍是 true，
            // 而 `Close()` 会重入 `InternalClose` → `VerifyNotClosing()` 抛
            // `InvalidOperationException`。异常从 `async void` 里逃出去 → 没人接 →
            // 崩溃处理器把整个进程收掉（**用户看到的是「关个向导程序没了」**）。
            //
            // 之所以一直没被发现：**只有两个 `await` 都同步完成时才会同步走到这里**。
            // 预览或麦克风在跑时 `await` 真的让出，异常就变成「稍后抛」而不在这条栈上。
            // 而没相机/没麦克风的机器（本机就是）恰好两条都同步完成 ——
            // 2026-10-02 实测：走完【完成】崩一次、打开后直接按 X 再崩一次。
            //
            // 丢回消息队列，等这一轮派发走完（`_isClosing` 复位）再关。
            // 第二次进来时 `_closingNow` 已经为 true，那道门直接放行。
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    // ─────────────────────────────────────────────
    // 步骤切换
    // ─────────────────────────────────────────────

    private void OnStepChecked(object sender, RoutedEventArgs e)
    {
        // ⚠️ `IsChecked="True"` 是在 InitializeComponent 里解析到就立刻响的，
        // 而那一刻后面的面板**还没建出来**（null）。
        if (!_ready)
        {
            return;
        }

        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var index))
        {
            ShowStep(index);
        }
    }

    private void ShowStep(int index)
    {
        _step = index;

        var panels = new[]
        {
            Step0Panel, Step1Panel, Step2Panel, Step3Panel, Step4Panel, Step5Panel, Step6Panel,
        };

        for (var i = 0; i < panels.Length; i++)
        {
            panels[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        var navs = new[] { Nav0, Nav1, Nav2, Nav3, Nav4, Nav5, Nav6 };

        for (var i = 0; i < navs.Length; i++)
        {
            // 判一次才赋：赋值会再响一次 Checked，白跑一趟（而且看着像递归）。
            if (navs[i].IsChecked != (i == index))
            {
                navs[i].IsChecked = i == index;
            }
        }

        BackButton.IsEnabled = index > 0;
        NextButton.Content = index == LastStep ? "完成" : "下一步";

        _ = EnterStepAsync(index);
    }

    private async Task EnterStepAsync(int index)
    {
        try
        {
            if (index != PerformanceStep && _perfRunning)
            {
                // 离开这一步就别再探了：探测要**真开一次相机**，
                // 让它跑完会占住下一步的预览。
                _work?.Cancel();
            }

            // ⚠️ 顺序是承重的：先把预览按需要停/起，再决定麦克风 ——
            // 而性能检测**必须在预览停掉之后**才跑（相机独占）。
            await EnsurePreviewAsync();
            await EnsureMicrophoneAsync();

            if (index == PerformanceStep)
            {
                await RunPerformanceAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // 关窗或换步，正常。
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, "向导", $"进第 {index + 1} 步时出错：{ex.Message}");
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
        {
            ShowStep(_step - 1);
        }
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step < LastStep)
        {
            ShowStep(_step + 1);
            return;
        }

        await FinishAsync();
    }

    // ─────────────────────────────────────────────
    // 待保存的设置（向导全程**不写盘**，【完成】才写）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 界面上现在这一份设置。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>每次现算，不维护一份 <c>_pending</c> 字段</b>：那种字段与控件状态
    /// 是一对会走岔的东西（改了一个忘了同步另一个），而走岔的表现是
    /// 「界面上选的是 A，存下去的是 B」—— 静默、且用户无法自查。
    /// </para>
    /// <para>
    /// 并进去的规矩（换种类保留另一边的值、麦克风不选时保持原值）在
    /// <see cref="WizardPlan.Build"/> 那头，这里只读控件。
    /// </para>
    /// </remarks>
    private AppSettings Pending() => WizardPlan.Build(_host.Settings, Draft());

    /// <summary>界面上读出来的那一份草稿 —— 一行一个控件。</summary>
    private WizardDraft Draft() => new()
    {
        Continuous = ModeContinuous.IsChecked == true,
        Camera = SelectedSource(),
        Recognition = RecognitionOn.IsChecked == true,
        Rotation = _rotation,
        Microphone = SelectedMicrophone(),
    };

    // ─────────────────────────────────────────────
    // 步 2 · 选择摄像头
    // ─────────────────────────────────────────────

    /// <summary>摄像头下拉里的一项。<paramref name="DeviceName"/> 为 null = 网络那一档。</summary>
    private sealed record CameraChoice(string? DeviceName);

    private CameraSource SelectedSource()
    {
        if (CameraCombo.SelectedItem is not ComboBoxItem { Tag: CameraChoice choice })
        {
            return CameraSource.None;
        }

        return choice.DeviceName is null
            ? CameraSource.Network(NetworkUrlBox.Text)
            : CameraSource.Local(choice.DeviceName);
    }

    private async Task LoadCamerasAsync()
    {
        CameraCombo.Items.Clear();

        if (_host.Services.FfmpegPath is { } ffmpegPath)
        {
            // ⚠️ logger 要传：枚举那几个构造点的 logger 是**可选**参数，
            // 不传就静默不落盘（2026-09-29 审计出来的那类缺口）。
            var devices = await DshowDevices.ListVideoAsync(ffmpegPath, _logger, _closing.Token);

            for (var i = 0; i < devices.Count; i++)
            {
                CameraCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"[{i}] {devices[i]}",
                    Tag = new CameraChoice(devices[i]),
                });
            }
        }

        // 「网络摄像头（手动地址）」**永远在最后**（照图 `_17`）。
        CameraCombo.Items.Add(new ComboBoxItem
        {
            Content = "网络摄像头（手动地址）",
            Tag = new CameraChoice(null),
        });

        CameraCombo.IsEnabled = true;

        var remembered = _host.Settings.Camera;

        // 当前用的是网络那一档时，选中的就是它（照图 `_18`）。
        var target = remembered.IsNetwork
            ? CameraCombo.Items.Count - 1
            : IndexOfDevice(remembered.Address);

        CameraCombo.SelectedIndex = target >= 0 ? target : 0;
        ApplyCameraKind();
    }

    private int IndexOfDevice(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return -1;
        }

        for (var i = 0; i < CameraCombo.Items.Count; i++)
        {
            if (CameraCombo.Items[i] is ComboBoxItem { Tag: CameraChoice { DeviceName: { } name } }
                && string.Equals(name, deviceName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 按当前选中的种类，显示/藏起地址框与【测试连接】，并写上第 3 步那一行的来源名。
    /// </summary>
    /// <remarks>
    /// ⚠️ 来源名在这里写、**不在 <see cref="OnCameraChanged"/> 里写**：
    /// 那个处理器在装填下拉时会被触发一次，而那时 `_ready` 还是 false ——
    /// 它直接返回，名字就永远空着（2026-09-30 实测：第 3 步预览左下角一直是空的，
    /// 而用户没碰过下拉的话根本不会补上）。
    /// </remarks>
    private void ApplyCameraKind()
    {
        var source = SelectedSource();

        NetworkUrlBox.Visibility = source.IsNetwork ? Visibility.Visible : Visibility.Collapsed;
        NetworkUrlHint.Visibility = source.IsNetwork ? Visibility.Visible : Visibility.Collapsed;
        TestConnectionButton.Visibility = source.IsNetwork ? Visibility.Visible : Visibility.Collapsed;

        // 走 Display（凭据已抹掉），不是 Address —— 那一行会画在屏幕上给人看。
        RecognitionDeviceLabel.Text = source.IsEmpty ? string.Empty : source.Display;
    }

    private void OnCameraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        // 换了来源，「验过没有」就作废了 —— 不重置的话，换成网络那一档时
        // 会拿着上一路验过的结论去开这一路的预览。
        _networkVerified = false;
        ApplyCameraKind();
        _ = EnsurePreviewAsync();
    }

    private void OnNetworkUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        // 地址一动，上次那次「测试连接」的结论就作废了。
        _networkVerified = false;
        _ = EnsurePreviewAsync();
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        var source = SelectedSource();

        TestConnectionButton.IsEnabled = false;
        SetOverlayHint(source.ConfigurationProblem ?? "正在连接…");

        try
        {
            // ⚠️ 先放掉预览再测：同一路网络源同时开两个 ffmpeg 不会报「占用」，
            // 但那两个进程会互相抢带宽，测出来的画面不说明问题。
            await StopPreviewAsync();

            if (_host.Services.FfmpegPath is not { } ffmpegPath)
            {
                SetOverlayHint("没有找到 FFmpeg，测不了。");
                return;
            }

            var info = await new FfmpegNetworkCameraProbe(
                    ffmpegPath, new SystemProcessRunner(_logger))
                .InspectAsync(source, _closing.Token);

            if (!info.Connected)
            {
                // ⚠️ 日志走 **Identity**（凭据已抹掉）—— 地址里是用户自己的摄像头密码。
                _logger.Log(LogLevel.Warn, "向导", $"测试连接失败（{source.Identity}）：{info.FailureReason}");
                SetOverlayHint(info.FailureReason ?? "连不上。");
                return;
            }

            _networkVerified = true;

            // 「连上了但读不出尺寸」是**正常的一种结果**，不判失败（见 NetworkStreamInfo）。
            var size = info.SizeKnown ? $"{info.Width}×{info.Height}" : "尺寸没读出来";
            var codec = string.IsNullOrWhiteSpace(info.VideoCodec) ? string.Empty : $" {info.VideoCodec}";
            var audio = info.HasAudio ? "、带音轨" : string.Empty;

            _logger.Log(LogLevel.Info, "向导", $"测试连接成功（{source.Identity}）：{size}{codec}{audio}");

            await EnsurePreviewAsync();
        }
        catch (OperationCanceledException)
        {
            // 关窗了。
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, "向导", $"测试连接出错：{ex.Message}");
            SetOverlayHint($"测试连接出错：{ex.Message}");
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 转方向：**一档一档转**（照图那颗胶囊按钮）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 图上那颗按钮写的是「旋转 180°」，而需求方 2026-09-29 裁定
    /// **要完整四档**（不转 / 转 180° / 左转 90° / 右转 90°）。
    /// 两件事在**同一个控件**上不冲突：不转时它印的就是图上的「旋转 180°」
    /// （按一下的去处也正是 180°），其余三档印那一档的名字。
    /// 名字一律走 <see cref="RecordingSpec.RotationLabel"/>——**唯一一处产出**，
    /// 不在这一层写死（写死的话四档迟早有一处印成「180度」这种形状）。
    /// </remarks>
    private void OnCycleRotation(object sender, RoutedEventArgs e)
    {
        _rotation = _rotation switch
        {
            CameraRotation.None => CameraRotation.UpsideDown,
            CameraRotation.UpsideDown => CameraRotation.Left90,
            CameraRotation.Left90 => CameraRotation.Right90,
            _ => CameraRotation.None,
        };

        ShowRotation();

        // ⚠️ 方向是**烘焙进 ffmpeg 参数**的（输入侧滤镜），改了必须重开预览 ——
        // 不重开的话用户看着的是旧朝向的画面，而他在这一步正是靠画面判断装正没有。
        _ = EnsurePreviewAsync();
    }

    private void ShowRotation()
    {
        var label = new RecordingSpec(
            _host.Settings.Codec, _host.Settings.Resolution, _rotation).RotationLabel;

        // 照图：不转时那颗按钮印的就是「旋转 180°」—— 而按一下的去处也正是 180°。
        RotationButton.Content = _rotation == CameraRotation.None ? "旋转 180°" : label;

        RotationButton.ToolTip =
            $"每点一次换一档（当前：{label}）："
            + "不转 → 转 180° → 左转 90° → 右转 90° → 不转。"
            + "方向不参与规格回落 —— 无论这台电脑能编哪一档，方向都按这里选的拍。";
    }

    // ─────────────────────────────────────────────
    // 预览（步 2 / 步 3）
    // ─────────────────────────────────────────────

    /// <summary>当前这一步要不要预览（规则在 <see cref="WizardPlan.NeedsPreview"/>）。</summary>
    private bool NeedsPreview() =>
        WizardPlan.NeedsPreview(_step, RecognitionOn.IsChecked == true, SourceReady());

    /// <summary>
    /// 这一路现在**能不能**取景（规则在 <see cref="WizardPlan.SourceReady"/>：
    /// 网络那一档在「测试连接」成功之前不取景，照图 `_18`）。
    /// </summary>
    private bool SourceReady() => WizardPlan.SourceReady(SelectedSource(), _networkVerified);

    /// <summary>正在跑的那一路预览是给谁跑的 —— 变了就要重开。</summary>
    /// <remarks>
    /// ⚠️ 键怎么拼（用 <c>Identity</c> 不用 <c>Address</c>，地址里有密码）
    /// 在 <see cref="WizardPlan.PreviewKey"/> 那头。
    /// </remarks>
    private string DesiredPreviewKey() =>
        NeedsPreview() ? WizardPlan.PreviewKey(SelectedSource(), _rotation) : string.Empty;

    private async Task EnsurePreviewAsync()
    {
        await _previewGate.WaitAsync();

        try
        {
            var key = DesiredPreviewKey();
            if (key == _previewKey)
            {
                return;
            }

            await StopPreviewCoreAsync();

            if (key.Length == 0)
            {
                ShowIdleHint();
                return;
            }

            await StartPreviewCoreAsync();
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, "向导", $"起预览出错：{ex.Message}");
            SetOverlayHint($"起预览出错：{ex.Message}");
        }
        finally
        {
            _previewGate.Release();
        }
    }

    private async Task StartPreviewCoreAsync()
    {
        if (_host.Services.FfmpegPath is not { } ffmpegPath)
        {
            SetOverlayHint("没有找到 FFmpeg，取不了景。");
            return;
        }

        var pending = Pending();
        var sink = new SingleSlotPreviewSink();

        PreviewProcess process;
        try
        {
            process = await PreviewProcess.StartAsync(
                ffmpegPath, pending.Camera, sink, pending.Rotation, _logger, _closing.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ⚠️ ffmpeg 不存在、路径不对都在这一支上。**必须说出来**（I3）——
            // 不说的话用户看到的只是一个黑框加一句「正在打开摄像头…」。
            _logger.Log(LogLevel.Error, "向导", $"预览起不来：{ex.Message}");
            SetOverlayHint($"预览起不来：{ex.Message}");
            return;
        }

        _preview = process;
        _sink = sink;
        _previewKey = DesiredPreviewKey();
        _previewStartedAtMs = Environment.TickCount64;
        _gotFrame = false;
        _frameTick = 0;
        _lastDecoded = null;
        RecognitionResultBox.Visibility = Visibility.Collapsed;

        SetOverlayHint("正在打开摄像头…");
        _previewTimer.Start();
    }

    private async Task StopPreviewAsync()
    {
        await _previewGate.WaitAsync();

        try
        {
            await StopPreviewCoreAsync();
        }
        finally
        {
            _previewGate.Release();
        }
    }

    /// <summary>放掉预览。**调用方必须已经拿着 <see cref="_previewGate"/>**。</summary>
    private async Task StopPreviewCoreAsync()
    {
        _previewTimer.Stop();

        var process = _preview;
        _preview = null;
        _sink = null;
        _previewKey = null;

        if (process is not null)
        {
            await process.StopAsync(_closing.Token);
        }

        PreviewImage.Source = null;
        RecognitionImage.Source = null;
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        var frame = _sink?.TakeLatest();

        if (frame is not null)
        {
            _gotFrame = true;
            Render(frame);

            if (_step == RecognitionStep && ++_frameTick % DecodeEveryNthFrame == 0)
            {
                TryDecode(frame);
            }

            return;
        }

        // 一直没有帧：等够了就把 ffmpeg 说的那句话贴出来。
        // ⚠️ 「不出画面」的原因（设备被占用、地址打不开）**只在它那儿** ——
        // 不贴的话用户看到的是一个永远的黑框。
        if (!_gotFrame
            && _preview is { } stalled
            && Environment.TickCount64 - _previewStartedAtMs > StallReportMs)
        {
            var said = stalled.ErrorTail.Trim();
            SetOverlayHint(said.Length > 0
                ? CameraErrorText.Describe(said)
                : "摄像头没有出画面（可能被别的程序占着）。");

            // 报过一次就停 —— 这个定时器每 33 毫秒跑一次，不停的话
            // 那句话会被反复重设（而且日志那边看不出有什么区别）。
            _previewTimer.Stop();
        }
    }

    private void Render(PreviewFrame frame)
    {
        // 尺寸固定 640×360（`PreviewProcess` 那一档定的），所以这块位图建一次就够。
        _frameBitmap ??= new WriteableBitmap(
            frame.Width, frame.Height, 96, 96, PixelFormats.Rgb24, null);

        _frameBitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height), frame.Rgb, frame.Stride, 0);

        if (_step == RecognitionStep)
        {
            RecognitionImage.Source = _frameBitmap;
        }
        else
        {
            PreviewImage.Source = _frameBitmap;
        }

        PreviewHintBox.Visibility = Visibility.Collapsed;
        RecognitionHintBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 从**同一帧**上识码（§72.1 量过吞吐才定的：一路彩色帧，C# 侧降频转灰度）。
    /// </summary>
    private void TryDecode(PreviewFrame frame)
    {
        string? text;
        try
        {
            text = _decoder.TryDecode(frame.ToGray());
        }
        catch (Exception)
        {
            // 解不出来是常态，一帧失败绝不能把定时器带下去。
            return;
        }

        if (string.IsNullOrWhiteSpace(text) || text == _lastDecoded)
        {
            return;
        }

        _lastDecoded = text;

        RecognitionResultText.Text = $"识别到：{text}";
        RecognitionResultBox.Visibility = Visibility.Visible;

        _logger.Log(LogLevel.Info, "向导", $"摄像头识别测试读到 {text}");
    }

    private void OnRecognitionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var on = RecognitionOn.IsChecked == true;
        RecognitionPreviewBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        RecognitionOffBox.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        _ = EnsurePreviewAsync();
    }

    // ─────────────────────────────────────────────
    // 步 4 · 检测录制性能
    // ─────────────────────────────────────────────

    /// <summary>照图 `_30` 那句未完成态里的尾注。</summary>
    private const string PerfNote = "点击「下一步」继续测试麦克风；完成整个向导后配置才会保存";

    private async void OnRetryPerformance(object sender, RoutedEventArgs e) =>
        await RunPerformanceAsync();

    /// <summary>
    /// 真开一次相机，按回落顺序试到第一个跑得通的组合。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>探测期间相机被它占着</b>（规格 §3.1.7 要求「真录一秒再解码验」），
    /// 所以进来之前预览必须已经放掉 —— <see cref="EnterStepAsync"/> 那边排的顺序
    /// 就是这个原因。
    /// </para>
    /// <para>
    /// ⚠️ 结论**只报不写设置**：写下去的话，一次临时故障（相机被别的程序占着、
    /// 网络抖一下）会把用户选的 4K **永久降级**成当时探通的那一档，而他不会知道。
    /// 录制那边每次都重探（`AppHost.PrepareCaptureAsync`，§80），
    /// 所以不写也不会录错档。
    /// </para>
    /// </remarks>
    private async Task RunPerformanceAsync()
    {
        if (_perfRunning)
        {
            return;
        }

        var wanted = new RecordingSpec(Pending().Codec, Pending().Resolution, Pending().Rotation);
        var source = Pending().Camera;

        PerfResultBox.Visibility = Visibility.Collapsed;
        PerfRunningBox.Visibility = Visibility.Visible;
        PerfBar.IsIndeterminate = true;
        // 照图 `_31` 逐字。⚠️ 图里那个「2K」**不在档位表里**（规格三档是 4K / 1080P / 720P）——
        // 照图抄文案，但它是一句口播词，不是第四档（`docs/实现决策.md` §71 记了这一笔）。
        PerfRunningText.Text = "正在测试 720P、1080P、2K 和 4K 的实时编码能力，请稍候";

        if (_host.Services.FfmpegPath is not { } ffmpegPath)
        {
            ShowPerformanceFailure(wanted, "没有找到 FFmpeg，检测不了。");
            return;
        }

        if (source.IsEmpty)
        {
            ShowPerformanceFailure(wanted, "还没选摄像头 —— 回去第 2 步选一个再检测。");
            return;
        }

        // ⚠️ **没测通的网络源，绝不往下探**。两件事都会发生，都是坏的：
        //
        // ① **要跑好几分钟**：探测是按回落表一个个试的，而每一档试的都是
        //    「起 ffmpeg → 录一秒 → 解码验」。一个打不开的网络地址每一档都要
        //    等满 `FfmpegSpecProbe.DefaultNetworkTimeout`（10 秒），
        //    ×十几档就是三分钟以上。2026-09-30 实测：地址连不上时这里跑了
        //    **180 秒还没完**，而进度条是 indeterminate —— 用户只会以为它死了。
        // ② **结论是假的**：探不通会回落成 `H.264 1080P`，界面上就写成
        //    「当前选择没跑通，已改用 H.264 1080P」—— 那句话在说**这台电脑弱**，
        //    而真实原因是**那台摄像机连不上**。凭这个结论用户会把自己的 4K 白降一档。
        //
        // 判据就是第 2 步那个「测试连接」的结论（同一个 `_networkVerified`），
        // 所以这里不重复去探，只把话说清楚。
        if (source.IsNetwork && !_networkVerified)
        {
            ShowPerformanceFailure(wanted, "这一路网络摄像头还没测通 —— 先回第 2 步填好地址、点一次【测试连接】。");
            return;
        }

        _perfRunning = true;
        _work?.Dispose();
        _work = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);

        try
        {
            var selection = await SpecSelectionPolicy.SelectAsync(
                wanted,
                source,
                new FfmpegSpecProbe(ffmpegPath, new SystemProcessRunner(_logger)),
                _work.Token);

            ShowPerformanceResult(wanted, selection);
        }
        catch (OperationCanceledException)
        {
            // 离开这一步 / 关窗了。
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, "向导", $"性能检测失败：{ex.Message}");
            ShowPerformanceFailure(wanted, $"检测没跑起来：{ex.Message}");
        }
        finally
        {
            _perfRunning = false;
            PerfBar.IsIndeterminate = false;
        }
    }

    private void ShowPerformanceFailure(RecordingSpec wanted, string reason)
    {
        // ⚠️ 检测「没跑起来」与「跑起来但失败了」都要留痕（`AGENTS.md` §6.1）：
        // 这一档决定了接下来每一段录像是 4K 还是 1080P，而它**不写设置**（只报），
        // 所以日志是事后唯一能还原因果的地方。
        _logger.Log(LogLevel.Warn, "向导", $"录制性能检测没跑完：{reason}",
            new Dictionary<string, object?> { ["用户选的"] = wanted.Label });

        PerfRunningBox.Visibility = Visibility.Collapsed;
        PerfResultBox.Visibility = Visibility.Visible;

        PerfBanner.Text = reason;
        Tint(PerfBanner, "Warning");
        PerfResultTitle.Text = "性能建议未完成";
        PerfResultSpec.Text = $"当前按 {wanted.Label} 录制";
        PerfResultDetail.Text = "这一次检测没跑起来，所以这一档没有被验过 —— 上面那句话是这次失败的原因，不是这一档的结论。";
        PerfResultNote.Text = PerfNote;
    }

    private void ShowPerformanceResult(RecordingSpec wanted, SpecSelection selection)
    {
        PerfRunningBox.Visibility = Visibility.Collapsed;
        PerfResultBox.Visibility = Visibility.Visible;

        // ⚠️ 印**实测过的**那个数（原生档时是「640×480 @ 30 FPS」这种，照图），
        // 其余档就是那一档的名字。不印一个我们没测过的标称值。
        var measured = selection.Spec.ObservedDescription ?? selection.Spec.Label;

        // ⚠️ 必须留痕（`AGENTS.md` §6.1）：这一步**只报不写设置**，所以事后
        // 没有任何别的地方能还原「那天为什么录的是 1080P」。而回落这一档
        // 恰恰是最需要还原的 —— §5 真机验收里那个「索引说 H.265、产物是 h264」
        // 就是靠日志定性的（§79）。
        _logger.Log(LogLevel.Info, "向导", "录制性能检测完成",
            new Dictionary<string, object?>
            {
                ["用户选的"] = wanted.Label,
                ["实测采用"] = measured,
                ["回落过"] = selection.ChangedFromRequested,
                ["落到原生档"] = selection.NativeFallback,
                ["编码器"] = selection.EncoderName,
                ["原因"] = selection.Reason,
            });

        if (!selection.ChangedFromRequested)
        {
            PerfBanner.Text = "本机编码能力检测通过，可以继续";
            Tint(PerfBanner, "TextPrimary");
            PerfResultTitle.Text = "性能建议已完成";
            PerfResultSpec.Text = $"当前采用：{measured}";
            PerfResultDetail.Text =
                "这一档是真开相机录了一秒、再把产物解码验过的，不是照着参数表猜的 ——"
                + "录像会按它走。";
            PerfResultNote.Text = PerfNote;
            return;
        }

        // 未完成态（照图 `_30`）。⚠️ 那句横幅里「已采用可用的**原生配置**」
        // 只在**真的落到原生档**时才是实话 —— 回落表里还有「同编码降分辨率」
        // 与「保 1080P 换 H.264」两档，那两档说「原生配置」是往界面上写假话。
        PerfBanner.Text = selection.NativeFallback
            ? "当前选择未通过编码器检测，已采用可用的原生配置，仍可继续"
            : $"当前选择没跑通，已改用 {selection.Spec.Label}，仍可继续";
        Tint(PerfBanner, "Warning");

        PerfResultTitle.Text = selection.NativeFallback ? "性能建议未完成" : "性能建议已调整";
        PerfResultSpec.Text = $"当前采用：{measured}";

        // 说法由 Core 那一处收口（`SpecSelectionPolicy.Describe`）——
        // 一个标志盖了「真回落」与「一个都没探通」两种情形，句子不一样，
        // 而那句话放在 Core 才**测得到**（App 层没有测试工程）。
        PerfResultDetail.Text = SpecSelectionPolicy.Describe(selection, wanted);
        PerfResultNote.Text = PerfNote;
    }

    // ─────────────────────────────────────────────
    // 步 5 · 选择麦克风
    // ─────────────────────────────────────────────

    private string? SelectedMicrophone() =>
        (MicrophoneCombo.SelectedItem as ComboBoxItem)?.Tag as string;

    private async Task LoadMicrophonesAsync()
    {
        MicrophoneCombo.Items.Clear();

        if (_host.Services.FfmpegPath is not { } ffmpegPath)
        {
            MicrophoneCombo.IsEnabled = false;
            MicLevelText.Text = "没有找到 FFmpeg，列不出麦克风。";
            return;
        }

        var devices = await DshowDevices.ListAudioAsync(ffmpegPath, _logger, _closing.Token);

        foreach (var device in devices)
        {
            MicrophoneCombo.Items.Add(new ComboBoxItem { Content = device, Tag = device });
        }

        if (devices.Count == 0)
        {
            // ⚠️ 「没有麦克风」是**正常的运行环境**，不是错误（与摄像头那一档同一条口径）。
            // 而它**不影响录制**（I4）—— 那句话必须写出来。
            MicrophoneCombo.IsEnabled = false;
            MicLevelText.Text = "没有找到麦克风。录像照常进行，只是不会有声音。";
            return;
        }

        MicrophoneCombo.IsEnabled = true;

        var remembered = _host.Settings.MicrophoneDevice;
        var index = 0;

        for (var i = 0; i < devices.Count; i++)
        {
            if (string.Equals(devices[i], remembered, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        MicrophoneCombo.SelectedIndex = index;
    }

    private async void OnMicrophoneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        // 换了麦克风当然要重开一路监听（电平是**这一路**的电平）。
        await StopMicrophoneAsync();
        await EnsureMicrophoneAsync();
    }

    private async Task EnsureMicrophoneAsync()
    {
        if (_step != MicrophoneStep)
        {
            await StopMicrophoneAsync();
            return;
        }

        await _micGate.WaitAsync();

        try
        {
            if (_mic is not null)
            {
                return;
            }

            if (_host.Services.FfmpegPath is not { } ffmpegPath
                || SelectedMicrophone() is not { } device)
            {
                return;
            }

            _mic = await MicrophoneLevelMonitor.StartAsync(
                ffmpegPath, device, _logger, _closing.Token);

            _micTimer.Start();
        }
        catch (OperationCanceledException)
        {
            // 关窗了。
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warn, "向导", $"电平监视起不来（{ex.Message}）");
            MicLevelText.Text = $"音量条起不来（{ex.Message}）。麦克风本身可能是好的。";
        }
        finally
        {
            _micGate.Release();
        }
    }

    private async Task StopMicrophoneAsync()
    {
        _micTimer.Stop();

        await _micGate.WaitAsync();

        try
        {
            var monitor = _mic;
            _mic = null;

            if (monitor is not null)
            {
                await monitor.StopAsync(_closing.Token);
            }
        }
        finally
        {
            _micGate.Release();
        }

        MicLevelBar.Value = 0;
    }

    private void OnMicrophoneTick(object? sender, EventArgs e)
    {
        if (_mic is not { } monitor)
        {
            return;
        }

        var level = monitor.Level;
        MicLevelBar.Value = level;

        // 图上那行小字就是那一步**唯一的判据**：
        // 「说话时音量条有跳动，就说明麦克风基础配置正常」。
        var heard = level > 0.02;
        MicLevelText.Text = heard
            ? "已检测到麦克风音量"
            : "还没有听到声音 —— 对着麦克风说句话试试";
        Tint(MicLevelText, heard ? "Success" : "TextSecondary");
    }

    // ─────────────────────────────────────────────
    // 步 6 · 扫码枪（可选）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 把一个 DP 指到资源键上。
    /// </summary>
    /// <remarks>
    /// ⚠️ 别改回 <c>X.Foreground = (Brush)FindResource("…")</c>：那样取到的是一支
    /// **冻结的**笔刷，赋给 DP 之后就跟资源字典脱钩了 —— 用户**开着程序去改
    /// Windows 主题**时，这个控件会一直停在亮色，而且没有任何东西会喊。
    /// <c>SetResourceReference</c> 找不到时**只是先不赋值**（不像 <c>FindResource</c>
    /// 那样抛），字典一换 WPF 自己重解析。
    /// </remarks>
    private static void Tint(FrameworkElement target, string key) =>
        target.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, key);

    private void ShowBarcode()
    {
        // ⚠️ 最近邻缩放（XAML 里那个 `BitmapScalingMode`）：插值会把条与空的边界
        // 糊成灰边，表现是「看着像条码、扫不出来」——与 `EnrollWindow` 那张二维码
        // 同一条理由，**那个属性一个字都别动**。
        var modules = Code128.Modules(TestBarcode);

        // 一个模块 2 个像素：既落在整数边界上（最近邻不会出现半像素），
        // 又让整张图有足够宽度 —— 一维码是靠横向的明暗边界定位的，印窄了不好扫。
        BarcodeImage.Width = modules.GetLength(0) * 2;
        BarcodeImage.Source = BitmapSource.Create(
            modules.GetLength(0), modules.GetLength(1), 96, 96,
            PixelFormats.Gray8, null, Code128.Pixels(modules), modules.GetLength(0));

        // 照图 `_33` 逐字（载荷与那句说明共用一个常量，不会走岔）。
        BarcodeCaption.Text = $"可选扫码枪测试条码：{TestBarcode}，也可以扫描任意真实面单条码";
    }

    private void OnBarcodeTyped(object sender, TextChangedEventArgs e)
    {
        var typed = BarcodeInput.Text.Trim();

        if (typed.Length == 0)
        {
            BarcodeHint.Text = "没有扫码枪可直接进入下一步";
            Tint(BarcodeHint, "TextSecondary");
            return;
        }

        if (typed.Contains(TestBarcode, StringComparison.OrdinalIgnoreCase))
        {
            BarcodeHint.Text = "✅ 扫码枪工作正常 —— 条码内容已经打进上面的框里了。";
            Tint(BarcodeHint, "Success");
            return;
        }

        // 打进别的单号也算数（照图那句「也可以扫描任意真实面单条码」）——
        // 判据是「字有没有进来」，不是「进来的是不是这一串」。
        BarcodeHint.Text = $"读到「{typed}」—— 扫码枪能把字打进输入框，就是能用。";
        Tint(BarcodeHint, "Success");
    }

    // ─────────────────────────────────────────────
    // 完成
    // ─────────────────────────────────────────────

    private async Task FinishAsync()
    {
        NextButton.IsEnabled = false;

        try
        {
            // 相机先放手：设置存完这里就要关窗，而关窗那条路也会再放一次（幂等）。
            await StopPreviewAsync();
            await StopMicrophoneAsync();

            var next = Pending();
            await _host.SaveSettingsAsync(next);

            // ⚠️ 生效时机**不一样**，而这张列表是用户唯一的被告知处（I3）。
            // 存盘成功不代表界面上立刻就是新的（`SaveSettingsAsync` 自己会记一条
            // 变更日志，但用户看的是界面）。
            _logger.Log(LogLevel.Info, "向导", "配置向导已完成",
                new Dictionary<string, object?>
                {
                    ["工作模式"] = next.Mode,
                    ["摄像头"] = next.Camera.Identity,
                    ["摄像头识别"] = next.CameraRecognition,
                    ["方向"] = next.Rotation,
                    ["麦克风"] = next.MicrophoneDevice,
                });

            Completed = true;
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, "向导", $"保存配置失败：{ex.Message}");
            MessageBox.Show(
                this,
                $"配置没能保存：{ex.Message}\n\n" + "硬盘上原来那份设置没有被动过。",
                "配置向导",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            NextButton.IsEnabled = true;
        }

        if (Completed)
        {
            Close();
        }
    }

    // ─────────────────────────────────────────────
    // 预览区那两句提示
    // ─────────────────────────────────────────────

    /// <summary>按当前处境给一句说明（预览压根没起来时用）。</summary>
    private void ShowIdleHint()
    {
        var source = Pending().Camera;

        string text;
        if (source.IsEmpty)
        {
            // 照图 `_17` 逐字。
            text = "未检测到可用摄像头";
        }
        else if (source.IsNetwork)
        {
            // 照图 `_18` 那句话，但**只在地址框还空着时**用。
            // ⚠️ 地址是**预填**的（2026-10-02 实测：框里已经有
            // `http://admin:admin@192.168.101.66:8081`），预填之后再让人「请输入」是
            // 自相矛盾 —— 用户会去找一个根本不用填的东西，而真正该做的是点一下
            // 【测试连接】（那一下才是把这一路验通）。
            text = string.IsNullOrWhiteSpace(NetworkUrlBox.Text)
                ? "请输入网络摄像头地址，然后点击测试连接"
                : "地址已填好，点一下【测试连接】验证这一路";
        }
        else
        {
            text = "正在打开摄像头…";
        }

        SetOverlayHint(text);
    }

    private void SetOverlayHint(string text)
    {
        PreviewHint.Text = text;
        RecognitionHint.Text = text;

        PreviewHintBox.Visibility = Visibility.Visible;
        RecognitionHintBox.Visibility = Visibility.Visible;
    }
}
