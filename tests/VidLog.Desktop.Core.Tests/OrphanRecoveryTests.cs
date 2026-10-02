using VidLog.Desktop.Core.Diagnostics;
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

    /// <summary>把日志收起来，好在测试里断言（本仓既有写法）。</summary>
    private sealed class CapturingLogger : IAppLogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void Log(LogLevel level, string category, string message) =>
            Entries.Add((level, message));

        public void Log(
            LogLevel level, string category, string message, IReadOnlyDictionary<string, object?> data) =>
            Entries.Add((level, message));
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

    /// <summary>
    /// 同上，但 manifest 里**记着**这一场用的规格 —— 2026-10-02 起正常录制的写法
    /// （<c>RecordingSession.StartAsync</c> 就是这么写的）。
    /// </summary>
    private static SessionManifest ManifestWithSpecFor(
        string sessionId, RecordingSpec spec, params string[] segmentFiles) =>
        ManifestFor(sessionId, segmentFiles) with { Spec = SessionSpecManifest.From(spec) };

    /// <summary>造一个「录到一半被杀」的工作区：有分段、有 manifest、没有 finalized 标记。</summary>
    private static async Task<RecordingWorkspace> CreateKilledSessionAsync(
        TempDir dir,
        string sessionId = "session-killed",
        bool markFinalized = false,
        RecordingSpec? spec = null)
    {
        var workspace = new RecordingWorkspace(dir.Dir("work"));
        var sessionDir = workspace.SessionDirectory(sessionId);
        Directory.CreateDirectory(sessionDir);

        var segmentName = "segment-000.mkv";
        await File.WriteAllTextAsync(System.IO.Path.Combine(sessionDir, segmentName), "intermediate-mkv-bytes");

        await workspace.WriteManifestAsync(spec is null
            ? ManifestFor(sessionId, segmentName)
            : ManifestWithSpecFor(sessionId, spec, segmentName));

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

    /// <summary>
    /// **孤儿发现把这一场的录制规格一起带出来了。**
    /// </summary>
    /// <remarks>
    /// ⚠️ 规格**只能从 `session.json` 读回来** —— 进程是被杀的，内存里那份早没了。
    /// 所以这条判的正是「它有没有落到盘上」，而下一段（收尾）判的是「有没有被读回来」。
    /// </remarks>
    [Fact]
    public async Task 孤儿发现时把录制规格一起带出来()
    {
        using var dir = new TempDir();
        var spec = new RecordingSpec(VideoCodec.H264, VideoResolution.P720, CameraRotation.Left90);
        var workspace = await CreateKilledSessionAsync(dir, spec: spec);

        var orphan = Assert.Single(await workspace.ListOrphansAsync());

        Assert.Equal(spec, orphan.Spec);
    }

    /// <summary>
    /// **老 <c>session.json</c>（追加规格字段之前写的）照样读得出来，只是规格为空。**
    /// </summary>
    /// <remarks>
    /// ⚠️ 这一条守的是**就地升级**：用户的机器上有升级前留下的会话目录，里面那份
    /// manifest 没有 <c>Spec</c> 这一项。读不出来 ⇒ <see cref="RecordingWorkspace.ListOrphansAsync"/>
    /// 静默 <c>continue</c> ⇒ **那批分段永远收不了尾**（本仓唯一会丢证据的方向，I2/I9）。
    /// 「规格读回来是空」可以接受，「整个会话读不出来」不行。
    /// </remarks>
    [Fact]
    public async Task 老的manifest没有规格字段时照样读得出来_只是规格为空()
    {
        using var dir = new TempDir();
        var workspace = new RecordingWorkspace(dir.Dir("work"));
        var sessionDir = workspace.SessionDirectory("session-old");
        Directory.CreateDirectory(sessionDir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(sessionDir, "segment-000.mkv"), "bytes");

        // 逐字照**追加 Spec 之前**那份写出来的形状。
        const string oldJson = """{"SessionId":"session-old","Waybill":"SF1234567890","SourceDeviceId":"device-1","StartedAt":"2026-09-16T10:30:00+08:00","Segments":[{"Sequence":0,"FileName":"segment-000.mkv","StartedAt":"2026-09-16T10:30:00+08:00","EndedAt":"2026-09-16T10:31:00+08:00"}]}""";
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(sessionDir, "session.json"), oldJson);

        var orphan = Assert.Single(await workspace.ListOrphansAsync());

        Assert.Null(orphan.Spec);
        Assert.Single(orphan.Segments);
    }

    [Fact]
    public void 规格写得进去也读得回来()
    {
        var spec = new RecordingSpec(VideoCodec.H265, VideoResolution.Uhd4K, CameraRotation.Right90);

        Assert.Equal(spec, SessionSpecManifest.From(spec).ToSpec());
    }

    [Theory]
    [InlineData("H999", "P720", "None")]
    [InlineData("H264", "P999", "None")]
    [InlineData("H264", "P720", "Sideways")]
    public void 规格里有一个值认不出时_整档算认不出_不逐项回落(
        string codec, string resolution, string rotation)
    {
        // ⚠️ 不逐项回落是有意的：这组数要写进索引、还要拿去估容量。
        // 拿默认档补上一个认不出的字段，会得到一条**看着正常、其实是编的**元数据 ——
        // 那比空着坏（空着时估容量那一头至少知道自己是在猜）。
        Assert.Null(new SessionSpecManifest(codec, resolution, rotation).ToSpec());
    }

    /// <summary>
    /// **规格有值但认不出时，会话照样收尾，只在日志里说一声。**
    /// </summary>
    /// <remarks>
    /// ⚠️ 一条用例守两个相反的方向：
    /// <list type="number">
    /// <item>认不出规格**不许**把整个会话丢掉 —— 那是本仓唯一会丢证据的方向（I2/I9）：
    /// 分段文件还在盘上，只是元数据读不懂，照样得收尾。</item>
    /// <item>但这一条**必须留痕**，否则它的症状（收尾出来的索引三栏空）与
    /// 「当初根本没记过规格」**逐字一样**，而两者修法完全不同
    /// （见 <see cref="RecordingWorkspace.ListOrphansAsync"/> 里那一段注释）。</item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task 规格认不出时照样收尾_但日志里要说一声()
    {
        using var dir = new TempDir();
        var logger = new CapturingLogger();
        var workspace = new RecordingWorkspace(dir.Dir("work"), logger);
        var sessionDir = workspace.SessionDirectory("session-drift");
        Directory.CreateDirectory(sessionDir);
        await File.WriteAllTextAsync(System.IO.Path.Combine(sessionDir, "segment-000.mkv"), "bytes");

        // 枚举名后来改了名（或文件被手改过）—— Spec 在，但那个值认不出。
        await workspace.WriteManifestAsync(
            ManifestFor("session-drift", "segment-000.mkv") with
            {
                Spec = new SessionSpecManifest("H999", "Uhd4K", "None"),
            });

        var orphan = Assert.Single(await workspace.ListOrphansAsync());
        Assert.Null(orphan.Spec);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Contains("H999", entry.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// **孤儿收尾出来的索引条目带着这一场的录制规格。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// 2026-10-02 检索页实证：<c>…041052-…-000</c>（正常停录）有
    /// <c>Codec=H265 / Resolution=Uhd4K / Orientation=None</c>，而
    /// <c>…042113-…-000~003</c>（孤儿恢复）三项**全空**。
    /// </para>
    /// <para>
    /// ⚠️ 代价不止是「检索页少显示一栏」：<c>CleanupPolicy.EstimateBytes</c> 见空就按
    /// **默认档**（H.264 1080P）估，而「按空间清理」是 <c>freed += size</c> 攒到够为止。
    /// 同一条缺陷的另一半：主窗「152.1 MB」（实测字节）vs 数据窗「约 355.9 MB」（估算），
    /// 差 <b>2.34 倍</b> —— 而那两栏是同一个用户在同一屏上看的。
    /// </para>
    /// <para>
    /// ⚠️ 这条必须走完「写 manifest → 重启发现 → 收尾 → 写索引」**整条路**：
    /// 只测 <see cref="SessionSpecManifest.ToSpec"/> 的话，
    /// 「规格没被写进 manifest」或「没被喂给收尾器」这两处漏都拦不住
    /// （它们正是 2026-10-02 之前真实存在的那两处漏）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 孤儿收尾出来的索引条目带着录制规格()
    {
        using var dir = new TempDir();
        var spec = new RecordingSpec(VideoCodec.H264, VideoResolution.P720, CameraRotation.Left90);
        var workspace = await CreateKilledSessionAsync(dir, spec: spec);
        var index = new JsonLinesRecordingIndex(dir.File("index.jsonl"));
        var recovery = BuildRecovery(dir, workspace, new SucceedingRunner(), index);

        var outcomes = await recovery.RecoverAsync();

        Assert.True(Assert.Single(outcomes).Succeeded);

        var entry = Assert.Single(await index.LoadAllAsync());
        Assert.Equal("H264", entry.Codec);
        Assert.Equal("P720", entry.Resolution);
        Assert.Equal("Left90", entry.Orientation);
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
