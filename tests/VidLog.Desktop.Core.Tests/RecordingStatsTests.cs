using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Search;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「打包数据深度分析」的聚合（<see cref="RecordingStats"/>）。
/// </summary>
/// <remarks>
/// ⚠️ <b>这个文件里的时间一律用 <see cref="LocalAt"/> 造</b>，不许直接写
/// <c>new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.FromHours(8))</c>：
/// 分桶按的是**运行机器的本地日历**（用户问的是「22 号录了多少」），
/// 而开发机是 +08、CI 是 UTC —— 写死偏移的话同一份数据会在两处落进不同的桶，
/// 本地绿、CI 红，或者反过来。
/// </remarks>
public sealed class RecordingStatsTests
{
    /// <summary>本机时区下的一个时刻。</summary>
    private static DateTimeOffset LocalAt(int y, int m, int d, int hour = 10, int minute = 0)
    {
        var naive = new DateTime(y, m, d, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(naive, TimeZoneInfo.Local.GetUtcOffset(naive));
    }

    private static RecordingEntry Entry(
        string id, int y, int m, int d, double minutes,
        string? codec = "h264", string? resolution = "1080p", string waybill = "SF1000000001")
    {
        var start = LocalAt(y, m, d);

        return new RecordingEntry(
            id,
            $"session-{id}",
            WaybillNumber.Parse(waybill),
            start,
            start.AddMinutes(minutes),
            TimeSpan.FromMinutes(minutes),
            RelativePath.Parse($"2026/{m:00}/{d:00}/{waybill}/{id}.mp4"),
            ContentHash.Parse(new string('a', 64)),
            "device-1",
            codec,
            resolution);
    }

    private static readonly DateTimeOffset From = LocalAt(2026, 9, 1, 0);
    private static readonly DateTimeOffset To = LocalAt(2026, 10, 1, 0);

    [Fact]
    public void 空集_不炸也不编数()
    {
        Assert.Empty(RecordingStats.Aggregate([], From, To, StatsGranularity.Day));

        var summary = RecordingStats.Summarize([], From, To);

        Assert.Equal(0, summary.Count);
        Assert.Equal(0, summary.WaybillCount);
        Assert.Equal(0, summary.EstimatedBytes);
        Assert.Equal(TimeSpan.Zero, summary.Duration);

        // ⚠️ 零件时**不许除零**。界面上那一格写「0 秒」，不是 NaN 或崩溃。
        Assert.Equal(TimeSpan.Zero, summary.AveragePerWaybill);
    }

    [Fact]
    public void 单条_自己一个桶()
    {
        var bucket = Assert.Single(
            RecordingStats.Aggregate([Entry("e1", 2026, 9, 22, 1.5)], From, To, StatsGranularity.Day));

        Assert.Equal(new DateOnly(2026, 9, 22), bucket.Start);
        Assert.Equal(1, bucket.Count);
        Assert.Equal(TimeSpan.FromMinutes(1.5), bucket.Duration);
    }

    [Fact]
    public void 跨日的分成两个桶_并且按时间升序()
    {
        var buckets = RecordingStats.Aggregate(
            [
                Entry("e2", 2026, 9, 23, 1),
                Entry("e1", 2026, 9, 22, 1),
                // 同一天的第二条：它不该开一个新桶，只该让 22 号那个桶变成 2。
                Entry("e3", 2026, 9, 22, 2),
            ],
            From,
            To,
            StatsGranularity.Day);

        Assert.Equal(2, buckets.Count);

        // ⚠️ 顺序是**升序**：图里从上往下是时间轴，倒过来的话「最近几天」
        // 会跑到最上面，与用户对时间轴的直觉相反。
        Assert.Equal(new DateOnly(2026, 9, 22), buckets[0].Start);
        Assert.Equal(new DateOnly(2026, 9, 23), buckets[1].Start);

        Assert.Equal(2, buckets[0].Count);
        Assert.Equal(TimeSpan.FromMinutes(3), buckets[0].Duration);
        Assert.Equal(1, buckets[1].Count);
    }

    [Fact]
    public void 桶里的字节数_与清理那套系数逐条对得上()
    {
        // ⚠️ 这一条是「不另起一个系数」的守卫（规格 §3.5.5 的连带项）。
        // 在聚合里另写一份系数的话，同一块盘会在「清理预告」与「数据分析」
        // 两个页面上报出两个容量 —— 而用户会以为其中一个在骗他。
        var a = Entry("e1", 2026, 9, 22, 1);
        var b = Entry("e2", 2026, 9, 22, 2);

        var bucket = Assert.Single(
            RecordingStats.Aggregate([a, b], From, To, StatsGranularity.Day));

        Assert.Equal(CleanupPlanner.EstimateBytes(a) + CleanupPlanner.EstimateBytes(b), bucket.EstimatedBytes);

        var summary = RecordingStats.Summarize([a, b], From, To);
        Assert.Equal(bucket.EstimatedBytes, summary.EstimatedBytes);
    }

    [Fact]
    public void 区间是半开的_右端点那天不算()
    {
        // 界面上「结束那天」传的是次日零点，所以那一天要**整日包含**。
        // 若这里写成闭区间，零点录的那一条会被算两次。
        var atTo = Entry("e-edge", 2026, 10, 1, 1);

        Assert.Empty(RecordingStats.Aggregate([atTo], From, To, StatsGranularity.Day));
        Assert.Equal(0, RecordingStats.Summarize([atTo], From, To).Count);

        // 左端点是**含**的。
        var atFrom = Entry("e-from", 2026, 9, 1, 1);
        Assert.Single(RecordingStats.Aggregate([atFrom], From, To, StatsGranularity.Day));
    }

    [Fact]
    public void 按周聚合_周一才是桶的起点()
    {
        // ⚠️ 这一条钉的是「周一起算」这个**选择**（不是周日）。
        // DayOfWeek.Sunday 是 0，直接拿它当「往前推几天」会把周日算成一周的第一天。
        var monday = new DateOnly(2026, 9, 28);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);

        var sunday = new DateOnly(2026, 9, 27);   // 同周的**前一天**，但它是周日
        Assert.Equal(DayOfWeek.Sunday, sunday.DayOfWeek);

        var buckets = RecordingStats.Aggregate(
            [Entry("e-sun", 2026, 9, 27, 1), Entry("e-mon", 2026, 9, 28, 1)],
            From,
            To,
            StatsGranularity.Week);

        // 周日那条属于**上一周**（9/21 起），周一那条属于 9/28 起的这一周 ——
        // 两个不同的桶。按周日起算的话它们会并成一个。
        Assert.Equal(2, buckets.Count);
        Assert.Equal(new DateOnly(2026, 9, 21), buckets[0].Start);
        Assert.Equal(new DateOnly(2026, 9, 28), buckets[1].Start);
    }

