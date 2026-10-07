using System.Globalization;
using System.IO;
using System.Windows;
using VidLog.Desktop.Core;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Import;
using VidLog.Desktop.Core.Labels;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的同名类型都带进来。这里钉死成 WPF 的那套。
using MessageBox = System.Windows.MessageBox;

namespace VidLog.Desktop.App;

/// <summary>
/// 导入录像 —— 问清三件事，然后交给 <see cref="RecordingImporter"/>。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>这里一行「猜」都没有</b>：单号、业务类型、录制时间全部由人确认。
/// 它们三个都会进**证据元数据**，猜错了一个，这条录像在库里的样子就是假的
/// —— 而界面上一点都看不出来。
/// </para>
/// <para>
/// ⚠️ 预填的那两个值（文件名、文件时间）都只是**起点**，界面上每一处都写明了
/// 「这是猜的，请核对」。不写的话用户会以为它们是读出来的。
/// </para>
/// </remarks>
public partial class ImportWindow : Window
{
    private readonly AppHost _host;

    /// <summary>用户挑中的那个文件；还没挑时是 <see langword="null"/>。</summary>
    private string? _sourcePath;

    /// <summary>
    /// 导入成功之后给检索页看的一句话。
    /// </summary>
    /// <remarks>
    /// ⚠️ 关窗之后那份 `ImportResult` 就没人拿了，而界面需要告诉用户的恰恰是
    /// 「时长量出来了没有」这件事 —— 它是**在关窗之前**定下来的，所以带在这里。
    /// </remarks>
    public string? ImportedSummary { get; private set; }

    /// <summary>
    /// 窗口已经关掉了。导入是关不掉的（见 <see cref="OnClosing"/>），
    /// 所以它跑完之后**必须**先看这一位再碰界面。
    /// </summary>
    private bool _closed;

