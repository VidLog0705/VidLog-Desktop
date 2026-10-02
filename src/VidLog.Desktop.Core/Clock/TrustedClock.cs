using System.Text.Json;
using System.Text.Json.Serialization;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Clock;

/// <summary>校准的来源（规格 §3.6.4：两个来源，取到过任何一个即算校准）。</summary>
public enum CalibrationSource
{
    /// <summary>公网时间服务（HTTP `Date` 头）。</summary>
    PublicTime,

    /// <summary>归档回执里的外部时间锚（`ReceiptPayload.TimeAnchor`）。</summary>
    ArchiveReceipt,
}

/// <summary>落盘的校准状态。</summary>
/// <remarks>
/// ⚠️ <b>要落盘</b>：规格 §3.6.4「已校准状态**落盘持久化**，之后离线照常录制」——
/// 不落盘的话，一台交付之后从没联过网的机器重启一次就再也录不了，
/// 而它其实早就校准过了。
/// </remarks>
public sealed record CalibrationState
{
    /// <summary>外部时间锚（用户改不了）。</summary>
    public DateTimeOffset? AnchorUtc { get; init; }

    /// <summary>拿到锚那一刻的**单调**读数。</summary>
    public double? MonotonicAtAnchor { get; init; }

    public CalibrationSource Source { get; init; } = CalibrationSource.PublicTime;

    public DateTimeOffset? CalibratedAtUtc { get; init; }

    /// <summary>上次退出（或上次核对）时的墙钟。</summary>
    /// <remarks>
    /// ⚠️ 它与 <see cref="LastSeenMonotonic"/> 一起用来判跳变（规格 §3.6.3：
    /// 「每次启动都要核对一次挂钟与上次退出时留下的记录是否自洽」）。
    /// </remarks>
    public DateTimeOffset? LastSeenWallClockUtc { get; init; }

    public double? LastSeenMonotonic { get; init; }

    /// <summary>
    /// <see cref="LastSeenMonotonic"/> 与 <see cref="MonotonicAtAnchor"/>
    /// 是**哪一把尺子**量出来的。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>没有它就会误判</b>：这两个数从前是「本进程 `Stopwatch` 的读数」，
    /// 现在是「机器开机以来的毫秒」。换了口径之后拿新读数跟旧读数相减毫无意义
    /// （差出整整一个开机时长），会被当成「墙钟往前调了」而要求重新校准 ——
    /// 一台离线机器于是**再也录不了**（撞 I10）。
    /// 老文件里没有这个字段（<see langword="null"/>）⇒ 认作不可比，推齐一次即可。
    /// </remarks>
    public string? MonotonicEpoch { get; init; }

    /// <summary>历史上记录到的跳变（只留最近若干条）。</summary>
    public IReadOnlyList<JumpRecord> Jumps { get; init; } = [];

    /// <summary>上一次核对发现跳变、因此**要求重新校准**。</summary>
    public bool NeedsRecalibration { get; init; }

    public static CalibrationState Empty { get; } = new();
}

/// <summary>一次墙钟跳变。</summary>
/// <param name="DetectedAtUtc">发现它的时刻（当时读到的墙钟）。</param>
/// <param name="WallClockDeltaSeconds">墙钟跳了多少。</param>
/// <param name="MonotonicDeltaSeconds">同期单调钟走了多少。</param>
/// <param name="Backwards">是不是「往回调」（伪造更早的证据，I11 点名的那一种）。</param>
public sealed record JumpRecord(
    DateTimeOffset DetectedAtUtc,
    double WallClockDeltaSeconds,
    double MonotonicDeltaSeconds,
    bool Backwards);

/// <summary>可信时钟 —— 录制的**唯一**时间来源。</summary>
/// <remarks>
/// <para>
/// 规格 §3.6.3 原话：「**水印与时长都不得取自墙钟** —— 用户改系统时间**不得**
/// 改变视频里的时间」。所以录制用的时刻必须由
/// <b>外部时间锚 + 单调时钟</b>推进（<see cref="Now"/>）。
/// </para>
/// <para>
/// ⚠️ <b>它不宣传「不可篡改的时间」</b>（规格 §3.6.3 的产品表述约束）：
/// 它做的是「可验证、可追溯、有外部时间锚」。取到锚之后，本地时钟再怎么改，
/// 也改不动已经起步的那条时间线。
/// </para>
/// </remarks>
public interface ITrustedClock
{
    /// <summary>能不能开始新的录制。</summary>
    bool IsCalibrated { get; }

    /// <summary>可信的当前时刻。未校准时**不要用它**（见 <see cref="IsCalibrated"/>）。</summary>
    DateTimeOffset Now { get; }

