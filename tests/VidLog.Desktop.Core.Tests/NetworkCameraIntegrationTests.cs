using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 真网络摄像头端到端：连上 → 录一段 → 产物能解码。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 这一组是本仓**第一次**端到端验 RTSP。在此之前「网络摄像头能不能录出来」
/// 只是**参数上对**（见 <c>docs/实现决策.md</c> §66.7 那句「没验」）——
/// 而这一组存在的理由就是那句话不该一直挂着。
/// </para>
/// <para>
/// 地址走环境变量 <see cref="RequiresRtspFactAttribute.EnvironmentVariable"/>，
/// 不写进仓库。
/// </para>
/// </remarks>
public class NetworkCameraIntegrationTests
{
    private static string Ffmpeg => FfmpegLocator.TryFind()!;

    private static CameraSource Source =>
        CameraSource.Network(Environment.GetEnvironmentVariable(
            RequiresRtspFactAttribute.EnvironmentVariable)!);

    [RequiresRtspFact]
    public async Task 录一段_产物非空且能通过真实解码校验()
    {
        using var dir = new TempDir();
        var runner = new SystemProcessRunner();

        // ⚠️ 用探测出来的编码器，而不是写死 libx264 —— 与摄像头那一路同一条规矩
        // （规格 §3.1.5「实测本机可用的编码能力，而不是假定」）。
        var encoder = EncoderSelection.Select(await new FfmpegEncoderProbe(Ffmpeg, runner).ProbeAsync());
        Assert.NotNull(encoder);

        var mkv = dir.File("segment-000.mkv");
        var capture = new FfmpegCameraCapture(Ffmpeg);
        var process = await capture.StartAsync(Source, mkv, encoder!);

        // 网络那一路不需要「开相机」那 1~1.5 秒，但起流 + 首帧仍要一点时间。
        await Task.Delay(TimeSpan.FromSeconds(6));

        var exitCode = await process.StopAsync(TimeSpan.FromSeconds(20));

        // ⚠️ 退出码这一条比摄像头那一路更要紧：网络源的失败**不抛异常**，
        // 它只是不产出东西 —— 不查退出码的话「录不出来」会一路静默到底。
        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(mkv), "采集必须产出文件");
        Assert.True(new FileInfo(mkv).Length > 0, "产物不能是 0 字节");

        var verification = await new DecodeVerifier(Ffmpeg, runner).VerifyAsync(mkv);
        Assert.True(verification.IsPlayable, verification.FailureReason);
    }

    [RequiresRtspFact]
    public async Task 测试连接_对着真源说得对编码与尺寸()
    {
        // 「测试连接」按钮跑的就是这一条（`FfmpegNetworkCameraProbe.InspectAsync`）。
        //
        // ⚠️ 这条路在 2026-09-30 之前**从来没对着真源跑过** —— 而它恰恰是
        // http 源上最先坏的那一条：`-rtsp_transport` 是 rtsp 解复用器的**私有**选项，
        // 加在 http 源上 ffmpeg 直接 `Option rtsp_transport not found`。
        // 后果是界面上「测试连接」报失败，而**摄像头其实是好的**
        // （错的是一条我们多加的选项）。见 `CameraSource.IsRtsp`。
        var info = await new FfmpegNetworkCameraProbe(Ffmpeg, new SystemProcessRunner())
            .InspectAsync(Source);

        Assert.True(info.Connected, info.FailureReason);

        // ⚠️ 「连上了但读不出尺寸」本身**不算失败**（见 NetworkStreamInfo.SizeKnown）——
        // 但那说明这台源印的流信息换个形状了，得有人看一眼，所以这里要求它读得出。
        Assert.True(info.SizeKnown, "连上了却没读出尺寸 —— 看一眼这台源印的流信息是什么形状");
        Assert.False(string.IsNullOrWhiteSpace(info.VideoCodec));
    }

    [RequiresRtspFact]
    public async Task 没开声音时_产物里不许有音轨()
    {
        // ⚠️ 这条守的是一件很容易被忽略的事：**网络摄像头自己可能带音轨**。
        // 实测过的那一路（2026-09-29）就同时推 H.264 + AAC。
        // 而 ffmpeg 在**不给 `-map`** 时会自动挑一路「最好的音频流」录进去 ——
        // 于是「设置里『录制声音』是关的」变成一句假话：文件里照样有声音，
        // 而且是**摄像头那头的**声音（不是用户本地的麦克风）。
        //
        // 判据用 `-map 0:a:0 -f null -`：有音轨就解成功（退出码 0），
        // 没有就报 `matches no streams`（非 0）。不需要另写一个 ffprobe 封装。
        using var dir = new TempDir();
        var runner = new SystemProcessRunner();

        var encoder = EncoderSelection.Select(await new FfmpegEncoderProbe(Ffmpeg, runner).ProbeAsync());
        Assert.NotNull(encoder);

        var mkv = dir.File("no-audio.mkv");
        var capture = new FfmpegCameraCapture(Ffmpeg);
        var process = await capture.StartAsync(Source, mkv, encoder!);

        await Task.Delay(TimeSpan.FromSeconds(6));
        await process.StopAsync(TimeSpan.FromSeconds(20));

        var probe = await runner.RunAsync(Ffmpeg,
            ["-hide_banner", "-v", "error", "-i", mkv, "-map", "0:a:0", "-f", "null", "-"]);

        Assert.False(
            probe.Succeeded,
            "「录制声音」关着，产物里却有一条音轨 —— 那是摄像头自带的那一路被自动挑进去了");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-net-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            // 与摄像头那一路同一条：Windows 在「文件正被另一个进程持有」时可能报
            // UnauthorizedAccessException，清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
