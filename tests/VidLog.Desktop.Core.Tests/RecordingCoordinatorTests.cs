using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 一次「工作」的编排 —— 三种工作模式都落在这里（规格 §3.3.1）。
/// </summary>
/// <remarks>
/// 与 <see cref="RecordingSessionTests"/> 的分工：那个测**一段**，这个测**一次工作**
/// （反复开关若干段、换件换单号、打点落盘）。
/// </remarks>
public class RecordingCoordinatorTests
{
    private static readonly WaybillNumber A = WaybillNumber.Parse("SF1234567890");
    private static readonly WaybillNumber B = WaybillNumber.Parse("SF9999999999");

    /// <summary>一台校准过 / 没校准过的假时钟（规格 §3.6.4）。</summary>
    private sealed class FakeTrustedClock(bool calibrated) : ITrustedClock
    {
        public bool IsCalibrated => calibrated;

        public DateTimeOffset Now { get; } = new(2026, 9, 27, 4, 0, 0, TimeSpan.Zero);

        public string? BlockedReason => calibrated ? null : "这台电脑还没有过一次可信的时间校准。";
    }

    // ─────────────────────────────────────────────
    // 未校准不得开始录制（规格 §3.6.4）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 未校准时不开始工作_而且当场说出来()
    {
        // ⚠️ 光是不开始是不够的：用户看到的是「点了没反应」。
        // 所以这条同时验「没有开录」与「有没有话」。
        using var dir = new TempDir();
        var punches = new FakePunchLog();

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, punches,
            trustedClock: new FakeTrustedClock(calibrated: false));

        var notices = new List<CoordinatorNotice>();
        coordinator.Notice += notices.Add;

        coordinator.StartWork();

        Assert.False(coordinator.IsWorking);
        Assert.Contains(notices, n => n.Message.Contains("校准"));

