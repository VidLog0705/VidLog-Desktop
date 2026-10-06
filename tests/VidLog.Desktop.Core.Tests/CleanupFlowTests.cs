using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 设置页那两颗按钮按下之后**该算什么**（T26①）。
/// </summary>
/// <remarks>
/// <para>
/// 这一整套判断此前写在 WPF 的按钮点击事件里（<c>OnCleanupByTime</c> /
/// <c>OnCleanupBySpace</c>）—— 也就是母仓 §4 明令禁止的「逻辑塞进 UI 层」。
/// 而那个工程**没有测试工程**（T27②），所以那些分支过去只能靠读代码确认。
/// 搬进 <see cref="CleanupFlow"/> 之后它们第一次可以被钉住。
/// </para>
/// <para>
/// ⚠️ 验的是**「这一次该算哪一种、算完该跟用户说什么」**，不是判定算法本身
/// （那些在 <c>CleanupTests</c> 里）。所以每条都断言到「有没有方案 + 那句话说了什么」为止。
/// </para>
/// </remarks>
public class CleanupFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.FromHours(8));

    private const long Gb = DiskSpace.Gigabyte;

    private static readonly string DiskRoot = @"X:\盘";

    private static RecordingEntry Entry(string id, DateTimeOffset endedAt) => new(
        id,
        "sess-1",
        WaybillNumber.Parse("SF1000000001"),
        endedAt.AddMinutes(-5),
        endedAt,
        TimeSpan.FromMinutes(5),
        RelativePath.Parse($"2026/09/06/SF1000000001/{id}.mp4"),
        ContentHash.Parse(new string('a', 64)),
        "device-1");

    private static RecordingLabel Outbound(string id) => new(
        id, LabelKeys.BusinessType, BusinessTypes.ToValue(BusinessType.Outbound), Now);

    /// <summary>把「已归档」写成回执 —— 起算点就是从它来的（没有锚点就永久豁免）。</summary>
    private static ReceiptPayload Receipt(string evidenceId, DateTimeOffset anchor) => new(
        evidenceId,
        new string('a', 64),
        anchor,
        anchor,
        "receiver-1",
        "receiver-1",
        $"2026/09/06/SF1000000001/{evidenceId}.mp4");

    /// <summary>留 1 天，于是 30 天前的那些都到期了。</summary>
    private static RetentionSettings DeleteAfter(int days) => RetentionSettings.KeepAll with
    {
        ArchivedOutbound = new RetentionSetting(days),
        ArchivedReturn = new RetentionSetting(days),
    };

    /// <summary>归档层不是本机（那才允许清理，规格 §3.5.1）。</summary>
    private const ArchiveBackendKind Nas = ArchiveBackendKind.Nas;

    private static CleanupFlow Build(
        CleanupFlowFixture fixture,
        string? archiveRoot = null,
        IReadOnlyList<RecordingEntry>? entries = null,
        IReadOnlyList<ReceiptPayload>? receipts = null,
        ArchiveBackendKind? kind = null)
    {
        var ids = (entries ?? []).Select(one => one.EvidenceId).ToArray();

        // 每一条都给一份「已归档」回执 —— 否则它被判成唯一副本而永久豁免，
        // 那两个「有候选」的用例就永远是空的（而且不会报错，只是测不到东西）。
        var receiptStore = new ReceiptStore(System.IO.Path.Combine(fixture.Root, "receipts.jsonl"));

        foreach (var receipt in receipts ?? ids.Select(id => Receipt(id, Now.AddDays(-30))))
        {
            receiptStore.AppendAsync(receipt).GetAwaiter().GetResult();
        }

        var executor = new CleanupExecutor(
            new DirectoryArchiveBackend(
                archiveRoot ?? System.IO.Path.Combine(fixture.Root, "nas"), kind ?? Nas),
            fixture.ArchiveRoot,
            new CleanupAuditLog(System.IO.Path.Combine(fixture.Root, "cleanup-audit.jsonl")),
            VidLog.Desktop.Core.Diagnostics.NullLogger.Instance);

        return new CleanupFlow(
            new CleanupService(
                new FakeIndex(entries ?? []),
                new FakeLabels((entries ?? []).Select(one => Outbound(one.EvidenceId)).ToList()),
                receiptStore,
                new PublishedStore(System.IO.Path.Combine(fixture.Root, "published.jsonl")),
                executor),
            fixture.Storage);
    }

    // ─────────────────────────────────────────────
    // 【按时间清理…】
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 按时间_没有到期该清的也要说一句_因为是用户主动点的()
    {
        // ⚠️ 与**启动时**那一次刻意不同：那边「没有候遷就不打扰」（开机弹一个是噪音），
        // 这边是用户自己点的 —— 什么都不说会让人以为按钮坏了。
        using var fixture = new CleanupFlowFixture();
        var flow = Build(fixture, entries: [Entry("e-000", Now.AddDays(-30))]);

        var preview = await flow.ByTimeAsync(RetentionSettings.KeepAll, Now);

        Assert.Null(preview.Proposal);
        Assert.Equal("按现在的保留期设置，没有到期该清的。", preview.Message);
    }

    [Fact]
    public async Task 按时间_有候选就带着方案和那句开头话()
    {
        using var fixture = new CleanupFlowFixture();
        var flow = Build(fixture, entries: [Entry("e-000", Now.AddDays(-30))]);

        var preview = await flow.ByTimeAsync(DeleteAfter(1), Now);

        var proposal = Assert.IsType<CleanupProposal>(preview.Proposal);
        Assert.Single(proposal.Plan.Candidates);

        // 开头那句话必须说清「这一次要清多少」—— 规格 §3.5.5 的「预告」就是它。
        Assert.Contains("保留期到了的录像有 1 条", proposal.Headline);
    }

    [Fact]
    public async Task 按时间_用的是这一次传进来的保留期_不是建流程时那份()
    {
        // ⚠️ 这条钉的是**保留期按调用传**这个形状（而不是收在字段里）。
        // 收在字段里的话，用户改完保留期再点【按时间清理…】用的还是改之前那个值，
        // 而且**不会报错**，只是清错了东西。
        using var fixture = new CleanupFlowFixture();
        var flow = Build(fixture, entries: [Entry("e-000", Now.AddDays(-30))]);

        Assert.Null((await flow.ByTimeAsync(RetentionSettings.KeepAll, Now)).Proposal);

        // 同一个流程实例，换一个保留期 —— 结论必须跟着变。
        Assert.NotNull((await flow.ByTimeAsync(DeleteAfter(1), Now)).Proposal);
    }

    // ─────────────────────────────────────────────
    // 【按空间释放…】
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 按空间_读不到盘就说算不出来()
    {
        using var fixture = new CleanupFlowFixture();
        fixture.Storage = fixture.NewStorage(freeGb: null);
        var flow = Build(fixture, entries: [Entry("e-000", Now.AddDays(-30))]);

        var preview = await flow.BySpaceAsync(Now);

        Assert.Null(preview.Proposal);
        Assert.Contains("读不到", preview.Message);
        Assert.Contains("算不出来", preview.Message);
    }

    [Fact]
    public async Task 按空间_归档层是本机时照实说不提供()
    {
        // 界面上这一整块在「归档层 = 本机磁盘」时是藏着的，所以走到这里说明
        // 设置在这一瞬间被改了 —— 照实说，别装作算过。
        using var fixture = new CleanupFlowFixture();
        fixture.Storage = fixture.NewStorage(freeGb: 3);
        var flow = Build(
            fixture,
            entries: [Entry("e-000", Now.AddDays(-30))],
            kind: ArchiveBackendKind.LocalDisk);

        var preview = await flow.BySpaceAsync(Now);

        Assert.Null(preview.Proposal);
        Assert.Contains("归档层是本机磁盘", preview.Message);
        Assert.Contains("不提供", preview.Message);
    }

    [Fact]
    public async Task 按空间_没有要清的说还剩多少并带上预留线()
    {
        // 盘上还很宽裕：18 GB 可用、预留线 5 GB ⇒ 够用，没有要清的。
        using var fixture = new CleanupFlowFixture();
        fixture.Storage = fixture.NewStorage(freeGb: 18, reservedGb: 5);
        var flow = Build(fixture, entries: [Entry("e-000", Now.AddDays(-30))]);

        var preview = await flow.BySpaceAsync(Now);

        Assert.Null(preview.Proposal);
        Assert.Contains("没有要清的", preview.Message);
        Assert.Contains("预留线 5 GB", preview.Message);
    }

    [Fact]
    public async Task 按空间_预留线取用户配的那个而不是现算的默认()
    {
        // ⚠️ 这条钉的是「用配置里那个预留空间」。界面里原来是**手抄**一遍
        // `?? DefaultReservedGb(...)`，抄出来的那份一旦与 Core 分家，表现是
        // 「设置页说的预留线」与「清理实际用的预留线」对不上，而且两处都不报错。
        using var fixture = new CleanupFlowFixture();
        fixture.Storage = fixture.NewStorage(freeGb: 3, reservedGb: 5);
        var flow = Build(fixture, entries: [Entry("e-000", Now.AddDays(-30))]);

        var preview = await flow.BySpaceAsync(Now);

        var proposal = Assert.IsType<CleanupProposal>(preview.Proposal);

        // 100 GB 的非系统盘，默认预留是 20 GB（比例下限 5% 只有 5 GB，被 20 GB 的下限压住）
        // —— 界面上说的是 5，所以这句必须写 5。
        Assert.Contains("预留线是 5 GB", proposal.Headline);
    }

    [Fact]
    public async Task 按空间_没配过预留线就按默认现算()
    {
        // 反过来的那一半：一个槽位都没配过（或没填预留）时，按 `ReservedSpace` 现算。
        // 100 GB 的非系统盘 ⇒ max(20 GB, 5%) = 20 GB。
        using var fixture = new CleanupFlowFixture();
        fixture.Storage = fixture.NewStorage(freeGb: 3, reservedGb: null);
        var flow = Build(fixture, entries: [Entry("e-000", Now.AddDays(-30))]);

        var preview = await flow.BySpaceAsync(Now);

        var proposal = Assert.IsType<CleanupProposal>(preview.Proposal);
        Assert.Contains("预留线是 20 GB", proposal.Headline);
    }

    [Fact]
    public async Task 按空间_有候选时那两句要说清_root_条数与可能没腾够()
    {
        // 「可能没腾够」那句是**按空间**独有的：未归档的永不自动删（唯一副本），
        // 所以盘满了也可能清不动。不说的话用户会以为程序白干了一趟。
        using var fixture = new CleanupFlowFixture();
        fixture.Storage = fixture.NewStorage(freeGb: 3, reservedGb: 5);
        var flow = Build(fixture, entries: [Entry("e-000", Now.AddDays(-30))]);

        var preview = await flow.BySpaceAsync(Now);

        var proposal = Assert.IsType<CleanupProposal>(preview.Proposal);
        Assert.Contains(DiskRoot, proposal.Headline);
        Assert.Contains("拟清 1 条", proposal.Headline);
        Assert.Contains("可能没腾够", proposal.Afterword);
    }

    [Fact]
    public async Task 按时间那条没有补充要说_所以补充那句是空的()
    {
        // 两个入口共用同一套「跑完接一句」的收尾，按时间那条不接任何东西 ——
        // 若这里给了一句非空的话，用户点完【按时间清理】会看到一句无关的提醒。
        using var fixture = new CleanupFlowFixture();
        var flow = Build(fixture, entries: [Entry("e-000", Now.AddDays(-30))]);

        var preview = await flow.ByTimeAsync(DeleteAfter(1), Now);

        Assert.Equal(string.Empty, preview.Proposal!.Afterword);
    }

    // ─────────────────────────────────────────────
    // 台子
    // ─────────────────────────────────────────────

    /// <summary>
    /// 一个临时目录 + 一块**假装**的盘。
    /// </summary>
    /// <remarks>
    /// ⚠️ 容量必须是假的：真盘在测试里既填不满也清不动，而「盘快满了」正是
    /// 【按空间释放…】唯一要处理的情形。同样理由见 <c>StorageLocationsTests</c>。
    /// </remarks>
    private sealed class CleanupFlowFixture : IDisposable
    {
        public CleanupFlowFixture()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-cleanup-flow-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(ArchiveRoot);
            Storage = NewStorage(freeGb: 3);
        }

        public string Root { get; }

        public string ArchiveRoot => System.IO.Path.Combine(Root, "archive");

        /// <summary>被验的那个流程会用到的「盘」。用例可以在建流程**之前**换掉它。</summary>
        public StorageLocations Storage { get; set; }

        /// <param name="freeGb"><see langword="null"/> = 这块盘读不到（拔了、没权限）。</param>
        /// <param name="reservedGb"><see langword="null"/> = 没配过，按默认现算。</param>
        public StorageLocations NewStorage(long? freeGb, long? reservedGb = null) => new(
            [new DiskSlot(DiskRoot, (int?)reservedGb)],
            fallbackRoot: System.IO.Path.Combine(Root, "fallback"),
            probe: new FakeProbe
            {
                [DiskRoot] = freeGb is { } free ? new VolumeSpace(free * Gb, 100 * Gb) : null,
            });

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FakeProbe : Dictionary<string, VolumeSpace?>, IVolumeSpaceProbe
    {
        public VolumeSpace? Measure(string path) =>
            TryGetValue(path, out var space) ? space : null;
    }

    private sealed class FakeIndex(IReadOnlyList<RecordingEntry> entries) : IRecordingIndex
    {
        public Task AddAsync(RecordingEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RecordingEntry>> LoadAllAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(entries);
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
