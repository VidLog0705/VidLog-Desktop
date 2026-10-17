using System.Windows;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.License;

// 本工程同时开了 UseWPF 与 UseWindowsForms，两边同名类型要钉死成 WPF 那套
// （与其它窗口同一条口径）。
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace VidLog.Desktop.App;

/// <summary>这一张窗出现在什么时候。</summary>
public enum LicenseWindowMode
{
    /// <summary>首次运行：说明 7 天试用，给出机器码；点【不同意】退出程序。</summary>
    FirstRun,

    /// <summary>试用到期后的购买引导：同一份机器码与二维码，只有一个【关闭】。</summary>
    Purchase,
}

/// <summary>
/// 「7 天试用 / 机器码 / 联系我们」—— 首次运行与试用到期后的购买引导共用（需求方 2026-10-17）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 窗口里**没有激活输入框**：需求方的流程是「用户把机器码发给我，我把码给他，
/// 他在【设置 → 许可】里手动激活」—— 再放一个输入框就是第二条激活路径，
/// 而两条路径迟早只补了一条。
/// </para>
/// <para>
/// ⚠️ 机器码**一直显示**（与设置页同一条理由）：它是用户唯一要抄给提供方的东西，
/// 而这件事恰恰发生在**还没激活的时候**。
/// </para>
/// <para>
/// ⚠️ 二维码与「关于」页是同一份 `contact-qr.png`（编进程序集）——
/// 换人只换那张图。
/// </para>
/// </remarks>
public partial class LicenseWindow : Window
{
    private readonly AppHost _host;
    private readonly LicenseWindowMode _mode;

    /// <param name="host">取机器码与日志用。</param>
    /// <param name="mode">见 <see cref="LicenseWindowMode"/>。</param>
    public LicenseWindow(AppHost host, LicenseWindowMode mode)
    {
        _host = host;
        _mode = mode;

        InitializeComponent();

        FillTexts();
        ShowMachineCode();

        if (mode == LicenseWindowMode.Purchase)
        {
            // 购买引导只有一条出路：看完机器码/二维码就关掉。
            AcceptButton.Visibility = Visibility.Collapsed;
            DeclineButton.Content = "关闭";
            DeclineButton.Click -= OnDecline;
            DeclineButton.Click += OnClose;
        }
    }

    private void FillTexts()
    {
        // ⚠️ 天数与小时取常量，**不写字面量**（`TrialRecordStore.Window` 是权威值）。
        var days = TrialRecordStore.Window.TotalDays;
        var hours = TrialRecordStore.Window.TotalHours;

        if (_mode == LicenseWindowMode.FirstRun)
        {
            Title = "VidLog 7 天试用";
            HeadingText.Text = $"本程序提供 {days:0} 天试用";
            IntroText.Text =
                $"试用期 {hours:0} 小时，从你拿到试用码并激活的那一刻起算。试用期间可以正常录制、"
                + "接入 4 台手机端。到期后不能再录制新视频，但本机已经保存的录像不受影响，"
                + "照常可以检索、回放、导出。";
            return;
        }

        Title = "购买激活";
        HeadingText.Text = "试用已结束";
        IntroText.Text =
            "要接着录新视频，需要购买激活码。把下面这串机器码发给提供方，"
            + "拿到激活码后在【设置 → 许可】里粘进去即可 —— 不用重装、已有的录像不受影响。";
    }

    private void ShowMachineCode()
    {
        var license = _host.Services.License;

        if (license is null)
        {
            // 与设置页同一句：这是**安装的问题**，不是用户的机器问题。
            // 把机器码那一行留空并说清楚，免得用户拿着一串空白去找人。
            MachineCodeBox.Text = "（本机没配好许可公钥，请联系提供方重新安装）";
            CopyButton.IsEnabled = false;
            return;
        }

        MachineCodeBox.Text = license.Status.MachineCode;

        if (license.Status.Degraded)
        {
            DegradedText.Text = "⚠️ 这台机器的部分硬件标识读不到（机器码里有全零段），"
                + "同型号的机器可能算出一样的码 —— 请先查清为什么读不到（常见是 WMI 被禁用了）。";
            DegradedText.Visibility = Visibility.Visible;
        }
    }

    private void OnCopyMachineCode(object sender, RoutedEventArgs e)
    {
        var code = MachineCodeBox.Text;

        if (string.IsNullOrWhiteSpace(code) || !CopyButton.IsEnabled)
        {
            return;
        }

        try
        {
            // 必须限定：同时引了 WinForms，那边的 Clipboard 撞名。
            System.Windows.Clipboard.SetText(code);
            CopyButton.Content = "已复制";
        }
        catch (Exception ex)
        {
            // 剪贴板是跨进程共享的，被别的程序占着时会抛 COMException。
            // 不崩、也不装作复制成功 —— 让用户手动选中复制。
            MessageBox.Show(
                this,
                $"复制不了（{ex.Message}）—— 请手动选中上面那串机器码复制。",
                "VidLog",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        _host.Logger.Log(LogLevel.Info, "许可", "首次运行：用户同意了 7 天试用说明");
        DialogResult = true;
    }

    private void OnDecline(object sender, RoutedEventArgs e)
    {
        _host.Logger.Log(LogLevel.Info, "许可", "首次运行：用户不同意，退出程序");
        DialogResult = false;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
