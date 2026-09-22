using System.Diagnostics;

namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 真的 ffmpeg 采集进程。
/// </summary>
/// <remarks>
/// 与 <see cref="Media.SystemProcessRunner"/> 的关键差别只有一处，但它是承重的：
/// <b>本类重定向 stdin</b>。ffmpeg 是少数把 stdin 当控制通道用的程序 ——
/// 收到 <c>q</c> 就会走正常退出路径（写 MKV 尾部、退出码 0）。
/// 不重定向 stdin 的话，除了杀进程树没有别的办法停下它，而那样会丢尾部。
/// </remarks>
public sealed class FfmpegCaptureProcess : ICaptureProcess
{
    private readonly Process _process;
    private readonly Func<string> _errors;

    /// <param name="errors">
    /// 取 stderr 尾部的回调。用来把 ffmpeg 真正说的话报给用户 ——
    /// 只说「采集失败」是 I3 不允许的静默失败。
    /// </param>
    internal FfmpegCaptureProcess(Process process, Func<string> errors)
    {
        _process = process;
        _errors = errors;
    }

    /// <summary>采集进程 stderr 的尾部（有上限）。</summary>
    public string ErrorTail => _errors();

    public async Task<int?> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (HasExited())
        {
            return TryGetExitCode();
        }

        try
        {
            // 实测：这一个字符就能让它干净收尾。
            await _process.StandardInput.WriteLineAsync("q");
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // stdin 已经不通了 —— 下面的等待会超时，然后走强杀。
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await _process.WaitForExitAsync(cts.Token);
            return TryGetExitCode();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 超时未退 —— 兜底强杀。此时尾部可能不完整，但那也好过留一个
            // 占着分片文件的孤儿进程（那会破坏「收尾只有一条路径」I9）。
            Kill();
            return null;
        }
    }

    private bool HasExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // 进程还没真正起来就被收掉了 —— 视同已退出。
            return true;
        }
    }

    private void Kill()
    {
        try
        {
            if (!HasExited())
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
        }
    }

    private int? TryGetExitCode()
    {
        try
        {
            return _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
