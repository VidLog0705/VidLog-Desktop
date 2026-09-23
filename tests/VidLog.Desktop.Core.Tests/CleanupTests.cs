using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 生命周期清理（规格 §3.5）。
/// </summary>
/// <remarks>
/// 最要紧的是 **I8**：清理前必须回查归档层，回查不通过**绝不删除**。
/// 下面一半的用例都在验这条。
/// </remarks>
public class CleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    // ─────────────────────────────────────────────
    // 计划：三条豁免
    // ─────────────────────────────────────────────

    [Fact]
    public void 默认策略是全部保留_谁都不清()
    {
        var plan = new CleanupPlanner().Plan(
            [Entry("e1", Now.AddDays(-365))], NoLabels(), Archived("e1"), RetentionPolicy.KeepAll, Now);

        Assert.Empty(plan.Candidates);
        Assert.Single(plan.Exempted);
    }

    [Fact]
    public void 最近24小时的一律不清()
    {
        // 刚录完的东西往往还在被检查、被导出，而清理不可逆 —— 给一个冷静期。
        var plan = new CleanupPlanner().Plan(
            [Entry("e1", Now.AddHours(-2))], NoLabels(), Archived("e1"),
            new RetentionPolicy(RetentionMode.ByDays, KeepDays: 7), Now);

        Assert.Empty(plan.Candidates);
        Assert.Contains(plan.Exempted, e => e.Why.Contains("24"));
    }

    [Fact]
    public void 被锁定的不清()
    {
        var locked = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["e1"] = new Dictionary<string, string> { [LabelKeys.Locked] = "true" },
        };

        var plan = new CleanupPlanner().Plan(
            [Entry("e1", Now.AddDays(-365))], locked, Archived("e1"),
            new RetentionPolicy(RetentionMode.ByDays, KeepDays: 7), Now);

        Assert.Empty(plan.Candidates);
        Assert.Contains(plan.Exempted, e => e.Why.Contains("锁定"));
    }

    [Fact]
    public void 解锁之后可以清()
    {
        // 标签是追加写、后者胜出 —— 所以「解锁」就是再追加一条 false。
        var unlocked = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["e1"] = new Dictionary<string, string> { [LabelKeys.Locked] = "false" },
        };

        var plan = new CleanupPlanner().Plan(
            [Entry("e1", Now.AddDays(-365))], unlocked, Archived("e1"),
            new RetentionPolicy(RetentionMode.ByDays, KeepDays: 7), Now);

        Assert.Single(plan.Candidates);
    }

    [Fact]
    public void 认不出来的锁值一律当锁着()
    {
        // 朝**少删**的那头落。与 `RetentionSetting.fromConfig` 解析失败回落到
        // 「全部保留」、归档状态认不出当 pending 是同一条规矩：把锁读丢了的代价是
        // **删掉用户锁上的证据**，而反过来只是少清一条（它在豁免列表里看得见）。
        // ⚠️ 手机端 `lifecycle.dart` 的 `_isLocked` 必须与这里同向。
        foreach (var raw in new[] { "1", "yes", "", "TRUE ", "被人手改坏了" })
        {
            var labels = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["e1"] = new Dictionary<string, string> { [LabelKeys.Locked] = raw },
            };

            var plan = new CleanupPlanner().Plan(
                [Entry("e1", Now.AddDays(-365))], labels, Archived("e1"),
                new RetentionPolicy(RetentionMode.ByDays, KeepDays: 7), Now);

            Assert.Empty(plan.Candidates);
            Assert.Contains(plan.Exempted, e => e.Why.Contains("锁定"));
        }
    }

    [Fact]
    public void 超过保留期的进候选()
    {
        var plan = new CleanupPlanner().Plan(
            [Entry("e1", Now.AddDays(-30)), Entry("e2", Now.AddDays(-2))],
            NoLabels(),
            // 一条 30 天前归档、一条 2 天前归档。**起算点是归档时刻**，
            // 所以分界的是 e2 的归档时间，不是它「录于 2 天前」。
            new Dictionary<string, DateTimeOffset>
            {
                ["e1"] = Now.AddDays(-30),
                ["e2"] = Now.AddDays(-2),
            },
            new RetentionPolicy(RetentionMode.ByDays, KeepDays: 7), Now);

        Assert.Single(plan.Candidates);
        Assert.Equal("e1", plan.Candidates[0].Entry.EvidenceId);
    }

    [Fact]
    public void 还没归档的一律不清_它是唯一副本()
    {
        // 规格 §3.5.3①，硬豁免。归档状态表里没有这条 = 它还没传上去，
        // 本地这份就是**唯一副本**（I2）。与手机端 `planCleanup` 同一个判法。
        var plan = new CleanupPlanner().Plan(
            [Entry("e1", Now.AddDays(-365))], NoLabels(), NeverArchived(),
            new RetentionPolicy(RetentionMode.ByDays, KeepDays: 7), Now);

        Assert.Empty(plan.Candidates);
        Assert.Contains(plan.Exempted, e => e.Why.Contains("唯一副本"));
    }

    [Fact]
    public void 录完很久但刚归档的不清_起算点是归档成功时刻()
    {
        // 规格 §3.5.2.1：保留期自**归档成功时刻**起算，不自录制结束时刻。
        // 依据是 §4.3 的合取式「归档成功 **且** 超过保留期」—— 一台离线 35 天的
        // 机器若按录完时刻算，会在**刚归档那一瞬间**就被删掉，那等于绕开了
        // 「至少一份副本」（I2）的意图。
        // §9 的验收判据原文：「离线 35 天后才归档的段，**归档当天不算已过期**」。
        var plan = new CleanupPlanner().Plan(
            [Entry("e1", Now.AddDays(-35))], NoLabels(), ArchivedAt(Now.AddHours(-1), "e1"),
            new RetentionPolicy(RetentionMode.ByDays, KeepDays: 3), Now);

        Assert.Empty(plan.Candidates);
        Assert.Contains(plan.Exempted, e => e.Why.Contains("还在 3 天保留期内"));
    }

    [Fact]
    public void 按空间清时从最旧的开始清到够为止()
    {
        var entries = new[]
        {
            Entry("old", Now.AddDays(-30)),
            Entry("mid", Now.AddDays(-10)),
            Entry("new", Now.AddDays(-2)),
        };

        // 差 1MB；每条按时长估约 9.6MB/分钟 ⇒ 一条就够。
        var plan = new CleanupPlanner().Plan(
            entries, NoLabels(), Archived("old", "mid", "new"),
            new RetentionPolicy(RetentionMode.BySpace, MinFreeBytes: 10L * 1024 * 1024),
            Now, freeBytes: 9L * 1024 * 1024);

        Assert.Equal("old", plan.Candidates[0].Entry.EvidenceId);
        Assert.Contains(plan.Exempted, e => e.Why.Contains("已经清够"));
    }

    [Fact]
    public void 空间充足时什么都不清()
    {
        var plan = new CleanupPlanner().Plan(
            [Entry("e1", Now.AddDays(-30))], NoLabels(), Archived("e1"),
            new RetentionPolicy(RetentionMode.BySpace, MinFreeBytes: 1024),
            Now, freeBytes: 100L * 1024 * 1024 * 1024);

        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void 豁免的也要给出理由()
    {
        // 规格 §3.5.5：用户要能问「这条为什么被删 / 为什么没删」。
        var plan = new CleanupPlanner().Plan(
            [Entry("e1", Now.AddHours(-1))], NoLabels(), Archived("e1"),
            new RetentionPolicy(RetentionMode.ByDays, KeepDays: 7), Now);

        Assert.All(plan.Exempted, e => Assert.False(string.IsNullOrWhiteSpace(e.Why)));
    }

    // ─────────────────────────────────────────────
    // 按业务类型分开的保留期（规格 §3.5.2.1）
    // ─────────────────────────────────────────────

    [Fact]
    public void 发货和退货各用自己那一份保留期()
    {
        // 需求方 2026-09-23：发货与退货**各自一份，不共用**。
        var labels = Typed(("out", BusinessType.Outbound), ("ret", BusinessType.Return));

        var plan = new CleanupPlanner().PlanPerBusinessType(
            [Entry("out", Now.AddDays(-5)), Entry("ret", Now.AddDays(-5))],
            labels,
            ArchivedAt(Now.AddDays(-5), "out", "ret"),
            new RetentionPolicies(
                new RetentionPolicy(RetentionMode.ByDays, KeepDays: 3),
                new RetentionPolicy(RetentionMode.ByDays, KeepDays: 30)),
            Now);

        // 同样录于 5 天前、同样 5 天前归档：发货那件超了 3 天，退货那件还在 30 天里。
        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal("out", candidate.Entry.EvidenceId);
        Assert.Contains(plan.Exempted, e => e.Entry.EvidenceId == "ret");
    }

    [Fact]
    public void 改发货那一份不影响退货()
    {
        var labels = Typed(("ret", BusinessType.Return));

        // 发货调到「不保留」，退货仍是 30 天 —— 退货这条不该被牵着走。
        var plan = new CleanupPlanner().PlanPerBusinessType(
            [Entry("ret", Now.AddDays(-5))], labels, ArchivedAt(Now.AddDays(-5), "ret"),
            new RetentionPolicies(
                new RetentionPolicy(RetentionMode.ByDays, KeepDays: 0),
                new RetentionPolicy(RetentionMode.ByDays, KeepDays: 30)),
            Now);

        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void 没有业务类型标签的一律不清_且说明原因()
    {
        // 判不出它是发货还是退货 —— 猜错的代价是删掉证据，猜不出的代价只是占地方。
        var plan = new CleanupPlanner().PlanPerBusinessType(
            [Entry("e1", Now.AddDays(-365))], NoLabels(), Archived("e1"),
            new RetentionPolicies(
                new RetentionPolicy(RetentionMode.ByDays, KeepDays: 0),
                new RetentionPolicy(RetentionMode.ByDays, KeepDays: 0)),
            Now);

        Assert.Empty(plan.Candidates);
        Assert.Contains(plan.Exempted, e => e.Why.Contains("业务类型"));
    }

    [Fact]
    public void 不保留的实际语义是24小时()
    {
        // 规格 §3.5.2.1：「不保留」= 0 天；但 §3.5.3③ 的 24 小时豁免硬性、
        // 用户不可关闭 —— 所以实际生效是 max(24h, 0) = 24 小时，不是「立刻删」。
        var noKeep = new RetentionPolicies(
            new RetentionPolicy(RetentionMode.ByDays, KeepDays: 0),
            new RetentionPolicy(RetentionMode.ByDays, KeepDays: 0));

        var fresh = new CleanupPlanner().PlanPerBusinessType(
            [Entry("e1", Now.AddHours(-2))], Typed(("e1", BusinessType.Outbound)),
            Archived("e1"), noKeep, Now);
        Assert.Empty(fresh.Candidates);

        var stale = new CleanupPlanner().PlanPerBusinessType(
            [Entry("e1", Now.AddHours(-25))], Typed(("e1", BusinessType.Outbound)),
            Archived("e1"), noKeep, Now);
        Assert.Single(stale.Candidates);
    }

    [Fact]
    public void 两份都是全部保留时谁都不清()
    {
        var plan = new CleanupPlanner().PlanPerBusinessType(
            [Entry("out", Now.AddDays(-365)), Entry("ret", Now.AddDays(-365))],
            Typed(("out", BusinessType.Outbound), ("ret", BusinessType.Return)),
            Archived("out", "ret"),
            RetentionPolicies.KeepAll,
            Now);

        Assert.Empty(plan.Candidates);
        Assert.Equal(2, plan.Exempted.Count);
    }

    [Fact]
    public void 下拉选项与需求方列举的一致()
    {
        // 需求方原话是「不保留/3/5/7/10/15/30/」；
        // **「全部保留」是规格 §3.5.2 本来就规定的默认**，所以多这一项 ——
        // 这个偏差要跟他确认（母仓 交接.md §5）。
        Assert.Equal(
            new[] { "全部保留", "不保留", "3 天", "5 天", "7 天", "10 天", "15 天", "30 天" },
            RetentionPolicies.Choices.Select(c => c.Label));
    }

    // ─────────────────────────────────────────────
    // 执行：I8 的落点
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 归档层是本机磁盘时根本不删()
    {
        // 规格 §3.5.1：那时该副本是唯一副本，不提供清理选项。
        using var dir = new TempDir();
        var executor = BuildExecutor(dir, new LocalFolderArchiveBackend(dir.ArchiveRoot));
        var plan = PlanFor(Entry("e1", Now.AddDays(-365)));

        Assert.False(executor.CanCleanup);

        var report = await executor.ExecuteAsync(plan);

        Assert.Empty(report.Deleted);
        Assert.Single(report.Refused);
    }

    [Fact]
    public async Task 回查说文件不在时绝不删()
    {
        using var dir = new TempDir();
        var file = dir.WriteArtifact("a.mp4");

        // ★ I8：归档层上找不到 ⇒ 不删本地副本。
        var executor = BuildExecutor(dir, new FakeArchive(ArchiveBackendKind.Cloud, exists: false));
        var report = await executor.ExecuteAsync(PlanFor(Entry("e1", Now.AddDays(-365), "a.mp4")));

        Assert.Empty(report.Deleted);
        Assert.Single(report.Refused);
        Assert.True(File.Exists(file), "I8：回查不通过时本地副本必须原样保留");
    }

    [Fact]
    public async Task 回查不了时也绝不删()
    {
        using var dir = new TempDir();
        var file = dir.WriteArtifact("a.mp4");

        // 「查不了」**不能**当成「不存在」—— 那会删掉最后一份副本（I2）。
        // 网络断了、凭据失效都属这一类。
        var executor = BuildExecutor(dir, new FakeArchive(
            ArchiveBackendKind.Cloud, exists: false, failure: "网络不可达"));
        var report = await executor.ExecuteAsync(PlanFor(Entry("e1", Now.AddDays(-365), "a.mp4")));

        Assert.Empty(report.Deleted);
        Assert.True(File.Exists(file), "I8：回查不了时必须原样保留");
    }

    [Fact]
    public async Task 回查通过才删()
    {
        using var dir = new TempDir();
        var file = dir.WriteArtifact("a.mp4");

        var executor = BuildExecutor(dir, new FakeArchive(ArchiveBackendKind.Cloud, exists: true));
        var report = await executor.ExecuteAsync(PlanFor(Entry("e1", Now.AddDays(-365), "a.mp4")));

        Assert.Single(report.Deleted);
        Assert.Empty(report.Refused);
    }

    [Fact]
    public async Task 回查抛异常时也不删()
    {
        using var dir = new TempDir();
        var file = dir.WriteArtifact("a.mp4");

        var executor = BuildExecutor(dir, new ThrowingArchive());
        var report = await executor.ExecuteAsync(PlanFor(Entry("e1", Now.AddDays(-365), "a.mp4")));

        Assert.Empty(report.Deleted);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task 每次清理都留审计()
    {
        using var dir = new TempDir();
        dir.WriteArtifact("a.mp4");
        var audit = new CleanupAuditLog(dir.AuditPath);

        var executor = BuildExecutor(dir, new FakeArchive(ArchiveBackendKind.Cloud, exists: true), audit);
        await executor.ExecuteAsync(PlanFor(Entry("e1", Now.AddDays(-365), "a.mp4")));

        // 规格 §6.2：禁止静默清理 —— 清理必须留可查的记录。
        var records = await audit.LoadAllAsync();
        var record = Assert.Single(records);
        Assert.Equal("deleted", record.Action);
        Assert.Equal("e1", record.EvidenceId);
    }

    [Fact]
    public async Task 拒绝的也留审计()
    {
        using var dir = new TempDir();
        var audit = new CleanupAuditLog(dir.AuditPath);

        var executor = BuildExecutor(dir, new FakeArchive(ArchiveBackendKind.Cloud, exists: false), audit);
        await executor.ExecuteAsync(PlanFor(Entry("e1", Now.AddDays(-365))));

        var record = Assert.Single(await audit.LoadAllAsync());
        Assert.Equal("refused", record.Action);
    }

    // ─────────────────────────────────────────────
    // 脚手架
    // ─────────────────────────────────────────────

    private static RecordingEntry Entry(string id, DateTimeOffset endedAt, string fileName = "a.mp4") =>
        new(id, "sess-1", WaybillNumber.Parse("SF1"), endedAt.AddMinutes(-1), endedAt,
            TimeSpan.FromMinutes(1), RelativePath.Parse(fileName), ContentHash.Parse(new string('a', 64)), "dev-1");

    private static CleanupPlan PlanFor(params RecordingEntry[] entries) =>
        new([.. entries.Select(e => new CleanupCandidate(e, 0, e.Location, "测试"))], []);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NoLabels() =>
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// <summary>空的归档状态表 —— 这些录像一条都还没成功归档（规格 §3.5.3①）。</summary>
    private static IReadOnlyDictionary<string, DateTimeOffset> NeverArchived() =>
        new Dictionary<string, DateTimeOffset>();

    /// <summary>归档状态表：给定每条录像的**归档成功时刻**（回执里的 timeAnchor）。</summary>
    private static IReadOnlyDictionary<string, DateTimeOffset> ArchivedAt(
        DateTimeOffset anchor, params string[] ids) =>
        ids.ToDictionary(id => id, _ => anchor);

    /// <summary>默认归档状态：30 天前就归档成功了 —— 出了任何一档保留期，默认结论是「该清」。</summary>
    /// <remarks>
    /// 这个默认值是**故意挑的**：它让「该清」的用例不必自己造表，而
    /// 要验「还没归档」或「刚归档」的用例**必须**自己传表 —— 那正是它们要验的东西。
    /// </remarks>
    private static IReadOnlyDictionary<string, DateTimeOffset> Archived(params string[] ids) =>
        ArchivedAt(Now.AddDays(-30), ids);

    /// <summary>只给业务类型标签的标签表（规格 §3.5.2.1 判定要用的就这一个键）。</summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Typed(
        params (string EvidenceId, BusinessType Type)[] items) =>
        items.ToDictionary(
            i => i.EvidenceId,
            i => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                [LabelKeys.BusinessType] = BusinessTypes.ToValue(i.Type),
            });

    private static CleanupExecutor BuildExecutor(
        TempDir dir, IArchiveBackend archive, CleanupAuditLog? audit = null) =>
        new(archive, dir.ArchiveRoot, audit ?? new CleanupAuditLog(dir.AuditPath), NullLogger.Instance);

    private sealed class FakeArchive(
        ArchiveBackendKind kind, bool exists, string? failure = null) : IArchiveBackend
    {
        public ArchiveBackendKind Kind => kind;

        public Task<ArchiveVerifyResult> VerifyAsync(
            RelativePath location, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArchiveVerifyResult(exists, failure));
    }

    private sealed class ThrowingArchive : IArchiveBackend
    {
        public ArchiveBackendKind Kind => ArchiveBackendKind.Cloud;

        public Task<ArchiveVerifyResult> VerifyAsync(
            RelativePath location, CancellationToken cancellationToken = default) =>
            throw new IOException("回查炸了");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public string ArchiveRoot => System.IO.Path.Combine(Path, "archive");
        public string AuditPath => System.IO.Path.Combine(Path, "cleanup-audit.jsonl");

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-clean-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ArchiveRoot);
        }

        public string WriteArtifact(string name)
        {
            var file = System.IO.Path.Combine(ArchiveRoot, name);
            File.WriteAllText(file, "artifact");
            return file;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
