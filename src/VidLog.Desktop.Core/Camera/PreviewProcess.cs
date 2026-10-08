using System.Diagnostics;
using System.Text;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Camera;

/// <summary>一帧预览画面（**RGB24**，紧凑，stride == width × 3）。</summary>
/// <remarks>
/// 与识码那一档的 <see cref="CameraFrame"/> 分开是**刻意的**：那个是**灰度**
/// （ZXing 的 <c>Gray8</c> 直接吃），这个是**彩色**（要给人看）。
/// 硬合成一个类型的话，要么让字段名撒谎（叫 <c>Gray</c> 却装 RGB），
/// 要么给它加一层泛型 —— 两者都比多一个 10 行的记录贵。
/// </remarks>
public sealed record PreviewFrame(byte[] Rgb, int Width, int Height, long CapturedAtMs)
{
    /// <summary>一行的字节数。RGB24 是 3 字节一个像素。</summary>
    public int Stride => Width * 3;

    /// <summary>
    /// 把一帧**灰度**画面当成预览帧用（每个亮度值抄三份）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 给「取景识码那一档的画面也显示出来」用（<c>PrerecordController</c>）：
    /// 它那条管子里本来就在流灰度帧，**顺手**投进预览槽 —— 不额外开一路输出、
    /// 不动那条已经验过的 argv。代价是那一档的预览是灰的（它是识别用的帧，
    /// 不是为了给人看而生的）。
    /// </para>
    /// <para>
    /// ⚠️ 之所以在这里转成 rgb24 而不是让界面认两种像素格式：
    /// <b>界面只该有一条渲染路径</b>。多一条 Gray8 分支就多一处「尺寸/格式对不上」
    /// 的静默错位，而那是花屏、不报错。转换代价在这一档可以忽略
    /// （640×480 @3 fps ≈ 2.7 MB/s 的内存写）。
    /// </para>
    /// </remarks>
    public static PreviewFrame FromGray(CameraFrame frame)
    {
        var gray = frame.Gray;
        var rgb = new byte[frame.Width * frame.Height * 3];

        for (int p = 0, i = 0; p < gray.Length && i + 2 < rgb.Length; p++, i += 3)
        {
            var value = gray[p];
            rgb[i] = value;
            rgb[i + 1] = value;
            rgb[i + 2] = value;
        }

        return new PreviewFrame(rgb, frame.Width, frame.Height, frame.CapturedAtMs);
    }
}

/// <summary>
/// 单槽预览缓冲：**只留最新一帧，旧的直接丢**。
/// </summary>
/// <remarks>
/// ⚠️ 理由与 <see cref="SingleSlotFrameSink"/> **逐字相同**，别改成有界队列：
/// 消费者（画到屏幕上）比生产者（管道读）慢时可接受的做法是丢帧，
/// 而**堵住管道会一路顶到 ffmpeg** —— 它连 stdin 上的 <c>q</c> 都处理不了，
/// 只能强杀。预览丢帧完全无所谓（人要的是「现在画面里是什么」）。
/// </remarks>
public sealed class SingleSlotPreviewSink
{
    private readonly Lock _gate = new();
    private PreviewFrame? _latest;
    private long _dropped;

    /// <summary>投一帧。永不阻塞。</summary>
    public void Publish(PreviewFrame frame)
    {
        lock (_gate)
        {
            if (_latest is not null)
            {
                _dropped++;
            }

            _latest = frame;
        }
    }

    /// <summary>取最新一帧并清空槽。没有新帧时返回 <see langword="null"/>。</summary>
    public PreviewFrame? TakeLatest()
    {
        lock (_gate)
        {
            var frame = _latest;
            _latest = null;
            return frame;
        }
    }

    /// <summary>因为消费者没跟上而丢掉的帧数（诊断用）。</summary>
    public long DroppedCount
    {
        get { lock (_gate) { return _dropped; } }
    }
}

