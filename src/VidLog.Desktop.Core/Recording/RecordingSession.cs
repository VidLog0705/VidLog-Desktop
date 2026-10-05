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
    TimeSpan? PromptGrace = null,
    // 追加字段（规格 §3.1.8 的音轨）。
    // ⚠️ 默认值是 **null（不录声音）**，而不是规格里那句「默认开」——
    // 「默认开」是**设置层**的事（`AppSettings.RecordAudio = true`），
    // 而这里是「这一次工作到底给了哪个设备」。这两件事混成一件的话，
    // 一个没读设置的调用点（测试、诊断工具）会突然要求一个麦克风，
    // 而它接不上时降级虽然不会弄失败录制，却会白白多等一次开设备。
    string? Microphone = null)
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
    public static RecordingSessionOptions From(int segmentMinutes, DurationFallbackOption durationFallback) =>
        Default.WithSchedule(segmentMinutes, durationFallback);

    /// <summary>
    /// 只改时长那两个档位，**其余字段原样保留**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 存在的理由是那个已经发生过的 bug：<c>AppHost.ApplySettingsAsync</c> 原来写的是
    /// <c>SessionOptions = From(…)</c> —— `From` 从 `Default` 起算，于是**用户一改任何设置，
    /// 已经探好的录制规格就被抹成 null**（水印尺寸跟着掉回 1280×720、索引里也不再记编码）。
    /// 那种值不会报错，只会「有时候对、有时候不对」。
    /// 改成在**现有值**上改字段，从形状上就不可能再丢。
    /// </remarks>
    public RecordingSessionOptions WithSchedule(int segmentMinutes, DurationFallbackOption durationFallback)
    {
        // 越界的值不抛：设置文件是可以被手改的，而一个改坏了的配置
        // 不该让用户**录不了像**（I4 的同一条精神）。夹到合法区间继续用。
        var minutes = Math.Clamp(segmentMinutes, 1, 10);
        var fallback = durationFallback.Minutes();

        return this with
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

    /// <summary>带上麦克风（规格 §3.1.8）。<see langword="null"/> = 这一段不录声音。</summary>
    /// <remarks>
    /// ⚠️ 它落在**会话选项**上而不是采集进程上，正是为了规格那句
    /// 「改了下次『开始工作』才生效、录制中不可改」：本记录由协调器在
    /// **每次开始工作**时整份交给会话，会话在整场里读的是同一个值。
    /// </remarks>
    public RecordingSessionOptions WithMicrophone(string? microphone) =>
        this with { Microphone = microphone };

}

