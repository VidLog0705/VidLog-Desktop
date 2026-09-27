using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Search;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 规格 §3.8：检索维度 = 单号（精确/前缀/模糊）、日期、发货/退货。
/// </summary>
public class RecordingSearchTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-search-" + Guid.NewGuid().ToString("N"));
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

    private static readonly DateTimeOffset Day = new(2026, 9, 16, 10, 0, 0, TimeSpan.FromHours(8));

    private static RecordingEntry Entry(
        string id,
        string waybill,
        DateTimeOffset started,
        int minutes = 1,
        string? sessionId = null) => new(
        id,
        sessionId ?? "session-" + id,
        WaybillNumber.Parse(waybill),
        started,
        started.AddMinutes(minutes),
        TimeSpan.FromMinutes(minutes),
        RelativePath.Parse($"2026/09/16/{waybill}/{id}.mp4"),
        ContentHash.Parse(new string('a', 64)),
        "device-1");

    /// <summary>造一个装了 5 条录像的索引 —— 正好对上 M3 验收里的「录 5 条」。</summary>
    private static async Task<(JsonLinesRecordingIndex Index, JsonLinesLabelStore Labels)> SeedAsync(TempDir dir)
    {
        var index = new JsonLinesRecordingIndex(dir.File("index.jsonl"));
        var labels = new JsonLinesLabelStore(dir.File("labels.jsonl"));

        await index.AddAsync(Entry("e1", "SF1000000001", Day));
        await index.AddAsync(Entry("e2", "SF1000000002", Day.AddHours(1)));
        await index.AddAsync(Entry("e3", "SF2000000003", Day.AddHours(2)));
        await index.AddAsync(Entry("e4", "YT3000000004", Day.AddDays(1)));
        await index.AddAsync(Entry("e5", "YT3000000005", Day.AddDays(1).AddHours(1)));

        return (index, labels);
    }

    private static RecordingSearch Build(JsonLinesRecordingIndex index, JsonLinesLabelStore labels) =>
        new(index, labels);

    // ─────────────────────────────────────────────
    // 单号匹配
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 精确匹配()
    {
        using var dir = new TempDir();
        var (index, labels) = await SeedAsync(dir);

        var hits = await Build(index, labels).SearchAsync(new RecordingQuery
        {
            WaybillText = "SF1000000001",
            MatchMode = WaybillMatchMode.Exact,
        });

        Assert.Equal("e1", Assert.Single(hits).Entry.EvidenceId);
    }

    [Fact]
    public async Task 精确匹配不返回前缀相同的其他单号()
    {
        using var dir = new TempDir();
        var (index, labels) = await SeedAsync(dir);

        var hits = await Build(index, labels).SearchAsync(new RecordingQuery
        {
            WaybillText = "SF100000000",
            MatchMode = WaybillMatchMode.Exact,
        });

        Assert.Empty(hits);
    }

    [Fact]
    public async Task 前缀匹配()
    {
        using var dir = new TempDir();
        var (index, labels) = await SeedAsync(dir);

        var hits = await Build(index, labels).SearchAsync(new RecordingQuery
        {
            WaybillText = "SF100",
            MatchMode = WaybillMatchMode.Prefix,
        });

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.StartsWith("SF100", h.Entry.Waybill.Value));
    }

    [Fact]
    public async Task 模糊匹配_包含即可()
    {
        using var dir = new TempDir();
        var (index, labels) = await SeedAsync(dir);

        var hits = await Build(index, labels).SearchAsync(new RecordingQuery
        {
            WaybillText = "0000004",
            MatchMode = WaybillMatchMode.Contains,
        });

        Assert.Equal("e4", Assert.Single(hits).Entry.EvidenceId);
    }

    [Fact]
    public async Task 查询串也走归一化_小写和空格都能查到()
    {
        // 库里的单号是大写形态；用户敲小写或带空格不该查不到。
        using var dir = new TempDir();
        var (index, labels) = await SeedAsync(dir);

        var hits = await Build(index, labels).SearchAsync(new RecordingQuery
        {
            WaybillText = "  sf1000000001  ",
            MatchMode = WaybillMatchMode.Exact,
        });

        Assert.Equal("e1", Assert.Single(hits).Entry.EvidenceId);
    }

    [Fact]
    public async Task 不给单号就不按单号过滤()
    {
        using var dir = new TempDir();
        var (index, labels) = await SeedAsync(dir);

        var hits = await Build(index, labels).SearchAsync(new RecordingQuery());

        Assert.Equal(5, hits.Count);
    }

    // ─────────────────────────────────────────────
    // 日期筛选
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 日期范围筛选是半开区间()
    {
        using var dir = new TempDir();
        var (index, labels) = await SeedAsync(dir);

        // 只看 9/16 那一天：[16 日 00:00, 17 日 00:00)
        var hits = await Build(index, labels).SearchAsync(new RecordingQuery
        {
            From = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.FromHours(8)),
            To = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.FromHours(8)),
        });

        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.Equal(16, h.Entry.StartedAt.Day));
    }

    [Fact]
    public async Task 结果按录制时间倒序()
    {
        using var dir = new TempDir();
        var (index, labels) = await SeedAsync(dir);

        var hits = await Build(index, labels).SearchAsync(new RecordingQuery());

        Assert.Equal(
            hits.OrderByDescending(h => h.Entry.StartedAt).Select(h => h.Entry.EvidenceId),
            hits.Select(h => h.Entry.EvidenceId));
    }

    // ─────────────────────────────────────────────
    // 发货 / 退货筛选（标签）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 按发货退货筛选()
    {
        using var dir = new TempDir();
        var (index, labels) = await SeedAsync(dir);

        await labels.SetAsync("e1", LabelKeys.BusinessType, BusinessTypes.OutboundValue);
        await labels.SetAsync("e2", LabelKeys.BusinessType, BusinessTypes.ReturnValue);
        await labels.SetAsync("e3", LabelKeys.BusinessType, BusinessTypes.OutboundValue);

        var returns = await Build(index, labels).SearchAsync(new RecordingQuery
        {
            BusinessType = BusinessType.Return,
        });

        Assert.Equal("e2", Assert.Single(returns).Entry.EvidenceId);
    }

    [Fact]
    public async Task 没有标签的录像不会被发货退货筛选选中()
    {
        using var dir = new TempDir();
        var (index, labels) = await SeedAsync(dir);

        await labels.SetAsync("e1", LabelKeys.BusinessType, BusinessTypes.OutboundValue);

        var outbound = await Build(index, labels).SearchAsync(new RecordingQuery
        {
            BusinessType = BusinessType.Outbound,
        });

        Assert.Equal("e1", Assert.Single(outbound).Entry.EvidenceId);
    }

    [Fact]
    public async Task 检索结果带上标签()
    {
        using var dir = new TempDir();
        var (index, labels) = await SeedAsync(dir);

        await labels.SetAsync("e1", LabelKeys.Company, "某公司");
        await labels.SetAsync("e1", LabelKeys.BusinessType, BusinessTypes.OutboundValue);

        var hits = await Build(index, labels).SearchAsync(new RecordingQuery { WaybillText = "SF1000000001" });

        var hit = Assert.Single(hits);
        Assert.Equal("某公司", hit.Labels[LabelKeys.Company]);
        Assert.Equal(BusinessType.Outbound, hit.BusinessType);
    }

    // ─────────────────────────────────────────────
    // 标签的「可修正」（I5）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 标签可反复修正_后者胜出()
    {
        using var dir = new TempDir();
        var labels = new JsonLinesLabelStore(dir.File("labels.jsonl"));

        await labels.SetAsync("e1", LabelKeys.Company, "写错了");
        await labels.SetAsync("e1", LabelKeys.Company, "改对了");

        var stored = await labels.GetForEvidenceAsync("e1");

        Assert.Equal("改对了", stored[LabelKeys.Company]);
    }

    [Fact]
    public async Task 修正标签不影响证据本身()
    {
        // I5：单号是唯一事实标识，其余属性只是可修正标签。
        // 证据的哈希、路径、时长都不该因为改标签而变。
        using var dir = new TempDir();
        var (index, labels) = await SeedAsync(dir);

        var before = Assert.Single(await index.LoadAllAsync(), e => e.EvidenceId == "e1");

        await labels.SetAsync("e1", LabelKeys.Company, "甲");
        await labels.SetAsync("e1", LabelKeys.Company, "乙");

        var after = Assert.Single(await index.LoadAllAsync(), e => e.EvidenceId == "e1");

        Assert.Equal(before, after);
    }

    // ─────────────────────────────────────────────
    // 打点持久化（规格 §3.2.4）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 打点写入后立即可读()
    {
        using var dir = new TempDir();
        var log = new JsonLinesPunchLog(dir.File("punches.jsonl"));

        var punch = new Punch(
            "p1", "s1", WaybillNumber.Parse("SF1000000001"),
            Day, 12_345, PunchSource.KeyboardScanner);

        await log.AppendAsync(punch);

        var loaded = Assert.Single(await log.LoadAllAsync());
        Assert.Equal(punch, loaded);
    }

    [Fact]
    public async Task 同一个会话的多次打点都留着()
    {
        // 规格 §3.2.4：一个录制会话内可以有多次打点（多件同单、连续多个包裹）。
        using var dir = new TempDir();
        var log = new JsonLinesPunchLog(dir.File("punches.jsonl"));

        foreach (var (index, offset) in new[] { ("p1", 1000L), ("p2", 3000L), ("p3", 9000L) })
        {
            await log.AppendAsync(new Punch(
                index, "s1", WaybillNumber.Parse("SF1000000001"),
                Day.AddMilliseconds(offset), offset, PunchSource.CameraDecoder));
        }

        Assert.Equal(3, (await log.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task 打点日志文件不存在时返回空而不是抛()
    {
        using var dir = new TempDir();
        var log = new JsonLinesPunchLog(dir.File("never-written.jsonl"));

        Assert.Empty(await log.LoadAllAsync());
    }
}