/// <summary>
/// 取景预览：一个 ffmpeg 出一路**彩色**帧。
/// </summary>
/// <remarks>
/// <para>
/// 用在配置向导的第 2 步（选摄像头）与第 3 步（摄像头识别）——
/// 那两步要「看着画面把摄像头摆正 / 把面单放进框里」。
/// </para>
/// <para>
/// ⚠️ <b>它是「独起一个 ffmpeg」，所以只在相机没人占的时候用</b>：DirectShow 相机是
/// **独占**的（§25 实测），另有进程开着同一台相机时（录制中、取景识码中）
/// 它会拿到 <c>device already in use</c>。
/// 那两种情形下的画面走的是**别人进程**的第二路输出或顺带的那一帧 ——
/// <c>FfmpegCameraCapture</c>（第二路输出）与 <c>PrerecordController</c>（顺带灰度帧），
/// 两者都投进同一只 <see cref="SingleSlotPreviewSink"/>。
/// </para>
/// <para>
/// ⚠️ <b>一路彩色帧，不是两路</b>：第 3 步要「预览 + 同时识码」，
/// 而识码可以从**同一帧**上做（C# 侧降频取帧喂 ZXing）。
/// 实测过吞吐（`PreviewThroughputTests`：12.1 fps / 目标 12）——
/// 两路的话还得自己去对齐时间、还得给每一路各自限长（§54.4 那个坑）。
/// </para>
/// </remarks>
public sealed class PreviewProcess : IAsyncDisposable
{
    /// <summary>预览帧的宽高。</summary>
    /// <remarks>
    /// ⚠️ **固定 16:9**，源按比例缩放、四周补黑 —— 两条理由：
    /// ① 读端要**固定的帧字节数**（裸帧没有容器告诉它尺寸，而按源比例算高度的话
    /// 每台相机的值都不一样）；② 设计图上的预览区本来就是「中间画面 + 两侧黑边」。
    /// </remarks>
    public const int Width = 640;

    public const int Height = 360;

    /// <summary>
    /// 预览帧率。
    /// </summary>
    /// <remarks>
    /// 12 fps 是「够看」与「别白烧 CPU」之间的取舍：人眼对预览的流畅感大致从
    /// 10 fps 起，而这是**配摄像头时看一眼**用的，不是给人看片的。
    /// </remarks>
    public const int Fps = 12;

    private readonly Process _process;
    private readonly BoundedTextTail _errors;
    private readonly Task _readLoop;
    private readonly IAppLogger? _logger;

    /// <summary>0 = 还没停过，1 = 已经停过（见 <see cref="StopAsync"/> 的幂等说明）。</summary>
    private int _stopped;

    private PreviewProcess(
        Process process, BoundedTextTail errors, Task readLoop, IAppLogger? logger)
    {
        _process = process;
        _errors = errors;
        _readLoop = readLoop;
        _logger = logger;
    }

    /// <summary>ffmpeg 说的最后一句话（起不来时用它给用户一个原因，I3）。</summary>
    public string ErrorTail => _errors.ToString();

    /// <summary>起一个预览进程。</summary>
    /// <param name="rotation">
    /// 方向（规格 §3.1.7）。⚠️ 预览**也要转** —— 用户在这一步就是靠看着画面
    /// 把摄像头摆正的，预览不转的话他会把一个本来就正的摄像头摆歪。
    /// </param>
    public static Task<PreviewProcess> StartAsync(
        string ffmpegPath,
        CameraSource source,
        SingleSlotPreviewSink sink,
        CameraRotation rotation = CameraRotation.None,
        IAppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 承重：不重定向 stdin 就没法用 q 优雅停止。
            RedirectStandardInput = true,

            // ⚠️ 承重：ffmpeg 写的是 UTF-8，不声明就按控制台码页（中文 Windows = 936）读
            // —— 这一路留尾部就是为了「设备被占用」那类判定与界面提示。
            // stdout 是**裸帧**（走 BaseStream），与编码无关，所以只声明这一条。
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in BuildArguments(source, rotation))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();

