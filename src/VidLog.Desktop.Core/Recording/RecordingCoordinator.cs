using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Punches;

namespace VidLog.Desktop.Core.Recording;

/// <summary>协调器要告诉用户的事。</summary>
/// <param name="Kind">事件种类。</param>
/// <param name="Waybill">相关单号（有的话）。</param>
/// <param name="Message">给用户看的说明。</param>
public sealed record CoordinatorNotice(CoordinatorNoticeKind Kind, WaybillNumber? Waybill, string Message);

public enum CoordinatorNoticeKind
{
    /// <summary>
    /// 规格 §3.3.3：连续 N 分钟没有扫码或打点。<b>只提醒，不改录制状态。</b>
    /// </summary>
    Idle,

    /// <summary>开始工作了。</summary>
    WorkStarted,

    /// <summary>结束工作。</summary>
    WorkStopped,

    /// <summary>以这个单号开录了。</summary>
    SegmentStarted,

    /// <summary>这一段收了。</summary>
    SegmentStopped,

    /// <summary>换件（连续扫）—— **正常路径**，不是错误。</summary>
    SwitchedWaybill,

    /// <summary>错码保护：扫到不同单号，不停录，仅提示（规格 §3.3.2）。</summary>
    WrongWaybill,

    /// <summary>收尾失败 —— 必须让用户看见（I3）。</summary>
    FinalizeFailed,
}

/// <summary>协调器的可调参数。</summary>
public sealed record CoordinatorOptions(
    string DeviceName,
    string SourceDeviceId,
    string Encoder);

/// <summary>
/// 一次「工作」的编排 —— 规格 §3.3.1 的三种工作模式都落在这里。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="RecordingSession"/> 的分工：那个管**一段**（起采集、滚段、收尾），
/// 这个管**一次工作**（按工作模式反复开关若干段、换件换单号、打点落盘）。
/// </para>
/// <para>
/// 之所以必须单独有一层：三种模式都要求「一次工作里开很多段」，
/// 而会话是一次性的 —— 把这段编排写在窗口里就永远测不到。
/// </para>
/// </remarks>
public sealed class RecordingCoordinator : IAsyncDisposable
{
    private readonly RecordingWorkspace _workspace;
    private readonly ICameraCapture _capture;
    private readonly SessionFinalizer _finalizer;
    private readonly DiskSpaceGuard _diskGuard;
    private readonly IPunchLog _punches;
    private readonly IAppLogger _logger;
    private readonly WorkModePolicy _policy;
    private readonly CoordinatorOptions _options;

    /// <summary>
    /// 错误扫描记录（规格 §6.1「必须保存的事实」里的那一条）。
    /// </summary>
    /// <remarks>
    /// 可空：它是一条**诊断**记录，缺了不影响录制 ——
    /// 所以那些不关心它的装配（以及测试）不必硬塞一个。
    /// </remarks>
    private readonly ScanErrorLog? _scanErrors;

    private RecordingSession? _current;

    /// <summary>闲置提醒的轮询间隔。1 秒：它只是「到点了没有」，密一点没有意义。</summary>
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(1);

    /// <summary>单调时钟。闲置计时与 I11 同一条精神 —— 不读墙钟。</summary>
    private readonly Func<TimeSpan> _clock;

    /// <summary>等待。抽成可注入的，测试才不必真的等两分钟。</summary>
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>
    /// 最后一次活动（扫码 / 打点 / **开一段**）的单调刻度。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>「开录那一刻」也算一次活动</b> —— 这不是顺手写的，是规格 §3.3.3 那句
    /// 「闲置计时**从开录那一刻起算**，不能拿『上次扫码到现在』当起点，
    /// 否则架机半小时后一开录就会立刻被提醒」的落点。
    /// <para>
    /// 因为**开一段只可能由一次扫码触发**（<see cref="SubmitAsync"/> 与其中的换件），
    /// 这条不变式是结构性成立的：开录那一刻必然刚更新过它。
    /// 所以这里**不需要**再拿「本段已录时长」去封顶 —— 试过那样写：
    /// 既永远不生效，又会让功能在假时钟下永远不触发。
    /// 谁要是新加了一条「不经过扫码就开段」的路径，**那就是这条不变式破的时候**。
    /// </para>
    /// </remarks>
    private TimeSpan _lastActivityAt;

