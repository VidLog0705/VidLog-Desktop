namespace VidLog.Desktop.Core.Search;

/// <summary>图里的一行（一个时间桶）。</summary>
/// <param name="Ratio">
/// 这一行相对**最高的那一行**的比例（0–1）。条的长度用它。
/// </param>
/// <remarks>
/// ⚠️ 2026-10-07 从 <c>DataWindow</c> 搬下来（T27② 第 4 批）时才从
/// <see langword="internal"/> 改成 <see langword="public"/> —— 搬到 Core 之后
/// 测试工程要看得见它。界面按属性名绑（<c>{Binding Label}</c>），不受影响。
/// </remarks>
public sealed record BucketRow(string Label, double Ratio, string Value);

/// <summary>
/// 「打包数据深度分析」那张图怎么画（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>条的长度是相对最高的那一行，不是绝对值。</b>一个只有 2 段的上午与一个
/// 300 段的旺季放在一起，绝对刻度会把前者压成一条看不见的线。
/// 具体数值在右边一列写着，所以「相对」不丢信息。
/// </para>
/// <para>
/// ⚠️ 它原先长在 <c>DataWindow</c> 里，而那个工程没有测试工程 ——
/// 「最大值那一行必须是满格」与「一桶都没有时不许除以零」都没有东西能挡。
/// </para>
/// </remarks>
public static class StatsChart
{
    /// <summary>把时间桶摆成图里的行。</summary>
    /// <param name="buckets">按时间排好的桶（<see cref="RecordingStats.Aggregate"/> 给的）。</param>
    /// <param name="by">粒度 —— 只影响左边那一列怎么写。</param>
    /// <param name="dimension">
    /// 条的长度按什么算：<c>"duration"</c> / <c>"bytes"</c> / 别的都当段数。
    /// ⚠️ 这三个键与 <see cref="DimensionLabel"/> 是**同一套**，
    /// 界面那个单选框返回的就是它们（见 <c>DataWindow.Dimension</c>）。
    /// </param>
    public static IReadOnlyList<BucketRow> Rows(
        IReadOnlyList<StatsBucket> buckets, StatsGranularity by, string dimension)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        // 桶是空的时 max 定成 0，下面那条 `max <= 0` 就把它挡在除法外面。
        var max = buckets.Count == 0 ? 0 : buckets.Max(bucket => Measure(bucket, dimension));

        return buckets.Select(bucket =>
        {
            var value = Measure(bucket, dimension);

            return new BucketRow(
                BucketLabel(bucket.Start, by),
                max <= 0 ? 0 : value / max,
                ValueText(bucket, dimension));
        }).ToList();
    }

    /// <summary>这一行条的长度按什么量算。</summary>
    private static double Measure(StatsBucket bucket, string dimension) => dimension switch
    {
        "duration" => bucket.Duration.TotalSeconds,
        "bytes" => bucket.EstimatedBytes,
        _ => bucket.Count,
    };

    /// <summary>右边那一列的具体数值。</summary>
    /// <remarks>
    /// ⚠️ 数量那一档写「N 段」：这里是**条的长度**，是段还是件会直接改变
    /// 每根条的长短 —— 写清楚，不靠猜（VidLog 里一个单号可以有多段录像）。
    /// </remarks>
    private static string ValueText(StatsBucket bucket, string dimension) => dimension switch
    {
        "duration" => Display.Duration(bucket.Duration),
        "bytes" => $"约 {Display.Bytes(bucket.EstimatedBytes)}",
        _ => $"{bucket.Count} 段",
    };

    /// <summary>图上那一列时间。</summary>
    /// <remarks>
    /// ⚠️ 「起」这个字**不能省**：按周时它是那一周的**周一**，不写的话
    /// 会被读成「这一周就那一天有录像」。
    /// </remarks>
    public static string BucketLabel(DateOnly start, StatsGranularity by) => by switch
    {
        StatsGranularity.Month => start.ToString("yyyy-MM"),
        StatsGranularity.Week => $"{start:MM-dd} 起",
        _ => start.ToString("MM-dd"),
    };

    /// <summary>标题前半段：「按日 / 按周 / 按月」。</summary>
    public static string GranularityLabel(StatsGranularity by) => by switch
    {
        StatsGranularity.Week => "按周",
        StatsGranularity.Month => "按月",
        _ => "按日",
    };

    /// <summary>标题后半段：「时长 / 大小（约） / 录像段数」。</summary>
    /// <remarks>
    /// ⚠️ 写「录像段数」而不是图上两个字「数量」：图上的「数量」在一张只有一个
    /// 数字的卡片里没有歧义，但这里是**条的长度**，段还是件会直接改变每根条的长短。
    /// </remarks>
    public static string DimensionLabel(string dimension) => dimension switch
    {
        "duration" => "时长",
        "bytes" => "大小（约）",
        _ => "录像段数",
    };
}
