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
using VidLog.Desktop.Core.Update;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.App;

public partial class SettingsWindow : Window
{
    // ─────────────────────────────────────────────
    // 外观与启动（批次 9，设计图 `_49`）
    // ─────────────────────────────────────────────

    /// <summary>填「界面语言」与「外观主题」两个下拉。</summary>
    /// <remarks>
    /// ⚠️ <b>这两行现在是**可点**的</b>（需求方 2026-10-01 裁决：「只留窗口，
    /// 用户如点开，显示正在开发中」）。先前是「禁用 + 悬停写明原因」，
    /// 而悬停提示**触屏看不见、不悬停的人也看不见**。
    /// <para>
    /// ⚠️ 选了还没做的那一档：**说一句实话，然后退回**。
    /// 它只有一个真值，而选中的那一档**不会落盘** —— 留在那里就是骗人。
    /// </para>
    /// </remarks>
    private void LoadPreferences()
    {
        FillPreferenceCombo(LanguageCombo, AppPreferences.Languages);
        FillPreferenceCombo(ThemeCombo, AppPreferences.Themes);
    }

    private void FillPreferenceCombo(ComboBox combo, IReadOnlyList<PreferenceOption> options)
    {
        combo.Items.Clear();

        foreach (var option in options)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = option.Label,
                Tag = option.Label,
                // ⚠️ **不禁用** —— 每一档都点得动（需求方 2026-10-01 的裁决）。
                // 悬停那句话说给愿意悬停的人听；点下去那句话**谁都看得见**。
                ToolTip = option.Hint,
            });
        }

        // 选中真做了的那一档（照实显示现状）。哪一档是真的、点了不真的会怎样，
        // 两件都在 `AppPreferences` 里（T27② 第 4 批）—— 那边有测试。
        var real = AppPreferences.RealIndex(options);

        combo.SelectedIndex = real;

        // ⚠️ 挂在**这里**而不是 XAML 上：退回用的是 `real`，而它只有这里知道。
        combo.SelectionChanged += (_, _) =>
        {
            // 放行时给 null（含「退回时又进来一次」那一趟，不会递归）。
            if (AppPreferences.RejectNote(options, combo.SelectedIndex, real) is not { } note)
            {
                return;
            }

            PreferencesNote.Text = note;
            PreferencesNote.Visibility = Visibility.Visible;

            // ⚠️ **退回**：它只有一个真值，而选中的那一档**不落盘** ——
            // 留在那里就是一个骗人的假开关。（说那句话就等于承诺退回，
            // 两者是一对，见 `AppPreferences.RejectNote`。）
            combo.SelectedIndex = real;
        };
    }

    /// <summary>
    /// 「引导式录像」那个入口（`IMPLEMENTATION.md` M8 一行名字，零规格）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它和别的占位一样**只留入口**（需求方 2026-10-01）。
    /// 摆在这一节是**我挑的**（属于录制行为，而这一节就是录制设置所在），设计图上没有它。
    /// </remarks>
    private void OnOpenGuidedRecording(object sender, RoutedEventArgs e) =>
        ShowNotBuilt(
            PreferencesNote,
            "引导式录像还在开发中 —— 它会录的时候给软提示、录完再检测一遍，"
            + "而且只提醒、不打断录制。现在还没有这套逻辑，所以点开只能看到这句话。");

    /// <summary>
    /// 「关闭窗口时」三档（设计图 `_49`）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 三档**都**走同一条退出路（<c>AppHost.RequestExit</c> → <c>ShutdownAsync</c>），
    /// 所以「直接退出」也一样会先问一句在录的那一段 —— 下面那行说明照实写。
    /// </remarks>
    private void LoadCloseActions()
    {
        foreach (var action in Enum.GetValues<CloseWindowAction>())
        {
            CloseActionCombo.Items.Add(new ComboBoxItem
            {
                Content = DescribeCloseAction(action),
                Tag = action.ToString(),
            });
        }

        SelectByTag(CloseActionCombo, _host.Settings.CloseWindowAction.ToString());
    }

    private static string DescribeCloseAction(CloseWindowAction action) => action switch
    {
        CloseWindowAction.AskEveryTime => "每次询问",
        CloseWindowAction.Exit => "直接退出",

        // 兜底给**最保守**的那一档。新增枚举值忘了改这里时，后果只是「写着最小化、
        // 行为也是最小化」，不会丢任何东西。
        _ => "最小化到托盘",
    };

    private void OnCloseActionChanged(object sender, SelectionChangedEventArgs e)
    {
        MarkDirty();
        ShowCloseActionNote();
    }

    /// <summary>把选中那一档**真会发生的**行为写在下面。</summary>
    /// <remarks>
    /// ⚠️ 这三句里最容易写错的是「直接退出」：它<b>不是</b>「在录的段直接丢掉」，
    /// 而是照旧先问一句（`ConfirmExitWhileRecording`）。写成前者的话，
    /// 这行字正好是关于「会不会丢录像」的 —— 一个假警报会让人不敢用这一档。
    /// </remarks>
    private void ShowCloseActionNote()
    {
        CloseActionNote.Text = Enum.TryParse<CloseWindowAction>(TagOf(CloseActionCombo), out var action)
            ? action switch
            {
                CloseWindowAction.AskEveryTime => "关闭窗口时问一句：收进托盘，还是退出程序。",
                CloseWindowAction.Exit => "关闭窗口就退出程序。正在录的那一段会先问一句，不会直接丢掉。",
                _ => "关闭窗口只是收进托盘，录制照常继续。",
            }
            : string.Empty;
    }

    /// <summary>两个只落进设置文件的开关，保存之前什么都不做。</summary>
    /// <remarks>
    /// ⚠️ 开机自启动**不在这里写注册表**，尽管写起来最容易：这一节是「显式保存」
    /// 那一套（改了还能按【取消】反悔），勾一下就把注册表改了的话，
    /// 【取消】又变成骗人的了。真正的落地在保存之后的 `AppHost.SaveSettingsAsync`。
    /// </remarks>
    private void OnRunAtStartupChanged(object sender, RoutedEventArgs e) => MarkDirty();

    private void OnCheckUpdateChanged(object sender, RoutedEventArgs e) => MarkDirty();

    // ── 扩展与联动 / 扩展市场（2026-10-01：**只留入口**）──

    /// <summary>
    /// 「扩展市场」那颗按钮（设计图 `_45` / `_46` 左下角）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>它现在是**可点**的，而不再是灰的。</b>需求方 2026-10-01 裁决：
    /// 没做的功能留入口、点开**如实说一句** —— 这**推翻了**先前那条
    /// 「禁用 + 悬停写明原因」（`实时多画面` / `安装订单联动` 是按那一条办的）。
    /// 两种做法的区别是「点不动」与「点了有实话」，而踩坑 #13 禁的是**第三种**：
    /// 点了没反应、让用户以为是自己那边坏了。
    /// </remarks>
    private void OnOpenExtensionMarket(object sender, RoutedEventArgs e) =>
        ShowNotBuilt(
            ExtensionsNote,
            "扩展市场还在开发中 —— 它会用来浏览和安装别人写好的扩展。"
            + "现在这里还没有可装的东西，所以点开只能看到这句话。");

    /// <summary>「扩展 API」那个入口。同上：只留入口。</summary>
    private void OnOpenExtensionApi(object sender, RoutedEventArgs e) =>
        ShowNotBuilt(
            ExtensionsNote,
            "扩展 API 还在开发中 —— 它会让第三方脚本通过接口读取录像与单号。"
            + "现在还没有这套接口，所以点开只能看到这句话。");

    /// <summary>
    /// 「还没做」那句话 —— 写在**点击处最近的地方**，不弹模态框。
    /// </summary>
    /// <remarks>
    /// ⚠️ 目标那块字**由调用方给**：这一页有不止一处「还没做」的入口，
    /// 写死到其中一处的话，另一处点下去那句话会落在**看不见的另一节里** ——
    /// 表现就是「点了没反应」，正是踩坑 #13 本身。
    /// <para>
    /// ⚠️ 不弹模态框：这一页本来就是静态的，一句话放在按钮下面最好找；
    /// 而模态框会让用户以为「这是个要处理的错」。
    /// </para>
    /// </remarks>
    private static void ShowNotBuilt(TextBlock note, string message)
    {
        note.Text = message;
        note.Visibility = Visibility.Visible;
    }

    // ── 日志（2026-10-01「级别可配」）──────────────

    private void OnLogLevelChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();

    private void OnLogRetainDaysChanged(object sender, RoutedEventArgs e) => MarkDirty();

    /// <summary>把日志那两项填进界面。</summary>
    private void FillLogging(AppSettings settings)
    {
        LogLevelCombo.Items.Clear();

        foreach (var level in LogLevels)
        {
            LogLevelCombo.Items.Add(new ComboBoxItem
            {
                Content = DescribeLogLevel(level),
                Tag = level.ToString(),
            });
        }

        // 认不出来的档（手改坏了）就落到「信息」那一档 —— 取保守的那一头。
        var index = Array.IndexOf(LogLevels, settings.LogMinLevel);
        LogLevelCombo.SelectedIndex = index >= 0 ? index : Array.IndexOf(LogLevels, LogLevel.Info);

        LogRetainDaysBox.Text = settings.LogRetainDays.ToString();
    }

    /// <summary>能选的级别。**顺序即下拉里的顺序**（从最啰嗦到最安静）。</summary>
    private static readonly LogLevel[] LogLevels =
        [LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error];

    /// <summary>
    /// 级别在界面上叫什么。
    /// </summary>
    /// <remarks>
    /// ⚠️ 用中文而不是 `Debug` / `Info` —— 这一格是给用户按的，
    /// 而这个下拉就在「开机自启动」下面。
    /// </remarks>
    private static string DescribeLogLevel(LogLevel level) => level switch
    {
        LogLevel.Debug => "调试（最啰嗦，排查用）",
        LogLevel.Info => "信息（默认）",
        LogLevel.Warn => "警告",
        _ => "错误（最安静）",
    };


    // ─────────────────────────────────────────────
    // 关于
    // ─────────────────────────────────────────────

    /// <summary>填「关于」那一页。**只放真读得出来的东西**（规格 §13.1）。</summary>
    private void ShowAbout()
    {
        // ⚠️ 版本号从 `AppHost.CurrentVersion` 取，**不在这里再读一次程序集**：
        // 两处各读一次的话，判「有没有新版本」用的那个串与这里显示的可能不是一个
        // （`1.0.0` 与 `1.0.0+abc123`），而那种不一致没人查得出来。
        AboutVersionText.Text = _host.CurrentVersion;
        ShowUpdateStatus();

        AboutSpecText.Text = _host.EffectiveSpec.Label
            + $"（用户选的是 {new RecordingSpec(_host.Settings.Codec, _host.Settings.Resolution).Label}）";

        var server = _host.Services.Server;
        AboutServerText.Text = server?.BaseUrl is { Length: > 0 } url
            ? url
            : "未启动";
        AboutArchiveText.Text = _host.Services.ArchiveTarget.Label;
        AboutDataDirText.Text = _host.Services.Layout.RootDirectory;

        // 「局域网与网页」那一节里也有一句 —— 两处读的是**同一个** server 对象，
        // 不可能一边说已启动一边说未启动。
        ServerUrlText.Text = server?.BaseUrl is { Length: > 0 } other
            ? $"现在这个地址是：{other}"
              + (server.IsUsingFallback ? "（只绑到本机，别的设备访问不了）" : string.Empty)
            : "回放服务没有起来 —— 手机和别的电脑现在打不开回放页。";
    }

    /// <summary>「关于」页那一行「更新检查」的结论。</summary>
    /// <remarks>
    /// ⚠️ 那句话本身在 <see cref="UpdateStatusText.Describe"/>（T27② 第 4 批）——
    /// 连同「『没问到』与『已是最新』必须分开说」那条规矩；这里只剩**取哪三样**。
    /// </remarks>
    private void ShowUpdateStatus() =>
        AboutUpdateText.Text = UpdateStatusText.Describe(
            _host.Settings.CheckForUpdates, _host.UpdateStatus, _host.CurrentVersion);

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        if (ShellOpen.Try(_host.Services.Layout.RootDirectory) is { } error)
        {
            SettingsStatus.Text = error;
        }
    }

    /// <summary>把日志、设置与环境信息打成一个 zip，返回它的路径。</summary>
    /// <remarks>
    /// 生成在**临时目录**里，由调用方决定它最后去哪儿（用户挑的位置 / 当附件发出去）。
    /// 直接往数据目录里写的话，「保存到本地」就成了「先生成一份、再复制一份」，
    /// 而那一份还会留在那儿占地方。
    /// </remarks>
    private async Task<string> BuildDiagnosticsAsync()
    {
        var package = new DiagnosticsPackage(new DiagnosticsSources(
            _host.Services.Layout,
            _host.Settings,
            _host.Warnings,
            _ => Task.FromResult<IReadOnlyList<string>>(BuildEnvironmentLines())));

        return await package.ExportAsync(Path.Combine(Path.GetTempPath(), "vidlog-diagnostics"));
    }

    /// <summary>日志导出——「保存到本地…」（需求方 2026-10-03）。</summary>
    private async void OnSaveDiagnostics(object sender, RoutedEventArgs e)
    {
        // ⚠️ **先弹对话框再生成**：用户在对话框上点了取消就不该白生成一份东西。
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存诊断包",
            FileName = $"vidlog-诊断包-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            DefaultExt = ".zip",
            Filter = "压缩包 (zip)|*.zip|所有文件|*.*",
            OverwritePrompt = true,
            InitialDirectory = _host.Services.Layout.RootDirectory,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var produced = await BuildDiagnosticsAsync();

            // 生成出来的文件名带自己的时间戳（`DiagnosticsPackage` 定的），
            // 而用户挑的那个名字说了算 —— 复制过去而不是要求那边改名。
            File.Copy(produced, dialog.FileName, overwrite: true);

            SettingsStatus.Text = $"诊断包已保存：{dialog.FileName}";

            _host.Logger.Log(
                LogLevel.Info, "诊断包", "导出到本地",
                new Dictionary<string, object?> { ["目标"] = dialog.FileName });
        }
        catch (Exception ex)
        {
            // catch 不静默：状态栏那句话**必须显示**（§6.1 的「用户可见通道」）。
            SettingsStatus.Text = $"导出失败：{ex.Message}";
            _host.Logger.Log(
                LogLevel.Warn, "诊断包", $"导出到本地失败：{ex.Message}");
        }
    }

    /// <summary>日志导出——「发送到开发者邮箱」（需求方 2026-10-03）。</summary>
    /// <remarks>
    /// ⚠️ 走的是**系统邮件客户端**（见 <see cref="MailComposer"/> 的说明）：
    /// 程序里没有、也不该有发信凭据。所以最后那一下由用户按。
    /// </remarks>
    private async void OnEmailDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            var produced = await BuildDiagnosticsAsync();
            var now = DateTimeOffset.Now;

            SettingsStatus.Text = "正在把它交给邮件客户端…";

            var problem = MailComposer.TryCompose(
                SupportMail.Address,
                SupportMail.Subject(Environment.MachineName, now),
                SupportMail.Body(Environment.MachineName, _host.CurrentVersion, now),
                produced);

            if (problem is null)
            {
                // ⚠️ 说法是「交给客户端了」而**不是**「已经发出去了」：
                // MAPI 返回成功只说明信到了客户端（可能是发件箱），
                // 而最后那一下在邮件客户端那一侧。
                SettingsStatus.Text =
                    $"已交给邮件客户端，请在那儿按发送（收件人 {SupportMail.Address}）。";
                _host.Logger.Log(
                    LogLevel.Info, "诊断包", "交给邮件客户端",
                    new Dictionary<string, object?>
                    {
                        ["收件人"] = SupportMail.Address,
                        ["附件"] = produced,
                    });
            }
            else
            {
                // ⚠️ 失败**不等于**日志没导出来 —— 那句话里必须带上附件到底在哪儿，
                // 否则用户只会以为「什么都没发生」。附件留在临时目录里**刻意不删**：
                // 这条路的下一步就是让他手发。
                SettingsStatus.Text = $"{problem}（文件在 {produced}）";
                _host.Logger.Log(
                    LogLevel.Warn, "诊断包", $"邮件没能发出去：{problem}",
                    new Dictionary<string, object?>
                    {
                        ["收件人"] = SupportMail.Address,
                        ["附件"] = produced,
                    });
            }
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"导出失败：{ex.Message}";
            _host.Logger.Log(
                LogLevel.Warn, "诊断包", $"发邮件前导出失败：{ex.Message}");
        }
    }

    private IReadOnlyList<string> BuildEnvironmentLines()
    {
        var lines = new List<string>
        {
            $"OS: {Environment.OSVersion}",
            $".NET: {Environment.Version}",
            $"机器名: {Environment.MachineName}",
            $"回放服务: {(_host.Services.Server?.BaseUrl is { Length: > 0 } url ? url : "未启动")}",
        };

        if (_host.Services.FfmpegPath is { } ffmpeg)
        {
            lines.Add($"FFmpeg: {ffmpeg}");
        }

        lines.AddRange(_host.Warnings.Select(w => $"警告: {w}"));
        return lines;
    }

    /// <summary>启动时的问题。**必须看得见** —— 它们发生在用户没盯着屏幕的时候。</summary>
    private void ShowWarnings()
    {
        if (_host.Warnings.Count == 0)
        {
            return;
        }

        WarningsHeader.Visibility = Visibility.Visible;
        WarningsList.Visibility = Visibility.Visible;

        foreach (var warning in _host.Warnings)
        {
            WarningsList.Items.Add(warning);
        }
    }

    // ─────────────────────────────────────────────
    // 连接手机（规格 §3.4.5）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 【连接电脑/手机】—— 弹出二维码。
    /// </summary>
    /// <remarks>
    /// 模态：一次只有一个。否则用户可以开出两份二维码，而
    /// <c>DeviceRegistry</c> 只留得住**最后一张** —— 屏幕上摆着两张，
    /// 能用的只有一张，那是最难解释的一种「扫了没反应」。
    /// </remarks>
    private void OnEnroll(object sender, RoutedEventArgs e) =>
        new EnrollWindow(_host) { Owner = this }.ShowDialog();
}
