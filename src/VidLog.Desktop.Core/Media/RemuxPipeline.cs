namespace VidLog.Desktop.Core.Media;

/// <summary>remux 的结果。</summary>
/// <param name="Succeeded">是否成功产出可用的 MP4。</param>
/// <param name="FailureReason">失败原因；成功时为 <see langword="null"/>。</param>
public sealed record RemuxResult(bool Succeeded, string? FailureReason);

/// <summary>
/// MKV 中间容器 → 无损 remux MP4。
/// </summary>
/// <remarks>
/// 规格 §3.1.4：产出必须是**广兼容、可独立播放**的成品。
/// <para>
/// <b>为什么录制期用 MKV 而不是直接写 MP4</b>：MP4 的索引（moov）在文件末尾，
/// 进程被杀时那段没写下去，整个文件就是彻底打不开的废件 —— 正好撞上规格 §8
/// 的「录制中掉电/进程被杀」和 I9 的「不产生无法播放的半成品」。
/// MKV 是分段结构，写到哪算哪，剩余的也能解出来。
/// 所以：**录制期 MKV，停下来之后 remux 成 MP4**。
/// </para>
/// <para>
/// remux 用 <c>-c copy</c>，不重编码 —— 画面零损失，也快（不涉及 GPU/CPU 编码）。
/// </para>
/// </remarks>
public sealed class RemuxPipeline
{
    private readonly string _ffmpegPath;
    private readonly IProcessRunner _runner;

    public RemuxPipeline(string ffmpegPath, IProcessRunner runner)
    {
        _ffmpegPath = ffmpegPath;
        _runner = runner;
    }

    /// <summary>
    /// 把 <paramref name="sourcePath"/> 无损转成 <paramref name="destinationPath"/>。
    /// </summary>
    /// <remarks>
    /// **失败时不得删除源文件。** MKV 是此刻唯一存在的副本 ——
    /// 删掉它就违反了 I2（任何录像出厂后任何时刻至少存在一份完整副本）。
    /// 本方法不负责清理源文件，那是上层的事。
    /// </remarks>
    public async Task<RemuxResult> RemuxAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            return new RemuxResult(false, $"源文件不存在：{sourcePath}");
        }

        var arguments = new[]
        {
            "-hide_banner",
            "-v", "error",
            "-i", sourcePath,
            "-c", "copy",              // 不重编码
            "-movflags", "+faststart", // moov 挪到文件头，便于边下边播
            "-y", destinationPath,
        };

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_ffmpegPath, arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            return new RemuxResult(false, $"调用 FFmpeg 失败：{ex.Message}");
        }

        if (!result.Succeeded)
        {
            return new RemuxResult(
                false,
                $"FFmpeg 退出码 {result.ExitCode}：{Truncate(result.StandardError)}");
        }

        if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length == 0)
        {
            return new RemuxResult(false, "remux 退出码为 0，但没有产出可用文件");
        }

        return new RemuxResult(true, null);
    }

    private static string Truncate(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