        // stderr 必须排空：一次刷屏就能把它灌满、把进程顶住。
        // 内容有用（「设备被占用」这类判定文本），所以留尾部而不是丢。
        var errors = new BoundedTextTail();
        _ = Task.Run(() => DrainAsync(process.StandardError, errors));

        var readLoop = Task.Run(() => ReadAsync(process.StandardOutput.BaseStream, sink));

        cancellationToken.ThrowIfCancellationRequested();

        // ⚠️ `AGENTS.md` §6「异常必须留痕」。地址带上 **Identity**（凭据已抹掉）——
        // 网络摄像头那一路的 `Address` 里带着密码。
        logger?.Log(LogLevel.Info, "预览", $"开始预览（{source.Identity}）");

        return Task.FromResult(new PreviewProcess(process, errors, readLoop, logger));
    }

    /// <summary>预览的命令。单独抽出来是为了让参数能被断言。</summary>
    public static IReadOnlyList<string> BuildArguments(
        CameraSource source, CameraRotation rotation = CameraRotation.None)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
        };

        // ⚠️ 输入参数由源自己给（本机设备带 `-video_size`/`-framerate`，
        // 网络地址一个都不带）—— 与采集、识码两处同一条规矩。
        arguments.AddRange(source.InputArguments("64M"));

        arguments.AddRange(
        [
            "-map", "0:v:0",
            "-an",
            "-vf", string.Join(',', PreviewFilters(rotation)),
            "-r", Fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-f", "rawvideo",
            "-pix_fmt", "rgb24",
            "pipe:1",
        ]);

        return arguments;
    }

    /// <summary>
    /// 预览那一路的滤镜链：**转完方向再缩放补黑边**，产出恒为
    /// <see cref="Width"/>×<see cref="Height"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>顺序是承重的：方向排在最前。</b> 转 90° 会把宽高换过来，所以
    /// 「先缩到 640×360 再转」得到的是 360×640 —— 而读端**按定长切帧**
    /// （裸帧没有容器告诉它宽高），切出来的是错位的花屏，且不报任何错。
    /// 先转再缩就没有这个问题，而且**任何方向都落在同一个尺寸上**。
    /// </para>
    /// <para>
    /// ⚠️ 按比例缩放 + 补黑边，**不是**拉伸（`scale=640:360` 会把 4:3 的源拉变形，
    /// 而用户正靠这个画面判断摄像头摆正了没有）。
    /// </para>
    /// <para>
    /// ⚠️ 方向滤镜的产出**只有一处**（<see cref="CameraRotationFilters.For"/>）——
    /// 与录制、识码两处用的是同一个函数，所以各处朝向不可能不一致。
    /// </para>
    /// <para>
    /// ⚠️ <b>抽成公开的纯函数是因为它有第二个调用方</b>：采集进程的第二路输出
    /// （<c>FfmpegCameraCapture</c>，见 §62）必须产出**同一个形状**，
    /// 否则同一个界面控件会在两种状态下收到两种尺寸的帧。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> PreviewFilters(CameraRotation rotation)
    {
        var filters = new List<string>();

        if (CameraRotationFilters.For(rotation) is { } rotate)
        {
            filters.Add(rotate);
        }

        filters.Add($"scale={Width}:{Height}:force_original_aspect_ratio=decrease");
        filters.Add($"pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2");

        return filters;
    }

    /// <summary>
    /// 停下并**等到进程真的退出**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 等它退干净是必须的：相机是独占的，没释放干净的话紧接着的录制进程
    /// 会拿到 <c>device already in use</c>。与 <see cref="PrerecordProcess"/> 同一条理由。
    /// </para>
    /// <para>
    /// ⚠️ <b>幂等，而且是**认领式**的</b>（与 <c>RecordingSession.ReleaseCaptureAsync</c>
    /// 同一手法）：调用方很可能既显式停一次、又让 <c>await using</c> 收尾 ——
    /// 第二次碰到的是一个**已经 Dispose 的 <c>Process</c>**，而
    /// <c>WaitForExitAsync</c> 在它上面抛 <c>InvalidOperationException</c>。
    /// 2026-09-29 写测试时就是这么撞上的。
    /// </para>
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // 谁认领谁停。`Interlocked.Exchange` 而不是「先读后置」——
        // 后者会让两个调用方都通过判据（本仓在 ReleaseCaptureAsync 上踩过这条）。
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync("q");
                await _process.StandardInput.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // stdin 已经不通了 —— 走下面的等待（多半会超时，然后强杀）。
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 已经退了，竞态。
            }
        }

        // 读循环会在管道断开后自己结束。
        await Task.WhenAny(_readLoop, Task.Delay(TimeSpan.FromSeconds(2)));

        // ⚠️ ffmpeg 说过话就记一条（「设备被占用」「地址打不开」这类原因**只在它那儿**）。
        // 空的时候不记 —— 正常停下来的预览没有话说，记一条只会把日志灌满。
        if (ErrorTail.Trim() is { Length: > 0 } said)
        {
            _logger?.Log(LogLevel.Warn, "预览", $"预览进程说过：{said}");
        }

        _logger?.Log(LogLevel.Info, "预览", "预览已停，相机已释放");

        _process.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// 从管道里读 RGB24 帧，投进单槽。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>读循环不接受可取消的读</b>，理由与采集、识码两处完全一样：
    /// 读端一停管道就满，ffmpeg 被顶住，连 <c>q</c> 都处理不了。
    /// 停机靠**进程退出**（管道自然断），不是靠取消这个循环。
    /// </remarks>
    private static Task ReadAsync(Stream stream, SingleSlotPreviewSink sink)
        => ReadCoreAsync(stream, sink, Width, Height);

    /// <summary>
    /// 从**别人进程**的管道上读预览帧，投进单槽。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="ReadAsync"/> 是同一份读法，只是尺寸由调用方给 ——
    /// 采集进程的第二路输出（§62）与这里的关系是「同一个形状、不同的进程」。
    /// ⚠️ 这一路**必须及时读**：读端一停管道就满，而那条管子挂在**录制进程**上，
    /// 堵住的后果是 ffmpeg 连 <c>q</c> 都处理不了、只能强杀、MKV 尾部丢掉
    /// （§54.2 的 B 场景真机复现过）。所以这里与 <see cref="ReadAsync"/> 一样
    /// **不接受可取消的读**，且投帧走的是**永不阻塞**的单槽。
    /// </para>
    /// <para>
    /// 尺寸**由调用方给**而不是像 <see cref="ReadAsync"/> 那样用常量：
    /// 采集那一档的帧尺寸跟着方向走（转 90° 之后宽高是换过来的），
    /// 而滤镜链那边是「转完再缩」—— 两边算出来的必须是一回事。
    /// </para>
    /// </remarks>
    public static Task ReadFramesAsync(
        Stream stream, SingleSlotPreviewSink sink, int width, int height)
        => ReadCoreAsync(stream, sink, width, height);

    private static async Task ReadCoreAsync(
        Stream stream, SingleSlotPreviewSink sink, int width, int height)
    {
        var frameSize = width * height * 3;
        var buffer = new byte[frameSize];
        var filled = 0;

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled, frameSize - filled))
                    .ConfigureAwait(false);

                if (read <= 0)
                {
                    break;
                }

                filled += read;

                if (filled == frameSize)
                {
                    sink.Publish(new PreviewFrame(
                        (byte[])buffer.Clone(), width, height, Environment.TickCount64));

                    filled = 0;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static async Task DrainAsync(StreamReader reader, BoundedTextTail sink)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                sink.Append(buffer, read);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 进程退了就是结束，不是错误。
        }
    }
}
