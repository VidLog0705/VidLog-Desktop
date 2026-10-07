using VidLog.Desktop.Core.Search;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「时间范围 ↔ 两个日期」（T27② 第 2 批从 <c>DataWindow</c> 搬进 Core）。
/// </summary>
/// <remarks>
/// ⚠️ 这一块错一天，**界面上一点都看不出来**：查出来的数看着挺像回事，
/// 只是少了一整天或者多了一整天。原先它在没有测试工程的 App 里。
/// </remarks>
public class DateRangeSelectionTests
{
    private static readonly DateOnly Today = new(2026, 3, 15);

    /// <summary>东八区。固定住，免得结果随跑测试的机器时区变。</summary>
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    [Fact]
    public void 最近N天是含今天在内的N天_不是往前推N天()
    {
        // ⚠️ 这条钉的是那个最容易写错的「减几天」：7 天 = 今天 + 往前 6 天。
        // 写成 `-7` 的话区间是 8 天，而屏幕上两个日期看着照样「挺对」。
        var week = DateRangeSelection.DatesFor("7", Today);
        Assert.Equal((new DateOnly(2026, 3, 9), Today), week);

        // 认不出来的 tag 走的是同一档。
        Assert.Equal(week, DateRangeSelection.DatesFor(null, Today));
        Assert.Equal(week, DateRangeSelection.DatesFor("不认识的档位", Today));
    }

    [Fact]
    public void 三十天与九十天同样含今天()
    {
        Assert.Equal((new DateOnly(2026, 2, 14), Today), DateRangeSelection.DatesFor("30", Today));
        Assert.Equal((new DateOnly(2025, 12, 16), Today), DateRangeSelection.DatesFor("90", Today));
    }

    [Fact]
    public void 本月是从一号到今天_不是往前三十天()
    {
        Assert.Equal((new DateOnly(2026, 3, 1), Today), DateRangeSelection.DatesFor("month", Today));
    }

    [Fact]
    public void 自定义不动日期()
    {
        Assert.Null(DateRangeSelection.DatesFor("custom", Today));
    }

    [Fact]
    public void 右端点是次日零点_结束那一天整日包含()
    {
        // ⚠️ 这条是这一块最贵的：直接拿结束日当上界的话，那天最后一个小时录的
        // 全都不算。跨月也要进位正确（3 月 31 日 → 4 月 1 日零点）。
        var (from, to) = DateRangeSelection.HalfOpen(
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), Today, Offset);

        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, Offset), from);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, Offset), to);
    }

    [Fact]
    public void 日期空着就退回默认区间_不许变成null()
    {
        var (from, to) = DateRangeSelection.HalfOpen(null, null, Today, Offset);

        Assert.Equal(new DateTimeOffset(2026, 3, 9, 0, 0, 0, Offset), from);
        Assert.Equal(new DateTimeOffset(2026, 3, 16, 0, 0, 0, Offset), to);
    }

    [Fact]
    public void 两个日期选反了也能用_交换的是日期不是端点()
    {
        // 用户先点结束再点开始是常事。
        var (from, to) = DateRangeSelection.HalfOpen(
            new DateOnly(2026, 3, 31), new DateOnly(2026, 3, 1), Today, Offset);

        // ⚠️ 交换之后右端点**仍是**次日零点 —— 只把两个 `DateTimeOffset` 对调的话，
        // 交出去的就是一个从 4 月 1 日到 3 月 1 日的**空区间**。
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, Offset), from);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, Offset), to);
    }
}
