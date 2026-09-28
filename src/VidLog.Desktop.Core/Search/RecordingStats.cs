using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Search;

/// <summary>按什么粒度把录像分组。</summary>
public enum StatsGranularity
{
    /// <summary>按日历日。</summary>
    Day,

    /// <summary>按周 —— <b>周一起算</b>（国内习惯，不是周日）。</summary>
    Week,

    /// <summary>按月。</summary>
    Month,
}

/// <summary>一个时间桶。</summary>
/// <param name="Start">
/// 桶的**起点**（按粒度归一化过的：日 / 那周的周一 / 那月 1 号）。
/// ⚠️ 名字不叫 <c>Day</c>：按周按月时它不是一个日子，叫 Day 会误导下一个人。
/// </param>
/// <param name="EstimatedBytes">
/// ⚠️ <b>是估的，不是 stat 出来的</b> —— 与 <see cref="CleanupPlanner.EstimateBytes"/>
/// 用的是**同一套系数**（规格 §3.5.5 的连带项要求「不另起一个」）。
/// 界面上凡是显示它的地方**必须带「约」**。
/// </param>
public sealed record StatsBucket(
    DateOnly Start,
    int Count,
    long EstimatedBytes,
    TimeSpan Duration);

/// <summary>一段时间里的汇总。四张统计卡就是它。</summary>
/// <remarks>
/// ⚠️ <b><paramref name="Count"/> 与 <paramref name="WaybillCount"/> 是两个不同的数，
/// 都要给出来</b>：VidLog 里**一个单号可以有多段录像**（分段录，
/// 规格 §3.1.1），所以「录了多少段」与「打了多少个包」不是一回事。
/// 只报一个的话，界面上「件数」这个词必然是错的 —— 要么把一段算成一件，
/// 要么把多段算成一件。两个都给，「件」才有确定的含义。
/// </remarks>
/// <param name="Count">录像**段数**（索引里有几条）。</param>
/// <param name="WaybillCount">去重之后的**单号个数** —— 用户心里的「今天我打了多少件」。</param>
public sealed record StatsSummary(
    int Count, int WaybillCount, long EstimatedBytes, TimeSpan Duration)
{
    /// <summary>
    /// 平均单件用时（界面上那张橙色的卡）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 除以<b>单号数</b>，<b>不是段数</b>。图上那张卡写的是「平均单件用时」，
    /// 而这里「件」就是单号 —— 一个单号拆成两段录的话，按段平均会把它当成两件，
    /// 报出来的数会比用户的实际手感小一半。
    /// </remarks>
    public TimeSpan AveragePerWaybill =>
        WaybillCount == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Duration.Ticks / WaybillCount);
}

/// <summary>
/// 把录像按时间聚合起来（「打包数据深度分析」页，照需求方设计图）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>只做能从索引算出来的聚合</b>（规格 §13.1「只展示磁盘上真实可测的内容」）。
/// 设计图上还有「订单联动」「上传成功率」这类卡片，本仓**算不出来**
/// （订单联动要至今未开工的服务端 M6）—— 那些不在这里，也不该在界面上编一个。
/// </para>
/// <para>
/// ⚠️ 字节数走 <see cref="CleanupPlanner.EstimateBytes"/>，
/// 与「按空间清理」**同一个函数**：另写一个系数的话，同一块盘会在两个页面上
/// 报出两个容量，而用户会以为其中一个在骗他。
/// </para>
/// </remarks>
public static class RecordingStats
{
    /// <summary>
    /// 按 <paramref name="by"/> 分桶，只统计 <c>[from, to)</c> 里的那些。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 区间是**半开**的（含 <paramref name="from"/>、不含 <paramref name="to"/>）——
    /// 与 <see cref="RecordingQuery"/> 一致。界面把「结束那天」传成次日零点，
    /// 于是那一天是**整日包含**的。
    /// </para>
    /// <para>
    /// ⚠️ <b>不补空桶</b>：只有真有录像的那些桶会出现在结果里。
    /// 补的话「最近 7 天」在用户选了三年时会是上千行；而补一部分（比如只补最近 N 个）
    /// 又会让「哪些桶是空的」变得看不出来。界面那边**必须写明「只列有录像的」**，
    /// 否则一条断掉的时间轴会被当成连续的。
    /// </para>
    /// <para>
    /// ⚠️ 分桶按<b>本地日历</b>（<see cref="RecordingEntry.StartedAt"/> 转本地时区的日期）：
    /// 用户问的是「9 月 22 号录了多少」，而不是「UTC 的 9 月 22 号」。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<StatsBucket> Aggregate(
        IEnumerable<RecordingEntry> entries,
        DateTimeOffset from,
        DateTimeOffset to,
        StatsGranularity by)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return entries
            .Where(e => e.StartedAt >= from && e.StartedAt < to)
            .GroupBy(e => BucketOf(DateOnly.FromDateTime(e.StartedAt.ToLocalTime().DateTime), by))
            .OrderBy(g => g.Key)
            .Select(g => new StatsBucket(
                g.Key,
                g.Count(),
                g.Sum(CleanupPlanner.EstimateBytes),
                g.Aggregate(TimeSpan.Zero, (sum, e) => sum + e.Duration)))
            .ToList();
    }

    /// <summary>同一批录像的汇总（四张统计卡）。</summary>
    /// <remarks>
    /// ⚠️ 它**重新过滤一遍**而不是让调用方先筛再传：调用方筛完再传的话，
    /// 「卡片上的总数」与「图里的柱子之和」会由两段代码各自算 ——
    /// 那两处迟早会对不上，而对不上的时候没人知道该信哪个。
    /// </remarks>
    public static StatsSummary Summarize(
        IEnumerable<RecordingEntry> entries, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var inRange = entries.Where(e => e.StartedAt >= from && e.StartedAt < to).ToList();

        return new StatsSummary(
            inRange.Count,
            // 单号用 `Value` 比而不是比 `WaybillNumber` 本身：`Value` 是
            // **归一化过**的那个（`WaybillNumber.Normalize`），
            // 大小写/空格/连字符的差异到这里已经抹平了。
            inRange.Select(e => e.Waybill.Value).Distinct(StringComparer.Ordinal).Count(),
            inRange.Sum(CleanupPlanner.EstimateBytes),
            inRange.Aggregate(TimeSpan.Zero, (sum, e) => sum + e.Duration));
    }

    /// <summary>把一个日子归到它所属的那个桶的起点。</summary>
    /// <remarks>
    /// ⚠️ 周一起算：<c>DayOfWeek.Sunday</c> 是 0，直接用它当「往前推几天」
    /// 会让周日变成一周的**第一天**（而国内习惯是最后一天）。
    /// <c>((int)d + 6) % 7</c> 把周一映射成 0、周日映射成 6。
    /// </remarks>
    public static DateOnly BucketOf(DateOnly day, StatsGranularity by) => by switch
    {
        StatsGranularity.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        StatsGranularity.Month => new DateOnly(day.Year, day.Month, 1),
        _ => day,
    };
}