    [Fact]
    public void 按月聚合_一号是桶的起点()
    {
        var buckets = RecordingStats.Aggregate(
            [Entry("e-1", 2026, 9, 22, 1), Entry("e-2", 2026, 9, 28, 1)],
            From,
            To,
            StatsGranularity.Month);

        var bucket = Assert.Single(buckets);
        Assert.Equal(new DateOnly(2026, 9, 1), bucket.Start);
        Assert.Equal(2, bucket.Count);
    }

    [Fact]
    public void 跨月的数据分成两个桶()
    {
        var buckets = RecordingStats.Aggregate(
            [Entry("e-9", 2026, 9, 30, 1), Entry("e-10", 2026, 10, 1, 1)],
            From,
            LocalAt(2026, 11, 1, 0),
            StatsGranularity.Month);

        Assert.Equal([new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)], buckets.Select(b => b.Start));
    }

    [Fact]
    public void 段数与单号数是两个数_一个单号可以有好几段()
    {
        // ⚠️ VidLog 里分段录（规格 §3.1.1），所以「录了多少段」与「打了多少个包」
        // 不是一回事。界面若只报一个数还管它叫「件数」，那个词必然是错的。
        var summary = RecordingStats.Summarize(
            [
                Entry("e1", 2026, 9, 22, 1, waybill: "SF1000000001"),
                Entry("e2", 2026, 9, 22, 1, waybill: "SF1000000001"),
                Entry("e3", 2026, 9, 22, 1, waybill: "SF1000000002"),
            ],
            From,
            To);

        Assert.Equal(3, summary.Count);
        Assert.Equal(2, summary.WaybillCount);
    }

    [Fact]
    public void 平均单件用时除以单号数_不是除以段数()
    {
        // ⚠️ 这一条钉的是**分母**。图上的卡叫「平均单件用时」，
        // 而「件」= 单号。按段平均的话，一个拆成两段录的单号会被算成两件，
        // 报出来的数会比用户的实际手感小一半 —— 而界面上看不出用的是哪个分母。
        var summary = RecordingStats.Summarize(
            [
                Entry("e1", 2026, 9, 22, 1, waybill: "SF1000000001"),
                Entry("e2", 2026, 9, 22, 1, waybill: "SF1000000001"),   // 同单号的续录
                Entry("e3", 2026, 9, 22, 2, waybill: "SF1000000002"),
            ],
            From,
            To);

        Assert.Equal(3, summary.Count);
        Assert.Equal(2, summary.WaybillCount);
        Assert.Equal(TimeSpan.FromMinutes(4), summary.Duration);

        Assert.Equal(TimeSpan.FromMinutes(2), summary.AveragePerWaybill);      // 4 分 / 2 件
        Assert.NotEqual(TimeSpan.FromMinutes(4.0 / 3), summary.AveragePerWaybill);   // 不是 / 3 段
    }

    [Fact]
    public void 汇总与分桶看的是同一批数据()
    {
        // ⚠️ 这两处若各自过滤一遍，卡片上的总数与图里的柱子之和迟早对不上，
        // 而对不上的时候没人知道该信哪个。
        var entries = new[]
        {
            Entry("e-in-1", 2026, 9, 22, 1),
            Entry("e-in-2", 2026, 9, 23, 2),
            Entry("e-out", 2026, 8, 15, 9),      // 区间之外
        };

        var buckets = RecordingStats.Aggregate(entries, From, To, StatsGranularity.Day);
        var summary = RecordingStats.Summarize(entries, From, To);

        Assert.Equal(summary.Count, buckets.Sum(b => b.Count));
        Assert.Equal(summary.Duration, buckets.Aggregate(TimeSpan.Zero, (s, b) => s + b.Duration));
        Assert.Equal(summary.EstimatedBytes, buckets.Sum(b => b.EstimatedBytes));
    }

    [Theory]
    [InlineData(2026, 9, 28, StatsGranularity.Week, 2026, 9, 28)]   // 周一 → 它自己
    [InlineData(2026, 9, 27, StatsGranularity.Week, 2026, 9, 21)]   // 周日 → 上一个周一
    [InlineData(2026, 10, 1, StatsGranularity.Week, 2026, 9, 28)]   // 跨月的周四
    [InlineData(2026, 9, 30, StatsGranularity.Month, 2026, 9, 1)]
    [InlineData(2026, 9, 30, StatsGranularity.Day, 2026, 9, 30)]
    public void 桶起点归一化(
        int y, int m, int d, StatsGranularity by, int ey, int em, int ed)
    {
        Assert.Equal(
            new DateOnly(ey, em, ed),
            RecordingStats.BucketOf(new DateOnly(y, m, d), by));
    }

    [Fact]
    public void 老条目没有录制规格时_照样估得出字节数()
    {
        // 那两个字段是后来才加到索引里的，之前的录像没有 ——
        // 它们**不该被静默排除在统计之外**（那样「总占用」会偏小，而用户拿它判断盘还够不够）。
        var legacy = Entry("e-old", 2026, 9, 22, 1, codec: null, resolution: null);

        var summary = RecordingStats.Summarize([legacy], From, To);

        Assert.Equal(1, summary.Count);
        Assert.True(summary.EstimatedBytes > 0);
        Assert.Equal(CleanupPlanner.EstimateBytes(legacy), summary.EstimatedBytes);
    }
}
