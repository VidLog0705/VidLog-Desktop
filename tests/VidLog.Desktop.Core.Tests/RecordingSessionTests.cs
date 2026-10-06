using System.Text.Json;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 采集会话的编排：开录 → 分段滚动 → 收尾 → 入库。
/// </summary>
/// <remarks>
/// 规格 §3.1.1（连续分段录制、重启后能收尾孤儿）与 §4.1（收尾只有一条路径）。
/// <para>
/// 这里的替身是**记录型**的：价值不在「假采集能不能跑」，而在
/// 「编排器调它时给的参数对不对、按什么顺序、落了几次盘」。
/// 真采集那一半由 <see cref="FfmpegCameraCaptureIntegrationTests"/> 与真机回归负责。
/// </para>
/// </remarks>
public class RecordingSessionTests
{
    // ─────────────────────────────────────────────
    // 开录：manifest 必须立刻落盘
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 开录时立刻写第一版manifest_否则进程被杀后没有孤儿可恢复()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        await using var session = Build(dir, capture);

        await session.StartAsync(WaybillNumber.Parse("SF1234567890"), "libx264");

        // 这条是孤儿的**前提**：孤儿判据是「有 session.json 但没有 finalized.json」。
        // 不写这一版，进程被杀后 ListOrphansAsync 根本看不见这个会话。
        var manifestPath = Path.Combine(dir.WorkspaceRoot, session.SessionId, "session.json");
        Assert.True(File.Exists(manifestPath), "开录必须立刻写下 session.json");
    }

    /// <summary>
    /// **开录写下的那一版 manifest 里，带着这一场用的录制规格。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 2026-10-02 检索页实证：正常停录的
    /// <c>…041052-…-000</c> 有 <c>Codec=H265 / Resolution=Uhd4K / Orientation=None</c>，
    /// 而同日孤儿恢复出来的 <c>…042113-…-000~003</c> 三项**全空**。根因不在收尾器 ——
    /// <b>规格从来没被写到盘上</b>，进程被杀后孤儿恢复手里那份只能不传。
    /// </para>
    /// <para>
    /// ⚠️ 这条是**唯一**一条经过 <see cref="RecordingSession.StartAsync"/> 的规格测试：
    /// <c>OrphanRecoveryTests</c> 那几用的是测试自己造的 manifest，
    /// <c>SessionSpecManifest</c> 那两条只测自己的编解码 ——
    /// 谁把这一行改回 <c>null</c>，那几条纹丝不动，**只有这条会红**。
    /// </para>
    /// <para>
    /// ⚠️ 与 <see cref="RecordingSessionOptions.WithSchedule"/> 的注释是同一件事的两面：
    /// 那里写着「用户一改任何设置，已探好的录制规格就被抹成 null」——
    /// 现在连「抹成 null 之后会怎样」也有据可查了：索引三栏空，
    /// 而按空间清理的估算会被系统性放大（实测 2.34 倍）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 开录写下的manifest里带着这一场的录制规格()
    {
        using var dir = new TempDir();
        var spec = new RecordingSpec(VideoCodec.H264, VideoResolution.P720, CameraRotation.Left90);
        await using var session = Build(dir, new FakeCapture(), options: DefaultOptions.With(spec));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");

        var manifestPath = Path.Combine(dir.WorkspaceRoot, session.SessionId, "session.json");
        var manifest = JsonSerializer.Deserialize<SessionManifest>(
            await File.ReadAllTextAsync(manifestPath));

        Assert.Equal(SessionSpecManifest.From(spec), manifest!.Spec);
    }

    [Fact]
    public async Task 开录后采集进程收到的是设备名与目标路径()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        await using var session = Build(dir, capture);

        await session.StartAsync(WaybillNumber.Parse("SF1"), "h264_nvenc");

        var start = Assert.Single(capture.Starts);
        Assert.Equal("Lenovo EasyCamera", start.Source.Address);
        Assert.Equal("h264_nvenc", start.Encoder);

        // ⚠️ 给的是**模式**，不是文件名（T17）：分段由这一个进程自己按时长滚，
        // 段号由 ffmpeg 填 `%03d`。写成 `segment-000.mkv` 的话那个进程只落一片。
        Assert.EndsWith("segment-%03d.mkv", start.OutputPath);

        // 单段秒数 = 段时长（本文件的 `DefaultOptions` 是 1 小时），起始号 0（没采纳预录段）。
        Assert.Equal(3600, start.SegmentSeconds);
        Assert.Equal(0, start.SegmentStartNumber);
    }

    // ─────────────────────────────────────────────
    // 音轨（规格 §3.1.8）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 麦克风从会话选项原样交给采集进程()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        await using var session = Build(
            dir, capture,
            options: RecordingSessionOptions.Default.WithMicrophone("麦克风 (USB Audio Device)"));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");

        Assert.Equal("麦克风 (USB Audio Device)", Assert.Single(capture.Starts).Microphone);
    }

    [Fact]
    public async Task 没配麦克风时传的是null而不是空串()
    {
        // ⚠️ 空串会在 ffmpeg 那边变成 `-i audio=`，那是**一定失败**的一路 ——
        // 而失败的代价是每段开头都白等一次「起没起来」的判定。
        using var dir = new TempDir();
        var capture = new FakeCapture();
        await using var session = Build(dir, capture);

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");

        Assert.Null(Assert.Single(capture.Starts).Microphone);
    }

    [Fact]
    public async Task 段的问题必须同时走日志_不能只到界面()
    {
        // ⚠️ `AGENTS.md` §6：「**异常必须留痕**」。2026-09-29 查出来：
        // 这一类问题（音轨没接上、水印写不出来）在协调器那边**一次都没进过日志** ——
        // `LastProblem` 只到界面，而查日志的时候什么都看不到。
        using var dir = new TempDir();
        var capture = new FakeCapture { StartupWarning = "麦克风没能接上，这一段没有音轨。" };

        var reported = new List<string>();

        await using var session = Build(
            dir, capture,
            options: RecordingSessionOptions.Default.WithMicrophone("话筒"),
            problemReported: reported.Add);

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.ReleaseCaptureAsync();

        // 界面那一份（`LastProblem`）与日志那一份都要有。
        Assert.Equal("麦克风没能接上，这一段没有音轨。", session.LastProblem);
        Assert.Equal(["麦克风没能接上，这一段没有音轨。"], reported);
    }

    // ⚠️ 这里原来有一条「同一个问题重复出现时只回调一次」：它的判据是「真的滚出两段」，
    // 因为**每段开头都会重开一次采集进程、把那句话重设一遍**。
    // T17 之后一场只起一个进程 ⇒ 那句话一场只可能设一次，判据不再可达 ——
    // 用例删掉，`LastProblem` 里那三行去重留着（它同时在防将来任何一条重复触发的路）。

    [Fact]
    public async Task 音频降级的那句话必须让用户看见()
    {
        // 规格 §3.1.8：麦克风接不上 ⇒ 照常录视频、只是这一段没有音轨。
        // 但「照常录」不等于「当没事发生」—— I3 不允许静默降级，
        // 否则用户是**事后听回放**才发现整批货都没声音的。
        using var dir = new TempDir();
        var capture = new FakeCapture { StartupWarning = "麦克风没能接上，这一段没有音轨。" };

        await using var session = Build(
            dir, capture,
            options: RecordingSessionOptions.Default.WithMicrophone("话筒"));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.ReleaseCaptureAsync();

        Assert.Equal("麦克风没能接上，这一段没有音轨。", session.LastProblem);
    }

    [Fact]
    public async Task 进程真的崩了时_崩溃那句盖掉音频降级那句()
    {
        // 两句都成立（比如摄像头坏了 ⇒ 降级重开也没起来）时，用户要看的是
        // **真正的原因**。顺序反过来的话，界面会把一次设备故障说成「麦克风没接上」。
        using var dir = new TempDir();
        var capture = new FakeCapture
        {
            StartupWarning = "麦克风没能接上，这一段没有音轨。",
            ExitCode = 1,
        };

        await using var session = Build(
            dir, capture,
            options: RecordingSessionOptions.Default.WithMicrophone("话筒"));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.ReleaseCaptureAsync();

        Assert.NotNull(session.LastProblem);
        Assert.DoesNotContain("麦克风", session.LastProblem);
    }

    // ─────────────────────────────────────────────
    // 设置 → 会话参数的映射（规格 §3.3.4）
    // ─────────────────────────────────────────────

    [Fact]
    public void 设置映射到会话参数()
    {
        var options = RecordingSessionOptions.From(
            segmentMinutes: 5, durationFallback: DurationFallbackOption.Six);

        Assert.Equal(TimeSpan.FromMinutes(5), options.SegmentDuration);
        Assert.Equal(TimeSpan.FromMinutes(6), options.MaxDuration);
    }

    [Fact]
    public void 时长兜底关掉时永不按时长自动停()
    {
        var options = RecordingSessionOptions.From(
            segmentMinutes: 1, durationFallback: DurationFallbackOption.Off);

        // 「关闭」的语义是**永远比不到**，而不是退回某个默认值 ——
        // 退回默认的话，用户关掉它反而会被一个他没选的时长停掉。
        Assert.Equal(RecordingSessionOptions.NoFallback, options.MaxDuration);
        Assert.True(TimeSpan.FromDays(365) < options.MaxDuration);
    }

    [Fact]
    public void 手改坏的分段时长被夹回合法区间而不是让录制起不来()
    {
        // 设置文件是可以被手改的。一个改坏了的配置不该让用户**录不了像**（I4 的同一条精神）。
        Assert.Equal(TimeSpan.FromMinutes(1), RecordingSessionOptions.From(0, DurationFallbackOption.Off).SegmentDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), RecordingSessionOptions.From(999, DurationFallbackOption.Off).SegmentDuration);
    }

    // ─────────────────────────────────────────────
    // 分段滚动 —— 规格 §3.1.1「不因切分而中断用户体验」
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 分段交给ffmpeg自己滚_收尾时按盘上那几片数出来()
    {
        using var dir = new TempDir();
        // 一个进程跑到底、盘上落 3 片（T17：片由 ffmpeg 按时长滚，会话只看它落了什么）。
        var capture = new FakeCapture { SegmentsOnStop = 3 };
        var clock = new FakeClock();

        // 段 2 分钟、循环每次推进 1 分钟（见 AdvancingDelay）：
        // 第 5 分钟撞上时长兜底 ⇒ **先问**（规格 §3.3.4，2026-09-27 起），
        // 没人理 ⇒ 再过宽限期（默认 1 分钟 = 1 圈）才自动收尾 —— 所以第 6 圈停下。
        await using var session = Build(dir, capture, clock: clock, options: new RecordingSessionOptions(
            SegmentDuration: TimeSpan.FromMinutes(2),
            MaxDuration: TimeSpan.FromMinutes(5),
            StopGracePeriod: TimeSpan.FromSeconds(1),
            PollInterval: TimeSpan.FromMinutes(1)));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.RunAsync("libx264");

        // 撞上限 → **问** → 没人理 → 过宽限期自动收尾，循环必然退出
        // （所以这条测试不会空转）。
        Assert.Equal(StopReason.DurationFallback, session.StoppedBecause);

        // ★ T17 最要紧的一条：**全程只起一个采集进程**。
        // 原来是每段一个进程（关相机 → 开相机，实测 1~1.5 秒）⇒ 每个段边界
        // 有 1~2 秒真的没画面。段数一多这条不成立，空洞就回来了。
        var start = Assert.Single(capture.Starts);
        Assert.EndsWith("segment-%03d.mkv", start.OutputPath);
        Assert.Equal(120, start.SegmentSeconds);

        // 序号从 0 起、连续 —— 收尾器按 Sequence 排序拼时间轴，重号会让跨分段定位错位。
        var segments = session.Outcome!.Segments;
        Assert.Equal([0, 1, 2], segments.Select(s => s.Source.Sequence));

        // 时刻是**算**出来的（可信时钟 + 段号 × 单段时长），不是读文件时间：
        // 中间几片是名义时长，最后那一片到「停录那一刻」为止（这里恰好也满了 2 分钟）。
        var durations = segments
            .Select(s => s.Source.EndedAt - s.Source.StartedAt)
            .ToArray();
        Assert.Equal(
            [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2)],
            durations);

        // 最后那一片的终点 = 停录那一刻（第 6 分钟），不是「名义时长」硬加出来的。
        Assert.Equal(
            TimeSpan.FromMinutes(6), segments[2].Source.EndedAt - segments[0].Source.StartedAt);
    }

    [Fact]
    public async Task 收尾时把盘上每一片都补进manifest_下次启动只认它()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture { SegmentsOnStop = 3 };

        // ⚠️ 归档层发不上去 ⇒ 收尾之后工作目录**留着**（T21 只在发布成功时丢），
        // 这一条才有东西可读。用「进程被杀」那条路读不到 —— 那时还没收尾。
        await using var session = Build(
            dir, capture, relay: new ArchiveRelay(
                new FailingPublisher(), "NAS",
                new PublishedStore(dir.File("published.jsonl")), new ArchiveFailureLog(dir.File("archive-failures.jsonl"))));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        var outcome = await session.StopAsync(StopReason.Manual);
        Assert.True(outcome.Succeeded, outcome.FailureReason);

        // ⚠️ **录制中不再逐段更新 manifest**（T17 的取舍）：段是 ffmpeg 自己滚的，
        // 会话没有「段封闭」那一刻可挂钩。所以恢复链路的重心移到两条路上 ——
        // ① 收尾这一刻把**盘上数到的每一片**补进 manifest（下面这条）；
        // ② 进程被杀时走 T20 那条「扫目录捞没登记的分段」的路
        //    （`OrphanRecoveryTests` 里那批用例钉着它）。
        // 少了 ① 的话，收尾走到一半被杀 = 已经落盘的那几片没人认领。
        var json = await File.ReadAllTextAsync(
            Path.Combine(dir.WorkspaceRoot, session.SessionId, "session.json"));

        foreach (var name in new[] { "segment-000.mkv", "segment-001.mkv", "segment-002.mkv" })
        {
            Assert.Contains(name, json, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 盘上多出一片<b>对不上</b>的分段 ⇒ 按零时长登记，而且**用户看得见**。
    /// </summary>
    /// <remarks>
    /// T17 起分段是**从盘上数**的（见 <c>CapturedSegments</c>），而目录里可能出现
    /// 不属于这一场的片（同一会话目录里从前一次尝试留下的）。按名义时长算，
    /// 那种片的起点会**晚于**停录时刻 —— 记下它就是往索引里塞一条**时间倒流**的证据，
    /// 跨段定位会彻底错位，比丢掉它更坏。
    /// </remarks>
    [Fact]
    public async Task 盘上那一片对不上时间轴时_按零时长登记而且说出来()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture { SegmentsOnStop = 2 };
        var problems = new List<string>();

        await using var session = Build(
            dir, capture,
            options: new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromMinutes(1),
                MaxDuration: TimeSpan.FromMinutes(1),
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1)),
            problemReported: problems.Add);

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");

        // 塞一片这一场**录不出来**的：一场才跑了几毫秒，而 7 号的起点按名义时长
        // 落在 7 分钟之后 ⇒ 起点比停录时刻还晚。
        await File.WriteAllTextAsync(
            Path.Combine(dir.WorkspaceRoot, session.SessionId, "segment-007.mkv"), "stray");

        var outcome = await session.StopAsync(StopReason.Manual);

        Assert.True(outcome.Succeeded, outcome.FailureReason);

        var stray = outcome.Segments.Single(s => s.Source.Sequence == 7).Source;
        Assert.Equal(stray.StartedAt, stray.EndedAt);   // 零时长，不是负的

        // I3：盘上出现了对不上的东西，用户有权知道 —— 不许静默。
        Assert.Contains(problems, p => p.Contains("对不上时间轴", StringComparison.Ordinal));
    }

    // ─────────────────────────────────────────────
    // 采纳预录缓冲（规格 §3.1.3）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 采纳的预录段占掉000_正式首段从001起_起录时刻与已录时长都从缓冲起点算()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();

        // 真实路径上这是工作区 `_prerecord` 下裁好的那一份（见 `PrerecordController`）。
        var buffered = dir.File("adopted-source.mkv");
        await File.WriteAllTextAsync(buffered, "buffered-bytes");

        // 缓冲起点比现在早 5 秒 —— 需求方 2026-10-02 裁定「从缓冲起点起算」。
        var started = DateTimeOffset.UtcNow.AddSeconds(-5);
        var leading = new AdoptedClip(buffered, started, TimeSpan.FromSeconds(5));

        await using var session = Build(dir, capture, clock: clock);

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264", default, leading);

        var sessionDir = Path.Combine(dir.WorkspaceRoot, session.SessionId);

        // ① 搬进会话目录、用清单里的名字。不搬的话孤儿恢复的 `Where(File.Exists)`
        //    会**静默**把它过滤掉 —— 等于丢证据。
        Assert.False(File.Exists(buffered), "采纳段必须从临时位置搬走");
        Assert.Equal(
            "buffered-bytes",
            await File.ReadAllTextAsync(Path.Combine(sessionDir, "segment-000.mkv")));

        // ② 正式首段从 001 起 —— 撞号会让收尾拼时间轴时错位。
        //    ⚠️ 现在它表现为交给 ffmpeg 的**起始号**（T17）：输出是模式、段号由那边填，
        //    所以判据从「文件名是 001」变成「起始号是 1」。
        Assert.Equal(1, Assert.Single(capture.Starts).SegmentStartNumber);

        // ③ 第一版 manifest 里就得有它（进程被杀时恢复链路只认 manifest）。
        var json = await File.ReadAllTextAsync(Path.Combine(sessionDir, "session.json"));
        Assert.Contains("segment-000.mkv", json, StringComparison.Ordinal);

        // ④ 起录时刻是**缓冲起点**，不是扫码那一刻 —— 产物最前面那几秒
        //    真的是从那个时刻开始录的。
        // ⚠️ 比到秒为止、不比整个 `"O"`：JSON 编码器会把时区里的 `+`
        // 转义成 `+`，逐字比会**假红**（而它守的那件事其实是对的）。
        Assert.Contains(started.ToString("yyyy-MM-ddTHH:mm:ss"), json, StringComparison.Ordinal);

        // ⑤ 已录时长从缓冲起点起算（打点偏移、闲置提醒、时长兜底全部跟着它走）。
        Assert.True(
            session.Elapsed == TimeSpan.FromSeconds(5),
            $"已录时长应当从缓冲起点起算（5 秒），实际 {session.Elapsed}");
    }

    [Fact]
    public async Task 采纳段搬不动时_开录照常_而且时钟一秒都不往前挪()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();
        var problems = new List<string>();

        // 源文件不在（磁盘满、临时目录被清、路径写错）。
        var missing = dir.File("没有这一份.mkv");
        var leading = new AdoptedClip(missing, DateTimeOffset.UtcNow.AddSeconds(-5), TimeSpan.FromSeconds(5));

        await using var session = Build(dir, capture, clock: clock, problemReported: problems.Add);

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264", default, leading);

        // ⚠️ **不挪时钟、不占序号**：没有画面却声称「从 5 秒前开始录」是一条
        // 看起来完全正常的时间轴错误 —— 打点会偏、时长会多算，而且查不出来。
        Assert.Equal(TimeSpan.Zero, session.Elapsed);
        Assert.Equal(0, session.ClosedSegmentCount);
        Assert.Equal(0, Assert.Single(capture.Starts).SegmentStartNumber);

        // I3：用户要看得见（那几秒没了是事实，不能静默）。
        Assert.Contains(problems, p => p.Contains("预录", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 编排循环异常退出时_会话仍会被收尾而不是卡在收尾中()    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();
        var rounds = 0;

        // 让循环跑过两圈之后抛一个非取消异常 —— 模拟编排途中出岔子。
        // ⚠️ 判据不能用「起过第 2 次采集」：T17 之后一场只起一个进程，那个条件**永远不成立**，
        // 于是这个假 delay 从不抛 —— 而异常路径（循环里出岔子照样要收尾）就没人测了。
        Func<TimeSpan, CancellationToken, Task> failOnThirdRound = async (interval, _) =>
        {
            clock.Advance(interval);
            if (++rounds >= 3)
            {
                throw new InvalidOperationException("模拟编排途中的意外");
            }

            await Task.Yield();
        };

        await using var session = new RecordingSession(
            new RecordingWorkspace(dir.WorkspaceRoot), capture,
            BuildFinalizer(dir, new SucceedingRunner(), new RecordingIndexSpy()),
            new DiskSpaceGuard(new PlentyOfSpaceProbe()),
            CameraSource.Local("Lenovo EasyCamera"), "device-1",
            new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromMinutes(1),
                MaxDuration: TimeSpan.FromHours(1),
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1)),
            clock.Read, failOnThirdRound);

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.RunAsync("libx264");

        // 关键：不能卡在「收尾中」。卡住的话界面会永远显示正在收尾，
        // 等 Completion 的人（界面、测试）会一起挂死 —— 这个坑真踩过。
        Assert.NotEqual(RecordingSessionState.Finalizing, session.State);
        Assert.Equal(RecordingSessionState.Indexed, session.State);

        // 收尾走通了（状态是 Indexed）⇒ T21 之后工作目录已经被丢掉。
        // ⚠️ 这条**同时**守着「唯一那条收尾路径走完了」：丢掉目录只发生在
        // `MarkFinalizedAsync`（写了 finalized.json）**之后**那一步。
        Assert.False(Directory.Exists(Path.Combine(dir.WorkspaceRoot, session.SessionId)));
    }

    // ─────────────────────────────────────────────
    // 收尾 —— 不变量 I9：只有一条路径
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 停录走的是既有的SessionFinalizer_不另起一条收尾路径()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();
        var index = new RecordingIndexSpy();
        var runner = new SucceedingRunner();

        await using var session = Build(dir, capture, clock: clock, runner: runner, index: index);

        await session.StartAsync(WaybillNumber.Parse("SF1234567890"), "libx264");
        clock.Advance(TimeSpan.FromSeconds(5));
        var outcome = await session.StopAsync(StopReason.Manual);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal(RecordingSessionState.Indexed, session.State);
        Assert.Equal(StopReason.Manual, session.StoppedBecause);

        // 收尾成功 → 必须写 finalized.json（否则下次启动会把它当孤儿重收一遍），
        // 然后 T21 把整个工作目录丢掉 —— 成品已经在归档层上了，源 MKV 只剩占地方。
        Assert.False(Directory.Exists(Path.Combine(dir.WorkspaceRoot, session.SessionId)));
    }

    [Fact]
    public async Task 归档层那一份没发上去时_工作目录留着()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();

        // 归档层是 NAS，而且发不上去（发布失败**不影响**收尾成败）。
        await using var session = Build(
            dir, capture, clock: clock, relay: new ArchiveRelay(
                new FailingPublisher(), "NAS",
                new PublishedStore(dir.File("published.jsonl")), new ArchiveFailureLog(dir.File("archive-failures.jsonl"))));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        clock.Advance(TimeSpan.FromSeconds(5));
        var outcome = await session.StopAsync(StopReason.Manual);

        // 本机这一份是好的：索引里有、能播能检索。
        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.False(outcome.ArchiveComplete);

        // ★ T21：归档层上那份没上去 ⇒ **源 MKV 留着**。
        // I2 的方向是「宁可多占地方，不可少一份证据」—— 这条判据错了的话，
        // 删掉的可能是归档层那一份的替身。
        Assert.True(Directory.Exists(Path.Combine(dir.WorkspaceRoot, session.SessionId)));
    }

    [Fact]
    public async Task 收尾失败时不写finalized_留着让下次启动当孤儿重试()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var runner = new FailingRunner();

        await using var session = Build(dir, capture, runner: runner, index: new RecordingIndexSpy());

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        var outcome = await session.StopAsync(StopReason.Manual);

        Assert.False(outcome.Succeeded);
        Assert.Equal(RecordingSessionState.FinalizeFailed, session.State);

        // 关键：**不能**写 finalized.json —— 写了就等于把这批录像判了死刑（I2）。
        Assert.False(File.Exists(Path.Combine(dir.WorkspaceRoot, session.SessionId, "finalized.json")));

        // 而且它得能被当成孤儿看见，下次启动才有重试的机会。
        var orphans = await new RecordingWorkspace(dir.WorkspaceRoot).ListOrphansAsync();
        Assert.Single(orphans);
    }

    [Fact]
    public async Task 停录原因原样传给收尾器_所有原因走同一条路径()
    {
        var reasons = Enum.GetValues<StopReason>();

        foreach (var reason in reasons)
        {
            using var dir = new TempDir();
            var capture = new FakeCapture();
            var index = new RecordingIndexSpy();
            var runner = new SucceedingRunner();

            await using var session = Build(dir, capture, runner: runner, index: index);
            await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
            var outcome = await session.StopAsync(reason);

            Assert.True(outcome.Succeeded, $"{reason}: {outcome.FailureReason}");
            Assert.Equal(reason, outcome.Reason);
            Assert.Equal(reason, session.StoppedBecause);
        }
    }

    [Fact]
    public async Task 一段都没录到时收尾失败并且原因对用户可见()
    {
        using var dir = new TempDir();
        // 采集进程一起来就退出，什么也没写出 —— 模拟摄像头打不开。
        var capture = new FakeCapture { ProcessProducesFile = false };

        await using var session = Build(dir, capture, index: new RecordingIndexSpy());

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        var outcome = await session.StopAsync(StopReason.Manual);

        Assert.False(outcome.Succeeded);
        // I3：不允许静默失败 —— 必须有一句人能读懂的话。
        Assert.False(string.IsNullOrWhiteSpace(session.LastProblem));
    }

    // ─────────────────────────────────────────────
    // 时间：不变量 I11 —— 单调时钟，墙钟改了不算数
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 分段时长按单调时钟算_不随墙钟跳变()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();

        await using var session = Build(dir, capture, clock: clock, index: new RecordingIndexSpy());

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");

        // 用户在这时候把系统时间往前调了一天。单调时钟不为所动。
        clock.Advance(TimeSpan.FromSeconds(7));
        var outcome = await session.StopAsync(StopReason.Manual);

        // 段的时长应当正好是单调走的 7 秒，而不是墙钟跳变后的一天。
        var finalized = Assert.Single(outcome.Segments);
        var duration = finalized.Source.EndedAt - finalized.Source.StartedAt;
        Assert.Equal(TimeSpan.FromSeconds(7), duration);
    }

    // ─────────────────────────────────────────────
    // 兜底
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 到时长上限自动收尾_原因是时长兜底()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();

        // 段 1 小时、上限 1 小时、每次推进 1 分钟：
        // 段永远滚不到，循环到第 60 次撞上时长上限自己停下。
        await using var session = Build(dir, capture, clock: clock, index: new RecordingIndexSpy(),
            options: new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromHours(1),
                MaxDuration: TimeSpan.FromHours(1),
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1)));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.RunAsync("libx264");

        // 循环自动收尾时，调用方没有 StopAsync 的 Task 可以等 —— 等这个信号。
        await session.Completion;

        Assert.Equal(StopReason.DurationFallback, session.StoppedBecause);
        Assert.Equal(RecordingSessionState.Indexed, session.State);
    }

    [Fact]
    public async Task 磁盘将满时主动收尾_而不是等崩溃()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();

        await using var session = new RecordingSession(
            new RecordingWorkspace(dir.WorkspaceRoot), capture,
            BuildFinalizer(dir, new SucceedingRunner(), new RecordingIndexSpy()),
            new DiskSpaceGuard(new AlwaysFullProbe()),
            CameraSource.Local("Lenovo EasyCamera"), "device-1",
            // 磁盘永远告急 ⇒ 第一轮轮询就该收尾，与时长无关。
            DefaultOptions, clock.Read, AdvancingDelay(clock));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.RunAsync("libx264");
        await session.Completion;

        Assert.Equal(StopReason.StorageLow, session.StoppedBecause);
        Assert.Equal(RecordingSessionState.Indexed, session.State);
    }

    // ─────────────────────────────────────────────
    // 测试脚手架
    // ─────────────────────────────────────────────

    private static readonly string Ffmpeg = FfmpegLocator.TryFind()!;

    // ─────────────────────────────────────────────
    // 时长兜底：到点**先问**（规格 §3.3.4）
    // ─────────────────────────────────────────────
    //
    // ⚠️ 这一组是 2026-09-27 补的，因为原来电脑端把这条做成了**到点直接停** ——
    // 而「它会把正常录制打断」正是 2026-09-21 那次需求变更要修掉的东西。
    // 口径与手机端 `stop_controller.dart` 的 `_evaluateTimers` 逐条同形。

    /// <summary>首问时刻到了、<b>但没停</b> —— 这是这一组最要紧的一条。</summary>
    [Fact]
    public async Task 时长兜底到点是先问_录制不中断()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var steps = new StepDelay(clock);
        var asked = 0;

        await using var session = Build(dir, new FakeCapture(), clock: clock, steps: steps,
            options: new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromHours(1),
                MaxDuration: TimeSpan.FromMinutes(4),      // 首问时刻
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1),     // 每圈推进 1 分钟
                // ⚠️ 宽限期必须**远大于**一圈：循环跑一圈是**瞬间**的，宽限期若与
                // PollInterval 同量级，放行的那几圈里它就自己跨过宽限期收掉了。
                PromptGrace: TimeSpan.FromHours(10)),
            onPrompt: () => Interlocked.Increment(ref asked));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");

        // ⚠️ 不能 await 它：问到用户之后循环**故意不退出**，await 会把测试挂死。
        var loop = session.RunAsync("libx264");

        steps.Release(4);   // 4 圈 = 4 分钟 = 首问时刻
        await WaitUntilAsync(() => Volatile.Read(ref asked) > 0, "第一次询问");

        Assert.Equal(1, Volatile.Read(ref asked));
        Assert.True(session.IsAwaitingDurationAnswer, "到点应当**问**用户（语音 + 界面两键靠它）");

        // ★ 最要紧的一句：问了不等于停。规格原话「提示与按钮必须在**录制不被中断**的前提下出现」。
        //
        // ⚠️ **判据是「时钟还在被推进」，不是 `Assert.False(loop.IsCompleted)`**。
        // 后者**测不住**这件事：到点直接停的话，循环 `break` 之后还要跑收尾
        // （remux / 校验 / 写索引，真文件 IO），而这一刻 `loop` 很可能**还没完成** ——
        // 于是那条断言照样绿。**这是实测出来的**：把旧行为（问完就停）塞回去，
        // 这条用例当时**没红**。
        // 等时钟再走一段就不一样了：停了就没人推进它，`WaitUntilAsync` 必然抛。
        // （循环卡在门外，所以这 6 圈要**放行**才走 —— 不止是「等」。）
        var before = clock.Read();
        steps.Release(6);
        await WaitUntilAsync(
            () => clock.Read() > before + TimeSpan.FromMinutes(5),
            "询问之后循环继续推进（停了就不会再推进）");

        Assert.Equal(1, Volatile.Read(ref asked));   // 宽限期内不该重问

        session.AnswerDurationPrompt(continueRecording: false);
        steps.Release(1);   // 下一圈才看得见【停止】
        await loop;
    }

    /// <summary>点【继续】⇒ 不停，隔「下一轮间隔」再问一次。</summary>
    [Fact]
    public async Task 时长兜底答继续_不停_隔一轮再问()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var steps = new StepDelay(clock);
        var asked = 0;

        await using var session = Build(dir, new FakeCapture(), clock: clock, steps: steps,
            options: new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromHours(1),
                MaxDuration: TimeSpan.FromMinutes(4),
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1),
                PromptRepeatEvery: TimeSpan.FromMinutes(2),   // 缩短，少跑几圈
                // 宽限期远大于一圈 —— 见「先问不中断」那条的说明。
                PromptGrace: TimeSpan.FromHours(10)),
            onPrompt: () => Interlocked.Increment(ref asked));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        var loop = session.RunAsync("libx264");

        steps.Release(4);   // 4 圈 = 4 分钟 = 首问时刻
        await WaitUntilAsync(() => Volatile.Read(ref asked) > 0, "第一次询问");

        session.AnswerDurationPrompt(continueRecording: true);

        // 循环卡在门外 ⇒ 这两句读的是**停住的**快照（用 AdvancingDelay 时它会抢跑，
        // 实测断言里打出来的时钟已经走到「第二次询问之后」了）。
        Assert.False(session.IsAwaitingDurationAnswer, "答过了就不该还在问");
        Assert.False(loop.IsCompleted, "点了【继续】之后录制必须继续");

        // 隔 PromptRepeatEvery（2 分钟 = 2 圈）再问 —— 规格「再过 5 分钟再次询问（循环）」。
        steps.Release(2);
        await WaitUntilAsync(() => Volatile.Read(ref asked) >= 2, "第二次询问");
        Assert.Equal(2, Volatile.Read(ref asked));

        session.AnswerDurationPrompt(continueRecording: false);
        steps.Release(1);   // 下一圈才看得见【停止】
        await loop;
    }

    /// <summary>点【停止】⇒ 立即收尾，理由仍是时长兜底。</summary>
    [Fact]
    public async Task 时长兜底答停止_立即收尾()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var asked = 0;

        await using var session = Build(dir, new FakeCapture(), clock: clock,
            options: new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromHours(1),
                MaxDuration: TimeSpan.FromMinutes(4),
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1),
                PromptGrace: TimeSpan.FromHours(10)),    // 远大于一圈，见上
            onPrompt: () => Interlocked.Increment(ref asked));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        var loop = session.RunAsync("libx264");

        await WaitUntilAsync(() => Volatile.Read(ref asked) > 0, "第一次询问");

        session.AnswerDurationPrompt(continueRecording: false);
        await loop;   // 下一圈就收，所以这里等得到

        Assert.Equal(StopReason.DurationFallback, session.StoppedBecause);
    }

    /// <summary>
    /// 问了<b>没人理</b> ⇒ 过宽限期按兜底收尾（规格「1 分钟无操作 → 默认继续 → 自动停止」）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这条与「答【继续】」是<b>两个不同的结局</b>，规格明令区分：
    /// 「『无操作』与『主动继续』必须区分：前者代表用户不在场（兜底生效），
    /// 后者代表用户在场且明确要继续（不误停）」。
    /// </remarks>
    [Fact]
    public async Task 时长兜底问了没人理_过宽限期自动收尾()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var asked = 0;

        await using var session = Build(dir, new FakeCapture(), clock: clock,
            options: new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromHours(1),
                MaxDuration: TimeSpan.FromMinutes(4),
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1),
                PromptGrace: TimeSpan.FromMinutes(2)),    // 2 圈
            onPrompt: () => Interlocked.Increment(ref asked));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");

        // 这里可以 await：没人答 ⇒ 宽限期一到循环自己收掉。
        await session.RunAsync("libx264");

        Assert.Equal(1, Volatile.Read(ref asked));
        Assert.Equal(StopReason.DurationFallback, session.StoppedBecause);
    }

    /// <summary>档位设成「关闭」⇒ 从不问、也从不自动停。</summary>
    [Fact]
    public async Task 时长兜底关闭时_从不问也从不自动停()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var steps = new StepDelay(clock);
        var asked = 0;
        using var cts = new CancellationTokenSource();

        await using var session = Build(dir, new FakeCapture(), clock: clock, steps: steps,
            options: new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromHours(1),
                MaxDuration: RecordingSessionOptions.NoFallback,   // 关闭
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1)),
            onPrompt: () => Interlocked.Increment(ref asked));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        var loop = session.RunAsync("libx264", cts.Token);

        // 跑够久 —— 若它真会问/真会停，这几圈之内必然发生。
        steps.Release(10);
        await WaitUntilAsync(() => clock.Read() >= TimeSpan.FromMinutes(10), "循环跑过 10 圈");

        Assert.Equal(0, Volatile.Read(ref asked));
        Assert.False(loop.IsCompleted, "关闭档位下循环不该自己停");

        await cts.CancelAsync();
        await loop;
    }

    /// <summary>
    /// 没在问的时候答它 ⇒ <b>什么都不做</b>（晚到一步的按钮不许改掉新那一段的计时）。
    /// </summary>
    /// <remarks>
    /// 场景：用户正要按【继续】，同一刻扫了下一件 ⇒ 这一段作废、新段开始。
    /// 那时把新段的兜底计时重置掉，会让新段**被问得晚得多**（或永远不问）。
    /// </remarks>
    [Fact]
    public async Task 没在问的时候答它_什么都不做()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var steps = new StepDelay(clock);
        var asked = 0;
        using var cts = new CancellationTokenSource();

        await using var session = Build(dir, new FakeCapture(), clock: clock, steps: steps,
            options: new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromHours(1),
                MaxDuration: TimeSpan.FromMinutes(4),
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1),
                PromptGrace: TimeSpan.FromHours(10)),    // 远大于一圈，见上
            onPrompt: () => Interlocked.Increment(ref asked));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        var loop = session.RunAsync("libx264", cts.Token);

        // 一上来就答（此刻并没有在问）—— 循环还卡在门外，什么都没发生过。
        session.AnswerDurationPrompt(continueRecording: true);

        Assert.False(session.IsAwaitingDurationAnswer);

        // 首问照样按时来 —— 证明上面那一下没把计时改掉。
        steps.Release(4);   // 4 圈 = 4 分钟 = 首问时刻
        await WaitUntilAsync(() => Volatile.Read(ref asked) > 0, "第一次询问");
        Assert.Equal(1, Volatile.Read(ref asked));

        await cts.CancelAsync();
        await loop;
    }

    private static RecordingSession Build(
        TempDir dir,
        FakeCapture capture,
        FakeClock? clock = null,
        IProcessRunner? runner = null,
        RecordingIndexSpy? index = null,
        RecordingSessionOptions? options = null,
        Action? onPrompt = null,
        Action<string>? problemReported = null,
        ArchiveRelay? relay = null,
        StepDelay? steps = null)
    {
        // 方法组不能直接配合 ?. —— 显式判空，让类型明确是 Func<TimeSpan>?。
        Func<TimeSpan>? effectiveClock = clock is null ? null : clock.Read;

        Func<TimeSpan, CancellationToken, Task> delay =
            steps is null ? AdvancingDelay(clock) : steps.WaitAsync;

        return new RecordingSession(
            new RecordingWorkspace(dir.WorkspaceRoot),
            capture,
            BuildFinalizer(dir, runner ?? new SucceedingRunner(), index ?? new RecordingIndexSpy(), relay),
            new DiskSpaceGuard(new PlentyOfSpaceProbe()),
            CameraSource.Local("Lenovo EasyCamera"),
            "device-1",
            options ?? DefaultOptions,
            effectiveClock,
            delay,
            durationPrompted: onPrompt,
            problemReported: problemReported);
    }

    /// <summary>
    /// 等到某个条件成立，然后**立刻**把执行权还给测试。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用来抓「循环跑过某一圈之后」的中间状态 —— 而<b>不能</b>
    /// <c>await session.RunAsync(...)</c>：时长兜底问到用户之后循环
    /// **故意不退出**（那正是这条的行为），await 它会把测试挂死。
    /// </para>
    /// <para>
    /// ⚠️ <b>用 <c>Task.Yield</c> 而不是 <c>Task.Delay</c></b>：假时钟下循环跑一圈
    /// 只需一次 <c>await</c>，而 <c>Delay(5)</c> 会让循环在这 5 毫秒里**跑几千圈** ——
    /// 于是「询问之后、宽限到期之前」那个窗口**根本插不进去**：测试还没来得及答，
    /// 循环已经自己跨过宽限期收尾了（实测就是这么红的）。
    /// </para>
    /// <para>
    /// ⚠️ <b>Yield 也只是「够用」而不是「可控」</b>：循环跑在**别的线程**上，测试自己
    /// 不说话的那几行（两条同步语句之间）它照样往前跑 —— 实测断言里打出来的时钟已经
    /// 是「第二次询问之后」。所以凡是**要看某一刻快照**的用例，必须改用
    /// <see cref="StepDelay"/> 把循环卡在门外，不能指望 Yield 让出一次就一定轮到测试。
    /// 这个 helper 仍然留着：它只等条件成立，不保证条件成立那一刻循环停了。
    /// </para>
    /// <para>
    /// 上限用**圈数**而不是墙钟时间 —— 判据是「循环有没有往前跑」，与真实耗时无关。
    /// </para>
    /// </remarks>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100_000; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Yield();
        }

        throw new TimeoutException($"等不到：{what}");
    }

    /// <summary>
    /// 编排循环的等待：**不真等，但要把假时钟往前推，并且让出一次执行权**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只返回 <see cref="Task.CompletedTask"/> 是不够的 —— 时钟不动的话循环永远停在
    /// 同一时刻，变成空转（第一版就是这么把测试挂住的）。推进时钟才等价于「真的等了一会」。
    /// </para>
    /// <para>
    /// ⚠️ <b>2026-09-27 补上 <see cref="CancellationToken.ThrowIfCancellationRequested"/>：
    /// 真的 <c>Task.Delay</c> 在取消时**会抛</b>，而这个假实现原来把令牌整个忽略掉 ——
    /// 于是「取消之后循环退出」这条路在测试里**永远走不到**，用取消收尾的用例会
    /// `await` 到天荒地老（实测：挂到 600 秒超时）。
    /// </para>
    /// <para>
    /// ⚠️ <b>2026-10-05 补上 <see cref="Task.Yield"/>：真的 <c>Task.Delay</c> 一定让出执行权</b>，
    /// 而这个假实现原来直接返回已完成的 Task ⇒ <c>await</c> 不让出 ⇒
    /// **整个编排循环在调用 <see cref="RecordingSession.RunAsync"/> 的那条线程上同步跑完**
    /// （<c>RunAsync</c> 要等循环退出才返回）。
    /// </para>
    /// <para>
    /// 这个缺陷**直到 T17 才露出来**：原来循环体里唯一让出过的 <c>await</c> 是滚段那一路的
    /// <c>WriteManifestAsync</c>（真文件 IO），所以每滚一段顺带让出一次，几条「循环还在跑」
    /// 的用例才碰巧成立。T17 把滚段分支删了（改由 ffmpeg 自己滚），循环体**再没有任何
    /// 让出点** ⇒ 「关闭档位」那条用例（循环没有退出条件）把测试挂死、另外几条读到的是
    /// 「循环已经跑完」的快照。
    /// </para>
    /// <para>
    /// 所以补在**假替身**上而不是往生产循环里塞一个 <c>Yield</c>：生产路径的
    /// <c>_delay</c> 是 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>，本来就让出。
    /// </para>
    /// </remarks>
    private static Func<TimeSpan, CancellationToken, Task> AdvancingDelay(FakeClock? clock) =>
        async (interval, token) =>
        {
            clock?.Advance(interval);
            token.ThrowIfCancellationRequested();
            await Task.Yield();
        };

    /// <summary>
    /// 一步一圈的假等待：<b>只有测试放行，循环才往下走</b>（每圈推 1 个 <c>PollInterval</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 给「要看某一刻快照」的用例用。假时钟下循环是**瞬间**的，而它跑在别的线程上 ——
    /// 用 <see cref="AdvancingDelay"/>（每圈只 Yield）时，测试自己那几行同步代码跑完之前
    /// 循环早已冲过去几十上百圈，读到的快照根本没有意义（实测：断言里打出来的时钟已经是
    /// 「第二次询问之后」，且每次跑的圈数都不一样）。
    /// </para>
    /// <para>
    /// 用法：<c>steps.Release(4); await WaitUntilAsync(条件, "…");</c> ——
    /// 放行 4 圈，等条件成立；循环用完这 4 圈就卡在门外，后面的断言都是**停住的**快照。
    /// 凡是 <c>await loop</c> 收尾之前，记得再放行至少 1 圈，否则循环永远出不来。
    /// </para>
    /// <para>
    /// 用 <see cref="SemaphoreSlim"/> 当门闸：<see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
    /// 在取消时会抛，与真的 <see cref="Task.Delay(TimeSpan, CancellationToken)"/> 同形，
    /// 「取消收尾」那几条用例照旧走得通。
    /// </para>
    /// </remarks>
    private sealed class StepDelay(FakeClock clock)
    {
        private readonly SemaphoreSlim _released = new(0);

        /// <summary>放行 <paramref name="rounds"/> 圈。</summary>
        public void Release(int rounds = 1) => _released.Release(rounds);

        /// <summary>与生产路径的 <c>Task.Delay</c> 同签名的假等待。</summary>
        public async Task WaitAsync(TimeSpan interval, CancellationToken token)
        {
            await _released.WaitAsync(token).ConfigureAwait(false);
            clock.Advance(interval);   // 放行了才算「等了这么久」
        }
    }

    private static RecordingSessionOptions DefaultOptions => new(
        SegmentDuration: TimeSpan.FromHours(1),
        MaxDuration: TimeSpan.FromHours(1),
        StopGracePeriod: TimeSpan.FromSeconds(1),
        PollInterval: TimeSpan.FromSeconds(1));

    /// <param name="relay">
    /// 归档层不是本机时才传。默认 <see langword="null"/> = 归档层就是本机
    /// （发布是空操作，<c>ArchiveComplete</c> 恒真）。
    /// </param>
    private static SessionFinalizer BuildFinalizer(
        TempDir dir, IProcessRunner runner, RecordingIndexSpy index, ArchiveRelay? relay = null) =>
        new(new RemuxPipeline(Ffmpeg, runner),
            new DecodeVerifier(Ffmpeg, runner),
            index,
            Path.Combine(dir.Path, "archive"),
            relay: relay);

    /// <summary>归档层永远发不上去的那种替身（NAS 断了、盘没挂上）。</summary>
    private sealed class FailingPublisher : IArchivePublisher
    {
        public Task<ArchivePublishResult> PublishAsync(
            RelativePath location, string localPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(ArchivePublishResult.Failed("网络不通"));
    }

    /// <summary>可控的单调时钟。</summary>
    private sealed class FakeClock
    {
        private TimeSpan _now;

        public TimeSpan Read() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>记录型采集替身：记下每次起采的参数，并真的造出文件。</summary>
    private sealed class FakeCapture : ICameraCapture
    {
        public List<Start> Starts { get; } = [];

        /// <summary>置 false 模拟「摄像头打不开」——进程起来了但没产物。</summary>
        public bool ProcessProducesFile { get; init; } = true;

        /// <summary>模拟「音频那一路没接上」的降级说明（规格 §3.1.8）。</summary>
        public string? StartupWarning { get; init; }

        /// <summary>停止时报的退出码（非 0 模拟采集中途出过事）。</summary>
        public int ExitCode { get; init; }

        /// <summary>滚段那一趟停下时，盘上落了**几片**（T17）。</summary>
        public int SegmentsOnStop { get; init; } = 1;

        /// <summary>一次起采。</summary>
        public sealed record Start(
            CameraSource Source,
            string OutputPath,
            string Encoder,
            string? Microphone,
            int? SegmentSeconds,
            int SegmentStartNumber);

        public Task<ICaptureProcess> StartAsync(
            CameraSource source, string outputPath, string encoder, string? microphone = null,
            CancellationToken cancellationToken = default,
            int? segmentSeconds = null, int segmentStartNumber = 0)
        {
            Starts.Add(new Start(
                source, outputPath, encoder, microphone, segmentSeconds, segmentStartNumber));

            return Task.FromResult<ICaptureProcess>(new FakeProcess(
                outputPath,
                ProcessProducesFile,
                StartupWarning,
                ExitCode,
                segmentSeconds is null ? 1 : SegmentsOnStop,
                segmentStartNumber));
        }
    }

    /// <summary>假的采集进程。停的时候按需留下产物。</summary>
    private sealed class FakeProcess(
        string outputPath,
        bool producesFile,
        string? startupWarning = null,
        int exitCode = 0,
        int files = 1,
        int startNumber = 0)
        : ICaptureProcess
    {
        public bool HasExited { get; private set; }

        public string? StartupWarning { get; } = startupWarning;

        public Task<int?> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (producesFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

                // 滚段那一趟（T17）`outputPath` 是**模式**，落下来的是一串文件；
                // 老走法下它就是那唯一的产物。
                if (outputPath.Contains("%03d", StringComparison.Ordinal))
                {
                    for (var i = 0; i < files; i++)
                    {
                        File.WriteAllText(
                            outputPath.Replace(
                                "%03d",
                                (startNumber + i).ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                                StringComparison.Ordinal),
                            "captured-bytes");
                    }
                }
                else
                {
                    File.WriteAllText(outputPath, "captured-bytes");
                }
            }

            HasExited = true;
            return Task.FromResult<int?>(exitCode);
        }
    }

    /// <summary>磁盘充裕。</summary>
    private sealed class PlentyOfSpaceProbe : IDiskSpaceProbe
    {
        public long GetFreeBytes(string path) => 100L * 1024 * 1024 * 1024;
    }

    /// <summary>磁盘告急。</summary>
    private sealed class AlwaysFullProbe : IDiskSpaceProbe
    {
        public long GetFreeBytes(string path) => 1;
    }

    /// <summary>成功路径的假 FFmpeg：会真的造出产物，让校验能过。</summary>
    private sealed class SucceedingRunner : IProcessRunner
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Invocations.Add(arguments.ToList());

            var args = arguments.ToList();
            var yIndex = args.IndexOf("-y");
            if (yIndex >= 0 && yIndex + 1 < args.Count)
            {
                var output = args[yIndex + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, "published-bytes");
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    /// <summary>总是失败的假 FFmpeg。</summary>
    private sealed class FailingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(1, string.Empty, "boom"));
    }

    /// <summary>索引替身。</summary>
    private sealed class RecordingIndexSpy : IRecordingIndex
    {
        public List<RecordingEntry> Entries { get; } = [];

        public Task AddAsync(RecordingEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RecordingEntry>> LoadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RecordingEntry>>(Entries);
    }

    /// <summary>每个测试类自带的临时目录（本仓惯例：不共用基类）。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public string WorkspaceRoot => System.IO.Path.Combine(Path, "work");

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-sess-" + Guid.NewGuid().ToString("N"));
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
}
