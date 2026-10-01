using System.Diagnostics;
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

    private readonly object _gate = new();
    private LiveFrame? _latest;
    private long _dropped;
    private int _stopped;

    /// <summary>读帧那一趟。⚠️ 它有**一次赋值、一次读**，都在同一线程的
    /// 构造与 <see cref="DisposeAsync"/> 上，所以不加锁。</summary>
    private Task _readLoop = Task.CompletedTask;

    private LiveTileProcess(Process process, BoundedTextTail errors, IAppLogger logger)
    {
        _process = process;
        _errors = errors;
        _logger = logger;
    }

    /// <summary>最新那一帧。还没有就是 <see langword="null"/>（界面画「无信号输入」）。</summary>
    public LiveFrame? Latest()
    {
        lock (_gate)
        {
            return _latest;
        }
    }

    /// <summary>因为界面没跟上而丢掉的帧数（诊断用）。</summary>
    public long DroppedCount
    {
        get { lock (_gate) { return _dropped; } }
    }

    /// <summary>ffmpeg 说的最后一句话（起不来时用它给用户一个原因，I3）。</summary>
    public string ErrorTail => _errors.ToString();

    /// <summary>还在跑没有。</summary>
    public bool IsRunning => !_process.HasExited;

    /// <summary>
    /// 起一路。<paramref name="url"/> 形如 <c>http://192.168.1.9:PORT/live</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>起不来返回 <see langword="null"/>，不抛。</b>一格拉不起来不该让整个
    /// 多画面窗口开不了 —— 它自己那一格画「无信号输入」并写明原因就够了。
    /// </remarks>
    public static LiveTileProcess? Start(
        string ffmpegPath,
        string url,
        int width,
        int height,
        IAppLogger? logger = null)
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

        // stderr 必须排空：一次刷屏就能把它灌满、把进程顶住。
        var drainErrors = Task.Run(async () =>
        {
            var buffer = new char[4096];

            try
            {
                int read;
                while ((read = await process.StandardError.ReadAsync(buffer)) > 0)
                {
                    errors.Append(buffer, read);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // 进程收掉了 —— 正常路径。
            }
        });

        // ⚠️ 先建实例再起读循环，**而且只建一个**：读循环要挂在返回出去的那一个上。
        // （先前写成「建两个、循环挂在前一个上」，返回的那个永远读不到帧。）
        var tile = new LiveTileProcess(process, errors, log);
        tile._readLoop = Task.Run(
            () => tile.ReadFramesAsync(process, drainErrors, width, height, CancellationToken.None));

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
    /// ⚠️ <b>低延迟那几项是这条路的意义所在</b>：默认 ffmpeg 会攒够一段才出帧，
    /// 那在实时画面上就是「比现实慢几秒」—— 而看的人以为那就是现在。
    /// <c>nobuffer</c> / <c>low_delay</c> 让它来一帧出一帧。
    /// </remarks>
    internal static List<string> BuildArguments(string url, int width, int height)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;

        return
        [
            "-hide_banner",
            "-loglevel", "warning",

            // ⚠️ 读超时（微秒）：手机相机没开时那条 HTTP 连接会一直挂着，
            // 没有它这一格的 ffmpeg 就永远等下去。
            "-rw_timeout", "5000000",

            "-fflags", "nobuffer",
            "-flags", "low_delay",

            // ⚠️ 裸流必须显式指定，别让 ffmpeg 去探测（它探不出来）。
            "-f", "h264",
            "-i", url,

            "-an",
            "-map", "0:v:0",

            // 按比例缩放、四周补黑：格子尺寸是固定的，而各路手机的宽高比可能不同。
            "-vf", $"scale={width}:{height}:force_original_aspect_ratio=decrease,"
                + $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2",

            "-r", Fps.ToString(invariant),
            "-f", "rawvideo",
            "-pix_fmt", "rgb24",
            "pipe:1",
        ];
    }

    private async Task ReadFramesAsync(
        Process process, Task drainErrors, int width, int height, CancellationToken cancellationToken)
    {
        var frameSize = width * height * 3;
        var buffer = new byte[frameSize];
        var filled = 0;

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
                        if (_latest is not null) _dropped++;
                        _latest = frame;
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
}
