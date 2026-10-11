using System.Globalization;
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
/// <param name="ObservedFrameRate">
/// **实测出来的**帧率；同 <paramref name="ObservedSize"/>，只有原生档会填。
/// 量不到时是 <see langword="null"/> —— 那只是少印一个数字，**不判失败**。
/// </param>
public sealed record SpecProbeResult(
    RecordingSpec Spec,
    string EncoderName,
    bool Usable,
    string? FailureReason,
    (int Width, int Height)? ObservedSize = null,
    double? ObservedFrameRate = null)
{
    /// <summary>
    /// 这次失败是**画面源本身打不开**，不是「这一档参数不合适」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>调用方看到它为 true 就该收工</b>：输入侧压根没打开，分辨率与编码器
    /// 都还没轮到上场，换哪一档录制规格得到的是**一模一样**的结果。
    /// </para>
    /// <para>
    /// ⚠️ 做成属性而不是第 7 个位置参数：成功那一路、以及所有既有构造点
    /// **一行都不用改**，而且默认 <see langword="false"/> 正好是保守的一侧
    /// （不知道 = 继续试，见 <see cref="CameraErrorKind"/>）。
    /// </para>
    /// </remarks>
    public bool SourceUnavailable { get; init; }
}

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
/// 所以它跑的时机只有两个，都在**录制开始之前**，与规格「录制前可选、录制中不可改、
/// 改了**下次开始工作**才生效」是同一个口径：
/// </para>
/// <list type="number">
/// <item><b>启动时一次</b>，结果管到用户下次改规格为止。</item>
/// <item><b>改过编码 / 分辨率之后的、每一次开段之前</b> —— 那一刻相机必然是空的
/// （取景刚被放掉、采集还没起），所以开这一次相机是安全的。
/// 落点在 `RecordingCoordinator.PrepareCaptureAsync`，装配见 `AppHost`。</item>
/// </list>
/// <para>
/// ⚠️ <b>改设置那一刻**不能**探</b>：那时候取景识码多半正占着相机，
/// 探测会拿到「设备被占用」并把它**误判成这个组合跑不通** ——
/// 把能用的说成不能用，比不探更坏。
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
        // ⚠️ 这两个都是**与录制规格无关**的「源根本没打开」—— 换哪一档、
        // 哪个编码器都是同一句话，所以带着 SourceUnavailable 回去让调用方直接收工
        // （见 SpecProbeResult.SourceUnavailable）。
        if (source.ConfigurationProblem is { } problem)
        {
            return new SpecProbeResult(spec, spec.EncoderCandidates[0], false, problem)
            {
                SourceUnavailable = true,
            };
        }

        if (source.IsEmpty)
        {
            // 本机设备那一档「没配」的走法（网络那一档上面已经拦掉了）。
            return new SpecProbeResult(spec, spec.EncoderCandidates[0], false, "没有可用的摄像头")
            {
                SourceUnavailable = true,
            };
        }

        string? firstFailure = null;
        var firstEncoder = spec.EncoderCandidates[0];

        foreach (var encoder in spec.EncoderCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (usable, reason, kind, observed, observedRate) =
                await ProbeOneAsync(spec, source, encoder, cancellationToken);
            if (usable)
            {
                return new SpecProbeResult(spec, encoder, true, null, observed, observedRate);
            }

            firstFailure ??= reason;

            // ⚠️ **源打不开就别再换编码器了。** 这一条与编码器无关（输入侧就没成），
            // 换下一个只是把同一句失败再等一遍 —— 网络那一档每个编码器还要
            // 各等满 10 秒的超时（见 DefaultNetworkTimeout）。
            if (kind == CameraErrorKind.SourceUnavailable)
            {
                return new SpecProbeResult(spec, firstEncoder, false, firstFailure)
                {
                    SourceUnavailable = true,
                };
            }
        }

        return new SpecProbeResult(spec, firstEncoder, false, firstFailure);
    }

    /// <summary>试一个编码器。第三个返回值是「卡在哪一段」，决定还要不要换下一个。</summary>
    private async Task<(bool Usable, string? Reason, CameraErrorKind Kind,
        (int Width, int Height)? Observed, double? FrameRate)>
        ProbeOneAsync(
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
                // 10 秒没能连上 = 源打不开（网络那一档不会自己结束，见 DefaultNetworkTimeout）。
                // ⚠️ 这句话里**不出现编码器名**：源打不开与换哪个编码器毫无关系，
                // 冠上 `h264_qsv` 只会把用户指向「换个编码器试试」。见下面那条同源的注释。
                return (false, $"在 {_networkTimeout.TotalSeconds:0} 秒内没能连上"
                    + "（地址可能不对，或者对端不通）", CameraErrorKind.SourceUnavailable, null, null);
            }
            catch (Exception ex)
            {
                // ⚠️ 这里**不**断言是源的问题：进程起不来（比如 ffmpeg 没了）与
                // 「这一路打不开」是两回事，而这种失败很快，多试一档不吃亏。
                return (false, $"调用 FFmpeg 失败：{ex.Message}", CameraErrorKind.Unknown, null, null);
            }

            if (!result.Succeeded)
            {
                // ⚠️ 理由走 `CameraErrorText`（中文），**不贴 ffmpeg 的英文原文** ——
                // 界面提示必须是中文（需求方 2026-09-29 写死）。原文进日志。
                // ★ 分类与话**认的是同一遍关键词**，见 `CameraErrorText.Explain`。
                var (kind, text) = CameraErrorText.Explain(result.StandardError);

                // ⚠️ 「源打不开」那句话里**不出现编码器名**：它换哪个编码器都是同一句
                // （输入侧压根没打开），冠上编码器就把用户指向「换个编码器试试」——
                // 而这句话是他在**启动时那一条警告**里唯一能看到的东西。
                // 其余几条（参数不合适那一条）冠上是对的：那确实是「这个组合」不行。
                return (false,
                    kind == CameraErrorKind.SourceUnavailable
                        ? text
                        : $"{encoder} 打不开这个组合：{text}",
                    kind, null, null);
            }

            if (!File.Exists(probeFile) || new FileInfo(probeFile).Length == 0)
            {
                // 退出码 0 却没有产物 —— 与编码器探测同一条规矩：**必须验产物**。
                // 这是编码器那一侧的事，与源无关。
                return (false, $"{encoder} 退出码为 0 但没有产物", CameraErrorKind.Unknown, null, null);
            }

            var verification = await _verifier.VerifyAsync(probeFile, cancellationToken);
            if (!verification.IsPlayable)
            {
                return (false, $"{encoder} 产出的文件解不开：{verification.FailureReason}",
                    CameraErrorKind.Unknown, null, null);
            }

            // ★ 原生档的尺寸与帧率**只能在这里问出来**（我们没钉，是相机自己出的）。
            // 设计图上那句「当前采用：640×480 @ 30 FPS」就是这两个值；
            // 拿不到的话界面只能说「相机原生档」，而索引里会缺一个真尺寸。
            ((int Width, int Height)? Size, double? FrameRate) observed = spec.NativeCaptureSize
                ? await MeasureObservedAsync(probeFile, cancellationToken)
                : (null, null);

            return (true, null, CameraErrorKind.Unknown, observed.Size, observed.FrameRate);
        }
        finally
        {
            TryDelete(probeFile);
        }
    }

    /// <summary>
    /// 「把录好的成品读回来，问它的尺寸与帧率」用的那段 argv。
    /// </summary>
    /// <remarks>
    /// <para>
    /// `public` 是为了**测试能拿同一份**：真机上那条「四档方向的宽高」用例
    /// 要**独立地**量一遍产物（用作交叉核对），而它自己拼一份的话，
    /// 两份一旦走岔，测的就不是生产真正用的那个读法了
    /// （与 <see cref="FfmpegCameraCapture.BuildArguments"/> 同一条理由）。
    /// </para>
    /// <para>
    /// ⚠️ **不能**用 `-v error`：编码、尺寸与帧率都是 ffmpeg 在 **info** 级别打的。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ReadBackArguments(string path) =>
    [
        "-hide_banner",
        "-v", "info",
        "-i", path,
        "-frames:v", "1",
        "-f", "null", "-",
    ];

    /// <summary>
    /// 从一个已经录好的文件里问出**它到底是什么尺寸、多少帧**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 解析走 <see cref="FfmpegNetworkCameraProbe.ParseStreams"/> —— 那是本仓**唯一一处**
    /// 解析 ffmpeg 流信息的代码。抄一份的话，ffmpeg 的输出格式一变就要改两处，
    /// 而漏掉的那一处会**静默**读不到尺寸（拿到的不是错误，是 null）。
    /// </para>
    /// <para>
    /// ⚠️ 这里**不能**用 `-v error`：编码、尺寸与帧率都是 ffmpeg 在 **info** 级别打出来的。
    /// </para>
    /// <para>
    /// ⚠️ 量不到**不让这次探测失败**：这个组合已经验过「真能录出可解码的成品」了，
    /// 缺的只是「界面上那句具体尺寸」。少一句说明，不该把一次成功的探测判成失败。
    /// 尺寸与帧率分别独立 —— **一个量到一个没量到**是正常结果（那一句就只印量到的那个）。
    /// </para>
    /// <para>
    /// ⚠️ 读的是**录回来的成品**，不是探测那一次 ffmpeg 的 stderr。理由是这条：
    /// 成品是**真落盘的东西**（尺寸与帧率是它自己带的），而 stderr 里那句是
    /// ffmpeg **打算**怎么开设备 —— 两者不一致时，该信的是成品。
    /// </para>
    /// </remarks>
    private async Task<((int Width, int Height)? Size, double? FrameRate)> MeasureObservedAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(
                _ffmpegPath, ReadBackArguments(path), cancellationToken);

            var streams = FfmpegNetworkCameraProbe.ParseStreams(result.StandardError);

            var size = streams is { Width: > 0, Height: > 0 }
                ? (streams.Width.Value, streams.Height.Value)
                : ((int Width, int Height)?)null;

            return (size, streams.FrameRate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消的（关窗 / 收尾）—— 那是取消，不是「量不到」。
            throw;
        }
        catch (Exception)
        {
            return (null, null);
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
/// <param name="NativeFallback">回落到相机原生档了（设计图步 4 的未完成态）。</param>
/// <param name="EncoderName">
/// **实测编出过片子的那个编码器名**（`-c:v` 要的值）。
/// </param>
/// <remarks>
/// ⚠️ <b>`EncoderName` 是这一路探测真正的产出物，别只把它当诊断信息。</b>
/// 它答的是「这台机器 + 这个相机 + 这个尺寸 + 这个编码，**用哪个编码器真编得出来**」——
/// 那正是录制要问的问题。2026-09-30 之前它没被带出来，于是录制那一侧
/// 只能自己另挑一次（`EncoderSelection` + 一份 **H.264-only** 的候选表），
/// 结果是「选 H.265 录出来是 H.264」而界面与索引都说 H.265。
/// 见 <c>docs/实现决策.md</c> §79。
/// <para>
/// 为 <see langword="null"/> 有两种情形，都要调用方自己兜底：
/// **一个组合都没探**（连 FFmpeg 都没有），或者**探过的全都没通过** ——
/// 那时没有任何一个编码器是被证明可用过的，随便挑一个都是在猜。
/// </para>
/// </remarks>
public sealed record SpecSelection(
    RecordingSpec Spec,
    bool ChangedFromRequested,
    string? Reason,
    bool NativeFallback = false,
    bool CapabilityFallback = false,
    string? EncoderName = null);

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
        ICameraCapabilities? capabilities = null,
        CancellationToken cancellationToken = default)
    {
        string? firstReason = null;

        // ── 先问相机「你能出哪些档」────────────────────────────────────────
        //
        // ⚠️ **拿不到表（空表）就完全走老路** —— 这一层是「只改进、不挡路」的：
        // ffmpeg 起不来、设备被别的进程占着、输出格式变了，都会落到空表，
        // 而空表在下面每一处的判断都是「不知道，那就不裁」。
        // 所以这个新功能**没有一条路径能变成新的故障**。
        var modes = capabilities is null
            ? []
            : await capabilities.ListAsync(source, cancellationToken);

        foreach (var candidate in RecordingSpec.FallbacksFrom(wanted))
        {
            // ⚠️ 表里**明确没有**这一档 ⇒ 连开都不开：那一次开相机是必败的，
            // 还要把设备独占一次（冷启动那几十秒大半就花在这种必败的尝试上）。
            if (modes.Count > 0 && !Capable(modes, candidate))
            {
                firstReason ??= $"这台相机不支持 {candidate.ResolutionLabel}"
                    + $"（{candidate.CaptureSize.Width}×{candidate.CaptureSize.Height}）；"
                    + $"它自报的档位是 {DescribeModes(modes)}。";
                continue;
            }

            var result = await probe.ProbeAsync(candidate, source, cancellationToken);
            if (result.Usable)
            {
                return new SpecSelection(
                    WithMeasured(candidate, result),
                    ChangedFromRequested: candidate != wanted,
                    Reason: candidate == wanted ? null : firstReason,
                    // ⚠️ 这个编码器**真编出过片子**（`ProbeOneAsync` 验过产物能解码）——
                    // 录制要用的就是它，不是另一份候选表挑出来的那个。见 §79。
                    EncoderName: result.EncoderName);
            }

            firstReason ??= result.FailureReason;

            // ⚠️ **源打不开就别再往下试了**，连原生档那一轮也不用试：
            // 原生档改的只是「尺寸钉不钉」，而问题出在**输入侧压根没打开**。
            // 2026-10-02 实测：一路连不上的网络地址，4 档 × 4 编码器 + 2 个原生档 × 4
            // 里的每一次都要等满 10 秒超时 —— 冷启动因此在**窗口还没画出来之前**
            // 耗掉 134 秒。收工之后只剩「一次超时」那么多。
            if (result.SourceUnavailable)
            {
                return GiveUp(firstReason);
            }
        }

        // ── ★ 从相机的档位表里挑一个**真实存在**的档（先于「不钉尺寸的原生档」）──
        //
        // 走到这里 = 三档一个都没通过。从前下一步只有「原生档」，而它是
        // 「相机自己出什么就是什么」—— 相机自报有 1600×1200，却可能只给 640×480。
        // 现在先按它自报的表挑一个离用户意图最近的档**钉住**；
        // 挑不出来（表是空的）或钉不住（真开一次失败）再落到原生档。
        //
        // ⚠️ 只要**离散档**（`IsExact`）：区间型那一行给的是两端，
        // 钉一个中间的尺寸等于钉一个**相机没自报过**的数 —— 那就不是「照它说的录」了。
        if (modes.Count > 0
            && DshowCapabilities.Nearest(
                modes, wanted.CaptureSize.Width, wanted.CaptureSize.Height, RecordingSpec.FrameRate)
                is { IsExact: true } picked)
        {
            var pinned = wanted with { CapabilitySize = (picked.MaxWidth, picked.MaxHeight) };
            var result = await probe.ProbeAsync(pinned, source, cancellationToken);
            if (result.Usable)
            {
                return new SpecSelection(
                    WithMeasured(pinned, result),
                    ChangedFromRequested: true,
                    Reason: firstReason,
                    CapabilityFallback: true,
                    EncoderName: result.EncoderName);
            }

            firstReason ??= result.FailureReason;

            if (result.SourceUnavailable)
            {
                return GiveUp(firstReason);
            }
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
                    NativeFallback: true,
                    EncoderName: result.EncoderName);
            }

            firstReason ??= result.FailureReason;

            if (result.SourceUnavailable)
            {
                return GiveUp(firstReason);
            }
        }

        return GiveUp(firstReason);
    }

    /// <summary>
    /// 一档都跑不通时那一条：**不假装**，回到默认档并说明原因 ——
    /// 调用方会把它变成一条用户可见的警告（I3：不存在静默失败）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 抽出来是因为它现在有**三个**出口：三档跑完、原生档跑完、
    /// 以及「源打不开时提前收工」。三处各写一份的话，改措辞时会漏掉一处 ——
    /// 而漏掉的那一处正是用户在特定机器上会看到的那一句。
    /// </remarks>
    private static SpecSelection GiveUp(string? firstReason) =>
        new(
            RecordingSpec.Default,
            ChangedFromRequested: true,
            Reason: firstReason ?? "没有任何可用的录制规格组合");

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
        // ⚠️ 这一档排在原生档**前面**：两者必不会同时为真（同一处只设一个），
        // 但读的人要先看到更具体的那个说法 —— 「按相机自报的档位录」比
        // 「按相机原生档录」多说了**一个尺寸**，信息更多。
        if (selection.CapabilityFallback)
        {
            var (width, height) = selection.Spec.CaptureSize;
            return $"你选的 {wanted.Label} 这台相机不支持（它自报的档位里没有这一档）—— "
                + $"已按它自报的 {width}×{height} 录。"
                + $"原因：{selection.Reason}";
        }

        if (selection.NativeFallback)
        {
            // 落点就是设计图步 4 那句未完成态：「已采用可用的原生配置（640×480 @ 30 FPS）」
            // 「该配置来自摄像头原生模式，仅作为安全兜底」。
            //
            // ⚠️ 只印**实测过的**那些：量不到尺寸时说「相机自己那一档」，
            // 量不到帧率时那句里连「@」都不出现（`ObservedDescription` 一处决定）。
            // 不印一个我们没测过的标称值（§13.1：只展示磁盘上真实可测的内容）。
            var size = selection.Spec.ObservedDescription ?? "相机自己那一档";

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

    /// <summary>把探测**实测**出来的尺寸与帧率贴到规格上（只有原生档会真的带值）。</summary>
    /// <remarks>
    /// <para>
    /// 单独一处是为了让「贴尺寸」这件事**不可能被漏掉**：漏掉的话
    /// 索引里会写一个标称的分辨率（见 <see cref="RecordingSpec.ObservedSize"/>）。
    /// </para>
    /// <para>
    /// ⚠️ 只量到尺寸也要贴（<c>frameRate: null</c>）：那一档下
    /// 「640×480」已经比「相机原生档」有用，不该因为帧率没量到就整块丢掉。
    /// </para>
    /// </remarks>
    private static RecordingSpec WithMeasured(RecordingSpec spec, SpecProbeResult result) =>
        result.ObservedSize is { } size
            ? spec.WithObservedSize(size.Width, size.Height, result.ObservedFrameRate)
            : spec;

    /// <summary>相机自报的表里有没有这一档（尺寸 + <see cref="RecordingSpec.FrameRate"/>）。</summary>
    private static bool Capable(IReadOnlyList<CameraMode> modes, RecordingSpec spec) =>
        DshowCapabilities.Supports(
            modes, spec.CaptureSize.Width, spec.CaptureSize.Height, RecordingSpec.FrameRate);

    /// <summary>
    /// 把相机自报的档位说成一句人话（去重、最多列 6 个、帧率按不变文化印）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 帧率**不能省**：这台相机之所以被判「不支持 1080P」，往往正是因为
    /// 它那个尺寸只有 15 fps —— 只说尺寸的话，用户会拿着一份
    /// 「1920×1080」的表来问「那为什么不用」。
    /// </remarks>
    private static string DescribeModes(IReadOnlyList<CameraMode> modes)
    {
        var all = modes
            .Select(mode => $"{mode.MaxWidth}×{mode.MaxHeight}@"
                + mode.MaxFrameRate.ToString("0.##", CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var listed = string.Join('、', all.Take(6));

        return all.Count > 6 ? $"{listed}…（共 {all.Count} 档）" : listed;
    }
}
