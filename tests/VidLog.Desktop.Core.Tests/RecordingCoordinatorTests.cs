using VidLog.Desktop.Core.Camera;
using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Scanning;

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
    // 单号形状核查（需求方 2026-10-10：防止扫到假码）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 不像单号的东西开不了录()
    {
        // 面单上除了单号还有分拣码那一类条码 —— 摄像头会认到它们。
        using var dir = new TempDir();
        var punches = new FakePunchLog();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, punches);

        coordinator.StartWork();

        // ⚠️ 先钉住「工作确实开始了」—— 不然这一条在「工作压根没开」时
        // 也会绿（`CurrentSessionId` 本来就是 null），那是条**空集绿灯**。
        Assert.True(coordinator.IsWorking);

        await coordinator.SubmitAsync(
            WaybillNumber.Parse("320D-D140BBB"), PunchSource.CameraDecoder);

        Assert.Null(coordinator.CurrentSessionId);
        Assert.Empty(punches.Written);
    }

    [Fact]
    public async Task 手打不过形状核查()
    {
        // 手打是用户明确要做的事（§3.2.2 的兜底）—— 拦下它比放过假码更糟。
        using var dir = new TempDir();
        var punches = new FakePunchLog();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, punches);

        coordinator.StartWork();
        Assert.True(coordinator.IsWorking);

        await coordinator.SubmitAsync(WaybillNumber.Parse("A1"), PunchSource.ManualEntry);

        Assert.NotNull(coordinator.CurrentSessionId);
    }

    [Fact]
    public async Task 开段那一刻先把单号喂进录制期识码的闸()
    {
        // ⚠️ 这条盖住的是 `StartSegmentAsync` 里**两句代码的先后**：
        // `Raise(SegmentStarted)` 紧邻着的那句 `Recognition?.PrimeWith(...)`。
        // 删掉它，同码停模式会在开录后的第一帧把**还摆在框里**的那张面单
        // 当成「又扫了一次」—— **刚开录就停**。
        using var dir = new TempDir();
        var recognition = new RecordingRecognition(
            new AlwaysScanner(A), NullLogger.Instance);
        recognition.Start();

        try
        {
            await using var coordinator = Build(
                dir, WorkMode.StopOnSameWaybill, new FakePunchLog());
            coordinator.Recognition = recognition;

            var seen = new List<WaybillNumber>();
            recognition.Scanned += seen.Add;

            coordinator.StartWork();
            await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
            Assert.NotNull(coordinator.CurrentSessionId);

            // 面单还在框里：持续投帧一秒，它**不该**再报一次。
            var deadline = Environment.TickCount64 + 1000;
            while (Environment.TickCount64 < deadline)
            {
                recognition.OnFrame(new PreviewFrame(
                    new byte[640 * 360 * 3], 640, 360, Environment.TickCount64));
                await Task.Delay(10);
            }

            Assert.Empty(seen);
        }
        finally
        {
            await recognition.StopAsync();
        }
    }

    /// <summary>一直读得到同一个码的假解码器（录制期识码那几条用）。</summary>
    private sealed class AlwaysScanner(WaybillNumber waybill) : IFrameScanner
    {
        public string? TryDecode(CameraFrame frame) => waybill.Value;
    }

    [Fact]
    public async Task 命令码不被形状核查挡住()
    {
        // ⚠️ 绊线：`VLREC` 一个数字都没有 —— 形状核查单独看**必然**把它判成假码
        // （见 `WaybillPlausibilityTests`）。它还能走通，全靠 `SubmitAsync` 里
        // 两道闸的**先后**。有人把核查挪到命令码前面，这条就红。
        using var dir = new TempDir();
        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(),
            trustedClock: new FakeTrustedClock(calibrated: true));

        // 扫这一下**本身就是**「按开始录制」（见 `HandleScanCommand`）。
        await coordinator.SubmitAsync(
            WaybillNumber.Parse(ScanCommand.StartWork), PunchSource.KeyboardScanner);

        Assert.True(coordinator.IsWorking);
        Assert.Null(coordinator.CurrentWaybill);
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

    [Fact]
    public async Task 换件时开始录制只报一条_旧段也报一条结束()
    {
        // 需求方 2026-10-10：「动态栏开始录制只显示一条，结束录制也要显示一条」。
        // 换件这条路原来报两句「开始录制 B。」（`StartSegmentAsync` 一句 + 换件那句
        // 也是「开始录制 next。」），而旧单号一句收尾都没有。
        using var dir = new TempDir();
        var punches = new FakePunchLog();
        await using var coordinator = Build(dir, WorkMode.Continuous, punches, new RecordingIndexSpy());

        var notices = new List<CoordinatorNotice>();
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(B, PunchSource.KeyboardScanner);
        await coordinator.PendingFinalization;

        Assert.Single(notices, n => n.Message.Contains($"开始录制 {B.Value}", StringComparison.Ordinal));

        // 收尾那条带的是**旧单号**（换件那条如果说的是新单号，等于又报了一遍开始）。
        Assert.Contains(notices, n => n.Message.Contains("结束录制", StringComparison.Ordinal)
            && n.Message.Contains(A.Value, StringComparison.Ordinal));
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
    public async Task 不装错误扫描日志时_错码保护照样提示_而且日志里留下了()
    {
        // ⚠️ 这一条守的是「诊断记录写不下去不能把录制带下去」：
        // 不装（或者盘写不进去）时，提示这条路径必须一个字都不少。
        //
        // ⚠️ 2026-10-10 需求方裁定：**错码这条通知自己要有一条日志**。
        // 从前它的留痕全押在 `ScanErrorLog` 那本账上，而那本账**是有条件的**
        // —— 这个用例正是「条件不成立」的那一档（`scanErrors` 一个都没装）：
        // 那一刻在日志里**一个字都没有**，而错码保护恰恰是「事后要能查出操作员
        // 那一刻扫到了什么」的那件事。所以这里同时盯两件事：提示还在，
        // 而且日志里**查得到扫到的那个码**（只记「发生过错码」不够用）。
        using var dir = new TempDir();
        var notices = new List<CoordinatorNotice>();
        var logger = new CapturingLogger();

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), logger: logger);
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(B, PunchSource.KeyboardScanner);

        Assert.Contains(notices, n => n.Kind == CoordinatorNoticeKind.WrongWaybill);
        Assert.Equal(A, coordinator.CurrentWaybill);

        var line = Assert.Single(
            logger.Lines, m => m.StartsWith("错码保护：", StringComparison.Ordinal));
        Assert.Contains(B.Value, line, StringComparison.Ordinal);
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
    // 开段之前的准备（规格 §3.1.7：改了下次开始工作生效）
    // ─────────────────────────────────────────────

    /// <summary>
    /// **准备钩子在开段之前跑，换掉的采集与编码器就是真用的那两个。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规格 §3.1.7：编码 / 分辨率改了「**下次开始工作**才生效」，判据明写**不用重启**。
    /// 2026-09-30 之前那是句假话 —— 规格钉在构造协调器那一刻，改完还是老样子（§80）。
    /// </para>
    /// <para>
    /// ⚠️ <b>编码器也必须在这一次换掉</b>：录制那一侧原来自己另挑过一次编码器，
    /// 而那份候选表**全是 H.264** —— 于是「选 H.265、界面与索引都说 H.265、
    /// 录出来是 H.264」（§79）。所以这条同时守着那两件事。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 开段前跑准备钩子_换掉的采集与编码器才是真用的那两个()
    {
        using var dir = new TempDir();
        var before = new SpyCapture();
        var after = new SpyCapture();
        var order = new List<string>();

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), capture: before);

        coordinator.PrepareCaptureAsync = _ =>
        {
            order.Add("prepare");
            coordinator.Capture = after;
            coordinator.Encoder = "hevc_nvenc";
            return Task.CompletedTask;
        };

        before.OnStart = () => order.Add("use-before");
        after.OnStart = () => order.Add("use-after");

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        // 钩子先跑、采集后起 —— 那是它能真开一次相机重探的前提（相机那一刻必然是空的）。
        Assert.Equal(["prepare", "use-after"], order);

        // ⚠️ 编码器跟着换了：不换就是 §79（选 H.265、录出来是 H.264）。
        Assert.Equal("hevc_nvenc", Assert.Single(after.Encoders));
        Assert.Empty(before.Encoders);
    }

    // ─────────────────────────────────────────────
    // 编排循环：滚段与时长兜底（规格 §3.1.1 / §3.3.4）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 几百毫秒就能观察到分段与时长兜底的参数。
    /// </summary>
    /// <remarks>
    /// 协调器**不给**注入假时钟/假延时（那是会话层的口子，见
    /// <c>RecordingSessionTests</c> 里那批用 <c>FakeClock</c> 的用例），
    /// 所以这里只能走真实时间：档位压小、循环 10ms 一次，整条用例仍是亚秒级，
    /// 而验的正是**生产走的那条路**。
    /// <para>
    /// ⚠️ <paramref name="segment"/> 是**秒**级：会话把它 `Ceiling` 成整秒交给
    /// ffmpeg 的 `-segment_time`（见 <c>RecordingSession.StartCaptureAsync</c>），
    /// 给个零点几秒的值只会被抬成 1 秒，看不出区别。
    /// </para>
    /// </remarks>
    private static RecordingSessionOptions FastRolling(
        TimeSpan segment, TimeSpan max) => RecordingSessionOptions.Default with
    {
        SegmentDuration = segment,
        MaxDuration = max,
        PollInterval = TimeSpan.FromMilliseconds(10),
    };

    [Fact]
    public async Task 分段交给ffmpeg自己滚_整场只起一路采集()
    {
        // 规格 §3.1.1：连续分段录像，「长录不断、掉电不丢」。
        // 段不滚的话，进程被杀时盘上只有一个大文件、manifest 里一段都没登记。
        //
        // ⚠️ **T17（2026-10-05）改了这条的实现，所以要守的东西也变了。**
        // 从前是「段到点了：关掉旧采集 → 再 `await` 起一路新的」，实测**每段之间
        // 丢 1~2 秒画面**（关相机到重开完）。现在整场只起**一路** ffmpeg，
        // 让它用 `-f segment` 自己按 N 秒切 —— 边界上不重开相机。
        //
        // 于是可测的就三件（会话层那几条 FakeClock 用例已经把「数盘上那几片、
        // 按算出来的时间戳登记」测透了）：
        //   ① 协调器真把「按 N 秒切」告诉了采集 —— 不给的话整场只有一个文件，
        //      掉电全丢，而**所有测试照样绿**；
        //   ② 整场只起**一路**（起两路 = 又把画面切断了，正是 T17 要修的）；
        //   ③ 0 号那一片在收尾结果里（掉电时唯一能捞回来的就是它）。
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        var capture = new SpyCapture();
        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), index, capture: capture,
            session: FastRolling(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30)));

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        // 不用等过分段点：滚段那一刻已经没有我方代码可等了（ffmpeg 自己滚），
        // 该验的是**开录时给的参数**。等「采集被叫起来」就够。
        await WaitUntilAsync(() => capture.Starts.Count > 0);

        // ① 采集被要求按 2 秒切，落成的是一串 `segment-000/001/…`。
        var start = Assert.Single(capture.Starts);
        Assert.Equal(2, start.SegmentSeconds);
        Assert.EndsWith("segment-%03d.mkv", start.OutputPath);

        var outcome = await coordinator.StopWorkAsync();

        Assert.NotNull(outcome);
        Assert.Contains(outcome!.Segments, segment => segment.Source.Sequence == 0);

        // ② 整场就这一路 —— 收尾之后再数一遍，中途**没有**再起过第二个进程
        //（从前每滚一段就重开一路，那 1~2 秒的空洞就是这么多出来的）。
        Assert.Single(capture.Starts);
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

        // ⚠️ 通知是**另一个线程**发过来的，而 `List<T>` 不是线程安全的。
        // 所以「等条件」这一步不看那个列表，看一个 `Interlocked` 的旗子。
        var prompted = 0;

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), index,
            session: FastRolling(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(150)));

        coordinator.Notice += n =>
        {
            if (n.Kind == CoordinatorNoticeKind.DurationPrompt)
            {
                Interlocked.Exchange(ref prompted, 1);
            }

            lock (notices)
            {
                notices.Add(n);
            }
        };

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        // ⚠️ 原来写的是「固定等 400ms，越过兜底时机（150ms）」—— 也是**赌墙钟**，
        // 而 Release 下这条实测约 1/3 概率红（机器忙时 400ms 里那一圈还没跑到）。
        // 改成**等到那次询问真的发生**。
        await WaitUntilAsync(
            () => Volatile.Read(ref prompted) == 1 || coordinator.CurrentWaybill is null);

        // ① 到点了 —— **只是问**，录制必须还在继续。
        Assert.NotNull(coordinator.CurrentWaybill);
        Assert.Empty(index.Entries);
        Assert.Contains(notices, n => n.Kind == CoordinatorNoticeKind.DurationPrompt);

        // ② 用户答【停止】→ 这才收尾，而且协调器要把状态接回来。
        coordinator.AnswerDurationPrompt(continueRecording: false);

        // ⚠️ 同样不赌时间：等**收尾真的走完**（状态被接回来）。
        await WaitUntilAsync(() => coordinator.CurrentWaybill is null);

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
        var logger = new CapturingLogger();

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(),
            duplicateProbe: (waybill, _) =>
            {
                queried.Add(waybill.Value);
                return Task.FromResult<IReadOnlyList<RecordingEntry>>([OldEntryFor(waybill, 3)]);
            },
            duplicateCheckDays: 7,
            logger: logger);
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await WaitForNoticeAsync(notices, CoordinatorNoticeKind.DuplicateWaybill);

        // ⚠️ 需求方 2026-10-10 定形：**上屏照旧带天数与次数** ——「录过 N 次」正是
        // 用户拿来判断是不是重复录件的那个数。改短的只是**念的那一句**
        // （`App.OnNotice` 里只念「单号重复」四个字），两件事分开。
        // 两头都盯：通知里要有这两个数，日志里也要有（界面之外的那条退路）。
        var notice = Assert.Single(notices, n => n.Kind == CoordinatorNoticeKind.DuplicateWaybill);
        Assert.Contains($"{A.Value} 在最近 7 天里录过 1 次", notice.Message, StringComparison.Ordinal);
        Assert.Contains("核对一下是不是重复录件或者单号扫错了。", notice.Message, StringComparison.Ordinal);

        var line = Assert.Single(
            logger.Lines, m => m.StartsWith("重复单号：", StringComparison.Ordinal));
        Assert.Contains("7 天", line, StringComparison.Ordinal);
        Assert.Contains("次数=1", line, StringComparison.Ordinal);

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
    public async Task 同一次会话分成多段也只算一次()
    {
        // ⚠️ 一次会话会被分成多段（时长兜底、磁盘将满都会分段），每段在索引里
        // 各占一行 —— 不去重的话「这个单号录过 1 次」会被说成「3 次」，
        // 而界面那句话正是用户拿来判断是不是重复录件的（D2，2026-10-10）。
        // 反证：去掉 `.GroupBy(e => e.SessionId, …)` ⇒ 这条红（会说「4 次」）。
        using var dir = new TempDir();
        var notices = new List<CoordinatorNotice>();
        var logger = new CapturingLogger();

        var first = OldEntryFor(A, 3);
        var second = first with { EvidenceId = "ev-old-2" };   // 同一次会话的第 2 段
        var third = first with { EvidenceId = "ev-old-3" };    // 第 3 段
        var other = first with { SessionId = "sess-other", EvidenceId = "ev-old-4" }; // 另一次会话

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(),
            duplicateProbe: (_, _) => Task.FromResult<IReadOnlyList<RecordingEntry>>(
                [first, second, third, other]),
            duplicateCheckDays: 7,
            logger: logger);
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await WaitForNoticeAsync(notices, CoordinatorNoticeKind.DuplicateWaybill);

        var notice = Assert.Single(notices, n => n.Kind == CoordinatorNoticeKind.DuplicateWaybill);

        // ⚠️ 次数**上屏**（需求方 2026-10-10 裁定），而且这里专盯它算得对：
        // 三段归一次会话 ⇒ **2**，不是 4。日志那一行照旧也盯（见下）。
        Assert.Contains($"{A.Value} 在最近 7 天里录过 2 次", notice.Message, StringComparison.Ordinal);

        var line = Assert.Single(
            logger.Lines, m => m.StartsWith("重复单号：", StringComparison.Ordinal));

        // 三段归一次会话 + 另一次会话 —— **两次**，不是四次。
        Assert.Contains("次数=2", line, StringComparison.Ordinal);
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
    // 发货 / 退货 + 屏幕上那两张命令条码（设计图 `_35`）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 扫到切换那两张码只切档_不开录也不打点()
    {
        using var dir = new TempDir();
        var punches = new FakePunchLog();
        var index = new RecordingIndexSpy();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, punches, index);

        await coordinator.SubmitAsync(WaybillNumber.Parse(ScanCommand.SwitchToReturn),
            PunchSource.KeyboardScanner);

        Assert.Equal(BusinessType.Return, coordinator.CurrentBusinessType);

        // ⚠️ 这三条才是这一条的要点：扫了一张**码**，不能因此开一段录像、
        // 也不能落一次打点 —— 那等于「扫了张屏幕上的图，凭空多出一条证据」。
        Assert.Null(coordinator.CurrentWaybill);
        Assert.Empty(punches.Written);
        Assert.Empty(index.Entries);
    }

    [Fact]
    public async Task 再扫一次切回发货()
    {
        using var dir = new TempDir();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog());

        await coordinator.SubmitAsync(WaybillNumber.Parse(ScanCommand.SwitchToReturn),
            PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(WaybillNumber.Parse(ScanCommand.SwitchToOutbound),
            PunchSource.KeyboardScanner);

        Assert.Equal(BusinessType.Outbound, coordinator.CurrentBusinessType);
    }

    [Fact]
    public async Task 已经是这一档时不重复报已切换()
    {
        // 连着扫两次同一张码是很自然的动作（没看清扫上没有）。
        // 每次都说一遍「已切到退货」会让人以为它真的切了两下。
        using var dir = new TempDir();
        var notices = new List<CoordinatorNotice>();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog());
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(WaybillNumber.Parse(ScanCommand.SwitchToReturn),
            PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(WaybillNumber.Parse(ScanCommand.SwitchToReturn),
            PunchSource.KeyboardScanner);

        Assert.Single(notices, n => n.Kind == CoordinatorNoticeKind.BusinessTypeChanged);
    }

    [Fact]
    public async Task 扫到开始录像那张码等于按下开始录制()
    {
        using var dir = new TempDir();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog());

        await coordinator.SubmitAsync(WaybillNumber.Parse(ScanCommand.StartWork),
            PunchSource.KeyboardScanner);

        Assert.True(coordinator.IsWorking);

        // ⚠️ **只是开始工作** —— 开录仍然要等一张真面单。
        // 把这条码做成「直接开录」的话，录出来的那一段会挂在一个叫 VLREC 的单号上。
        Assert.Null(coordinator.CurrentWaybill);
    }

    [Fact]
    public async Task 切到退货之后录的那一段带上退货标签()
    {
        // ⚠️ 这一条是**端到端**的那条：扫屏幕上的码 → 开段 → 收尾 → 标签落盘。
        // 中间任何一环断了（档没抄进会话、收尾时读的是当前档、标签写失败被吞），
        // 表现都是「录像标签是发货」，而那种错在界面上完全看不出来。
        using var dir = new TempDir();
        var labels = new SpyLabelStore();
        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), labels: labels);

        await coordinator.SubmitAsync(WaybillNumber.Parse(ScanCommand.SwitchToReturn),
            PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        // 同码复扫 ⇒ 停录并**当场**收尾（不是后台）。
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        var written = Assert.Single(labels.Written);
        Assert.Equal(LabelKeys.BusinessType, written.Key);
        Assert.Equal(BusinessTypes.ReturnValue, written.Value);

        // 证据 id 是 `{会话}-{段号:000}` —— 标签必须挂在**那条录像**上，
        // 挂错地方的表现是「检索页按类型筛，这条不见了」。
        Assert.EndsWith("-000", written.EvidenceId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 没切过档就是发货()
    {
        using var dir = new TempDir();
        var labels = new SpyLabelStore();
        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), labels: labels);

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        Assert.Equal(BusinessTypes.OutboundValue, Assert.Single(labels.Written).Value);
    }

    [Fact]
    public async Task 换件时旧段用的是它开始时的档()
    {
        // ⚠️ 这一条盯着一个**只有换件路径上才会出**的错：旧段是在后台收尾的，
        // 那时 `CurrentBusinessType` 可能已经被切过了 —— 收尾若去读「当前档」，
        // 退回件的旧段会被标成发货，而界面上什么都看不出来。
        using var dir = new TempDir();
        var labels = new SpyLabelStore();
        var index = new RecordingIndexSpy();
        await using var coordinator = Build(dir, WorkMode.Continuous, new FakePunchLog(), index,
            labels: labels);

        // 第 1 件：发货。
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        // 换件前切成退货……但 `B` 这一段的档要等下面那次扫码才被读走，
        // 所以这里换的顺序是：先切档，再扫下一件。
        await coordinator.SubmitAsync(WaybillNumber.Parse(ScanCommand.SwitchToReturn),
            PunchSource.KeyboardScanner);

        // 第 2 件：退货；同时第 1 件在后台收尾。
        await coordinator.SubmitAsync(B, PunchSource.KeyboardScanner);
        await coordinator.PendingFinalization;

        // 两段各自的标签：A 是发货，B 是退货。
        // ⚠️ 全部写完要等 B 也收尾 —— 那要等 B 自己停。
        await coordinator.StopWorkAsync();

        var byEvidence = labels.Written.ToDictionary(l => l.EvidenceId, l => l.Value);

        Assert.Equal(2, byEvidence.Count);
        Assert.Contains(BusinessTypes.OutboundValue, byEvidence.Values);
        Assert.Contains(BusinessTypes.ReturnValue, byEvidence.Values);
    }

    // ─────────────────────────────────────────────
    // 测试脚手架
    // ─────────────────────────────────────────────

    /// <summary>记下每一次写标签（证据 id / 键 / 值）。</summary>
    private sealed class SpyLabelStore : ILabelStore
    {
        public List<(string EvidenceId, string Key, string Value)> Written { get; } = [];

        public Task SetAsync(
            string evidenceId, string key, string value, CancellationToken cancellationToken = default)
        {
            Written.Add((evidenceId, key, value));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetForEvidenceAsync(
            string evidenceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(
                new Dictionary<string, IReadOnlyDictionary<string, string>>());
    }

    // ─────────────────────────────────────────────
    // 相机让路（配置向导那几步要开相机）
    // ─────────────────────────────────────────────

    /// <summary>
    /// <c>ResumePrerecordAsync</c> 在**录着的时候不接相机**，没在录时才接。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>为什么要有这条</b>：相机独占，录着的时候它在采集进程手里 ——
    /// 这时去起待扫会拿到 <c>device already in use</c>，而用户看到的是一句
    /// 看不懂的错。所以接回来这件事有个闸，闸的判据是「相机现在是不是空的」。
    /// </para>
    /// <para>
    /// ⚠️ <b>判据用 <c>Failed</c> 事件当计数器</b>：<c>StartAsync</c> 把「起不来」
    /// 变成一条日志与 <c>Failed</c>（I3），不抛。给一个**根本不存在**的 exe，
    /// 这个路径就必然走到 <c>Failed</c> —— 于是「有没有真去起」这件事
    /// 从外面看得见。<see cref="PrerecordController"/> 是具体类（不是接口），
    /// 没有比这更省事的观察点。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 录着的时候不把相机接回来_没在录时才接()
    {
        using var dir = new TempDir();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog());

        var attempts = 0;
        var prerecord = new PrerecordController(
            "不存在的-ffmpeg.exe",
            CameraSource.Network("rtsp://192.0.2.1:1/x"),
            decoder: null,
            NullLogger.Instance,
            new SystemProcessRunner());

        prerecord.Failed += _ => Interlocked.Increment(ref attempts);

        coordinator.Prerecord = prerecord;
        coordinator.StartWork();

        await Task.Delay(200);
        Assert.Equal(1, attempts);

        // 没在录 —— 会去起（这一条是**反证**：没有它，下面那条断言
        // 在「这个方法永远什么都不做」的实现下也会绿）。
        await coordinator.ResumePrerecordAsync();
        await Task.Delay(200);
        Assert.Equal(2, attempts);

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        Assert.NotNull(coordinator.CurrentWaybill);

        // 录着 —— 不接。
        await coordinator.ResumePrerecordAsync();
        await Task.Delay(200);
        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// 停一段时**先把待扫接回来，再收尾** —— 收尾那几秒不许把扫码空着。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>为什么要这条</b>：收尾要 remux ＋ 解码校验，几秒起步。等它跑完才接待扫的话，
    /// 那几秒里下一件包裹扫不进来 —— 用户看到的是「扫码枪/摄像头坏了」，
    /// 而实际只是我们自己在收尾。顺序照 <c>SwitchSegmentAsync</c>（换件那条路）
    /// 已验证过的那一套：**放设备 → 起待扫 → 收尾**。
    /// </para>
    /// <para>
    /// ⚠️ <b>判据是「收尾还卡着的时候待扫已经起过一次」</b>，不是「先后两次都发生了」——
    /// 后者在「先收尾、后起待扫」那种实现下**照样绿**。收尾卡住这个人造点由
    /// <see cref="BlockingRunner"/> 提供（收尾里那两条 ffmpeg 命令都走它）。
    /// 待扫有没有真去起，用 <c>Failed</c> 当计数器（同上一条的理由：
    /// <c>PrerecordController</c> 是具体类，没有更省事的观察点）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 停一段时先把待扫接回来_而不是等收尾跑完()
    {
        using var dir = new TempDir();
        var gate = new BlockingRunner();

        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), runner: gate);

        var attempts = 0;
        var prerecord = new PrerecordController(
            "不存在的-ffmpeg.exe",
            CameraSource.Network("rtsp://192.0.2.1:1/x"),
            decoder: null,
            NullLogger.Instance,
            new SystemProcessRunner());

        prerecord.Failed += _ => Interlocked.Increment(ref attempts);
        coordinator.Prerecord = prerecord;

        coordinator.StartWork();
        await WaitUntilAsync(() => attempts == 1);   // 开始工作那一次

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await WaitUntilAsync(() => coordinator.CurrentWaybill is not null);

        // 复扫同码 ⇒ 停这一段。**不等它** —— 要看的正是「它还没跑完」的那一刻。
        var stopping = coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        try
        {
            await WaitUntilAsync(() => attempts == 2);

            Assert.False(
                stopping.IsCompleted,
                "等到收尾跑完才接的待扫 —— 那几秒正是要省掉的那几秒");
        }
        finally
        {
            gate.Release();
        }

        await stopping;
    }

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
        int duplicateCheckDays = 0,
        ICameraCapture? capture = null,
        ILabelStore? labels = null,
        IAppLogger? logger = null)
    {
        var ffmpeg = FfmpegLocator.TryFind() ?? "ffmpeg";
        var effectiveRunner = runner ?? new SucceedingRunner();

        return new RecordingCoordinator(
            new RecordingWorkspace(dir.WorkspaceRoot),
            capture ?? new FakeCapture(),
            new SessionFinalizer(
                new RemuxPipeline(ffmpeg, effectiveRunner),
                new DecodeVerifier(ffmpeg, effectiveRunner),
                index ?? new RecordingIndexSpy(),
                Path.Combine(dir.Path, "archive"),
                logger: null,
                relay: null,
                labels: labels),
            new DiskSpaceGuard(new PlentyOfSpaceProbe()),
            punches,
            logger ?? NullLogger.Instance,
            policy ?? new WorkModePolicy(mode, IdleReminderOption.Off),
            new CoordinatorOptions(
                CameraSource.Local("Lenovo EasyCamera"), "device-1", "libx264", duplicateCheckDays),
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

    /// <summary>
    /// 把日志收起来，好在测试里断言（本仓既有写法，见 `LiveDirectoryTests.CapturingLogger`）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>数据那一栏也折进同一行</b>（`键=值`）：重复单号**录过几次**只在
    /// `data["次数"]` 里 —— 消息本身按需求方 2026-10-10 改短之后只剩单号。
    /// 不折进来的话，「同一次会话分成多段也只算一次」这条用例就**没有观察点**了。
    /// </remarks>
    private sealed class CapturingLogger : IAppLogger
    {
        public List<string> Lines { get; } = [];

        public void Log(LogLevel level, string category, string message) => Lines.Add(message);

        public void Log(
            LogLevel level, string category, string message,
            IReadOnlyDictionary<string, object?> data) =>
            Lines.Add(message + " " + string.Join(
                " ", data.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    private sealed class FakeCapture : ICameraCapture
    {
        public Task<ICaptureProcess> StartAsync(
            CameraSource source, string outputPath, string encoder, string? microphone = null,
            CancellationToken cancellationToken = default,
            int? segmentSeconds = null, int segmentStartNumber = 0) =>
            Task.FromResult<ICaptureProcess>(new FakeProcess(outputPath, segmentStartNumber));
    }

    /// <summary>记下「被叫开录时用的是什么编码器」的假采集。</summary>
    private sealed class SpyCapture : ICameraCapture
    {
        public List<string> Encoders { get; } = [];

        /// <summary>
        /// 每次开录收到的分片参数。<c>SegmentSeconds</c> 为 <see langword="null"/> ⇒
        /// 没开分片（整场一个文件）。
        /// </summary>
        /// <remarks>
        /// T17 起「一段一路采集进程」没了（见 <c>RecordingCoordinatorTests.分段交给ffmpeg自己滚</c>），
        /// 「有没有把滚段交给 ffmpeg」就只能在这儿看。
        /// </remarks>
        public List<(int? SegmentSeconds, int StartNumber, string OutputPath)> Starts { get; } = [];

        public Action? OnStart { get; set; }

        public Task<ICaptureProcess> StartAsync(
            CameraSource source, string outputPath, string encoder, string? microphone = null,
            CancellationToken cancellationToken = default,
            int? segmentSeconds = null, int segmentStartNumber = 0)
        {
            Encoders.Add(encoder);
            Starts.Add((segmentSeconds, segmentStartNumber, outputPath));
            OnStart?.Invoke();
            return Task.FromResult<ICaptureProcess>(new FakeProcess(outputPath, segmentStartNumber));
        }
    }

    /// <param name="startNumber">
    /// 滚段那一趟（T17）`outputPath` 是模式，产物要按起始号落成具体文件。
    /// </param>
    private sealed class FakeProcess(string outputPath, int startNumber = 0) : ICaptureProcess
    {
        public string? StartupWarning => null;

        public Task<int?> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            File.WriteAllText(
                outputPath.Replace(
                    "%03d",
                    startNumber.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal),
                "captured");

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

    /// <summary>
    /// 收尾那两条 ffmpeg 命令会走它，而它卡在闸上 ——
    /// 「收尾还没跑完」因此变成一个**可以从外面断言**的状态（见上一条用例）。
    /// </summary>
    private sealed class BlockingRunner : IProcessRunner
    {
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async Task<ProcessResult> RunAsync(
            string executable, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            await _gate.Task.WaitAsync(cancellationToken);

            var args = arguments.ToList();
            var yIndex = args.IndexOf("-y");
            if (yIndex >= 0 && yIndex + 1 < args.Count)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(args[yIndex + 1])!);
                File.WriteAllText(args[yIndex + 1], "published");
            }

            return new ProcessResult(0, string.Empty, string.Empty);
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
