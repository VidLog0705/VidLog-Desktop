using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Recording;

/// <summary>采集会话的可调参数。</summary>
/// <param name="SegmentDuration">单段时长上限。到点就滚下一段，用户无感（规格 §3.1.1）。</param>
/// <param name="MaxDuration">
/// 时长兜底的**首次询问时机**（规格 §3.3.4）。⚠️ <b>不是「到点就停」</b> ——
/// 到点是**问**，用户答了才停。见 <see cref="PromptGrace"/>。
/// </param>
/// <param name="StopGracePeriod">优雅停止的等待上限；超时升级为强杀。</param>
/// <param name="PollInterval">编排循环的检查间隔。</param>
/// <param name="PromptRepeatEvery">
/// 用户点了【继续】之后，隔多久**再问一次**（规格 §3.3.4「进入下一轮」）。
/// </param>
/// <param name="PromptGrace">
/// 问了之后多久没人理 ⇒ 视为**用户不在场** ⇒ 按兜底停止
/// （规格 §3.3.4「1 分钟无操作 → 默认继续 → 到 5 分钟自动停止」）。
/// </param>
public sealed record RecordingSessionOptions(
    TimeSpan SegmentDuration,
    TimeSpan MaxDuration,
    TimeSpan StopGracePeriod,
    TimeSpan PollInterval,
    // 追加字段（规格 §3.1.7 的连带项：索引要记编码 / 分辨率）。
    // 可空 ⇒ 老调用点与老条目都不受影响。
    Media.RecordingSpec? Spec = null,
    // 追加字段（规格 §3.3.4 的询问-宽限-循环）。
    // ⚠️ 默认值与手机端 `RecorderConfig` **逐字同值**（5 分钟 / 1 分钟）——
    // 两端对这条的行为必须一样，见 `From` 的说明。
    TimeSpan? PromptRepeatEvery = null,
    TimeSpan? PromptGrace = null)
{
    /// <summary>点了【继续】之后隔多久再问（默认 5 分钟，与手机端同值）。</summary>
    public TimeSpan PromptRepeat => PromptRepeatEvery ?? TimeSpan.FromMinutes(5);

    /// <summary>问了之后多久没操作就算用户不在场（默认 1 分钟，与手机端同值）。</summary>
    public TimeSpan Grace => PromptGrace ?? TimeSpan.FromMinutes(1);

    /// <summary>
    /// 默认值。
    /// </summary>
    /// <remarks>
    /// 分段定为 <b>1 分钟</b>，与手机端取齐：手机端在 M4 验收时从 5 分钟改成了 1 分钟，
    /// 原因是「录 1 分多钟时一段都还没封闭，杀进程后什么都恢复不出来」——
    /// 同样的推理在电脑端一字不差地成立。
    /// 时长兜底 30 分钟是**防忘停录**的上限，不是主要路径。
    /// </remarks>
    public static RecordingSessionOptions Default { get; } = new(
        SegmentDuration: TimeSpan.FromMinutes(1),
        MaxDuration: TimeSpan.FromMinutes(30),
        StopGracePeriod: TimeSpan.FromSeconds(15),
        PollInterval: TimeSpan.FromMilliseconds(500));

    /// <summary>
    /// 「时长兜底」档位设成「关闭」时的取值。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="TimeSpan.MaxValue"/> 表示「永远比不到」。
    /// <para>
    /// ⚠️ <b>2026-09-27 起它只是「档位是关闭」的标记</b>，而不再靠「比不到」生效 ——
    /// 时长兜底改成「到点先问」之后，循环里的判据变成了可空的
    /// <c>_nextPromptAt</c>（<c>null</c> = 关闭），这个常量由
    /// <c>RestartClock</c> 翻译成那个 <c>null</c>。
    /// 保留它是因为 <see cref="From"/> 与设置层都拿它当「关闭」的值。
    /// </para>
    /// </remarks>
    public static TimeSpan NoFallback { get; } = TimeSpan.MaxValue;

    /// <summary>
    /// 由设置构造（分段时长 = 界面上那个 1~10 分钟；时长兜底 = 档位）。
    /// </summary>
    /// <remarks>
    /// 映射只此一处 —— 写两遍就会有一天两边不一致，而不一致的表现是
    /// 「界面上写着 5 分钟，实际按 1 分钟录」，极难被发现。
    /// </remarks>
    public static RecordingSessionOptions From(int segmentMinutes, DurationFallbackOption durationFallback)
    {
        // 越界的值不抛：设置文件是可以被手改的，而一个改坏了的配置
        // 不该让用户**录不了像**（I4 的同一条精神）。夹到合法区间继续用。
        var minutes = Math.Clamp(segmentMinutes, 1, 10);
        var fallback = durationFallback.Minutes();

        return Default with
        {
            SegmentDuration = TimeSpan.FromMinutes(minutes),
            MaxDuration = fallback is { } value ? TimeSpan.FromMinutes(value) : NoFallback,
        };
    }

    /// <summary>带上录制规格（规格 §3.1.7 的连带项：索引要记编码 / 分辨率）。</summary>
    /// <remarks>
    /// 单独一个方法而不是往 <see cref="From"/> 里再加一个参数：
    /// 那个方法的调用点有十几处（大多是测试），它们关心的是时长档位，
    /// 不该被一个跟它们无关的参数牵连着全改一遍。
    /// </remarks>
    public RecordingSessionOptions With(Media.RecordingSpec spec) => this with { Spec = spec };
}