    /// <summary>不能录时的原因（给用户看的那句话）；能录时为 <see langword="null"/>。</summary>
    string? BlockedReason { get; }
}

/// <summary>
/// 单调读数与墙钟的差值超过它就算跳变（秒）。
/// </summary>
/// <remarks>
/// ⚠️ <b>这个数是本仓标定的，不是规格里写的</b> —— 规格 §3.6.3 明说「这类阈值要
/// 真机标定，写死只会让它在真机上不成立……由实现标定并记进该仓的
/// <c>docs/实现决策.md</c>」。
/// <para>
/// 取 **120 秒**：NTP 校正通常是毫秒到秒级、时区变更与用户改时间是小时级，
/// 所以两分钟足以把两者分开，又不会因为一次正常的系统对时而误判。
/// 真机上若发现误判（或漏判），改的就是这一个数。
/// </para>
/// </remarks>
public static class ClockTolerance
{
    public static readonly TimeSpan JumpThreshold = TimeSpan.FromSeconds(120);
}

/// <summary>校准状态的读写（`&lt;root&gt;/calibration.json`）。</summary>
public sealed class CalibrationStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public CalibrationStore(string path)
    {
        _path = path;
    }

    /// <summary>读。**任何失败都回落到「没校准过」** —— 但那样会拦住录制，
    /// 所以调用方要把原因说出来（不是静默回落到默认值那种情况）。</summary>
    public async Task<CalibrationState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return CalibrationState.Empty;
        }

        try
        {
            // ⚠️ `ConfigureAwait(false)` **不能省** —— 少了它就是「电脑端只能启动一次」。
            //
            // 缘由：`DesktopServices.Create` 是在 **UI 线程**上
            // `calibration.LoadAsync().GetAwaiter().GetResult()` 阻塞等它的
            // （那个 Create 是同步 API）。不写 ConfigureAwait(false) 的话，
            // 这个 await 的后续会被投回 UI 线程的同步上下文，而 UI 线程正卡在
            // 那个 GetResult 上 —— 谁也走不了，**进程永久挂住、窗口永远不出现**。
            //
            // ⚠️ 为什么第一次跑没事：没校准过时上面 `!File.Exists` 那条**同步返回**，
            // 根本没有 await，所以不死锁；而它紧接着就把文件写出来了 ——
            // 于是**从第二次启动起必挂**。这正是「交付后第一次能开、之后再也开不了」。
            //
            // ⚠️ 单元测试抓不到：测试线程上 `SynchronizationContext.Current` 是 null，
            // 而 App 层没有测试工程。见 `CalibrationStoreTests` 里那条专门造上下文拦的。
            var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CalibrationState>(json, Options) ?? CalibrationState.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // ⚠️ 读坏了**当作没校准过**（保守），而不是当作已校准。
            // 这个文件被手改坏的后果是「录不了像」，而反过来是
            // 「录出来的东西时间不可信」—— 后者更重。
            return CalibrationState.Empty;
        }
    }

    public async Task SaveAsync(CalibrationState state, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(state, Options);
        var temporary = _path + ".tmp";

        // 与 LoadAsync 同一条理由：这个存储不该带着调用方的同步上下文。
        await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);

        try
        {
            File.Move(temporary, _path, overwrite: true);
        }
        catch (IOException)
        {
            // Windows 上改名可能被 Defender 之类的句柄短暂拒绝。
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
    }
}

/// <summary>
/// 可信时钟的实现。
/// </summary>
/// <remarks>
/// <para>
/// 时间线 = <c>锚 + (当前单调读数 − 拿到锚时的单调读数)</c>。
/// 单调钟跨重启会**归零**，所以跨重启之后这条线要重新校准 —— 那正是
/// <see cref="CheckStartupAsync"/> 在判的事。
/// </para>
/// <para>
/// ⚠️ <b>诚实说清能力边界</b>（这一条要写进决策文档）：
/// </para>
/// <list type="bullet">
/// <item><b>同一次开机内</b>（单调钟连续）：墙钟**双向**跳变都测得出，两者都记录。</item>
/// <item><b>跨重启</b>（单调钟归零）：只能判出<b>「时间被调回去了」</b>
/// （现在的墙钟早于上次记录的墙钟）—— 那正是 I11 点名的伪造方向。
/// 往前调、或正常关机过了一夜，从这两个数**分辨不出来**。</item>
/// </list>
/// <para>
/// ⚠️ 分辨不出来的那一种，处理方式是<b>把基准推齐到当前墙钟</b>
/// （<see cref="RebaseAsync"/>），而**不是**从前的「放行、旧锚接着用」。
/// 两者的差别只在重启之后那一段：旧做法会把关机期间流逝的时间**当成没流过**，
/// 于是重启后的水印从上次的锚点接着走 —— 2026-10-02 实测整体错位约 37 分钟
/// （`calibration.json` 里 `MonotonicAtAnchor: 0.2015` 对
/// `LastSeenMonotonic: 0.0087`，两个进程各自的秒表读数）。
/// </para>
/// <para>
/// ⚠️ 推齐**用到了墙钟，但只用在「补关机那一段」**：会话内的时间线仍然只由单调钟
/// 推进，用户录到一半改系统时间照样改不动已经起步的那条线（I11 的原意）。
/// 代价如实记在这里：关着机的时候把系统时间改掉，重启后那条线会跟着走 ——
/// 而离线时要接上一段没有录制的空档，本来也没有别的依据可用。
/// </para>
/// </remarks>
public sealed class TrustedClock : ITrustedClock
{
    private readonly Func<TimeSpan> _monotonic;
    private readonly CalibrationStore _store;
    private readonly IAppLogger _logger;
    private CalibrationState _state;

