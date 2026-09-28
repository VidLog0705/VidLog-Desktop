using System.Windows;
using System.Windows.Controls;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的同名类型都带进来。这里钉死成 WPF 的那套。
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Search;

namespace VidLog.Desktop.App;

/// <summary>图里的一行（一个时间桶）。</summary>
/// <param name="Ratio">
/// 这一行相对**最高的那一行**的比例（0–1）。条的长度用它。
/// </param>
internal sealed record BucketRow(string Label, double Ratio, string Value);

/// <summary>
/// 打包数据深度分析（照需求方设计图 `_40`）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 这个窗是**只读**的：聚合、过滤、格式化全在这里做，一条录像都不动。
/// 它不写索引、不删文件、不碰标签。
/// </para>
/// <para>
/// ⚠️ <b>只有能从索引算出来的东西才画</b>（规格 §13.1）。图上这类页常见的
/// 「上传成功率」「订单按时率」本仓算不出来（订单联动要至今未开工的服务端 M6）
/// —— 这里没有，**也不许补一个假的**。
/// </para>
/// <para>
/// ⚠️ 统计走的 <c>RecordingStats</c> 与「按空间清理」**共用同一套字节系数**
/// （<c>CleanupPlanner.EstimateBytes</c>）。在这一页另算一份的话，同一块盘会在
/// 两个页面上报出两个容量，而用户会以为其中一个在骗他。
/// </para>
/// </remarks>
public partial class DataWindow : Window
{
    private readonly AppHost _host;

    /// <summary>本次取回来的录像。过滤已经由检索做掉了，这里只负责聚合。</summary>
    private IReadOnlyList<RecordingEntry> _entries = [];

    /// <summary>
    /// 正在把「时间范围」里的日期写进两个 <see cref="DatePicker"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 没有它的话会死循环：改日期 → 把下拉切到「自定义」 → 下拉又去写日期。
    /// 而且 <c>SelectionChanged</c>/<c>SelectedDateChanged</c> 在
    /// <c>InitializeComponent</c> 里**就会先响一次**（XAML 里设了
    /// <c>SelectedIndex="0"</c>），所以两个处理器都得先看这个旗子。
    /// </remarks>
    private bool _syncing;