/// <summary>
/// 一次录制会话的编排：开录 → 分段滚动 → 收尾 → 入库。
/// </summary>
/// <remarks>
/// <para>
/// <b>职责边界</b>：本类只保证「MKV 分段落进工作区 + <c>session.json</c> 及时更新」。
/// 封闭、remux、解码校验、算哈希、写索引**全部交给已有的
/// <see cref="SessionFinalizer"/>** —— 规格 §4.1 要求任何进入「收尾中」的路径都走同一套
/// 逻辑（I9），这里再实现一份就是造旁路。
/// </para>
/// <para>
/// <b>时间用单调时钟</b>（I11）。墙钟只在开录那一刻取一次，之后所有时间戳都是
/// 「起点 + 单调偏移」—— 用户改系统时间因此伪造不出更早的证据时间。
/// </para>
/// <para>
/// <b>manifest 必须在每段封闭时立刻落盘</b>。孤儿判定是「有 session.json 但没有
/// finalized.json」，而 <see cref="RecordingWorkspace.ListOrphansAsync"/> 只认 manifest
/// 里列出的分段 —— 不写 manifest 的话，进程被杀后那批文件**对恢复链路完全不可见**。
/// </para>
/// </remarks>
public sealed class RecordingSession : IAsyncDisposable
{
    private readonly RecordingWorkspace _workspace;
    private readonly ICameraCapture _capture;
    private readonly SessionFinalizer _finalizer;
    private readonly DiskSpaceGuard _diskGuard;
    private readonly RecordingSessionOptions _options;
    private readonly Func<TimeSpan> _clock;

    /// <summary>可信时钟；<see langword="null"/> = 不设闸（见构造函数的说明）。</summary>
    private readonly ITrustedClock? _trustedClock;

    /// <summary>这一段的单号 —— 水印第二行要用它（规格 §3.6.2）。</summary>
    private string _waybill = string.Empty;

    /// <summary>「开录那一刻」的时钟读数。<see cref="Elapsed"/> 是相对它的差值。</summary>
    /// <remarks>
    /// 不用「重新绑定 _clock」那种写法：那样第二次读会把已经减过的值再减一遍。
    /// 显式记一个基准，含义一目了然，也不会随重置次数漂移。
    /// </remarks>
    private TimeSpan _clockOrigin;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _deviceName;

    private readonly List<SegmentProduct> _closedSegments = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SessionManifest? _manifest;
    private ICaptureProcess? _currentProcess;
    /// <summary>一段尚未封闭的分段。</summary>
    /// <remarks>
    /// 刻意做成**引用类型**：它要被 <see cref="Interlocked.Exchange{T}(ref T, T)"/> 交换，
    /// 而那个泛型只接受引用类型／基元／枚举 —— 换成 <c>(string, int)?</c> 这种值类型，
    /// 编译能过，运行时抛 <see cref="NotSupportedException"/>（2026-09-23 踩过）。
    /// </remarks>
    private sealed record OpenSegment(string FileName, int Sequence);

    /// <summary>
    /// 当前**尚未封闭**的分段。<c>null</c> 表示没有待登记的段。
    /// </summary>
    /// <remarks>
    /// 文件名与序号挤在一个字段里，是为了能用**一次**交换原子地认领它 ——
    /// 拆成两个字段的话，「认领的瞬间」可能读到上一段的序号，
    /// 而重号会让时间轴错位（见 <see cref="CloseCurrentSegmentAsync"/> 的注释）。
    /// </remarks>
    private OpenSegment? _openSegment;
    private TimeSpan _segmentStartedAt;
    private DateTimeOffset _startedAt;
    private CancellationTokenSource? _loopCancellation;

