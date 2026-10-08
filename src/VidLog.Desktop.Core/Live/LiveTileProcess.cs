using System.Diagnostics;
using System.Text;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Live;

/// <summary>一路实时画面里的一帧（RGB24，已经缩放到格子要的尺寸）。</summary>
public sealed record LiveFrame(byte[] Rgb, int Width, int Height, long CapturedAtMs)
{
    /// <summary>一行的字节数。RGB24 是 3 字节一个像素。</summary>
    public int Stride => Width * 3;
}

/// <summary>
/// 多画面里的**一格**：一个 ffmpeg 拉一路手机的实时流，吐出定尺寸的裸帧。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="PreviewProcess"/> 是**两份实现**，故意的：那个拉的是本机相机
/// （DirectShow 名）、尺寸写死 640×360、给配置向导用；这个拉的是手机推上来的
/// HTTP 裸流、尺寸按画质档变、给多画面用。合成一个类要么让一半参数在另一半那里
/// 永远没有意义，要么加一层「源」的抽象 —— 而两边真正共用的只有「读定长裸帧」
/// 这十来行。
/// </para>
/// <para>
/// ⚠️ <b>输入是 H.264 裸流，所以必须显式给 <c>-f h264</c>。</b>
/// 不给的话 ffmpeg 会去**探测**容器，而裸流没有容器可探 —— 表现是一直卡在
/// 「等待输入」上，看起来像手机没推流（其实是这边认不出来）。
/// 手机那侧的契约见 `LiveServer`：Annex-B、每个关键帧前带 SPS/PPS。
/// </para>
/// <para>
/// ⚠️ <b><c>-rw_timeout</c> 不是可选项。</b>手机那边相机没开时，HTTP 连接会
/// **一直挂着不发一个字节**（那边刻意如此：ffmpeg 连上就该等到有帧为止）。
/// 不给读超时的话，这一格的 ffmpeg 会永远挂着，而用户看到的是「一直黑着」——
/// 分不出是手机没开、还是网络断了、还是电脑这边卡住了。
/// </para>
/// <para>
/// ⚠️ <b>每一格一个 ffmpeg 进程。</b>9 格就是 9 个。这是这一块最大的成本，
/// 也是为什么格子只出 480P：ffmpeg 那边解码 + 缩放的开销与分辨率直接相关。
/// </para>
/// </remarks>
public sealed class LiveTileProcess : IAsyncDisposable
{
    /// <summary>格子的帧率。</summary>
    /// <remarks>
    /// 12 fps：与预览同一档。多画面是「看现场有没有在动」，
    /// 不是看片 —— 而 9 格 × 30 fps 的缩放与贴图开销全是白烧的。
    /// </remarks>
    public const int Fps = 12;

    private readonly Process _process;
    private readonly BoundedTextTail _errors;
    private readonly IAppLogger _logger;

    /// <summary>这一路**自己**结束了（不是我们收的）时报一下 —— 见 <see cref="Start"/>。</summary>
    private readonly Action<LiveTileProcess>? _onEnded;

    private readonly object _gate = new();

    /// <summary>还没被取走的那一帧（`_dropped` 数的是它被盖掉了几次）。</summary>
    private LiveFrame? _latest;

    /// <summary>上一次被 <see cref="Take"/> 取走的那一帧 —— 取走不等于画面没了，
    /// 它的用处是让「这一手还没新帧」与「真的没画面」分得开（见 <see cref="Take"/>）。</summary>
    private LiveFrame? _shown;
    private long _dropped;
    private long _received;
    private int _stopped;
    private bool _hadFrame;

    /// <summary>ffmpeg 自报的那几个数（`-progress pipe:2`）。见 <see cref="DrainErrorsAsync"/>。</summary>
    private readonly ProgressWatch _progress;

    /// <summary>读帧那一趟。⚠️ 它有**一次赋值、一次读**，都在同一线程的
    /// 构造与 <see cref="DisposeAsync"/> 上，所以不加锁。</summary>
    private Task _readLoop = Task.CompletedTask;

