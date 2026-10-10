using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Scanning;

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

    /// <summary>
    /// 规格 §3.2.5：这个单号近 N 天里录过（**重复单号检测**）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它只是个**提醒** —— 不挡开录、也不改任何录制状态。
    /// 规格那条的硬约束是「全部异步执行，**绝不阻塞开录**」。
    /// </remarks>
    DuplicateWaybill,

    /// <summary>
    /// 规格 §3.3.4：录制到档位时间了，**问**用户要不要停。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>它只是问 —— 录制不中断</b>（规格原话「提示与按钮必须在录制不被中断的
    /// 前提下出现」）。界面收到它要：语音播一遍 + 显示【停止】【继续】两键。
    /// <para>
    /// ⚠️ 与 <see cref="Idle"/> 的区别：那条**只提醒、绝不改录制状态**，
    /// 而这条**用户答了就会停**（答【停止】或 1 分钟没人理）。
    /// </para>
    /// </remarks>
    DurationPrompt,

    /// <summary>
    /// 业务类型（发货 / 退货）换了 —— 设计图 `_35` 顶部的按钮，或扫屏幕上的那张码。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>只对**下一段**生效。</b> 正在录的那一段用的是它开始时的档位
    /// （见 <see cref="RecordingSession.BusinessType"/>）—— 一件包裹不可能录到一半
    /// 从发货变成退货，而按当前档去标签会把它标错。
    /// </para>
    /// <para>
    /// 界面上它要做两件事：把按钮文字换成新档，**并把那张码重画**（码的载荷是
    /// 「切到另一档」，所以两档的码不是同一张）。
    /// </para>
    /// </remarks>
    BusinessTypeChanged,
}

