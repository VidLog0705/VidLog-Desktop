using System.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Camera;

/// <summary>
/// 取景识码用的轻量采集进程：只出低频灰度裸帧，不写文件。
/// </summary>
/// <remarks>
/// <para>
/// 与录制的 <see cref="Recording.FfmpegCameraCapture"/> 分开：那个要写 MKV、
/// 要考虑编码器与收尾；这个只要「有没有码」，越便宜越好。
/// </para>
/// <para>
/// 帧走 <b>stdout</b>，所以这次不需要多输出 —— MKV 那条路不在这里。
/// stdout 的读循环**不接受可取消的读**（理由同采集：读端一停管道就满，
/// 会把 ffmpeg 顶住）。停机靠送 <c>q</c> 让进程退出，管道自然断。
/// </para>
/// </remarks>
public sealed class ScannerProcess
{
    /// <summary>取景的画面尺寸。</summary>
    /// <remarks>
    /// 640x480 是本机摄像头的能力上限（实测只有 3 档：160x120 / 320x240 / 640x480）。
    /// 取最大是因为小面单上的条码在小尺寸下读不出来。
    /// </remarks>
    public const int Width = 640;

    public const int Height = 480;

    /// <summary>
    /// 取帧频率。
    /// </summary>
    /// <remarks>
    /// 3 fps：识码不需要流畅画面，而每帧都要过一遍 ZXing（约 5~15ms）。
    /// 更高的频率只会在画面没变时白烧 CPU。
    /// </remarks>
    public const int Fps = 3;

    private readonly Process _process;
    private readonly RawGrayFrameReader _reader;
    private readonly Task _readLoop;
    private readonly BoundedTextTail _errors;
    private readonly Diagnostics.IAppLogger? _logger;

    private ScannerProcess(
        Process process, RawGrayFrameReader reader, Task readLoop,
        BoundedTextTail errors, Diagnostics.IAppLogger? logger)
    {
        _process = process;
        _reader = reader;
        _readLoop = readLoop;
        _errors = errors;
        _logger = logger;
    }

    /// <summary>读到过多少帧（诊断用）。</summary>
    public long FramesRead => _reader.FramesRead;

    public static Task<ScannerProcess> StartAsync(
        string ffmpegPath, CameraSource source, SingleSlotFrameSink sink,
        CameraRotation rotation = CameraRotation.None,
        Diagnostics.IAppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 承重：不重定向 stdin 就没法用 q 优雅停止（见 FfmpegCaptureProcess 的说明）。
            RedirectStandardInput = true,
        };

        foreach (var argument in BuildArguments(source, rotation))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();

        // stderr 必须排空，否则一次刷屏就能把管道灌满、把进程顶住。
        //
        // ⚠️ **而且内容要留下来**（2026-09-29 审计查出来的缺口）：原来这里是
        // `ReadToEndAsync()` 把结果**丢掉**，于是识码进程起来之后死掉
        // （`device in use`、地址打不开）时，`_process` 不动声色地跑完，
        // `CameraFrameScanner` 只看到「没有新帧」—— **日志与 `Failed` 事件都不响**。
        // 它的两个兄弟（`PreviewProcess` / `MicrophoneLevelMonitor`）都是留尾部的，
        // 只有它没有。
        var errors = new BoundedTextTail();
        _ = Task.Run(() => DrainAsync(process.StandardError, errors));