    private LiveTileProcess(
        Process process,
        BoundedTextTail errors,
        IAppLogger logger,
        Action<LiveTileProcess>? onEnded,
        long progressIntervalMs)
    {
        _process = process;
        _errors = errors;
        _logger = logger;
        _onEnded = onEnded;
        _progress = new ProgressWatch(progressIntervalMs);
    }

    /// <summary>最新那一帧。还没有就是 <see langword="null"/>（界面画「无信号输入」）。</summary>
    /// <remarks>
    /// ⚠️ 这是**看一眼**，帧不会少（也不会让 <see cref="DroppedCount"/> 变准）。
    /// 要「画一次」请用 <see cref="Take"/>，两条路的区别见那边。
    /// </remarks>
    public LiveFrame? Latest()
    {
        lock (_gate)
        {
            return _latest ?? _shown;
        }
    }

    /// <summary>
    /// 最新那一帧，**取走**：有新的就给新的，没有就把上一次给过的那一帧再给一次。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>画的那一处必须用它。</b><see cref="DroppedCount"/> 数的正是
    /// 「新的一帧来的时候，上一帧**还没被取走**」—— 也就是说，只有取走的人
    /// 才能让这个数有意义。画的路上要是用 <see cref="Latest"/>（只看不拿），
    /// 第一帧之后**每一帧都会被记成「我们丢了」**：2026-10-08 真机上量到的是
    /// 「我们收到 1069 帧、界面丢了 1068」—— 恒等于收到数减一。
    /// 而这个数正是 T11 用来分「手机上没编出来」与「这台电脑画不过来」的东西，
    /// 恒非零就等于没有（与上面 <c>-r</c> 把 <c>drop_frames</c> 顶成恒非零同一条）。
    /// </para>
    /// <para>
    /// ⚠️ <b>「这一手没新的」返回上一次那一帧，不返回 <see langword="null"/>。</b>
    /// 界面那条画帧的路见空就抹画面写「无信号输入」，而**空只代表一件事：
    /// 没有画面**（从没出过、或者已经过期）。两种空分不开的话，
    /// 画面正常、只是这一拍还没轮到新帧时，格子会闪一下黑 ——
    /// 2026-10-08 量过：界面按 12 fps 的节拍取，**73 手里有 24 手取到空（32.9%）**，
    /// 因为取帧的钟与出帧的钟各走各的，总有一手落在两帧中间。
    /// 判「有没有变」是界面自己的事（比 <c>CapturedAtMs</c>），
    /// 这一层只保证「拿到的永远是画面，除非真的没画面」。
    /// </para>
    /// </remarks>
    public LiveFrame? Take()
    {
        lock (_gate)
        {
            if (_latest is null) return _shown;

            _shown = _latest;
            _latest = null;
            return _shown;
        }
    }

    /// <summary>因为界面没跟上而丢掉的帧数（诊断用）。</summary>
    public long DroppedCount
    {
        get { lock (_gate) { return _dropped; } }
    }

    /// <summary>
    /// 这一路**收进来**的完整帧数（诊断用，T11 的「网络侧」那个数的底料）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它与 <see cref="DroppedCount"/> 是**两件事**，不重合：
    /// 这个是「网络那头给了多少帧」（来源健不健康），那个是
    /// 「界面这一头没跟上多少」（我们自己画得慢不慢）。
    /// 只看得见其中一个时，「画面卡」到底怪谁分不出来 —— 那正是 T11 要拆的东西。
    /// </remarks>
    public long ReceivedCount
    {
        get { lock (_gate) { return _received; } }
    }

    /// <summary>ffmpeg 说的最后一句话（起不来时用它给用户一个原因，I3）。</summary>
    public string ErrorTail => _errors.ToString();

    /// <summary>还在跑没有。</summary>
    public bool IsRunning => !_process.HasExited;

    /// <summary>这一路出过画面没有。</summary>
    /// <remarks>
    /// ⚠️ 重连时要用它分两种情况：<b>出过画面又断</b>（网抖、手机那侧重启）值得马上再试；
    /// <b>一帧都没出过</b>多半是手机没开实时共享，硬试没用，得退避甚至停手。
    /// </remarks>
    public bool HadFrame
    {
        get { lock (_gate) { return _hadFrame; } }
    }

