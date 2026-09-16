using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 规格 §3.1.1 / §8：录制中进程被杀 → 重启后必须能自动收尾孤儿分段，
/// 不产生无法播放的半成品。
/// </summary>
public class OrphanRecoveryTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-orphan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Dir(string name) => System.IO.Path.Combine(Path, name);
        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class SucceedingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            var args = arguments.ToList();
            var yIndex = args.IndexOf("-y");
            if (yIndex >= 0 && yIndex + 1 < args.Count)
            {
                var output = args[yIndex + 1];
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
                File.WriteAllText(output, "published-bytes");
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private static SessionManifest ManifestFor(string sessionId, params string[] segmentFiles) => new(
        sessionId,
        "SF1234567890",
        "device-1",
        "2026-09-16T10:30:00+08:00",
        segmentFiles
            .Select((name, index) => new SegmentManifest(
                index,
                name,
                "2026-09-16T10:30:00+08:00",
                "2026-09-16T10:31:00+08:00"))
            .ToList());

    /// <summary>造一个「录到一半被杀」的工作区：有分段、有 manifest、没有 finalized 标记。</summary>
    private static async Task<RecordingWorkspace> CreateKilledSessionAsync(
        TempDir dir,
        string sessionId = "session-killed",
        bool markFinalized = false)
    {
        var workspace = new RecordingWorkspace(dir.Dir("work"));
        var sessionDir = workspace.SessionDirectory(sessionId);
        Directory.CreateDirectory(sessionDir);

        var segmentName = "segment-000.mkv";
        await File.WriteAllTextAsync(System.IO.Path.Combine(sessionDir, segmentName), "intermediate-mkv-bytes");

        await workspace.WriteManifestAsync(ManifestFor(sessionId, segmentName));

        if (markFinalized)
        {
            await workspace.MarkFinalizedAsync(sessionId);
        }

        return workspace;
    }

    private static OrphanRecovery BuildRecovery(
        TempDir dir, RecordingWorkspace workspace, IProcessRunner runner, IRecordingIndex index)
    {
        var finalizer = new SessionFinalizer(
            new RemuxPipeline("ffmpeg", runner),
            new DecodeVerifier("ffmpeg", runner),
            index,
            dir.Dir("archive"));

        return new OrphanRecovery(workspace, finalizer);
    }

    // ─────────────────────────────────────────────
    // 孤儿发现
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 没有收尾标记的会话被认作孤儿()
    {
        using var dir = new TempDir();
        var workspace = await CreateKilledSessionAsync(dir);

        var orphans = await workspace.ListOrphansAsync();

        var orphan = Assert.Single(orphans);
        Assert.Equal("session-killed", orphan.SessionId);
        Assert.Equal("SF1234567890", orphan.Waybill.Value);
        Assert.Equal("device-1", orphan.SourceDeviceId);
        Assert.Single(orphan.Segments);
    }

    [Fact]
    public async Task 已收尾的会话不是孤儿()
    {
        using var dir = new TempDir();
        var workspace = await CreateKilledSessionAsync(dir, markFinalized: true);

        Assert.Empty(await workspace.ListOrphansAsync());
    }

    [Fact]
    public async Task 分段文件已消失的会话不是孤儿()
    {
        // 没有东西可以收尾，硬报一条只是噪声。
        using var dir = new TempDir();
        var workspace = new RecordingWorkspace(dir.Dir("work"));
        await workspace.WriteManifestAsync(ManifestFor("session-gone", "missing.mkv"));

        Assert.Empty(await workspace.ListOrphansAsync());
    }

    [Fact]
    public async Task 旧文件半截JSON不会让发现流程崩掉()
    {
        using var dir = new TempDir();
        var workspace = new RecordingWorkspace(dir.Dir("work"));
        var sessionDir = workspace.SessionDirectory("session-broken");
        Directory.CreateDirectory(sessionDir);

        // 原子写要防的正是这种半截文件；历史遗留或外部中断仍可能留下
        await File.WriteAllTextAsync(System.IO.Path.Combine(sessionDir, "session.json"), "{ \"SessionId\": \"sess");

        Assert.Empty(await workspace.ListOrphansAsync());
    }

    // ─────────────────────────────────────────────
    // 收尾行为
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 孤儿收尾走的是同一个收尾器_停录原因是进程被杀()
    {
        using var dir = new TempDir();
        var workspace = await CreateKilledSessionAsync(dir);
        var index = new JsonLinesRecordingIndex(dir.File("index.jsonl"));
        var recovery = BuildRecovery(dir, workspace, new SucceedingRunner(), index);

        var outcomes = await recovery.RecoverAsync();

        var outcome = Assert.Single(outcomes);
        Assert.Equal(StopReason.ProcessKilled, outcome.Reason);
        Assert.Equal(RecordingSessionState.Indexed, outcome.State);
    }

    [Fact]
    public async Task 收尾成功后打上标记_下次启动不再重复收尾()
    {
        using var dir = new TempDir();
        var workspace = await CreateKilledSessionAsync(dir);
        var index = new JsonLinesRecordingIndex(dir.File("index.jsonl"));
        var recovery = BuildRecovery(dir, workspace, new SucceedingRunner(), index);

        await recovery.RecoverAsync();

        Assert.Empty(await workspace.ListOrphansAsync());
    }

    [Fact]
    public async Task 收尾失败不打标记_下次启动会重试()
    {
        // 源 MKV 一定还在（I2），所以失败可能只是瞬时故障 —— 不能当成不可恢复丢掉。
        using var dir = new TempDir();
        var workspace = await CreateKilledSessionAsync(dir);
        var index = new JsonLinesRecordingIndex(dir.File("index.jsonl"));

        var failingRunner = new FailingRunner();
        var recovery = BuildRecovery(dir, workspace, failingRunner, index);

        var outcomes = await recovery.RecoverAsync();

        Assert.False(Assert.Single(outcomes).Succeeded);
        Assert.Single(await workspace.ListOrphansAsync());
    }

    private sealed class FailingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessResult(1, string.Empty, "boom"));
    }

    // ─────────────────────────────────────────────
    // 端到端：真 FFmpeg
    // ─────────────────────────────────────────────

    [RequiresFfmpegFact]
    public async Task 端到端_被杀会话重启后产出可播成品并写进索引()
    {
        var ffmpeg = FfmpegLocator.TryFind()!;
        var runner = new SystemProcessRunner();

        using var dir = new TempDir();
        var workspace = new RecordingWorkspace(dir.Dir("work"));
        var sessionDir = workspace.SessionDirectory("session-real");
        Directory.CreateDirectory(sessionDir);

        // 先真的录一段 MKV（模拟被强杀时留在盘上的中间容器）
        var segmentPath = System.IO.Path.Combine(sessionDir, "segment-000.mkv");
        var encode = await runner.RunAsync(ffmpeg, [
            "-hide_banner", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=1",
            "-c:v", "libx264", "-y", segmentPath,
        ]);
        Assert.True(encode.Succeeded, encode.StandardError);

        await workspace.WriteManifestAsync(ManifestFor("session-real", "segment-000.mkv"));

        var index = new JsonLinesRecordingIndex(dir.File("index.jsonl"));
        var recovery = BuildRecovery(dir, workspace, runner, index);

        var outcomes = await recovery.RecoverAsync();

        // 1. 收尾成功
        var outcome = Assert.Single(outcomes);
        Assert.True(outcome.Succeeded, outcome.FailureReason);

        // 2. 成品真的能解码（规格 §3.1.4）
        var published = Assert.Single(outcome.Segments).PublishedPath!;
        var verification = await new DecodeVerifier(ffmpeg, runner).VerifyAsync(published);
        Assert.True(verification.IsPlayable, verification.FailureReason);

        // 3. 进了索引，且索引里只有相对路径
        var entries = await index.LoadAllAsync();
        var entry = Assert.Single(entries);
        Assert.Equal("SF1234567890", entry.Waybill.Value);
        Assert.False(System.IO.Path.IsPathRooted(entry.Location.Value));

        // 4. 已经打过标记，不会重复收尾
        Assert.Empty(await workspace.ListOrphansAsync());
    }
}
