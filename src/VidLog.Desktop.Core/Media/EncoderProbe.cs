namespace VidLog.Desktop.Core.Media;

/// <summary>候选编码器，按优先级排列。</summary>
public static class EncoderCandidates
{
    /// <summary>
    /// 规格 §3.1.5：**优先级由实测结果决定**，硬件编码不可用时自动降级。
    /// </summary>
    /// <remarks>
    /// 顺序 = 画质/CPU 占用上的偏好，但真正决定用哪个的是<see cref="IEncoderProbe"/> 的实测结果 ——
    /// 这个列表只是「先试谁」。
    /// <para>
    /// <c>libx264</c> 放最后当兜底：它在任何 x86 机器上都该可用。
    /// 如果连它也探不通，那说明 FFmpeg 本身有问题，不是编码器的问题。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Default { get; } =
        ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"];
}

/// <summary>单个编码器的实测结果。</summary>
/// <param name="EncoderName">编码器名（FFmpeg 的 <c>-c:v</c> 参数值）。</param>
/// <param name="Usable">实测能否真的编出可解码的成品。</param>
/// <param name="FailureReason">不可用时的原因，用于诊断包。</param>
public sealed record EncoderProbeResult(string EncoderName, bool Usable, string? FailureReason);

/// <summary>编码能力探测。</summary>
public interface IEncoderProbe
{
    Task<IReadOnlyList<EncoderProbeResult>> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 规格 §3.1.5 要求的**实测**探测。
/// </summary>
/// <remarks>
/// <b>为什么不能只查 <c>ffmpeg -encoders</c></b>：那份列表是「编译进去了」，
/// 不是「这台机器能跑」。本机实测（2026-09-16）：列表里 8 个 H.264/HEVC 编码器，
/// 真能出片的只有 3 个 —— <c>h264_nvenc</c>（无 N 卡）、<c>h264_amf</c>（无 A 卡）、
/// <c>hevc_qsv</c>（HD 630 上 qsv 编不了 HEVC）全都一编就失败。
/// <para>
/// 所以这里是**真编一小段再解码验一遍**，而不是查表。
/// </para>
/// </remarks>
public sealed class FfmpegEncoderProbe : IEncoderProbe
{
    private readonly string _ffmpegPath;
    private readonly IProcessRunner _runner;
    private readonly DecodeVerifier _verifier;
    private readonly IReadOnlyList<string> _candidates;

    public FfmpegEncoderProbe(
        string ffmpegPath,
        IProcessRunner runner,
        DecodeVerifier? verifier = null,
        IReadOnlyList<string>? candidates = null)
    {
        _ffmpegPath = ffmpegPath;
        _runner = runner;
        _verifier = verifier ?? new DecodeVerifier(ffmpegPath, runner);
        _candidates = candidates ?? EncoderCandidates.Default;
    }

    public async Task<IReadOnlyList<EncoderProbeResult>> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<EncoderProbeResult>(_candidates.Count);

        foreach (var candidate in _candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ProbeOneAsync(candidate, cancellationToken));
        }

        return results;
    }

    private async Task<EncoderProbeResult> ProbeOneAsync(
        string encoder,
        CancellationToken cancellationToken)
    {
        var probeFile = Path.Combine(
            Path.GetTempPath(),
            $"vidlog-encoder-probe-{Guid.NewGuid():N}.mp4");

        try
        {
            // 合成源 1 秒 320x240 —— 足够逼出「驱动/硬件不支持」这类错误，
            // 又快到可以放在启动路径上跑。
            var arguments = new[]
            {
                "-hide_banner",
                "-v", "error",
                "-f", "lavfi",
                "-i", "testsrc=size=320x240:rate=15:duration=1",
                "-c:v", encoder,
                "-y", probeFile,
            };

            ProcessResult result;
            try
            {
                result = await _runner.RunAsync(_ffmpegPath, arguments, cancellationToken);
            }
            catch (Exception ex)
            {
                return new EncoderProbeResult(encoder, false, $"调用 FFmpeg 失败：{ex.Message}");
            }

            if (!result.Succeeded)
            {
                return new EncoderProbeResult(
                    encoder,
                    false,
                    $"编码失败（退出码 {result.ExitCode}）：{FirstLine(result.StandardError)}");
            }

            // 退出码为 0 也可能是空产物 —— 必须验。
            if (!File.Exists(probeFile))
            {
                return new EncoderProbeResult(encoder, false, "编码退出码为 0 但没有产物");
            }

            if (new FileInfo(probeFile).Length == 0)
            {
                return new EncoderProbeResult(encoder, false, "产物是 0 字节");
            }

            // 最后再真解一遍 —— 有些编码器会写出「能生成但解不开」的文件。
            var verification = await _verifier.VerifyAsync(probeFile, cancellationToken);
            if (!verification.IsPlayable)
            {
                return new EncoderProbeResult(encoder, false, verification.FailureReason);
            }

            return new EncoderProbeResult(encoder, true, null);
        }
        finally
        {
            TryDelete(probeFile);
        }
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var newline = trimmed.IndexOf('\n');
        return newline < 0 ? trimmed : trimmed[..newline];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 探测残留文件删不掉不该让录制起不来 —— 交给系统临时目录清理。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>从探测结果里挑出要用的编码器。</summary>
public static class EncoderSelection
{
    /// <summary>
    /// 按候选顺序取**第一个实测可用**的编码器。
    /// </summary>
    /// <returns>可用的编码器名；一个都不可用时返回 <see langword="null"/>。</returns>
    public static string? Select(IReadOnlyList<EncoderProbeResult> probeResults)
    {
        foreach (var result in probeResults)
        {
            if (result.Usable)
            {
                return result.EncoderName;
            }
        }

        return null;
    }
}