    /// <summary>
    /// 下一次**问**「要不要停」的时刻（规格 §3.3.4）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>null</c> = 时长兜底档位是「关闭」—— 那就**不问也不停**
    /// （与手机端 <c>_nextPromptAtMs</c> 同形）。原来这里靠
    /// <see cref="NoFallback"/>（<c>TimeSpan.MaxValue</c>）「比不到」来实现关闭，
    /// 改成可空之后「关闭」是一眼能看出来的，不必再推一遍算术。
    /// </remarks>
    private TimeSpan? _nextPromptAt;

    /// <summary>已经**问出去**的时刻；<c>null</c> = 当前没在问。</summary>
    /// <remarks>
    /// ⚠️ 它与 <see cref="_nextPromptAt"/> 是**互斥**的两个状态：
    /// 问了就把 <see cref="_nextPromptAt"/> 置空 —— 不置空的话每圈都会重问一遍。
    /// </remarks>
    private TimeSpan? _promptShownAt;

    /// <summary>用户在询问里点了【停止】。下一圈就收尾。</summary>
    /// <remarks>
    /// 不在这里直接停：收尾要跳出循环、由 <see cref="RunAsync"/> 之后那段统一做。
    /// 循环的检查间隔是 <see cref="RecordingSessionOptions.PollInterval"/>（500 ms），
    /// 所以「点了之后 0.5 秒内收」，用户感觉不到延迟。
    /// </remarks>
    private bool _durationStopRequested;

    /// <summary>该问了 —— 由协调器接到之后去发语音与界面两键。</summary>
    private readonly Action? _durationPrompted;

    /// <param name="clock">
    /// 单调时钟读数（默认 <c>Stopwatch.GetElapsedTime</c>）。
    /// 测试传可控读数，就能不靠等待验证分段滚动与时长兜底。
    /// </param>
    /// <param name="delay">等待。测试传「立即返回」即可把编排循环推快。</param>
    /// <param name="trustedClock">
    /// 可信时钟（规格 §3.6.4：**未校准不得开始录制**）。
    /// <b>不传 = 不设闸</b> —— 那些直接把本类 new 出来的测试（以及电脑端内部
    /// 用来验证分段的用例）不该被时间校准牵连；生产路径由
    /// <see cref="RecordingCoordinator"/> 传真的那个。
    /// </param>
    public RecordingSession(
        RecordingWorkspace workspace,
        ICameraCapture capture,
        SessionFinalizer finalizer,
        DiskSpaceGuard diskGuard,
        string deviceName,
        string sourceDeviceId,
        RecordingSessionOptions? options = null,
        Func<TimeSpan>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ITrustedClock? trustedClock = null,
        Action? durationPrompted = null)
    {
        _workspace = workspace;
        _capture = capture;
        _finalizer = finalizer;
        _diskGuard = diskGuard;
        _deviceName = deviceName;
        _options = options ?? RecordingSessionOptions.Default;
        _clock = clock ?? NewDefaultClock();
        _delay = delay ?? Task.Delay;
        _trustedClock = trustedClock;
        _durationPrompted = durationPrompted;

        SourceDeviceId = sourceDeviceId;
        SessionId = NewSessionId();
    }

    /// <summary>
    /// 现在是不是**在问**「要不要停」（规格 §3.3.4）。
    /// </summary>
    /// <remarks>界面拿它决定那两个按钮显不显示；测试拿它断言「真的问了」。</remarks>
    public bool IsAwaitingDurationAnswer
    {
        get
        {
            lock (_gate)
            {
                return _promptShownAt is not null;
            }
        }
    }

