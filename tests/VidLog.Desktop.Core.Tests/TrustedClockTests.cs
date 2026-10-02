using VidLog.Desktop.Core.Clock;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 可信时钟与跳变检测（规格 §3.6.3 / §3.6.4）。
/// </summary>
/// <remarks>
/// ⚠️ **墙钟在本类里是被测对象，所以要能随意摆布它** —— 但 `TrustedClock`
/// 内部读的是 `DateTimeOffset.UtcNow`。为了不把「当前时刻」做成可注入的
/// （那会让生产路径多一个能传错的口子），这里的做法是：**用真的 UtcNow 造状态**，
/// 再把「上次见到的墙钟」写成相对它的偏移。等价于「用户把时间改了」。
/// </remarks>
public class TrustedClockTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-clock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File => System.IO.Path.Combine(Path, "calibration.json");

        public void Dispose()
        {
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（2026-09-27 实测：会话还在
            // 收尾时删含 `.ass` / `.mkv` 的临时目录）。清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static CalibrationStore Store(TempDir dir) => new(dir.File);

    // ─────────────────────────────────────────────
    // 校准与时间线
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 没校准过就不能录_而且说得出为什么()
    {
        using var dir = new TempDir();
        var clock = new TrustedClock(await Store(dir).LoadAsync(), Store(dir));

        Assert.False(clock.IsCalibrated);
        Assert.NotNull(clock.BlockedReason);
        Assert.Contains("校准", clock.BlockedReason);
    }

    [Fact]
    public async Task 校准之后就能录_而且状态落盘_重启还在()
    {
        // 规格 §3.6.4：「已校准状态**落盘持久化**，之后离线照常录制」。
        // 不落盘的话，一台交付后从没联过网的机器重启一次就再也录不了。
        using var dir = new TempDir();
        var anchor = DateTimeOffset.UtcNow;

        var first = new TrustedClock(await Store(dir).LoadAsync(), Store(dir));
        await first.CalibrateAsync(anchor, CalibrationSource.PublicTime);

        Assert.True(first.IsCalibrated);

        var reopened = new TrustedClock(await Store(dir).LoadAsync(), Store(dir));
        Assert.True(reopened.IsCalibrated);
        Assert.Equal(CalibrationSource.PublicTime, reopened.State.Source);
    }

    [Fact]
    public async Task 时间线由锚加单调读数推进_与墙钟无关()
    {
        using var dir = new TempDir();

        var monotonic = TimeSpan.Zero;
        var clock = new TrustedClock(
            await Store(dir).LoadAsync(), Store(dir), () => monotonic);

        var anchor = new DateTimeOffset(2026, 9, 27, 4, 0, 0, TimeSpan.Zero);
        await clock.CalibrateAsync(anchor, CalibrationSource.PublicTime);

        Assert.Equal(anchor, clock.Now);

        // 单调钟走了 90 秒 ⇒ 可信时间也走 90 秒，**无论墙钟被改成什么**。
        monotonic = TimeSpan.FromSeconds(90);
        Assert.Equal(anchor.AddSeconds(90), clock.Now);
    }

    // ⚠️ 这里原先有一条「认不出的时间字符串一律不当成时刻」，测的是
    // `HttpDateParser.TryParse`（严格 RFC 1123、不放宽）。2026-09-27 **两者一起删了**：
    // 那个解析器在生产里**零调用点** —— `HttpDateClockSource` 用的是框架解析好的
    // `HttpResponseHeaders.Date`（内部就是按 RFC 1123 + invariant 文化解析的，
    // 同样不会把本机区域格式卷进来）。
    //
    // ⚠️ 所以那条测试验的是一个**没人跑的实现** —— 它绿着，却覆盖不到
    // 生产真正走的那条路。这是「测试绿在一个巧合上」的另一种脸：
    // **测了不跑的东西**。删掉它之后覆盖情况反而是诚实的。
    // 那两句推理（只用 RFC 1123、放宽会卷进本机格式）已经搬到
    // `HttpDateClockSource.QueryAsync` 的注释里，不会再丢。

    // ─────────────────────────────────────────────
    // 跳变检测（规格 §3.6.3）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 同一次开机内_墙钟往后跳会被记下来并要求重新校准()
    {
        using var dir = new TempDir();

        var monotonic = TimeSpan.FromHours(1);
        var clock = new TrustedClock(
            await Store(dir).LoadAsync(), Store(dir), () => monotonic);
        await clock.CalibrateAsync(DateTimeOffset.UtcNow, CalibrationSource.PublicTime);

        // 上次核对的记录：墙钟是「现在」，单调是 1 小时。
        // 现在把单调只往前推 1 秒，但墙钟已经是「现在 + 30 分钟」——
        // 等价于用户把系统时间往后调了 30 分钟。
        var state = clock.State with
        {
            LastSeenWallClockUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            LastSeenMonotonic = monotonic.TotalSeconds,
        };

        var reloaded = new TrustedClock(state, Store(dir), () => monotonic.Add(TimeSpan.FromSeconds(1)));

        Assert.True(await reloaded.CheckStartupAsync());
        Assert.False(reloaded.IsCalibrated);

        var jump = Assert.Single(reloaded.State.Jumps);
        Assert.False(jump.Backwards, "往后调是「变晚」那一类");
    }

    [Fact]
    public async Task 时间被调回去了_无论跨没跨重启都算跳变()
    {
        // ⚠️ 这一条是 I11 点名的那一种：**把时间调回去是在伪造「更早的证据」**。
        // 而且它是**跨重启也判得出来**的那一半（单调钟归零照样判得出）。
        using var dir = new TempDir();

        var state = new CalibrationState
        {
            AnchorUtc = DateTimeOffset.UtcNow,
            MonotonicAtAnchor = 0,
            // 「上次见到」比现在**晚** 30 分钟 ⇒ 现在是**被调回去了** 30 分钟。
            LastSeenWallClockUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            LastSeenMonotonic = 3600,
            MonotonicEpoch = TrustedClock.MonotonicEpoch,
        };

        // 单调读数也比上次记录的**小**（= 中间关过机重启，单调钟归零了）。
        var clock = new TrustedClock(state, Store(dir), () => TimeSpan.FromSeconds(5));

        Assert.True(await clock.CheckStartupAsync());
        Assert.False(clock.IsCalibrated);
        Assert.True(Assert.Single(clock.State.Jumps).Backwards);
    }

    [Fact]
    public async Task 跨重启而且时间没被改过_放行_不误伤离线可用()
    {
        // ⚠️ 这一条与上一条是**成对的**：跨重启时单调钟归零，往前调的那一半
        // 分辨不出来。那时**必须放行** —— 否则一台每晚关机的机器天天早上都要
        // 重新校准，而规格 §3.6.4 承诺的是「已校准之后离线照常录制」。
        using var dir = new TempDir();

        var state = new CalibrationState
        {
            AnchorUtc = DateTimeOffset.UtcNow.AddHours(-8),
            MonotonicAtAnchor = 0,
            LastSeenWallClockUtc = DateTimeOffset.UtcNow.AddHours(-8),
            LastSeenMonotonic = 7200,   // 上次跑到 2 小时
            MonotonicEpoch = TrustedClock.MonotonicEpoch,
        };

        var clock = new TrustedClock(state, Store(dir), () => TimeSpan.FromSeconds(30));

        Assert.False(await clock.CheckStartupAsync());
        Assert.True(clock.IsCalibrated);

        // ⚠️ **判定的重点在这一句**：重启之后 `Now` 必须落在墙钟上。
        // 从这里返回「不是跳变」是不够的 —— 从前就是那样，而它同时把旧锚留着，
        // 于是水印从**八小时前**那个锚点续着走（2026-10-02 实测错位 37 分钟）。
        Assert.True(
            (clock.Now - DateTimeOffset.UtcNow).Duration() < TimeSpan.FromSeconds(5),
            $"重启后 Now 该落在当前墙钟上，实际是 {clock.Now:O}");
    }

    [Fact]
    public async Task 同一次开机内_墙钟没动就不算跳变()
    {
        using var dir = new TempDir();

        var monotonic = TimeSpan.FromMinutes(10);
        var state = new CalibrationState
        {
            AnchorUtc = DateTimeOffset.UtcNow,
            MonotonicAtAnchor = 0,
            LastSeenWallClockUtc = DateTimeOffset.UtcNow.AddSeconds(-30),
            LastSeenMonotonic = monotonic.TotalSeconds - 30,
            MonotonicEpoch = TrustedClock.MonotonicEpoch,
        };

        var clock = new TrustedClock(state, Store(dir), () => monotonic);

        Assert.False(await clock.CheckStartupAsync());
        Assert.True(clock.IsCalibrated);
    }

    [Fact]
    public async Task 老版本写的校准文件_不误判成跳变_也不要求重新校准()
    {
        // ⚠️ 这一条是**升级路径**：`MonotonicEpoch` 是这一版才加的，老文件里没有它，
        // 于是 `LastSeenMonotonic` 是「某个进程的秒表读数」，跟新的「开机以来毫秒」
        // 相减会差出整整一个开机时长 —— 当成跳变的话，一台**离线**机器
        // 会被要求「联一次网才能继续录制」，而它其实什么都没被改过（撞 I10）。
        using var dir = new TempDir();

        var state = new CalibrationState
        {
            AnchorUtc = DateTimeOffset.UtcNow.AddHours(-2),
            MonotonicAtAnchor = 0.2,        // 老口径：某进程跑了 0.2 秒
            LastSeenWallClockUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
            LastSeenMonotonic = 0.0087,     // 老口径
            MonotonicEpoch = null,          // 老文件没有这一栏
        };

        // 新口径：开机以来 3 天（跟老读数的量级完全不同）。
        var clock = new TrustedClock(state, Store(dir), () => TimeSpan.FromDays(3));

        Assert.False(await clock.CheckStartupAsync());
        Assert.True(clock.IsCalibrated);
        Assert.Empty(clock.State.Jumps);
        Assert.True((clock.Now - DateTimeOffset.UtcNow).Duration() < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task 默认的单调尺子是开机以来的_不是本进程的秒表()
    {
        // ⚠️ 这一条盯着**尺子本身**。从前用的是「本进程的 `Stopwatch`」，
        // 每次启动都从 0 开始数，于是「程序重启」与「整机重启」在核对眼里
        // 一模一样 —— 而两者的处理完全不同（一个该照常比，一个该推齐）。
        using var dir = new TempDir();
        var clock = new TrustedClock(await Store(dir).LoadAsync(), Store(dir));

        await clock.CalibrateAsync(DateTimeOffset.UtcNow, CalibrationSource.PublicTime);

        // 进程秒表在这一刻是几百**毫秒**；开机以来的读数是百万级。
        var reading = clock.State.MonotonicAtAnchor!.Value;
        Assert.True(
            reading > 60,
            $"单调读数只有 {reading:0.###} 秒 —— 那是本进程的秒表，不是机器开机以来的时长");
    }

    [Fact]
    public async Task 程序关着的那段时间里墙钟被往前调_照样测得出来()
    {
        // ⚠️ 这一条是「尺子必须跨进程连续」的**收益**所在：用本进程秒表时，
        // 程序一重启读数就从 0 重来，这段时间窗口里的「往前调」会被判成
        // 「跨了重启、分辨不出来」而放行 —— 于是 I11 点名的那一半在这一段里失效。
        using var dir = new TempDir();

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Assert.True(
            uptime > TimeSpan.FromMinutes(10),
            $"这台机器才开机 {uptime.TotalMinutes:0.0} 分钟，这条测试说明不了问题");

        // 上次运行是 5 分钟前结束的；锚点是更早以前取的。
        var lastExit = uptime - TimeSpan.FromMinutes(5);

        var state = new CalibrationState
        {
            AnchorUtc = DateTimeOffset.UtcNow.AddHours(-6),
            MonotonicAtAnchor = (lastExit - TimeSpan.FromHours(1)).TotalSeconds,
            // 真实只过了 5 分钟，而墙钟显示过了 65 分钟 ⇒ 往前调了 1 小时。
            LastSeenWallClockUtc = DateTimeOffset.UtcNow.AddMinutes(-65),
            LastSeenMonotonic = lastExit.TotalSeconds,
            MonotonicEpoch = TrustedClock.MonotonicEpoch,
        };

        var clock = new TrustedClock(state, Store(dir));

        Assert.True(await clock.CheckStartupAsync(), "往前调的跳变漏掉了");
        Assert.False(clock.IsCalibrated);
        Assert.False(Assert.Single(clock.State.Jumps).Backwards);
    }

    [Fact]
    public async Task 重新校准会清掉_要求重新校准_那个标记()
    {
        using var dir = new TempDir();

        var state = CalibrationState.Empty with { NeedsRecalibration = true };
        var clock = new TrustedClock(state, Store(dir));

        Assert.False(clock.IsCalibrated);

        await clock.CalibrateAsync(DateTimeOffset.UtcNow, CalibrationSource.PublicTime);

        Assert.True(clock.IsCalibrated);
    }

    [Fact]
    public async Task 校准文件被手改坏_当作没校准过_而不是当作已校准()
    {
        // 两个方向的代价不对称：当作「没校准」只是录不了像（看得见、说得清），
        // 当作「已校准」会让录出来的东西时间不可信 —— 后者更重。
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File, "{ 这不是 JSON");

        var clock = new TrustedClock(await Store(dir).LoadAsync(), Store(dir));

        Assert.False(clock.IsCalibrated);
    }

    [Fact]
    public async Task 取不到公网时间时_不猜一个时刻()
    {
        // 猜出来的锚比没有锚更糟：它看起来是校准过的。
        var source = new HttpDateClockSource(
            urls: ["http://127.0.0.1:1/这里一定连不上"],
            timeout: TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.QueryAsync());
    }
}