    public DataWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            SyncDatesFromRange();
            await ReloadAsync();
        };
    }

    // ─────────────────────────────────────────────
    // 筛选
    // ─────────────────────────────────────────────

    private static string? TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !IsLoaded)
        {
            return;
        }

        SyncDatesFromRange();
        _ = ReloadAsync();
    }

    private void OnGranularityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        // 粒度只影响怎么分组，**不用重新读索引** —— 手上那份就是全的。
        Render();
    }

    private void OnDimensionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        Render();
    }

    private void OnDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !IsLoaded)
        {
            return;
        }

        // 用户手改了日期 ⇒ 时间范围那一格跟着变成「自定义」，
        // 否则下拉上写「最近 7 天」而实际查的是别的区间。
        _syncing = true;
        RangeCombo.SelectedIndex = 4;
        _syncing = false;

        _ = ReloadAsync();
    }

    /// <summary>按「时间范围」把两个日期填上。选了「自定义」就不动它们。</summary>
    private void SyncDatesFromRange()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        var (from, to) = TagOf(RangeCombo) switch
        {
            // 「最近 7 天」= 含今天在内的 7 天，不是从今天往前 7 天。
            "30" => (today.AddDays(-29), today),
            "90" => (today.AddDays(-89), today),
            "month" => (new DateOnly(today.Year, today.Month, 1), today),
            "custom" => ((DateOnly?)null, (DateOnly?)null),
            _ => (today.AddDays(-6), today),
        };

        if (from is null || to is null)
        {
            return;
        }

        _syncing = true;
        FromDate.SelectedDate = from.Value.ToDateTime(TimeOnly.MinValue);
        ToDate.SelectedDate = to.Value.ToDateTime(TimeOnly.MinValue);
        _syncing = false;
    }

    /// <summary>
    /// 界面上选的那个区间，转成半开区间的两个端点。
    /// </summary>
    /// <remarks>
    /// ⚠️ 右端点取**次日零点**：用户在「结束日期」里选的那一天要**整日包含**。
    /// 直接拿它当上界的话，那天最后一个小时录的全都不算 —— 而界面上一点都看不出来。
    /// </remarks>
    private (DateTimeOffset From, DateTimeOffset To) SelectedRange()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        // 日期被清空时给个兜底，不让它变成 null：这条路上出现 null
        // 会一路传到查询里，而「没设日期」与「日期是空的」在结果上
        // 长得一模一样 —— 与其猜，不如退回默认区间。
        var from = FromDate.SelectedDate is { } f ? DateOnly.FromDateTime(f) : today.AddDays(-6);
        var to = ToDate.SelectedDate is { } t ? DateOnly.FromDateTime(t) : today;

        // 选反了也能用（用户先点结束再点开始是常事）。
        if (to < from)
        {
            (from, to) = (to, from);
        }

        var offset = DateTimeOffset.Now.Offset;

        return (
            new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), offset),
            new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), offset));
    }

    private StatsGranularity Granularity() => TagOf(GranularityCombo) switch
    {
        "Week" => StatsGranularity.Week,
        "Month" => StatsGranularity.Month,
        _ => StatsGranularity.Day,
    };

    /// <summary>查看维度：<c>count</c> / <c>duration</c> / <c>bytes</c>。</summary>
    private string Dimension() =>
        DimDuration.IsChecked == true ? "duration"
        : DimBytes.IsChecked == true ? "bytes"
        : "count";

    // ─────────────────────────────────────────────
    // 统计
    // ─────────────────────────────────────────────

    private async Task ReloadAsync()
    {
        var (from, to) = SelectedRange();

        ChartStatusText.Text = "正在统计…";

        try
        {
            // ⚠️ `Limit` 默认只有 200，必须显式顶高：截断了的话「总件数」是假的，
            // 而界面上一点都看不出来（图上那条曲线会好端端地在某个日期断掉）。
            var hits = await _host.Services.Search.SearchAsync(new RecordingQuery
            {
                From = from,
                To = to,
                Limit = int.MaxValue,
            });

            _entries = hits.Select(h => h.Entry).ToList();
        }
        catch (Exception ex)
        {
            // ⚠️ 算不出来**不许让整页空着当没事**（§13.1）：把原因写在图表标题旁边，
            // 卡片留在 0 —— 但那一刻的 0 是「没算出来」而不是「没有录像」，
            // 所以下面会把这句写清楚。
            _entries = [];
            Render();
            ChartStatusText.Text = $"统计出错：{ex.Message}";
            return;
        }

        Render();
    }

    private void Render()
    {
        var (from, to) = SelectedRange();
        var by = Granularity();

        // ⚠️ 卡片与图**看的是同一批数据**（`Summarize` 与 `Aggregate` 各自
        // 按同一个区间过滤）。分头过滤的话两者迟早对不上，而对不上的时候
        // 没人知道该信哪个。
        var summary = RecordingStats.Summarize(_entries, from, to);
        var buckets = RecordingStats.Aggregate(_entries, from, to, by);

        // 「打包总件数」的大字是**单号数** —— 用户心里的「今天打了多少件」
        // 就是这个数。段数写在下面一行：分段录时一个单号可以有好几段，
        // 把段数当件数会把它算成好几件。
        TotalCountText.Text = $"{summary.WaybillCount} 件";
        TotalWaybillsText.Text = summary.Count == summary.WaybillCount
            ? $"共 {summary.Count} 段录像"
            : $"共 {summary.Count} 段录像（其中 {summary.Count - summary.WaybillCount} 段是同一个单号的续录）";

        TotalBytesText.Text = $"约 {Display.Bytes(summary.EstimatedBytes)}";
        TotalDurationText.Text = Display.Duration(summary.Duration);
        AverageText.Text = Display.Duration(summary.AveragePerWaybill);

        ChartTitleText.Text = $"{GranularityLabel(by)} · {DimensionLabel(Dimension())}";

        var rows = BuildRows(buckets, by);
        BucketList.ItemsSource = rows;

        // 空态：一句话摆在图表区的正中间，比一块大白框诚实得多。
        BucketEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BucketScroll.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // `Aggregate` 与 `Summarize` 用的是**同一个过滤条件**，所以
        // 「一个桶都没有」与「一段录像都没有」是同一件事，不用分两种情况说。
        ChartStatusText.Text = $"共 {rows.Count} 个时间段 · {summary.Count} 段录像";
    }

    private static string GranularityLabel(StatsGranularity by) => by switch
    {
        StatsGranularity.Week => "按周",
        StatsGranularity.Month => "按月",
        _ => "按日",
    };

    /// <remarks>
    /// ⚠️ 数量那一档写「录像段数」而不是图上两个字「数量」：图上的「数量」
    /// 在一张只有一个数字的卡片里没有歧义，但这里是**条的长度**，
    /// 是段还是件会直接改变每根条的长短 —— 写清楚，不靠猜。
    /// </remarks>
    private static string DimensionLabel(string dimension) => dimension switch
    {
        "duration" => "时长",
        "bytes" => "大小（约）",
        _ => "录像段数",
    };

    /// <summary>
    /// 图里的每一行。
    /// </summary>
    /// <remarks>
    /// 条的长度是**相对最高的那一行**，不是绝对值：一个只有 2 段的上午与一个
    /// 300 段的旺季放在一起，绝对刻度会把前者压成一条看不见的线。
    /// 具体数值在右边一列写着，所以「相对」不丢信息。
    /// </remarks>
    private IReadOnlyList<BucketRow> BuildRows(IReadOnlyList<StatsBucket> buckets, StatsGranularity by)
    {
        var dimension = Dimension();

        double Measure(StatsBucket b) => dimension switch
        {
            "duration" => b.Duration.TotalSeconds,
            "bytes" => b.EstimatedBytes,
            _ => b.Count,
        };

        var max = buckets.Count == 0 ? 0 : buckets.Max(Measure);

        return buckets.Select(b =>
        {
            var value = Measure(b);

            return new BucketRow(
                Label(b.Start, by),
                max <= 0 ? 0 : value / max,
                dimension switch
                {
                    "duration" => Display.Duration(b.Duration),
                    "bytes" => $"约 {Display.Bytes(b.EstimatedBytes)}",
                    _ => $"{b.Count} 段",
                });
        }).ToList();
    }

    private static string Label(DateOnly start, StatsGranularity by) => by switch
    {
        StatsGranularity.Month => start.ToString("yyyy-MM"),
        // 「起」这个字不能省：按周时它是那一周的**周一**，不写的话
        // 会被读成「这一周就那一天有录像」。
        StatsGranularity.Week => $"{start:MM-dd} 起",
        _ => start.ToString("MM-dd"),
    };
}
