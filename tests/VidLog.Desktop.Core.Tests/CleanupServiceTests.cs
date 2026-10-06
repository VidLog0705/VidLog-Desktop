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
        IReadOnlyList<ReceiptPayload>? receipts = null,
        IReadOnlyList<PublishedRecord>? published = null)
    {
        var index = new FakeIndex(entries ?? []);
        var labelStore = new FakeLabels(labels ?? []);
        var receiptStore = new ReceiptStore(System.IO.Path.Combine(fixture.Root, "receipts.jsonl"));

        foreach (var receipt in receipts ?? [])
        {
            receiptStore.AppendAsync(receipt).GetAwaiter().GetResult();
        }

        // T18：自记账那一张表。**与回执是两本账**（见 `PublishedStore` 的类注释）。
        var publishedStore = new PublishedStore(System.IO.Path.Combine(fixture.Root, "published.jsonl"));

        foreach (var record in published ?? [])
        {
            publishedStore.AppendAsync(record).GetAwaiter().GetResult();
        }

        var executor = new CleanupExecutor(
            new DirectoryArchiveBackend(
                archiveRoot ?? Path.Combine(fixture.Root, "nas"), kind),
            fixture.ArchiveRoot,
            new CleanupAuditLog(System.IO.Path.Combine(fixture.Root, "cleanup-audit.jsonl")),
            NullLogger.Instance);

        return new CleanupService(index, labelStore, receiptStore, publishedStore, executor);
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

    // ─────────────────────────────────────────────
    // 按空间释放（设计图 `_43` 上那个按钮）
    // ─────────────────────────────────────────────

    /// <summary>GB → 字节。</summary>
    private static long Gb(double n) => (long)(n * 1024 * 1024 * 1024);

    /// <summary>三条可清的录像，最旧的叫 oldest。</summary>
    private static (IReadOnlyList<RecordingEntry> Entries, IReadOnlyList<RecordingLabel> Labels, IReadOnlyList<ReceiptPayload> Receipts) ThreeClearable() => (
        [Entry("oldest", Now.AddDays(-40)), Entry("middle", Now.AddDays(-30)), Entry("newest", Now.AddDays(-20))],
        [Outbound("oldest"), Outbound("middle"), Outbound("newest")],
        [Receipt("oldest", Now.AddDays(-39)), Receipt("middle", Now.AddDays(-29)), Receipt("newest", Now.AddDays(-19))]);

    [Fact]
    public async Task 空间充足时按空间释放什么都不清()
    {
        using var fixture = new Fixture();

        var service = Build(
            fixture, ArchiveBackendKind.Nas,
            entries: [Entry("e1", Now.AddDays(-40))],
            labels: [Outbound("e1")],
            receipts: [Receipt("e1", Now.AddDays(-39))]);

        var plan = await service.PreviewBySpaceAsync(minFreeBytes: Gb(20), freeBytes: Gb(50), Now);

        Assert.NotNull(plan);
        Assert.Empty(plan!.Candidates);
    }

    [Fact]
    public async Task 缺一点点就只清最旧的那一条_清到够就停()
    {
        // ⚠️ 判据刻意**不依赖单条录像估多大**：缺口只要 1 字节 ⇒
        // 清掉**第一条**（最旧的）就够了 ⇒ 候选恰好一条。
        // 写成「清掉 N 条」的话，那条断言会随着字节系数的标定而碎。
        using var fixture = new Fixture();
        var (entries, labels, receipts) = ThreeClearable();

        var service = Build(
            fixture, ArchiveBackendKind.Nas,
            entries: entries, labels: labels, receipts: receipts);

        var plan = await service.PreviewBySpaceAsync(minFreeBytes: 1, freeBytes: 0, Now);

        var candidate = Assert.Single(plan!.Candidates);

        // ⚠️ **从最旧的开始** —— 空间紧张时先腾掉最老的素材。
        Assert.Equal("oldest", candidate.Entry.EvidenceId);
    }

    [Fact]
    public async Task 缺口很大时把能清的全清上()
    {
        // 缺口远比全部录像加起来还大 ⇒ 可清的一条不剩（但**豁免的那些仍然不动**）。
        using var fixture = new Fixture();
        var (entries, labels, receipts) = ThreeClearable();

        var service = Build(
            fixture, ArchiveBackendKind.Nas,
            entries: entries, labels: labels, receipts: receipts);

        var plan = await service.PreviewBySpaceAsync(minFreeBytes: Gb(1024), freeBytes: 0, Now);

        Assert.Equal(3, plan!.Candidates.Count);
    }

    [Fact]
    public async Task 按空间释放不看业务类型_只走一份策略()
    {
        // ⚠️ 磁盘满不满跟发货/退货无关（`PlanPerBusinessType` 的注释里写着这条）。
        // 判据取「**没有业务类型标签**的录像在按空间释放时也会被清」——
        // 而在按时间清理那条路上，它会因为「判不出该用哪份保留期」被豁免。
        // 这是两条路行为上唯一看得见的差别，所以用它钉住。
        using var fixture = new Fixture();

        var service = Build(
            fixture, ArchiveBackendKind.Nas,
            entries: [Entry("no-label", Now.AddDays(-40))],
            labels: [],                                    // ← 故意没有业务类型标签
            receipts: [Receipt("no-label", Now.AddDays(-39))]);

        var byTime = await service.PreviewAsync(DeleteAfter(7), Now);
        var bySpace = await service.PreviewBySpaceAsync(Gb(20), 0, Now);

        Assert.Empty(byTime.Candidates);
        Assert.Single(bySpace!.Candidates);
    }

    [Fact]
    public async Task 归档层是本机时按空间释放返回null而不是空计划()
    {
        // ⚠️ 用 null 而不是空计划：界面要能分开「**不许**清」与「**没得**清」——
        // 前者得说一句原因（否则用户点了按钮什么都没发生），后者不该说话。
        using var fixture = new Fixture();

        var service = Build(
            fixture, ArchiveBackendKind.LocalDisk,
            entries: [Entry("e1", Now.AddDays(-40))],
            labels: [Outbound("e1")],
            receipts: [Receipt("e1", Now.AddDays(-39))]);

        Assert.Null(await service.PreviewBySpaceAsync(Gb(20), 0, Now));
    }

    [Fact]
    public async Task 按空间释放也清不到未归档与锁着的_宁可盘满()
    {
        // ⚠️ 规格 §3.5.3 的三条豁免与「策略模式」无关（它们在 `Plan` 里判）
        // ⇒ 按空间释放**可能释放不出足够空间**。而那是**对的**：
        // **宁可盘满，也不删唯一副本**（I2）。
        using var fixture = new Fixture();

        var service = Build(
            fixture, ArchiveBackendKind.Nas,
            entries:
            [
                Entry("unarchived", Now.AddDays(-40)),   // 没有回执 ⇒ 未归档 ⇒ 唯一副本
                Entry("locked", Now.AddDays(-40)),
            ],
            labels:
            [
                Outbound("unarchived"),
                new RecordingLabel("locked", LabelKeys.Locked, "true", Now),
            ],
            receipts: [Receipt("locked", Now.AddDays(-39))]);

        var plan = await service.PreviewBySpaceAsync(Gb(1024), 0, Now);

        Assert.NotNull(plan);
        Assert.Empty(plan!.Candidates);

        // 两条都要**说出为什么没清**（规格 §3.5.5：不许静默）。
        Assert.Equal(2, plan.Exempted.Count);
        Assert.Contains(plan.Exempted, e => e.Why.Contains("唯一副本"));
        Assert.Contains(plan.Exempted, e => e.Why.Contains("锁定"));
    }

    // ─────────────────────────────────────────────
    // T18：桌面**自己**发出去的那一份也要算数
    // ─────────────────────────────────────────────
    //
    // 缺陷原样：时间锚只有一个来源（`receipts.jsonl`），而那份回执**只有手机上传那一路会写**
    // ⇒ 桌面自己录、自己发到 NAS 的录像**一个锚都没有** ⇒ 被判成「唯一副本」**永久豁免**。
    // 于是「本机保留多久」这个设置对本机录制内容根本不成立，**盘满只是时间问题**。

    /// <summary>「已归档」自记账的一条（T18 的 `published.jsonl`）。</summary>
    private static PublishedRecord Published(string evidenceId, DateTimeOffset at) =>
        new(evidenceId, at, $"2026/09/27/SF1000000001/{evidenceId}.mp4");

    [Fact]
    public async Task 自录的发布成功后就成了可清候选_不再被判成唯一副本()
    {
        // ⚠️ 这条**故意不给回执** —— 自录那一路本来就没有回执，
        // 给了就变成在验手机上传那条路，验不到 T18。
        using var fixture = new Fixture();

        var service = Build(
            fixture, ArchiveBackendKind.Nas,
            entries: [Entry("self-rec", Now.AddDays(-40))],
            labels: [Outbound("self-rec")],
            published: [Published("self-rec", Now.AddDays(-39))]);

        var plan = await service.PreviewBySpaceAsync(Gb(1024), 0, Now);

        Assert.NotNull(plan);
        Assert.Single(plan!.Candidates);
        Assert.Equal("self-rec", plan.Candidates[0].Entry.EvidenceId);
    }

    [Fact]
    public async Task 自录的发布失败仍然豁免_一张账都没记就不该清()
    {
        // 反证的另一半：T18 修的是「账没记」，不是「把没归档的也放行」。
        // 归档层那一份没上去（`ArchiveRelay` 就不会记账）⇒ 本机这份是唯一副本 ⇒ 仍然豁免。
        using var fixture = new Fixture();

        var service = Build(
            fixture, ArchiveBackendKind.Nas,
            entries: [Entry("never-published", Now.AddDays(-40))],
            labels: [Outbound("never-published")],
            published: []);                                  // ← 没记上账 = 没发上去

        var plan = await service.PreviewBySpaceAsync(Gb(1024), 0, Now);

        Assert.Empty(plan!.Candidates);
        Assert.Contains(plan.Exempted, e => e.Why.Contains("唯一副本"));
    }

    [Fact]
    public async Task 两张账都有的那条以回执为准_自记账把锚往后挪不算数()
    {
        // 手机传上来的那条**两张表都有**。时刻真不一致时（这里刻意的：
        // 回执 39 天前、自记账 1 天前），**认回执** —— 它是对外签出去的那个
        // `timeAnchor`，规格 §3.5.2.1 要的正是「外部时间锚，用户改不了」。
        //
        // ⚠️ 判据刻意选成**方向敏感**的：保留 7 天，回执锚 39 天前 ⇒ 到期该清；
        // 若自记账那条赢了（锚 1 天前）⇒ **一条都不会清**。所以这个断言只在
        // 「回执优先」时成立 —— 合并方向写反了它就会红。
        using var fixture = new Fixture();

        var service = Build(
            fixture, ArchiveBackendKind.Nas,
            entries: [Entry("from-phone", Now.AddDays(-40))],
            labels: [Outbound("from-phone")],
            receipts: [Receipt("from-phone", Now.AddDays(-39))],
            published: [Published("from-phone", Now.AddDays(-1))]);

        var plan = await service.PreviewAsync(DeleteAfter(7), Now);

        Assert.Equal("from-phone", Assert.Single(plan.Candidates).Entry.EvidenceId);
    }

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