    /// <summary>这一段闲置里已经响过提醒了没有。</summary>
    private bool _idleReminded;

    /// <summary>闲置看门狗的取消源。</summary>
    private CancellationTokenSource? _idleWatch;

    public RecordingCoordinator(
        RecordingWorkspace workspace,
        ICameraCapture capture,
        SessionFinalizer finalizer,
        DiskSpaceGuard diskGuard,
        IPunchLog punches,
        IAppLogger logger,
        WorkModePolicy policy,
        CoordinatorOptions options,
        ScanErrorLog? scanErrors = null,
        Func<TimeSpan>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ITrustedClock? trustedClock = null)
    {
        _clock = clock ?? NewDefaultClock();
        _delay = delay ?? Task.Delay;
        _workspace = workspace;
        _capture = capture;
        _finalizer = finalizer;
        _diskGuard = diskGuard;
        _punches = punches;
        _logger = logger;
        _policy = policy;
        _options = options;
        _scanErrors = scanErrors;
        _trustedClock = trustedClock;
    }

    /// <summary>
    /// 可信时钟（规格 §3.6.4）。<see langword="null"/> = 不设闸。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不传的那些装配（大多是测试）**行为与从前完全一致** ——
    /// 加闸不该顺带把几十条不相干的用例改成「必先校准」。
    /// 生产路径由 `DesktopServices` 传真的那个。
    /// </remarks>
    private readonly ITrustedClock? _trustedClock;