/// <summary>
/// 一段**在开录之前就已经录好的画面**（规格 §3.1.3 的预录缓冲采纳下来的那一段）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>它的 <see cref="Path"/> 是调用方那边的一份临时文件</b>，会话会把它**搬进**
/// 自己的目录并登记成 <c>segment-000.mkv</c>。搬是必须的：孤儿恢复只认
/// manifest 里列出、且**文件真在**的分段（<see cref="RecordingWorkspace.ListOrphansAsync"/>
/// 有一道 <c>Where(File.Exists)</c>）—— 停在临时目录里等于这一段**静默消失**。
/// </para>
/// <para>
/// ⚠️ <b>时长是「缓冲窗口」，不是文件的真实长度</b>：裁切走 <c>-c copy</c>，
/// 只能落在关键帧上，所以产物通常比窗口长一点点（最多一个关键帧间隔）。
/// 记账用窗口（它才是「从什么时候开始有画面」的准确表述）。
/// </para>
/// </remarks>
/// <param name="Path">那份裁好的 MKV。</param>
/// <param name="StartedAt">这段画面的**起点**（可信时刻，I11）。会话的起录时刻就是它。</param>
/// <param name="Duration">缓冲窗口的长度。</param>
public sealed record AdoptedClip(string Path, DateTimeOffset StartedAt, TimeSpan Duration);

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
/// <b>manifest 要尽早在盘上有一份。</b>孤儿判定是「有 session.json 但没有
/// finalized.json」；录制期它是**随分段滚出来的**（T17 之后由 ffmpeg 自己滚、
/// 会话在收尾时才数，见 <see cref="CloseCurrentSegmentAsync"/>），进程被杀时
/// 靠 <see cref="RecordingWorkspace.ListOrphansAsync"/> 那条「扫目录捞没登记的分段」
/// 的路兜底（T20）—— 分段不会丢，只是那一路的时间戳只能取文件时间。
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

    /// <summary>画面从哪来（本机设备或网络地址）。</summary>
    /// <remarks>
    /// ⚠️ 存的是整个 <see cref="CameraSource"/> 而不是一个名字：网络那一路
    /// 的地址里**带凭据**，而 <see cref="SourceDeviceId"/> 会写进 manifest 与索引 ——
    /// 两件事分开之后，进盘的那一份永远是抹掉凭据的
    /// （见 <see cref="CameraSource.Identity"/>）。
    /// </remarks>
    private readonly CameraSource _source;

    private readonly List<SegmentProduct> _closedSegments = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SessionManifest? _manifest;
    private ICaptureProcess? _currentProcess;

    /// <summary>滚动分片那一路的输出**模式**（T17）。</summary>
    /// <remarks>
    /// ⚠️ <c>%03d</c> 是 ffmpeg 的 printf 风格占位符，段号由它自己填；起始号走
    /// <c>-segment_start_number</c>（见 <c>ICameraCapture.StartAsync</c>）。
    /// <b>它与 <see cref="SegmentFileName"/> 必须保持同一套写法</b> ——
    /// 一个给 ffmpeg 一个给收尾，对不上就会「收尾按名字去找、什么都找不到」，
    /// 而那正是最难看的一种：录了、也留在盘上，就是没人认。
    /// </remarks>
    private const string SegmentFilePattern = "segment-%03d.mkv";

    /// <summary>滚动分片里第 <paramref name="sequence"/> 片的文件名。</summary>
    private static string SegmentFileName(int sequence) => $"segment-{sequence:D3}.mkv";

    /// <summary>一场尚未收尾的采集。</summary>
    /// <remarks>
    /// 刻意做成**引用类型**：它要被 <see cref="Interlocked.Exchange{T}(ref T, T)"/> 交换，
    /// 而那个泛型只接受引用类型／基元／枚举 —— 换成 <c>int?</c> 这种值类型，
    /// 编译能过，运行时抛 <see cref="NotSupportedException"/>（2026-09-23 踩过）。
    /// </remarks>
    /// <param name="FirstSequence">
    /// 这一场采集的**第 0 片**是几号。采纳了预录缓冲时它是 1（0 号被预录那一段占了）。
    /// </param>
    private sealed record OpenSegment(int FirstSequence);

    /// <summary>
    /// 这一场**还没收尾**的采集。<c>null</c> = 没有可认领的。
    /// </summary>
    /// <remarks>
    /// ⚠️ 用一次原子交换认领它，而不是「先读、后面再清」：编排循环与
    /// <see cref="StopAsync"/> 可能在同一瞬间都想收这一场（用户正好点【结束】），
    /// 先读后清会让两边都通过判据，把同一批分段登记两次 —— 重号会让时间轴错位。
    /// </remarks>
    private OpenSegment? _openSegment;

    /// <summary>
    /// 这一场采集是从**哪个偏移**开始的（<see cref="Elapsed"/> 的刻度）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它原来叫「换段时段的计时归零」—— 那时每滚一段就重开一个进程、这里跟着重取。
    /// T17 之后**一个进程跑到底**，所以它只在开录那一刻取一次，含义变成了
    /// 「分段的第 0 片从哪儿起」：收尾时按 <c>它 + 段号 × 单段时长</c> 推每一段的起止。
    /// </remarks>
    private TimeSpan _captureStartedOffset;

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

    /// <summary>这一段遇到了问题 —— 由协调器接到之后**记进日志**（见 <see cref="LastProblem"/>）。</summary>
    private readonly Action<string>? _problemReported;

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
        CameraSource source,
        string sourceDeviceId,
        RecordingSessionOptions? options = null,
        Func<TimeSpan>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ITrustedClock? trustedClock = null,
        Action? durationPrompted = null,
        Action<string>? problemReported = null,
        Labels.BusinessType? businessType = null)
    {
        _workspace = workspace;
        _capture = capture;
        _finalizer = finalizer;
        _diskGuard = diskGuard;
        _source = source;
        _options = options ?? RecordingSessionOptions.Default;
        _clock = clock ?? NewDefaultClock();
        _delay = delay ?? Task.Delay;
        _trustedClock = trustedClock;
        _durationPrompted = durationPrompted;
        _problemReported = problemReported;
        BusinessType = businessType;

        SourceDeviceId = sourceDeviceId;
        SessionId = NewSessionId();
    }

    /// <summary>
    /// 这一段属于发货还是退货（设计图 `_35` 左上角那个【发货/退货】）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>在会话建好的那一刻就定下来，收尾时按它写标签</b>，而不是收尾时
    /// 去问协调器「现在是什么档」：换件之后旧段是在**后台**收尾的，
    /// 那时操作员完全可能已经把档切到另一边了 —— 现读会把**退货的标签
    /// 写到发货那一段上**，而那种错在界面上完全看不出来。
    /// <para>
    /// 为 <see langword="null"/> = 这次装配不管标签（测试与不关心它的调用点）。
    /// </para>
    /// </remarks>
    public Labels.BusinessType? BusinessType { get; }

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
    /// <remarks>
    /// ⚠️ <b>设值时会回调 <see cref="_problemReported"/></b>（只在**变了**的时候），
    /// 让调用方把它**记进日志**。`AGENTS.md` §6 要求「异常必须留痕」——
    /// 而 2026-09-29 查出来：这一类问题（音轨没接上、水印写不出来）
    /// **一次都没进过日志**，因为它只到界面。
    /// <para>
    /// ⚠️ 去重放在**这里**而不是回调方：同一个问题每段都会重新设一次，
    /// 不去重的话长期录制会把日志刷满 —— 而**被刷满的日志等于没有日志**。
    /// </para>
    /// </remarks>
    public string? LastProblem
    {
        get => _lastProblem;
        private set
        {
            if (string.Equals(value, _lastProblem, StringComparison.Ordinal))
            {
                return;
            }

            _lastProblem = value;

            if (value is { Length: > 0 })
            {
                _problemReported?.Invoke(value);
            }
        }
    }

    private string? _lastProblem;

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
        CancellationToken cancellationToken = default,
        AdoptedClip? leading = null)
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

        // ⚠️ **采纳预录缓冲**（规格 §3.1.3）。
        //
        // ⚠️ 它排在**设置时钟与起录时刻之前**：搬得成，那几秒画面才真的在这段录像里，
        // 时钟与起录时刻才该往前挪；搬不成（源文件没了、磁盘满）就**一点都不挪** ——
        // 否则 manifest 会声称「从 5 秒前开始录的」而盘上根本没有那 5 秒的画面，
        // 时间轴会整体前移，而**没有任何东西看起来是错的**。
        Directory.CreateDirectory(_workspace.SessionDirectory(SessionId));

        var adopted = leading is { } buffered && AdoptLeadingClip(buffered);

        // 单调时钟的起点挪到开录这一刻 —— 会话可能在开录前先建好（协调器就是）。
        //
        // ⚠️ **采纳了预录缓冲时它还要往前挪**（2026-10-02 需求方裁定
        // 「计入已录时长封顶，**从缓冲起点起算**」）：缓冲那几秒是**已经录下来的画面**，
        // 不往前挪的话 `Elapsed` 会比产物的真实长度短一截 ——
        // 于是闲置提醒、时长兜底、以及打点偏移（`PunchAsync` 按 `Elapsed` 算）
        // 全都系统性偏晚。挪的是**时钟起点**，不是「重设时钟」。
        RestartClock(adopted ? leading!.Duration : default);

        // ⚠️ 起录时刻取**可信时钟**，不是墙钟（规格 §3.6.3：「水印与时长都不得
        // 取自墙钟 —— 用户改系统时间**不得**改变视频里的时间」）。
        // 没接可信时钟时（测试路径）才退回墙钟。
        //
        // ⚠️ 采纳了缓冲时它取**缓冲起点**（由预录那一路记下、随 `AdoptedClip` 传进来）：
        // 那一段画面真的是从那个时刻开始在录的，manifest 的起录时刻写它才与产物对得上。
        _startedAt = adopted ? leading!.StartedAt : _trustedClock?.Now ?? DateTimeOffset.UtcNow;

        // 水印第二行要它（规格 §3.6.2：一段只用一个单号）。
        _waybill = waybill.Value;

        // ⚠️ 采纳段**必须出现在第一版 manifest 里**，不能等收尾再补：
        // 孤儿恢复只认 manifest 里列出、且**文件真在**的分段
        // （`RecordingWorkspace.ListOrphansAsync` 有一道 `Where(File.Exists)`），
        // 不列它的话，进程被杀时那一段**静默消失**（既不在索引里、也没人知道它存在过）。
        _manifest = new SessionManifest(
            SessionId, waybill.Value, SourceDeviceId, _startedAt.ToString("O"),
            adopted ? [LeadingSegmentManifest(leading!)] : [],
            // ⚠️ 规格**必须落在 manifest 上**，不能只在内存里：进程被系统杀掉时
            // 内存里那份一起没了，而孤儿恢复是**从盘上读**的（见 `SessionManifest.Spec`）。
            // 缺了它，收尾出来的索引条目编码 / 分辨率 / 方向三栏全空，
            // 「按空间清理」的估算还会因此被系统性放大（2026-10-02 实测 2.34 倍）。
            _options.Spec is { } spec ? SessionSpecManifest.From(spec) : null);

        await _workspace.WriteManifestAsync(_manifest, cancellationToken);

        State = RecordingSessionState.Recording;
        await StartCaptureAsync(encoder, cancellationToken);
    }

    /// <summary>采纳段在清单里的那一行。文件名与序号在 <see cref="AdoptLeadingClip"/> 里。</summary>
    private static SegmentManifest LeadingSegmentManifest(AdoptedClip buffered) =>
        new(LeadingSegmentSequence, LeadingSegmentFileName,
            buffered.StartedAt.ToString("O"),
            (buffered.StartedAt + buffered.Duration).ToString("O"));

    /// <summary>
    /// 把预录缓冲采纳的那一段搬进会话目录，并登记成**第 0 段**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>它占掉序号 0</b>，所以正式的第一次开录自然从 <c>segment-001.mkv</c> 起
    /// —— 靠的是 <see cref="StartCaptureAsync"/> 把 <see cref="ClosedSegmentCount"/>
    /// 交给 ffmpeg 当 `<c>-segment_start_number</c>`，不是硬写。
    /// 两处各写一个数字的话，将来加一段顺序会撞号；而这里撞号的后果比从前更重 ——
    /// 输出带 `-y`，同号会把预录那几秒**直接覆盖掉**（2026-10-05 实测）。
    /// </para>
    /// <para>
    /// ⚠️ <b>它不经过 <c>ICameraCapture</c></b>（本来就录好了，只是一个文件），
    /// 所以没有「起进程 → 确认开起来了」那一步，也不需要编码器。
    /// </para>
    /// <para>
    /// 搬失败**不让开录失败**（那一段是「多出来的几秒」，开录本身才是主任务），
    /// 但**调用方必须知道成没成** —— 时钟与起录时刻要不要往前挪全看它，
    /// 而「挪了却没有画面」是一条**看起来没错**的时间轴错误。所以返回成败，
    /// 并按 <see cref="LastProblem"/> 报出去（I3：用户要看得见）。
    /// </para>
    /// </remarks>
    /// <returns>真的搬进去并登记了返回 <see langword="true"/>。</returns>
    private bool AdoptLeadingClip(AdoptedClip buffered)
    {
        var destination = Path.Combine(
            _workspace.SessionDirectory(SessionId), LeadingSegmentFileName);

        try
        {
            File.Move(buffered.Path, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastProblem = $"预录的那几秒没能保留（{ex.Message}），这一段从扫码后开始。";
            return false;
        }

        lock (_gate)
        {
            // ⚠️ `ClosedSegmentCount` 是后面算序号的依据 —— 它必须在这里就变，
            // 而不是等收尾（`StartCaptureAsync` 紧随其后）。
            _closedSegments.Add(new SegmentProduct(
                LeadingSegmentSequence, destination,
                buffered.StartedAt, buffered.StartedAt + buffered.Duration));
        }

        return true;
    }

    /// <summary>采纳段在会话里的位置 —— 它**必须是第 0 段**（时间轴最前面那一段）。</summary>
    private const int LeadingSegmentSequence = 0;

    private const string LeadingSegmentFileName = "segment-000.mkv";

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

                // ⚠️ 这里原来还有一段「到点滚下一段」：关掉旧进程、重开新进程。
                // T17 起**没有了** —— 分段由 ffmpeg 自己按时长滚（`-f segment`），
                // 中途停进程正是段边界那 1~2 秒画面空洞的来头。
                // 循环于是只剩一个职责：**决定该不该停**（上面那几条 break）。
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
                spec: _options.Spec,
                // 发货/退货的标签（设计图 `_35`）。**在会话建好时就定下来的那个**，
                // 不是此刻的档位 —— 换件的旧段是在后台收尾的，见 `BusinessType`。
                businessType: BusinessType);

            State = outcome.State;
            Outcome = outcome;

            if (outcome.Succeeded)
            {
                await _workspace.MarkFinalizedAsync(SessionId, cancellationToken);

                // ★ T21：成品已经进归档层了 ⇒ `work/` 里那份源 MKV 只剩下一个身份：占地方。
                // 清理层只清归档层的成品、从来不碰 `work/`，留着它就是无界增长。
                //
                // ⚠️ **没发上去就不丢** —— 那时留着它是 I2 的方向（宁可多占地方）。
                // 那件事已经有人记了：`ArchiveRelay.PublishAsync` 记一条 Warn，
                // 界面还会在设置页挂一条（`ShowArchiveRelayFailure`）。
                if (outcome.ArchiveComplete)
                {
                    _workspace.DiscardSessionDirectory(SessionId);
                }
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

    /// <summary>
    /// 起这一场的采集：**一个 ffmpeg 进程跑到底，由它自己按时长滚段**（T17）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>原来的走法是「每段一个进程」</b>：到点先关旧的（停 ffmpeg ⇒ 相机被放开），
    /// 再开新的（重开 ffmpeg ⇒ 重开相机）。而开一次相机实测 1~1.5 秒
    /// （见 <c>FfmpegCameraCapture.ConfirmStartedAsync</c>）⇒
    /// <b>每段边界有 1~2 秒真实的画面空洞</b>：默认 1 分钟一段 ⇒ 一小时少 60~120 秒，
    /// 而且少在哪儿不可知。手机端没有这个问题（编码器全程不停、只换封装器）。
    /// </para>
    /// <para>
    /// ⚠️ <b>代价说清楚（这一条不能只写在提交信息里）</b>：分段什么时候滚出来
    /// 只有 ffmpeg 与盘知道，会话**不再有「段封闭」那一刻可挂钩** ⇒
    /// 录制中不再逐段更新 <c>session.json</c>，收尾时改从目录里数
    /// （见 <see cref="CapturedSegments"/>）。进程被杀时那批分段**照样进得了收尾**
    /// —— 走的是 T20 那条「扫目录捞没登记的分段」的路，只是那时的时间戳只能取
    /// 文件时间（进程都没了，可信时钟的读数也没了），不是这一次这条路上的口径。
    /// </para>
    /// <para>
    /// ⚠️ <b>顺带消掉一个已知竞态</b>：原来「先关旧段 → 等新进程起来（几十毫秒）→
    /// 才发布新段」，收尾正好挤进那个窗口时谁都认领不到（见
    /// <see cref="CloseCurrentSegmentAsync"/> 的旧注释）。现在段在会话开始时就发布一次，
    /// 没有那个窗口了。
    /// </para>
    /// </remarks>
    private async Task StartCaptureAsync(string encoder, CancellationToken cancellationToken)
    {
        var firstSequence = ClosedSegmentCount;

        // 这一场采集从哪儿起 —— 收尾时按它 + 段号推每一段的起止。
        _captureStartedOffset = Elapsed;

        WriteWatermark();

        _currentProcess = await _capture.StartAsync(
            _source,
            Path.Combine(_workspace.SessionDirectory(SessionId), SegmentFilePattern),
            encoder,
            _options.Microphone,
            cancellationToken,
            segmentSeconds: (int)Math.Ceiling(_options.SegmentDuration.TotalSeconds),
            // ⚠️ **必须给**：采纳了预录缓冲时 0 号已经躺在目录里，
            // 而 ffmpeg 默认从 0 起、这个输出又带 `-y` —— 不加它会把开场那几秒覆盖掉。
            segmentStartNumber: firstSequence);

        Interlocked.Exchange(ref _openSegment, new OpenSegment(firstSequence));
    }

    /// <summary>
    /// 给这一场写一份水印字幕（规格 §3.6.2）。
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
    /// ⚠️ <b>一**整场**一份</b>（T17）：段边界不再停进程，也就没机会换字幕
    /// —— <c>ass</c> 滤镜在开进程那一刻就把文件读进去了。而秒针**跨段接着走**
    /// （实测：`-reset_timestamps 1` 只动封装器写出的时间戳，滤镜看到的 pts 是连续的），
    /// 所以一份覆盖整场是对的，不是将就。
    /// </para>
    /// <para>
    /// 覆盖时长取「整场上限 + 2 分钟余量」：段会按时长滚动，最后一段可能超出一点
    /// （滚动要等关键帧）。**少了余量就会出现「最后几秒没有水印」**——
    /// 而那种「部分缺失」比全都没有更难被发现。
    /// </para>
    /// </remarks>
    private void WriteWatermark()
    {
        try
        {
            var start = _trustedClock?.Now ?? DateTimeOffset.UtcNow;

            var (width, height) = _options.Spec?.Size ?? (1280, 720);

            File.WriteAllText(
                AssWatermark.PathFor(Path.Combine(
                    _workspace.SessionDirectory(SessionId), SegmentFilePattern)),
                AssWatermark.Build(start, _waybill, WatermarkCoverage, width, height));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastProblem = $"这一场没有水印（字幕文件写不出来：{ex.Message}）";
        }
    }

    /// <summary>
    /// 水印字幕要覆盖多久。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>兜底档位是「关闭」时这一场没有上限</b>，而字幕得有个头
    /// —— 它要按秒排下去（见 <see cref="AssWatermark.Build"/>）。
    /// 取一个远够用的天花板：超出之后就没有水印了（画面照录），
    /// 而「没有上限」这件事在产品上本来就是「用户自己会来停」的意思。
    /// </remarks>
    private TimeSpan WatermarkCoverage =>
        (_options.MaxDuration == RecordingSessionOptions.NoFallback
            ? WatermarkCeiling
            : _options.MaxDuration) + TimeSpan.FromMinutes(2);

    /// <summary>水印字幕覆盖时长的天花板。见 <see cref="WatermarkCoverage"/>。</summary>
    private static readonly TimeSpan WatermarkCeiling = TimeSpan.FromHours(12);

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

        // ⚠️ 降级也要**可见**（I3）：音频那一路没接上时这一场照录，但用户有权知道
        // 它没有声音 —— 否则「事后发现整批货都没声音」就成了一次静默失败。
        // 顺序在退出码之前：两句都成立时（比如摄像头真的坏了），
        // 退出码那句才是真正的原因，让它盖掉这里的降级说明。
        if (process.StartupWarning is { Length: > 0 } warning)
        {
            LastProblem = warning;
        }

        if (exitCode is not null and not 0)
        {
            // 退出码非 0 说明采集中途出过事。文件可能仍在（ffmpeg 常留下部分内容），
            // 所以照样登记 —— 由收尾器的「实际解码校验」判它到底能不能用，
            // 而不是在这里替它下结论（那正是「编译绿≠正确」的翻版）。
            // ⚠️ 说的是「这一场」不是「这一段」：T17 起一个进程跑一整场，
            // 它带着**这一场全部的分段**（分段由 ffmpeg 自己滚）。
            LastProblem = $"采集进程以退出码 {exitCode} 结束，这一场可能不完整。";
        }
    }

    /// <summary>
    /// 收这一场采集：停进程，然后**从盘上**数出它落了哪些分段（T17）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 它是收尾唯一入口（<see cref="StopAsync"/>）的第一步，所以这里的分段清单
    /// 就是 <c>outcome.Segments</c> 的全部来源（I9：不得有旁路）。
    /// </para>
    /// <para>
    /// ⚠️ <b>认领用一次原子交换，不是「先读、后面再清」</b>：编排循环的 finally
    /// 与 <see cref="StopAsync"/> 可能在同一瞬间都想收这一场（用户正好点【结束】）。
    /// 先读后清会让两边都通过判据，把同一批分段登记两次 —— 重号会让时间轴错位。
    /// </para>
    /// <para>
    /// ⚠️ <b>起点那次还有一个已知的、有界的竞态</b>（2026-10-01 查出来，没修）：
    /// <see cref="StartCaptureAsync"/> 是「先发布 <c>_currentProcess</c>、再发布
    /// <c>_openSegment</c>」，收尾正好挤进这两句之间时这里认领不到。后果有界 ——
    /// 那个进程刚起来、几乎没有内容，且会被 <c>DisposeAsync</c> 收掉不留孤儿进程；
    /// 留在目录里的空壳会被 T20 那条路按「0 字节不算」滤掉。
    /// （**原来那个「先关旧段、再开新段」的竞态已经随 T17 消失**：段不再中途重开。）
    /// </para>
    /// </remarks>
    private async Task CloseCurrentSegmentAsync(CancellationToken cancellationToken)
    {
        var claimed = Interlocked.Exchange(ref _openSegment, null);
        if (claimed is not { } open)
        {
            return;
        }

        // ⚠️ 先读停录时刻、**再**停进程：停进程要走优雅停机（发 q、等它写完），
        // 那几百毫秒不是录制时间。这一条与 T17 之前逐字一致。
        var endedAt = Elapsed;
        await ReleaseCaptureAsync(cancellationToken);

        // 水印字幕**用完就删**：它是这一场的临时素材，不是产物。
        // 留着的话它会跟着会话目录进归档（而归档里多一个 .ass 是垃圾），
        // 也会让「盘上占了多少」那个数字虚高一点。
        // 顺序在 `ReleaseCaptureAsync` **之后**：ffmpeg 还拿着它的话删不掉。
        TryDeleteWatermark(Path.Combine(_workspace.SessionDirectory(SessionId), SegmentFilePattern));

        var segments = CapturedSegments(open, endedAt);

        lock (_gate)
        {
            _closedSegments.AddRange(segments);

            if (_manifest is not null)
            {
                _manifest = _manifest with
                {
                    Segments =
                    [
                        .. _manifest.Segments,
                        .. segments.Select(s => new SegmentManifest(
                            s.Sequence, Path.GetFileName(s.SourcePath),
                            s.StartedAt.ToString("O"), s.EndedAt.ToString("O"))),
                    ],
                };
            }
        }

        // 立刻落盘：收尾中途进程被杀，这批分段仍能被孤儿恢复找到。
        if (_manifest is not null)
        {
            await _workspace.WriteManifestAsync(_manifest, cancellationToken);
        }
    }

    /// <summary>
    /// 这一场采集落下来的分段 —— **从盘上数**，时刻是**算**出来的（T17）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>为什么从盘上数</b>：分段是 ffmpeg 自己按时长滚的，会话没有「段封闭」
    /// 那一刻可挂钩（见 <see cref="StartCaptureAsync"/>）。目录里的
    /// <c>segment-*.mkv</c> 就是这一段录下来的全部东西，包括最后那一片。
    /// </para>
    /// <para>
    /// ⚠️ <b>为什么时刻是算的、不是读文件时间</b>：文件时间是**墙钟**，
    /// 而本仓的时间轴只有一个来源 —— 可信时钟（I11：用户改系统时间不得改变
    /// 视频里的时间）。两者差的可能远不止几毫秒（可信时钟锚在外部时间上）。
    /// 分段既然是按名义时长滚的，第 k 片的起点就是「这一场采集的起点 + k × 单段时长」
    /// —— 与「停下来重开」那套不同，这里没有开相机那 1~1.5 秒的空洞，
    /// 所以中间那几段可以照名义算。<c>-segment_time</c> 在到点后的**下一个关键帧**
    /// 上切，而 <c>-g</c> 是 30 帧 = 1 秒 ⇒ 误差在 1 秒以内（2026-10-05 实测：
    /// 60 秒档下各片就是 60 秒上下，**不累积漂移**）。
    /// </para>
    /// <para>
    /// ⚠️ 最后那一片的终点取**停录那一刻**：它通常比名义时长短一截。
    /// </para>
    /// </remarks>
    private List<SegmentProduct> CapturedSegments(OpenSegment open, TimeSpan endedAt)
    {
        var directory = _workspace.SessionDirectory(SessionId);
        var sequences = _workspace.ListSegmentSequences(SessionId);
        var last = sequences.Count > 0 ? sequences[^1] : -1;

        var products = new List<SegmentProduct>();

        foreach (var sequence in sequences)
        {
            // 预录采纳的那一段（0 号）早在开录时就已经登记过了，这里不重来一遍。
            if (sequence < open.FirstSequence)
            {
                continue;
            }

            var startedAt = _startedAt + _captureStartedOffset
                + (sequence - open.FirstSequence) * _options.SegmentDuration;

            var stoppedAt = sequence == last
                ? _startedAt + endedAt
                : startedAt + _options.SegmentDuration;

            // 兜底：盘上多出一片不该在的、或者时刻对不上时，宁可时长记成 0，
            // 也不能往索引里写一条**时间倒流**的证据。
            //
            // ⚠️ 但它**不许静默**（AGENTS §6.1）：走到这条支路说明**盘上出现了
            // 对不上的东西**（同一会话目录里从前一次尝试留下的片、或者被人塞进来的），
            // 而这种情况一旦发生，用户事后要问的正是「哪一片」。只说「录好了」是 I3 不允许的。
            if (stoppedAt < startedAt)
            {
                LastProblem = $"收尾时发现一片对不上时间轴的分段（{SegmentFileName(sequence)}），"
                    + "已按零时长登记。";
                stoppedAt = startedAt;
            }

            products.Add(new SegmentProduct(
                sequence,
                Path.Combine(directory, SegmentFileName(sequence)),
                startedAt,
                stoppedAt));
        }

        return products;
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
    /// <param name="backdate">
    /// 把起点再**往前挪**这么多（规格 §3.1.3 的预录缓冲采纳了多少就挪多少）。
    /// 默认 <see cref="TimeSpan.Zero"/> = 今天的行为，一动不变。
    /// <para>
    /// ⚠️ <b>挪起点而不是「把已录时长加上去」</b>：`Elapsed` 是所有下游记账
    /// （打点偏移、时长兜底、闲置提醒、段落起止）的**唯一**基准，
    /// 只改它一处，下游全部自动跟着走；在每一处各加一遍的话，
    /// 迟早漏一处，而漏的那一处表现是「时间对不上，但没人知道是哪一段」。
    /// </para>
    /// <para>
    /// ⚠️ 兜底的计时**也跟着往前挪**（`_nextPromptAt` 用同一个 `Elapsed` 比），
    /// 这正是需求方 2026-10-02 裁定的「计入已录时长封顶，从缓冲起点起算」。
    /// </para>
    /// </param>
    private void RestartClock(TimeSpan backdate = default)
    {
        _clockOrigin = _clock() - backdate;

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