        var reader = new RawGrayFrameReader(process.StandardOutput.BaseStream, sink, Width, Height);
        var readLoop = Task.Run(reader.RunAsync);

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new ScannerProcess(process, reader, readLoop, errors, logger));
    }

    /// <summary>
    /// 取景识码的命令。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与录制那条路的差别：不编码、不写文件，直接出灰度裸帧。
    /// <b>几何必须钉住</b> —— 裸帧没有容器告诉读端宽高，
    /// 所以 <see cref="Width"/>×<see cref="Height"/> 是**读端**的硬前提。
    /// </para>
    /// <para>
    /// ⚠️ <b>网络摄像头那一档必须在输出侧缩放</b>：RTSP 没法要求对端按 640×480 发流
    /// （<c>-video_size</c> 对 rtsp 解复用器来说是不存在的选项），
    /// 而对端多半是 1080P/4K —— 不缩的话读端按 640×480 去切一路 1920×1080 的裸帧，
    /// 切出来的是**错位的花屏**，识码永远认不出来（而且不会有任何报错）。
    /// </para>
    /// </remarks>
    /// <param name="rotation">方向（规格 §3.1.7）。必须与录制那一档一致。</param>
    public static IReadOnlyList<string> BuildArguments(
        CameraSource source, CameraRotation rotation = CameraRotation.None)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
        };

        // 输入参数由源自己给：本机设备带 `-video_size 640x480 -framerate 30`，
        // 网络地址一个都不带（见 CameraSource.InputArguments）。
        arguments.AddRange(source.InputArguments("64M", $"{Width}x{Height}"));

        // ── 几何：缩到「转完之后正好是 640×480」的那个尺寸 ─────────────
        //
        // ⚠️ **转 90° 时采集侧要缩到 480×640**（宽高一反），这样 transpose 之后
        // 正好是读端硬要求的 640×480。
        // 反过来（先缩到 640×480 再转）会得到 480×640 —— 而读端**按 640×480 切**，
        // 切出来的是错位的花屏，识码永远认不出来，而且不报错。
        // 另一种写法「转完再缩回 640×480」会把画面**非等比拉伸**
        // （480×640 → 640×480），条码被横向拉扁 —— 这一种是「能不能解得出」的问题，
        // 比花屏好，但仍然没必要。
        var swapped = rotation is CameraRotation.Left90 or CameraRotation.Right90;
        var captureWidth = swapped ? Height : Width;
        var captureHeight = swapped ? Width : Height;

        var filters = new List<string>();

        // ⚠️ 本机设备**不转**时不必缩：`-video_size 640x480` 已经把采集尺寸钉住了。
        // 所以这里的条件是「网络源（对端不是 640×480）或者要转 90°」。
        if (source.IsNetwork || swapped)
        {
            filters.Add($"scale={captureWidth}:{captureHeight}");
        }

        // ⚠️ 方向滤镜的产出**只有一处**（`CameraRotationFilters.For`）——
        // 与录制那一档用的是同一个函数，所以两边朝向不可能不一致。
        // `fps`/`format` 排在它之后：那两步不改变几何。
        if (CameraRotationFilters.For(rotation) is { } rotationFilter)
        {
            filters.Add(rotationFilter);
        }

        filters.Add($"fps={Fps},format=gray");

        arguments.AddRange(
        [
            "-vf", string.Join(',', filters),
            "-pix_fmt", "gray",
            "-f", "rawvideo",
            "pipe:1",
        ]);

        return arguments;
    }

    /// <summary>
    /// 停下并**等到进程真的退出**。
    /// </summary>
    /// <remarks>
    /// 等它退干净是必须的：相机独占，没释放干净的话紧接着的录制进程
    /// 会拿到 <c>device already in use</c>。
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
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
            // stdin 已经不通了 —— 走下面的强杀。
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
            }
        }

        // 读循环会在管道断开后自己结束。
        await Task.WhenAny(_readLoop, Task.Delay(TimeSpan.FromSeconds(2)));

        // ⚠️ ffmpeg 说过话就记一条：识码进程起来之后死掉的原因
        // （`device in use`、地址打不开）**只在它那儿** —— 而它一死，
        // 外面只看到「没有新帧」，那与「画面里就是没有码」长得一模一样。
        // 空的时候不记（正常停下来没有话说）。
        if (_errors.ToString().Trim() is { Length: > 0 } said)
        {
            _logger?.Log(Diagnostics.LogLevel.Warn, "识码", $"取景进程说过：{said}");
        }

        _process.Dispose();
    }

    /// <summary>把管道读干，尾部留下（照 <see cref="PreviewProcess"/> 的兄弟做法）。</summary>
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
