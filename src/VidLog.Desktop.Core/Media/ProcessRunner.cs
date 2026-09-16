using System.Diagnostics;

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

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
}
