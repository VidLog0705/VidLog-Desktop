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
        var arguments = FfmpegCameraCapture.BuildArguments("Camera", "out.mkv", "libx264", spec);

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
        var arguments = FfmpegCameraCapture.BuildArguments("Camera", "out.mkv", "libx264");

        Assert.DoesNotContain("-video_size", arguments);
        Assert.DoesNotContain("-framerate", arguments);
        Assert.Contains("-i", arguments);
    }

    // ─────────────────────────────────────────────
    // 可用性检查与回落
    // ─────────────────────────────────────────────

    /// <summary>只认某些组合的假探测。</summary>
    private sealed class FakeSpecProbe(params RecordingSpec[] usable) : IRecordingSpecProbe
    {
        public List<RecordingSpec> Tried { get; } = [];

        public Task<SpecProbeResult> ProbeAsync(
            RecordingSpec spec, string device, CancellationToken cancellationToken = default)
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
            wanted, "Camera", new FakeSpecProbe(wanted));

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
        var selection = await SpecSelectionPolicy.SelectAsync(wanted, "Camera", probe);

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
            "Camera",
            new FakeSpecProbe());

        Assert.Equal(RecordingSpec.Default, selection.Spec);
        Assert.True(selection.ChangedFromRequested);
        Assert.False(string.IsNullOrWhiteSpace(selection.Reason));
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