        // 而且真的录不进去 —— 扫到单号也不会开段。
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        Assert.Null(coordinator.CurrentSessionId);
    }

    [Fact]
    public async Task 校准过就照常开录()
    {
        using var dir = new TempDir();
        var punches = new FakePunchLog();

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, punches,
            trustedClock: new FakeTrustedClock(calibrated: true));

        coordinator.StartWork();
        Assert.True(coordinator.IsWorking);

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        Assert.NotNull(coordinator.CurrentSessionId);
    }

    // ─────────────────────────────────────────────
    // 开录与打点
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 扫到单号就开录并打点()
    {
        using var dir = new TempDir();
        var punches = new FakePunchLog();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, punches);

        coordinator.StartWork();
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        Assert.Equal(A, coordinator.CurrentWaybill);

        // 规格 §3.2.4：识别到单号 → 记录该时刻与该单号的关联，且**立即**持久化。
        var punch = Assert.Single(punches.Written);
        Assert.Equal(A, punch.WaybillNumber);
        Assert.Equal(PunchSource.KeyboardScanner, punch.Source);

        // 偏移从**开录那一刻**起算，所以它应该贴近 0。
        // 不要求恰好 0：开录本身要写 manifest 落盘、起采集上下文，
        // 打点必然在它们之后。这里要验的是「基准对」，不是「零耗时」。
        Assert.InRange(punch.MonotonicOffsetMilliseconds, 0, 500);
    }

    [Fact]
    public async Task 没开始工作时扫到单号也开录()
    {
        // 扫码枪打进来的时候用户未必先点过【开始工作】——
        // 规格 §4.1 的状态机就是从「识别到单号」起算的。
        using var dir = new TempDir();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog());

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        Assert.Equal(A, coordinator.CurrentWaybill);
    }

    [Fact]
    public async Task 手动输入也算打点来源()
    {
        using var dir = new TempDir();
        var punches = new FakePunchLog();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, punches);

        await coordinator.SubmitAsync(A, PunchSource.ManualEntry);

        Assert.Equal(PunchSource.ManualEntry, Assert.Single(punches.Written).Source);
    }

    // ─────────────────────────────────────────────
    // 同码停
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 同码停下复扫同码就收尾()
    {
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), index);

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        Assert.Null(coordinator.CurrentWaybill);
        Assert.Single(index.Entries);
    }

    [Fact]
    public async Task 同码停下扫到异码只提示不停录()
    {
        using var dir = new TempDir();
        var notices = new List<CoordinatorNotice>();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog());
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(B, PunchSource.KeyboardScanner);

        // 规格 §3.3.2：不停止录制，仅声音提示。
        Assert.Equal(A, coordinator.CurrentWaybill);
        Assert.Contains(notices, n => n.Kind == CoordinatorNoticeKind.WrongWaybill);
    }

    // ─────────────────────────────────────────────
    // 连续扫：换件
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 连续扫下扫到异码就换段_旧段入库新段开录()
    {
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        var punches = new FakePunchLog();
        await using var coordinator = Build(dir, WorkMode.Continuous, punches, index);

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(B, PunchSource.KeyboardScanner);

        // 立刻以新单号开下一段。
        Assert.Equal(B, coordinator.CurrentWaybill);

        // 旧段在后台收尾 —— 等它。
        await coordinator.PendingFinalization;

        // 旧段入库了。停因（WaybillChanged）体现在收尾结果上，索引里不带停因字段。
        Assert.Single(index.Entries);
        Assert.Equal(2, punches.Written.Count);
    }

    // ─────────────────────────────────────────────
    // 闲置提醒（规格 §3.3.3 电脑端那半 —— 2026-09-27 补）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 可拨的假时钟 + **闸门式**的等待。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 真等两分钟当然不行。这个 <c>delay</c> 每被调用一次就把假时钟往前拨一个轮询间隔，
    /// 于是看门狗「跑了 N 轮」= 「过了 N 秒」。
    /// </para>
    /// <para>
    /// ⚠️ <b>它必须由测试显式放行（<see cref="Tick"/>），不能写成 <c>Task.Yield</c></b>：
    /// 第一版就是 Yield，结果那个看门狗变成一个**热循环**，在并行跑的全套里抢线程池 ——
    /// 表现是套件里**别的**用例的 flake 变多（`PlaybackServer` 的 Dispose 竞态、
    /// 以及 `实现决策.md` §30 记的那条「采集进程被停两次」）。
    /// 一个只该每秒醒一次的循环在测试里变成忙等，是**测试自己制造的**干扰。
    /// </para>
    /// <para>
    /// 闸门式的另一个好处：循环在没被 <c>Tick</c> 时**就停在那儿**，
    /// 于是「不该有第二条提醒」这种断言不必靠 sleep 去等 —— 时钟没动，它本来就不会响。
    /// </para>
    /// </remarks>
    private sealed class IdleTicker
    {
        private readonly SemaphoreSlim _ticks = new(0);
        private TimeSpan _now = TimeSpan.Zero;

        public TimeSpan Now => _now;

        public async Task WaitAsync(TimeSpan step, CancellationToken cancellationToken)
        {
            await _ticks.WaitAsync(cancellationToken);
            _now += step;
        }

        /// <summary>放行 <paramref name="times"/> 轮（= 过 <paramref name="times"/> 秒）。</summary>
        public void Tick(int times = 1) => _ticks.Release(times);
    }

    /// <summary>把闲置提醒数出来，并在第 <paramref name="target"/> 条时放行。</summary>
    private static TaskCompletionSource<int> CountIdle(
        RecordingCoordinator coordinator, int target, Action<int>? onCount = null)
    {
        var signaled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;

        coordinator.Notice += notice =>
        {
            if (notice.Kind != CoordinatorNoticeKind.Idle)
            {
                return;
            }

            var now = Interlocked.Increment(ref count);
            onCount?.Invoke(now);

            if (now >= target)
            {
                signaled.TrySetResult(now);
            }
        };

        return signaled;
    }

    [Fact]
    public async Task 闲置到点提醒一次_而且不停录()
    {
        using var dir = new TempDir();
        var ticker = new IdleTicker();

        var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(),
            policy: new WorkModePolicy(WorkMode.StopOnSameWaybill, IdleReminderOption.Two),
            clock: () => ticker.Now,
            delay: ticker.WaitAsync);

        var first = CountIdle(coordinator, 1);
        var seen = new List<CoordinatorNotice>();
        coordinator.Notice += seen.Add;

        coordinator.StartWork();
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        Assert.True(coordinator.IsWorking);
        // 「还录着」的证据就是**当前段还在**（协调器没有单独的 isRecording 属性，
        // 而 `CurrentWaybill` 非空 == 有一段开着）。
        Assert.Equal(A, coordinator.CurrentWaybill);

        ticker.Tick(130); // 130 秒 > 档位那 2 分钟
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var idle = Assert.Single(seen.Where(n => n.Kind == CoordinatorNoticeKind.Idle).ToList());
        Assert.Contains("没有扫码", idle.Message, StringComparison.Ordinal);

        // ⚠️ **它只提醒，绝不改录制状态** —— 规格 §3.3.3 原话「用户不理就一直录」。
        // 把闲置接成「到点就停」是个很自然的写法（两个都叫"防忘停录"），
        // 而那样操作员离开一会儿就会被停掉一段录像。这条盯着它。
        Assert.True(coordinator.IsWorking, "还该在工作状态");
        Assert.Equal(A, coordinator.CurrentWaybill); // ⚠️ 段还在 == 没被停掉

        // 一段闲置里**只响一次**：再放过一大截，第二条也不该来
        // —— 而且这里不必 sleep：闸门没开，时钟根本没动。
        ticker.Tick(600);
        await coordinator.DisposeAsync();

        Assert.Single(seen.Where(n => n.Kind == CoordinatorNoticeKind.Idle).ToList());
    }

    [Fact]
    public async Task 扫一次码就把闲置计时推后_而且下个闲置段会再提醒一次()
    {
        using var dir = new TempDir();
        var ticker = new IdleTicker();

        var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(),
            policy: new WorkModePolicy(WorkMode.StopOnSameWaybill, IdleReminderOption.Two),
            clock: () => ticker.Now,
            delay: ticker.WaitAsync);

        var first = CountIdle(coordinator, 1);
        var second = CountIdle(coordinator, 2);

        coordinator.StartWork();
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        ticker.Tick(130);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 扫一次 —— **这是唯一能解除「已提醒」的东西**，同时把计时推后。
        //
        // ⚠️ 扫的是**别的**单号（B），不是同一张：同码停模式下复扫同码是**停录**，
        // 停完之后 `Snapshot()` 就是空闲、再也不会提醒 —— 那样这条用例证明的
        // 只是「停了就不提醒」，而不是「计时被推后」。
        // 扫 B 走错码保护那条路：只提示、不停录（规格 §3.3.2）。
        await coordinator.SubmitAsync(B, PunchSource.KeyboardScanner);

        ticker.Tick(130);
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task 结束工作之后不再提醒()
    {
        using var dir = new TempDir();
        var ticker = new IdleTicker();

        var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(),
            policy: new WorkModePolicy(WorkMode.StopOnSameWaybill, IdleReminderOption.Two),
            clock: () => ticker.Now,
            delay: ticker.WaitAsync);

        var count = 0;
        var first = CountIdle(coordinator, 1, onCount: n => Volatile.Write(ref count, n));

        coordinator.StartWork();
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        ticker.Tick(130);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref count));

        await coordinator.StopWorkAsync();

        // 结束之后**放行再多轮也不该有第二条**。
        //
        // ⚠️ 天花板说清楚：这条**分不开**两种原因 —— 看门狗被取消了，
        // 还是「没在录了所以判据不成立」（`OnIdle` 见 `SegmentOpen == false` 就返回 Nothing）。
        // 两种都算对，而这条也确实只该保证「不再提醒」这一件事。
        ticker.Tick(600);
        await Task.Delay(50);

        await coordinator.DisposeAsync();
        Assert.Equal(1, Volatile.Read(ref count));
    }

    // ─────────────────────────────────────────────
    // 错误扫描（规格 §6.1「必须保存的事实」—— 2026-09-26 补）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 扫到别的面单要落一条错误扫描记录()
    {
        // 这一条是规格 §6.1 点名的「错误扫描（错码保护触发的事件，诊断用）」，
        // 2026-09-26 之前**两端都没实现**：错码保护只做了界面提示与播报，
        // 事后查不出操作员那一刻扫到了什么。
        using var dir = new TempDir();
        var scanErrors = new ScanErrorLog(Path.Combine(dir.Path, "scan-errors.jsonl"));

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), scanErrors: scanErrors);

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        var sessionId = coordinator.CurrentSessionId;

        await coordinator.SubmitAsync(B, PunchSource.KeyboardScanner);

        var entry = Assert.Single(await scanErrors.LoadAllAsync());

        // 四个字段逐字对齐母仓 docs/02-数据模型.md §1.7。
        Assert.Equal(sessionId, entry.SessionId);
        Assert.Equal(A.Value, entry.ExpectedWaybill.Value);
        Assert.Equal(B.Value, entry.ScannedWaybill.Value);
        Assert.NotEqual(default, entry.OccurredAt);
    }

    [Fact]
    public async Task 复扫同码不记错误扫描_那是正常停止路径()
    {
        using var dir = new TempDir();
        var scanErrors = new ScanErrorLog(Path.Combine(dir.Path, "scan-errors.jsonl"));

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), scanErrors: scanErrors);

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        Assert.Empty(await scanErrors.LoadAllAsync());
    }

    [Fact]
    public async Task 不装错误扫描日志时_错码保护照样提示()
    {
        // ⚠️ 这一条守的是「诊断记录写不下去不能把录制带下去」：
        // 不装（或者盘写不进去）时，提示这条路径必须一个字都不少。
        using var dir = new TempDir();
        var notices = new List<CoordinatorNotice>();

        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog());
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(B, PunchSource.KeyboardScanner);

        Assert.Contains(notices, n => n.Kind == CoordinatorNoticeKind.WrongWaybill);
        Assert.Equal(A, coordinator.CurrentWaybill);
    }

    [Fact]
    public async Task 连续扫下复扫同码不停也不提示()
    {
        using var dir = new TempDir();
        var notices = new List<CoordinatorNotice>();
        await using var coordinator = Build(dir, WorkMode.Continuous, new FakePunchLog());
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        Assert.Equal(A, coordinator.CurrentWaybill);
        // 连续扫下换件是正常路径，不该报「面单不同」。
        Assert.DoesNotContain(notices, n => n.Kind == CoordinatorNoticeKind.WrongWaybill);
    }

    // ─────────────────────────────────────────────
    // 开始 / 结束工作
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 结束工作会收尾在录的段()
    {
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), index);

        coordinator.StartWork();
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        var outcome = await coordinator.StopWorkAsync();

        Assert.NotNull(outcome);
        Assert.True(outcome!.Succeeded, outcome.FailureReason);
        Assert.Null(coordinator.CurrentWaybill);
        Assert.Single(index.Entries);
    }

    [Fact]
    public async Task 没在录时结束工作不抛()
    {
        using var dir = new TempDir();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog());

        Assert.Null(await coordinator.StopWorkAsync());
    }

    [Fact]
    public async Task 收尾失败时给出可见的提示()
    {
        using var dir = new TempDir();
        var notices = new List<CoordinatorNotice>();

        // 让收尾器失败。
        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), index: null, runner: new FailingRunner());
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.StopWorkAsync();

        // I3：收尾失败必须让用户看见。
        var notice = Assert.Single(notices, n => n.Kind == CoordinatorNoticeKind.FinalizeFailed);
        // 而且要说明**东西还在**，否则用户会以为录像丢了。
        Assert.Contains("仍在工作区", notice.Message, StringComparison.Ordinal);
        // 也得给出真正的原因 —— 只说「失败了」等于没说（I3 的同一条精神）。
        Assert.Contains("boom", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 改工作模式立刻算数_不必重启()
    {
        // 设置页写着「工作模式立即生效」—— 那句要真。策略若是构造时定死，
        // 用户改完发现没反应，只会以为这个下拉坏了（踩坑 #13）。
        using var dir = new TempDir();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog());

        // 从「同码停」（扫到异码只提示）改成「连续扫」（扫到异码即换段）。
        coordinator.Mode = WorkMode.Continuous;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(B, PunchSource.KeyboardScanner);

        // 换成新单号了 —— 说明新档位真的被用上了，不是只存进了设置。
        Assert.Equal(B, coordinator.CurrentWaybill);
    }

    // ─────────────────────────────────────────────
    // 编排循环：滚段与时长兜底（规格 §3.1.1 / §3.3.4）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 几百毫秒就能观察到滚段与时长兜底的参数。
    /// </summary>
    /// <remarks>
    /// 协调器**不给**注入假时钟/假延时（那是会话层的口子，见
    /// <c>RecordingSessionTests</c> 里那批用 <c>FakeClock</c> 的用例），
    /// 所以这里只能走真实时间。把段压到 80ms、循环 10ms 一次，
    /// 整条用例仍是亚秒级，而验的正是**生产走的那条路**。
    /// </remarks>
    private static RecordingSessionOptions FastRolling(
        TimeSpan segment, TimeSpan max) => RecordingSessionOptions.Default with
    {
        SegmentDuration = segment,
        MaxDuration = max,
        PollInterval = TimeSpan.FromMilliseconds(10),
    };

    [Fact]
    public async Task 分段时长到点会滚段()
    {
        // 规格 §3.1.1：连续分段录像，「长录不断、掉电不丢」。
        // 段不滚的话，进程被杀时 manifest 里一段都没封闭 —— 什么都恢复不出来。
        //
        // ⚠️ 这条钉的是**协调器起没起编排循环**。会话层早已测透（见
        // RecordingSessionTests 里那批 FakeClock 用例），但循环曾经根本没被启动：
        // 整场只录一个永不滚动的段，而所有测试照样全绿。
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), index,
            session: FastRolling(TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(30)));

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        // 80ms 一段，等 500ms —— 足够滚好几段。
        await Task.Delay(500);

        var outcome = await coordinator.StopWorkAsync();

        Assert.NotNull(outcome);
        Assert.True(
            outcome!.Segments.Count >= 2,
            $"分段时长 80ms、录了约 500ms，应当已经滚过段；实际只有 {outcome.Segments.Count} 段");
    }

    [Fact]
    public async Task 时长兜底到点先问_答停止之后才收尾并把状态接回来()
    {
        // 规格 §3.3.4：到点**问**用户，不是直接停。
        // ⚠️ 这条原来叫「到点会自动收尾」—— 2026-09-27 改掉了那个行为：
        // 到点直接收尾会把**任何一段正常录制**在档位时间切断，
        // 而「它会把正常录制打断」正是 2026-09-21 那次需求变更要修的东西。
        //
        // 同时保留原来那条目的：收尾是**循环自己**发起的，没人 await 得到 ——
        // 协调器不接住的话，CurrentWaybill 会一直指着那个已经收尾的会话：
        // 界面显示「录制中」，用户会一直等一个永远不会发生的停录。
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        var notices = new List<CoordinatorNotice>();
        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), index,
            session: FastRolling(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(150)));
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await Task.Delay(400);   // 越过兜底时机（150 ms），但远没到宽限期（默认 1 分钟）

        // ① 到点了 —— **只是问**，录制必须还在继续。
        Assert.NotNull(coordinator.CurrentWaybill);
        Assert.Empty(index.Entries);
        Assert.Contains(notices, n => n.Kind == CoordinatorNoticeKind.DurationPrompt);

        // ② 用户答【停止】→ 这才收尾，而且协调器要把状态接回来。
        coordinator.AnswerDurationPrompt(continueRecording: false);
        await Task.Delay(400);

        Assert.Null(coordinator.CurrentWaybill);

        // 录像必须真的入了库 —— 自己停掉却没入库，那是丢证据。
        Assert.Single(index.Entries);

        // 而且要让用户看得见为什么停了（I3）。
        Assert.Contains(notices, n => n.Message.Contains("时长上限", StringComparison.Ordinal));
    }

    // ─────────────────────────────────────────────
    // 重复单号检测（规格 §3.2.5）
    // ─────────────────────────────────────────────
    //
    // 规格原话：「识别到的单号若在**近 N 天内**已有未删除记录，必须提示。**N 可配置**。」
    // 硬约束：「这三项**全部异步执行，绝不阻塞开录**」。

    /// <summary>造一条「若干天前录的」记录（同单号）。</summary>
    private static RecordingEntry OldEntryFor(WaybillNumber waybill, int daysAgo) =>
        new("ev-old", "sess-old", waybill,
            DateTimeOffset.UtcNow.AddDays(-daysAgo),
            DateTimeOffset.UtcNow.AddDays(-daysAgo),
            TimeSpan.FromMinutes(5),
            RelativePath.Parse("2026/09/20/ev-old.mp4"),
            ContentHash.Parse(new string('a', 64)),
            "device-1");

    /// <summary>检测是**异步**的（规格要求），所以不能立刻断言 —— 等它出现。</summary>
    private static async Task WaitForNoticeAsync(
        List<CoordinatorNotice> notices, CoordinatorNoticeKind kind, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (!notices.Any(n => n.Kind == kind))
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"等不到 {kind} 通知");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task 近N天录过就提示_而且没有挡住开录()
    {
        using var dir = new TempDir();
        var notices = new List<CoordinatorNotice>();
        var queried = new List<string>();

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(),
            duplicateProbe: (waybill, _) =>
            {
                queried.Add(waybill.Value);
                return Task.FromResult<IReadOnlyList<RecordingEntry>>([OldEntryFor(waybill, 3)]);
            },
            duplicateCheckDays: 7);
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await WaitForNoticeAsync(notices, CoordinatorNoticeKind.DuplicateWaybill);

        var notice = Assert.Single(notices, n => n.Kind == CoordinatorNoticeKind.DuplicateWaybill);
        Assert.Contains(A.Value, notice.Message, StringComparison.Ordinal);
        Assert.Contains("7 天", notice.Message, StringComparison.Ordinal);
        Assert.Contains("1 次", notice.Message, StringComparison.Ordinal);

        // ★ 而且**开录照常**：这条检测绝不挡路（规格原话「绝不阻塞开录」）。
        Assert.Equal(A, coordinator.CurrentWaybill);
        Assert.Equal([A.Value], queried);
    }

    [Fact]
    public async Task N天以外的不提示_但检测确实跑过了()
    {
        using var dir = new TempDir();
        var notices = new List<CoordinatorNotice>();
        var queried = false;

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(),
            duplicateProbe: (waybill, _) =>
            {
                queried = true;
                // 30 天前录的，而窗口是 7 天。
                return Task.FromResult<IReadOnlyList<RecordingEntry>>([OldEntryFor(waybill, 30)]);
            },
            duplicateCheckDays: 7);
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        // ⚠️ 先等「检测真的跑过」再断言「没提示」—— 否则这条会**绿在巧合上**
        // （检测还没跑，当然没有通知）。
        await WaitUntilAsync(() => queried);

        Assert.DoesNotContain(notices, n => n.Kind == CoordinatorNoticeKind.DuplicateWaybill);
    }

    [Fact]
    public async Task 重复单号检测绝不阻塞开录_哪怕探测永远不返回()
    {
        // 规格 §3.2.5 的硬约束：「这三项**全部异步执行，绝不阻塞开录**」。
        // 所以这里让探测**永远不返回** —— 开录必须照样完成。
        //
        // ⚠️ 变红配方：把 `_ = CheckDuplicateWaybillAsync(...)` 改成
        // `await CheckDuplicateWaybillAsync(...)` ⇒ 这条**挂住**（开录永不返回），
        // 表现为测试超时。
        using var dir = new TempDir();

        var never = new TaskCompletionSource<IReadOnlyList<RecordingEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(),
            duplicateProbe: (_, _) => never.Task,
            duplicateCheckDays: 7);

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        // ★ 开录回来了、而且真的在录 —— 探测挂着不影响它。
        Assert.Equal(A, coordinator.CurrentWaybill);
    }

    [Fact]
    public async Task 关掉时连探测都不发()
    {
        // 规格说「N 可配置」，那 0 就得真的关掉 —— 不只是「不提示」，
        // 而是**连索引都不读**（连续扫的工位上每件读一次索引是白花钱）。
        using var dir = new TempDir();
        var queried = false;

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(),
            duplicateProbe: (waybill, _) =>
            {
                queried = true;
                return Task.FromResult<IReadOnlyList<RecordingEntry>>([OldEntryFor(waybill, 1)]);
            },
            duplicateCheckDays: 0);
        var notices = new List<CoordinatorNotice>();
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await Task.Delay(150);   // 给「假如它要跑」留出时间

        Assert.False(queried, "关掉之后连探测都不该发");
        Assert.DoesNotContain(notices, n => n.Kind == CoordinatorNoticeKind.DuplicateWaybill);
    }

    /// <summary>等一个条件成立（用于等异步检测真的跑过）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("等不到那个条件");
            }

            await Task.Delay(10);
        }
    }

    // ─────────────────────────────────────────────
    // 测试脚手架
    // ─────────────────────────────────────────────

    private static RecordingCoordinator Build(
        TempDir dir,
        WorkMode mode,
        IPunchLog punches,
        RecordingIndexSpy? index = null,
        IProcessRunner? runner = null,
        RecordingSessionOptions? session = null,
        ScanErrorLog? scanErrors = null,
        WorkModePolicy? policy = null,
        Func<TimeSpan>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ITrustedClock? trustedClock = null,
        Func<WaybillNumber, CancellationToken, Task<IReadOnlyList<RecordingEntry>>>? duplicateProbe = null,
        int duplicateCheckDays = 0)
    {
        var ffmpeg = FfmpegLocator.TryFind() ?? "ffmpeg";
        var effectiveRunner = runner ?? new SucceedingRunner();

        return new RecordingCoordinator(
            new RecordingWorkspace(dir.WorkspaceRoot),
            new FakeCapture(),
            new SessionFinalizer(
                new RemuxPipeline(ffmpeg, effectiveRunner),
                new DecodeVerifier(ffmpeg, effectiveRunner),
                index ?? new RecordingIndexSpy(),
                Path.Combine(dir.Path, "archive")),
            new DiskSpaceGuard(new PlentyOfSpaceProbe()),
            punches,
            NullLogger.Instance,
            policy ?? new WorkModePolicy(mode, IdleReminderOption.Off),
            new CoordinatorOptions("Lenovo EasyCamera", "device-1", "libx264", duplicateCheckDays),
            scanErrors,
            clock,
            delay,
            trustedClock,
            license: null,
            duplicateProbe: duplicateProbe)
        {
            SessionOptions = session ?? RecordingSessionOptions.Default,
        };
    }

    private sealed class FakeCapture : ICameraCapture
    {
        public Task<ICaptureProcess> StartAsync(
            string device, string outputPath, string encoder, CancellationToken cancellationToken = default) =>
            Task.FromResult<ICaptureProcess>(new FakeProcess(outputPath));
    }

    private sealed class FakeProcess(string outputPath) : ICaptureProcess
    {
        public Task<int?> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, "captured");
            return Task.FromResult<int?>(0);
        }
    }

    private sealed class FakePunchLog : IPunchLog
    {
        public List<Punch> Written { get; } = [];

        public Task AppendAsync(Punch punch, CancellationToken cancellationToken = default)
        {
            Written.Add(punch);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Punch>> LoadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Punch>>(Written);
    }

    private sealed class PlentyOfSpaceProbe : IDiskSpaceProbe
    {
        public long GetFreeBytes(string path) => 100L * 1024 * 1024 * 1024;
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
                Directory.CreateDirectory(Path.GetDirectoryName(args[yIndex + 1])!);
                File.WriteAllText(args[yIndex + 1], "published");
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class FailingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(1, string.Empty, "boom"));
    }

    /// <summary>索引替身 —— 记下每次入库，供断言停因。</summary>
    /// <summary>索引替身 —— 记下每次入库。</summary>
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

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public string WorkspaceRoot => System.IO.Path.Combine(Path, "work");

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-coord-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

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