    /// <summary>
    /// 用户对时长兜底那次询问的回答（规格 §3.3.4）。
    /// </summary>
    /// <param name="continueRecording">
    /// <see langword="true"/> = 点了【继续】（取消本轮上限，隔
    /// <see cref="RecordingSessionOptions.PromptRepeat"/> 再问）；
    /// <see langword="false"/> = 点了【停止】。
    /// </param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>没在问的时候调它什么都不做</b>：界面上的按钮可能因为
    /// 「按下的同时用户正好扫了下一件」而晚到一步，而那时这一问已经作废了 ——
    /// 此时把 <c>_nextPromptAt</c> 重置掉，会把**新那一段**的兜底计时也一起改掉。
    /// </para>
    /// <para>
    /// ⚠️ 与手机端 `DurationPromptAnswered` 同一口径，**两端必须同向**：
    /// 两端对这条的表现不一致时，用户会以为其中一个坏了。
    /// </para>
    /// </remarks>
    public void AnswerDurationPrompt(bool continueRecording)
    {
        lock (_gate)
        {
            if (_promptShownAt is null)
            {
                return;
            }

            _promptShownAt = null;

            if (continueRecording)
            {
                _nextPromptAt = Elapsed + _options.PromptRepeat;
            }
            else
            {
                _durationStopRequested = true;
            }
        }
    }

    /// <summary>本次会话的标识。落进索引，也是工作区目录名。</summary>
    public string SessionId { get; }

    /// <summary>本机的设备标识（端间契约 §2）。</summary>
    public string SourceDeviceId { get; }

    /// <summary>本会话的单号。开录前为 <see langword="null"/>。</summary>
    /// <remarks>打点要带着它落盘（规格 §3.2.4）。</remarks>
    public WaybillNumber? Waybill => _manifest is null ? null : WaybillNumber.Parse(_manifest.Waybill);

    public RecordingSessionState State { get; private set; } = RecordingSessionState.Idle;

    /// <summary>从开录起算的已录时长（单调，I11）。</summary>
    public TimeSpan Elapsed => _clock() - _clockOrigin;

    /// <summary>已经封闭、可以进收尾的分段数。</summary>
    public int ClosedSegmentCount
    {
        get { lock (_gate) { return _closedSegments.Count; } }
    }

    /// <summary>
    /// 录制中遇到的、需要让用户看见的问题（I3：不存在静默失败）。
    /// </summary>
    public string? LastProblem { get; private set; }

    /// <summary>
    /// 本次会话最终由哪个原因结束。收尾前为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 名字刻意不叫 <c>StopReason</c> —— 那会与同名的枚举类型撞车，
    /// 之后每一处用到该枚举的地方都得写限定名。
    /// </remarks>
    public StopReason? StoppedBecause { get; private set; }

    /// <summary>
    /// 会话彻底走完（已入库或已判失败）时完成的信号。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="RunAsync"/> 自动收尾时，调用方**没有**一个 Task 可以 await
    /// <see cref="StopAsync"/> —— 而状态从「收尾中」落到终态是异步的。
    /// 没有这个信号，界面只能轮询 <see cref="State"/>，测试也会读到中间的
    /// <see cref="RecordingSessionState.Finalizing"/>（这两种都发生过）。
    /// </remarks>
    public Task Completion => _completion.Task;

    /// <summary>
    /// 收尾结果。收尾走完之前为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 循环**自己**收尾时（时长兜底 / 磁盘将满），调用方手里没有返回值的来源 ——
    /// <see cref="RunAsync"/> 把 <see cref="TryStopAsync"/> 的结果丢掉了。
    /// 没有它，协调器只能报一句「停了」，说不出成没成功、入库了几段，
    /// 而这正是 I3 不许的那种「失败了但没人知道」。
    /// </remarks>
    public FinalizeOutcome? Outcome { get; private set; }

    /// <summary>
    /// 开录。
    /// </summary>
    /// <remarks>
    /// 开录即写第一版 manifest（分段为空）。看着像多余的一步，
    /// 但它正是「进程被杀后还能恢复」的前提 —— 没有 manifest 就没有孤儿。
    /// </remarks>
    public async Task StartAsync(
        WaybillNumber waybill,
        string encoder,
        CancellationToken cancellationToken = default)
    {
        if (State != RecordingSessionState.Idle)
        {
            throw new InvalidOperationException($"会话已经在 {State} 状态，不能重复开录。");
        }

        // ⚠️ **未校准不得开始录制**（规格 §3.6.4，2026-09-24 的需求变更）。
        //
        // 这道闸在**两个地方**都有：协调器的 `StartWork()` 与这里。
        // 只在前者的话，任何绕过协调器直接建会话的路径都能录 ——
        // 而「绕过去」这件事在这类不可逆的保证上不该靠自觉。
        if (_trustedClock is { IsCalibrated: false } clock)
        {
            throw new InvalidOperationException(
                $"{clock.BlockedReason}（规格 §3.6.4：未校准不得开始录制）");
        }

        // 单调时钟的起点挪到开录这一刻 —— 会话可能在开录前先建好（协调器就是）。
        RestartClock();

        // ⚠️ 起录时刻取**可信时钟**，不是墙钟（规格 §3.6.3：「水印与时长都不得
        // 取自墙钟 —— 用户改系统时间**不得**改变视频里的时间」）。
        // 没接可信时钟时（测试路径）才退回墙钟。
        _startedAt = _trustedClock?.Now ?? DateTimeOffset.UtcNow;

        // 水印第二行要它（规格 §3.6.2：一段只用一个单号）。
        _waybill = waybill.Value;
        _manifest = new SessionManifest(
            SessionId, waybill.Value, SourceDeviceId, _startedAt.ToString("O"), []);

        Directory.CreateDirectory(_workspace.SessionDirectory(SessionId));
        await _workspace.WriteManifestAsync(_manifest, cancellationToken);

        State = RecordingSessionState.Recording;
        await StartSegmentAsync(encoder, cancellationToken);
    }

