using VidLog.Desktop.Core.Recording;

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
/// <para>
/// ⚠️ <b>argv 走 <see cref="FfmpegCameraCapture.BuildArguments"/></b>，本类不自己拼 ——
/// 「探测的组合」与「录制的组合」必须是同一个东西，拼两份迟早走岔（2026-09-29 修过一次）。
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
            // ⚠️ **argv 走录制那一条路的构造器**，不在这里另拼一份。
            //
            // 2026-09-29 修掉的正是这条：这里原来自己拼了一份，把 `-video_size` 写在
            // `-i` **之后** —— 真 ffmpeg 实测那是输出侧选项，于是**被静默忽略**
            // （让它录 1280×720，产物 320×240），退出码 0、无任何报错。
            // 后果是探测**没在验它声称要验的东西**：用户选 4K 而相机不支持时，
            // 探测报「通过」，接着真录制（那边把 `-video_size` 放在 `-i` 之前）打不开设备、
            // 整段录不出来 —— 而拦住这种事正是 §3.1.7 要这个探测的全部理由。
            //
            // 共用构造器之后，「探测的组合」与「录制的组合」**从形状上就是同一个**，
            // 不可能再走岔。`durationSeconds: 1` 是唯一的差别（规格要求真录 1 秒）。
            var arguments = FfmpegCameraCapture.BuildArguments(
                device, probeFile, encoder, spec, durationSeconds: 1);

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

    /// <summary>
    /// 把一次选择的结果说成**一句人话**（界面直接用这个字符串）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 为什么要有这个方法：<see cref="SpecSelection.ChangedFromRequested"/>
    /// 一个标志盖了**两种情形**，而它们的说法不一样 ——
    /// </para>
    /// <list type="number">
    /// <item><b>真回落了</b>（探测挑中了别的组合）：说清「从哪落到哪」。</item>
    /// <item><b>一个组合都没实测通过</b>（上面最后一档回到默认档）：
    /// 这时 <c>Spec</c> 可能就是用户选的那个 —— 按情形 1 的句式会印出
    /// 「回落到了 H.264 1080P（你选的是 H.264 1080P）」，**一句自相矛盾的话**
    /// （2026-09-28 在电脑端概览页上实测到的）。</item>
    /// </list>
    /// <para>
    /// ⚠️ 情形 2 **不能因为「没变」就不说**：它是「探测全失败」这件事的唯一出口
    /// （I3：不存在静默失败）。所以两句话都带 <see cref="SpecSelection.Reason"/>。
    /// </para>
    /// <para>
    /// 放在 Core 而不是界面里，纯粹是为了**它能被测到** —— 界面那层（App）没有测试工程。
    /// </para>
    /// </remarks>
    public static string Describe(SpecSelection selection, RecordingSpec wanted) =>
        selection.Spec == wanted
            ? $"录制规格没能实测通过，仍然按 {selection.Spec.Label} 走。"
              + $"原因：{selection.Reason}"
            : $"录制规格回落到了 {selection.Spec.Label}（你选的是 {wanted.Label}）。"
              + $"原因：{selection.Reason}";
}