    /// <summary>默认时钟：一个从构造时开始走的秒表。</summary>
    /// <remarks>
    /// 与 <see cref="RecordingSession"/> 的默认时钟同一手法 —— 用单调时钟而不是
    /// <c>DateTimeOffset.UtcNow</c>：闲置计时不该因为用户改了系统时间而跳（I11 的精神）。
    /// </remarks>
    private static Func<TimeSpan> NewDefaultClock()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        return () => stopwatch.Elapsed;
    }

    /// <summary>是否处于「工作中」（规格 §3.3.1 的用词）。</summary>
    public bool IsWorking { get; private set; }

    /// <summary>
    /// 取景识码（规格 §3.2.1 的第二种识别入口）。
    /// </summary>
    /// <remarks>
    /// 可以为 null —— 没摄像头、或没配解码器时就不装它。
    /// 装上了的话，<see cref="StartWork"/> 会开始取景，扫到单号自动开录。
    /// </remarks>
    public Camera.CameraFrameScanner? Scanner { get; set; }

    /// <summary>
    /// 会话参数：分段时长与时长兜底（规格 §3.1.1 / §3.3.4）。
    /// </summary>
    /// <remarks>
    /// 装配层在启动时按设置填一次，用户改设置时再填一次 —— 取值发生在
    /// <b>每次开新段</b>那一刻，所以改动是「下次录段生效」，
    /// 与界面上那句说明一致，不需要重启。
    /// </remarks>
    public RecordingSessionOptions SessionOptions { get; set; } = RecordingSessionOptions.Default;

    /// <summary>
    /// 工作模式（规格 §3.3.1）。改了**立刻算数**，不需要重启。
    /// </summary>
    /// <remarks>
    /// 写穿到策略上。设置页写着「下次录段生效」，而只有把新值送到策略里才是真的。
    /// </remarks>
    public WorkMode Mode
    {
        get => _policy.Mode;
        set => _policy.Mode = value;
    }

    /// <summary>闲置提醒档位（规格 §3.3.3 电脑端那半）。</summary>
    /// <remarks>
    /// ⚠️ 改它会**立刻**决定看门狗跑不跑（设置页写着「立即生效」，那就得是真的）：
    /// 关掉时停掉看门狗，打开时（且正在工作）起一个。
    /// </remarks>
    public IdleReminderOption IdleReminder
    {
        get => _policy.IdleReminder;
        set
        {
            _policy.IdleReminder = value;
            SyncIdleWatch();
        }
    }

    /// <summary>闲置提醒「自定义」档的分钟数。</summary>
    public int IdleReminderMinutes
    {
        get => _policy.IdleReminderMinutes;
        set
        {
            _policy.IdleReminderMinutes = value;
            SyncIdleWatch();
        }
    }

    /// <summary>当前段的单号；没有在录时为 <see langword="null"/>。</summary>
    public WaybillNumber? CurrentWaybill => _current?.Waybill;

    /// <summary>当前段的已录时长；没有在录时为零。</summary>
    public TimeSpan Elapsed => _current?.Elapsed ?? TimeSpan.Zero;

    /// <summary>当前段的会话标识；没有在录时为 <see langword="null"/>。</summary>
    public string? CurrentSessionId => _current?.SessionId;

    public event Action<CoordinatorNotice>? Notice;

    /// <summary>
    /// 后台收尾的任务（换件时旧段）。
    /// </summary>
    /// <remarks>
    /// 换件不做后台收尾的话，收尾那几秒会把下一段的开头吃掉。
    /// 但「什么时候收完」总得有人能等 —— 界面要显示、测试要断言、
    /// 关程序时要等干净。所以把最后一个后台收尾暴露出来。
    /// </remarks>
    public Task PendingFinalization { get; private set; } = Task.CompletedTask;

    /// <summary>【开始工作】。这一段工作从这里起算。</summary>
    public void StartWork()
    {
        if (IsWorking)
        {
            return;
        }

        // ⚠️ **未校准不得开始录制**（规格 §3.6.4）。
        //
        // 这道闸在这里、以及 `RecordingSession.StartAsync` 里各有一道：
        // 前者是为了**当场告诉用户**（不然他只会看到「点了没反应」），
        // 后者是为了**绕不过去**（任何直接建会话的路径都被挡住）。
        if (_trustedClock is { IsCalibrated: false } clock)
        {
            var reason = clock.BlockedReason ?? "这台电脑还没有过一次可信的时间校准。";

            _logger.Log(LogLevel.Warn, "校时", $"拒绝开始工作：{reason}");
            Raise(CoordinatorNoticeKind.FinalizeFailed, null, reason);
            return;
        }

        BeginWorking();

        _logger.Log(LogLevel.Info, "工作", "开始工作");
        Raise(CoordinatorNoticeKind.WorkStarted, null, "开始工作。");

        // 开始取景识码 —— 扫到单号会自动开录（规格 §4.1 的状态机就是从
        // 「识别到单号」起算的）。没装扫描器时用户仍可手打单号。
        if (Scanner is not null)
        {
            _ = Scanner.StartAsync();
        }
    }

    /// <summary>
    /// 盯着「连续 N 分钟没有任何扫码或打点」（规格 §3.3.3 电脑端那半）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 它**只播报，绝不改录制状态** —— 规格原话「用户不理就一直录」。
    /// 所以这个循环里没有任何 <c>Stop</c>：真正把录制停掉的是时长兜底（§3.3.4）。
    /// </para>
    /// <para>
    /// ⚠️ 判据是「扫码或打点」，**不是画面**：电脑端那条路已被实测否决
    /// （录制中相机独占，取不到画面，见 <c>docs/实现决策.md</c> §24/§25）。
    /// 语义因此与「画面静止」不同，规格里写明并接受了这一点。
    /// </para>
    /// <para>
    /// 一个闲置段里**只响一次**（<c>_idleReminded</c>）：规格没写「多久响一次」，
    /// 而验收只说「等过档位 → 提醒，但仍在录」。一次比反复响更接近那句。
    /// </para>
    /// </remarks>
    private async Task WatchIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _delay(IdlePollInterval, cancellationToken);
                if (cancellationToken.IsCancellationRequested || _idleReminded)
                {
                    continue;
                }

                var state = Snapshot();
                if (_policy.OnIdle(state) is not WorkDecision.Announce { Kind: AnnouncementKind.Idle })
                {
                    continue;
                }

                _idleReminded = true;

                var minutes = IdleReminder.Minutes(IdleReminderMinutes) ?? 0;
                _logger.Log(LogLevel.Info, "工作", "闲置提醒", new Dictionary<string, object?>
                {
                    ["分钟"] = minutes,
                    ["会话"] = _current?.SessionId,
                });

                Raise(CoordinatorNoticeKind.Idle, state.Current,
                    $"已经 {minutes} 分钟没有扫码或打点了。还在录 —— 要停就扫同一张面单，或者点【结束】。");
            }
        }
        catch (OperationCanceledException)
        {
            // 结束工作 / 退出 —— 正常收场。
        }
        catch (Exception ex)
        {
            // 看门狗自己出问题**绝不能把录制带下去**（I4 的同一条精神）。
            _logger.Log(LogLevel.Warn, "工作", $"闲置提醒的看门狗停了：{ex.Message}");
        }
    }

    /// <summary>
    /// 【结束】。停掉在录的段并收尾，回到空闲。
    /// </summary>
    public async Task<FinalizeOutcome?> StopWorkAsync(CancellationToken cancellationToken = default)
    {
        if (!IsWorking)
        {
            return null;
        }

        IsWorking = false;

        // 停掉闲置看门狗 —— 不结束工作的人不该继续收到提醒。
        StopIdleWatch();

        // 先停取景 —— 结束时不该把相机留着开着（隐私指示灯长亮）。
        if (Scanner is not null)
        {
            await Scanner.StopAsync(cancellationToken);
        }

        var outcome = await StopCurrentSegmentAsync(StopReason.Manual, cancellationToken);

        _logger.Log(LogLevel.Info, "工作", "结束工作");
        Raise(CoordinatorNoticeKind.WorkStopped, null, "已结束工作。");

        return outcome;
    }

    /// <summary>
    /// 识别到一个单号（扫码枪 / 摄像头 / 手动输入都走这里）。
    /// </summary>
    /// <remarks>
    /// 打点在这里落盘：规格 §3.2.4 要求**识别到单号即记录该时刻**，
    /// 且必须立即持久化（掉电会丢）。
    /// </remarks>
    public async Task SubmitAsync(
        WaybillNumber waybill,
        PunchSource source,
        CancellationToken cancellationToken = default)
    {
        var state = Snapshot();
        var decision = _policy.OnScan(state, waybill);

        // 一次扫码 = 一次活动：闲置计时从这里重算，已响过的提醒也解除。
        _lastActivityAt = _clock();
        _idleReminded = false;

        switch (decision)
        {
            case WorkDecision.StartSegment start:
                await StartSegmentAsync(start.Waybill, source, cancellationToken);
                break;

            case WorkDecision.SwitchTo next:
                await SwitchSegmentAsync(next.Next, source, cancellationToken);
                break;

            case WorkDecision.StopSegment stop:
                await StopCurrentSegmentAsync(stop.Reason, cancellationToken);
                break;

            case WorkDecision.Announce announce:
                await HandleAnnounceAsync(announce, cancellationToken);
                break;

            case WorkDecision.Nothing:
                break;
        }
    }

    private async Task StartSegmentAsync(
        WaybillNumber waybill, PunchSource source, CancellationToken cancellationToken)
    {
        // ⚠️ **未校准不得开始录制**（规格 §3.6.4）—— 这道闸**也必须在这里**。
        //
        // 只挡 `StartWork()` 是不够的：这个方法是「**没按开始就扫码**」那条路的
        // 落点（下面那一段就是为它写的），而扫码枪是硬件，它不等用户点按钮。
        // 少这一道的话，绕开闸门只需要「直接扫一张面单」——
        // 而规格对手机端明确要求「`startWorking()` **以及扫码开录那条路**」都有闸，
        // 两端同一个道理。
        if (_trustedClock is { IsCalibrated: false } clock)
        {
            var reason = clock.BlockedReason ?? "这台电脑还没有过一次可信的时间校准。";

            _logger.Log(LogLevel.Warn, "校时", $"拒绝开录（{waybill.Value}）：{reason}");
            Raise(CoordinatorNoticeKind.FinalizeFailed, waybill, reason);
            return;
        }

        // 扫码枪打进来时用户未必先点过【开始工作】（规格 §4.1 的状态机就是从
        // 「识别到单号」起算的）。这时也要把工作置为进行中 ——
        // 否则没有任何合法方式结束它：【结束】会因为「没在工作」直接返回，
        // 而那段录像**不进收尾**就没了（既不入库、也不写 finalized.json）。
        if (!IsWorking)
        {
            // ⚠️ 走 BeginWorking 而不是只把 IsWorking 置真：**闲置看门狗也得起来**
            // （这条路径是「没按开始就扫码」，用户同样需要那条提醒）。
            BeginWorking();
            Raise(CoordinatorNoticeKind.WorkStarted, null, "开始工作。");
        }

        // ⚠️ **必须先放掉取景识码进程**：相机是独占的（实测），
        // 识码进程还开着的话，下面的采集进程会拿到 device already in use。
        // StopAsync 会等到进程真的退出 —— 那正是为了让它把设备放开。
        if (Scanner is { IsScanning: true })
        {
            await Scanner.StopAsync(cancellationToken);
        }

        var session = new RecordingSession(
            _workspace, _capture, _finalizer, _diskGuard,
            _options.DeviceName, _options.SourceDeviceId, SessionOptions,
            trustedClock: _trustedClock);

        await session.StartAsync(waybill, _options.Encoder, cancellationToken);
        _current = session;

        // ── 起编排循环：到点滚段、到时长上限或磁盘将满就自动收尾 ──────────
        // ⚠️ 这一句以前漏了，后果比「档位没接线」严重得多：
        // 整场只会录一个**永不滚动**的分段，时长兜底永不触发 ——
        // 而规格 §3.1.1 要求连续分段（「长录不断、掉电不丢」：
        // 段不滚，进程被杀时 manifest 里就一段都没封闭，什么都恢复不出来）。
        // 界面上「分段时长」「时长兜底」两个档位改起来毫无反应，根子也在这里。
        _ = WatchSessionLoopAsync(session);

        await PunchAsync(session, waybill, source, cancellationToken);

        _logger.Log(LogLevel.Info, "录制", $"开录 {waybill.Value}",
            new Dictionary<string, object?> { ["会话"] = session.SessionId, ["来源"] = source });

        Raise(CoordinatorNoticeKind.SegmentStarted, waybill, $"开始录制 {waybill.Value}。");
    }

    /// <summary>
    /// 换件：放掉旧段的相机、**立刻**以新单号开新段，旧段在后台收尾。
    /// </summary>
    /// <remarks>
    /// 收尾要 remux + 解码校验，几秒起步；等它做完再开下一段，那几秒就白丢了。
    /// 所以顺序是「放设备 → 开新段 → 后台收旧段」。
    /// 相机是独占的（实测），所以**必须先放掉**，否则新段开不起来。
    /// <para>
    /// I9 不破：收尾仍然只有 <see cref="SessionFinalizer"/> 一条路，
    /// 这里只是把「放设备」那一步提前了。
    /// </para>
    /// </remarks>
    private async Task SwitchSegmentAsync(
        WaybillNumber next, PunchSource source, CancellationToken cancellationToken)
    {
        var previous = _current;
        if (previous is null)
        {
            await StartSegmentAsync(next, source, cancellationToken);
            return;
        }

        await previous.ReleaseCaptureAsync(cancellationToken);
        _current = null;

        await StartSegmentAsync(next, source, cancellationToken);

        // 旧段在后台收尾 —— 它的结论只影响提示，不影响新段的开始。
        var finishing = previous;
        PendingFinalization = Task.Run(async () =>
        {
            try
            {
                var outcome = await finishing.StopAsync(StopReason.WaybillChanged);
                ReportFinalize(finishing, outcome);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, "录制", $"换件后旧段收尾出错：{ex.Message}");
            }
            finally
            {
                await finishing.DisposeAsync();
            }
        });

        Raise(CoordinatorNoticeKind.SwitchedWaybill, next, $"换件，开始录制 {next.Value}。");
    }

    private async Task<FinalizeOutcome?> StopCurrentSegmentAsync(
        StopReason reason, CancellationToken cancellationToken)
    {
        var session = _current;
        if (session is null)
        {
            return null;
        }

        _current = null;

        var outcome = await session.StopAsync(reason, cancellationToken);
        ReportFinalize(session, outcome);
        await session.DisposeAsync();

        // 相机随收尾释放了 —— 还在工作中的话要把取景接回去，
        // 否则下一件包裹扫不进来（用户会以为扫码枪/摄像头坏了）。
        if (IsWorking && Scanner is not null)
        {
            _ = Scanner.StartAsync();
        }

        return outcome;
    }

    /// <summary>
    /// 盯住会话的编排循环 —— 它**自己**收尾时（时长兜底 / 磁盘将满）把它接回来。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 循环是 fire-and-forget 的，没人知道它什么时候自己停了。不盯的话
    /// <see cref="_current"/> 会一直指着一个早已收尾的会话：界面显示「录制中」，
    /// 而实际早停了 —— 用户会一直等一个永远不会发生的停录，也不会去点【结束】。
    /// </para>
    /// <para>
    /// 用 <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> 认领而不是先读后写：
    /// 用户点【结束】或换件时，另一个线程也会把 <see cref="_current"/> 清掉／换成新会话，
    /// 先读后写会把这个新会话误清成 null（表现是那一件包裹再也不会停录）。
    /// </para>
    /// </remarks>
    private async Task WatchSessionLoopAsync(RecordingSession session)
    {
        try
        {
            await session.RunAsync(_options.Encoder).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // RunAsync 自己保证不抛（异常都变成收尾结论）。这里是最后一道兜底，
            // 漏出去就是未处理异常 —— 而用户需要的是一条看得见的说明（I3）。
            _logger.Log(LogLevel.Error, "录制", $"编排循环异常退出：{ex.Message}");
        }

        // 是我们主动停的（结束工作 / 换件 / 复扫同码）就到此为止 ——
        // 那几条路自己会收尾、自己会报告，这里再报一次就是重复。
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _current, null, session), session))
        {
            return;
        }

        if (session.Outcome is { } outcome)
        {
            ReportFinalize(session, outcome);
        }

        // 自己停了而用户没点过任何东西 —— 必须说出来。录像已经在库里了，
        // 而界面还停在「录制中」的话，他会一直等下去。
        Raise(CoordinatorNoticeKind.WorkStopped, session.Waybill, DescribeAutoStop(session.StoppedBecause));

        await session.DisposeAsync();

        // 还在工作中的话把取景接回去，否则下一件包裹扫不进来
        // （与 StopCurrentSegmentAsync 同一条理由）。
        if (IsWorking && Scanner is not null)
        {
            _ = Scanner.StartAsync();
        }
    }

    private static string DescribeAutoStop(StopReason? reason) => reason switch
    {
        StopReason.DurationFallback => "到了时长上限，已自动结束并收尾。",
        StopReason.StorageLow => "磁盘将满，已自动结束并收尾。",
        _ => "已自动结束并收尾。",
    };

    private void ReportFinalize(RecordingSession session, FinalizeOutcome outcome)
    {
        if (outcome.Succeeded)
        {
            _logger.Log(LogLevel.Info, "录制", $"已入库 {outcome.Segments.Count} 段",
                new Dictionary<string, object?>
                {
                    ["会话"] = session.SessionId,
                    ["停因"] = outcome.Reason,
                });
            return;
        }

        // I3：收尾失败必须让用户看见，而且要说明**东西还在**，
        // 否则用户会以为录像丢了。
        var message = $"收尾失败：{outcome.FailureReason} 录像仍在工作区，下次启动会自动重试。";
        _logger.Log(LogLevel.Error, "录制", message,
            new Dictionary<string, object?> { ["会话"] = session.SessionId });

        Raise(CoordinatorNoticeKind.FinalizeFailed, session.Waybill, message);
    }

    /// <summary>
    /// 提示类决策（规格 §3.3.2）。错码保护那一条**同时要落一条记录**。
    /// </summary>
    /// <remarks>
    /// 规格 §6.1「必须保存的事实」里点名要有「错误扫描（错码保护触发的事件，诊断用）」，
    /// 而 2026-09-26 之前**两端都没实现** —— 错码保护只做了界面提示与播报，
    /// 事后查不出操作员那一刻扫到了什么。
    /// </remarks>
    private async Task HandleAnnounceAsync(
        WorkDecision.Announce announce, CancellationToken cancellationToken)
    {
        switch (announce.Kind)
        {
            case AnnouncementKind.WrongWaybill:
                // 规格 §3.3.2 的措辞。
                Raise(CoordinatorNoticeKind.WrongWaybill, announce.Waybill,
                    "面单错误，请扫描正确面单");

                // ⚠️ 写失败**不能把这一下带下去**：本表按规格「不是控制流的输入」，
                // 而这一下正在**录着像**。诊断记录丢了是小事，
                // 让错码保护这条路径整个失败是大事（`RecordingCoordinatorTests`
                // 里有一条专门盯着「错码保护这条路径整个失败」的用例）。
                // 「扫到的那个单号」在这条决策里是可空的（类型上没有更强的保证）——
                // 拿不到就不记，**不编一个**：一条编出来的诊断记录比没有更坏。
                if (_scanErrors is not null
                    && _current?.Waybill is { } expected
                    && announce.Waybill is { } scanned)
                {
                    try
                    {
                        await _scanErrors.AppendAsync(
                            new ScanErrorEvent(
                                _current.SessionId, expected, scanned, DateTimeOffset.UtcNow),
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.Log(LogLevel.Warn, "扫码", "错误扫描记录写不下去",
                            new Dictionary<string, object?> { ["原因"] = ex.Message });
                    }
                }

                break;

            case AnnouncementKind.SwitchedWaybill:
                break;

            case AnnouncementKind.Idle:
                // ⚠️ 这里**不该**走到：闲置提醒由看门狗直接 Raise（它没有「扫码事件」可依附）。
                // 留着这一支是因为枚举上加了成员就必须处理 —— 空着比编一句话好。
                break;
        }
    }

    /// <summary>进入「工作中」：置标志、重置闲置计时、起看门狗。</summary>
    /// <remarks>
    /// 两个入口都用它：【开始工作】按钮，以及「没按开始就扫码」（规格 §4.1 的状态机
    /// 从「识别到单号」起算）。后者漏了这一段的话，那条路上**永远不会有闲置提醒**。
    /// </remarks>
    private void BeginWorking()
    {
        IsWorking = true;

        // 闲置计时从这里起算（它更常被「每一次扫码」推后，见 SubmitAsync）。
        _lastActivityAt = _clock();
        _idleReminded = false;

        SyncIdleWatch();
    }

    /// <summary>让看门狗的存在状态与档位一致：档位是「关闭」就**根本不起它**。</summary>
    /// <remarks>
    /// <para>
    /// 不只是省一点：一个每秒醒一次的循环，即使每次都得出「什么也不做」，
    /// 也是**每个协调器一个定时器**。测试里那两个入口（<c>Build</c> 造了几十个协调器）
    /// 全都用「关闭」，于是几十个定时器白跑 —— 而并行跑的全套里，
    /// 这种白跑会**把既有的时序敏感用例的窗口撑大**（实测：加上它之后
    /// `RecordingCoordinatorTests` 里那条 §30 的 flake 从 1/11 变成 2/6）。
    /// </para>
    /// <para>
    /// 「关闭」的语义本来就该是**连看门狗都不跑**，而不是跑着然后每次都说不用提醒。
    /// </para>
    /// </remarks>
    private void SyncIdleWatch()
    {
        var wanted = IdleReminder.Minutes(IdleReminderMinutes) is not null;

        if (!wanted)
        {
            StopIdleWatch();
            return;
        }

        if (!IsWorking || _idleWatch is not null)
        {
            return;
        }

        _idleWatch = new CancellationTokenSource();
        _ = WatchIdleAsync(_idleWatch.Token);
    }

    /// <summary>停掉闲置看门狗（结束工作、退出）。幂等。</summary>
    private void StopIdleWatch()
    {
        var watch = _idleWatch;
        _idleWatch = null;

        if (watch is null)
        {
            return;
        }

        try
        {
            watch.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经收过了。
        }
    }

    /// <summary>打点落盘。</summary>
    /// <remarks>
    /// 规格 §3.2.4：识别到单号 → 记录该**时刻**与该单号的关联，且立即持久化。
    /// 偏移用**会话已录毫秒**（单调），回放定位靠它 —— 墙钟只用于呈现。
    /// </remarks>
    private async Task PunchAsync(
        RecordingSession session, WaybillNumber waybill, PunchSource source,
        CancellationToken cancellationToken)
    {
        var punch = new Punch(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            waybill,
            DateTimeOffset.UtcNow,
            (long)session.Elapsed.TotalMilliseconds,
            source);

        await _punches.AppendAsync(punch, cancellationToken);
    }

    private WorkModeState Snapshot() =>
        _current is null
            ? WorkModeState.Idle
            : new WorkModeState(
                SegmentOpen: true,
                Current: _current.Waybill,
                IdleFor: _clock() - _lastActivityAt);

    private void Raise(CoordinatorNoticeKind kind, WaybillNumber? waybill, string message) =>
        Notice?.Invoke(new CoordinatorNotice(kind, waybill, message));

    public async ValueTask DisposeAsync()
    {
        StopIdleWatch();

        var session = _current;
        _current = null;

        if (session is not null)
        {
            // 不在这里收尾 —— 那是 StopWorkAsync 的事。这里只保证相机被放掉，
            // 否则会留一个占着分片文件的孤儿 ffmpeg（破坏 I9）。
            await session.DisposeAsync();
        }
    }
}
