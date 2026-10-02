using System.Globalization;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 录制规格（规格 §3.1.7）：编码 / 分辨率 / 帧率上限，以及**回落必须可见**。
/// </summary>
/// <remarks>
/// ⚠️ 本机没有摄像头，所以「真开相机试一次」那一段（<c>FfmpegSpecProbe</c>）
/// 只能靠**假 runner** 验：验的是「试了什么、按什么顺序试、失败了怎么记」，
/// **不是**「这台机器真能跑哪一档」。后者只有真机知道 —— 如实记在文档里。
/// </remarks>
public class RecordingSpecTests
{
    // ─────────────────────────────────────────────
    // 规格本身
    // ─────────────────────────────────────────────

    [Fact]
    public void 默认档是_H264_加_1080P()
    {
        // 规格 §3.1.7 的表格：默认 H.264 + 1080P。
        Assert.Equal(VideoCodec.H264, RecordingSpec.Default.Codec);
        Assert.Equal(VideoResolution.P1080, RecordingSpec.Default.Resolution);
    }

    [Fact]
    public void 界面上的编码名只有一种写法_而且没有_HEVC()
    {
        // 规格原话：「**界面上统一写「H.265」，任何地方都不得出现「HEVC」** ——
        // 两个名字混用会让用户以为是两种不同的编码」。
        foreach (var codec in Enum.GetValues<VideoCodec>())
        {
            var label = new RecordingSpec(codec, VideoResolution.P1080).CodecLabel;

            Assert.DoesNotContain("HEVC", label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("265", label.Replace("H.265", string.Empty, StringComparison.Ordinal));
        }

        Assert.Equal("H.265", new RecordingSpec(VideoCodec.H265, VideoResolution.P1080).CodecLabel);
        Assert.Equal("H.264", new RecordingSpec(VideoCodec.H264, VideoResolution.P1080).CodecLabel);
    }

    [Fact]
    public void 三档分辨率与帧率上限()
    {
        Assert.Equal((3840, 2160), new RecordingSpec(VideoCodec.H264, VideoResolution.Uhd4K).Size);
        Assert.Equal((1920, 1080), new RecordingSpec(VideoCodec.H264, VideoResolution.P1080).Size);
        Assert.Equal((1280, 720), new RecordingSpec(VideoCodec.H264, VideoResolution.P720).Size);

        // 「最高 30 帧、不提供选择」—— 所以它是个常量，不是配置项。
        Assert.Equal(30, RecordingSpec.FrameRate);
    }

    [Fact]
    public void 回落顺序_先保住用户选的编码()
    {
        var wanted = new RecordingSpec(VideoCodec.H265, VideoResolution.Uhd4K);
        var fallbacks = RecordingSpec.FallbacksFrom(wanted);

        // ① 先试用户选的那个
        Assert.Equal(wanted, fallbacks[0]);

        // ② 同编码降分辨率（画质差一点，但用户的编码偏好保住了）
        Assert.Equal(VideoCodec.H265, fallbacks[1].Codec);
        Assert.Equal(VideoResolution.P1080, fallbacks[1].Resolution);

        // ③ 最后才换回 H.264
        Assert.Contains(fallbacks, s => s.Codec == VideoCodec.H264);

        // 不重复
        Assert.Equal(fallbacks.Count, fallbacks.Distinct().Count());
    }

    [Fact]
    public void 用户选的就是默认档时_回落表不重复()
    {
        var fallbacks = RecordingSpec.FallbacksFrom(RecordingSpec.Default);

        Assert.Equal(RecordingSpec.Default, fallbacks[0]);
        Assert.Equal(fallbacks.Count, fallbacks.Distinct().Count());
    }

    // ─────────────────────────────────────────────
    // argv
    // ─────────────────────────────────────────────

    [Fact]
    public void 尺寸与帧率必须是输入选项_写在_i_前面()
    {
        var spec = new RecordingSpec(VideoCodec.H264, VideoResolution.P720);
        var arguments = FfmpegCameraCapture.BuildArguments(
            CameraSource.Local("Camera"), "out.mkv", "libx264", spec);

        var inputIndex = arguments.ToList().IndexOf("-i");
        var sizeIndex = arguments.ToList().IndexOf("-video_size");

        Assert.True(sizeIndex >= 0, "没有 -video_size");
        Assert.True(sizeIndex < inputIndex,
            "⚠️ -video_size 必须写在 -i 之前：对 dshow 来说它是「按这个模式打开设备」，" +
            "写在后面会变成「把画面缩到这个尺寸」—— 后者会悄悄缩放，" +
            "于是探测永远成功、而画质不是用户选的那一档");

        Assert.Equal("1280x720", arguments[arguments.ToList().IndexOf("-video_size") + 1]);
        Assert.Equal("30", arguments[arguments.ToList().IndexOf("-framerate") + 1]);

        // -y 后面紧跟产物路径 —— 测试替身靠这个位置定位产物。
        Assert.Equal("out.mkv", arguments[arguments.ToList().IndexOf("-y") + 1]);
    }

    [Fact]
    public void 不带规格时_argv_里没有尺寸与帧率()
    {
        // 改动前的行为：相机用自己的默认档。探测失败时的兜底就走这条路。
        var arguments = FfmpegCameraCapture.BuildArguments(CameraSource.Local("Camera"), "out.mkv", "libx264");

        Assert.DoesNotContain("-video_size", arguments);
        Assert.DoesNotContain("-framerate", arguments);
        Assert.Contains("-i", arguments);
    }

    // ─────────────────────────────────────────────
    // 可用性检查与回落
    // ─────────────────────────────────────────────

    // ─────────────────────────────────────────────
    // 方向（规格 §3.1.7 的 2026-09-29 需求变更：电脑端四档）
    // ─────────────────────────────────────────────

    [Fact]
    public void 转九十度会交换成片尺寸_采集尺寸不变()
    {
        // ⚠️ 这两个尺寸**必须分开**，用错的那一处不会报错：
        // 用错了只会「水印按错误的尺寸排版」（字跑到画面外）或
        // 「容量按错误的像素数估」—— 两种都是静默的。
        var upright = new RecordingSpec(VideoCodec.H264, VideoResolution.P1080, CameraRotation.None);
        var sideways = new RecordingSpec(VideoCodec.H264, VideoResolution.P1080, CameraRotation.Right90);

        // 采集侧永远是横的（相机只按它自己的模式出图）。
        Assert.Equal((1920, 1080), upright.CaptureSize);
        Assert.Equal((1920, 1080), sideways.CaptureSize);

        // 成片侧随方向。
        Assert.Equal((1920, 1080), upright.Size);
        Assert.Equal((1080, 1920), sideways.Size);

        // 180° 不换宽高。
        Assert.Equal(
            (1920, 1080),
            new RecordingSpec(VideoCodec.H264, VideoResolution.P1080, CameraRotation.UpsideDown).Size);
    }

    [Fact]
    public void 分辨率的ffmpeg参数用的是采集尺寸()
    {
        // ⚠️ 相机**不能**按「转完的尺寸」打开 —— 它只认自己的模式。
        // 这里用了成片尺寸的话，转 90° 时相机会打不开（或打开成另一个模式）。
        var spec = new RecordingSpec(VideoCodec.H264, VideoResolution.P1080, CameraRotation.Right90);

        Assert.Equal("1920x1080", spec.FfmpegSize);
    }

    [Fact]
    public void 回落表里每一档都保住用户选的方向()
    {
        // ⚠️ 与手机端同一条理由：方向是「摄像头装成什么样」，**不是设备能力** ——
        // 回落里换掉方向只会让用户莫名其妙拿到一段方向不对的录像，
        // 而方向错了的画面**可能整段都不能用**（不是画质差一点）。
        foreach (var candidate in RecordingSpec.FallbacksFrom(
            new RecordingSpec(VideoCodec.H265, VideoResolution.Uhd4K, CameraRotation.Left90)))
        {
            Assert.Equal(CameraRotation.Left90, candidate.Rotation);
        }
    }

    [Fact]
    public void 方向不参与回落的去重()
    {
        // 方向本来就每一档都一样，所以回落表里不该出现「同编码同分辨率的两档」。
        var fallbacks = RecordingSpec.FallbacksFrom(
            new RecordingSpec(VideoCodec.H264, VideoResolution.P1080, CameraRotation.UpsideDown));

        Assert.Equal(fallbacks.Count, fallbacks.Distinct().Count());
    }

    [Fact]
    public void 默认档那句话一个字都没变()
    {
        // ⚠️ 现有日志与测试都在断言「H.264 1080P」—— 方向是默认时不附上，
        // 否则所有旧断言与日志都会莫名其妙地多一截。
        Assert.Equal("H.264 1080P", RecordingSpec.Default.Label);

        // 非默认时才附上（否则用户在日志里看不出这一段是转过的）。
        Assert.Equal(
            "H.264 1080P 左转 90°",
            new RecordingSpec(VideoCodec.H264, VideoResolution.P1080, CameraRotation.Left90).Label);
    }

    [Fact]
    public void 四档方向的名字与滤镜都只有一处产出()
    {
        Assert.Null(CameraRotationFilters.For(CameraRotation.None));
        Assert.Equal("transpose=2", CameraRotationFilters.For(CameraRotation.Left90));
        Assert.Equal("transpose=1", CameraRotationFilters.For(CameraRotation.Right90));
        Assert.Equal("hflip,vflip", CameraRotationFilters.For(CameraRotation.UpsideDown));

        // 名字也各有一个（界面上写的就是这四个词）。
        Assert.Equal("不转", new RecordingSpec(VideoCodec.H264, VideoResolution.P1080).RotationLabel);
        Assert.Equal(
            "转 180°",
            new RecordingSpec(VideoCodec.H264, VideoResolution.P1080, CameraRotation.UpsideDown).RotationLabel);

        // 认不出的档位（手改坏的设置文件）当不转，**不抛**（I4 的同一条精神）。
        Assert.Null(CameraRotationFilters.For((CameraRotation)99));
    }


    [Fact]
    public async Task 探测跑的就是录制那一份argv_一个字都不差()
    {
        // ⚠️ 这条守的是 2026-09-29 修掉的那个 bug：探测原来**自己拼一份 argv**，
        // 把 `-video_size` 写在 `-i` **之后**。真 ffmpeg 实测那是输出侧选项，
        // 于是被**静默忽略**（让它录 1280×720，产物 320×240），退出码 0、无任何报错
        // ⇒ 探测**没在验它声称要验的东西**：用户选 4K 而相机不支持时，探测报「通过」，
        // 接着真录制打不开设备、整段录不出来 —— 而拦住这种事正是这个探测的全部理由。
        //
        // 判据取「两份 argv 逐字相等」而**不是**「-video_size 在 -i 前面」：
        // 后者只挡住已经发生过的那一种走岔，而这里要挡的是**这一类**。
        var runner = new ProbingRunner();
        var probe = new FfmpegSpecProbe("ffmpeg.exe", runner);
        var spec = new RecordingSpec(VideoCodec.H264, VideoResolution.P720);

        await probe.ProbeAsync(spec, CameraSource.Local("Camera"));

        Assert.NotEmpty(runner.Invocations);

        // 第 1 次是探测本身；后面几次是解码校验（它走同一个 runner）。
        var arguments = runner.Invocations[0];
        var probeFile = arguments[arguments.IndexOf("-y") + 1];

        var recording = FfmpegCameraCapture.BuildArguments(
            CameraSource.Local("Camera"), probeFile, spec.EncoderCandidates[0], spec, durationSeconds: 1);

        Assert.Equal(recording.ToArray(), arguments.ToArray());

        // 另外把两处最要紧的位置单独钉一下，失败时能一眼看出是哪一种走岔。
        var sizeIndex = arguments.IndexOf("-video_size");
        Assert.True(
            sizeIndex >= 0 && sizeIndex < arguments.IndexOf("-i"),
            "-video_size 必须在 -i 之前：写在后面 ffmpeg 会**静默忽略**它，探测就成了空转");

        Assert.Equal("1", arguments[arguments.IndexOf("-t") + 1]);
    }

    // ─────────────────────────────────────────────
    // ★ 源打不开时**一次就收工**（2026-10-02）
    // ─────────────────────────────────────────────

    /// <remarks>
    /// <para>
    /// 2026-10-02 实测：一路连不上的网络地址，让冷启动在**窗口还没画出来之前**
    /// 耗掉 134 秒 —— 4 档 × 4 编码器 + 2 个原生档 × 4，每一次都要等满
    /// <c>DefaultNetworkTimeout</c>（10 秒）。
    /// </para>
    /// <para>
    /// ⚠️ 判据取「ffmpeg 被起了几次」而**不是**「花了几秒」：
    /// 秒数在这台机器上取决于真实网络，而次数是那个 134 秒的**构成**本身。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 源打不开时_一次就收工_不把每个编码器再等一遍()
    {
        var runner = new DeadSourceRunner("rtsp://h:554/s: Connection refused");

        var result = await new FfmpegSpecProbe("ffmpeg.exe", runner)
            .ProbeAsync(RecordingSpec.Default, CameraSource.Network("rtsp://h/s"));

        Assert.False(result.Usable);
        Assert.True(result.SourceUnavailable, "源打不开必须报出来，调用方才知道该收工");
        Assert.Single(runner.Invocations);

        // ⚠️ 这句话是用户在**启动时那一条警告**里唯一能看到的东西 ——
        // 冠上编码器名会把他指向「换个编码器试试」，而源打不开换哪个都一样。
        Assert.DoesNotContain(
            RecordingSpec.Default.EncoderCandidates[0], result.FailureReason);
    }

    /// <remarks>
    /// ⚠️ 这一条是上面那条的**反面**，别当成重复删掉：收工的门只有一个
    /// （<see cref="CameraErrorKind.SourceUnavailable"/>）。把「这一档参数不合适」
    /// 也塞进那道门，会把 2026-09-30 那台只认 640×480 的相机**本该回落的那一档判死**。
    /// </remarks>
    [Fact]
    public async Task 设备不支持这一档参数时_每个编码器都还得试()
    {
        var spec = RecordingSpec.Default;
        var runner = new DeadSourceRunner("[dshow @ 0] Could not set video options");

        var result = await new FfmpegSpecProbe("ffmpeg.exe", runner)
            .ProbeAsync(spec, CameraSource.Local("Camera"));

        Assert.False(result.Usable);
        Assert.False(result.SourceUnavailable);
        Assert.Equal(spec.EncoderCandidates.Count, runner.Invocations.Count);
        Assert.Contains("不支持这一档参数", result.FailureReason);

        // ⚠️ 与上一条相反：**这一条冠上编码器名是对的** —— 它说的确实是
        // 「这个组合（含这个编码器）不行」，而这句话同样会进用户可见的警告。
        Assert.Contains(spec.EncoderCandidates[0], result.FailureReason);
    }

    /// <remarks>
    /// ⚠️ 提前收工的门在**两个**循环里各有一道（普通回落 / 原生档），
    /// 少一道就还是会把 10 秒再等好几遍 —— 所以这里数的是「试了几档」。
    /// </remarks>
    [Fact]
    public async Task 源打不开时_回落链一次都不试()
    {
        var probe = new DeadSourceProbe();

        var selection = await SpecSelectionPolicy.SelectAsync(
            new RecordingSpec(VideoCodec.H265, VideoResolution.Uhd4K),
            CameraSource.Network("rtsp://h/s"),
            probe);

        Assert.Single(probe.Tried);

        // I3：不静默 —— 收工也得把那句话说出去。
        Assert.True(selection.ChangedFromRequested);
        Assert.False(string.IsNullOrWhiteSpace(selection.Reason));
    }

    /// <summary>永远打不开源的假 runner：记下**被叫了几次**，这是本组唯一的判据。</summary>
    private sealed class DeadSourceRunner(string stderr) : IProcessRunner
    {
        public List<List<string>> Invocations { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(arguments.ToList());

            return Task.FromResult(new ProcessResult(1, string.Empty, stderr));
        }
    }

    /// <summary>一开就报「源打不开」的假探测。</summary>
    private sealed class DeadSourceProbe : IRecordingSpecProbe
    {
        public List<RecordingSpec> Tried { get; } = [];

        public Task<SpecProbeResult> ProbeAsync(
            RecordingSpec spec, CameraSource source, CancellationToken cancellationToken = default)
        {
            Tried.Add(spec);

            return Task.FromResult(
                new SpecProbeResult(spec, spec.EncoderCandidates[0], false, "连不上")
                {
                    SourceUnavailable = true,
                });
        }
    }

    /// <summary>只认某些组合的假探测。</summary>
    private sealed class FakeSpecProbe(params RecordingSpec[] usable) : IRecordingSpecProbe
    {
        public List<RecordingSpec> Tried { get; } = [];

        /// <summary>
        /// 探测成功时报出来的尺寸。
        /// </summary>
        /// <remarks>
        /// ⚠️ 只有**原生档**会拿到它 —— 其余档的尺寸是我们钉的，本来就已知
        /// （与生产里 <c>FfmpegSpecProbe</c> 的口径一致）。
        /// </remarks>
        public (int Width, int Height)? ObservedOnSuccess { get; init; }

        /// <summary>
        /// 探测成功时报出来的帧率。
        /// </summary>
        /// <remarks>
        /// ⚠️ 生产里它是**独立**量出来的（与尺寸同一次读回，但键不同）——
        /// 所以「尺寸量到了、帧率没量到」是正常结果，这里的两个属性也各自独立。
        /// </remarks>
        public double? ObservedFrameRateOnSuccess { get; init; }

        /// <summary>
        /// 探测成功时报出来的编码器名；不填就是这一档候选表里的第一个。
        /// </summary>
        /// <remarks>
        /// ⚠️ 真机上成功的那一个**未必是第一个**（`hevc_nvenc` 排在最前、却常常编不了），
        /// 所以它得能单独指定 —— 不然「带出来的是哪一个」这件事根本测不出来。
        /// </remarks>
        public string? EncoderOnSuccess { get; init; }

        public Task<SpecProbeResult> ProbeAsync(
            RecordingSpec spec, CameraSource source, CancellationToken cancellationToken = default)
        {
            Tried.Add(spec);

            return Task.FromResult(usable.Contains(spec)
                ? new SpecProbeResult(
                    spec, EncoderOnSuccess ?? spec.EncoderCandidates[0], true, null,
                    spec.NativeCaptureSize ? ObservedOnSuccess : null,
                    spec.NativeCaptureSize ? ObservedFrameRateOnSuccess : null)
                : new SpecProbeResult(spec, spec.EncoderCandidates[0], false, "这个组合打不开"));
        }
    }

    // ─────────────────────────────────────────────
    // ★ 编码器：探测的结论**就是**录制要用的那个（§79）
    // ─────────────────────────────────────────────

    /// <summary>
    /// **选规格这一路把「实测编出过片子的编码器」带出来了。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// 2026-09-30 真机实测：选 H.265，界面说「按 H.265 4K 录制」、索引里也记
    /// <c>"Codec":"H265"</c>，而**产物是 <c>h264 (High)</c>**。
    /// 根因是两份候选表：探测走 <see cref="RecordingSpec.EncoderCandidates"/>（认编码），
    /// 录制却走另一份**全是 H.264** 的表。对取证产品最坏的一点是
    /// **索引里的编码字段是假的**。
    /// </para>
    /// <para>
    /// 所以这条守的是：带出来的那个必须属于**实际录的那一档**的候选表。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 带出来的是实测通过那一档的编码器_不是用户选那一档的()
    {
        // 用户要 H.265 4K，只有 H.264 1080P 跑得通（真机上 hevc_* 常常全编不了）。
        var wanted = new RecordingSpec(VideoCodec.H265, VideoResolution.Uhd4K);
        var onlyH264 = new RecordingSpec(VideoCodec.H264, VideoResolution.P1080);

        var probe = new FakeSpecProbe(onlyH264) { EncoderOnSuccess = "libx264" };
        var selection = await SpecSelectionPolicy.SelectAsync(wanted, CameraSource.Local("Camera"), probe);

        Assert.Equal(onlyH264, selection.Spec);
        Assert.Equal("libx264", selection.EncoderName);

        // ⚠️ 必须是**它自己那一档**那一组里的名字。
        // 带回一个 hevc_* 的话，录制那边会拿着 H.265 的编码器去编 H.264 的规格。
        Assert.Contains(selection.EncoderName!, selection.Spec.EncoderCandidates);
        Assert.DoesNotContain(selection.EncoderName!, wanted.EncoderCandidates);
    }

    /// <summary>
    /// **一个组合都没探通时不给编码器**（<see langword="null"/>）——
    /// 那时没有任何一个编码器是被证明可用过的，随便挑一个都是在猜。
    /// </summary>
    [Fact]
    public async Task 一个组合都没探通时不硬给一个编码器()
    {
        var selection = await SpecSelectionPolicy.SelectAsync(
            new RecordingSpec(VideoCodec.H265, VideoResolution.Uhd4K),
            CameraSource.Local("Camera"),
            new FakeSpecProbe());   // 什么都不认

        Assert.Null(selection.EncoderName);
    }

    [Fact]
    public async Task 用户选的能跑通就不回落_也不报原因()
    {
        var wanted = new RecordingSpec(VideoCodec.H264, VideoResolution.P720);
        var selection = await SpecSelectionPolicy.SelectAsync(
            wanted, CameraSource.Local("Camera"),new FakeSpecProbe(wanted));

        Assert.Equal(wanted, selection.Spec);
        Assert.False(selection.ChangedFromRequested);
        Assert.Null(selection.Reason);
    }

    [Fact]
    public async Task 跑不通就回落_而且把原因带回来()
    {
        // 规格 §3.1.7：「**回落必须可见**……并**明确告诉用户实际用的是什么**。
        // **不得静默回落**」。所以这条不只看「落到了哪一档」，
        // 还要看「有没有带回一句话让界面说得出来」。
        var wanted = new RecordingSpec(VideoCodec.H265, VideoResolution.Uhd4K);
        var only720 = new RecordingSpec(VideoCodec.H265, VideoResolution.P720);

        var probe = new FakeSpecProbe(only720);
        var selection = await SpecSelectionPolicy.SelectAsync(wanted, CameraSource.Local("Camera"), probe);

        Assert.Equal(only720, selection.Spec);
        Assert.True(selection.ChangedFromRequested);
        Assert.False(string.IsNullOrWhiteSpace(selection.Reason));

        // 顺序：先试用户选的，再逐级退（不重复试）。
        Assert.Equal(wanted, probe.Tried[0]);
        Assert.Equal(probe.Tried.Count, probe.Tried.Distinct().Count());
    }

    [Fact]
    public async Task 一个组合都跑不通时_回默认档并给原因()
    {
        var selection = await SpecSelectionPolicy.SelectAsync(
            new RecordingSpec(VideoCodec.H265, VideoResolution.Uhd4K),
            CameraSource.Local("Camera"),
            new FakeSpecProbe());

        Assert.Equal(RecordingSpec.Default, selection.Spec);
        Assert.True(selection.ChangedFromRequested);
        Assert.False(string.IsNullOrWhiteSpace(selection.Reason));
    }

    /// <summary>
    /// **「回落到了 X（你选的是 X）」那句自相矛盾的话的绊线。**
    /// </summary>
    /// <remarks>
    /// 复现的是 2026-09-28 在电脑端概览页上实测到的那一条警告：本机没摄像头时
    /// 所有组合都探测失败，策略回到默认档 —— 而用户选的**恰好就是**默认档，
    /// 于是界面印出「录制规格回落到了 H.264 1080P（你选的是 H.264 1080P）」。
    /// </remarks>
    [Fact]
    public async Task 探测全失败但结果与用户选的一致时_不许说成回落()
    {
        // 用户选的就是默认档，而一个组合都跑不通（没摄像头）。
        var selection = await SpecSelectionPolicy.SelectAsync(
            RecordingSpec.Default, CameraSource.Local("Camera"), new FakeSpecProbe());

        Assert.Equal(RecordingSpec.Default, selection.Spec);

        var text = SpecSelectionPolicy.Describe(selection, RecordingSpec.Default);

        Assert.DoesNotContain("回落到了", text);
        // ⚠️ 但不能因为「没变」就闭嘴：探测全失败这件事**必须说出来**（I3）。
        Assert.Contains("没能实测通过", text);
        Assert.Contains(selection.Reason!, text);
    }

    [Fact]
    public async Task 真回落了就说清从哪落到哪()
    {
        var wanted = new RecordingSpec(VideoCodec.H265, VideoResolution.Uhd4K);
        var only720 = new RecordingSpec(VideoCodec.H265, VideoResolution.P720);
        var selection = await SpecSelectionPolicy.SelectAsync(
            wanted, CameraSource.Local("Camera"),new FakeSpecProbe(only720));

        var text = SpecSelectionPolicy.Describe(selection, wanted);

        Assert.Contains(only720.Label, text);
        Assert.Contains(wanted.Label, text);
        Assert.Contains("回落到了", text);
    }

    // ─────────────────────────────────────────────
    // ★ 相机原生档（设计图步 4 的未完成态）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 三档全跑不通时_回落到相机原生档_而不是假装回到某一档()
    {
        // 设计图步 4 未完成态原话：「已采用可用的**原生配置**（640×480 @ 30 FPS）」
        // 「该配置来自**摄像头原生模式**，仅作为安全兜底」。
        //
        // ⚠️ 改之前这里是回到 `RecordingSpec.Default`（H.264 1080P）——
        // 而三档都跑不通时 1080P **同样跑不通**，那不是兜底，是一句假话：
        // 2026-09-30 真机实测（一台只支持 640×480 的相机）回落之后录制根本起不来。
        var wanted = new RecordingSpec(VideoCodec.H265, VideoResolution.Uhd4K, CameraRotation.Right90);
        var native = RecordingSpec.Default with
        {
            Rotation = CameraRotation.Right90,
            NativeCaptureSize = true,
        };

        var probe = new FakeSpecProbe(native) { ObservedOnSuccess = (640, 480) };
        var selection = await SpecSelectionPolicy.SelectAsync(wanted, CameraSource.Local("Camera"), probe);

        Assert.True(selection.NativeFallback);
        Assert.True(selection.Spec.NativeCaptureSize);
        Assert.True(selection.ChangedFromRequested);

        // ★ 方向**不能丢**（与回落表同一条规矩：方向与编码能力无关，
        // 而方向错了的画面可能整段都不能用，不是「画质差一点」）。
        Assert.Equal(CameraRotation.Right90, selection.Spec.Rotation);

        // ★ 尺寸是**实测出来的那个**，不是标称的 1080P —— 它会进索引（证据元数据）。
        Assert.Equal((640, 480), selection.Spec.ObservedSize);
        Assert.Equal((480, 640), selection.Spec.Size);
    }

    [Fact]
    public async Task 原生档是最后才试的_前面每一档都试过了()
    {
        var wanted = new RecordingSpec(VideoCodec.H264, VideoResolution.P1080);
        var probe = new FakeSpecProbe();   // 什么都不认

        await SpecSelectionPolicy.SelectAsync(wanted, CameraSource.Local("Camera"), probe);

        var regular = RecordingSpec.FallbacksFrom(wanted).Count;
        var firstNative = probe.Tried.FindIndex(s => s.NativeCaptureSize);

        Assert.True(firstNative >= 0, "原生档必须被试过");
        Assert.Equal(regular, firstNative);
    }

    [Fact]
    public async Task 连原生档都跑不通时_才是那个一个组合都没通过()
    {
        var selection = await SpecSelectionPolicy.SelectAsync(
            new RecordingSpec(VideoCodec.H265, VideoResolution.Uhd4K),
            CameraSource.Local("Camera"),
            new FakeSpecProbe());

        Assert.False(selection.NativeFallback);
        Assert.Equal(RecordingSpec.Default, selection.Spec);
        Assert.False(string.IsNullOrWhiteSpace(selection.Reason));
    }

    [Fact]
    public async Task 原生档那句话量到帧率时_与设计图逐字一致()
    {
        // 需求方 2026-09-30 裁决：**补测帧率、照图印**。
        // 设计图步 4 未完成态的原话就是这一句，所以这里断言的是**逐字**相等
        // 的片段 —— 少一个空格、把「×」写成「x」、把「FPS」写成「fps」都要红。
        var wanted = new RecordingSpec(VideoCodec.H265, VideoResolution.Uhd4K);
        var native = RecordingSpec.Default with { NativeCaptureSize = true };
        var probe = new FakeSpecProbe(native)
        {
            ObservedOnSuccess = (640, 480),
            ObservedFrameRateOnSuccess = 30,
        };

        var selection = await SpecSelectionPolicy.SelectAsync(wanted, CameraSource.Local("Camera"), probe);
        var text = SpecSelectionPolicy.Describe(selection, wanted);

        Assert.Contains("已采用可用的原生配置（640×480 @ 30 FPS）", text);
        Assert.Contains("该配置来自摄像头原生模式，仅作为安全兜底", text);
        Assert.Contains(selection.Reason!, text);

        // 索引/日志那一句（Label）也必须带上实测的帧率。
        Assert.Contains("640×480 @ 30 FPS", selection.Spec.Label);
    }

    [Fact]
    public async Task 原生档那句话没量到帧率时_不许出现FPS这个字()
    {
        // ⚠️ 这一条是上面那条的**另一半**，别当成重复删掉：
        // 帧率是**独立**量出来的，量不到时**不许印**（§13.1：只展示磁盘上真实可测的内容）。
        // 印一个没测过的帧率，与印一个没测过的分辨率是同一件事。
        var wanted = new RecordingSpec(VideoCodec.H265, VideoResolution.Uhd4K);
        var native = RecordingSpec.Default with { NativeCaptureSize = true };

        // 尺寸量到了、帧率没量到 —— 这是真机会出现的组合。
        var probe = new FakeSpecProbe(native) { ObservedOnSuccess = (640, 480) };

        var selection = await SpecSelectionPolicy.SelectAsync(wanted, CameraSource.Local("Camera"), probe);
        var text = SpecSelectionPolicy.Describe(selection, wanted);

        Assert.Contains("已采用可用的原生配置（640×480）", text);
        Assert.Contains("原生", text);
        Assert.Contains(selection.Reason!, text);

        // 「@」也不能留 —— 留一个孤零零的「@」比不印更怪。
        Assert.DoesNotContain("FPS", text);
        Assert.DoesNotContain("FPS", selection.Spec.Label);
        Assert.DoesNotContain("@", text);
    }

    [Fact]
    public void 原生档不钉尺寸_也不印一个标称的分辨率()
    {
        var native = RecordingSpec.Default with { NativeCaptureSize = true };

        // 不钉 ⇒ ffmpeg 一个 -video_size / -framerate 都不该收到。
        Assert.Null(native.PinnedFfmpegSize);

        // 而 Label 里**不许**出现「1080P」——那是我们没给它选过的档。
        Assert.DoesNotContain("1080P", native.Label);
        Assert.Contains("原生", native.Label);

        // 测出来之后，Label 说的是实测的那一对。
        Assert.Contains("640×480", native.WithObservedSize(640, 480).Label);

        // 带上帧率时那句话的形状（设计图上那一格）。
        Assert.Contains("640×480 @ 30 FPS", native.WithObservedSize(640, 480, 30).Label);
    }

    [Fact]
    public void 原生档那句帧率按不变文化印_不会变成逗号小数点()
    {
        // ⚠️ 那句话是要与设计图**逐字**对上的，所以它不能受这台机器的小数点设置影响。
        // 这条用例**真的把文化切成逗号那一档**再断言 —— 否则它只是个摆设
        // （本机的 zh-CN 本来也是点，切不切都过）。
        var spec = RecordingSpec.Default.WithObservedSize(640, 480, 29.97);

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("640×480 @ 29.97 FPS", spec.ObservedDescription);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }


    // ─────────────────────────────────────────────
    // 容量系数（规格 §3.5.5 的连带项）
    // ─────────────────────────────────────────────

    private static RecordingEntry Entry(
        TimeSpan duration, string? codec, string? resolution) => new(
        "e-000",
        "sess-1",
        WaybillNumber.Parse("SF1000000001"),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.Add(duration),
        duration,
        RelativePath.Parse("2026/09/27/SF1000000001/e-000.mp4"),
        ContentHash.Parse(new string('a', 64)),
        "device-1",
        codec,
        resolution);

    [Fact]
    public void 容量系数按录制规格算_不是一个写死的数()
    {
        // 规格 §3.5.5 原话：「拿一个写死的系数算，4K 下会错得离谱 ——
        // 而电脑端「按空间清理」**正是用它决定删到够为止**，估错就是「删了还不够」」。
        var minute = TimeSpan.FromMinutes(1);

        var uhd = CleanupPlanner.EstimateBytes(Entry(minute, "H264", "Uhd4K"));
        var p1080 = CleanupPlanner.EstimateBytes(Entry(minute, "H264", "P1080"));
        var p720 = CleanupPlanner.EstimateBytes(Entry(minute, "H264", "P720"));

        Assert.True(uhd > p1080, "4K 必须比 1080P 大");
        Assert.True(p1080 > p720, "1080P 必须比 720P 大");

        // 同一个分辨率下 H.265 更省（这正是选它的理由）。
        Assert.True(
            CleanupPlanner.EstimateBytes(Entry(minute, "H265", "P1080")) < p1080,
            "H.265 应当比同分辨率的 H.264 小");
    }

    [Fact]
    public void 老条目没有规格字段时_按默认档估_而不是回到旧的低清系数()
    {
        // 追加字段之前录的那些没有 codec/resolution。
        // 回到那个 640x480 的旧系数只对旧的低清录像准，而「默认档」至少对得上
        // 今天真实录出来的东西。
        var minute = TimeSpan.FromMinutes(1);

        Assert.Equal(
            CleanupPlanner.EstimateBytes(Entry(minute, "H264", "P1080")),
            CleanupPlanner.EstimateBytes(Entry(minute, null, null)));
    }

    [Fact]
    public void 认不出的规格名也回默认档_不抛()
    {
        var minute = TimeSpan.FromMinutes(1);

        Assert.Equal(
            CleanupPlanner.EstimateBytes(Entry(minute, "H264", "P1080")),
            CleanupPlanner.EstimateBytes(Entry(minute, "HEVC", "8K")));
    }

    [Fact]
    public void 零时长估出零字节()
    {
        Assert.Equal(0, CleanupPlanner.EstimateBytes(Entry(TimeSpan.Zero, "H264", "P1080")));
    }
}
