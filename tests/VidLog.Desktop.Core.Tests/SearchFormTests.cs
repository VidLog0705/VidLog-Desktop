using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Search;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 检索那一屏的条件映射与分页算术（T27② 第 2 批从 <c>SearchWindow</c> 搬进 Core）。
/// </summary>
/// <remarks>
/// ⚠️ 原先它在没有测试工程的 App 里 —— 而这里写错**界面上看不出来**：
/// 条件映射错了是一份少了半截的列表，分页算错了是「共 N 条」跟翻得到的页数对不上。
/// </remarks>
public class SearchFormTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    [Fact]
    public void 单号空白就等于没填_有值就去掉首尾空格()
    {
        Assert.Null(Query(waybillText: "   ").WaybillText);
        Assert.Null(Query(waybillText: "").WaybillText);
        Assert.Null(Query(waybillText: null).WaybillText);
        Assert.Equal("SF123", Query(waybillText: "  SF123  ").WaybillText);
    }

    [Theory]
    [InlineData("Prefix", WaybillMatchMode.Prefix)]
    [InlineData("Fuzzy", WaybillMatchMode.Contains)]
    [InlineData("Exact", WaybillMatchMode.Exact)]
    [InlineData("", WaybillMatchMode.Exact)]
    [InlineData(null, WaybillMatchMode.Exact)]
    public void 匹配方式按tag映射_认不出来当精确(string? tag, WaybillMatchMode expected)
    {
        // ⚠️ 「精确」是那条兜底 —— 认不出来的 tag 往**最窄**的那一档退，
        // 而不是最宽：宽了会让用户拿到一份他以为筛过的列表。
        Assert.Equal(expected, Query(matchTag: tag).MatchMode);
    }

    [Theory]
    [InlineData("Outbound", BusinessType.Outbound)]
    [InlineData("Return", BusinessType.Return)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 业务类型按tag映射_认不出来就是不限(string? tag, BusinessType? expected)
    {
        Assert.Equal(expected, Query(businessTag: tag).BusinessType);
    }

    [Fact]
    public void 日期是半开区间_结束那天整日包含()
    {
        var query = Query(from: new DateOnly(2026, 3, 15), to: new DateOnly(2026, 3, 20));

        Assert.Equal(new DateTimeOffset(2026, 3, 15, 0, 0, 0, Offset), query.From);
        Assert.Equal(new DateTimeOffset(2026, 3, 21, 0, 0, 0, Offset), query.To);
    }

    [Fact]
    public void 日期没选就是不限_不是当成今天()
    {
        var query = Query(from: null, to: null);

        Assert.Null(query.From);
        Assert.Null(query.To);
    }

    [Fact]
    public void 条数上限必须顶高_否则共N条是假的()
    {
        // ⚠️ `RecordingQuery.Limit` 默认只有 200，而分页在**客户端**切 ——
        // 不顶高的话超过 200 条的结果会被悄悄截掉，「共 N 条」是编的，
        // 界面上一点都看不出来。
        Assert.Equal(int.MaxValue, Query().Limit);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(50, 1)]
    [InlineData(51, 2)]
    [InlineData(100, 2)]
    [InlineData(101, 3)]
    public void 页数是向上取整_而且空结果也算一页(int total, int expected)
    {
        // ⚠️ 空结果必须是 1 页而不是 0 页：给 0 的话界面上会出「第 x / 0 页」。
        Assert.Equal(expected, SearchForm.PageCount(total, pageSize: 50));
    }

    [Fact]
    public void 条数那句话_零条与有结果各说各的()
    {
        Assert.Equal("共 0 条", SearchForm.SummaryText(0, 0, 1));

        // ⚠️ `page` 从 0 起，说给人听的是从 1 起。
        Assert.Equal("共 51 条，第 1 / 2 页", SearchForm.SummaryText(51, 0, 2));
        Assert.Equal("共 51 条，第 2 / 2 页", SearchForm.SummaryText(51, 1, 2));
    }

    [Fact]
    public void 还能不能往后翻_到了最后一页就不行()
    {
        Assert.True(SearchForm.CanGoNext(0, 2));
        Assert.False(SearchForm.CanGoNext(1, 2));

        // 空结果只有一页 ⇒ 两边的按钮都不该亮。
        Assert.False(SearchForm.CanGoNext(0, SearchForm.PageCount(0, 50)));
    }

    private static RecordingQuery Query(
        string? waybillText = null,
        string? matchTag = null,
        string? businessTag = null,
        DateOnly? from = null,
        DateOnly? to = null) =>
        SearchForm.BuildQuery(waybillText, matchTag, businessTag, from, to, Offset);
}
