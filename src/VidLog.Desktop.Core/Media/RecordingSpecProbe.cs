using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Media;

/// <summary>一次规格探测的结果。</summary>
/// <param name="Spec">试的那个组合。</param>
/// <param name="EncoderName">试成功时用的编码器名；失败时为尝试过的第一个。</param>
/// <param name="Usable">能不能真的按这个组合录出可解码的成品。</param>
/// <param name="FailureReason">不可用时的原因（给诊断包与用户提示）。</param>
/// <param name="ObservedSize">
/// **实测出来的**尺寸；只有原生档（<see cref="RecordingSpec.NativeCaptureSize"/>）
/// 会填它，其余档是我们钉的尺寸、本来就已知。
/// </param>
public sealed record SpecProbeResult(
    RecordingSpec Spec,
    string EncoderName,
    bool Usable,
    string? FailureReason,
    (int Width, int Height)? ObservedSize = null);

/// <summary>录制规格的可用性检查（规格 §3.1.7）。</summary>
public interface IRecordingSpecProbe
{
    Task<SpecProbeResult> ProbeAsync(
        RecordingSpec spec,
        CameraSource source,
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
    /// <summary>
    /// 网络摄像头那一档的尝试上限。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>不设这个上界就是「开机卡住」</b>：2026-09-29 实测，一个连不上的
    /// RTSP 地址会让 ffmpeg **静默挂住好几分钟**（>180 秒时 stderr 一个字都没有，
    /// 进程还活着）—— 而这条探测在**启动路径**上，`AppHost.StartAsync` 会一直等它。
    /// 本机设备那一档不需要它：dshow 打不开是**立刻**返回的。
    /// </remarks>
    public static TimeSpan DefaultNetworkTimeout { get; } = TimeSpan.FromSeconds(10);

    private readonly string _ffmpegPath;
    private readonly IProcessRunner _runner;
    private readonly DecodeVerifier _verifier;
    private readonly TimeSpan _networkTimeout;

    /// <param name="networkTimeout">
    /// 网络那一档的尝试上限；<see langword="null"/> = <see cref="DefaultNetworkTimeout"/>。
    /// 可注入是为了让「超时真的会生效」能被**验到** —— 否则验一次要等满 10 秒。
    /// </param>
    public FfmpegSpecProbe(
        string ffmpegPath,
        IProcessRunner runner,
        DecodeVerifier? verifier = null,
        TimeSpan? networkTimeout = null)
    {
        _ffmpegPath = ffmpegPath;
        _runner = runner;
        _verifier = verifier ?? new DecodeVerifier(ffmpegPath, runner);
        _networkTimeout = networkTimeout ?? DefaultNetworkTimeout;
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
        CameraSource source,
        CancellationToken cancellationToken = default)
    {
        // 配不了的那一档**不用真开一次**，直接说清楚：地址没填、或者少了 rtsp://
        // 这种，让 ffmpeg 去报 `Protocol not found` 对用户没有指向性 ——
        // 而 RTSP 连不上时还可能等很久（本机没有超时选项可用）。
        if (source.ConfigurationProblem is { } problem)
        {
            return new SpecProbeResult(spec, spec.EncoderCandidates[0], false, problem);
        }

        if (source.IsEmpty)
        {
            // 本机设备那一档「没配」的走法（网络那一档上面已经拦掉了）。
            return new SpecProbeResult(spec, spec.EncoderCandidates[0], false, "没有可用的摄像头");
        }

        string? firstFailure = null;
        var firstEncoder = spec.EncoderCandidates[0];

        foreach (var encoder in spec.EncoderCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (usable, reason, observed) = await ProbeOneAsync(spec, source, encoder, cancellationToken);
            if (usable)
            {
                return new SpecProbeResult(spec, encoder, true, null, observed);
            }

            firstFailure ??= reason;
        }

        return new SpecProbeResult(spec, firstEncoder, false, firstFailure);
    }

    private async Task<(bool Usable, string? Reason, (int Width, int Height)? Observed)> ProbeOneAsync(
        RecordingSpec spec,
        CameraSource source,
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
                source, probeFile, encoder, spec, durationSeconds: 1);

            // 网络那一档自己带上界（见 DefaultNetworkTimeout）。
            // ⚠️ 用链接令牌而不是「看时间够不够」：必须真把那个 ffmpeg **杀掉** ——
            // 它是网络读，不会自己超时，留着就是一根一直占着相机的线。
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (source.IsNetwork)
            {
                attempt.CancelAfter(_networkTimeout);
            }

            ProcessResult result;
            try
            {
                result = await _runner.RunAsync(_ffmpegPath, arguments, attempt.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 调用方取消的（关窗 / 收尾）—— 那是**取消**，不是「这个组合不可用」。
                // 吞掉它会把一次正常收尾变成一条假警告。
                throw;
            }
            catch (OperationCanceledException)
            {
                return (false, $"{encoder} 在 {_networkTimeout.TotalSeconds:0} 秒内没能连上"
                    + "（地址可能不对，或者对端不通）", null);
            }
            catch (Exception ex)
            {
                return (false, $"调用 FFmpeg 失败：{ex.Message}", null);
            }

            if (!result.Succeeded)
            {
                // ⚠️ 理由走 `CameraErrorText`（中文），**不贴 ffmpeg 的英文原文** ——
                // 界面提示必须是中文（需求方 2026-09-29 写死）。原文进日志。
                return (false, $"{encoder} 打不开这个组合：{CameraErrorText.Describe(result.StandardError)}", null);
            }

            if (!File.Exists(probeFile) || new FileInfo(probeFile).Length == 0)
            {
                // 退出码 0 却没有产物 —— 与编码器探测同一条规矩：**必须验产物**。
                return (false, $"{encoder} 退出码为 0 但没有产物", null);
            }

            var verification = await _verifier.VerifyAsync(probeFile, cancellationToken);
            if (!verification.IsPlayable)
            {
                return (false, $"{encoder} 产出的文件解不开：{verification.FailureReason}", null);
            }

            // ★ 原生档的尺寸**只能在这里问出来**（我们没钉，是相机自己出的）。
            // 设计图上那句「当前采用：640×480 @ 30 FPS」就是这个值；
            // 拿不到的话界面只能说「相机原生档」，而索引里会缺一个真尺寸。
            var observed = spec.NativeCaptureSize
                ? await MeasureSizeAsync(probeFile, cancellationToken)
                : null;

            return (true, null, observed);
        }
        finally
        {
            TryDelete(probeFile);
        }
    }