    /// <summary>
    /// 起一路。<paramref name="url"/> 形如 <c>http://192.168.1.9:PORT/live</c>。
    /// </summary>
    /// <param name="onEnded">
    /// ⚠️ <b>这一路**自己**结束了（不是被 <see cref="DisposeAsync"/> 收掉的）时叫一下。</b>
    /// 调用方（<see cref="LiveTile"/>）据此把它接回来。参数就是**结束的这一路** ——
    /// 调用方靠它认出「这是不是我刚换掉的那个」（换档时旧的那一路也是这么结束的，
    /// 那种不算断线）。
    /// <para>
    /// 没有这个回调的话，ffmpeg 一退那一格就**永远黑着** —— 关掉重开多画面窗口是
    /// 当时唯一的恢复路径。2026-10-03 的日志里那一分钟正是这个形状：
    /// 一格退了 → 用户折腾了六次改档 → 最后还是把窗口关掉重开的。
    /// </para>
    /// </param>
    /// <param name="progressInterval">
    /// 「ffmpeg 自报」那句日志**隔多久放一条**（默认 <see cref="ProgressWatch.DefaultIntervalMs"/>）。
    /// ⚠️ <b>这个口子是给测试开的，生产上别传。</b>那条日志的节奏是**给眼睛看的**
    /// （10 秒既够看清哪一段时间不对、又不至于把日志淹掉），可它同时也把
    /// 「等这句日志出现」这件事**钉在了墙上时钟上** —— 2026-10-07 CI 上就这么红过一次
    /// （本机 11 秒，托管 runner 上超了 30 秒的预算）。测试把它调到一两秒，
    /// 断言的还是**同一段逻辑**（认进度行、攒数、跟错误尾巴分开），只是不必真等十秒。
    /// </param>
    /// <remarks>
    /// ⚠️ <b>起不来返回 <see langword="null"/>，不抛。</b>一格拉不起来不该让整个
    /// 多画面窗口开不了 —— 它自己那一格画「无信号输入」并写明原因就够了。
    /// </remarks>
    public static LiveTileProcess? Start(
        string ffmpegPath,
        string url,
        int width,
        int height,
        IAppLogger? logger = null,
        Action<LiveTileProcess>? onEnded = null,
        TimeSpan? progressInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);

