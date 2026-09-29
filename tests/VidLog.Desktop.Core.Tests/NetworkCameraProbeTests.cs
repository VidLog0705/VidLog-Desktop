using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「测试连接」：读对端的编码与尺寸。
/// </summary>
public class NetworkCameraProbeTests
{
    /// <summary>
    /// 2026-09-29 那台真网络摄像头（旧手机 + IP 摄像头软件）的真实输出。
    /// </summary>
    /// <remarks>
    /// ⚠️ **凭据已抹成占位**。原始输出里那一行是
    /// <c>Input #0, rtsp, from 'rtsp://账号:密码@主机:554/流':</c> ——
    /// 正是 <see cref="Diagnostics.UrlCredentials"/> 要挡的那条外泄路
    /// （ffmpeg 自己会把带凭据的地址打出来）。
    /// </remarks>
    private const string RealOutput = """
        Input #0, rtsp, from 'rtsp://user:pw@host:554/live':
          Metadata:
            title           : Live
          Duration: N/A, start: 0.006000, bitrate: N/A
          Stream #0:0: Video: h264 (High), yuv420p(progressive), 1920x1080, 29.97 tbr, 90k tbn, start 0.006000
          Stream #0:1: Audio: aac (LC), 48000 Hz, stereo, fltp, start 0.001708
        Stream mapping:
          Stream #0:0 -> #0:0 (h264 (native) -> wrapped_avframe (native))
        """;

    [Fact]
    public void 从真实输出里读出尺寸与编码_以及有没有音轨()
    {
        var streams = FfmpegNetworkCameraProbe.ParseStreams(RealOutput);

        Assert.True(streams.HasVideo);
        Assert.Equal(1920, streams.Width);
        Assert.Equal(1080, streams.Height);
        Assert.Equal("h264", streams.VideoCodec);

        // ⚠️ 这一条有实际用途：对端自带音轨时，「录制声音」关着也不能让它混进来
        // （见 docs/实现决策.md §66.9）。
        Assert.True(streams.HasAudio);
    }

    [Fact]
    public void 不许把映射行当成第二路视频()
    {
        // ⚠️ 真实输出里还有 `Stream #0:0 -> #0:0 (h264 …)` 这样的**映射行**，
        // 以及 `Stream #0:0: Video: wrapped_avframe, …, 1920x1080` 这样的**输出流**行。
        // 不挡的话「第一路」会被后面某一行覆盖，而尺寸未必一样
        // （输出那一路是我们自己要的尺寸，不是对端的）。
        var withOutput = RealOutput + "\n" + """
            Output #0, null, to 'pipe:':
              Stream #0:0: Video: wrapped_avframe, yuv420p(progressive), 1920x1080, q=2-31
              Stream #0:1: Audio: pcm_s16le, 48000 Hz, stereo, s16, 1536 kb/s
            """;

        var streams = FfmpegNetworkCameraProbe.ParseStreams(withOutput);

        Assert.Equal(1920, streams.Width);
        Assert.Equal("h264", streams.VideoCodec);
    }

    [Fact]
    public void 只有音频时没有视频那一路()
    {
        // 那种源没法录 —— 判据是「有没有视频路」，不是退出码。
        var streams = FfmpegNetworkCameraProbe.ParseStreams(
            "  Stream #0:0: Audio: aac (LC), 44100 Hz, mono, fltp");

        Assert.False(streams.HasVideo);
        Assert.True(streams.HasAudio);
    }

    [Fact]
    public void 读不出尺寸时也算有视频路_只是尺寸未知()
    {
        // ⚠️ 「不知道」与「不行」必须分开：前者不挡用户，后者才拦。
        var streams = FfmpegNetworkCameraProbe.ParseStreams(
            "  Stream #0:0: Video: h264, weird-but-no-size");

        Assert.True(streams.HasVideo);
        Assert.Null(streams.Width);
        Assert.Null(streams.Height);
    }

    [Fact]
    public void 空输出不抛()
    {
        var streams = FfmpegNetworkCameraProbe.ParseStreams(string.Empty);

        Assert.False(streams.HasVideo);
        Assert.False(streams.HasAudio);
        Assert.Null(streams.Width);
    }

    // ─────────────────────────────────────────────
    // 不真开进程的那几条
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 地址没配好时不去开进程_直接给中文()
    {
        var runner = new ProbingRunner();
        var probe = new FfmpegNetworkCameraProbe("ffmpeg.exe", runner);

        var info = await probe.InspectAsync(CameraSource.Network("192.168.1.64:554/s"));

        Assert.False(info.Connected);
        Assert.Contains("rtsp://", info.FailureReason);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task 空地址也说中文()
    {
        var probe = new FfmpegNetworkCameraProbe("ffmpeg.exe", new ProbingRunner());

        var info = await probe.InspectAsync(CameraSource.Network("  "));

        Assert.False(info.Connected);
        Assert.False(string.IsNullOrWhiteSpace(info.FailureReason));
        Assert.DoesNotContain("http", info.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>真源：连上并读出对端的真实尺寸与编码。</summary>
    [RequiresRtspFact]
    public async Task 真网络摄像头_读出对端真实的尺寸与编码()
    {
        var probe = new FfmpegNetworkCameraProbe(
            FfmpegLocator.TryFind()!, new SystemProcessRunner());

        var info = await probe.InspectAsync(CameraSource.Network(
            Environment.GetEnvironmentVariable(RequiresRtspFactAttribute.EnvironmentVariable)!));

        Assert.True(info.Connected, info.FailureReason);
        Assert.True(info.SizeKnown, "应读出对端的分辨率");

        // 裁决要用的那个数就是它（够不够 1080P）。
        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.Equal(CameraSizeVerdict.Enough, CameraSizePolicy.Judge(info.Width, info.Height, VideoResolution.P1080));
    }
}
