using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Search;

/// <summary>
/// 检索那一屏上填的东西 → 查询条件、以及结果列表分页用的那几个数。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 这些原先写在 <c>SearchWindow</c> 里（T27② 第 2 批搬过来）—— 那个工程没有测试
/// 工程，而这里写错**界面上看不出来**：条件映射错了是一份少了半截的列表，
/// 分页算错了是「共 N 条」跟实际翻得到的页数对不上。
/// </para>
/// </remarks>
public static class SearchForm
{
    /// <summary>
    /// 把界面上的条件收成一次查询。
    /// </summary>
    /// <param name="waybillText">单号那一格里的原文（没规整过）。</param>
    /// <param name="matchTag">匹配方式下拉项上的 tag（<c>"Prefix"</c> / <c>"Fuzzy"</c> / 其它=精确）。</param>
    /// <param name="businessTag">业务类型下拉项上的 tag（<c>"Outbound"</c> / <c>"Return"</c> / 其它=不限）。</param>
    /// <param name="from">开始日期；没选就是 <see langword="null"/>（= 不限）。</param>
    /// <param name="to">结束日期；没选就是 <see langword="null"/>（= 不限）。</param>
    /// <param name="offset">本地时区偏移。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>结束那一天要整日包含</b>，所以右边界取**次日零点**（半开区间）——
    /// 见 <see cref="DateRangeSelection.EndOfDay"/>。
    /// </para>
    /// <para>
    /// ⚠️ <b><c>Limit</c> 默认只有 200，必须显式顶高</b>：分页在客户端切，
    /// 截断了的话「共 N 条」是假的，而界面上一点都看不出来。
    /// </para>
    /// </remarks>
    public static RecordingQuery BuildQuery(
        string? waybillText,
        string? matchTag,
        string? businessTag,
        DateOnly? from,
        DateOnly? to,
        TimeSpan offset)
    {
        var text = waybillText?.Trim();

        return new RecordingQuery
        {
            WaybillText = string.IsNullOrWhiteSpace(text) ? null : text,
            MatchMode = matchTag switch
            {
                "Prefix" => WaybillMatchMode.Prefix,
                "Fuzzy" => WaybillMatchMode.Contains,
                _ => WaybillMatchMode.Exact,
            },
            From = from is { } f ? DateRangeSelection.StartOfDay(f, offset) : null,
            To = to is { } t ? DateRangeSelection.EndOfDay(t, offset) : null,
            BusinessType = businessTag switch
            {
                "Outbound" => BusinessType.Outbound,
                "Return" => BusinessType.Return,
                _ => null,
            },
            Limit = int.MaxValue,
        };
    }

    /// <summary>
    /// 一共几页。
    /// </summary>
    /// <remarks>
    /// ⚠️ 一条都没有时是 **1 页**，不是 0 页：那句话要说成「共 0 条」，
    /// 而按钮的启用判定要拿它比 —— 给 0 的话「第 x / 0 页」这种东西就出来了。
    /// </remarks>
    public static int PageCount(int total, int pageSize) =>
        Math.Max(1, (total + pageSize - 1) / pageSize);

    /// <summary>结果条数那句话。</summary>
    public static string SummaryText(int total, int page, int pageCount) =>
        total == 0
            ? "共 0 条"
            : $"共 {total} 条，第 {page + 1} / {pageCount} 页";

    /// <summary>还能不能往后翻（<paramref name="page"/> 从 0 起）。</summary>
    public static bool CanGoNext(int page, int pageCount) => page + 1 < pageCount;
}
