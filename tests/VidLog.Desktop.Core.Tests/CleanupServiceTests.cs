using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 清理链路的**接线**（规格 §3.5.4 / §3.5.5）。
/// </summary>
/// <remarks>
/// 这一段以前整套都不存在：`CleanupPlanner`、`CleanupExecutor`、`CleanupAuditLog`
/// 写好了、测过了、**没有生产调用点** —— 母仓 `HANDOFF.md` §6 第 19 条记的就是
/// 这种「零件好、没人接」的病。所以这里验的重点不是判定算法（那些在
/// <c>CleanupTests</c> 里），而是**接线本身**：
/// 起算点真的从回执里来、「预告不动手」、以及两道闸都在。
/// </remarks>
public class CleanupServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(8));

    private static RecordingEntry Entry(string id, DateTimeOffset endedAt) => new(
        id,
        "sess-1",
        WaybillNumber.Parse("SF1000000001"),
        endedAt.AddMinutes(-5),
        endedAt,
        TimeSpan.FromMinutes(5),
        RelativePath.Parse($"2026/09/27/SF1000000001/{id}.mp4"),
        ContentHash.Parse(new string('a', 64)),
        "device-1");

    private static RecordingLabel Outbound(string id) => new(
        id, LabelKeys.BusinessType, BusinessTypes.ToValue(BusinessType.Outbound), Now);

    /// <summary>归档目录（本机那一档 = 就是它）。</summary>
    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string ArchiveRoot => System.IO.Path.Combine(Root, "archive");

        public Fixture()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-cleanup-svc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ArchiveRoot);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private static CleanupService Build(
        Fixture fixture,
        ArchiveBackendKind kind,
        string? archiveRoot = null,
        IReadOnlyList<RecordingEntry>? entries = null,
        IReadOnlyList<RecordingLabel>? labels = null,
        IReadOnlyList<ReceiptPayload>? receipts = null)
    {
        var index = new FakeIndex(entries ?? []);
        var labelStore = new FakeLabels(labels ?? []);
        var receiptStore = new ReceiptStore(System.IO.Path.Combine(fixture.Root, "receipts.jsonl"));

        foreach (var receipt in receipts ?? [])
        {
            receiptStore.AppendAsync(receipt).GetAwaiter().GetResult();
        }

        var executor = new CleanupExecutor(
            new DirectoryArchiveBackend(
                archiveRoot ?? Path.Combine(fixture.Root, "nas"), kind),
            fixture.ArchiveRoot,
            new CleanupAuditLog(System.IO.Path.Combine(fixture.Root, "cleanup-audit.jsonl")),
            NullLogger.Instance);

        return new CleanupService(index, labelStore, receiptStore, executor);
    }

    /// <summary>把「已归档」写成回执 —— 起算点就是从它来的。</summary>
    private static ReceiptPayload Receipt(string evidenceId, DateTimeOffset anchor) => new(
        evidenceId,
        new string('a', 64),
        anchor,
        anchor,
        "receiver-1",
        "receiver-1",
        $"2026/09/27/SF1000000001/{evidenceId}.mp4");

    private static RetentionSettings DeleteAfter(int days) => RetentionSettings.KeepAll with
    {
        ArchivedOutbound = new RetentionSetting(days),
        ArchivedReturn = new RetentionSetting(days),
    };

    [Fact]
    public void 归档层是本机时根本不允许清理()
    {
        // 规格 §3.5.1：那时盘上这份是唯一副本。界面据此不摆入口，
        // 执行层也有同样判据的闸 —— 两道都在（这里是第一道）。
        using var fixture = new Fixture();

        var service = Build(fixture, ArchiveBackendKind.LocalDisk);

        Assert.False(service.CanCleanup);
    }

    [Fact]
    public async Task 目录型归档层允许清理_而且预告时不碰任何文件()
    {
        using var fixture = new Fixture();
        var old = Now.AddDays(-40);

        var service = Build(
            fixture,
            ArchiveBackendKind.Nas,
            entries: [Entry("e1", old)],
            labels: [Outbound("e1")],
            receipts: [Receipt("e1", Now.AddDays(-30))]);

        Assert.True(service.CanCleanup);

        var plan = await service.PreviewAsync(DeleteAfter(7), Now);

        Assert.Single(plan.Candidates);

        // ⚠️ 预告**只算不删**（规格 §3.5.5：清理前必须给出预告）。
        var archived = Path.Combine(fixture.ArchiveRoot, "2026/09/27/SF1000000001/e1.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        File.WriteAllText(archived, "evidence");

        await service.PreviewAsync(DeleteAfter(7), Now);
        Assert.True(File.Exists(archived), "预告这一步不许删任何东西");
    }

    [Fact]
    public async Task 起算点取自回执的时间锚_不是录完时刻()
    {
        // 依据是 §4.3 的合取式「归档成功 **且** 超过保留期」：
        // 一台离线 40 天的机器若按录完时刻算，会在**刚归档那一瞬间**就被删掉。
        using var fixture = new Fixture();

        var service = Build(
            fixture,
            ArchiveBackendKind.Nas,
            entries: [Entry("e1", Now.AddDays(-40))],
            labels: [Outbound("e1")],
            // 一小时前才归档成功 —— 保留期 7 天，所以**不该**清。
            receipts: [Receipt("e1", Now.AddHours(-1))]);

        var plan = await service.PreviewAsync(DeleteAfter(7), Now);

        Assert.Empty(plan.Candidates);
        Assert.Contains(plan.Exempted, e => e.Why.Contains("还在 7 天保留期内"));
    }

    [Fact]
    public async Task 没有回执的那条不会被清_它是唯一副本()
    {
        using var fixture = new Fixture();

        var service = Build(
            fixture,
            ArchiveBackendKind.Nas,
            entries: [Entry("e1", Now.AddDays(-40))],
            labels: [Outbound("e1")],
            receipts: []);   // 没归档

        var plan = await service.PreviewAsync(DeleteAfter(7), Now);

        Assert.Empty(plan.Candidates);
        Assert.Contains(plan.Exempted, e => e.Why.Contains("唯一副本"));
    }

    [Fact]
    public async Task 执行时逐条回查_查不到就不删_而且留审计()
    {
        // I8 的落点，也是这一整段的**方向性**判据。
        using var fixture = new Fixture();
        var old = Now.AddDays(-40);

        var service = Build(
            fixture,
            ArchiveBackendKind.Nas,
            archiveRoot: Path.Combine(fixture.Root, "nas"),   // 归档层上什么都没有
            entries: [Entry("e1", old)],
            labels: [Outbound("e1")],
            receipts: [Receipt("e1", Now.AddDays(-30))]);

        var archived = Path.Combine(fixture.ArchiveRoot, "2026/09/27/SF1000000001/e1.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        File.WriteAllText(archived, "evidence");

        var plan = await service.PreviewAsync(DeleteAfter(7), Now);
        var report = await service.RunAsync(plan);

        Assert.Empty(report.Deleted);
        Assert.Single(report.Refused);
        Assert.True(File.Exists(archived), "回查不通过 ⇒ **绝不删**本地副本");

        // 规格 §3.5.5：清理记录必须留着（「这条为什么没删」也要答得上来）。
        var audit = await new CleanupAuditLog(
            Path.Combine(fixture.Root, "cleanup-audit.jsonl")).LoadAllAsync();
        Assert.Contains(audit, a => a.EvidenceId == "e1" && a.Action == "refused");
    }

    [Fact]
    public async Task 归档层上真有一份时才删_并有_deleted_审计()
    {
        using var fixture = new Fixture();
        var old = Now.AddDays(-40);
        var nas = Path.Combine(fixture.Root, "nas");

        var service = Build(
            fixture,
            ArchiveBackendKind.Nas,
            archiveRoot: nas,
            entries: [Entry("e1", old)],
            labels: [Outbound("e1")],
            receipts: [Receipt("e1", Now.AddDays(-30))]);

        // 归档层上有一份（发布过去的），本机上有一份。
        var relative = "2026/09/27/SF1000000001/e1.mp4";
        Directory.CreateDirectory(Path.Combine(nas, "2026/09/27/SF1000000001"));
        File.WriteAllText(Path.Combine(nas, relative), "second-copy");

        var local = Path.Combine(fixture.ArchiveRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, "evidence");

        var plan = await service.PreviewAsync(DeleteAfter(7), Now);
        var report = await service.RunAsync(plan);

        Assert.Single(report.Deleted);
        Assert.Empty(report.Refused);

        // ⚠️ 删的是**本机那一份**，归档层上那份一个字都没动。
        Assert.False(File.Exists(local));
        Assert.True(File.Exists(Path.Combine(nas, relative)));

        var audit = await new CleanupAuditLog(
            Path.Combine(fixture.Root, "cleanup-audit.jsonl")).LoadAllAsync();
        Assert.Contains(audit, a => a.EvidenceId == "e1" && a.Action == "deleted");
    }

    [Fact]
    public async Task 归档层是本机时_哪怕计划里有候选_执行层也一条都不删()
    {
        // 「两道闸」里的第二道：界面（`CanCleanup`）不问是一回事，
        // 被绕过了还照样不删是另一回事 —— 后者才是 I2 的保证。
        using var fixture = new Fixture();
        var old = Now.AddDays(-40);

        var service = Build(
            fixture,
            ArchiveBackendKind.LocalDisk,
            entries: [Entry("e1", old)],
            labels: [Outbound("e1")],
            receipts: [Receipt("e1", Now.AddDays(-30))]);

        // 手工造一个「有候选」的计划，绕过界面那一层。
        var plan = new CleanupPlan(
            [new CleanupCandidate(Entry("e1", old), 1024, RelativePath.Parse("a.mp4"), "假的")],
            []);

        var report = await service.RunAsync(plan);

        Assert.Empty(report.Deleted);
        Assert.Single(report.Refused);
    }

    private sealed class FakeIndex(IReadOnlyList<RecordingEntry> entries) : IRecordingIndex
    {
        public Task AddAsync(RecordingEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RecordingEntry>> LoadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(entries);
    }

    private sealed class FakeLabels(IReadOnlyList<RecordingLabel> labels) : ILabelStore
    {
        public Task SetAsync(
            string evidenceId, string key, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, string>> GetForEvidenceAsync(
            string evidenceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadAllAsync(
            CancellationToken cancellationToken = default)
        {
            var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

            foreach (var label in labels)
            {
                if (!map.TryGetValue(label.EvidenceId, out var entry))
                {
                    entry = new Dictionary<string, string>(StringComparer.Ordinal);
                    map[label.EvidenceId] = entry;
                }

                entry[label.Key] = label.Value;
            }

            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(
                map.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyDictionary<string, string>)pair.Value,
                    StringComparer.Ordinal));
        }
    }
}
