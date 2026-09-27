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

    [Fact]
    public async Task 开录后采集进程收到的是设备名与目标路径()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        await using var session = Build(dir, capture);

        await session.StartAsync(WaybillNumber.Parse("SF1"), "h264_nvenc");

        var start = Assert.Single(capture.Starts);
        Assert.Equal("Lenovo EasyCamera", start.Device);
        Assert.Equal("h264_nvenc", start.Encoder);
        Assert.EndsWith("segment-000.mkv", start.OutputPath);
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
    public async Task 到分段时长就滚下一段_序号递增且文件名不重()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();

        // 段 2 分钟、循环每次推进 1 分钟（见 AdvancingDelay）：
        // 恰好滚 2 次，第 5 分钟撞上时长兜底 ⇒ **先问**（规格 §3.3.4，2026-09-27 起），
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

        // 序号从 0 起、连续、不重 —— 收尾器按 Sequence 排序拼时间轴，
        // 重号会让跨分段定位错位。
        Assert.Equal(
            ["segment-000.mkv", "segment-001.mkv", "segment-002.mkv"],
            capture.Starts.Select(s => Path.GetFileName(s.OutputPath)));
    }

    [Fact]
    public async Task 每滚一段就更新一次manifest_进程被杀后已封闭的段仍可恢复()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();

        // 段 1 分钟、上限 2.5 分钟、每次推进 1 分钟：
        // 第 2 分钟滚出第 2 段；第 3 分钟撞上兜底 ⇒ **先问**（规格 §3.3.4），
        // 没人理 ⇒ 再过宽限期（默认 1 分钟，1 圈）于第 4 分钟收尾。
        await using var session = Build(dir, capture, clock: clock, options: new RecordingSessionOptions(
            SegmentDuration: TimeSpan.FromMinutes(1),
            MaxDuration: TimeSpan.FromMinutes(2.5),
            StopGracePeriod: TimeSpan.FromSeconds(1),
            PollInterval: TimeSpan.FromMinutes(1)));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.RunAsync("libx264");

        // 读盘上留下了什么 —— 这相当于「进程被杀」时孤儿恢复能看到的东西。
        // 收尾在 RunAsync 里已经跑过了，所以这里先看它在收尾**之前**写下的 manifest：
        // 用一个新的工作区读同一批文件即可（finalized.json 已写，孤儿查不出来，
        // 因此直接查 manifest 里的分段，那正是恢复链路依赖的输入）。
        var manifestPath = Path.Combine(dir.WorkspaceRoot, session.SessionId, "session.json");
        var json = await File.ReadAllTextAsync(manifestPath);

        Assert.Contains("segment-000.mkv", json, StringComparison.Ordinal);
        Assert.Contains("segment-001.mkv", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 编排循环异常退出时_会话仍会被收尾而不是卡在收尾中()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();
        var thrown = false;

        // 让循环在滚出第 2 段之后抛一个非取消异常 —— 模拟编排途中出岔子。
        Func<TimeSpan, CancellationToken, Task> rollThenFail = (interval, _) =>
        {
            clock.Advance(interval);
            if (capture.Starts.Count >= 2 && !thrown)
            {
                thrown = true;
                throw new InvalidOperationException("模拟编排途中的意外");
            }

            return Task.CompletedTask;
        };

        await using var session = new RecordingSession(
            new RecordingWorkspace(dir.WorkspaceRoot), capture,
            BuildFinalizer(dir, new SucceedingRunner(), new RecordingIndexSpy()),
            new DiskSpaceGuard(new PlentyOfSpaceProbe()),
            "Lenovo EasyCamera", "device-1",
            new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromMinutes(1),
                MaxDuration: TimeSpan.FromHours(1),
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1)),
            clock.Read, rollThenFail);

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.RunAsync("libx264");

        // 关键：不能卡在「收尾中」。卡住的话界面会永远显示正在收尾，
        // 等 Completion 的人（界面、测试）会一起挂死 —— 这个坑真踩过。
        Assert.NotEqual(RecordingSessionState.Finalizing, session.State);
        Assert.Equal(RecordingSessionState.Indexed, session.State);

        // finalized.json 写了，说明确实走完了唯一那条收尾路径。
        Assert.True(File.Exists(Path.Combine(dir.WorkspaceRoot, session.SessionId, "finalized.json")));
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

        // 收尾成功 → 必须写 finalized.json，否则下次启动会把它当孤儿重收一遍。
        Assert.True(File.Exists(Path.Combine(dir.WorkspaceRoot, session.SessionId, "finalized.json")));
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
            "Lenovo EasyCamera", "device-1",
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
        var asked = 0;

        await using var session = Build(dir, new FakeCapture(), clock: clock,
            options: new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromHours(1),
                MaxDuration: TimeSpan.FromMinutes(4),      // 首问时刻
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1),     // 每圈推进 1 分钟
                // ⚠️ 宽限期必须**远大于**一圈：假时钟下循环是**瞬间**跑完几十圈的，
                // 宽限期若与 PollInterval 同量级，循环会在测试来得及「答」之前
                // 就自己跨过宽限期收掉了（第一版就是这么红的）。
                PromptGrace: TimeSpan.FromHours(10)),
            onPrompt: () => Interlocked.Increment(ref asked));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");

        // ⚠️ 不能 await 它：问到用户之后循环**故意不退出**，await 会把测试挂死。
        var loop = session.RunAsync("libx264");

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
        var before = clock.Read();
        await WaitUntilAsync(
            () => clock.Read() > before + TimeSpan.FromMinutes(5),
            "询问之后循环继续推进（停了就不会再推进）");

        Assert.Equal(1, Volatile.Read(ref asked));   // 宽限期内不该重问

        session.AnswerDurationPrompt(continueRecording: false);
        await loop;
    }

    /// <summary>点【继续】⇒ 不停，隔「下一轮间隔」再问一次。</summary>
    [Fact]
    public async Task 时长兜底答继续_不停_隔一轮再问()
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
                PromptRepeatEvery: TimeSpan.FromMinutes(2),   // 缩短，少跑几圈
                // 宽限期远大于一圈 —— 见「先问不中断」那条的说明。
                PromptGrace: TimeSpan.FromHours(10)),
            onPrompt: () => Interlocked.Increment(ref asked));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        var loop = session.RunAsync("libx264");

        await WaitUntilAsync(() => Volatile.Read(ref asked) > 0, "第一次询问");

        session.AnswerDurationPrompt(continueRecording: true);

        Assert.False(session.IsAwaitingDurationAnswer, "答过了就不该还在问");
        Assert.False(loop.IsCompleted, "点了【继续】之后录制必须继续");

        // 隔 PromptRepeatEvery（2 分钟 = 2 圈）再问 —— 规格「再过 5 分钟再次询问（循环）」。
        await WaitUntilAsync(() => Volatile.Read(ref asked) >= 2, "第二次询问");
        Assert.Equal(2, Volatile.Read(ref asked));

        session.AnswerDurationPrompt(continueRecording: false);
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
        var asked = 0;
        using var cts = new CancellationTokenSource();

        await using var session = Build(dir, new FakeCapture(), clock: clock,
            options: new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromHours(1),
                MaxDuration: RecordingSessionOptions.NoFallback,   // 关闭
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1)),
            onPrompt: () => Interlocked.Increment(ref asked));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        var loop = session.RunAsync("libx264", cts.Token);

        // 跑够久 —— 若它真会问/真会停，这几圈之内必然发生。
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
        var asked = 0;
        using var cts = new CancellationTokenSource();

        await using var session = Build(dir, new FakeCapture(), clock: clock,
            options: new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromHours(1),
                MaxDuration: TimeSpan.FromMinutes(4),
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1),
                PromptGrace: TimeSpan.FromHours(10)),    // 远大于一圈，见上
            onPrompt: () => Interlocked.Increment(ref asked));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        var loop = session.RunAsync("libx264", cts.Token);

        // 一上来就答（此刻并没有在问）。
        session.AnswerDurationPrompt(continueRecording: true);

        Assert.False(session.IsAwaitingDurationAnswer);

        // 首问照样按时来 —— 证明上面那一下没把计时改掉。
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
        Action? onPrompt = null)
    {
        // 方法组不能直接配合 ?. —— 显式判空，让类型明确是 Func<TimeSpan>?。
        Func<TimeSpan>? effectiveClock = clock is null ? null : clock.Read;

        return new RecordingSession(
            new RecordingWorkspace(dir.WorkspaceRoot),
            capture,
            BuildFinalizer(dir, runner ?? new SucceedingRunner(), index ?? new RecordingIndexSpy()),
            new DiskSpaceGuard(new PlentyOfSpaceProbe()),
            "Lenovo EasyCamera",
            "device-1",
            options ?? DefaultOptions,
            effectiveClock,
            AdvancingDelay(clock),
            durationPrompted: onPrompt);
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
    /// Yield 只让出一次执行权，窗口才是可控的。
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
    /// 编排循环的等待：**不真等，但要把假时钟往前推**。
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
    /// </remarks>
    private static Func<TimeSpan, CancellationToken, Task> AdvancingDelay(FakeClock? clock) =>
        (interval, token) =>
        {
            clock?.Advance(interval);
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

    private static RecordingSessionOptions DefaultOptions => new(
        SegmentDuration: TimeSpan.FromHours(1),
        MaxDuration: TimeSpan.FromHours(1),
        StopGracePeriod: TimeSpan.FromSeconds(1),
        PollInterval: TimeSpan.FromSeconds(1));

    private static SessionFinalizer BuildFinalizer(
        TempDir dir, IProcessRunner runner, RecordingIndexSpy index) =>
        new(new RemuxPipeline(Ffmpeg, runner),
            new DecodeVerifier(Ffmpeg, runner),
            index,
            Path.Combine(dir.Path, "archive"));

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
        public List<(string Device, string OutputPath, string Encoder)> Starts { get; } = [];

        /// <summary>置 false 模拟「摄像头打不开」——进程起来了但没产物。</summary>
        public bool ProcessProducesFile { get; init; } = true;

        public Task<ICaptureProcess> StartAsync(
            string device, string outputPath, string encoder, CancellationToken cancellationToken = default)
        {
            Starts.Add((device, outputPath, encoder));
            return Task.FromResult<ICaptureProcess>(new FakeProcess(outputPath, ProcessProducesFile));
        }
    }

    /// <summary>假的采集进程。停的时候按需留下产物。</summary>
    private sealed class FakeProcess(string outputPath, bool producesFile) : ICaptureProcess
    {
        public bool HasExited { get; private set; }

        public Task<int?> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (producesFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, "captured-bytes");
            }

            HasExited = true;
            return Task.FromResult<int?>(0);
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