    public TrustedClock(
        CalibrationState state,
        CalibrationStore store,
        Func<TimeSpan>? monotonic = null,
        IAppLogger? logger = null)
    {
        _state = state;
        _store = store;
        _monotonic = monotonic ?? DefaultMonotonic();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 单调读数：**机器开机以来**的时长。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>不能用「本进程的 <c>Stopwatch</c>」</b>（这里从前就是那么写的，
    /// 那正是缺陷的来源）：它每次都从 0 开始数，于是**程序重启**与**整机重启**
    /// 在 `CheckStartupAsync` 眼里长得一模一样 —— 而两者的处理完全不同。
    /// 2026-10-02 实测：12:10 那个进程留下的 `MonotonicAtAnchor = 0.2`，
    /// 12:48 新进程自己的读数是 `0.0087`，相减为负 ⇒ 判成「跨了重启」⇒
    /// 旧锚继续用 ⇒ 水印整体错位。
    /// <para>
    /// <c>TickCount64</c> 是开机以来毫秒数：**跨进程连续**（程序重启不影响它），
    /// 只在整机重启时归零 —— 那两个信号这才分得开。它不受改系统时间影响，
    /// 仍然满足「单调」的要求。
    /// </para>
    /// </remarks>
    private static Func<TimeSpan> DefaultMonotonic() =>
        () => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// 当前这把尺子的口径（见 <see cref="CalibrationState.MonotonicEpoch"/>）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 公开是**故意的**：它是落盘格式的一部分，测试要照着它造「上一版程序留下的
    /// state」 —— 拿一个字面量去对，改了那边忘了这边，测试就会绿在一个假结论上。
    /// </remarks>
    public const string MonotonicEpoch = "boot-ms";

    /// <summary>落盘的那份状态（诊断与界面用）。</summary>
    public CalibrationState State => _state;

    public bool IsCalibrated =>
        !_state.NeedsRecalibration
        && _state.AnchorUtc is not null
        && _state.MonotonicAtAnchor is not null;

    public DateTimeOffset Now => _state.AnchorUtc is { } anchor && _state.MonotonicAtAnchor is { } origin
        ? anchor + (_monotonic() - TimeSpan.FromSeconds(origin))
        // 没校准过时的兜底：墙钟。**它不该被用到** —— 调用方要先看 IsCalibrated。
        : DateTimeOffset.UtcNow;

    public string? BlockedReason => IsCalibrated ? null : BlockText();

    private string BlockText() =>
        _state.NeedsRecalibration
            ? "系统时间被改过（或者关机时改过），需要联一次网重新校准才能继续录制。"
            : "这台电脑还没有过一次可信的时间校准，按规格 §3.6.4 不能开始录制。";

    /// <summary>
    /// 启动时核对一次：挂钟与上次退出时留下的记录自不自洽（规格 §3.6.3）。
    /// </summary>
    /// <returns>发现跳变返回 true（那时 <see cref="IsCalibrated"/> 会变成 false）。</returns>
    public async Task<bool> CheckStartupAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var monotonic = _monotonic();

        var verdict = Judge(_state, now, monotonic);

        if (verdict is null)
        {
            // ⚠️ `null` 有**两种**含义，别混：真的自洽，以及「这两个数不可比」
            // （跨了重启、或这份状态是旧口径写的）。后者必须**把基准推齐** ——
            // 否则旧锚接着用，重启之后的水印就从上次的锚点续着走（缺陷 4 的症状）。
            if (!MonotonicComparable(_state, monotonic))
            {
                return await RebaseAsync(now, monotonic, cancellationToken);
            }

            // 自洽 —— 把这次的读数记下来，供下次核对。
            _state = _state with
            {
                LastSeenWallClockUtc = now,
                LastSeenMonotonic = monotonic.TotalSeconds,
                MonotonicEpoch = MonotonicEpoch,
            };

            await _store.SaveAsync(_state, cancellationToken);
            return false;
        }

        var (delta, monotonicDelta, backwards) = verdict.Value;

        return await RecordJumpAsync(now, monotonic, delta, monotonicDelta, backwards, cancellationToken);
    }