/// <summary>协调器的可调参数。</summary>
/// <param name="DuplicateCheckDays">
/// 重复单号检测回看几天（规格 §3.2.5：「识别到的单号若在**近 N 天内**已有未删除
/// 记录，必须提示。**N 可配置**」）。
/// <para>
/// ⚠️ <b><c>0</c> = 关闭</b> —— 规格说「N 可配置」，那就得允许用户关掉它：
/// 一个关不掉的提醒在连续扫的工位上就是噪声，而噪声会被无视。
/// </para>
/// <para>
/// ⚠️ <b>7 天</b>是<b>本仓标定</b>的默认值（规格没给数）：比「同一批货重复录」
/// 的时间跨度长，又短于退货周期，够用且不至于把半年前的旧单号翻出来。
/// </para>
/// </param>
public sealed record CoordinatorOptions(
    CameraSource Source,
    string SourceDeviceId,
    string Encoder,
    int DuplicateCheckDays = 0);

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
        ITrustedClock? trustedClock = null,
        License.LicenseService? license = null,
        // 重复单号检测（规格 §3.2.5）：问一句「这个单号近 N 天录过没有」。
        //
        // ⚠️ 做成**注入的委托**而不是让协调器持有索引 —— 与 `RecordingSession` 的
        // `durationPrompted` 同一个手法：这一层不该知道检索那一层的存在（它只管
        // 「一次工作」的编排）。生产那边接的是 `RecordingSearch`（已经实现了
        // 「按单号精确查」+ 单号归一化，不必再写一份）。
        Func<WaybillNumber, CancellationToken, Task<IReadOnlyList<Index.RecordingEntry>>>? duplicateProbe = null)
    {
        _duplicateProbe = duplicateProbe;
        _clock = clock ?? NewDefaultClock();
        _delay = delay ?? Task.Delay;
        _workspace = workspace;
        Capture = capture;
        Encoder = options.Encoder;
        _finalizer = finalizer;
        _diskGuard = diskGuard;
        _punches = punches;
        _logger = logger;
        _policy = policy;
        _options = options;
        _scanErrors = scanErrors;
        _trustedClock = trustedClock;
        _license = license;
    }

    /// <summary>
    /// 许可（`docs/04-许可设计.md`）。<see langword="null"/> = 不设闸（测试与不关心它的装配）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>L5：未激活 / 校验失败 → 不能录制</b>（本机与远程都不行）。
    /// ⚠️ <b>L8：它只挡住**新录**</b> —— 已有录像的检索、回放、导出、交付一条都不看它。
    /// </remarks>
    private readonly License.LicenseService? _license;

    /// <summary>现在允不允许开始新的录制（许可那一半）。</summary>
    private string? LicenseBlockedReason =>
        _license is null || _license.Status.Activated ? null : _license.Status.FailureReason;

    /// <summary>
    /// 可信时钟（规格 §3.6.4）。<see langword="null"/> = 不设闸。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不传的那些装配（大多是测试）**行为与从前完全一致** ——
    /// 加闸不该顺带把几十条不相干的用例改成「必先校准」。
    /// 生产路径由 `DesktopServices` 传真的那个。
    /// </remarks>
    private readonly ITrustedClock? _trustedClock;

    /// <summary>「这个单号近 N 天录过没有」—— 见构造函数的说明。</summary>
    private readonly Func<WaybillNumber, CancellationToken, Task<IReadOnlyList<Index.RecordingEntry>>>?
        _duplicateProbe;

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
    /// 待扫期那一路：取景识码（规格 §3.2.1）＋ 预录缓冲（规格 §3.1.3）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 可以为 null —— 没摄像头、既不开识码也不开预录时就不装它。
    /// 装上了的话，<see cref="StartWork"/> 会起它，扫到单号自动开录。
    /// </para>
    /// <para>
    /// ⚠️ <b>它同时也决定了「工作时段相机归谁」</b>：以前只有录制中才占相机，
    /// 现在**待扫期间也占着**（预录要求，2026-10-02 需求方知情并裁定）。
    /// 别的地方要开相机（规格探测、测试摄像头、向导）都得先让路，
    /// 见 <see cref="PrepareCaptureAsync"/> 与装配层那两处入口的说明。
    /// </para>
    /// </remarks>
    public Camera.PrerecordController? Prerecord { get; set; }

    /// <summary>
    /// 预录缓冲时长（规格 §3.1.3）。<see cref="TimeSpan.Zero"/> = 关闭。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 装配层按设置填。取值发生在**每次开始待扫**那一刻（见
    /// <see cref="StartPrerecordAsync"/>），所以改动是「下次开始工作生效」。
    /// </para>
    /// <para>
    /// ⚠️ <b>它可以非零而 <see cref="Prerecord"/> 为 null</b>（没摄像头 / 没 ffmpeg）
    /// —— 那时整个预录都不存在，这是装配层已经判过的事，这里不再重复判。
    /// </para>
    /// </remarks>
    public TimeSpan PrerecordBuffer { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// **录制期识码**（开录之后谁在读码：见 <see cref="Camera.RecordingRecognition"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>它归协调器管只为一件事：开段那一刻要把这一码先喂进识码闸</b>
    /// （见 <see cref="StartSegmentAsync"/> 里那一句）。放在装配层订通知也做得到，
    /// 但那样这条「顺序」就没有任何用例盖得住了 —— 而它错的表现是
    /// <b>同码停模式刚开录就停</b>。
    /// </para>
    /// <para>
    /// 帧**不从这里来**：帧由采集对象投（装配层挂在 <c>FfmpegCameraCapture.FrameObserver</c> 上），
    /// 所以它**不跟着采集对象替换**（规格重探换采集对象时要重新挂 observer，那边有说明）。
    /// </para>
    /// </remarks>
    public Camera.RecordingRecognition? Recognition { get; set; }

    /// <summary>采集那一层。**可替换** —— 见 <see cref="PrepareCaptureAsync"/>。</summary>
    /// <remarks>
    /// ⚠️ 做成可写属性而不是构造参数是因为**录制规格能在运行中改**（规格 §3.1.7
    /// 「改了下次开始工作生效」）。而规格本身钉在采集对象里
    /// （`FfmpegCameraCapture._spec`，决定输入侧的 `-video_size` / `-framerate`），
    /// 所以换规格就**必须换掉这个对象**。2026-09-30 之前它是 `readonly` 字段，
    /// 后果是「改完规格录出来还是上一档」—— 见 <c>docs/实现决策.md</c> §80。
    /// </remarks>
    public ICameraCapture Capture { get; set; }

    /// <summary>
    /// 开段时喂给 ffmpeg `-c:v` 的那个编码器名。**可替换**。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Capture"/> 同一条理由，而且要**一起换**：规格探测出来的
    /// 「哪个编码器真编得出来」是按编码选的（`RecordingSpec.EncoderCandidates`），
    /// 换了编码不换它就会「选 H.265、录 H.264」——
    /// 见 <c>docs/实现决策.md</c> §79。
    /// </remarks>
    public string Encoder { get; set; }

    /// <summary>
    /// 开一段**之前**的准备动作（装配层挂：规格改了就在这里重探并换掉
    /// <see cref="Capture"/> / <see cref="Encoder"/> / <see cref="SessionOptions"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>为什么挂在「开段前」而不是「改设置时」</b>：
    /// 规格 §3.1.7 的生效时机是「**改了下次开始工作才生效**」，而重探
    /// **要真开一次相机**（`FfmpegSpecProbe`：真录一小段再验解码）。
    /// 改设置时相机可能正被取景识码占着（相机是独占的，实测）——
    /// 那时重探会**误判成跑不通**，把能用的组合说成不能用，那是比不做更坏的结果。
    /// 而这一刻（取景已按下面那行停掉、采集还没起）相机必然是空的。
    /// </para>
    /// <para>
    /// 没挂它 = 不重探（测试与不在乎规格热改的装配）。挂了的话，
    /// **规格没变时它必须立刻返回** —— 它在每一次开段（也就是每一次扫码）的路径上，
    /// 白花的时间是用户站在那里等的。
    /// </para>
    /// </remarks>
    public Func<CancellationToken, Task>? PrepareCaptureAsync { get; set; }

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

    /// <summary>
    /// 当前的业务类型（设计图 `_35` 顶部那个「发货 / 退货」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>它不属于某一段录像</b>，只是「下一段按哪个档录」的当前选择 ——
    /// 每一段在<b>开段那一刻</b>把它抄一份到自己身上
    /// （<see cref="RecordingSession.BusinessType"/>），
    /// 因为换件之后旧段还在后台收尾，那时再读这个属性已经可能被切过了。
    /// </para>
    /// <para>
    /// ⚠️ <b>不持久化</b>：每次启动回到「发货」。这是工位上绝大多数时候的档，
    /// 而把它存起来会让某次切成退货之后**悄悄一直退货** ——
    /// 那种错在界面上只差一个字，录出来的标签却全反了。
    /// </para>
    /// </remarks>
    public BusinessType CurrentBusinessType { get; private set; } = BusinessType.Outbound;

    /// <summary>切换业务类型（界面上的按钮）。返回切换后的档。</summary>
    public BusinessType ToggleBusinessType() => SetBusinessType(
        CurrentBusinessType == BusinessType.Return ? BusinessType.Outbound : BusinessType.Return);

    /// <summary>
    /// 把业务类型设成这一档。**幂等** —— 已经是这一档就什么都不做、也不报「已切换」。
    /// </summary>
    /// <remarks>
    /// 界面上那个按钮与屏幕上那两张条码都走这里（条码那条路见 <see cref="SubmitAsync"/>）。
    /// 幂等很重要：连续扫两次同一张码是很自然的动作（没看清扫上没有），
    /// 每次都说一遍「已切到退货」会让人以为它真的切了两下。
    /// <para>
    /// ⚠️ 正在录的那一段**不跟着改** —— 见 <see cref="CurrentBusinessType"/>。
    /// </para>
    /// </remarks>
    public BusinessType SetBusinessType(BusinessType type)
    {
        if (CurrentBusinessType == type)
        {
            return CurrentBusinessType;
        }

        var previous = CurrentBusinessType;
        CurrentBusinessType = type;

        // 留痕（`AGENTS.md` §6.1）：标签是「这条录像到底算发货还是退货」的唯一来源，
        // 而它是**人在某一刻按下的**——事后要知道那一段为什么被标成退货，只有这条日志。
        _logger.Log(LogLevel.Info, "工作",
            $"业务类型：{ScanCommand.Describe(previous)} → {ScanCommand.Describe(type)}",
            new Dictionary<string, object?>
            {
                // 正在录的那一段**不受影响**，但要记下来 —— 这正是事后
                // 「为什么这一段还是发货」的那个解释。
                ["正在录"] = _current?.Waybill?.Value,
            });

        Raise(CoordinatorNoticeKind.BusinessTypeChanged, null, $"已切到{ScanCommand.Describe(type)}。");

        return CurrentBusinessType;
    }

    /// <summary>当前段的已录时长；没有在录时为零。</summary>
    public TimeSpan Elapsed => _current?.Elapsed ?? TimeSpan.Zero;

    /// <summary>当前段的会话标识；没有在录时为 <see langword="null"/>。</summary>
    public string? CurrentSessionId => _current?.SessionId;

    // ⚠️ 这里原来有一个 `CurrentClosedSegmentCount`（录制中的封闭段数），T17（2026-10-05）
    // 起**删掉**：滚段交给 ffmpeg 自己做了，会话**不再有「段封闭」那一刻**
    // （见 `RecordingSession.StartCaptureAsync` 的说明），它从此恒为 0 ——
    // 留着就是一个「看着像可观测性、其实永远读 0」的假口子。
    // 录制中「盘上现在有几片」仍旧看得到：`RecordingWorkspace.ListSegmentSequences`。

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

        // ⚠️ **未激活不得录制**（`docs/04-许可设计.md` L5）。
        // 与上面那道校时闸同一个形状：**当场把原因说出来**，而不是「点了没反应」。
        if (LicenseBlockedReason is { } licenseReason)
        {
            _logger.Log(LogLevel.Warn, "许可", $"拒绝开始工作：{licenseReason}");
            Raise(CoordinatorNoticeKind.FinalizeFailed, null, licenseReason);
            return;
        }

        BeginWorking();

        _logger.Log(LogLevel.Info, "工作", "开始工作");
        Raise(CoordinatorNoticeKind.WorkStarted, null, "开始工作。");

        // 开始取景识码（＋ 预录缓冲）—— 扫到单号会自动开录
        // （规格 §4.1 的状态机就是从「识别到单号」起算的）。
        // 没装它时用户仍可手打单号。
        _ = StartPrerecordAsync();
    }

    /// <summary>
    /// 起待扫那一路（识码 ＋ 预录）。没装配就是空操作。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>缓冲时长在**这里**读</b>（不是在装配时读一次存起来）：
    /// 用户改了档位、下次开始工作就该按新的来 —— 与
    /// <see cref="SessionOptions"/> 的「下次录段生效」同一个口径。
    /// </para>
    /// <para>
    /// 没人 await 它：<c>StartAsync</c> 自己把失败变成一条日志与
    /// <c>Failed</c> 事件（I3），不会抛。
    /// </para>
    /// </remarks>
    private Task StartPrerecordAsync()
    {
        if (Prerecord is not { } prerecord)
        {
            return Task.CompletedTask;
        }

        return prerecord.StartAsync(new Camera.PrerecordSetup(
            PrerecordBuffer, Encoder, SessionOptions.Spec, _workspace.PrerecordDirectory));
    }

    /// <summary>
    /// 把**相机让开**给别的窗口用（配置向导那几步要开相机），待扫那一路先停。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>为什么必须让</b>：相机是独占的（§25 实测），而待扫那一路在
    /// **整个工作时段**都占着它（预录要求）。不让的话向导第 2/3 步的取景与
    /// 第 4 步的性能检测会拿到 <c>device already in use</c> ——
    /// 而第 4 步那一次尤其糟：它会把**能用的组合误判成跑不通**
    /// （与 <see cref="PrepareCaptureAsync"/> 里那段同一个病）。
    /// </para>
    /// <para>
    /// ⚠️ <b>它不是「结束工作」</b>：工作状态、当前会话、下一件包裹该怎么走全都不动，
    /// 只是把相机借出去一会儿。向导关掉时调用方要 <see cref="ResumePrerecordAsync"/> 接回来。
    /// </para>
    /// </remarks>
    public async Task PausePrerecordAsync(CancellationToken cancellationToken = default)
    {
        if (Prerecord is not null)
        {
            await Prerecord.StopAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 把相机接回来（借出去之后）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>正在录着的时候不接</b>：那时相机在采集进程手里，起待扫会拿到
    /// <c>device already in use</c>，报出来是一句看不懂的错。
    /// 那种情况下待扫本来就该等着 —— 采完（<see cref="StopCurrentSegmentAsync"/> /
    /// 编排循环）自然会把它接回去。
    /// </remarks>
    public Task ResumePrerecordAsync() =>
        IsWorking && CurrentWaybill is null ? StartPrerecordAsync() : Task.CompletedTask;

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
    /// 用户对时长兜底那次询问的回答（规格 §3.3.4）。
    /// </summary>
    /// <param name="continueRecording">
    /// <see langword="true"/> = 【继续】；<see langword="false"/> = 【停止】。
    /// </param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>没在问的时候调它什么都不做</b>（转发给会话那一层判断）：
    /// 按钮可能晚到一步 —— 用户按下的同时正好扫了下一件，这一问就作废了。
    /// </para>
    /// <para>
    /// ⚠️ 「停止」这条路<b>不在这里收尾</b>：会话自己会在下一圈把循环收掉，
    /// 收尾仍然只走 <see cref="RecordingSession"/> 那一条路（I9：不许有旁路）。
    /// 这一层只负责转发。
    /// </para>
    /// </remarks>
    public void AnswerDurationPrompt(bool continueRecording) =>
        _current?.AnswerDurationPrompt(continueRecording);

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

        // 先停待扫那一路 —— 结束时不该把相机留着开着（隐私指示灯长亮），
        // 更不该留一个还在写分片的 ffmpeg（它占着工作区里的文件）。
        if (Prerecord is not null)
        {
            await Prerecord.StopAsync(cancellationToken);
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
    /// <para>
    /// 打点在这里落盘：规格 §3.2.4 要求**识别到单号即记录该时刻**，
    /// 且必须立即持久化（掉电会丢）。
    /// </para>
    /// <para>
    /// ⚠️ 扫到的也可能是**命令**而不是单号（屏幕上那两张码，设计图 `_35`）——
    /// 那种串在<b>这里</b>就被截住，绝不流进状态机。
    /// 拦在这里而不是拦在装配层，是因为这是**唯一**一个「扫到了」的入口：
    /// 拦在调用方就得在扫码枪 / 摄像头 / 手打三条路上各写一遍，
    /// 少写一条的表现是「那张码把 VLOUT 当成一个单号开录了」。
    /// </para>
    /// </remarks>
    public async Task SubmitAsync(
        WaybillNumber waybill,
        PunchSource source,
        CancellationToken cancellationToken = default)
    {
        // 一次扫码 = 一次活动：闲置计时从这里重算，已响过的提醒也解除。
        // ⚠️ 命令码也算一次活动 —— 扫它的人正站在工位前，不是闲置。
        _lastActivityAt = _clock();
        _idleReminded = false;

        if (ScanCommand.KindOf(waybill.Value) is var command && command != ScanCommandKind.None)
        {
            HandleScanCommand(command);
            return;
        }

        // 单号**形状核查**：面单上除了单号还有分拣码那一类条码，摄像头会认到它们
        // （见 `WaybillPlausibility`）。挡在这里是因为**这是唯一一个「扫到了」的入口**
        // —— 写在调用方就要在扫码枪 / 摄像头两条路上各写一遍。
        //
        // ⚠️ **必须排在命令码那道闸之后**：`VLOUT` / `VLRET` / `VLREC` 一个数字都没有，
        // 放前面会把三条命令全判成假码（扫屏幕上那两张码就整个失灵）。
        //
        // ⚠️ **手打不过这道闸**：手打是用户明确要做的事（§3.2.2 的兜底），
        // 用户自己要录一个短码不该被程序拦下。
        if (source is not PunchSource.ManualEntry && !WaybillPlausibility.IsPlausible(waybill.Value))
        {
            _logger.Log(
                LogLevel.Warn,
                "扫码",
                $"忽略不像单号的内容：{waybill.Value}（来源 {source}；"
                + $"判定规则：数字至少 {WaybillPlausibility.MinDigits} 个、"
                + $"长度不超过 {WaybillPlausibility.MaxLength}、只许数字字母与连字符）");
            return;
        }

        var state = Snapshot();
        var decision = _policy.OnScan(state, waybill);

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

    /// <summary>
    /// 扫到的是一条命令（不是面单）。见 <see cref="ScanCommand"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两个动作都**复用界面上那两条路的同一个方法**（<see cref="SetBusinessType"/> /
    /// <see cref="StartWork"/>）—— 扫一下屏幕上的码与点一下按钮必须是同一件事，
    /// 各写一份必然走岔（比如这里漏了校时那道闸）。
    /// </para>
    /// <para>
    /// ⚠️ <b>命令不落打点、不开段、不写错误扫描记录</b>：它不是工作事件。
    /// </para>
    /// </remarks>
    private void HandleScanCommand(ScanCommandKind command)
    {
        switch (command)
        {
            case ScanCommandKind.SwitchToOutbound:
                SetBusinessType(BusinessType.Outbound);
                break;

            case ScanCommandKind.SwitchToReturn:
                SetBusinessType(BusinessType.Return);
                break;

            case ScanCommandKind.StartWork:
                // 【开始录制】那一下。⚠️ 它**只是开始工作** ——
                // 真正的开录仍要等扫到一张面单，与点按钮完全同一条路。
                _logger.Log(LogLevel.Info, "扫码", $"扫到开始命令（{ScanCommand.StartWork}）");
                StartWork();
                break;

            default:
                // ScanCommandKind.None **不会走到这里**（调用方已经判过）。
                // 留着是因为枚举上加了成员就该被处理 —— 空着比编一个动作好。
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

        // 许可那道闸**也要在这里**（与校时同一个理由：扫码开录是另一条路）。
        if (LicenseBlockedReason is { } licenseReason)
        {
            _logger.Log(LogLevel.Warn, "许可", $"拒绝开录（{waybill.Value}）：{licenseReason}");
            Raise(CoordinatorNoticeKind.FinalizeFailed, waybill, licenseReason);
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

        // ⚠️ **必须先放掉待扫那个进程**（识码 ＋ 预录都在它手里）：相机是独占的
        // （实测），它还开着的话，下面的采集进程会拿到 device already in use。
        //
        // ⚠️ 而「停」与「取缓冲」在这里是**同一件事**：`TakeBufferedAsync` 会先停下
        // 并等到进程真的退出（那正是为了让设备放开），再从它最后那一片里裁出
        // 缓冲窗口 —— 拆成两次调用就有一天会有人只调了「取」忘了「停」。
        AdoptedClip? leading = null;
        if (Prerecord is not null)
        {
            leading = await Prerecord.TakeBufferedAsync(cancellationToken);
        }

        // ⚠️ **重探规格的最后机会** —— 相机已空（上一行刚把取景放掉）、
        // 采集还没起（下一行才建会话），这一刻换 `Capture` / `Encoder` 是安全的。
        // 挂上这个钩子的人负责「规格没变就立刻返回」，见 `PrepareCaptureAsync` 的注释。
        if (PrepareCaptureAsync is not null)
        {
            await PrepareCaptureAsync(cancellationToken).ConfigureAwait(false);
        }

        var session = new RecordingSession(
            _workspace, Capture, _finalizer, _diskGuard,
            _options.Source, _options.SourceDeviceId, SessionOptions,
            trustedClock: _trustedClock,
            // 时长兜底的询问（规格 §3.3.4）。
            //
            // ⚠️ 用**回调**而不是让协调器起一个定时器去轮询会话：这个仓已经因为
            // 「档位关闭时还在白跑的定时器」把既有的 flake 放大过一次
            // （见 `docs/实现决策.md` §39.4）—— 而测试里会造几十个协调器，
            // 每个多一个每秒醒一次的定时器，代价不是省下来的那点 CPU。
            // 回调是会话在**它自己的编排循环里**发现该问了才触发，零额外定时器，
            // 而且与手机端「事件链上判定」同形。
            durationPrompted: () => Raise(
                CoordinatorNoticeKind.DurationPrompt,
                waybill,
                "录制时间即将超时，是否需要停止录制？"),
            // ⚠️ **异常必须留痕**（`AGENTS.md` §6）。会话把问题放在 `LastProblem`
            // 给界面看，而在这之前它**一次都没进过日志** —— 音轨没接上、
            // 水印写不出来这一类，查日志的时候什么都看不到。
            problemReported: problem =>
                _logger.Log(LogLevel.Warn, "录制", $"{waybill.Value}：{problem}"),
            // 发货 / 退货（设计图 `_35`）。⚠️ 传的是**开段这一刻**的档 ——
            // 会话把它抄在自己身上，收尾时按它写标签。换件之后旧段在后台收尾，
            // 那时再读 CurrentBusinessType 已经可能被切过了，会把退货的件标成发货。
            businessType: CurrentBusinessType);

        // ⚠️ 缓冲那一段作为**开场段**交给会话（它占掉 segment-000，于是正式首段
        // 自然是 001；起录时刻与时钟起点也一起往前挪）。见 `AdoptedClip` 的说明。
        await session.StartAsync(waybill, Encoder, cancellationToken, leading);
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

        // ⚠️ **紧接着**把这一码喂进录制期识码的闸（见 `Recognition` 的说明）：
        // 开段这一瞬间面单还在识别框里，不先喂一下的话，下一帧就会被当成
        // 「又扫了一次」—— 同码停模式**刚开录就停**。
        // 排在 `Raise` 之后是承重的：界面在收到 `SegmentStarted` 时就该能看到这一段了。
        Recognition?.PrimeWith(waybill.Value);

        // 重复单号检测（规格 §3.2.5）—— ⚠️ **异步、绝不阻塞开录**（规格原话）。
        // 所以它排在 `Raise(SegmentStarted)` **之后**，而且**没人 await**。
        _ = CheckDuplicateWaybillAsync(waybill, session.SessionId);
    }

    /// <summary>
    /// 这个单号近 N 天录过没有（规格 §3.2.5）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规格那条： 「识别到的单号若在近 N 天内已有未删除记录，必须提示。N 可配置。」
    /// 它的硬约束是「**全部异步执行，绝不阻塞开录**」—— 所以这里是开录之后起的。
    /// </para>
    /// <para>
    /// ⚠️ <b>没人 await 的 Task，异常会被静默丢掉</b>（.NET 默认行为，见
    /// <c>Platform/CrashGuard</c> 的说明）⇒ 这里**必须自己兜住**：
    /// 否则一次索引读失败会**无声无息**，而用户以为检测做了。
    /// </para>
    /// <para>
    /// ⚠️ 时间用**墙钟**（不是可信时钟）：它只是个提醒，不参与任何证据。
    /// 可信时钟是给「录像里的时间」用的（§3.6.3），拿它来算「近 N 天」是误用。
    /// </para>
    /// </remarks>
    private async Task CheckDuplicateWaybillAsync(WaybillNumber waybill, string sessionId)
    {
        if (_duplicateProbe is null || _options.DuplicateCheckDays <= 0)
        {
            return;
        }

        try
        {
            var entries = await _duplicateProbe(waybill, CancellationToken.None);
            var since = DateTimeOffset.UtcNow.AddDays(-_options.DuplicateCheckDays);

            var recent = entries
                // ⚠️ 排掉**正在录的这一段**：它已经在索引里了吗？没有 ——
                // 索引是收尾时才写的。但换件那条路（连续扫）里，上一件刚入库，
                // 而同一张面单被复扫时就会命中它自己。排掉更稳。
                .Where(e => !string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
                .Where(e => e.StartedAt >= since)
                .OrderByDescending(e => e.StartedAt)
                // ⚠️ **按会话去重**（2026-10-10，D2）：一次会话会被分成多段
                // （时长兜底、磁盘将满都会分段），每段在索引里各占一行 ——
                // 不去重的话「这个单号录过 1 次」会被说成「录过 3 次」，
                // 而界面那句「录过 N 次」正是用户拿来判断是不是重复录件的。
                // 每组保的是**最近的那条**：上面已按 StartedAt 降序，取首条即可，
                // 而 `recent[0]`（也是最近一条）的那个用法不受影响。
                .GroupBy(e => e.SessionId, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            if (recent.Count == 0)
            {
                return;
            }

            _logger.Log(LogLevel.Info, "录制", $"重复单号：{waybill.Value} 近 {_options.DuplicateCheckDays} 天录过",
                new Dictionary<string, object?> { ["次数"] = recent.Count });

            // ⚠️ **短句**（需求方 2026-10-10：原来那句「在最近 7 天里录过 10 次
            // （最近一次 …）。核对一下是不是重复录件或者单号扫错了。」太长，
            // 而它要上的是**只占一行**的动态栏）。次数与最近一次时刻仍在**日志**里
            // （上面那条「重复单号：… 近 N 天录过 N 次」），不算丢。
            Raise(CoordinatorNoticeKind.DuplicateWaybill, waybill,
                $"{waybill.Value} 重复录制，请检查。");
        }
        catch (Exception ex)
        {
            // 检测失败**不该影响任何事** —— 它只是个提醒（I3 的反面：
            // 这一条不是「必须让用户看见」的那种事，它连录制都没参与）。
            _logger.Log(LogLevel.Warn, "录制", $"重复单号检测没做成：{ex.Message}");
        }
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

        // 旧段也要有一条收尾的话 —— 换件就是「一段结束了、下一段开始了」两件事。
        //
        // ⚠️ 这一条**只说结束**：`StartSegmentAsync` 下面会报「开始录制 next。」，
        // 原来这里再报一句「换件，开始录制 next。」，于是动态栏一次换件出现
        // **两条「开始录制」**，而旧单号一句收尾都没有
        // （需求方 2026-10-10：开始录制只显示一条、结束录制也要显示一条）。
        //
        // ⚠️ 排在 `StartSegmentAsync` **之前**：动态栏是「最新在上」，
        // 后报的那条会盖在前一条上面，顺序反了就念成「先开始、后结束」。
        if (previous.Waybill is { } finished)
        {
            Raise(CoordinatorNoticeKind.SwitchedWaybill, finished, $"换件：结束录制 {finished.Value}。");
        }

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

        // ⚠️ 顺序是承重的（照 <see cref="SwitchSegmentAsync"/>）：**先把相机放掉、
        // 把待扫接回来，再收尾**。收尾要 remux ＋ 解码校验，几秒起步；等它做完才
        // 接待扫的话，那几秒里下一件包裹扫不进来 —— 用户会以为扫码枪/摄像头坏了。
        // 相机是独占的，所以「放设备」必须排在「起待扫」之前。
        //
        // ⚠️ 收尾本身仍然**等**：调用方要拿它的结论，而且 I9 说了收尾只此一条路。
        // 这里提前的只是「放设备」这一步（<see cref="RecordingSession.ReleaseCaptureAsync"/>
        // 那一侧也是照这个用法写的）。
        await session.ReleaseCaptureAsync(cancellationToken);

        // 相机随「放设备」就空了 —— 还在工作中的话把待扫接回去（识码 ＋ 预录，
        // 下一件包裹的缓冲就从这一刻重新起算）。
        if (IsWorking)
        {
            _ = StartPrerecordAsync();
        }

        var outcome = await session.StopAsync(reason, cancellationToken);
        ReportFinalize(session, outcome);

        // 动态栏要有始有终：开录那条是 `StartSegmentAsync` 里的 "开始录制 X。"，
        // 这里补对称的一条。放在**收尾之后** ——「结束录制」应当是收尾有结论才说，
        // 而不是一按就报（2026-10-10 需求方要求）。
        //
        // ⚠️ 这里是所有「停一段」的唯一落点（手动 / 同码复扫 / 换件以外的自动停），
        // 见 `SubmitAsync` 的 `WorkDecision.StopSegment` 与 `StopWorkAsync`。
        if (session.Waybill is { } stopped)
        {
            Raise(CoordinatorNoticeKind.SegmentStopped, stopped, $"结束录制 {stopped.Value}。");
        }

        await session.DisposeAsync();

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
            await session.RunAsync(Encoder).ConfigureAwait(false);
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

        // 还在工作中的话把待扫接回去，否则下一件包裹扫不进来
        // （与 StopCurrentSegmentAsync 同一条理由）。
        if (IsWorking)
        {
            _ = StartPrerecordAsync();
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
                // ⚠️ 需求方 2026-10-10 改的措辞：**带上实际扫到的那个单号**
                //（原来那句只有「面单错误」，事后翻动态栏不知道错在哪一码上）。
                // 单号在这条决策里**是可空的**（类型上没有更强的保证）——
                // 拿不到就退回规格 §3.3.2 的原话，**不编一个**。
                Raise(CoordinatorNoticeKind.WrongWaybill, announce.Waybill,
                    announce.Waybill is { } wrong
                        ? $"{wrong.Value} 单号错误，请检查。"
                        : "面单错误，请扫描正确面单");

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

        // ⚠️ **待扫那一路也必须停**（2026-10-02 补）：它是**新的**相机占用者 ——
        // 从前待扫进程由 AppHost 自己收，协调器不管；而它现在还会写分片文件。
        // 不停的话退出时留一个占着相机、还在写盘、并且**没人再管**的孤儿 ffmpeg。
        if (Prerecord is not null)
        {
            await Prerecord.StopAsync(CancellationToken.None);
        }

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