    /// <summary>
    /// 从一个已经录好的文件里问出**它到底是什么尺寸**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 解析走 <see cref="FfmpegNetworkCameraProbe.ParseStreams"/> —— 那是本仓**唯一一处**
    /// 解析 ffmpeg 流信息的代码。抄一份的话，ffmpeg 的输出格式一变就要改两处，
    /// 而漏掉的那一处会**静默**读不到尺寸（拿到的不是错误，是 null）。
    /// </para>
    /// <para>
    /// ⚠️ 这里**不能**用 `-v error`：编码与尺寸是 ffmpeg 在 **info** 级别打出来的。
    /// </para>
    /// <para>
    /// ⚠️ 量不到**不让这次探测失败**：这个组合已经验过「真能录出可解码的成品」了，
    /// 缺的只是「界面上那句具体尺寸」。少一句说明，不该把一次成功的探测判成失败。
    /// </para>
    /// </remarks>
    private async Task<(int Width, int Height)?> MeasureSizeAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(_ffmpegPath,
            [
                "-hide_banner",
                "-v", "info",
                "-i", path,
                "-frames:v", "1",
                "-f", "null", "-",
            ], cancellationToken);

            var streams = FfmpegNetworkCameraProbe.ParseStreams(result.StandardError);

            return streams is { Width: > 0, Height: > 0 }
                ? (streams.Width.Value, streams.Height.Value)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消的（关窗 / 收尾）—— 那是取消，不是「量不到」。
            throw;
        }
        catch (Exception)
        {
            return null;
        }
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
    string? Reason,
    bool NativeFallback = false);

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
        CameraSource source,
        IRecordingSpecProbe probe,
        CancellationToken cancellationToken = default)
    {
        string? firstReason = null;

        foreach (var candidate in RecordingSpec.FallbacksFrom(wanted))
        {
            var result = await probe.ProbeAsync(candidate, source, cancellationToken);
            if (result.Usable)
            {
                return new SpecSelection(
                    WithMeasured(candidate, result),
                    ChangedFromRequested: candidate != wanted,
                    Reason: candidate == wanted ? null : firstReason);
            }

            firstReason ??= result.FailureReason;
        }

        // ── ★ 最后一个兜底：**相机原生档**（设计图步 4 的未完成态）──────────
        //
        // ⚠️ 原来这里是直接回到 `RecordingSpec.Default`（H.264 1080P）——
        // 而三档都跑不通时，1080P **同样跑不通**：那不是兜底，那是一句
        // 「假装回落了」。真机实测（2026-09-30，一台只支持 640×480/320×240/160×120
        // 的相机）三档全都 `Could not set video options`，而回落到 1080P 之后
        // **录制根本起不来**。设计图上那一格写的正是这件事：
        // 「当前采用：640×480 @ 30 FPS」「该配置来自**摄像头原生模式**，仅作为安全兜底」。
        //
        // ⚠️ **它也要真开一次验**（与上面每一档同一条规矩：不假定）——
        // 而且只有开了才知道**尺寸到底是几**（不带 `-video_size` 时是相机自己出的）。
        var nativeCandidates = new[]
        {
            // 先保住用户选的编码（尺寸这堵墙拆掉之后，编码未必也跑不通）
            wanted with { NativeCaptureSize = true },
            // 再退到 H.264（与 `FallbacksFrom` 的尾巴同一个选择：兼容性最好的那一档）
            RecordingSpec.Default with { Rotation = wanted.Rotation, NativeCaptureSize = true },
        };

        foreach (var native in nativeCandidates.Distinct())
        {
            var result = await probe.ProbeAsync(native, source, cancellationToken);
            if (result.Usable)
            {
                return new SpecSelection(
                    WithMeasured(native, result),
                    ChangedFromRequested: true,
                    Reason: firstReason,
                    NativeFallback: true);
            }

            firstReason ??= result.FailureReason;
        }

        // 连原生档都跑不通：**不假装**，回到默认档并说明原因 ——
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
    public static string Describe(SpecSelection selection, RecordingSpec wanted)
    {
        if (selection.NativeFallback)
        {
            // 落点就是设计图步 4 那句未完成态：「已采用可用的原生配置（…）」
            // 「该配置来自摄像头原生模式，仅作为安全兜底」。
            //
            // ⚠️ 尺寸只印**实测过的**那一个：量不到时说「相机自己那一档」，
            // 不印一个我们没测过的标称值（§13.1：只展示磁盘上真实可测的内容）。
            //
            // ⚠️ **帧率不印数字** —— 图上写的是「@ 30 FPS」，而帧率我们没测过：
            // 原生档的帧率是相机自己定的。这一处与图上不同是**有意的**：
            // 宁可少一句，不写一句没测过的话。要用图上的措辞，得先真把帧率测出来。
            var size = selection.Spec.ObservedSize is { } observed
                ? $"{observed.Width}×{observed.Height}"
                : "相机自己那一档";

            return $"三档分辨率都没能在这台摄像头上跑通 —— 已采用可用的原生配置（{size}）。"
                + $"该配置来自摄像头原生模式，仅作为安全兜底。"
                + $"原因：{selection.Reason}";
        }

        return selection.Spec == wanted
            ? $"录制规格没能实测通过，仍然按 {selection.Spec.Label} 走。"
              + $"原因：{selection.Reason}"
            : $"录制规格回落到了 {selection.Spec.Label}（你选的是 {wanted.Label}）。"
              + $"原因：{selection.Reason}";
    }

    /// <summary>把探测**实测**出来的尺寸贴到规格上（只有原生档会真的带值）。</summary>
    /// <remarks>
    /// 单独一处是为了让「贴尺寸」这件事**不可能被漏掉**：漏掉的话
    /// 索引里会写一个标称的分辨率（见 <see cref="RecordingSpec.ObservedSize"/>）。
    /// </remarks>
    private static RecordingSpec WithMeasured(RecordingSpec spec, SpecProbeResult result) =>
        result.ObservedSize is { } size ? spec.WithObservedSize(size.Width, size.Height) : spec;
}