    /// <summary>
    /// 这次核对的两个数不可比（跨了重启，或者这份状态是旧口径写的）：
    /// **把时间基准推齐到当前墙钟**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 推齐的做法：把「拿到锚时的单调读数」改成「当前读数 − 锚点到现在该有的时长」，
    /// 于是 <see cref="Now"/> 在启动这一刻正好落在墙钟上，之后照旧只由单调钟推进。
    /// <b>锚点本身不动</b> —— 它是用户改不了的那个外部时间，换掉就等于重新校准了。
    /// </remarks>
    private async Task<bool> RebaseAsync(
        DateTimeOffset now, TimeSpan monotonic, CancellationToken cancellationToken)
    {
        // 墙钟比上次记录的还早 ⇒ 那是**时间被调回去了**，不是重启。
        // 与 `Judge` 里那条同一个判据，照记跳变（这一半与跨没跨重启无关）。
        // ⚠️ 这一句必须排在推齐之前：推齐会把这个方向抹平掉。
        if (_state.LastSeenWallClockUtc is { } lastWall && now < lastWall)
        {
            var monotonicDelta = _state.LastSeenMonotonic is { } lastMonotonic
                ? monotonic - TimeSpan.FromSeconds(lastMonotonic)
                : TimeSpan.Zero;

            return await RecordJumpAsync(
                now, monotonic, now - lastWall, monotonicDelta, backwards: true, cancellationToken);
        }

        if (_state.AnchorUtc is { } anchor)
        {
            var offline = _state.LastSeenWallClockUtc is { } seen ? now - seen : TimeSpan.Zero;

            _state = _state with
            {
                MonotonicAtAnchor = (monotonic - (now - anchor)).TotalSeconds,
            };

            // ⚠️ 这一条日志从前**没有**，而它是排查「水印时间不对」时唯一能看的现场
            // （2026-10-02 实测：重启后整体错位 37 分钟，日志里一个字都没有）。
            _logger.Log(LogLevel.Info, "校时",
                $"这是另一次开机（或换了一把时钟口径），已把时间基准推齐到当前墙钟"
                + $"（上次运行到现在 {offline.TotalMinutes:0.0} 分钟）；"
                + "关机那一段按墙钟补，会话内仍只由单调钟推进",
                new Dictionary<string, object?>
                {
                    ["离线分钟"] = offline.TotalMinutes,
                    ["单调读数秒"] = monotonic.TotalSeconds,
                });
        }

        _state = _state with
        {
            MonotonicEpoch = MonotonicEpoch,
            LastSeenWallClockUtc = now,
            LastSeenMonotonic = monotonic.TotalSeconds,
        };

        await _store.SaveAsync(_state, cancellationToken);
        return false;
    }

    /// <summary>记一次墙钟跳变，并要求重新校准。</summary>
    /// <remarks>
    /// ⚠️ 抽出来是因为它现在有**两个**入口（正常判定、以及推齐路上撞见的往回调），
    /// 而「要求重新校准」这个后果是 I11 那一半的落点，两处各写一份迟早走岔。
    /// </remarks>
    private async Task<bool> RecordJumpAsync(
        DateTimeOffset now, TimeSpan monotonic, TimeSpan delta, TimeSpan monotonicDelta,
        bool backwards, CancellationToken cancellationToken)
    {
        _state = _state with
        {
            NeedsRecalibration = true,
            LastSeenWallClockUtc = now,
            LastSeenMonotonic = monotonic.TotalSeconds,
            MonotonicEpoch = MonotonicEpoch,
            // 只留最近 20 条：跳变是**罕见事件**，而这个文件每次启动都要读一遍、
            // 写一遍；留一屋子历史没有用处，还会把它撑大。
            Jumps = new List<JumpRecord>(_state.Jumps)
            {
                new(now, delta.TotalSeconds, monotonicDelta.TotalSeconds, backwards),
            }.TakeLast(20).ToList(),
        };

        await _store.SaveAsync(_state, cancellationToken);

        _logger.Log(LogLevel.Warn, "校时",
            $"检测到墙钟跳变（{(backwards ? "往回调" : "往前调")} {delta.TotalMinutes:0.0} 分钟），要求重新校准",
            new Dictionary<string, object?>
            {
                ["墙钟Δ秒"] = delta.TotalSeconds,
                ["单调Δ秒"] = monotonicDelta.TotalSeconds,
            });

        return true;
    }

