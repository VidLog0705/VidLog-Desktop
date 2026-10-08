using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VidLog.Desktop.Core.Live;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 多画面那一格：从**手机推上来的 HTTP 裸流**里取画面、问计数、改档。
/// </summary>
/// <remarks>
/// <para>
/// 这一组**真的把整条路径跑通**：本机 ffmpeg 先造一段 H.264，再起一台**假手机**
/// 按手机那侧的契约（`video/h264`、循环喂、每个循环开头带 SPS/PPS + IDR，
/// 外加 `/status` 与 `/quality`）把它喂出去。
/// </para>
/// <para>
/// ⚠️ <b>喂法有两种，别只留一种。</b>「一整段循环喂」是**突发**形状，跑得快、
/// 够绝大多数用例用；但它<strong>演不出「推流卡死」</strong>那一类缺陷（实测）。
/// 要看那个，得用现场按真时间编码那一档（<see cref="FakePhone"/> 的第二个构造函数）。
/// </para>
/// <para>
/// ⚠️ <b>它挡的是「两端契约对不上」这一类错</b>：电脑端少给 <c>-f h264</c>、
/// 或者手机那侧不是 Annex-B、或者 SPS/PPS 没跟着关键帧走、或者改档的参数名两边
/// 写得不一样 —— 这些在两边各自的单元测试里**都看不出来**，
/// 表现清一色是「那一格一直黑着」或者「选了 1080P 还是糊」。
/// </para>
/// <para>
/// ⚠️ <b>手写 `TcpListener` 而不是 `HttpListener`</b>：后者在 Windows 上要 urlacl
/// （要么管理员、要么事先注册前缀），不该为一条测试去动那个。
/// </para>
/// </remarks>
public class LiveTileProcessTests
{
    [RequiresFfmpegFact]
    public async Task 拉一路HTTP裸流_出得来帧_而且尺寸是我们要的那个()
    {
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));

        Assert.True(h264.Length > 0, "ffmpeg 没造出 H.264 来，后面的断言都不作数");

        using var phone = new FakePhone(h264);

        await using var tile = LiveTileProcess.Start(ffmpeg!, phone.LiveUrl, 320, 180);

        Assert.NotNull(tile);

        var frame = await WaitForFrameAsync(tile!, TimeSpan.FromSeconds(30));

        Assert.True(frame is not null, $"三十秒没等到一帧。ffmpeg 说：{tile!.ErrorTail}");

        Assert.Equal(320, frame!.Width);
        Assert.Equal(180, frame.Height);
        Assert.Equal(320 * 180 * 3, frame.Rgb.Length);
    }

    /// <summary>
    /// **一直在推的流，画面就要一直在来** —— 不是「开头出了一帧，之后再也不动」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>判据是「后来又来了多少帧」，不是「有没有帧」。</b>别的用例等到一帧就
    /// 返回，所以「第一帧之后画面就冻住」这一类毛病它们一概看不见。
    /// </para>
    /// <para>
    /// ⚠️ <b>但这条**抓不到** 2026-10-08 真机上那个 <c>-fflags nobuffer</c> 缺陷，
    /// 别把它当成那道闸。</b>当时按「真机那样按真时间喂」的思路做了这条用例，
    /// 实测：把那一版参数原样放回来，这条**照样绿**（六秒里 71 帧、掉帧 0，
    /// 与修好的版本分不出）。真机上也试过用预编好的字节去凑那个形状，
    /// 同样只是「差一截」而不是「卡死」。那道闸落在
    /// <see cref="参数里不许再有nobuffer_限速也不许走输出侧"/>（照参数本身判，
    /// 不靠演）。</para>
    /// </remarks>
    [RequiresFfmpegFact]
    public async Task 一直在推的流_画面要一直在来()
    {
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        // 480x854：手机竖屏那一档，宽高比与格子也不同 —— 缩放那条路一并走到。
        using var phone = new FakePhone(ffmpeg!, 480, 854, 30);

        await using var tile = LiveTileProcess.Start(ffmpeg!, phone.LiveUrl, 320, 180);

        Assert.NotNull(tile);

        var first = await WaitForFrameAsync(tile!, TimeSpan.FromSeconds(30));

        Assert.True(first is not null, $"三十秒没等到一帧。ffmpeg 说：{tile!.ErrorTail}");

        var before = tile!.ReceivedCount;
        await Task.Delay(TimeSpan.FromSeconds(6));
        var got = tile.ReceivedCount - before;

        // 这一格限速 12 fps ⇒ 理想值 72 帧。门槛 24（= 4 fps）给慢机器留了余量。
        Assert.True(
            got >= 24,
            $"六秒里只接着收到 {got} 帧 —— 这一格卡住了（第一帧之后画面不动了）。"
            + $"ffmpeg 说：{tile.ErrorTail}");
    }

    /// <summary>
    /// <b>「取走」和「看一眼」是两件事</b>：取走才让那本账对得上，而取走**不等于画面没了**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>这条是 2026-10-08 真机上那本「说假话的账」的闸。</b>画的那一处原来走
    /// 只看不拿的 <see cref="LiveTileProcess.Latest"/>，于是最新那一帧永远不被清掉 ⇒
    /// <c>_dropped</c> 从第一帧起**每一帧都加一**：真机上量到「我们收到 1069 帧、
    /// 界面丢了 1068」。而 T11 正是拿这两个数分「手机上没编出来」与「这台电脑画不过来」
    /// —— 恒等于收到数减一就等于没有。
    /// </para>
    /// <para>
    /// ⚠️ <b>判据是「一直取着，就不该有丢」。</b>取只是个锁、来帧是 30 fps，
    /// 取的速度比来帧快几个数量级，所以正常应该一帧都不丢。反过来跑那条
    /// 只看不拿的路（把 <c>Take</c> 里的 <c>_latest = null</c> 摘掉），这个数会
    /// 贴着收到数走 —— 反证过。
    /// </para>
    /// <para>
    /// ⚠️ <b>下面那半量的是另一件事：取走之后不许返回空。</b>界面那条画帧的路
    /// 见空就抹画面写「无信号输入」（<c>MultiViewWindow</c> 的 <c>Cell.Pump</c>），
    /// 所以「这一手还没轮到新帧」要是也返回空，格子在两帧之间会闪一下黑。
    /// 取帧的钟比出帧的钟快（界面 20 fps、这一路 12 fps），总有好几手落在两帧中间
    /// —— 2026-10-08 真机量过：就算**同频**（12 fps 取、12 fps 出）也已有约一成的手
    /// 落在两帧中间，且随相位在 0.2%–43% 之间摆。空只许代表一件事：真的没画面。
    /// </para>
    /// </remarks>
    [RequiresFfmpegFact]
    public async Task 取走的一帧不该再算丢()
    {
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var phone = new FakePhone(ffmpeg!, 480, 854, 30);

        await using var tile = LiveTileProcess.Start(ffmpeg!, phone.LiveUrl, 320, 180);

        Assert.NotNull(tile);

        Assert.NotNull(await WaitForFrameAsync(tile!, TimeSpan.FromSeconds(30)));

        // ⚠️ 只看**这一段里**长了多少：等第一帧那会儿是没人取的（`WaitForFrameAsync`
        // 每 50 ms 才看一眼），那期间攒下的几帧本来就该算丢 —— 那部分不算数。
        var droppedBefore = tile!.DroppedCount;

        // 一直取（比 12 fps 的来帧快得多）。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline) _ = tile.Take();

        var grew = tile.DroppedCount - droppedBefore;

        // 门槛给 5 帧：线程被抢走一下、正好卡在两帧中间，会真丢一两帧 ——
        // 而「取走那条路没有真的拿走」那一版这里会贴着来帧数走（反证到过 54 帧）。
        Assert.True(
            grew <= 5,
            $"一直取着的这 3 秒里还是记了 {grew} 帧「界面丢了」"
                + $"（这 3 秒一共收到 {tile.ReceivedCount} 帧）—— 取走那条路没有把帧真的拿走。");

        // ⚠️ 取走之后**不是空的**：取走只是「这一帧我看过了」，画面还在。
        // 空只代表一件事 —— **没有画面**（从没出过、或者已经过期）。
        // 这条与 `Cell.Pump` 那边是承重关系：它见空就抹掉画面写「无信号输入」。
        Assert.NotNull(await WaitForFrameAsync(tile!, TimeSpan.FromSeconds(10)));
        Assert.NotNull(tile!.Take());
        Assert.NotNull(tile.Take());

        // 而「没有新的」也确实是**同一帧**再给一次（界面比 CapturedAtMs 认得出，
        // 所以不会重画）。连取三手，至少有一对挨着的是同一帧 —— 中间来新帧是允许的。
        var seen = new List<long>();

        for (var i = 0; i < 3; i++) seen.Add(tile.Take()!.CapturedAtMs);

        Assert.True(
            seen[0] == seen[1] || seen[1] == seen[2],
            $"连着取三手拿到的是三个不同的帧（{string.Join('/', seen)}）—— "
                + "「没有新的就把上一帧再给一次」这条没有兑现。");
    }

    /// <summary>
    /// 这一格的参数里**不许再有 <c>-fflags nobuffer</c>**，限速也**不许再走输出侧的 <c>-r</c>**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>这是 2026-10-08 那个缺陷唯一站得住的闸。</b>真机上量到的现象是
    /// 「自报 0.01 fps（累计出 1 帧、丢 3099）」、画面从头到尾只有第一帧。
    /// 拦在参数这一层是因为**行为层拦不住**：把那一版参数放回去，本仓「按真时间
    /// 喂一台假手机」那条用例照样绿（实测 71 帧对 72 帧），真机那个形状在进程里
    /// 复现不出来。所以退一步 —— 照**参数本身**判，这个判法不会漏。
    /// </para>
    /// <para>
    /// ⚠️ 两条都要判：<c>nobuffer</c> 是那个开关，而输出侧 <c>-r</c> 会把
    /// <c>drop_frames</c> 顶成恒非零 —— 那是这一格唯一看得见的异常信号，
    /// 恒非零就等于没有。
    /// </para>
    /// </remarks>
    [Fact]
    public void 参数里不许再有nobuffer_限速也不许走输出侧()
    {
        var arguments = LiveTileProcess.BuildArguments("http://127.0.0.1:1/live", 320, 180);

        Assert.DoesNotContain("nobuffer", arguments);

        // `-r` 的后面跟着的就是它自己那个值，这里只看有没有这个开关。
        Assert.DoesNotContain("-r", arguments);

        var filter = arguments[arguments.IndexOf("-vf") + 1];

        Assert.Contains($"fps={LiveTileProcess.Fps}", filter, StringComparison.Ordinal);
    }

    /// <summary>
    /// 帧率要按**墙上时钟**算 —— 不许信 ffmpeg 替裸流猜的那个 25 fps。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>手机推的是裸 H.264，没有容器时间戳，ffmpeg 就给输入填一个默认的 25 fps</b>
    /// （`info` 那行印的 `25 fps, 1200k tbr, 1200k tbn` 里那两个 1200k 就是「没有信息」），
    /// 而手机**真推 32–33 fps**。下面 `fps=12` 照输入时间轴数 ⇒ 出来的是
    /// `12 × 33/25 ≈ 15.8`，比界面画得快，多出来的每一帧都被记成「界面丢了」。
    /// 2026-10-08 真机三档实测：**480P 16.40 / 720P 15.80 / 1080P 15.80**（与档位无关），
    /// 加上这一条之后三档都落回 **11.5–12**。
    /// </para>
    /// <para>
    /// ⚠️ <b>位置是承重的。</b>这是 <c>avformat</c> 的选项，必须在 <c>-i</c> <b>前面</b>；
    /// 放到后面 ffmpeg **不报错**、当输出侧选项默默忽略掉 —— 那就又回到 15.8 那条路上，
    /// 而这一格的画面看起来一切正常。
    /// </para>
    /// <para>
    /// ⚠️ <b>它换不掉那个「输入声明 <c>-r</c>」的写法。</b>实测过：`-r 30` 在 480P 是
    /// 11.90 看着还行，720P/1080P 冲到 13.80/13.70 —— 手机真推 32–33 &gt; 30。
    /// 那个数是手机给的，猜不得。
    /// </para>
    /// </remarks>
    [Fact]
    public void 帧率要按墙上时钟算()
    {
        var arguments = LiveTileProcess.BuildArguments("http://127.0.0.1:1/live", 320, 180);

        var at = arguments.IndexOf("-use_wallclock_as_timestamps");

        Assert.True(
            at >= 0,
            "这一格的参数里没有 -use_wallclock_as_timestamps —— 帧率又会回到 ffmpeg 替裸流猜的 25 fps 上去，"
                + "而 `fps=12` 出来的会是 15.8，一路健康也恒读一截「界面丢了」。");

        Assert.Equal("1", arguments[at + 1]);

        Assert.True(
            at < arguments.IndexOf("-i"),
            "-use_wallclock_as_timestamps 放在了 -i 后面 —— 那样 ffmpeg 不报错也不生效，等于没加。");
    }

    [RequiresFfmpegFact]
    public async Task 一格_画面与计数都拿得到()
    {
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264) { Outbound = 12, Returned = 3 };

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位");

        Assert.True(
            await WaitForFrameAsync(tile, TimeSpan.FromSeconds(30)) is not null,
            "没等到画面");

        await tile.RefreshStatusAsync();

        Assert.NotNull(tile.Counts);
        Assert.Equal(12, tile.Counts!.Outbound);
        Assert.Equal(3, tile.Counts.Returned);
        Assert.Equal(480, tile.Counts.ReportedQuality);
        Assert.Null(tile.Problem);

        // 编码侧丢帧（T11 的 `d`）真的被读进来了。
        Assert.Equal(5, tile.Counts.PhoneDropped);

        // ⚠️ 老手机端（改造之前那一版）报文里**没有 `d`**：要读成 0，
        // 不是「未知」。理由见 `LiveTile.RefreshStatusAsync` 里那一行注释 ——
        // 多一个界面上分不出来的「未知」态，只会让人去查一个不存在的问题。
        phone.Dropped = null;

        await tile.RefreshStatusAsync();

        Assert.Equal(0, tile.Counts!.PhoneDropped);
    }

    [RequiresFfmpegFact]
    public async Task 帧率是量出来的_手机一停它就掉到零()
    {
        // ★ T11 的这一条：它是「帧率」这两个字**唯一**的判据。
        //
        // ⚠️ 它挡的是「把 `LiveTileProcess.Fps` 那个 12 直接当成帧率」那种实现 ——
        // 它画面正常时看起来**完全正确**，只有在坏掉的时候才露出破绽，
        // 而「坏掉的时候」正是这一行字唯一要让人看见的时刻。
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264);

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位");

        Assert.True(await WaitForFrameAsync(tile, TimeSpan.FromSeconds(30)) is not null);

        // ⚠️ 第一次采样**只能记底数**（没有上一次的数就没有差值）——
        // 这时必须是 `null` 而不是 0：0 会被画成「0.0 fps」，
        // 而「刚开、还没量」与「这条管子已经死了」在屏幕上必须是两句话。
        tile.SampleReceiveRate();

        Assert.True(tile.ReceiveFps is null, $"刚开就报了个帧率：{tile.ReceiveFps}");

        await Task.Delay(700);
        tile.SampleReceiveRate();

        Assert.True(
            tile.ReceiveFps is > 0,
            $"画面在动、帧率却是 {tile.ReceiveFps?.ToString(CultureInfo.InvariantCulture) ?? "null"}");

        // 手机整个消失：管子死了，这个数**必须跟着掉到 0**。
        phone.Dispose();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && tile.Latest() is not null) await Task.Delay(50);

        Assert.Null(tile.Latest());

        // ⚠️ 采两次：第一次那一段窗口有一半还是**断之前**的，算出来会是个假的非零数。
        await Task.Delay(400);
        tile.SampleReceiveRate();
        await Task.Delay(400);
        tile.SampleReceiveRate();

        Assert.True(
            tile.ReceiveFps is 0,
            $"手机已经停了，帧率还报着 {tile.ReceiveFps?.ToString(CultureInfo.InvariantCulture) ?? "null"}");
    }

    [Fact]
    public void 进度行与告警行分得开()
    {
        // ⚠️ 分错的**两次**都贵：进度行每秒都来，判成告警就会把 `ErrorTail`
        // （16 KB）灌满 —— 而真出问题时 ffmpeg 说的那几句原话是 I3 唯一的证据；
        // 反过来把告警判成进度，那一句就**永远看不见**。
        //
        // 下面这些字符串是**真的**（2026-10-05 拿本机 ffmpeg 跑出来抄的）。
        Assert.Equal(("fps", "12.00"), LiveTileProcess.ParseProgressLine("fps=12.00"));
        Assert.Equal(("frame", "122"), LiveTileProcess.ParseProgressLine("frame=122"));
        Assert.Equal(("dup_frames", "3"), LiveTileProcess.ParseProgressLine("dup_frames=3"));
        Assert.Equal(("drop_frames", "28"), LiveTileProcess.ParseProgressLine("drop_frames=28"));

        // `stream_0_0_q` 那种带数字的键也算（ffmpeg 就这么发的）。
        Assert.Equal(("stream_0_0_q", "-0.0"), LiveTileProcess.ParseProgressLine("stream_0_0_q=-0.0"));
        Assert.Equal(("progress", "end"), LiveTileProcess.ParseProgressLine("progress=end"));

        // 只切第一个等号，值照原样给。
        Assert.Equal(("out_time", "00:00:10.166667"), LiveTileProcess.ParseProgressLine("out_time=00:00:10.166667"));

        foreach (var warning in new[]
                 {
                     "[tcp @ 000001ee0ab63cc0] Connection to tcp://127.0.0.1:9 failed: Error number -138 occurred",
                     "[in#0 @ 000001ee0abd2500] Error opening input: Error number -138 occurred",
                     "Error opening input file http://127.0.0.1:9/live.",
                     "Invalid data found when processing input",
                     string.Empty,
                 })
        {
            Assert.Null(LiveTileProcess.ParseProgressLine(warning));
        }
    }

    [RequiresFfmpegFact]
    public async Task 这一格把自己量到的帧率与复帧数写进日志()
    {
        // ★ T16 取证：这一格「卡不卡」，我们这一头只有「收到多少帧」「界面丢多少帧」
        // 两个数 —— 它们分不出「手机推得慢」与「ffmpeg 在按 `-r 12` 凑帧」。
        // 而后者（源 15 fps、输出钉死 12 fps）在画面上就是**有规律的顿挫**。
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264);
        var logger = new CapturingLogger();

        // ⚠️ 这里起的是**进程那一层**（不是 `LiveTile`）：`ErrorTail` 只有它有，
        // 而「进度行有没有灌进错误尾巴」正是这条用例的另一半。
        // ⚠️ 报数间隔调到 1 秒（生产上是 10 秒，见 `ProgressWatch.DefaultIntervalMs`）：
        // 那条日志是**攒够一段才报**的，而「攒够」是个墙上时钟 —— 原先照生产的 10 秒
        // 等，本机 11 秒能成、托管 runner 上就超了 30 秒的预算（2026-10-07 红过一次）。
        // 调短之后断言的是**同一段逻辑**（认进度行、攒数、跟错误尾巴分开），
        // 代价只是不再顺带证明「连续十秒都有进度」—— 那一件事由上面等帧那一步盖着。
        await using var tile = LiveTileProcess.Start(
            ffmpeg!, phone.LiveUrl, 320, 180, logger, progressInterval: TimeSpan.FromSeconds(1));
        Assert.NotNull(tile);

        Assert.True(await WaitForFrameAsync(tile!, TimeSpan.FromSeconds(30)) is not null);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var waitingFrom = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline && !logger.Messages.Any(m => m.Contains("ffmpeg 自报")))
        {
            await Task.Delay(100);
        }

        var line = logger.Messages.FirstOrDefault(m => m.Contains("ffmpeg 自报"));

        Assert.NotNull(line);

        // ★ 上面那个 `progressInterval: 1 秒` 的**绊线** —— 少了这一条，谁把那个实参
        // 去掉都不会有测试喊（两种写法在本机都绿，只差 2 秒与 11 秒）。
        // 判据是硬的、不是估的：`ProgressWatch.Take` 里那句
        // `if (now - _atMs < _intervalMs) return false;` —— 间隔写死成生产的
        // `DefaultIntervalMs`（10 000）时，**第一个 `progress` 块之后不满 10 秒，
        // 这句日志一个字都出不来**。所以 9 秒是个能失败的下界（走生产间隔必红），
        // 同时留着 4.5 倍的余量（2026-10-07 本机实测 2 秒）—— 比原先「11 秒等 30 秒」
        // 那点 2.7 倍余量宽。
        Assert.True(
            DateTime.UtcNow - waitingFrom < TimeSpan.FromSeconds(9),
            $"「ffmpeg 自报」等了 {(DateTime.UtcNow - waitingFrom).TotalSeconds:F1} 秒 —— "
            + "要么传下去的 progressInterval 没生效（又回到生产的 10 秒），"
            + "要么这台机器慢到不适合跑这条用例。");

        Assert.Contains("我们收到", line);
        Assert.Contains("复制", line);

        // ⚠️ 还有一半是**反向**的：进度那几行不许落在错误尾巴里。
        // 落在里面的话，真断线时那一格说的原因会被进度刷掉（尾巴只有 16 KB）。
        Assert.DoesNotContain("fps=", tile!.ErrorTail);
    }

    [RequiresFfmpegFact]
    public async Task 改档要先告诉手机_成了本地才按新尺寸重来()
    {
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264);

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位");
        Assert.Equal(LiveQuality.P480, tile.Quality);

        var changed = await tile.SetQualityAsync(LiveQuality.P1080);

        Assert.True(changed);
        Assert.Equal([1080], phone.QualityRequests);
        Assert.Equal(LiveQuality.P1080, tile.Quality);

        // 本地那一路按新尺寸重来了 —— 1080 档是 1920×1080。
        var frame = await WaitForFrameAsync(tile, TimeSpan.FromSeconds(30));
        Assert.NotNull(frame);
        Assert.Equal(1920, frame!.Width);
        Assert.Equal(1080, frame.Height);
    }

    [RequiresFfmpegFact]
    public async Task 手机拒了改档_本地就不许按新档来()
    {
        // 反过来的话：手机没改成而本地按新档解，那一格会一直等一个永远不来的分辨率
        // —— 看起来像卡死。
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264) { RejectQuality = true };

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位");

        var changed = await tile.SetQualityAsync(LiveQuality.P1080);

        Assert.False(changed);
        Assert.Equal(LiveQuality.P480, tile.Quality);
    }

    [RequiresFfmpegFact]
    public async Task 手机把流断了_那一格自己接回来()
    {
        // ⚠️ 2026-10-03 那次「多画面卡顿」的日志里就是这个形状：ffmpeg 退了之后
        // **没有任何人重起它**，那一格就一直黑着 —— 当时唯一的恢复办法是把
        // 多画面窗口关掉重开（日志里那一分钟正是「退了 → 改档六次 → 关窗重开」）。
        //
        // 这条用例钉的是：第一路被挂断（一帧都没给）之后，那一格要**自己**接回来，
        // 而且接回来的那一路要真的出画面。
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));

        using var phone = new FakePhone(h264) { HangUpLiveTimes = 1 };
        var logger = new CapturingLogger();

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位", logger: logger);

        // 第一路一帧都没有，画面只可能来自重连后的第二路。
        var frame = await WaitForFrameAsync(tile, TimeSpan.FromSeconds(60));

        Assert.True(
            frame is not null,
            $"断了一路之后没接回来。日志：{string.Join(" / ", logger.Messages)}");

        Assert.Contains(logger.Messages, m => m.Contains("再试"));
    }

    [RequiresFfmpegFact]
    public async Task 改档换掉的那一路_旧的不许被当成断线重连()
    {
        // ⚠️ 换档时旧的那一路也是「自己结束」的样子。认错的话，一次改档会额外
        // 多起一路 ffmpeg（日志里就是「起了一路」比改档次数多）。
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264);
        var logger = new CapturingLogger();

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位", logger: logger);

        Assert.True(await WaitForFrameAsync(tile, TimeSpan.FromSeconds(30)) is not null);

        Assert.True(await tile.SetQualityAsync(LiveQuality.P720));

        // 改档之后那一路要出得来画面（本地按新尺寸重起）。
        Assert.True(
            await WaitForFrameAsync(tile, TimeSpan.FromSeconds(30)) is not null,
            "改档之后没有画面");

        // 给它够长的时间去犯「把换掉的那一路当成断线」这个错（退避是 2 秒起）。
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(logger.Messages, m => m.Contains("接回来"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("再试"));
    }

    [RequiresFfmpegFact]
    public async Task 问不到计数不影响画面()
    {
        // 网络抖一下不该让整面墙闪一下「无信号输入」。
        //
        // ⚠️ 这里「问不到计数」用的是**假的坏 `/status`**，不是把手机整个关掉：
        // 关掉手机连**视频那一路**也断了，那是另一件事（见下一个用例）——
        // 2026-10-03 之前这条用例正是拿 `phone.Dispose()` 凑的，
        // 而那时「断了」根本不会有人管，所以两种毛病看着一样。
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264) { BrokenStatus = true };

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位");

        Assert.True(await WaitForFrameAsync(tile, TimeSpan.FromSeconds(30)) is not null);

        await tile.RefreshStatusAsync();

        Assert.Null(tile.Counts);
        // 画面还在，只是下面那两个字先不显示。
        Assert.NotNull(tile.Latest());
    }

    [RequiresFfmpegFact]
    public async Task 手机整个不见了_那一格会黑掉而不是冻着上一帧()
    {
        // ⚠️ **冻着的画面看起来与实时的没两样**：一台手机掉线之后那一格要是继续
        // 显示最后一帧，看的人会以为现场就是这样 —— 那比黑着更糟。
        // 所以视频流真的没了的时候，`Latest()` 必须回到 null（界面画「无信号输入」
        // 或者 `Problem` 里那句原因），而不是一直端着那张旧图。
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264);

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位");

        Assert.True(await WaitForFrameAsync(tile, TimeSpan.FromSeconds(30)) is not null);

        // 手机整个消失：视频那一路跟着断。
        phone.Dispose();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTime.UtcNow < deadline && tile.Latest() is not null)
        {
            await Task.Delay(50);
        }

        Assert.Null(tile.Latest());
    }

    [RequiresFfmpegFact]
    public async Task 手机没开实时共享时_起得来但不报帧也不抛()
    {
        // ⚠️ 这就是「无信号输入」那一态：机位不够、或者那台手机没开共享。
        // 抛异常的话整个多画面窗口都开不了 —— 一格拉不起来不该拖垮别的八格。
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        var dead = new TcpListener(IPAddress.Loopback, 0);
        dead.Start();
        var port = ((IPEndPoint)dead.LocalEndpoint).Port;
        dead.Stop();

        await using var tile = LiveTile.Start(ffmpeg!, $"http://127.0.0.1:{port}", "空机位");

        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Null(tile.Latest());

        // 计数问不到也不该抛。
        await tile.RefreshStatusAsync();
        Assert.Null(tile.Counts);
    }

    [RequiresFfmpegFact]
    public async Task 计数问不到时_只在变坏和变好各记一条()
    {
        // ⚠️ 这是 §6.1 那条配套要求：**重复的问题只在「变了」的时候记**。
        // 计数是**每秒问一次**的；每次都记的话一分钟六十条，
        // 而被灌满的日志等于没有日志。
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264) { BrokenStatus = true };
        var logger = new CapturingLogger();

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位", logger: logger);

        // 只数跟计数有关的那几条（同一路上还有「起了一路」之类的正常留痕）。
        List<string> About() => [.. logger.Messages.Where(m => m.Contains("计数") || m.Contains("问到"))];

        for (var i = 0; i < 4; i++) await tile.RefreshStatusAsync();

        Assert.Single(About());
        Assert.Contains("问不到计数", About()[0]);
        Assert.Null(tile.Counts);

        // 坏 → 好：这一下也该说一条 —— 不然「什么时候恢复的」没人知道。
        phone.BrokenStatus = false;
        await tile.RefreshStatusAsync();

        Assert.Equal(2, About().Count);
        Assert.Contains("又能问到计数", About()[1]);
        Assert.NotNull(tile.Counts);
    }

    // ─────────────────────────────────────────────
    // 夹具
    // ─────────────────────────────────────────────

    /// <summary>把日志收起来，好在测试里断言（本仓既有写法）。</summary>
    private sealed class CapturingLogger : VidLog.Desktop.Core.Diagnostics.IAppLogger
    {
        public List<string> Messages { get; } = [];

        public void Log(VidLog.Desktop.Core.Diagnostics.LogLevel level, string category, string message) =>
            Messages.Add(message);

        public void Log(
            VidLog.Desktop.Core.Diagnostics.LogLevel level,
            string category,
            string message,
            IReadOnlyDictionary<string, object?> data) => Messages.Add(message);
    }

    /// <summary>临时目录（本仓每个测试类各自带一个，是既有惯例）。</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 进程还没放干净 —— 留给系统清，不为它把测试弄红。
            }
        }
    }

    /// <summary>用 lavfi 造一段真的 H.264（Annex-B）。</summary>
    private static async Task<byte[]> EncodeAsync(string ffmpeg, string path)
    {
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error",
                     "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10",
                     "-t", "1",
                     "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
                     "-pix_fmt", "yuv420p",
                     "-f", "h264", path,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();

        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : [];
    }

    private static async Task<LiveFrame?> WaitForFrameAsync(LiveTileProcess tile, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (tile.Latest() is { } frame) return frame;
            await Task.Delay(50);
        }

        return null;
    }

    private static async Task<LiveFrame?> WaitForFrameAsync(LiveTile tile, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (tile.Latest() is { } frame) return frame;
            await Task.Delay(50);
        }

        return null;
    }

    /// <summary>
    /// 一台**假手机**：按手机那侧的契约提供 <c>/live</c>、<c>/status</c>、<c>/quality</c>。
    /// </summary>
    private sealed class FakePhone : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[]? _payload;
        private readonly string? _ffmpeg;
        private readonly List<string> _liveArguments = [];
        private readonly CancellationTokenSource _cts = new();

        public FakePhone(byte[] payload)
        {
            _payload = payload;
            BaseUrl = Listen();
        }

        /// <summary>
        /// 一台**真的在推流**的假手机：不喂预先编好的那段，而是**现场起一个 ffmpeg
        /// 按真时间一帧一帧编**，把它的输出原样转出去。
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>`-re` 是这里的承重项。</b>不加的话编码器会把整段一口气吐出来 ——
        /// 那是「先下完再播」的形状，**正好遮住 <c>-fflags nobuffer</c> 那个缺陷**
        /// （2026-10-08 实测：同一段流，按真时间喂这一格从头到尾只出 1 帧，
        /// 一口气喂就照常出帧）。拿预编好的字节循环喂也不行，试过：
        /// 全 I 帧那段 12 帧对 30 帧、长 GOP 那段 24 帧对 57 帧 —— 都只是
        /// 差一截，不像真机那样「卡死」。所以这一档必须现场编码。
        /// </remarks>
        public FakePhone(string ffmpeg, int width, int height, int fps)
        {
            _ffmpeg = ffmpeg;
            _liveArguments.AddRange(
            [
                "-hide_banner", "-loglevel", "error",
                "-re", "-f", "lavfi", "-i", $"testsrc=size={width}x{height}:rate={fps}",
                "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
                "-pix_fmt", "yuv420p",
                "-f", "h264", "pipe:1",
            ]);
            BaseUrl = Listen();
        }

        private string Listen()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);

            return $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        }

        public string BaseUrl { get; }

        public string LiveUrl => $"{BaseUrl}/live";

        public int Outbound { get; set; } = 7;

        public int Returned { get; set; } = 2;

        /// <summary>
        /// 手机报上来的编码侧丢帧（T11 的 <c>d</c>）。
        /// ⚠️ <b><see langword="null"/> = 报文里**不带这个键**</b> —— 用来演
        /// 「老的手机端」（改造之前那一版只会报 f/t/p）。
        /// </summary>
        public int? Dropped { get; set; } = 5;

        /// <summary>手机现在这一档。</summary>
        public int Quality { get; private set; } = 480;

        /// <summary>装成「这个应用不接受改档」（未过审、或那一档不支持）。</summary>
        public bool RejectQuality { get; set; }

        /// <summary>装成「计数坏了」（手机在、画面也在，就是 /status 回不了）。</summary>
        public bool BrokenStatus { get; set; }

        /// <summary>
        /// 头几路 <c>/live</c> **连上就断、一帧都不给**（模拟「那一格断了」）。
        /// </summary>
        /// <remarks>
        /// 断在**发数据之前**：这样重连是拿到画面的唯一途径，
        /// 用例才真的证明了「自己接回来了」，而不是靠第一路的帧蒙过去。
        /// </remarks>
        public int HangUpLiveTimes { get; set; }

        private int _hungUp;

        /// <summary>收到过的改档请求（按顺序）。</summary>
        public List<int> QualityRequests { get; } = [];

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;

            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, _cts.Token);
                if (request is null) return;

                var (path, query) = SplitTarget(request);

                if (path == "/live")
                {
                    await ServeVideoAsync(stream);
                    return;
                }

                if (path == "/status")
                {
                    if (BrokenStatus)
                    {
                        await WriteAsync(stream, 500, """{"error":"broken"}""");
                        return;
                    }

                    await WriteAsync(
                        stream,
                        200,
                        Dropped is { } d
                            ? $$"""{"f":{{Outbound}},"t":{{Returned}},"p":{{Quality}},"d":{{d}}}"""
                            : $$"""{"f":{{Outbound}},"t":{{Returned}},"p":{{Quality}}}""");
                    return;
                }

                if (path == "/quality")
                {
                    await ServeQualityAsync(stream, query);
                    return;
                }

                await WriteAsync(stream, 404, """{"error":"not found"}""");
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // 客户端走了 —— 正常路径。
            }
        }

        private async Task ServeQualityAsync(NetworkStream stream, string query)
        {
            var raw = query.Split('&')
                .Select(pair => pair.Split('=', 2))
                .FirstOrDefault(pair => pair.Length == 2 && pair[0] == "p")?[1];

            if (RejectQuality || !int.TryParse(raw, out var wanted) || wanted is not (480 or 720 or 1080))
            {
                await WriteAsync(stream, 400, """{"error":"bad quality"}""");
                return;
            }

            QualityRequests.Add(wanted);
            Quality = wanted;

            await WriteAsync(stream, 200, $$"""{"p":{{wanted}}}""");
        }

        private async Task ServeVideoAsync(NetworkStream stream)
        {
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: video/h264\r\n"
                    + "Cache-Control: no-store\r\n"
                    + "Connection: close\r\n\r\n"),
                _cts.Token);

            await stream.FlushAsync(_cts.Token);

            // 装成「这一路断了」：头几路连上就断、一个字节都不发（`ServeAsync` 的
            // `using` 一退出，这条连接就关了）。
            if (Interlocked.Increment(ref _hungUp) <= HangUpLiveTimes) return;

            if (_payload is { } payload)
            {
                // ⚠️ 一直循环喂：既是「实时流不结束」的形状，
                // 也让每个循环开头都带上一组 SPS/PPS + IDR —— 正是手机那侧的契约。
                while (!_cts.IsCancellationRequested)
                {
                    await stream.WriteAsync(payload, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                    await Task.Delay(50, _cts.Token);
                }

                return;
            }

            await ServeLiveAsync(stream);
        }

        /// <summary>
        /// 现场编码的推流：起一个 ffmpeg，把它吐出来的字节原样转给客户端。
        /// </summary>
        /// <remarks>
        /// ⚠️ 编码器**每条连接起一个、断连就杀**：它是个无限流，不杀就会一直
        /// 编下去（用例跑完进程还赖着）。每连接一个也正合手机那侧的契约 ——
        /// 重新连上就是新的一段流，开头照例带 SPS/PPS + IDR。
        /// </remarks>
        private async Task ServeLiveAsync(NetworkStream stream)
        {
            var startInfo = new ProcessStartInfo(_ffmpeg!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in _liveArguments) startInfo.ArgumentList.Add(argument);

            using var encoder = Process.Start(startInfo)!;

            // stderr 要排空（不排的话管道一满编码器就顶住了），但内容不用留 ——
            // 真出事时那几句会随用例断言一起从这一格自己的 `ErrorTail` 出来。
            _ = encoder.StandardError.ReadToEndAsync(_cts.Token);

            try
            {
                // ⚠️ <b>攒够一块再发，别「来多少发多少」。</b>编码器的管道是一小段
                // 一小段吐的，照抄着转发就成了细水长流 —— 而**细水长流正好遮住
                // `-fflags nobuffer` 那个缺陷**（2026-10-08 实测：细水长流 82 帧对
                // 116 帧，攒成 4 KB 一块发则是 1 帧对 100 帧）。真机也是**一帧一块**。
                var buffer = new byte[4096];
                var filled = 0;

                while (!_cts.IsCancellationRequested)
                {
                    var read = await encoder.StandardOutput.BaseStream.ReadAsync(
                        buffer.AsMemory(filled), _cts.Token);
                    if (read <= 0) return;

                    filled += read;
                    if (filled < buffer.Length) continue;

                    await stream.WriteAsync(buffer, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                    filled = 0;
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // 客户端走了 —— 正常路径。
            }
            finally
            {
                try
                {
                    if (!encoder.HasExited) encoder.Kill();
                }
                catch (InvalidOperationException)
                {
                    // 已经自己退了。
                }
            }
        }

        private static async Task WriteAsync(NetworkStream stream, int status, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {StatusText(status)}\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Cache-Control: no-store\r\n"
                + "Connection: close\r\n"
                + $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");

            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }

        private static string StatusText(int status) => status switch
        {
            200 => "OK",
            400 => "Bad Request",
            _ => "Not Found",
        };

        private static (string Path, string Query) SplitTarget(string requestLine)
        {
            // "GET /live?x=1 HTTP/1.1"
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return ("/", string.Empty);

            var target = parts[1];
            var cut = target.IndexOf('?');

            return cut < 0 ? (target, string.Empty) : (target[..cut], target[(cut + 1)..]);
        }

        private static async Task<string?> ReadRequestAsync(
            NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024];
            var seen = new StringBuilder();

            while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read <= 0) return null;

                seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (seen.Length > 8192) break; // 防呆：正常请求头不会这么大
            }

            var lines = seen.ToString().Split("\r\n");
            return lines.Length > 0 ? lines[0] : null;
        }

        public void Dispose()
        {
            // ⚠️ 幂等：有的用例会**中途**把手机「关掉」（模拟掉线），
            // 然后 `using` 再放一次 —— 直接 Cancel 一个已释放的 CTS 会抛。
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            try { _cts.Cancel(); } catch (ObjectDisposedException) { }

            _listener.Stop();
            _cts.Dispose();
        }

        private int _disposed;
    }
}
