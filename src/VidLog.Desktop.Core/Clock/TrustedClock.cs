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
/// 往前调、或正常关机过了一夜，从这两个数**分辨不出来**，
/// 所以那种情况**放行**（不误伤「离线照常录制」那条承诺），如实记在这里。</item>
/// </list>
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

    /// <summary>默认单调读数：进程内一个从构造时开始走的秒表。</summary>
    /// <remarks>
    /// 与 <c>RecordingSession</c> 用的是同一类东西 —— 整个应用只该有一把尺子。
    /// </remarks>
    private static Func<TimeSpan> DefaultMonotonic()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        return () => watch.Elapsed;
    }

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
            // 自洽 —— 把这次的读数记下来，供下次核对。
            _state = _state with
            {
                LastSeenWallClockUtc = now,
                LastSeenMonotonic = monotonic.TotalSeconds,
            };

            await _store.SaveAsync(_state, cancellationToken);
            return false;
        }

        var (delta, monotonicDelta, backwards) = verdict.Value;

        _state = _state with
        {
            NeedsRecalibration = true,
            LastSeenWallClockUtc = now,
            LastSeenMonotonic = monotonic.TotalSeconds,
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

    /// <summary>判定一次「自洽吗」；返回 null 表示自洽。</summary>
    /// <remarks>
    /// ⚠️ 跨重启（单调读数比上次记录的小）时**只判「往回调」那一半** ——
    /// 理由见类注释里那条能力边界。不这么分的话，一台每晚关机的机器
    /// 天天早上都要重新校准，而它的时间其实一直都是准的。
    /// </remarks>
    private static (TimeSpan Delta, TimeSpan MonotonicDelta, bool Backwards)? Judge(
        CalibrationState state, DateTimeOffset now, TimeSpan monotonic)
    {
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

        // 单调钟倒退 ⇒ 这一次跨了重启 ⇒ 往前调的那一半分辨不出来，放行。
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
            // ⚠️ 重新校准**清掉**「要重新校准」那个标记 —— 但它不清历史跳变记录
            // （那些是事实，留着对诊断有用）。
            NeedsRecalibration = false,
        };

        await _store.SaveAsync(_state, cancellationToken);

        _logger.Log(LogLevel.Info, "校时", $"已校准（来源：{source}）",
            new Dictionary<string, object?> { ["锚"] = anchorUtc.ToString("O") });
    }
}