    /// <summary>
    /// 这次核对的两个数**能不能相减**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不可比的三种情形，处理方式都是「推齐一次」，而不是「当成跳变」：
    /// 没有锚（第一次启动，没什么可推的）、口径对不上（老版本的文件）、
    /// 读数比上次小（跨了重启，开机以来的毫秒数从 0 重新数）。
    /// </remarks>
    private static bool MonotonicComparable(CalibrationState state, TimeSpan monotonic)
    {
        if (state.MonotonicAtAnchor is not null && state.MonotonicEpoch != MonotonicEpoch)
        {
            return false;
        }

        if (state.LastSeenMonotonic is not { } last)
        {
            return true;
        }

        return monotonic >= TimeSpan.FromSeconds(last);
    }

    /// <summary>判定一次「自洽吗」；返回 null 表示自洽<b>或不比不可</b>（见调用点）。</summary>
    /// <remarks>
    /// ⚠️ 跨重启（单调读数比上次记录的小）时**只判「往回调」那一半** ——
    /// 理由见类注释里那条能力边界。不这么分的话，一台每晚关机的机器
    /// 天天早上都要重新校准，而它的时间其实一直都是准的。
    /// <para>
    /// ⚠️ 返回 <see langword="null"/> 的两种情形由调用点用
    /// <see cref="MonotonicComparable"/> 再分一次：真自洽就记读数，
    /// 不可比就把基准推齐。
    /// </para>
    /// </remarks>
    private static (TimeSpan Delta, TimeSpan MonotonicDelta, bool Backwards)? Judge(
        CalibrationState state, DateTimeOffset now, TimeSpan monotonic)
    {
        // 口径对不上（老版本写的 `calibration.json`）⇒ 这两个数不可比，
        // 相减得出的「跳了多久」是假的。
        if (state.MonotonicEpoch != MonotonicEpoch)
        {
            return null;
        }

        if (state.LastSeenWallClockUtc is not { } lastWall || state.LastSeenMonotonic is not { } lastMonotonic)
        {
            return null;   // 没有可比的东西（第一次启动）
        }

        var wallDelta = now - lastWall;
        var monotonicDelta = monotonic - TimeSpan.FromSeconds(lastMonotonic);

        if (wallDelta < TimeSpan.Zero)
        {
            // 墙钟比上次记录的还早 —— 时间被调回去了。**无论跨没跨重启都算跳变。**
            return (wallDelta, monotonicDelta, true);
        }

        // 单调钟倒退 ⇒ 这一次跨了重启 ⇒ 往前调的那一半分辨不出来。
        // 返回 null，由调用点走「推齐基准」那一条（不是「放行、旧锚接着用」）。
        if (monotonicDelta < TimeSpan.Zero)
        {
            return null;
        }

        // 同一次开机内：墙钟与单调钟的差超过容差就是跳变。
        return (wallDelta - monotonicDelta).Duration() > ClockTolerance.JumpThreshold
            ? (wallDelta - monotonicDelta, monotonicDelta, false)
            : null;
    }

    /// <summary>用一个新的外部时间锚校准（或重新校准）。</summary>
    public async Task CalibrateAsync(
        DateTimeOffset anchorUtc,
        CalibrationSource source,
        CancellationToken cancellationToken = default)
    {
        var monotonic = _monotonic();

        _state = _state with
        {
            AnchorUtc = anchorUtc,
            MonotonicAtAnchor = monotonic.TotalSeconds,
            Source = source,
            CalibratedAtUtc = DateTimeOffset.UtcNow,
            LastSeenWallClockUtc = DateTimeOffset.UtcNow,
            LastSeenMonotonic = monotonic.TotalSeconds,
            MonotonicEpoch = MonotonicEpoch,
            // ⚠️ 重新校准**清掉**「要重新校准」那个标记 —— 但它不清历史跳变记录
            // （那些是事实，留着对诊断有用）。
            NeedsRecalibration = false,
        };

        await _store.SaveAsync(_state, cancellationToken);

        _logger.Log(LogLevel.Info, "校时", $"已校准（来源：{source}）",
            new Dictionary<string, object?> { ["锚"] = anchorUtc.ToString("O") });
    }
}