    /// <summary>
    /// 起编排循环：到点滚段、到上限或磁盘将满就自动收尾。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="StartAsync"/> 分开，是因为「循环」这件事需要有人 await；
    /// 界面可以只 fire-and-forget，也可以用返回的 Task 观察它结束。
    /// 循环自己不会抛 —— 所有异常都走收尾，变成用户可见的记录。
    /// </remarks>
    public async Task RunAsync(string encoder, CancellationToken cancellationToken = default)
    {
        _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 循环只**决定**该不该停，不在循环体里收尾。
        // 原因是踩过的：收尾要取消循环自己的令牌，而在循环体里收尾意味着
        // 那个取消会打断收尾自己 —— 表现是状态永远卡在「收尾中」。
        var reason = StopReason.Manual;

        try
        {
            while (State == RecordingSessionState.Recording)
            {
                await _delay(_options.PollInterval, _loopCancellation.Token);

                // 用户在询问里点了【停止】—— 收（延迟 ≤ 一个检查间隔）。
                if (_durationStopRequested)
                {
                    reason = StopReason.DurationFallback;
                    break;
                }

                if (_diskGuard.Check(_workspace.SessionDirectory(SessionId)).ShouldFinalize)
                {
                    reason = StopReason.StorageLow;
                    break;
                }

                // ── 时长兜底（规格 §3.3.4）────────────────────────────────
                //
                // ⚠️ **到点是「问」，不是「停」**（2026-09-27 修）。
                // 原来这里写的是 `if (Elapsed >= MaxDuration) { reason = …; break; }` ——
                // 于是**默认档下任何一段正常录制到 4 分钟就被切断**，
                // 而 2026-09-21 那次需求变更的动机正是「它会把正常录制打断」。
                // 现在的口径与手机端 `stop_controller.dart` 的 `_evaluateTimers` 逐条同形：
                //
                //   到点       → 问（语音 + 界面两键），**录制不中断**（规格明令）
                //   答【继续】 → 隔 RepeatEvery 再问
                //   答【停止】 → 立即收（上面那个 `_durationStopRequested`）
                //   1 分钟没人理 → 视为用户不在场 → 按兜底收
                //
                // 「无操作」与「主动继续」必须区分（规格原话）：前者是兜底生效，
                // 后者是用户在场且明确要继续 —— 所以宽限期内**什么都不做**，
                // 而不是替用户点一下继续。
                if (_promptShownAt is { } promptShownAt)
                {
                    if (Elapsed - promptShownAt >= _options.Grace)
                    {
                        reason = StopReason.DurationFallback;
                        break;
                    }
                }
                else if (_nextPromptAt is { } promptAt && Elapsed >= promptAt)
                {
                    _promptShownAt = Elapsed;
                    _nextPromptAt = null;

                    // ⚠️ 通知是**回调**而不是事件：本类没有通知出口，
                    // 塞一个进去会让它多一条依赖（它只该管「一段」）。
                    _durationPrompted?.Invoke();
                }

                if (Elapsed - _segmentStartedAt >= _options.SegmentDuration)
                {
                    await CloseCurrentSegmentAsync(CancellationToken.None);
                    await StartSegmentAsync(encoder, CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 界面主动取消。收尾照样要跑完 —— 只是停录原因记为手动。
        }
        catch (Exception ex)
        {
            // 编排途中出岔子（采集进程起不来、磁盘 I/O 挂了……）。
            // 不往上抛：界面只 await 这一个 Task，抛出去就变成未处理异常，
            // 而用户需要的是一条**看得见的**说明（I3：不存在静默失败）。
            LastProblem = $"录制过程中出错：{ex.Message}";
        }
        finally
        {
            // 无论怎么退出循环，都保证收尾有落定；否则 Completion 永不完成，
            // 等它的界面与测试会一起挂住。
            await TryStopAsync(reason).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 尝试收尾。已经在收尾或已收尾时**什么都不做**（幂等）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="StopAsync"/> 的区别只有一个：不抛。
    /// 给「可能已经收过尾」的调用点用（例如 <see cref="RunAsync"/> 的 finally）。
    /// </remarks>
    public async Task<FinalizeOutcome?> TryStopAsync(
        StopReason reason,
        CancellationToken cancellationToken = default)
    {
        if (State is not RecordingSessionState.Recording)
        {
            return null;
        }

        return await StopAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 停止并收尾。
    /// </summary>
    /// <remarks>
    /// 每一个进入「收尾中」的路径最终都落到这里，再交给
    /// <see cref="SessionFinalizer.FinalizeAsync"/> 统一处理 —— 没有旁路（I9）。
    /// </remarks>
    public async Task<FinalizeOutcome> StopAsync(
        StopReason reason,
        CancellationToken cancellationToken = default)
    {
        if (State is not RecordingSessionState.Recording)
        {
            throw new InvalidOperationException($"会话在 {State} 状态，没有可停止的录制。");
        }

        State = RecordingSessionState.Finalizing;
        StoppedBecause = reason;

        if (_loopCancellation is not null)
        {
            await _loopCancellation.CancelAsync();
        }

        try
        {
            await CloseCurrentSegmentAsync(cancellationToken);

            var manifest = _manifest
                ?? throw new InvalidOperationException("会话没有 manifest，无法收尾。");

            List<SegmentProduct> segments;
            lock (_gate)
            {
                segments = [.. _closedSegments];
            }

            if (segments.Count == 0)
            {
                State = RecordingSessionState.FinalizeFailed;
                LastProblem = "本次工作没有录到任何可收尾的分段（摄像头可能没能打开）。";
                return Outcome = new FinalizeOutcome(
                    RecordingSessionState.FinalizeFailed, reason, [], LastProblem);
                // ↑ 结果留在属性上：循环自己收尾时没人接得住这个返回值。
            }

            var outcome = await _finalizer.FinalizeAsync(
                SessionId, WaybillNumber.Parse(manifest.Waybill), SourceDeviceId,
                segments, reason, cancellationToken,
                // 规格进索引（§3.1.7 的连带项）—— 由这次会话的选项带过来。
                spec: _options.Spec);

            State = outcome.State;
            Outcome = outcome;

            if (outcome.Succeeded)
            {
                await _workspace.MarkFinalizedAsync(SessionId, cancellationToken);
            }
            else
            {
                // 收尾失败时**故意不写 finalized.json** —— 留着 manifest 让它下次启动
                // 被当成孤儿重试。清掉就永久失去了这批录像（I2）。
                LastProblem = outcome.FailureReason ?? "收尾失败。";
            }

            return outcome;
        }
        finally
        {
            // 无论走哪条路，终态都落定了 —— 唤醒等待者。
            _completion.TrySetResult();
        }
    }

    private async Task StartSegmentAsync(string encoder, CancellationToken cancellationToken)
    {
        var sequence = ClosedSegmentCount;
        var fileName = $"segment-{sequence:D3}.mkv";
        var outputPath = Path.Combine(_workspace.SessionDirectory(SessionId), fileName);

        // 换段时段的计时归零。
        _segmentStartedAt = Elapsed;

        WriteWatermark(outputPath);

        _currentProcess = await _capture.StartAsync(
            _deviceName, outputPath, encoder, cancellationToken);

        // 一次交换把两个值一起发布 —— 见 _openSegment 的说明。
        Interlocked.Exchange(ref _openSegment, new OpenSegment(fileName, sequence));
    }

    /// <summary>
    /// 给这一段写一份水印字幕（规格 §3.6.2）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>起算点是**可信时钟**</b>，不是墙钟（§3.6.3：水印的时间不得取自墙钟）。
    /// 没接可信时钟时（测试路径）退回墙钟 —— 与起录时刻同一个口径。
    /// </para>
    /// <para>
    /// ⚠️ <b>写失败不是错误</b>：字幕文件写不出来（磁盘满、目录没了）时，
    /// <b>采集照旧要起来</b> —— 那一段录像没有水印是遗憾，录不出来是事故。
    /// 所以这里吞掉异常，只记一条。
    /// </para>
    /// <para>
    /// 覆盖时长取「单段时长 + 2 分钟余量」：段会按时长滚动，但最后一段可能超出
    /// 一点（滚动要等关键帧）。**少了余量就会出现「最后几秒没有水印」**——
    /// 而那种「部分缺失」比全都没有更难被发现。
    /// </para>
    /// </remarks>
    private void WriteWatermark(string segmentPath)
    {
        try
        {
            var start = _trustedClock?.Now ?? DateTimeOffset.UtcNow;
            var coverage = _options.SegmentDuration + TimeSpan.FromMinutes(2);

            var (width, height) = _options.Spec?.Size ?? (1280, 720);

            File.WriteAllText(
                AssWatermark.PathFor(segmentPath),
                AssWatermark.Build(start, _waybill, coverage, width, height));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastProblem = $"这一段没有水印（字幕文件写不出来：{ex.Message}）";
        }
    }

    /// <summary>
    /// 只放掉相机，不做收尾。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 单独拆出来是为了<b>连续扫换件</b>：换件要立刻以新单号开下一段，
    /// 而收尾（remux + 解码校验）要几秒 —— 等它做完再开下一段，那几秒就白丢了。
    /// 但相机是独占的（实测），所以**必须先放掉设备**下一个会话才开得起来。
    /// </para>
    /// <para>
    /// 幂等：已经放过了就什么都不做。收尾仍然只有 <c>StopAsync</c> 那一条路（I9），
    /// 这里只是把它内部的第一步提前了。
    /// </para>
    /// <para>
    /// ⚠️ <b>用 <see cref="Interlocked.Exchange{T}(ref T, T)"/> 认领这个进程，谁认领谁停。</b>
    /// 原来是「先读、后清」—— 那个写法会让两个调用方**都通过判据**，把同一个采集进程
    /// <b>停两次</b>：一次来自编排循环的 <c>finally</c>（<c>TryStopAsync</c> →
    /// <c>CloseCurrentSegmentAsync</c> → 这里），另一次来自 <c>DisposeAsync</c>。
    /// 两次都会往同一个分段文件写字 —— 测试里的 <c>FakeProcess</c> 直接以
    /// <c>IOException：文件正被另一个进程使用</c> 暴露它（真进程那边则是两次写 stdin）。
    /// </para>
    /// <para>
    /// 这个写法与 <see cref="CloseCurrentSegmentAsync"/> 认领 <c>_openSegment</c>
    /// <b>完全同源</b>（那里的注释写着「先读后清会让两边都通过判据」）——
    /// 上一次只覆盖了**分段**，漏了**进程**，见 <c>docs/实现决策.md</c> §30。
    /// </para>
    /// </remarks>
    public async Task ReleaseCaptureAsync(CancellationToken cancellationToken = default)
    {
        var process = Interlocked.Exchange(ref _currentProcess, null);
        if (process is null)
        {
            return;
        }

        var exitCode = await process.StopAsync(_options.StopGracePeriod, cancellationToken);

        if (exitCode is not null and not 0)
        {
            // 退出码非 0 说明采集中途出过事。文件可能仍在（ffmpeg 常留下部分内容），
            // 所以照样登记 —— 由收尾器的「实际解码校验」判它到底能不能用，
            // 而不是在这里替它下结论（那正是「编译绿≠正确」的翻版）。
            LastProblem = $"采集进程以退出码 {exitCode} 结束，该段可能不完整。";
        }
    }

    private async Task CloseCurrentSegmentAsync(CancellationToken cancellationToken)
    {
        // 判据是**分段本身**而不是进程：ReleaseCaptureAsync 会把进程清空，
        // 但段还没登记。拿错了判据就会把同一段登记两次（重号会让时间轴错位）。
        //
        // ⚠️ 用一次原子交换**认领**这一段，而不是「先读、后面再清」：编排循环与
        // StopAsync 可能在同一瞬间都想封闭当前段（循环刚到滚段点、用户正好点
        // 【结束】）。先读后清会让两边都通过判据，把同一段登记两次 ——
        // 而循环在 2026-09-23 之前根本没被启动过，所以这条竞态是新暴露的。
        var claimed = Interlocked.Exchange(ref _openSegment, null);
        if (claimed is not { } open)
        {
            return;
        }

        var fileName = open.FileName;
        var sequence = open.Sequence;
        var startedAt = _segmentStartedAt;

        var endedAt = Elapsed;
        await ReleaseCaptureAsync(cancellationToken);

        var segmentPath = Path.Combine(_workspace.SessionDirectory(SessionId), fileName);

        // 水印字幕**用完就删**：它是这一段的临时素材，不是产物。
        // 留着的话它会跟着会话目录进归档（而归档里多一个 .ass 是垃圾），
        // 也会让「盘上占了多少」那个数字虚高一点。
        // 顺序在 `ReleaseCaptureAsync` **之后**：ffmpeg 还拿着它的话删不掉。
        TryDeleteWatermark(segmentPath);

        var segment = new SegmentProduct(
            sequence,
            segmentPath,
            _startedAt + startedAt,
            _startedAt + endedAt);

        lock (_gate)
        {
            _closedSegments.Add(segment);

            if (_manifest is not null)
            {
                _manifest = _manifest with
                {
                    Segments =
                    [
                        .. _manifest.Segments,
                        new SegmentManifest(
                            sequence, fileName,
                            segment.StartedAt.ToString("O"), segment.EndedAt.ToString("O")),
                    ],
                };
            }
        }

        // 立刻落盘：进程在这之后被杀，这批分段仍能被孤儿恢复找到。
        if (_manifest is not null)
        {
            await _workspace.WriteManifestAsync(_manifest, cancellationToken);
        }
    }

    /// <summary>删掉水印字幕。删不掉只是留个垃圾，不影响任何判定。</summary>
    private static void TryDeleteWatermark(string segmentPath)
    {
        try
        {
            File.Delete(AssWatermark.PathFor(segmentPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉不是错误：它是临时素材，不是产物。
        }
    }

    /// <summary>
    /// 把单调时钟的起点挪到「此刻」，**并重置时长兜底的计时**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认时钟是从**会话构造**那一刻开始走的，而会话可能在开录前先建好
    /// （协调器就是这么用的）。不重置的话，开录后第一件事读到的就已经是几十毫秒，
    /// 打点偏移会带上一段不存在的录制时间 —— 回放定位会偏。
    /// </para>
    /// <para>
    /// ⚠️ 兜底计时跟着一起重置，是因为两者共用同一个基准（<see cref="Elapsed"/>），
    /// 而这一句**只在开录那一刻被调**（<see cref="StartAsync"/> 里那一处）——
    /// 与手机端在 <c>startRecording</c> 里设 <c>_nextPromptAtMs</c> 同形。
    /// 分开写两处的话，将来有人加一条「不经过开录就重置时钟」的路径，
    /// 兜底的计时基准就会跟时钟走岔。
    /// </para>
    /// </remarks>
    private void RestartClock()
    {
        _clockOrigin = _clock();

        // 时长兜底档位「关闭」⇒ 不问也不停（与手机端的 null 同形）。
        _nextPromptAt = _options.MaxDuration == RecordingSessionOptions.NoFallback
            ? null
            : _options.MaxDuration;
        _promptShownAt = null;
        _durationStopRequested = false;
    }

    public async ValueTask DisposeAsync()
    {
        var loop = _loopCancellation;
        _loopCancellation = null;
        if (loop is not null)
        {
            await loop.CancelAsync();
            loop.Dispose();
        }

        // ⚠️ 走 ReleaseCaptureAsync 而不是自己读一次 `_currentProcess`：
        // 这里是**另一个**停进程的入口，而「先读后清」会让它和编排循环同时通过判据。
        // 认领（Interlocked.Exchange）在那一处，两个入口共用它 —— 见那个方法的说明。
        await ReleaseCaptureAsync(CancellationToken.None);
    }

    /// <summary>
    /// 默认时钟：一个从会话建立起就一直走的秒表。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="System.Diagnostics.Stopwatch"/> 而不是 <c>DateTime.UtcNow</c>
    /// 正是 I11 的落点 —— 墙钟会被用户改，单调时钟不会。
    /// 「起点是构造那一刻」没关系：<see cref="StartAsync"/> 之后才会有人读它，
    /// 而那段时间可以忽略不计；真正要保证的是**差值**不受墙钟影响。
    /// </remarks>
    private static Func<TimeSpan> NewDefaultClock()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        return () => stopwatch.Elapsed;
    }

    private static string NewSessionId() =>
        $"{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
}
