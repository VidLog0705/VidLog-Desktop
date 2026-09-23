using System.Diagnostics;

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

    private ScannerProcess(Process process, RawGrayFrameReader reader, Task readLoop)
    {
        _process = process;
        _reader = reader;
        _readLoop = readLoop;
    }

    /// <summary>读到过多少帧（诊断用）。</summary>
    public long FramesRead => _reader.FramesRead;

    public static Task<ScannerProcess> StartAsync(
        string ffmpegPath, string device, SingleSlotFrameSink sink,
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

        foreach (var argument in BuildArguments(device))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();

        // stderr 必须排空，否则一次刷屏就能把管道灌满、把进程顶住。
        // 内容有用（`device in use` 这类判定），所以留尾部而不是丢。
        _ = Task.Run(async () =>
        {
            try
            {
                await process.StandardError.ReadToEndAsync();
            }
            catch (Exception)
            {
                // 进程退了就是结束，不是错误。
            }
        });

        var reader = new RawGrayFrameReader(process.StandardOutput.BaseStream, sink, Width, Height);
        var readLoop = Task.Run(reader.RunAsync);

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new ScannerProcess(process, reader, readLoop));
    }

    /// <summary>
    /// 取景识码的命令。
    /// </summary>
    /// <remarks>
    /// 与录制那条路的差别：不编码、不写文件，直接出灰度裸帧。
    /// <b>几何必须钉住</b> —— 裸帧没有容器告诉读端宽高。
    /// </remarks>
    public static IReadOnlyList<string> BuildArguments(string device) =>
    [
        "-hide_banner",
        "-loglevel", "error",
        "-f", "dshow",
        "-rtbufsize", "64M",
        "-video_size", $"{Width}x{Height}",
        "-framerate", "30",
        "-i", $"video={device}",
        "-vf", $"fps={Fps},format=gray",
        "-pix_fmt", "gray",
        "-f", "rawvideo",
        "pipe:1",
    ];

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

        _process.Dispose();
    }
}
