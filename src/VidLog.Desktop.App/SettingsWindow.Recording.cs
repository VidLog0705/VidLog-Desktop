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
    // 录像清理（设计图 `_43` 的两颗按钮）
    // ─────────────────────────────────────────────

    /// <summary>【按时间清理…】—— 按当下保留期算一次，给用户看过才删。</summary>
    /// <remarks>
    /// ⚠️ 这里**一个判断都不该有**（T26①）：算什么、要不要问、开头那句话怎么写，
    /// 全在 <see cref="CleanupFlow.ByTimeAsync"/> 里。搬到 Core 之前，
    /// 「读设置 / 算保留期 / 要不要问」是写在按钮点击事件里的 ——
    /// 也就是母仓 §4 明令禁止的「逻辑塞进 UI 层」，而那个工程没有测试工程（T27②），
    /// 于是这一整套分支只能靠读代码确认。
    /// </remarks>
    private async void OnCleanupByTime(object sender, RoutedEventArgs e)
    {
        CleanupByTimeButton.IsEnabled = false;
        CleanupNote.Text = "正在算该清哪些…";

        try
        {
            var preview = await _host.Services.CleanupFlow.ByTimeAsync(
                _host.Settings.Retention, DateTimeOffset.Now);

            if (preview.Proposal is not { } proposal)
            {
                CleanupNote.Text = preview.Message;
                return;
            }

            CleanupNote.Text =
                (await CleanupPrompt.AskAndRunAsync(this, _host, proposal)).Message;
        }
        catch (Exception ex)
        {
            // ⚠️ 只写在界面上不算数：清理是**不可逆动作**，而这一行会被用户
            // 划走、窗口一关就没了 —— 事后只有日志能说明那一次为什么没清成。
            _host.Log(LogLevel.Warn, "清理", $"清理没能进行：{ex.Message}");
            CleanupNote.Text = $"清理没能进行：{ex.Message}";
        }
        finally
        {
            SyncCleanupButtons();
        }
    }

    /// <summary>【按空间释放…】—— 把这批录像清到活动那块盘至少还剩它的预留空间。</summary>
    private async void OnCleanupBySpace(object sender, RoutedEventArgs e)
    {
        CleanupBySpaceButton.IsEnabled = false;
        CleanupNote.Text = "正在算该清哪些…";

        try
        {
            var preview = await _host.Services.CleanupFlow.BySpaceAsync(DateTimeOffset.Now);

            if (preview.Proposal is not { } proposal)
            {
                CleanupNote.Text = preview.Message;
                return;
            }

            var outcome = await CleanupPrompt.AskAndRunAsync(this, _host, proposal);

            // ⚠️ 「可能没腾够」那句只在**真去删了**的时候说。用户点了【否】还说这句，
            // 他会以为程序背着他动过手。
            CleanupNote.Text = outcome.Message
                + (outcome.Ran ? proposal.Afterword : string.Empty);
        }
        catch (Exception ex)
        {
            // 同【按时间清理…】：只写界面的话，这一次为什么没清成事后查不到。
            _host.Log(LogLevel.Warn, "清理", $"清理没能进行：{ex.Message}");
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
    // 录制规格（规格 §3.1.7）
    // ─────────────────────────────────────────────

    private RadioButton[] CodecButtons => [CodecH264, CodecH265];

    private RadioButton[] ResolutionButtons => [Res4K, Res1080, Res720];

    /// <summary>
    /// 显示**实际会用**的录制规格。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>这里一个判断都不该有</b>（T27② 第 2 批）：说什么、要不要画成警告色，
    /// 全在 <see cref="EffectiveSpecNotice.Describe"/> 里 —— 那个工程有测试工程，
    /// 而本工程没有（这一段原先就长在这儿，三档情形只能靠读代码确认）。
    /// 外壳这一层只剩「把结果接到控件上」。
    /// </para>
    /// <para>
    /// ⚠️ 三档情形的理由、「2026-09-30 印过一句假话」那件事、
    /// 以及「方向要一起比」那条，都跟着搬进 <see cref="EffectiveSpecNotice"/> 了 ——
    /// 要读那几条去 Core 那个文件，别在这儿再抄一份。
    /// </para>
    /// </remarks>
    private void ShowEffectiveSpec()
    {
        var wanted = new RecordingSpec(
            _host.Settings.Codec, _host.Settings.Resolution, _host.Settings.Rotation);

        var (text, warning) = EffectiveSpecNotice.Describe(
            wanted, _host.EffectiveSpec, _host.ProbedSpec, _host.SpecFallbackReason);

        EffectiveSpecText.Foreground = (System.Windows.Media.Brush)FindResource(
            warning ? "Warning" : "TextSecondary");
        EffectiveSpecText.Text = text;
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
}
