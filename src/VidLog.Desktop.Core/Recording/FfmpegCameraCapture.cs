using System.Diagnostics;

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

    public FfmpegCameraCapture(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
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

        foreach (var argument in BuildArguments(device, outputPath, encoder))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();

        // stdout 没人读的话管道会满、进而把 ffmpeg 堵死。内容不需要，但必须排空。
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<ICaptureProcess>(new FfmpegCaptureProcess(process));
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
    public static IReadOnlyList<string> BuildArguments(string device, string outputPath, string encoder) =>
    [
        "-hide_banner",
        "-v", "error",
        "-f", "dshow",
        "-rtbufsize", BufferSize,
        "-i", $"video={device}",
        "-c:v", encoder,
        // 摄像头出的是 yuyv422，H.264 要 4:2:0。让 ffmpeg 显式转，
        // 而不是指望编码器自己接受 —— libx264 接受不了 yuyv422 会直接失败。
        "-pix_fmt", "yuv420p",
        "-f", "matroska",
        "-y", outputPath,
    ];
}
