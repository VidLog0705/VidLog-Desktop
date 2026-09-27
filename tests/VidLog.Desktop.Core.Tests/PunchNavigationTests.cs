using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Playback;
using VidLog.Desktop.Core.Punches;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 规格 §3.8：回放「跳转到打点位置」。
/// 打点记的是**会话内偏移**，播放器要的是「哪个文件、第几秒」——
/// 中间隔着一层：一次打包可能横跨多个分段文件。
/// </summary>
public class PunchNavigationTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-punch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（2026-09-27 实测：会话还在
            // 收尾时删含 `.ass` / `.mkv` 的临时目录）。清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static readonly DateTimeOffset Base = new(2026, 9, 16, 10, 0, 0, TimeSpan.FromHours(8));

    private static RecordingEntry Segment(string evidenceId, int sequence, DateTimeOffset started, int seconds) => new(
        evidenceId,
        "session-1",
        WaybillNumber.Parse("SF1000000001"),
        started,
        started.AddSeconds(seconds),
        TimeSpan.FromSeconds(seconds),
        RelativePath.Parse($"2026/09/16/SF1000000001/{evidenceId}.mp4"),
        ContentHash.Parse(new string('a', 64)),
        "device-1");

    /// <summary>一个会话两段，各 10 秒。</summary>
    private static async Task<(JsonLinesRecordingIndex Index, JsonLinesPunchLog Punches)> TwoSegmentsAsync(TempDir dir)
    {
        var index = new JsonLinesRecordingIndex(dir.File("index.jsonl"));
        await index.AddAsync(Segment("seg-000", 0, Base, 10));
        await index.AddAsync(Segment("seg-001", 1, Base.AddSeconds(10), 10));
        return (index, new JsonLinesPunchLog(dir.File("punches.jsonl")));
    }

    private static Punch PunchAt(string id, long offsetMs) => new(
        id, "session-1", WaybillNumber.Parse("SF1000000001"),
        Base.AddMilliseconds(offsetMs), offsetMs, PunchSource.KeyboardScanner);

    [Fact]
    public async Task 打点换算成段内偏移()
    {
        using var dir = new TempDir();
        var (index, punches) = await TwoSegmentsAsync(dir);

        // 会话第 12 秒 → 第二段（起点 10s）的第 2 秒
        await punches.AppendAsync(PunchAt("p1", 12_000));

        var targets = await new PunchNavigation(index, punches).ForEvidenceAsync("seg-000");

        var target = Assert.Single(targets);
        Assert.Equal("seg-001", target.EvidenceId);
        Assert.Equal(1, target.SegmentIndex);
        Assert.Equal(2, target.OffsetSeconds);
        Assert.Equal(12, target.SessionOffsetSeconds);
    }

    [Fact]
    public async Task 打点按会话全局偏移排序_不是段内偏移()
    {
        // 这条是防一个具体的写错法：按段内偏移排，
        // 「第 1 段的第 2 秒」会排到「第 0 段的第 5 秒」前面 —— 顺序就乱了。
        using var dir = new TempDir();
        var (index, punches) = await TwoSegmentsAsync(dir);

        await punches.AppendAsync(PunchAt("late-segment", 12_000)); // 第二段第 2 秒
        await punches.AppendAsync(PunchAt("early-in-first", 5_000)); // 第一段第 5 秒
        await punches.AppendAsync(PunchAt("first", 2_000)); // 第一段第 2 秒

        var targets = await new PunchNavigation(index, punches).ForEvidenceAsync("seg-000");

        Assert.Equal(
            ["first", "early-in-first", "late-segment"],
            targets.Select(t => t.PunchId));
    }

    [Fact]
    public async Task 从任一段进去都拿到同一会话的全部打点()
    {
        using var dir = new TempDir();
        var (index, punches) = await TwoSegmentsAsync(dir);
        await punches.AppendAsync(PunchAt("p1", 2_000));
        await punches.AppendAsync(PunchAt("p2", 12_000));

        var navigation = new PunchNavigation(index, punches);

        var fromFirst = await navigation.ForEvidenceAsync("seg-000");
        var fromSecond = await navigation.ForEvidenceAsync("seg-001");

        Assert.Equal(2, fromFirst.Count);
        Assert.Equal(fromFirst.Select(t => t.PunchId), fromSecond.Select(t => t.PunchId));
    }

    [Fact]
    public async Task 没有打点时返回空()
    {
        using var dir = new TempDir();
        var (index, punches) = await TwoSegmentsAsync(dir);

        Assert.Empty(await new PunchNavigation(index, punches).ForEvidenceAsync("seg-000"));
    }

    [Fact]
    public async Task 未知证据返回空而不是抛()
    {
        using var dir = new TempDir();
        var (index, punches) = await TwoSegmentsAsync(dir);

        Assert.Empty(await new PunchNavigation(index, punches).ForEvidenceAsync("no-such-evidence"));
    }

    [Fact]
    public async Task 打点时刻略晚于收尾时钳到最后一段()
    {
        using var dir = new TempDir();
        var (index, punches) = await TwoSegmentsAsync(dir);

        await punches.AppendAsync(PunchAt("p1", 25_000)); // 超过总时长 20 秒

        var target = Assert.Single(await new PunchNavigation(index, punches).ForEvidenceAsync("seg-000"));

        Assert.Equal("seg-001", target.EvidenceId);
        Assert.Equal(10, target.OffsetSeconds);
    }

    [Fact]
    public async Task 其他会话的打点不会串进来()
    {
        using var dir = new TempDir();
        var (index, punches) = await TwoSegmentsAsync(dir);

        await punches.AppendAsync(PunchAt("mine", 2_000));
        await punches.AppendAsync(new Punch(
            "theirs", "session-2", WaybillNumber.Parse("YT9999999999"),
            Base, 2_000, PunchSource.CameraDecoder));

        var target = Assert.Single(await new PunchNavigation(index, punches).ForEvidenceAsync("seg-000"));

        Assert.Equal("mine", target.PunchId);
    }
}
