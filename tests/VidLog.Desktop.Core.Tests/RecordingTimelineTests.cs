using VidLog.Desktop.Core.Playback;
using VidLog.Desktop.Core.Punches;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 规格 §3.8：回放要支持「跳转到打点位置」。
/// 难点是**一次打包是区间，可能横跨多个分段文件** —— 打点记的是会话内偏移，
/// 播放器要的是「打开哪个文件、seek 到第几秒」。
/// </summary>
public class RecordingTimelineTests
{
    private static RecordingTimeline ThreeSegments() => new([
        new TimelineSegment("seg-000.mp4", TimeSpan.FromSeconds(10)),
        new TimelineSegment("seg-001.mp4", TimeSpan.FromSeconds(10)),
        new TimelineSegment("seg-002.mp4", TimeSpan.FromSeconds(10)),
    ]);

    [Fact]
    public void 总时长是各段之和()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ThreeSegments().TotalDuration);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5, 0, 5)]
    [InlineData(10, 1, 0)]
    [InlineData(15, 1, 5)]
    [InlineData(25, 2, 5)]
    public void 定位到正确的分段与段内偏移(int offsetSeconds, int expectedSegment, int expectedOffsetSeconds)
    {
        var position = ThreeSegments().Locate(TimeSpan.FromSeconds(offsetSeconds));

        Assert.NotNull(position);
        Assert.Equal(expectedSegment, position.SegmentIndex);
        Assert.Equal(TimeSpan.FromSeconds(expectedOffsetSeconds), position.OffsetInSegment);
        Assert.Equal($"seg-{expectedSegment:000}.mp4", position.FilePath);
    }

    [Fact]
    public void 边界归前一段的末尾()
    {
        // 拖到两段的分界点时，看到上一段的最后一帧比看到下一段第一帧更符合操作直觉
        var position = ThreeSegments().Locate(TimeSpan.FromSeconds(10));

        Assert.NotNull(position);
        Assert.Equal(1, position.SegmentIndex);
        Assert.Equal(TimeSpan.Zero, position.OffsetInSegment);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(30)]
    [InlineData(100)]
    public void 越界返回null(int offsetSeconds)
    {
        Assert.Null(ThreeSegments().Locate(TimeSpan.FromSeconds(offsetSeconds)));
    }

    [Fact]
    public void 空时间轴定位返回null()
    {
        var timeline = new RecordingTimeline([]);

        Assert.Equal(TimeSpan.Zero, timeline.TotalDuration);
        Assert.Null(timeline.Locate(TimeSpan.Zero));
        Assert.Null(timeline.LocatePunch(new Punch(
            "p1", "s1", WaybillNumber.Parse("SF1"), DateTimeOffset.UtcNow, 0, PunchSource.ManualEntry)));
    }

    // ─────────────────────────────────────────────
    // 打点定位
    // ─────────────────────────────────────────────

    private static Punch PunchAt(long offsetMs) => new(
        "p1", "s1", WaybillNumber.Parse("SF1000000001"),
        DateTimeOffset.UtcNow, offsetMs, PunchSource.KeyboardScanner);

    [Fact]
    public void 打点定位到第二段()
    {
        // 打点在会话第 12.5 秒 → 第二段的第 2.5 秒
        var position = ThreeSegments().LocatePunch(PunchAt(12_500));

        Assert.NotNull(position);
        Assert.Equal(1, position.SegmentIndex);
        Assert.Equal(TimeSpan.FromMilliseconds(2_500), position.OffsetInSegment);
    }

    [Fact]
    public void 打点落在第一段起点()
    {
        var position = ThreeSegments().LocatePunch(PunchAt(0));

        Assert.NotNull(position);
        Assert.Equal(0, position.SegmentIndex);
        Assert.Equal(TimeSpan.Zero, position.OffsetInSegment);
    }

    [Fact]
    public void 打点时刻略晚于收尾时钳到末尾_而不是报定位失败()
    {
        // 收尾本身要花时间，打点时刻可能略晚于最后一段的收尾。
        // 这时该跳到末尾让用户看到画面，而不是弹一个「定位失败」。
        var position = ThreeSegments().LocatePunch(PunchAt(31_000));

        Assert.NotNull(position);
        Assert.Equal(2, position.SegmentIndex);
        Assert.Equal(TimeSpan.FromSeconds(10), position.OffsetInSegment);
    }

    [Fact]
    public void 打点偏移为负时钳到开头()
    {
        var position = ThreeSegments().LocatePunch(PunchAt(-5_000));

        Assert.NotNull(position);
        Assert.Equal(0, position.SegmentIndex);
        Assert.Equal(TimeSpan.Zero, position.OffsetInSegment);
    }

    // ─────────────────────────────────────────────
    // 不变量 I11：位置用单调偏移，不用墙钟
    // ─────────────────────────────────────────────

    [Fact]
    public void 打点定位只看单调偏移_不看墙钟时刻()
    {
        // 两个打点墙钟差了 10 小时（模拟用户改过系统时间），
        // 但单调偏移相同 → 必须定位到同一处。
        var early = new Punch("p1", "s1", WaybillNumber.Parse("SF1"),
            DateTimeOffset.UtcNow.AddHours(-10), 12_500, PunchSource.KeyboardScanner);
        var late = new Punch("p2", "s1", WaybillNumber.Parse("SF1"),
            DateTimeOffset.UtcNow.AddHours(10), 12_500, PunchSource.KeyboardScanner);

        var a = ThreeSegments().LocatePunch(early);
        var b = ThreeSegments().LocatePunch(late);

        Assert.Equal(a!.SegmentIndex, b!.SegmentIndex);
        Assert.Equal(a.OffsetInSegment, b.OffsetInSegment);
    }
}
