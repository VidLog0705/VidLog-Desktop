namespace VidLog.Desktop.Core.Search;

/// <summary>
/// 「时间范围」那一格与它旁边两个日期之间的换算。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 这些换算原先写在 <c>DataWindow</c> 里（T27② 第 2 批搬过来）—— 那个工程
/// 没有测试工程，而这里错一天**界面上一点都看不出来**：查出来的数看着挺像回事，
/// 只是少了一整天或者多了一整天。
/// </para>
/// </remarks>
public static class DateRangeSelection
{
    /// <summary>默认区间含今天在内一共几天（「最近 7 天」的 7）。</summary>
    public const int DefaultDays = 7;

    /// <summary>某个日期档位覆盖的那两天。返回 <see langword="null"/> = 「自定义」，不动用户选的日期。</summary>
    /// <param name="rangeTag">下拉项上的 tag（<c>"7"</c> / <c>"30"</c> / <c>"90"</c> / <c>"month"</c> / <c>"custom"</c>）。</param>
    /// <param name="today">今天（注入是为了能测）。</param>
    /// <remarks>
    /// ⚠️ <b>「最近 N 天」是含今天在内的 N 天，不是从今天往前推 N 天</b> ——
    /// 所以是 <c>-29</c> / <c>-89</c>，不是 <c>-30</c> / <c>-90</c>。
    /// 认不出来的 tag 退回默认区间。
    /// </remarks>
    public static (DateOnly From, DateOnly To)? DatesFor(string? rangeTag, DateOnly today) => rangeTag switch
    {
        // 「最近 7 天」= 含今天在内的 7 天，不是从今天往前 7 天。
        "30" => (today.AddDays(-29), today),
        "90" => (today.AddDays(-89), today),
        "month" => (new DateOnly(today.Year, today.Month, 1), today),
        "custom" => ((DateOnly, DateOnly)?)null,
        _ => Default(today),
    };

    /// <summary>
    /// 用户选的那两个日期 → 查询用的**半开区间**。
    /// </summary>
    /// <param name="from">开始日期；空着就退回默认区间的开始。</param>
    /// <param name="to">结束日期；空着就当成今天。</param>
    /// <param name="today">今天（注入是为了能测）。</param>
    /// <param name="offset">本地时区偏移。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>右端点取次日零点</b>：用户在「结束日期」里选的那一天要**整日包含**。
    /// 直接拿它当上界的话，那天最后一个小时录的全都不算 —— 而界面上一点都看不出来。
    /// </para>
    /// <para>
    /// ⚠️ <b>日期被清空时给个兜底，不让它变成 null</b>：这条路上出现 null 会一路
    /// 传到查询里，而「没设日期」与「日期是空的」在结果上长得一模一样 ——
    /// 与其猜，不如退回默认区间。
    /// </para>
    /// <para>
    /// ⚠️ <b>选反了也能用</b>（用户先点结束再点开始是常事）。交换的是**日期**，
    /// 所以交出去的仍然是左闭右开的两个端点。
    /// </para>
    /// </remarks>
    public static (DateTimeOffset From, DateTimeOffset To) HalfOpen(
        DateOnly? from,
        DateOnly? to,
        DateOnly today,
        TimeSpan offset)
    {
        var first = from ?? Default(today).From;
        var last = to ?? today;

        if (last < first)
        {
            (first, last) = (last, first);
        }

        return (StartOfDay(first, offset), EndOfDay(last, offset));
    }

    /// <summary>那一天的**起点**（该日零点，本地偏移）。</summary>
    public static DateTimeOffset StartOfDay(DateOnly day, TimeSpan offset) =>
        new(day.ToDateTime(TimeOnly.MinValue), offset);

    /// <summary>那一天的上界：**次日**零点 —— 于是这一天是整日包含的。</summary>
    public static DateTimeOffset EndOfDay(DateOnly day, TimeSpan offset) =>
        new(day.AddDays(1).ToDateTime(TimeOnly.MinValue), offset);

    private static (DateOnly From, DateOnly To) Default(DateOnly today) =>
        (today.AddDays(-(DefaultDays - 1)), today);
}
