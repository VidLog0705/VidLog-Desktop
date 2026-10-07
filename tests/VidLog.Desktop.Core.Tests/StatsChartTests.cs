using VidLog.Desktop.Core.Search;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「打包数据深度分析」那张图怎么画（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// ⚠️ 这里最坏的一种错是**除以零**与**相对长度算反**：前者让整张图空掉
/// （用户看到的是「今天没数据」，而其实只是最大值是 0），后者让最高的那一行
/// 反而最短。原先长在 <c>DataWindow</c> 里，那个工程没有测试工程。
/// </remarks>
public class StatsChartTests
{
    private static readonly DateOnly 一号 = new(2026, 10, 1);

    private static StatsBucket 桶(
        int day, int count, long bytes = 0, TimeSpan duration = default) =>
        new(new DateOnly(2026, 10, day), count, bytes, duration);

    [Fact]
    public void 最高的那一行是满格()
    {
        var rows = StatsChart.Rows(
            [桶(1, 2), 桶(2, 8)], StatsGranularity.Day, "count");

        Assert.Equal(2, rows.Count);
        Assert.Equal(0.25, rows[0].Ratio, 3);
        Assert.Equal(1.0, rows[1].Ratio, 3);
    }

    [Fact]
    public void 一段录像都没有时给空表而不是抛()
    {
        var rows = StatsChart.Rows([], StatsGranularity.Day, "count");

        Assert.Empty(rows);
    }

    [Fact]
    public void 量全是零时不许除以零()
    {
        // ⚠️ 一整段时间里每桶都是 0 段（只在别的档上才有的数据），
        // 或者选「时长」而每桶时长都是 0 —— max 会是 0，
        // `value / max` 不挡住就是 NaN（条的长度变成 NaN，整张图画不出来）。
        var rows = StatsChart.Rows(
            [桶(1, 0), 桶(2, 0)], StatsGranularity.Day, "duration");

        Assert.All(rows, row => Assert.Equal(0, row.Ratio));
    }

    [Fact]
    public void 换了量条的长短跟着换()
    {
        // 一号：2 段但 100 MB；二号：8 段但 10 MB。
        // 「数量」那档二号最长，「大小」那档一号最长 —— 选错档的话两者反着来。
        StatsBucket[] buckets =
        [
            桶(1, 2, bytes: 100 * 1024 * 1024),
            桶(2, 8, bytes: 10 * 1024 * 1024),
        ];

        var 按段数 = StatsChart.Rows(buckets, StatsGranularity.Day, "count");
        var 按大小 = StatsChart.Rows(buckets, StatsGranularity.Day, "bytes");

        Assert.True(按段数[0].Ratio < 按段数[1].Ratio);
        Assert.True(按大小[0].Ratio > 按大小[1].Ratio);
    }

    [Fact]
    public void 三个量各自写成什么字()
    {
        var bucket = 桶(1, 5, bytes: 2 * 1024 * 1024, duration: TimeSpan.FromMinutes(3));

        Assert.Equal("5 段", StatsChart.Rows([bucket], StatsGranularity.Day, "count")[0].Value);
        Assert.Equal("约 2 MB", StatsChart.Rows([bucket], StatsGranularity.Day, "bytes")[0].Value);
        Assert.Equal("3 分 0 秒", StatsChart.Rows([bucket], StatsGranularity.Day, "duration")[0].Value);
    }

    [Fact]
    public void 认不出来的量当成段数()
    {
        // 界面那个单选框返回的就是这几个键，多一个空档（比如还没选中）时
        // 不许把那一列留空 —— 留空的话图上每根条右边都是空白。
        var bucket = 桶(1, 5);

        Assert.Equal(
            StatsChart.Rows([bucket], StatsGranularity.Day, "count")[0].Value,
            StatsChart.Rows([bucket], StatsGranularity.Day, "没见过的键")[0].Value);
    }

    [Theory]
    [InlineData(StatsGranularity.Day, "10-01")]
    [InlineData(StatsGranularity.Week, "10-01 起")]
    [InlineData(StatsGranularity.Month, "2026-10")]
    public void 左边那一列按粒度写(StatsGranularity by, string expected)
    {
        var rows = StatsChart.Rows([桶(1, 1)], by, "count");

        Assert.Equal(expected, rows[0].Label);
    }

    [Fact]
    public void 按周要带上那个起字()
    {
        // ⚠️ 「起」这个字不能省：按周时这个日期是那一周的**周一**，
        // 不写的话会被读成「这一周就那一天有录像」。
        var rows = StatsChart.Rows([桶(1, 1)], StatsGranularity.Week, "count");

        Assert.Contains("起", rows[0].Label);
    }

    [Theory]
    [InlineData(StatsGranularity.Day, "按日")]
    [InlineData(StatsGranularity.Week, "按周")]
    [InlineData(StatsGranularity.Month, "按月")]
    public void 标题前半段(StatsGranularity by, string expected)
    {
        Assert.Equal(expected, StatsChart.GranularityLabel(by));
    }

    [Fact]
    public void 标题后半段与图上那三个键是同一套()
    {
        // ⚠️ 这里钉的是**字符串键**这条缝：`Rows` 与 `DimensionLabel` 各自
        // switch 同一组键，写错一个字的后果是「标题写着时长、条按段数画」，
        // 而两者都不报错。三个键必须都认得出来、且互不相同。
        string[] keys = ["duration", "bytes", "count"];

        var labels = keys.Select(StatsChart.DimensionLabel).ToList();

        Assert.Equal("时长", labels[0]);
        Assert.Equal("大小（约）", labels[1]);
        Assert.Equal("录像段数", labels[2]);
        Assert.Equal(3, labels.Distinct().Count());
    }

    [Fact]
    public void 认不出来的键在标题上也得有字()
    {
        Assert.Equal("录像段数", StatsChart.DimensionLabel("没见过的键"));
    }
}
