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
            [Entry("e1", Now.AddDays(-365))], NoLabels(), RetentionPolicy.KeepAll, Now);

        Assert.Empty(plan.Candidates);
        Assert.Single(plan.Exempted);
    }

    [Fact]
    public void 最近24小时的一律不清()
    {
        // 刚录完的东西往往还在被检查、被导出，而清理不可逆 —— 给一个冷静期。
        var plan = new CleanupPlanner().Plan(
            [Entry("e1", Now.AddHours(-2))], NoLabels(), new RetentionPolicy(RetentionMode.ByDays, KeepDays: 7), Now);

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
            [Entry("e1", Now.AddDays(-365))], locked,
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
            [Entry("e1", Now.AddDays(-365))], unlocked,
            new RetentionPolicy(RetentionMode.ByDays, KeepDays: 7), Now);

        Assert.Single(plan.Candidates);
    }

    [Fact]
    public void 超过保留期的进候选()
    {
        var plan = new CleanupPlanner().Plan(
            [Entry("e1", Now.AddDays(-30)), Entry("e2", Now.AddDays(-2))],
            NoLabels(), new RetentionPolicy(RetentionMode.ByDays, KeepDays: 7), Now);

        Assert.Single(plan.Candidates);
        Assert.Equal("e1", plan.Candidates[0].Entry.EvidenceId);
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
            entries, NoLabels(),
            new RetentionPolicy(RetentionMode.BySpace, MinFreeBytes: 10L * 1024 * 1024),
            Now, freeBytes: 9L * 1024 * 1024);

        Assert.Equal("old", plan.Candidates[0].Entry.EvidenceId);
        Assert.Contains(plan.Exempted, e => e.Why.Contains("已经清够"));
    }

    [Fact]
    public void 空间充足时什么都不清()
    {
        var plan = new CleanupPlanner().Plan(
            [Entry("e1", Now.AddDays(-30))], NoLabels(),
            new RetentionPolicy(RetentionMode.BySpace, MinFreeBytes: 1024),
            Now, freeBytes: 100L * 1024 * 1024 * 1024);

        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void 豁免的也要给出理由()
    {
        // 规格 §3.5.5：用户要能问「这条为什么被删 / 为什么没删」。
        var plan = new CleanupPlanner().Plan(
            [Entry("e1", Now.AddHours(-1))], NoLabels(),
            new RetentionPolicy(RetentionMode.ByDays, KeepDays: 7), Now);

        Assert.All(plan.Exempted, e => Assert.False(string.IsNullOrWhiteSpace(e.Why)));
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
