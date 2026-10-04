using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的同名类型都带进来。这里钉死成 WPF 的那套。
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using VidLog.Desktop.App.Platform;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Export;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Search;

namespace VidLog.Desktop.App;

/// <summary>结果列表里的一行。</summary>
/// <remarks>
/// 用真类型而不是匿名类型：`Hit` 要能被强类型地取回来（选中一条要播放、
/// 要导出）。此前在 <see cref="MainWindow"/> 里是靠反射读匿名类型的属性 ——
/// 那样写错了只会在运行时炸，而 App 层没有测试工程。
/// </remarks>
internal sealed record SearchResultRow(
    string Waybill,
    string Subtitle,
    string Note,
    Visibility NoteVisibility,
    string LockLabel,
    string EvidenceId,
    RecordingHit Hit);

/// <summary>
/// 历史录像检索与回放 —— 纯视图（2026-09-28 从 <see cref="MainWindow"/> 拆出来）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>检索与回放不得有任何许可门控</b>（<c>docs/04-许可设计.md</c> L8：
/// 「用户的数据是他的，不是人质」）。这里没有、也不许加「未激活先激活」——
/// 拆窗时最容易顺手补上那一步。
/// </para>
/// <para>
/// ⚠️ 播放走 WPF 自带的 <c>MediaElement</c>，**不引任何第三方播放器**（I10：
/// 离线可用）。能不能解出画面取决于系统解码器，所以播不了的时候
/// **要说出来**（把原因写在提示那一行上），不许静默变成一个黑框。
/// </para>
/// </remarks>
public partial class SearchWindow : Window
{
    private readonly AppHost _host;

    /// <summary>本次检索的全部结果。分页是**客户端**切的（见 <see cref="RenderPage"/>）。</summary>
    private IReadOnlyList<RecordingHit> _hits = [];

    private int _page;

    /// <summary>一页多少条。</summary>
    /// <remarks>
    /// 定 50 是**本仓标定**的（规格没给数）：左边那一栏一次能看这么多行，
    /// 再多就要滚，而滚起来比翻页更难找回刚才那一条。
    /// </remarks>
    private const int PageSize = 50;

    /// <summary>播放进度刷新。只在真的在播时跑。</summary>
    private readonly DispatcherTimer _playTicker;

    /// <summary>用户正拖着进度条 —— 这期间不要用播放位置去覆盖他拖到的位置。</summary>
    private bool _seeking;

    /// <param name="initialQuery">
    /// 打开时预先填进检索框、并**当场检索一次**（T12 命令面板打单号跳过来用）。
    /// <see langword="null"/> = 就照原来的样子：空框，等用户自己输。
    /// </param>
    public SearchWindow(AppHost host, string? initialQuery = null)
    {
        _host = host;
        InitializeComponent();

        SearchButton.IsEnabled = true;

        if (!string.IsNullOrWhiteSpace(initialQuery))
        {
            SearchBox.Text = initialQuery;

            // ⚠️ 用**模糊**，不是框上默认的「精确」：命令面板里敲进去的多半是单号
            // 的一截，精确匹配会一条都搜不出来 —— 而用户看到的会是「跳过来是个空列表」，
            // 看起来像这条录像丢了。
            MatchCombo.SelectedIndex = 2;

            // ⚠️ 等界面画完再搜：`SearchAsync` 会动列表与按钮文字，构造里调的时候
            // 这些控件还没量过尺寸。
            Loaded += async (_, _) => await SearchAsync();
        }

        _playTicker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playTicker.Tick += (_, _) => UpdatePlaybackPosition();

        Closed += (_, _) =>
        {
            _playTicker.Stop();

            // ⚠️ 必须收掉：MediaElement 拿着文件句柄，不收的话那个 mp4
            // 在窗口关掉之后仍然被占用 —— 用户会删不掉它。
            Player.Close();
        };
    }

    // ─────────────────────────────────────────────
    // 检索
    // ─────────────────────────────────────────────

