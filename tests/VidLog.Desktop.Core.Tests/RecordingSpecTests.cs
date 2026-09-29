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

    /// <summary>只认某些组合的假探测。</summary>
    private sealed class FakeSpecProbe(params RecordingSpec[] usable) : IRecordingSpecProbe
    {
        public List<RecordingSpec> Tried { get; } = [];

        public Task<SpecProbeResult> ProbeAsync(
            RecordingSpec spec, CameraSource source, CancellationToken cancellationToken = default)
        {
            Tried.Add(spec);

            return Task.FromResult(usable.Contains(spec)
                ? new SpecProbeResult(spec, spec.EncoderCandidates[0], true, null)
                : new SpecProbeResult(spec, spec.EncoderCandidates[0], false, "这个组合打不开"));
        }
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
