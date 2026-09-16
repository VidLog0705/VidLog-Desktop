namespace VidLog.Desktop.Core.Media;

/// <summary>解码校验的结果。</summary>
public sealed record DecodeVerification(bool IsPlayable, string? FailureReason);

/// <summary>
/// 成品的**实际解码校验**。
/// </summary>
/// <remarks>
/// 规格 §3.1.4：停止录制后必须实测成品可播，**校验失败不得入库为「正常」**。
/// <para>
/// 注意这**不是**「文件存在 + 大小不为零」那种检查 —— 那种会漏掉
/// 「容器头写坏了、文件却有几百 MB」的半成品，而这正是规格 §8 点名要防的。
/// 这里真的让 FFmpeg 把每一帧解一遍。
/// </para>
/// </remarks>
public sealed class DecodeVerifier
{
    private readonly string _ffmpegPath;
    private readonly IProcessRunner _runner;

    public DecodeVerifier(string ffmpegPath, IProcessRunner runner)
    {
        _ffmpegPath = ffmpegPath;
        _runner = runner;
    }

    public async Task<DecodeVerification> VerifyAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return new DecodeVerification(false, $"文件不存在：{filePath}");
        }

        var arguments = new[]
        {
            "-hide_banner",
            "-v", "error",     // 只输出错误，便于把「有输出」当作失败信号
            "-i", filePath,
            "-f", "null",      // 解出来的帧直接丢掉，不写盘、不占空间
            "-",
        };

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_ffmpegPath, arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            return new DecodeVerification(false, $"调用 FFmpeg 失败：{ex.Message}");
        }

        if (!result.Succeeded)
        {
            return new DecodeVerification(
                false,
                $"FFmpeg 退出码 {result.ExitCode}：{Summarize(result.StandardError)}");
        }

        // 光看退出码不够：FFmpeg 遇到损坏的流常常**照样退 0**，只是往 stderr 里骂。
        // 用了 -v error，所以 stderr 非空就说明解码过程报过错。
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            return new DecodeVerification(false, $"解码报错：{Summarize(result.StandardError)}");
        }

        return new DecodeVerification(true, null);
    }

    private static string Summarize(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= 300)
        {
            return trimmed;
        }

        return trimmed[..300] + "…";
    }
}
