namespace VidLog.Desktop.Core.Media;

/// <summary>一次规格探测的结果。</summary>
/// <param name="Spec">试的那个组合。</param>
/// <param name="EncoderName">试成功时用的编码器名；失败时为尝试过的第一个。</param>
/// <param name="Usable">能不能真的按这个组合录出可解码的成品。</param>
/// <param name="FailureReason">不可用时的原因（给诊断包与用户提示）。</param>
public sealed record SpecProbeResult(
    RecordingSpec Spec,
    string EncoderName,
    bool Usable,
    string? FailureReason);

/// <summary>录制规格的可用性检查（规格 §3.1.7）。</summary>
public interface IRecordingSpecProbe
{
    Task<SpecProbeResult> ProbeAsync(
        RecordingSpec spec,
        string device,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 规格 §3.1.7 要求的**真实**可用性检查：真开相机、真按这个组合录 1 秒、再真解一遍。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么不能用合成源（<c>testsrc</c>）代替</b>：合成源什么尺寸都收，
/// 而**相机不是** —— DirectShow 上按一个它不支持的 <c>-video_size</c> 打开会直接失败。
/// 规格原话：「组合是稀疏的……百十几种里能真跑通的远少于这个数，
/// 所以要**录制前做一次真实的可用性检查**，而不是假定"列出来了就能用"」。
/// </para>
/// <para>
/// 代价是每次探测要开一次相机、录 1 秒（独占：跑的时候别的取景进程必须停）。
/// 所以它只在**启动时**跑一次，结果管一次运行 —— 与规格「录制前可选、录制中不可改、
/// 改了下次开始工作才生效」是同一个口径。
/// </para>
/// </remarks>
public sealed class FfmpegSpecProbe : IRecordingSpecProbe
{
    private readonly string _ffmpegPath;
    private readonly IProcessRunner _runner;
    private readonly DecodeVerifier _verifier;

    public FfmpegSpecProbe(
        string ffmpegPath,
        IProcessRunner runner,
        DecodeVerifier? verifier = null)
    {
        _ffmpegPath = ffmpegPath;
        _runner = runner;
        _verifier = verifier ?? new DecodeVerifier(ffmpegPath, runner);
    }

    /// <summary>
    /// 试一个组合：**逐个编码器**试，第一个真出片的就算可用。
    /// </summary>
    /// <remarks>
    /// 编码器这一层与 <see cref="FfmpegEncoderProbe"/> 是两件事，别混：
    /// 那个验的是「这台机器能不能用这个编码器」（合成源），
    /// 这个验的是「**这台相机 + 这个尺寸 + 这个编码器**能不能一起跑」。
    /// 相机那一维只有真开它才知道。
    /// </remarks>
    public async Task<SpecProbeResult> ProbeAsync(
        RecordingSpec spec,
        string device,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(device))
        {
            return new SpecProbeResult(spec, spec.EncoderCandidates[0], false, "没有可用的摄像头");
        }

        string? firstFailure = null;
        var firstEncoder = spec.EncoderCandidates[0];

        foreach (var encoder in spec.EncoderCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (usable, reason) = await ProbeOneAsync(spec, device, encoder, cancellationToken);
            if (usable)
            {
                return new SpecProbeResult(spec, encoder, true, null);
            }

            firstFailure ??= reason;
        }

        return new SpecProbeResult(spec, firstEncoder, false, firstFailure);
    }

    private async Task<(bool Usable, string? Reason)> ProbeOneAsync(
        RecordingSpec spec,
        string device,
        string encoder,
        CancellationToken cancellationToken)
    {
        var probeFile = Path.Combine(
            Path.GetTempPath(), $"vidlog-spec-probe-{Guid.NewGuid():N}.mkv");

        try
        {
            var arguments = new[]
            {
                "-hide_banner",
                "-v", "error",
                "-f", "dshow",
                // 相机是独占的，探测时别抢太久。
                "-rtbufsize", "64M",
                "-i", $"video={device}",
                "-t", "1",
                "-video_size", spec.FfmpegSize,
                "-r", RecordingSpec.FrameRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-c:v", encoder,
                "-pix_fmt", "yuv420p",
                "-f", "matroska",
                "-y", probeFile,
            };

            ProcessResult result;
            try
            {
                result = await _runner.RunAsync(_ffmpegPath, arguments, cancellationToken);
            }
            catch (Exception ex)
            {
                return (false, $"调用 FFmpeg 失败：{ex.Message}");
            }

            if (!result.Succeeded)
            {
                return (false, $"{encoder} 打不开这个组合（退出码 {result.ExitCode}）：{FirstLine(result.StandardError)}");
            }

            if (!File.Exists(probeFile) || new FileInfo(probeFile).Length == 0)
            {
                // 退出码 0 却没有产物 —— 与编码器探测同一条规矩：**必须验产物**。
                return (false, $"{encoder} 退出码为 0 但没有产物");
            }

            var verification = await _verifier.VerifyAsync(probeFile, cancellationToken);
            return verification.IsPlayable
                ? (true, null)
                : (false, $"{encoder} 产出的文件解不开：{verification.FailureReason}");
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 探测残留删不掉不该让录制起不来 —— 交给系统临时目录清理。
        }
    }
}

/// <summary>从一组探测结果里挑出**实际要用**的那个规格。</summary>
/// <param name="Spec">实际要用的规格。全都不可用时是默认档。</param>
/// <param name="ChangedFromRequested">用户选的那个跑不通、已经回落过。</param>
/// <param name="Reason">回落的原因（要显示给用户）。</param>
public sealed record SpecSelection(
    RecordingSpec Spec,
    bool ChangedFromRequested,
    string? Reason);

/// <summary>选规格：按回落顺序试，取第一个真跑得通的。</summary>
/// <remarks>
/// ⚠️ 规格 §3.1.7：「**回落必须可见**……并**明确告诉用户实际用的是什么**。
/// **不得静默回落**」。所以这里返回的 <see cref="SpecSelection.ChangedFromRequested"/>
/// 与原因**必须**被界面说出来 —— 它是这一条的落点，不是可选的诊断信息。
/// </remarks>
public static class SpecSelectionPolicy
{
    public static async Task<SpecSelection> SelectAsync(
        RecordingSpec wanted,
        string device,
        IRecordingSpecProbe probe,
        CancellationToken cancellationToken = default)
    {
        string? firstReason = null;

        foreach (var candidate in RecordingSpec.FallbacksFrom(wanted))
        {
            var result = await probe.ProbeAsync(candidate, device, cancellationToken);
            if (result.Usable)
            {
                return new SpecSelection(
                    candidate,
                    ChangedFromRequested: candidate != wanted,
                    Reason: candidate == wanted ? null : firstReason);
            }

            firstReason ??= result.FailureReason;
        }

        // 一个都跑不通：**不假装**，回到默认档并说明原因 ——
        // 调用方会把它变成一条用户可见的警告（I3：不存在静默失败）。
        return new SpecSelection(
            RecordingSpec.Default,
            ChangedFromRequested: true,
            Reason: firstReason ?? "没有任何可用的录制规格组合");
    }
}
