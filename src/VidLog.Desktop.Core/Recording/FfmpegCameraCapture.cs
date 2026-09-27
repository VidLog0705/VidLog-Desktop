using System.Diagnostics;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 用 ffmpeg 从 DirectShow 设备采集。
/// </summary>
/// <remarks>
/// 选 ffmpeg 而不是 Media Foundation：本项目**已经**把 ffmpeg 当外部工具用
/// （<see cref="Media.FfmpegLocator"/>、remux、解码校验、编码探测），
/// 采集沿用它是零新依赖。走 MF 要引 Windows 互操作层，还得自己接编码器 ——
/// 而编码能力探测本来就以 ffmpeg 的编码器名为准。
/// </remarks>
public sealed class FfmpegCameraCapture : ICameraCapture
{
    /// <summary>
    /// 采集侧缓冲。
    /// </summary>
    /// <remarks>
    /// DirectShow 交帧是突发的，而 H.264 编码不是。缓冲给小了会掉帧
    /// （表现为画面卡顿但时间轴照走）。实测 256M 在 640x480@30 下不丢帧。
    /// </remarks>
    private const string BufferSize = "256M";

    private readonly string _ffmpegPath;

    /// <summary>采集进程 stderr 的尾部。</summary>
    /// <remarks>
    /// 留它不是为了记日志，是为了两件具体的事：① 判定 <c>device already in use</c>
    /// （换件/重开时要用）；② 采集失败时能把 ffmpeg 真正说的话报给用户，
    /// 而不是一句无话可说的「失败了」（I3）。
    /// </remarks>
    private readonly BoundedTextTail _errorTail = new();

    /// <summary>录制规格（编码已由 <paramref name="encoder"/> 带，这里管尺寸与帧率）。</summary>
    /// <remarks>
    /// 为 <see langword="null"/> 时**不带 <c>-video_size</c> / <c>-framerate</c>**，
    /// 相机用它自己的默认档 —— 那是改动前的行为，也是「启动时那次规格探测失败、
    /// 但总得录得起来」时的兜底。
    /// </remarks>
    private readonly RecordingSpec? _spec;

    public FfmpegCameraCapture(string ffmpegPath, RecordingSpec? spec = null)
    {
        _ffmpegPath = ffmpegPath;
        _spec = spec;
    }

    public Task<ICaptureProcess> StartAsync(
        string device,
        string outputPath,
        string encoder,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 承重：不重定向 stdin 就没法用 q 优雅停止（见 FfmpegCaptureProcess 的说明）。
            RedirectStandardInput = true,
        };

        // 水印字幕（规格 §3.6.2）：**按约定**从输出路径推同一个名字。
        // 文件不在时 ffmpeg 会报错起不来 —— 所以只在它真的存在时才带上，
        // 让「没有水印」比「录不起来」先发生（会话那边写失败也是这个口径）。
        var watermark = AssWatermark.PathFor(outputPath);
        var hasWatermark = File.Exists(watermark);

        foreach (var argument in BuildArguments(
            device, outputPath, encoder, _spec, hasWatermark ? watermark : null))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();

        // 两条管道都必须**无条件排空**，且**读端绝不接受可取消的读**。
        //
        // 这里踩过一个会丢录像的坑：原来写的是
        //     _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        // 一旦调用方传进来一个真会取消的 token，读端就停了 → 管道满 →
        // ffmpeg 写不进去 → 它连 stdin 上的 q 都处理不了 → StopAsync 超时 →
        // 只能强杀 → **MKV 尾部丢掉**。今天侥幸没出事，只是因为调用方一直传 None。
        // 停机只能走 q（FfmpegCaptureProcess 里那条唯一路径），不能靠取消读。
        _ = Task.Run(() => DrainAsync(process.StandardOutput, sink: null));