    private async void OnSearch(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // 标成已处理：不然将来谁给这个窗口加一颗 `IsDefault` 按钮，
            // 回车会**同时**触发检索与那颗按钮（本窗口现在没有，纯防御）。
            e.Handled = true;
            await SearchAsync();
        }
    }

    private async Task SearchAsync()
    {
        var from = FromDate.SelectedDate;
        var to = ToDate.SelectedDate;

        var query = new RecordingQuery
        {
            WaybillText = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim(),
            MatchMode = TagOf(MatchCombo) switch
            {
                "Prefix" => WaybillMatchMode.Prefix,
                "Fuzzy" => WaybillMatchMode.Contains,
                _ => WaybillMatchMode.Exact,
            },
            // 结束那一天要**整日包含**，所以右边界取次日零点（半开区间）。
            From = from is null ? null : new DateTimeOffset(from.Value.Date, DateTimeOffset.Now.Offset),
            To = to is null ? null : new DateTimeOffset(to.Value.Date.AddDays(1), DateTimeOffset.Now.Offset),
            BusinessType = TagOf(BusinessCombo) switch
            {
                "Outbound" => BusinessType.Outbound,
                "Return" => BusinessType.Return,
                _ => null,
            },
            // ⚠️ `Limit` 默认只有 200，必须显式顶高：分页在客户端切，
            // 截断了的话「共 N 条」是假的，而界面上一点都看不出来。
            Limit = int.MaxValue,
        };

        CountText.Text = "正在检索…";

        try
        {
            _hits = await _host.Services.Search.SearchAsync(query);
            _page = 0;
            RenderPage();
        }
        catch (Exception ex)
        {
            _hits = [];
            RenderPage();
            CountText.Text = $"检索出错：{ex.Message}";
        }
    }

    /// <summary>把当前这一页画出来。</summary>
    /// <remarks>
    /// 分页在客户端切：<c>SearchAsync</c> 本来就是全量读索引再过滤，
    /// 让 Core 支持 Offset 只是把同一份工作切成几次，省不下什么。
    /// ⚠️ 库特别大时这里会一次性拿回来 —— 已知的代价，先记着。
    /// </remarks>
    private void RenderPage()
    {
        var slice = _hits.Skip(_page * PageSize).Take(PageSize).ToList();

        ResultsList.ItemsSource = slice.Select(h =>
        {
            var note = BuildNote(h);

            return new SearchResultRow(
                h.Entry.Waybill.Value,
                $"{h.Entry.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                + $" · {(int)h.Entry.Duration.TotalMinutes}:{h.Entry.Duration.Seconds:00}"
                + $" · {BusinessLabel(h.BusinessType)}",
                note,
                note.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
                // 争议锁定（规格 §3.6.5）。判据用 `EvidenceLock.IsLocked`
                // —— 与清理判定**同一个函数**，在界面里另写一份会漏掉
                // 「认不出来的值当锁着」那一条。
                EvidenceLock.IsLocked(h.Labels) ? "已锁定" : "锁定",
                h.Entry.EvidenceId,
                h);
        }).ToList();

        CountText.Text = _hits.Count == 0
            ? "共 0 条"
            : $"共 {_hits.Count} 条，第 {_page + 1} / {PageCount()} 页";

        PrevPageButton.IsEnabled = _page > 0;
        NextPageButton.IsEnabled = _page + 1 < PageCount();

        ExportButton.IsEnabled = false;

        // 一条都没有时【导出单号】是禁用的 —— 导一张只有表头的空表没有意义，
        // 而按下去的后果是用户以为「导出来了，只是内容空」，然后去查为什么。
        ExportWaybillsButton.IsEnabled = _hits.Count > 0;
    }

    /// <summary>单号框里那个 ✕（图 `_41`）：清空输入框，<b>不动已经列出来的结果</b>。</summary>
    /// <remarks>
    /// ⚠️ 刻意**不**顺手把结果也清掉：结果列表是上一次检索的产物，它本身就是一份
    /// 有用的东西（用户可能正要照着它导出单号）。✕ 的含义到处都只是「清掉这格输入」——
    /// 在这里多清一样，用户下一次就不敢按它了。
    /// </remarks>
    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
    }

    private int PageCount() => Math.Max(1, (_hits.Count + PageSize - 1) / PageSize);

    private void OnPrevPage(object sender, RoutedEventArgs e)
    {
        if (_page == 0)
        {
            return;
        }

        _page--;
        RenderPage();
    }

    private void OnNextPage(object sender, RoutedEventArgs e)
    {
        if (_page + 1 >= PageCount())
        {
            return;
        }

        _page++;
        RenderPage();
    }

    private static string BusinessLabel(BusinessType? type) => type switch
    {
        BusinessType.Outbound => "发货",
        BusinessType.Return => "退货",
        _ => "类型未知",
    };

    /// <summary>
    /// 一条结果要不要带一句提醒。
    /// </summary>
    /// <remarks>
    /// ⚠️ 「成品不在盘上」**必须说出来**：索引里有、文件没了，是两回事，
    /// 而用户点播放只会看到一个黑框 —— 不说的话他无从判断是文件丢了、
    /// 还是播放器坏了。
    /// </remarks>
    private string BuildNote(RecordingHit hit)
    {
        var path = PathOf(hit);

        return File.Exists(path) ? string.Empty : "⚠️ 成品不在盘上（索引里有，文件没了）";
    }

    /// <remarks>
    /// ⚠️ 走 <c>Resolve</c> 而**不是**拼某一个根（设计图 `_43` 的多磁盘）：
    /// 录像可能落在用户配的任意一块盘上，拼第一个根的话另一块盘上的那些
    /// 会一律显示成「成品不在盘上」—— 而文件明明好好地在盘上。
    /// <para>
    /// 找不到时给活动根下的路径（**不是给空串**）：那个路径要拿去 <c>MediaElement</c>
    /// 与导出对话框用，而「一个不存在的路径」能让它们如实报错，
    /// 空串只会让它们静默什么都不做。
    /// </para>
    /// </remarks>
    private string PathOf(RecordingHit hit) =>
        _host.Services.Storage.ResolveOrActive(hit.Entry.Location.Value);

    // ─────────────────────────────────────────────
    // 选中与播放
    // ─────────────────────────────────────────────

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not SearchResultRow row)
        {
            return;
        }

        ExportButton.IsEnabled = true;

        var path = PathOf(row.Hit);

        if (!File.Exists(path))
        {
            // 文件不在盘上就别把播放器指向一个不存在的东西 —— MediaElement
            // 会静默失败，而那个黑框与「解码器缺失」长得一模一样。
            Player.Close();
            ShowPlaceholder($"成品不在盘上：{path}");
            return;
        }

        Player.Source = new Uri(path);
        Player.Position = TimeSpan.Zero;
        PositionSlider.Value = 0;
        ShowPlayer();
        Player.Play();
        _playTicker.Start();
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (Player.Source is null)
        {
            return;
        }

        if (_playing)
        {
            Player.Pause();
            _playTicker.Stop();
            _playing = false;
            PlayButton.Content = "播放";
        }
        else
        {
            Player.Play();
            _playTicker.Start();
            _playing = true;
            PlayButton.Content = "暂停";
        }
    }

    private bool _playing;

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        ShowPlayer();

        var total = Player.NaturalDuration.HasTimeSpan
            ? Player.NaturalDuration.TimeSpan
            : TimeSpan.Zero;

        PositionSlider.Maximum = Math.Max(1, total.TotalSeconds);
        PositionSlider.IsEnabled = true;
        PlayButton.IsEnabled = true;

        _playing = true;
        PlayButton.Content = "暂停";
        UpdatePlaybackPosition();
    }

    /// <summary>
    /// 播不了。
    /// </summary>
    /// <remarks>
    /// ⚠️ **不许静默**：<c>MediaElement</c> 用的是系统解码器，而这台机器上
    /// 有没有那一路解码器是**装完之后才知道**的。不说的话用户面对的是一个
    /// 黑框，而原因（缺解码器 / 文件坏了 / 路径不对）他一个都猜不到。
    /// </remarks>
    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _playing = false;
        _playTicker.Stop();
        PlayButton.Content = "播放";

        var system = e.ErrorException?.Message ?? "系统解码器不支持";

        // ⚠️ **留痕**（`AGENTS.md` §6 的「异常」、§6.1 的「catch 块不许静默吞掉」）：
        // 这件事**只有界面知道**，而它正是用户过几天会说的那句「你们这个录像打不开」。
        // 诊断包里没有它，事后只剩猜 —— 而原因在**这台机器**上（缺解码器），
        // 换台机器就复现不出来，更要留下当时的原话与编码。
        if (ResultsList.SelectedItem is SearchResultRow failed)
        {
            _host.Log(
                LogLevel.Warn, "回放",
                $"播不了：{system}（编码 {failed.Hit.Entry.Codec ?? "未知"}，"
                + $"证据 {failed.Hit.Entry.EvidenceId}）");
        }

        ShowPlaceholder(ExplainPlaybackFailure(system));
    }

    /// <summary>播不了时到底该说哪句话。</summary>
    /// <remarks>
    /// ⚠️ <b>系统那句话不能照抄</b>。文件明明在盘上（选中时已经核过
    /// <see cref="File.Exists(string)"/>），而 <c>MediaElement</c> 解不了这个编码时
    /// 报的原话是「找不到媒体文件。」—— 照抄等于把用户指去查一个**好端端在盘上**
    /// 的文件（2026-10-02 在 H.265 的成品上实测到的就是这么一句）。
    /// 所以这里说清楚三件事：**文件在**、问题在**这台电脑的解码能力**、哪一档编码最容易缺。
    /// </remarks>
    private string ExplainPlaybackFailure(string? systemMessage)
    {
        var system = systemMessage ?? "系统解码器不支持";

        if (ResultsList.SelectedItem is not SearchResultRow row)
        {
            return $"这段录像播不了：{system}";
        }

        var path = PathOf(row.Hit);

        // 真不在盘上时，系统那句话反倒是**对的** —— 照实说，别改。
        if (!File.Exists(path))
        {
            return $"这段录像播不了：{path} 不在盘上（{system}）";
        }

        return "这段录像播不了：文件在盘上，是这台电脑的播放组件打不开它"
            + CodecHint(row.Hit.Entry.Codec)
            + $"。（系统的原话：{system}）";
    }

    /// <summary>这一条是什么编码录的；认不出来就只说「多半是缺解码器」。</summary>
    /// <remarks>
    /// ⚠️ 名字走 <see cref="RecordingSpec.CodecLabel"/>（**唯一一处产出**）：
    /// 规格要求界面上**不得出现「HEVC」** —— 两个名字混用会让用户以为是两种编码。
    /// 所以连带指路时也只说「H.265 的解码器」与商店里那个「视频扩展」，
    /// **不印那个英文缩写**。
    /// </remarks>
    private static string CodecHint(string? storedCodec)
    {
        if (!Enum.TryParse<VideoCodec>(storedCodec, ignoreCase: true, out var codec))
        {
            return "，多半是缺这个编码的解码器（也可能是这个文件坏了）";
        }

        var label = new RecordingSpec(codec, VideoResolution.P1080).CodecLabel;

        return codec == VideoCodec.H265
            ? $"，这条录像是 {label} 编码的 —— Win10 默认不带 {label} 的解码器，"
              + "装上（Windows 商店里那个「视频扩展」就是干这个的）就能放"
            : $"，这条录像是 {label} 编码的，多半是缺解码器（也可能是这个文件坏了）";
    }

    private void UpdatePlaybackPosition()
    {
        var position = Player.Position;
        var total = Player.NaturalDuration.HasTimeSpan
            ? Player.NaturalDuration.TimeSpan
            : TimeSpan.Zero;

        // 拖动期间不要覆盖他拖到的位置。
        if (!_seeking)
        {
            PositionSlider.Value = position.TotalSeconds;
        }

        TimeText.Text = $"{Format(position)} / {Format(total)}";
    }

    private void OnSeekStarted(object sender, DragStartedEventArgs e) => _seeking = true;

    private void OnSeekCompleted(object sender, DragCompletedEventArgs e)
    {
        _seeking = false;
        Player.Position = TimeSpan.FromSeconds(PositionSlider.Value);
        UpdatePlaybackPosition();
    }

    private void ShowPlayer()
    {
        PlayerPlaceholder.Visibility = Visibility.Collapsed;
        Player.Visibility = Visibility.Visible;
    }

    private void ShowPlaceholder(string message)
    {
        Player.Visibility = Visibility.Hidden;
        PlayerPlaceholder.Visibility = Visibility.Visible;
        PlayerHintText.Text = message;
    }

    private static string Format(TimeSpan span) =>
        $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    // ─────────────────────────────────────────────
    // 争议锁定（规格 §3.6.5）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 锁定 / 解锁一条录像。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 锁定是**硬豁免**（§3.5.3②）：锁上之后保留期到了也不会自动清理本机这份。
    /// 用户拿它保住一条纠纷录像 —— 而这里是电脑端**唯一**的入口。
    /// </para>
    /// <para>
    /// ⚠️ 值只写 <c>"true"</c> / <c>"false"</c>：判据
    /// （<see cref="EvidenceLock.IsLocked"/>）对**认不出来的值当锁着**
    /// —— 写 <c>'1'</c> 之类会让用户**解不开**，而界面上看不出为什么。
    /// </para>
    /// <para>
    /// ⚠️ 解锁**不是删那一行**，是再追加一条 <c>false</c>
    /// （标签表追加写、后者胜出）—— 母仓 §6.2：数据删除必须极度克制。
    /// </para>
    /// </remarks>
    private async void OnToggleLock(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string evidenceId }
            || string.IsNullOrEmpty(evidenceId))
        {
            return;
        }

        try
        {
            // 当前锁没锁 —— **从标签读**（判据同一处），不是从按钮文字猜：
            // 按钮文字可能是上一次检索时的旧值。
            var labels = await _host.Services.Labels.GetForEvidenceAsync(evidenceId);
            var locked = EvidenceLock.IsLocked(labels);

            await _host.Services.Labels.SetAsync(
                evidenceId, LabelKeys.Locked, locked ? "false" : "true");

            // ⚠️ **留痕**（`AGENTS.md` §6「状态变更」）：这一个标签直接决定
            // 那条录像**会不会被清理**（§3.5.3② 的硬豁免），而在这之前
            // 成功路径只改了一行界面文案 —— 事后没人能回答「这条是谁、什么时候锁的」。
            _host.Log(
                LogLevel.Info, "锁定",
                $"{(locked ? "解除锁定" : "锁定")} {evidenceId}");

            // 重检索一遍，那一格（以及「已锁定 / 锁定」）才会跟着变。
            await SearchAsync();

            // ⚠️ 话里**不带那串证据 ID**（`20261002T104903-886f9c…`）：这一行
            // 是左栏底部那条窄状态行（右边还压着【上一页】【下一页】），
            // 22 个字的一串 ID 根本放不下，会伸到按钮底下被盖住
            // （2026-10-02 实测）。哪一条刚被锁，**列表里那一行自己就写着**
            // （按钮已变成「已锁定」），而留痕要的那串 ID 在**上面那条日志**里。
            CountText.Text = locked
                ? "这条已解除锁定：会照常按保留期清理。"
                : "这条已锁定：保留期到了也不会自动清理。";
        }
        catch (Exception ex)
        {
            // I3：写不进去要说出来 —— 用户以为锁上了而其实没锁，
            // 那条录像会在保留期到的时候被清掉。
            CountText.Text = $"锁定没能保存：{ex.Message}";

            // ⚠️ 失败也要留痕：这句话只留在界面上，而用户多半已经把它划走了。
            _host.Log(LogLevel.Warn, "锁定", $"锁定没能保存（{evidenceId}）：{ex.Message}");
        }
    }

    // ─────────────────────────────────────────────
    // 导出原视频（规格 §3.7）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 把选中的那一条**原样**交到用户选的位置。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规格原话：「改掉分享连接，只分享视频本身无损完整视频」；电脑端导出到
    /// **用户自选路径**，而且**不能是电脑端存放录像的那个路径**。
    /// </para>
    /// <para>
    /// ⚠️ <b>不转码、不压缩、不裁剪、也不打码</b>（§3.7.1 / §3.6.6）——
    /// 所以这一条路就是「另存为」。导出的成品里面单上的收件人信息**会原样跟出去**，
    /// 那是需求方权衡后的选择，界面**不许**暗示做过隐私处理。
    /// </para>
    /// </remarks>
    private async void OnExportResult(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not SearchResultRow row)
        {
            CountText.Text = "先在列表里选一条，再点【导出原视频】。";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出原视频（不转码、不压缩）",
            // 默认文件名就是规格里那个显示名：`快递单号.mp4`。
            FileName = $"{row.Hit.Entry.Waybill.Value}.mp4",
            Filter = "视频文件|*.mp4|所有文件|*.*",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var result = await _host.Services.Exporter.ExportAsync(row.Hit.Entry, dialog.FileName);

        if (!result.Exported)
        {
            // I3：导出失败必须说出来 —— 用户以为交付了，而对方什么都没收到。
            MessageBox.Show(
                result.FailureReason ?? "导出失败。",
                "导出原视频", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CountText.Text = $"已导出到：{result.TargetPath}";

        // 顺手把它所在的文件夹打开 —— 「交付」这个动作的下一步通常就是把文件发出去。
        ShellOpen.Try(result.TargetPath!);
    }

    // ─────────────────────────────────────────────
    // 导出单号（设计图 `_41` 左栏）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 把**当前检索出来的这一批**写成一个 CSV 交到用户选的位置。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 导的是**筛出来的这一批**，不是整个库：这颗按钮就摆在日期范围与单号框底下，
    /// 「我刚筛出来的这一批」是它唯一说得通的读法。
    /// </para>
    /// <para>
    /// ⚠️ 文件必须带 UTF-8 的 BOM。中文版 Windows 上的 Excel / WPS 打开一个
    /// <b>没有 BOM</b> 的 UTF-8 CSV 时，会按 GBK 去解 —— 表头「单号」两个字
    /// 当场变成乱码，而用户只会以为「导出来的东西坏了」。
    /// </para>
    /// </remarks>
    private async void OnExportWaybills(object sender, RoutedEventArgs e)
    {
        if (_hits.Count == 0)
        {
            CountText.Text = "先检索出结果，再导出单号。";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出单号（CSV）",
            FileName = WaybillCsv.DefaultFileName,
            Filter = "CSV 文件|*.csv|所有文件|*.*",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(
                dialog.FileName,
                WaybillCsv.Build(_hits),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            CountText.Text = $"已导出 {_hits.Count} 条单号到：{dialog.FileName}";

            // 留痕（`AGENTS.md` §6）：这是一次「把库里的一批数据交出去」的动作。
            _host.Logger.Log(
                LogLevel.Info, "导出",
                $"导出了 {_hits.Count} 条单号",
                new Dictionary<string, object?> { ["目标"] = dialog.FileName });

            ShellOpen.Try(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // I3：写不出去要说出来 —— 用户以为导好了，而那个文件根本不存在。
            CountText.Text = $"写不出去：{ex.Message}";

            // ⚠️ 与成功那一条**同样带数据**：出事时要答的是「往哪儿写、写了几条」，
            // 而这两样恰恰只在参数里。
            _host.Logger.Log(
                LogLevel.Warn, "导出",
                $"单号没能导出：{ex.Message}",
                new Dictionary<string, object?>
                {
                    ["目标"] = dialog.FileName,
                    ["条数"] = _hits.Count,
                });
        }
    }

    // ─────────────────────────────────────────────
    // 导入录像（设计图 `_41` 左栏）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 把外面录来的一个视频文件收进库里。
    /// </summary>
    /// <remarks>
    /// 真正的活在 <c>RecordingImporter</c> 里（Core，可测）；这个窗口只负责
    /// 问清「哪一段、挂哪个单号、什么时候录的」—— 那三件事**必须由人来定**，
    /// 猜不得（见 <c>ImportRequest</c> 的说明）。
    /// </remarks>
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new ImportWindow(_host) { Owner = this };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 导进来一条就该看得见 —— 重搜一遍，它才会出现在列表里。
        // ⚠️ 当前筛选条件可能筛掉它（日期范围、单号、类型都对不上）：那时列表里
        // 没有它是**对的**，所以这句话要说清它到底进没进库。
        await SearchAsync();

        CountText.Text = dialog.ImportedSummary is { Length: > 0 } summary
            ? summary
            : "已导入。";
    }

    private static string? TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
}
