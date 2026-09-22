namespace VidLog.Desktop.Core.Recording;

/// <summary>采集会话的可调参数。</summary>
/// <param name="SegmentDuration">单段时长上限。到点就滚下一段，用户无感（规格 §3.1.1）。</param>
/// <param name="MaxDuration">整场工作的时长兜底。到点自动收尾（规格 §3.3.4）。</param>
/// <param name="StopGracePeriod">优雅停止的等待上限；超时升级为强杀。</param>
/// <param name="PollInterval">编排循环的检查间隔。</param>
public sealed record RecordingSessionOptions(
    TimeSpan SegmentDuration,
    TimeSpan MaxDuration,
    TimeSpan StopGracePeriod,
    TimeSpan PollInterval)
{
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
    private string _currentFileName = string.Empty;
    private int _currentSequence;
    private TimeSpan _segmentStartedAt;
    private DateTimeOffset _startedAt;
    private CancellationTokenSource? _loopCancellation;

    /// <param name="clock">
    /// 单调时钟读数（默认 <c>Stopwatch.GetElapsedTime</c>）。
    /// 测试传可控读数，就能不靠等待验证分段滚动与时长兜底。
    /// </param>
    /// <param name="delay">等待。测试传「立即返回」即可把编排循环推快。</param>
    public RecordingSession(
        RecordingWorkspace workspace,
        ICameraCapture capture,
        SessionFinalizer finalizer,
        DiskSpaceGuard diskGuard,
        string deviceName,
        string sourceDeviceId,
        RecordingSessionOptions? options = null,
        Func<TimeSpan>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _workspace = workspace;
        _capture = capture;
        _finalizer = finalizer;
        _diskGuard = diskGuard;
        _deviceName = deviceName;
        _options = options ?? RecordingSessionOptions.Default;
        _clock = clock ?? NewDefaultClock();
        _delay = delay ?? Task.Delay;

        SourceDeviceId = sourceDeviceId;
        SessionId = NewSessionId();
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

        // 单调时钟的起点挪到开录这一刻 —— 会话可能在开录前先建好（协调器就是）。
        RestartClock();

        _startedAt = DateTimeOffset.UtcNow;
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

                if (_diskGuard.Check(_workspace.SessionDirectory(SessionId)).ShouldFinalize)
                {
                    reason = StopReason.StorageLow;
                    break;
                }

                if (Elapsed >= _options.MaxDuration)
                {
                    reason = StopReason.DurationFallback;
                    break;
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
                return new FinalizeOutcome(
                    RecordingSessionState.FinalizeFailed, reason, [], LastProblem);
            }

            var outcome = await _finalizer.FinalizeAsync(
                SessionId, WaybillNumber.Parse(manifest.Waybill), SourceDeviceId,
                segments, reason, cancellationToken);

            State = outcome.State;

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

        _currentProcess = await _capture.StartAsync(
            _deviceName, outputPath, encoder, cancellationToken);

        _currentSequence = sequence;
        _currentFileName = fileName;
    }

    /// <summary>
    /// 只放掉相机，不做收尾。
    /// </summary>
    /// <remarks>
    /// 单独拆出来是为了<b>连续扫换件</b>：换件要立刻以新单号开下一段，
    /// 而收尾（remux + 解码校验）要几秒 —— 等它做完再开下一段，那几秒就白丢了。
    /// 但相机是独占的（实测），所以**必须先放掉设备**下一个会话才开得起来。
    /// <para>
    /// 幂等：已经放过了就什么都不做。收尾仍然只有 <c>StopAsync</c> 那一条路（I9），
    /// 这里只是把它内部的第一步提前了。
    /// </para>
    /// </remarks>
    public async Task ReleaseCaptureAsync(CancellationToken cancellationToken = default)
    {
        var process = _currentProcess;
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

        _currentProcess = null;
    }

    private async Task CloseCurrentSegmentAsync(CancellationToken cancellationToken)
    {
        // 判据是**文件名**而不是进程：ReleaseCaptureAsync 会把进程清空，
        // 但段还没登记。拿错了判据就会把同一段登记两次（重号会让时间轴错位）。
        if (_currentFileName.Length == 0)
        {
            return;
        }

        var fileName = _currentFileName;
        var sequence = _currentSequence;
        var startedAt = _segmentStartedAt;

        var endedAt = Elapsed;
        await ReleaseCaptureAsync(cancellationToken);

        // 登记完就清掉，保证幂等。
        _currentFileName = string.Empty;

        var segment = new SegmentProduct(
            sequence,
            Path.Combine(_workspace.SessionDirectory(SessionId), fileName),
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

    /// <summary>
    /// 把单调时钟的起点挪到「此刻」。
    /// </summary>
    /// <remarks>
    /// 默认时钟是从**会话构造**那一刻开始走的，而会话可能在开录前先建好
    /// （协调器就是这么用的）。不重置的话，开录后第一件事读到的就已经是几十毫秒，
    /// 打点偏移会带上一段不存在的录制时间 —— 回放定位会偏。
    /// </remarks>
    private void RestartClock() => _clockOrigin = _clock();

    public async ValueTask DisposeAsync()
    {
        var loop = _loopCancellation;
        _loopCancellation = null;
        if (loop is not null)
        {
            await loop.CancelAsync();
            loop.Dispose();
        }

        if (_currentProcess is not null)
        {
            await _currentProcess.StopAsync(_options.StopGracePeriod);
            _currentProcess = null;
        }
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
