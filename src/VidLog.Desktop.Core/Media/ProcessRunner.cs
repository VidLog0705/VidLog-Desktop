using System.Diagnostics;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Media;

/// <summary>外部进程的执行结果。</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// 跑外部进程（实际是 FFmpeg）。
/// </summary>
/// <remarks>
/// 抽成接口的理由：失败路径（编码器不存在、磁盘写满、进程被杀）用真 FFmpeg
/// 极难稳定复现，而这些恰恰是规格 §8 要求必须覆盖的场景。
/// 真实验证由集成测试打真 FFmpeg 完成。
/// </remarks>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>真实子进程实现。</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    private readonly IAppLogger _logger;

    /// <param name="logger">
    /// **这一处就覆盖了全部 FFmpeg 调用** —— remux、解码校验、编码器探测
    /// 都是包在这个接口上的几行（<c>RemuxPipeline</c> / <c>DecodeVerifier</c> /
    /// <c>FfmpegEncoderProbe</c>），所以不必给那三个类各铺一个 logger。
    /// </param>
    public SystemProcessRunner(IAppLogger? logger = null) =>
        _logger = logger ?? NullLogger.Instance;

    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // 必须**先**开始读两个流再等退出：管道缓冲区满了会互相死锁。
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        // 取消时要真的把进程杀干净 —— 否则取消一个长录制会留下孤儿 ffmpeg，
        // 而孤儿进程仍占着分片文件，正好破坏「收尾只有一条路径」(I9)。
        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // 进程已经退出了，竞态，忽略。
            }
        });

        await process.WaitForExitAsync(cancellationToken);

        var result = new ProcessResult(process.ExitCode, await stdout, await stderr);

        // ⚠️ **只在失败时记**：这三条链路每次收尾都要跑，成功也记的话
        // 日志会被正常流量淹掉，而淹掉的日志等于没有日志。
        //
        // 失败时记的是 ffmpeg **自己说的话**（stderr 尾巴）——
        // 那个只有它说得清（「编码器不存在」「文件头损坏」），
        // 我们从退出码上读不出来。
        if (!result.Succeeded)
        {
            _logger.Log(LogLevel.Warn, "外部进程", $"{System.IO.Path.GetFileName(executable)} 以 {result.ExitCode} 退出",
                new Dictionary<string, object?>
                {
                    // ⚠️ **逐条抹掉 URL 里的凭据再拼**：网络摄像头的地址
                    // （`rtsp://账号:密码@主机/流`）就在 argv 里，而落败的 RTSP 连接
                    // 正是最常走到这一行的情况 —— 不抹的话，用户改错一次密码，
                    // 密码就进了日志文件，而诊断包会把整个 logs/ 打包外发。
                    ["参数"] = string.Join(' ', arguments.Select(Diagnostics.UrlCredentials.Strip)),
                    ["stderr"] = Tail(result.StandardError),
                });
        }

        return result;
    }

    /// <summary>stderr 的尾巴。ffmpeg 开头几行多半是版本与编译选项，真话在后面。</summary>
    private static string Tail(string text, int lines = 8)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return all.Length <= lines ? text.Trim() : string.Join('\n', all[^lines..]).Trim();
    }
}