        // stderr 同样没人读的话，长时间录制里一次异常刷屏就能把它灌满，后果同上。
        // 但它有内容价值（`device already in use` 这类判定文本、诊断包素材），
        // 所以排进一个有上限的环形缓冲，而不是丢掉。
        _ = Task.Run(() => DrainAsync(process.StandardError, _errorTail));

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<ICaptureProcess>(
            new FfmpegCaptureProcess(process, () => _errorTail.ToString()));
    }

    /// <summary>把管道读干。读到流结束为止，异常吞掉（进程退了就是结束，不是错误）。</summary>
    private static async Task DrainAsync(StreamReader reader, BoundedTextTail? sink)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                sink?.Append(buffer, read);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 进程被收掉时管道会断。这不是错误，只是没有更多输出了。
        }
    }

    /// <summary>
    /// 拼出采集命令。
    /// </summary>
    /// <remarks>
    /// 单独抽出来是为了让「参数拼得对不对」能**不依赖摄像头**被断言到。
    /// 集成测试需要真设备，而参数写错时集成测试只会表现为「录不出来」，
    /// 说不清是设备的问题还是参数的问题。
    /// <para>
    /// <c>-y</c> 后面紧跟输出路径 —— 测试替身靠这个位置定位产物，
    /// 换顺序会让一批测试静默失效。
    /// </para>
    /// </remarks>
    /// <param name="spec">
    /// 录制规格（规格 §3.1.7）。为 <see langword="null"/> 时不带尺寸与帧率 —— 见那个字段的说明。
    /// </param>
    /// <param name="watermarkAssPath">
    /// 水印字幕文件的路径（规格 §3.6.2）。为 <see langword="null"/> 时不烧水印
    /// （那些不关心它的调用点与测试）。
    /// <para>
    /// ⚠️ <b>必须在采集这一次就烧进去</b>：收尾是 <c>-c copy</c> 的 remux，
    /// 那一步加不了滤镜（加了就得重编码，违反「不转码」）。
    /// </para>
    /// </param>
    public static IReadOnlyList<string> BuildArguments(
        string device, string outputPath, string encoder, RecordingSpec? spec = null,
        string? watermarkAssPath = null)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-v", "error",
            "-f", "dshow",
            "-rtbufsize", BufferSize,
        };

        if (spec is not null)
        {
            // ⚠️ 这两个都必须是**输入选项**（放在 `-i` 之前）：对 dshow 来说
            // `-video_size` 是「按这个模式打开设备」，写在 `-i` 后面会变成
            // 「把画面缩到这个尺寸」—— 前者打不开就报错（那是对的，探测要的就是这个），
            // 后者会悄悄缩放，于是**探测永远成功、而画质不是用户选的那一档**。
            arguments.Add("-video_size");
            arguments.Add(spec.FfmpegSize);
            arguments.Add("-framerate");
            arguments.Add(RecordingSpec.FrameRate.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.AddRange(["-i", $"video={device}"]);

        if (!string.IsNullOrWhiteSpace(watermarkAssPath))
        {
            // ⚠️ 滤镜是**输出选项**（放在 `-i` 之后、输出路径之前）。
            // 写成输入选项的话 ffmpeg 会把它当成对输入的处理，行为完全不同。
            arguments.Add("-vf");
            arguments.Add($"ass={AssWatermark.EscapeFilterPath(watermarkAssPath)}");
        }

        arguments.AddRange(
        [
            "-c:v", encoder,
            // 摄像头出的是 yuyv422，H.264 要 4:2:0。让 ffmpeg 显式转，
            // 而不是指望编码器自己接受 —— libx264 接受不了 yuyv422 会直接失败。
            "-pix_fmt", "yuv420p",
            "-f", "matroska",
            "-y", outputPath,
        ]);

        return arguments;
    }
}

/// <summary>
/// 只留最后 N 个字符的文本缓冲。线程安全。
/// </summary>
/// <remarks>
/// 用来排空子进程的 stderr：既要**读干**（不读就会把管道灌满、把进程堵死），
/// 又不能无限攒（长录制会吃光内存）。留尾部而不是头部，是因为出问题时
/// 有用的那几行通常就在最后。
/// </remarks>
public sealed class BoundedTextTail
{
    private const int Capacity = 16 * 1024;

    private readonly Lock _gate = new();
    private readonly System.Text.StringBuilder _tail = new();

    public void Append(char[] buffer, int count)
    {
        lock (_gate)
        {
            _tail.Append(buffer, 0, count);

            if (_tail.Length > Capacity)
            {
                _tail.Remove(0, _tail.Length - Capacity);
            }
        }
    }

    public override string ToString()
    {
        lock (_gate)
        {
            return _tail.ToString();
        }
    }
}
