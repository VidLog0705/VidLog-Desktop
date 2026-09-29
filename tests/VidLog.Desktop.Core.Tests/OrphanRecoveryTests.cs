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
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（2026-09-27 实测：会话还在
            // 收尾时删含 `.ass` / `.mkv` 的临时目录）。清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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

    /// <param name="ffmpegPath">
    /// 真 ffmpeg 的路径。⚠️ **不许写死 `"ffmpeg"`** —— 2026-09-29 实测：
    /// 写死的话这台机器上那条端到端用例**必然红**（本机 ffmpeg 不在 PATH 上，
    /// 只有 `FFMPEG_EXE` 指得到它），而它在 CI 上又是绿的（CI 把 ffmpeg 装在 PATH 里）
    /// ⇒ 一条「只在 CI 上绿」的用例，本机永远验不了收尾那条路。
    /// 那几条用假 runner 的用例随便传什么都行，它们不真开进程。
    /// </param>
    private static OrphanRecovery BuildRecovery(
        TempDir dir, RecordingWorkspace workspace, IProcessRunner runner, IRecordingIndex index,
        string ffmpegPath = "ffmpeg")
    {
        var finalizer = new SessionFinalizer(
            new RemuxPipeline(ffmpegPath, runner),
            new DecodeVerifier(ffmpegPath, runner),
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

    [Fact]
    public async Task 反复写同一个manifest每次都成功且内容完整()
    {
        // Windows 上「改名覆盖已存在的文件」可能被拒（Defender 扫新文件时持有句柄），
        // 而 manifest 是**反复写同一个文件**的（开录一次、每个分段封闭再一次）。
        // 手机端先踩到这个坑：写入失败会让录像重启后收不了尾。
        // 这条覆盖的正是「重复写」那条路径 —— 之前只测过写一次。
        using var dir = new TempDir();
        var workspace = new RecordingWorkspace(dir.Dir("work"));

        for (var i = 0; i < 10; i++)
        {
            await workspace.WriteManifestAsync(ManifestFor("s1", $"segment-{i:000}.mkv"));
        }

        var sessionDirectory = workspace.SessionDirectory("s1");
        var json = await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "session.json"));

        Assert.Contains("segment-009.mkv", json, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(sessionDirectory, "*.tmp"));
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
        var recovery = BuildRecovery(dir, workspace, runner, index, ffmpeg);

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
