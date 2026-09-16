using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 打真 FFmpeg 的集成测试。
/// </summary>
/// <remarks>
/// 前面那些包装（探测、remux、解码校验）本身没什么逻辑，价值全在
/// **它们调用的 FFmpeg 参数对不对**。参数写错时单元测试照样绿，
/// 只有真跑一遍才知道 —— 所以这一组必须打真二进制。
/// </remarks>
public class FfmpegIntegrationTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "vidlog-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string Ffmpeg => FfmpegLocator.TryFind()!;

    private static async Task<string> EncodeTestClipAsync(
        TempDir dir,
        string fileName = "clip.mkv",
        string encoder = "libx264")
    {
        var runner = new SystemProcessRunner();
        var path = dir.File(fileName);

        var result = await runner.RunAsync(Ffmpeg, [
            "-hide_banner", "-v", "error",
            "-f", "lavfi",
            "-i", "testsrc=size=160x120:rate=10:duration=1",
            "-c:v", encoder,
            "-y", path,
        ]);

        Assert.True(result.Succeeded, $"测试素材编码失败：{result.StandardError}");
        return path;
    }

    // ─────────────────────────────────────────────
    // 编码能力实测探测（规格 §3.1.5）
    // ─────────────────────────────────────────────

    [RequiresFfmpegFact]
    public async Task 软件兜底编码器必须可用()
    {
        // libx264 是候选表最后的兜底。它都不可用说明 FFmpeg 本身有问题，
        // 那录制根本起不来 —— 这是硬失败，必须让测试红。
        var probe = new FfmpegEncoderProbe(Ffmpeg, new SystemProcessRunner());

        var results = await probe.ProbeAsync();

        var x264 = results.Single(r => r.EncoderName == "libx264");
        Assert.True(x264.Usable, $"libx264 应当可用，实际：{x264.FailureReason}");
    }

    [RequiresFfmpegFact]
    public async Task 候选表里的每一个都会被实测()
    {
        var probe = new FfmpegEncoderProbe(Ffmpeg, new SystemProcessRunner());

        var results = await probe.ProbeAsync();

        Assert.Equal(EncoderCandidates.Default.Count, results.Count);
        Assert.Equal(EncoderCandidates.Default, results.Select(r => r.EncoderName));
    }

    [RequiresFfmpegFact]
    public async Task 探测不存在的编码器会被判为不可用_证明它真在跑编码()
    {
        // 这条是「实测」的证明：如果探测只是查 ffmpeg -encoders 列表，
        // 一个不存在的名字不会得到「失败原因」，只会被当作没匹配上。
        var probe = new FfmpegEncoderProbe(
            Ffmpeg,
            new SystemProcessRunner(),
            candidates: ["this_encoder_does_not_exist", "libx264"]);

        var results = await probe.ProbeAsync();

        Assert.False(results[0].Usable);
        Assert.NotNull(results[0].FailureReason);
        Assert.True(results[1].Usable);
    }

    [RequiresFfmpegFact]
    public async Task 选择器取第一个实测可用的()
    {
        var probe = new FfmpegEncoderProbe(Ffmpeg, new SystemProcessRunner());

        var results = await probe.ProbeAsync();
        var selected = EncoderSelection.Select(results);

        Assert.NotNull(selected);
        Assert.True(results.Single(r => r.EncoderName == selected).Usable);
    }

    [Fact]
    public void 没有任何可用编码器时选择器返回null()
    {
        var results = new List<EncoderProbeResult>
        {
            new("h264_nvenc", false, "无 N 卡"),
            new("libx264", false, "FFmpeg 损坏"),
        };

        Assert.Null(EncoderSelection.Select(results));
    }

    // ─────────────────────────────────────────────
    // MKV → MP4 无损 remux（规格 §3.1.4）
    // ─────────────────────────────────────────────

    [RequiresFfmpegFact]
    public async Task remux出的MP4能通过实际解码校验()
    {
        using var dir = new TempDir();
        var mkv = await EncodeTestClipAsync(dir);
        var mp4 = dir.File("out.mp4");

        var remux = new RemuxPipeline(Ffmpeg, new SystemProcessRunner());
        var result = await remux.RemuxAsync(mkv, mp4);

        Assert.True(result.Succeeded, result.FailureReason);

        var verifier = new DecodeVerifier(Ffmpeg, new SystemProcessRunner());
        var verification = await verifier.VerifyAsync(mp4);

        Assert.True(verification.IsPlayable, verification.FailureReason);
    }

    [RequiresFfmpegFact]
    public async Task remux失败时不删源文件_那是此刻唯一的副本()
    {
        using var dir = new TempDir();
        var mkv = await EncodeTestClipAsync(dir);

        // 目标路径非法，制造 remux 失败
        var remux = new RemuxPipeline(Ffmpeg, new SystemProcessRunner());
        var result = await remux.RemuxAsync(mkv, dir.File("no-such-dir/out.mp4"));

        Assert.False(result.Succeeded);
        Assert.True(File.Exists(mkv), "remux 失败后源 MKV 必须还在（I2）");
    }

    [Fact]
    public async Task 源文件不存在时remux失败而不抛()
    {
        var remux = new RemuxPipeline("ffmpeg-does-not-exist", new SystemProcessRunner());

        var result = await remux.RemuxAsync("/no/such/file.mkv", "/no/such/out.mp4");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
    }

    // ─────────────────────────────────────────────
    // 实际解码校验（规格 §3.1.4 / §8）
    // ─────────────────────────────────────────────

    [RequiresFfmpegFact]
    public async Task 截断的文件通不过解码校验()
    {
        // 这条正是规格 §8 要防的「半成品」：文件存在、体积不小，但容器头没写完。
        // 只检查「文件存在 + 大小不为零」会放它过去。
        using var dir = new TempDir();
        var mkv = await EncodeTestClipAsync(dir);

        var bytes = await File.ReadAllBytesAsync(mkv);
        var truncated = dir.File("truncated.mkv");
        await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length / 3)]);

        var verifier = new DecodeVerifier(Ffmpeg, new SystemProcessRunner());
        var verification = await verifier.VerifyAsync(truncated);

        Assert.True(File.Exists(truncated));
        Assert.True(new FileInfo(truncated).Length > 0, "前置条件：这个文件不为空");
        Assert.False(verification.IsPlayable, "截断的文件不该判为可播放");
    }

    [RequiresFfmpegFact]
    public async Task 纯垃圾字节通不过解码校验()
    {
        using var dir = new TempDir();
        var junk = dir.File("junk.mp4");
        await File.WriteAllBytesAsync(junk, new byte[64 * 1024]);

        var verifier = new DecodeVerifier(Ffmpeg, new SystemProcessRunner());
        var verification = await verifier.VerifyAsync(junk);

        Assert.False(verification.IsPlayable);
    }

    [RequiresFfmpegFact]
    public async Task 文件不存在时解码校验失败而不抛()
    {
        var verifier = new DecodeVerifier(Ffmpeg, new SystemProcessRunner());

        var verification = await verifier.VerifyAsync("/no/such/file.mp4");

        Assert.False(verification.IsPlayable);
        Assert.NotNull(verification.FailureReason);
    }

    // ─────────────────────────────────────────────
    // FFmpeg 定位
    // ─────────────────────────────────────────────

    [RequiresFfmpegFact]
    public void 能在PATH上找到FFmpeg()
    {
        var found = FfmpegLocator.TryFind();

        Assert.NotNull(found);
        Assert.True(File.Exists(found));
    }

    [Fact]
    public void 显式路径存在时优先于PATH()
    {
        using var dir = new TempDir();
        var explicitPath = dir.File(FfmpegLocator.ExecutableNames[0]);
        File.WriteAllText(explicitPath, "not really ffmpeg");

        // 定位器只判断「文件在不在」，是不是真能跑由探测负责 —— 职责分开。
        Assert.Equal(explicitPath, FfmpegLocator.TryFind(explicitPath: explicitPath));
    }

    [Fact]
    public void 显式路径失效时回退而不是直接失败()
    {
        // 配置里写了个失效路径，不该让录制起不来（I4「坏配置不得导致录制失败」的同一条精神）。
        // 代价是「用户指定的 FFmpeg 没被用上」这件事必须被记录 —— 那是上层的事。
        var result = FfmpegLocator.TryFind(explicitPath: "/definitely/not/here/ffmpeg");

        Assert.Equal(FfmpegLocator.TryFind(), result);
    }
}