        var log = logger ?? NullLogger.Instance;
        var errors = new BoundedTextTail();

        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // ⚠️ 承重：ffmpeg 写的是 UTF-8，不声明就按控制台码页（中文 Windows = 936）读
            // —— 这一格掉线的原因就是从这儿进日志的。
            // stdout 是 **MJPEG 裸流**，与编码无关，所以只声明这一条。
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in BuildArguments(url, width, height))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.Log(LogLevel.Warn, "多画面", $"这一格起不来：{ex.Message}");
            process.Dispose();
            return null;
        }

        // ⚠️ 先建实例再起读循环，**而且只建一个**：读循环要挂在返回出去的那一个上。
        // （先前写成「建两个、循环挂在前一个上」，返回的那个永远读不到帧。）
        var tile = new LiveTileProcess(
            process,
            errors,
            log,
            onEnded,
            progressInterval is { } span ? (long)span.TotalMilliseconds : ProgressWatch.DefaultIntervalMs);
        var startedAt = Environment.TickCount64;

        // stderr 必须排空：一次刷屏就能把它灌满、把进程顶住。
        var drainErrors = Task.Run(() => tile.DrainErrorsAsync(process));

        tile._readLoop = Task.Run(
            () => tile.ReadFramesAsync(
                process, drainErrors, width, height, startedAt, CancellationToken.None));

        // ⚠️ 「起」也要留一条（§6.1：有生命周期的组件，起/停/失败各一条）。
        // 只记失败的话，「那一格为什么黑着」永远分不出「没起」和「起了没画面」。
        //
        // ⚠️ 地址直接记：**这一路上没有凭据** —— 手机那个推流服务是局域网内
        // 谁都能连的裸 HTTP（它不是鉴权端点，只是个画面源），所以这里不带
        // `CameraSource.Identity` 那种顾虑。真给它加鉴权时，这一行要跟着改。
        log.Log(LogLevel.Info, "多画面", $"这一格起了一路：{width}x{height} ← {url}");

        return tile;
    }

    /// <summary>
    /// ffmpeg 的参数。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>低延迟那一项是这条路的意义所在</b>：默认 ffmpeg 会攒够一段才出帧，
    /// 那在实时画面上就是「比现实慢几秒」—— 而看的人以为那就是现在。
    /// <c>low_delay</c> 让它来一帧出一帧。
    /// <para>
    /// ⚠️ <b>不要加回 <c>-fflags nobuffer</c>。</b>手机推的是**裸 H.264、没有容器
    /// 时间戳**；2026-10-08 真机上量到，那个开关一开，这一格**从头到尾只出 1 帧**
    /// （「自报 0.01 fps、累计出 1 帧、丢 3099」），而同一时刻去掉它出 215 帧。
    /// 为什么，**没验过** —— 推断是时间戳不可用 ⇒ 下面那步限速把每帧都判成
    /// 「还不到时候」。
    /// </para>
    /// <para>
    /// ⚠️ <b>行为层拦不住这个回归，闸落在参数上。</b>2026-10-08 试过两条路都失败：
    /// 拿预编好的流按真时间喂，只是「差一截」不是「卡死」；起一个<b>现场按真时间
    /// 编码</b>的假手机（<c>LiveTileProcessTests.FakePhone</c> 第二个构造函数），
    /// 把那一版参数原样放回来这一格**照样出 71 帧**，与修好的版本分不出。
    /// 所以改用 <c>LiveTileProcessTests.参数里不许再有nobuffer_限速也不许走输出侧</c>
    /// —— 照参数本身判。
    /// </para>
    /// <para>
    /// <c>public</c> 是本仓没有 <c>InternalsVisibleTo</c> 的结果（测试够得到的成员
    /// 一律是 <c>public</c>，<c>FfmpegCameraCapture.BuildArguments</c> 同款），
    /// 而上面那道闸正是照这个列表判的。
    /// </para>
    /// </remarks>
    public static List<string> BuildArguments(string url, int width, int height)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;

        return
        [
            "-hide_banner",
            "-loglevel", "warning",

            // ⚠️ 每秒往 stderr 吐一段 `键=值`（`fps=` / `frame=` / `dup_frames=` /
            // `drop_frames=` …）：**这一格自己是几帧、复制了几帧、丢了几帧**
            // 只有 ffmpeg 说得清（T16 取证）。读它的那一行在 `DrainErrorsAsync`。
            //
            // ⚠️ 只用 `-progress`，**不把 `-loglevel` 抬到 `info`**：info 会把
            // 输入/输出流的描述与那条人性化的进度行也一起倒进来，而它们与
            // 真正的告警混在一起、分不开 —— 那正是下面要避免的。
            "-progress", "pipe:2",

            // ⚠️ 读超时（微秒）：手机相机没开时那条 HTTP 连接会一直挂着，
            // 没有它这一格的 ffmpeg 就永远等下去。
            "-rw_timeout", "5000000",

            "-flags", "low_delay",

            // ⚠️ **帧率必须按墙上时钟算，不许按 ffmpeg 猜的那个。**
            //
            // 手机推的是裸 H.264，没有容器时间戳，于是 ffmpeg 给输入填一个**默认的
            // 25 fps**（`-loglevel info` 那行印的是 `25 fps, 1200k tbr, 1200k tbn`
            // —— 那两个 1200k 就是「没有信息」的占位值），而手机**真推的是 32–33 fps**。
            // 下面 `fps=12` 是照输入时间轴数的 ⇒ 出来的不是 12，是
            // `12 × 33/25 ≈ 15.8`（2026-10-08 真机三档实测 16.40 / 15.80 / 15.80，
            // **与画质档无关** —— 手机在 1080P 上照样推 32.2 fps）。
            //
            // 出得比界面画得快，多出来的每一帧都会被记成「界面丢了」（`DroppedCount`），
            // 于是一路健康、什么都不错的现场也恒读 20–28%，T11 拿这两个数分
            // 「手机上没编出来」与「这台电脑画不过来」就分不开了。
            // 加上这一条之后三档都落回 **11.5–12**（真机实测），结构性的那些丢帧没有了。
            //
            // ⚠️ **别改成输入侧 `-r 30`。** 实测：480P 那档是 11.90 看着还行，
            // 720P/1080P 冲到 13.80/13.70 —— 手机真推 32–33 > 30，那截又回来了。
            // 那个数是**手机给的**，猜不得，得让 ffmpeg 看真的到达时刻。
            //
            // ⚠️ 也不算「拿复制帧填空档」：2026-10-08 拿一台「推 5 秒、哑 4 秒、
            // 连接不关」的假手机量过，两种参数全程 `dup_frames=0`、
            // 哑的那几拍 `frame=` 原地不动 —— 「手机上没编出来」这条路没被遮住。
            "-use_wallclock_as_timestamps", "1",

            // ⚠️ 裸流必须显式指定，别让 ffmpeg 去探测（它探不出来）。
            "-f", "h264",
            "-i", url,

            "-an",
            "-map", "0:v:0",

            // 按比例缩放、四周补黑：格子尺寸是固定的，而各路手机的宽高比可能不同。
            //
            // ⚠️ 限速走 `fps` 滤镜、**不用输出侧的 `-r`**：后者是帧率转换，会把
            // `drop_frames` 顶成一个恒非零的数（30 fps 源上实测每秒 +15），而这个
            // 丢帧数正是 T16 唯一看得见的异常信号 —— 恒非零就等于没有。走滤镜后
            // 正常时是 0。
            "-vf", $"scale={width}:{height}:force_original_aspect_ratio=decrease,"
                + $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,"
                + $"fps={Fps.ToString(invariant)}",

            "-f", "rawvideo",
            "-pix_fmt", "rgb24",
            "pipe:1",
        ];
    }

    /// <summary>
    /// 排空 stderr，顺带把 `-progress pipe:2` 那几行挑出来（T16 取证）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>排空本身是必须的</b>：不读的话管道一满，ffmpeg 就被顶住
    /// （那一格会停住，看起来像手机掉线）。
    /// </para>
    /// <para>
    /// ⚠️ <b>进度那几行绝不能进 <see cref="ErrorTail"/></b>：它每秒都来一段，
    /// 而那个尾巴只有 16 KB —— 攒进去的话，真出问题时 ffmpeg 说的那几句原话
    /// 会被挤掉，而那是 I3 唯一的证据（「这一格为什么黑着」）。
    /// </para>
    /// </remarks>
    private async Task DrainErrorsAsync(Process process)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();

        try
        {
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    // `-progress` 是**一行一个键值**；`\r` 一起当断行（万一将来
                    // 谁把 `-loglevel` 抬上去，那条人性化进度行是 `\r` 结尾的）。
                    if (buffer[i] is not ('\n' or '\r'))
                    {
                        line.Append(buffer[i]);
                        continue;
                    }

                    var text = line.ToString();
                    line.Clear();

                    if (ParseProgressLine(text) is { } progress)
                    {
                        ReportProgress(progress.Key, progress.Value);
                    }
                    else if (text.Length > 0)
                    {
                        _errors.Append(text + "\n");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 进程收掉了 —— 正常路径。
        }
    }

    /// <summary>
    /// `键=值` 的一行（`-progress` 吐的）；**不是**就返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>认法是「小写键 + 等号」而不是列一份键名清单</b>：ffmpeg 那边加一个键
    /// （它确实会加），清单派就会把那一行判成告警、灌进 <see cref="ErrorTail"/> ——
    /// 正是这个方法要防的那件事。反过来，告警行几乎都以 `[组件 @ 地址]` 或者
    /// 一个大写词开头，两边都能分开。判错的代价也小：丢的是一条进度，
    /// 不是一句「为什么没画面」。
    /// <para>
    /// 公开是为了能被测（与 <see cref="BuildArguments"/> 同一条理由）。
    /// </para>
    /// </remarks>
    public static (string Key, string Value)? ParseProgressLine(string line)
    {
        var cut = line.IndexOf('=');

        if (cut <= 0) return null;

        for (var i = 0; i < cut; i++)
        {
            var ch = line[i];

            // 键是 `[a-z0-9_]`（`stream_0_0_q` 也算），别的都不算。
            var isKeyChar = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_';

            if (!isKeyChar) return null;
        }

        return (line[..cut], line[(cut + 1)..]);
    }

    /// <summary>收下 `-progress` 的一个键值；到点了就留一条。</summary>
    private void ReportProgress(string key, string value)
    {
        if (!_progress.Take(key, value)) return;

        _logger.Log(
            LogLevel.Info, "多画面",
            $"这一格：ffmpeg 自报 {_progress.Fps} fps（累计出 {_progress.Frames} 帧、"
            + $"复制 {_progress.Dup}、丢 {_progress.Drop}），"
            + $"我们收到 {ReceivedCount} 帧、界面丢了 {DroppedCount}");
    }

    private async Task ReadFramesAsync(
        Process process,
        Task drainErrors,
        int width,
        int height,
        long startedAt,
        CancellationToken cancellationToken)
    {
        var frameSize = width * height * 3;
        var buffer = new byte[frameSize];
        var filled = 0;
        var first = true;

        try
        {
            var stream = process.StandardOutput.BaseStream;

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled, frameSize - filled), cancellationToken);
                if (read <= 0) break;

                filled += read;

                if (filled == frameSize)
                {
                    var frame = new LiveFrame((byte[])buffer.Clone(), width, height, Environment.TickCount64);

                    lock (_gate)
                    {
                        // ⚠️ **只留最新那一帧，旧的直接丢。** 界面按自己的节拍来取；
                        // 排队的话延迟会越积越大 —— 而实时画面宁可掉帧也不能滞后。
                        //
                        // ⚠️ 这两下数的是**两件事**，别合并（T11）：
                        //    `_received` = **网络那头给了多少**（这一路健不健康）
                        //    `_dropped`  = **界面这一头没跟上多少**（我们自己的事）
                        // 合起来看才分得出「手机上没编出来」与「这台电脑画不过来」。
                        _received++;
                        if (_latest is not null) _dropped++;
                        _latest = frame;
                        _hadFrame = true;
                    }

                    // ⚠️ 「出画面了」也留一条（§6.1：有生命周期的组件，起/停/失败各一条）
                    // —— 而且**带上等了多久**：中途接入要等下一个关键帧，
                    // 「等了 0.2 秒」和「等了 6 秒」是两种完全不同的毛病，
                    // 而这句话是当场唯一能分出它们的东西（只记一次）。
                    if (first)
                    {
                        first = false;

                        var waited = (Environment.TickCount64 - startedAt) / 1000.0;

                        _logger.Log(
                            LogLevel.Info, "多画面",
                            $"这一格出画面了（{waited:0.0} 秒）：{width}x{height}");
                    }

                    filled = 0;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 进程被收掉了 —— 正常路径。
        }

        await drainErrors.ConfigureAwait(false);

        // ⚠️ 「ffmpeg 说过话就记，空的时候不记」（§6.1）。
        // 读循环正常跑完（上游收尾）不需要说话；**异常退出**时才把它的原话留下 ——
        // 不然这一格黑着，而原因只躺在进程的 stderr 里没人看得到（I3）。
        var tail = _errors.ToString().Trim();

        if (!process.HasExited)
        {
            _logger.Log(LogLevel.Warn, "多画面", "这一格断了（流结束或读不动了）");
        }
        else if (process.ExitCode != 0 || tail.Length > 0)
        {
            _logger.Log(
                LogLevel.Warn, "多画面",
                $"这一格的 ffmpeg 退了（code {process.ExitCode}）：{(tail.Length > 0 ? tail : "没说为什么")}");
        }

        // ⚠️ 「自己结束了」——`_stopped` 是**收**那个动作打的标记，所以这里一判就把
        // 「我们主动换档/关窗」和「它自己断了」分开了（后者才该接回来）。
        if (Volatile.Read(ref _stopped) == 0)
        {
            try
            {
                _onEnded?.Invoke(this);
            }
            catch (Exception ex)
            {
                // 回调是外面给的，它抛了不该把读循环这条线程弄没了 —— 但也不许静默。
                _logger.Log(LogLevel.Warn, "多画面", $"重连那一步自己出错了：{ex.Message}");
            }
        }
    }

    /// <summary>停掉（幂等）。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;

        try
        {
            if (!_process.HasExited)
            {
                // 先好好说 —— ffmpeg 收到 q 会收尾退出。
                try
                {
                    await _process.StandardInput.WriteLineAsync("q");
                    await _process.StandardInput.FlushAsync();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // 已经死了 —— 下面那一步会收掉。
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    // 超时就强杀：这一格不值得为它把窗口关不掉。
                    try { _process.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
                }
            }
        }
        finally
        {
            try { await _readLoop; } catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            _process.Dispose();

            // ⚠️ 「停」也要留一条（§6.1）。只在**真停过**的时候记 ——
            // 这个方法是幂等的，重复调用不该重复留痕。
            _logger.Log(
                LogLevel.Info, "多画面",
                $"这一格收了（丢过 {DroppedCount} 帧）");
        }
    }

    /// <summary>
    /// 攒 ffmpeg 自报的那几个数，每 <see cref="IntervalMs"/> 毫秒放一条出去（T16 取证）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>为什么要看 ffmpeg 自己那几个数</b>：这一格「卡不卡」，
    /// 我们这一头只有「收到多少帧」「界面丢了多少帧」两个数，
    /// 它们分不出「手机推得慢」与「ffmpeg 在按 <c>-r 12</c> 凑帧」——
    /// 而 <c>dup_frames</c> / <c>drop_frames</c> 正是那件事的两个计数器：
    /// 源是 15 fps 而输出钉死 12 fps 时，ffmpeg 要么复制、要么丢，
    /// 两边都会在画面上变成**有规律的顿挫**（2026-10-04 T16 的 A 形态）。
    /// </remarks>
    private sealed class ProgressWatch
    {
        /// <summary>多久放一条（生产上的那一个）。</summary>
        /// <remarks>
        /// 10 秒：ffmpeg 每秒来一段，这个节奏既够看清「哪一段时间不对」，
        /// 又不至于把日志淹掉（9 格 × 6 条/分）。窗口再短，量出来的也只是噪声。
        /// </remarks>
        public const long DefaultIntervalMs = 10_000;

        /// <summary>这一路实际用的间隔 —— <b>测试会把它调短</b>（见 <see cref="Start"/>）。</summary>
        private readonly long _intervalMs;

        private long _atMs;

        public ProgressWatch(long intervalMs) => _intervalMs = intervalMs;

        /// <summary>ffmpeg 自报的输出帧率。</summary>
        public string Fps { get; private set; } = "?";

        /// <summary>累计编出来的帧数。</summary>
        public string Frames { get; private set; } = "?";

        /// <summary>累计**复制**的帧数（凑 CFR 补出来的）。</summary>
        public string Dup { get; private set; } = "?";

        /// <summary>累计**丢掉**的帧数（源比输出快，多余的不要了）。</summary>
        public string Drop { get; private set; } = "?";

        /// <summary>
        /// 吃进一个键值。返回 <see langword="true"/> = 该放一条了。
        /// </summary>
        /// <remarks>
        /// ⚠️ 到点那一刻取的是**上一块的数**：`progress` 这个键写在每一段的末尾，
        /// 而上面几个键在它前面 —— 顺序对了，取到的才是完整的一段。
        /// </remarks>
        public bool Take(string key, string value)
        {
            switch (key)
            {
                case "fps": Fps = value; return false;
                case "frame": Frames = value; return false;
                case "dup_frames": Dup = value; return false;
                case "drop_frames": Drop = value; return false;
                case "progress": break;
                default: return false;
            }

            var now = Environment.TickCount64;

            // 第一段只记底数、不报 —— 那一段的 `fps` 常常还是 0.00
            //（ffmpeg 要等它自己算得出来），报出去就是一句假话。
            if (_atMs == 0)
            {
                _atMs = now;
                return false;
            }

            if (now - _atMs < _intervalMs) return false;

            _atMs = now;
            return true;
        }
    }
}