    public ImportWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        Closed += (_, _) => _closed = true;
    }

    // ─────────────────────────────────────────────
    // 挑文件
    // ─────────────────────────────────────────────

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "挑一个要导入的录像文件",
            // ⚠️ 过滤器只是**默认视图**，不是白名单：底下留着「所有文件」，
            // 因为用户手上的文件可能是任何一种容器，而收不收由内容说了算
            // （`RecordingImporter` 会真的解一遍），不由扩展名说了算。
            Filter = "视频文件|*.mp4;*.mkv;*.mov;*.avi;*.ts;*.m4v;*.flv;*.wmv"
                + "|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _sourcePath = dialog.FileName;
        SourceBox.Text = dialog.FileName;

        PrefillFromFile(dialog.FileName);

        ImportButton.IsEnabled = true;
        ShowStatus(
            "文件挑好了。核对下面三项，然后按【导入】—— 按下去之后才会真的开始检查与复制。",
            LogLevel.Info);
    }

    /// <summary>
    /// 从文件本身预填单号与时间。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>单号取文件名</b>，就这么简单：本机【导出原视频】落下来的文件名正是
    /// 「单号.mp4」，而导入的主要来源恰恰就是那些导出件与别的机器上的备份。
    /// 取不到（文件名是别的东西）时把它原样填进去让用户改 —— 在这里做更聪明的
    /// 推导只会让「默认值是错的」这件事更难被发现。
    /// </remarks>
    private void PrefillFromFile(string path)
    {
        WaybillBox.Text = WaybillNumber.Normalize(Path.GetFileNameWithoutExtension(path)) ?? string.Empty;

        try
        {
            // ⚠️ `GetLastWriteTime` 读不到时返回 1601 年那个值而不是抛 ——
            // 拿它当初值会让日期跳到 1601，用户看不出这是「读不到」还是「真那样」。
            // 所以太老的直接不用，让日期留空由用户自己选。
            var written = File.GetLastWriteTime(path);
            if (written.Year > 1980)
            {
                StartedDate.SelectedDate = written.Date;
                StartedTime.Text = written.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 预填失败不是错误 —— 用户自己填就是了。但**留痕**，
            // 否则「为什么这里默认是空的」事后没人答得上来。
            _host.Log(LogLevel.Info, "导入", $"读不出 {path} 的文件时间：{ex.Message}");
        }
    }

    // ─────────────────────────────────────────────
    // 导入
    // ─────────────────────────────────────────────

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        // ⚠️ 收请求这一步**一个判断都不该有**（T27② 第 2 批）：校验与元数据组装
        // 全在 `ImportRequestBuilder.Build` 里。这里只管把控件上的值读出来、
        // 按结论去导入。
        var (request, problem) = ImportRequestBuilder.Build(
            _sourcePath,
            WaybillBox.Text,
            StartedDate.SelectedDate is { } date ? DateOnly.FromDateTime(date) : null,
            StartedTime.Text,
            isReturn: ReturnRadio.IsChecked == true,
            DateTimeOffset.Now.Offset);

        if (request is null)
        {
            ShowStatus(problem, LogLevel.Warn);
            return;
        }

        SetBusy(true);
        ShowStatus("正在检查这个文件（算哈希、量时长、解一遍看能不能播）…", LogLevel.Info);

        ImportResult result;
        try
        {
            result = await _host.Services.Importer.ImportAsync(request);
        }
        catch (Exception ex)
        {
            // 走到这里说明是没人预料到的异常（`ImportAsync` 把可预期的失败
            // 都变成返回值了）。I3：照样要说出来，不许静默什么都不发生。
            if (_closed)
            {
                return;
            }

            SetBusy(false);
            ShowStatus($"导入出错：{ex.Message}", LogLevel.Warn);
            return;
        }

        // ⚠️ 用户在导入跑着的时候按了 ✕（那时只有 ✕ 可按）。导入**没有**被取消，
        // 它照常写完了 —— 但这个窗口已经没了，碰它就是碰一个关掉的窗口
        // （`DialogResult` 那一句会当场抛 `InvalidOperationException`，
        // 而它在一个 `async void` 里，会直接掀掉整个应用）。
        if (_closed)
        {
            return;
        }

        SetBusy(false);

        if (!result.Imported)
        {
            ShowStatus(result.FailureReason ?? "导入失败。", LogLevel.Warn);
            return;
        }

        // ⚠️ 时长没量出来要**明说**：索引里那条记的是 0 秒，而界面上不写的话
        // 用户会以为这段录像真的只有 0 秒（或者以为导入漏了什么）。
        ImportedSummary = result.Duration is null
            ? $"已导入（证据 {result.EvidenceId}）。⚠️ 没能从这个文件里量出时长，库里那条记的是 0 秒，"
              + "录像本身是完整的。"
            : $"已导入（证据 {result.EvidenceId}，时长 {Format(result.Duration.Value)}）。"
              + "它已经进库了 —— 当前这组筛选条件不一定筛得到它。";

        ShowStatus(ImportedSummary, LogLevel.Info);

        // 关窗，让检索页重搜一遍。
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    // ─────────────────────────────────────────────
    // 界面小事
    // ─────────────────────────────────────────────

    /// <summary>导入进行中：把能改的东西都冻住。</summary>
    /// <remarks>
    /// ⚠️ 冻住**不是**为了好看：导入跑起来之后再改单号或时间，用户会以为
    /// 改的是正在跑的那一次，而它读的是按下去那一刻的值。
    /// </remarks>
    private void SetBusy(bool busy)
    {
        BrowseButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy && _sourcePath is not null;
        CancelButton.IsEnabled = !busy;
        WaybillBox.IsReadOnly = busy;
        StartedTime.IsReadOnly = busy;
        StartedDate.IsEnabled = !busy;
        OutboundRadio.IsEnabled = !busy;
        ReturnRadio.IsEnabled = !busy;
    }

    private void ShowStatus(string message, LogLevel level)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;

        // ⚠️ 失败那一句必须与说明文字**在颜色上拉开**：混在一起时用户会把
        // 「导入没成」当成又一行说明读过去。
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            level == LogLevel.Warn ? "Warning" : "TextSecondary");
    }

    private static string Format(TimeSpan span) =>
        $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    /// <summary>
    /// 关窗时如果还在导入中，说一句。
    /// </summary>
    /// <remarks>
    /// 这一条不是多余的：导入一个几 GB 的文件要跑一会儿（中间那一步是完整解码），
    /// 而 `ImportAsync` 传的是默认的 <c>CancellationToken.None</c> ——
    /// 关窗**不会**把它停下来，它会照常写完。不说的话用户以为关掉就是取消了。
    /// </remarks>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!CancelButton.IsEnabled && _sourcePath is not null && ImportedSummary is null)
        {
            MessageBox.Show(
                "导入还在进行中，关掉这个窗口不会让它停下来 —— 它会照常做完并入库。",
                "导入录像", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        base.OnClosing(e);
    }
}
