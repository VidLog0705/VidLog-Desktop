using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「测试连接」：读对端的编码与尺寸。
/// </summary>
/// <remarks>
/// ⚠️ 在 <see cref="NetworkCameraCollection"/> 里：最后那一条打的是**同一台真手机**
/// （<c>VIDLOG_TEST_RTSP_URL</c>），与另外两个类串行跑。
/// </remarks>
[Collection(NetworkCameraCollection.Name)]
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

        // ⚠️ 这一路**没有** `fps` 那个字段（只有 `29.97 tbr`）⇒ 帧率读不出来。
        // 这不是缺陷：`tbr` 是时间基，RTSP 上常是 `90k tbr` 这种根本不是帧率的数
        // —— 所以宁可不认。它只影响「原生档那句话印不印帧率」。
        Assert.Null(streams.FrameRate);

        // ⚠️ 实时流的容器头写的是 `Duration: N/A` ⇒ 时长读不出来。
        // 这条有实际用途：【导入录像】靠它决定要不要说「没量出时长」，
        // 而把 N/A 读成某个数会让界面替用户认一个假的时长。
        Assert.Null(streams.Duration);
    }

    [Fact]
    public void 从读回的成品里读得出时长()
    {
        // 2026-09-30：读回一个录好的文件时，容器那一段会印
        //   Duration: 00:01:23.45, start: 0.000000, bitrate: 1234 kb/s
        // 「导入录像」（`RecordingImporter.InspectAsync`）量的就是它。
        const string ReadBack = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'C:\Temp\SF1000000001.mp4':
              Duration: 00:01:23.45, start: 0.000000, bitrate: 1234 kb/s
              Stream #0:0: Video: h264 (High), yuv420p(progressive), 1920x1080, 30 fps, 30 tbr, 90k tbn
            """;

        var streams = FfmpegNetworkCameraProbe.ParseStreams(ReadBack);

        Assert.Equal(TimeSpan.FromSeconds(83.45), streams.Duration);
    }

    [Fact]
    public void 时长为零时当作读不出来()
    {
        // `Duration: 00:00:00.00` 在实时流与坏容器上都会出现。收下它的话，
        // 一段真实录像会被记成 0 秒 —— 而且 `ImportResult.Duration` 会是 0
        // 而不是 null，界面就不会说「没量出时长」，用户以为这段录像真的只有 0 秒。
        var streams = FfmpegNetworkCameraProbe.ParseStreams(
            "  Duration: 00:00:00.00, start: 0.000000, bitrate: 0 kb/s");

        Assert.Null(streams.Duration);
    }

    [Fact]
    public void 前面那行是N_A也不挡住后面认得出的那一行()
    {
        var streams = FfmpegNetworkCameraProbe.ParseStreams("""
              Duration: N/A, start: 0.000000, bitrate: N/A
              Duration: 00:00:12.00, start: 0.000000, bitrate: 900 kb/s
            """);

        Assert.Equal(TimeSpan.FromSeconds(12), streams.Duration);
    }

    [Fact]
    public void 从读回的成品里读出帧率()
    {
        // 2026-09-30 本机实测：把录好的 MKV 读回来（`-v info -i x.mkv -frames:v 1 -f null -`），
        // ffmpeg 在流信息那一行**会**印帧率。这正是原生档问「多少帧」的路子
        // （`FfmpegSpecProbe.MeasureObservedAsync`）—— 所以这条解析必须有。
        const string ReadBack = """
            Input #0, matroska,webm, from 'C:\Temp\vidlog-spec-probe-abc.mkv':
              Stream #0:0: Video: h264 (High), yuv420p(tv, progressive), 640x480 [SAR 1:1 DAR 4:3], 30 fps, 30 tbr, 1k tbn
            """;

        var streams = FfmpegNetworkCameraProbe.ParseStreams(ReadBack);

        Assert.Equal(640, streams.Width);
        Assert.Equal(480, streams.Height);
        Assert.Equal(30, streams.FrameRate);
    }

    [Fact]
    public void 帧率带小数也读得出来()
    {
        var streams = FfmpegNetworkCameraProbe.ParseStreams(
            "  Stream #0:0: Video: h264, yuv420p, 1920x1080, 29.97 fps, 30k tbr, 90k tbn");

        Assert.Equal(29.97, streams.FrameRate);
    }

    [Fact]
    public void 不许把tbr当成帧率()
    {
        // ⚠️ 这条是防「认出一个错的帧率」——`90k tbr` 里的 90 显然不是帧率，
        // 而 `(?<![\d.])` 那个前视也挡着 `1k tbn` 这类邻近字段。
        // 「读不出来」是可接受的，「读出一个错的」不行。
        var streams = FfmpegNetworkCameraProbe.ParseStreams(
            "  Stream #0:0: Video: h264, yuv420p, 1920x1080, 90k tbr, 90k tbn");

        Assert.Null(streams.FrameRate);
    }

    [Fact]
    public void 帧率读不出来时是null_不猜()
    {
        var streams = FfmpegNetworkCameraProbe.ParseStreams(
            "  Stream #0:0: Video: h264, yuv420p, 640x480, 0 fps, 30 tbr");

        // `0 fps` 明显不是帧率（ffmpeg 表达「不知道」时会这么打）⇒ 不许当 0 收下，
        // 那样界面会印出「@ 0 FPS」。
        Assert.Null(streams.FrameRate);
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
