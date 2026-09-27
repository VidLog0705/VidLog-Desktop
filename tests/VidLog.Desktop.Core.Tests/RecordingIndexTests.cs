using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 录像索引的读写。
/// </summary>
/// <remarks>
/// <para>
/// 这一组的重点是**读得宽容**：两端的 `index.jsonl` 用的字段名不一样，
/// 而两端的文件都已经在盘上了（手机上装的是 19/21/22 号包）⇒ 读端必须宽容、
/// 写端维持原样。见 <c>docs/实现决策.md</c>。
/// </para>
/// <para>
/// ⚠️ 下面「手机端写的那一行」与「母仓数据模型那套名字」两处的 JSON 是**逐字抄**的，
/// 不是照着自己的 DTO 拼的 —— 照自己拼的话，这条测试证明的只是自己跟自己一致。
/// </para>
/// </remarks>
public class RecordingIndexTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-index-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string FileName(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（2026-09-27 实测：会话还在
            // 收尾时删含 `.ass` / `.mkv` 的临时目录）。清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static readonly RecordingEntry Sample = new(
        "sess-1-001",
        "sess-1",
        WaybillNumber.Parse("SF1000000001"),
        new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.FromHours(8)),
        new DateTimeOffset(2026, 9, 27, 10, 5, 0, TimeSpan.FromHours(8)),
        TimeSpan.FromMinutes(5),
        RelativePath.Parse("2026/09/27/SF1000000001/sess-1_000.mp4"),
        ContentHash.Parse(new string('a', 64)),
        "device-1");

    [Fact]
    public async Task 自己写的读得回来()
    {
        using var dir = new TempDir();
        var index = new JsonLinesRecordingIndex(dir.FileName("index.jsonl"));

        await index.AddAsync(Sample);
        var loaded = await index.LoadAllAsync();

        var entry = Assert.Single(loaded);
        Assert.Equal(Sample.EvidenceId, entry.EvidenceId);
        Assert.Equal(Sample.Waybill.Value, entry.Waybill.Value);
        Assert.Equal(Sample.Duration, entry.Duration);
        Assert.Equal(Sample.Location.Value, entry.Location.Value);
    }

    [Fact]
    public async Task 手机端写的那一行读得回来()
    {
        // 手机端 `lib/recording/recording_index.dart` 的 toJson 输出，逐字抄。
        // 它的字段名（camelCase）与**本仓 DTO 的名字不一样**，
        // 也与母仓 `docs/02-数据模型.md` §1.1 那张表不一样 —— 三套。
        using var dir = new TempDir();
        var path = dir.FileName("index.jsonl");
        await System.IO.File.WriteAllTextAsync(path,
            """
            {"evidenceId":"sess-1-001","sessionId":"sess-1","waybill":"SF1000000001","startedAt":"2026-09-27T02:00:00.000Z","endedAt":"2026-09-27T02:05:00.000Z","durationSeconds":300.0,"location":"2026/09/27/SF1000000001/sess-1_000.mp4","contentHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sourceDeviceId":"phone-1"}
            """);

        var entry = Assert.Single(await new JsonLinesRecordingIndex(path).LoadAllAsync());

        Assert.Equal("sess-1-001", entry.EvidenceId);
        Assert.Equal("sess-1", entry.SessionId);
        Assert.Equal("SF1000000001", entry.Waybill.Value);
        Assert.Equal(TimeSpan.FromMinutes(5), entry.Duration);
        Assert.Equal("phone-1", entry.SourceDeviceId);
        // 时刻按 UTC 落盘、读回来是同一刻（不是同一串字符）。
        Assert.Equal(
            new DateTimeOffset(2026, 9, 27, 2, 0, 0, TimeSpan.Zero),
            entry.StartedAt.ToUniversalTime());
    }

    [Fact]
    public async Task 母仓数据模型那套字段名也读得回来()
    {
        // `docs/02-数据模型.md` §1.1 那张表用的是 `WaybillNumber` / `RecordingStartedAt` ——
        // **两端都没照着它写**，但既然它是契约，读端就该认。
        using var dir = new TempDir();
        var path = dir.FileName("index.jsonl");
        await System.IO.File.WriteAllTextAsync(path,
            """
            {"EvidenceId":"sess-1-001","SessionId":"sess-1","WaybillNumber":"SF1000000001","RecordingStartedAt":"2026-09-27T02:00:00+00:00","RecordingEndedAt":"2026-09-27T02:05:00+00:00","DurationSeconds":300,"Location":"2026/09/27/SF1000000001/sess-1_000.mp4","ContentHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","SourceDeviceId":"device-1"}
            """);

        var entry = Assert.Single(await new JsonLinesRecordingIndex(path).LoadAllAsync());

        Assert.Equal("SF1000000001", entry.Waybill.Value);
        Assert.Equal(TimeSpan.FromMinutes(5), entry.Duration);
    }

    [Fact]
    public async Task 缺关键字段的记录丢掉_不编一条出来()
    {
        // 「读得宽容」指的是**字段名**，不是**内容**。少东西的记录宁可不要 ——
        // 编一条出来的话，一条不存在的录像会进检索、进清理判定。
        using var dir = new TempDir();
        var path = dir.FileName("index.jsonl");
        await System.IO.File.WriteAllTextAsync(path,
            """
            {"evidenceId":"sess-1-001","startedAt":"2026-09-27T02:00:00.000Z","endedAt":"2026-09-27T02:05:00.000Z","location":"a/b.mp4"}
            """);

        Assert.Empty(await new JsonLinesRecordingIndex(path).LoadAllAsync());
    }

    [Fact]
    public async Task 坏行跳过_不让一条坏行毁掉整份索引()
    {
        using var dir = new TempDir();
        var index = new JsonLinesRecordingIndex(dir.FileName("index.jsonl"));

        await index.AddAsync(Sample);

        var path = dir.FileName("index.jsonl");
        await System.IO.File.AppendAllTextAsync(path, "{这不是 JSON\n");
        await index.AddAsync(Sample with { EvidenceId = "sess-1-002" });

        var loaded = await index.LoadAllAsync();

        Assert.Equal(2, loaded.Count);
        Assert.DoesNotContain(loaded, e => e.EvidenceId.Length == 0);
    }

    [Fact]
    public async Task 没有索引文件时返回空表_不抛()
    {
        using var dir = new TempDir();

        Assert.Empty(await new JsonLinesRecordingIndex(dir.FileName("没有这个文件.jsonl")).LoadAllAsync());
    }
}
