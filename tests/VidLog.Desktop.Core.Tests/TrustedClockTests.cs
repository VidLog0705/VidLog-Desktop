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
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
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

    [Fact]
    public void 认不出的时间字符串一律不当成时刻()
    {
        // 只用 RFC 1123 那一种。放宽成 DateTimeOffset.Parse 会把本机区域格式
        // 卷进来，而这条路径要的是一个**外部**时刻。
        Assert.True(HttpDateParser.TryParse("Tue, 15 Nov 1994 08:12:31 GMT", out var parsed));
        Assert.Equal(new DateTimeOffset(1994, 11, 15, 8, 12, 31, TimeSpan.Zero), parsed);

        Assert.False(HttpDateParser.TryParse(null, out _));
        Assert.False(HttpDateParser.TryParse("", out _));
        Assert.False(HttpDateParser.TryParse("2026-09-27 12:00:00", out _));
        Assert.False(HttpDateParser.TryParse("昨天下午三点", out _));
    }

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
        };

        var clock = new TrustedClock(state, Store(dir), () => TimeSpan.FromSeconds(30));

        Assert.False(await clock.CheckStartupAsync());
        Assert.True(clock.IsCalibrated);
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
        };

        var clock = new TrustedClock(state, Store(dir), () => monotonic);

        Assert.False(await clock.CheckStartupAsync());
        Assert.True(clock.IsCalibrated);
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
