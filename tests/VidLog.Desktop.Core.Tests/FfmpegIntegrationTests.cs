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
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（实测：会话还在
            // 收尾时删含 `.ass` / `.mkv` 的临时目录）。清理失败不该让测试红。
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
    // 合并分段（2026-10-12 起一场录制合成一个成品）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 造一段**带音轨**的 1 秒片段。
    /// </summary>
    /// <remarks>
    /// ⚠️ 音轨是这条用例的重点：合并走的是 <c>-c copy</c>，任何一路流没被接上
    /// 都**不会报错** —— 出来的片子照样能播，只是从头到尾没声音。
    /// 本仓吃过这个亏（ffmpeg 管道那次静默丢音轨），所以这里必须有音轨。
    /// </remarks>
    private static async Task<string> EncodeClipWithAudioAsync(TempDir dir, string fileName)
    {
        var path = dir.File(fileName);
        var result = await new SystemProcessRunner().RunAsync(Ffmpeg, [
            "-hide_banner", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=1",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-c:v", "libx264", "-c:a", "aac", "-shortest",
            "-y", path,
        ]);

        Assert.True(result.Succeeded, $"测试素材编码失败：{result.StandardError}");
        return path;
    }

    /// <summary>把 ffmpeg 那句 <c>Duration: 00:00:02.00</c> 读成秒；读不出来给 -1。</summary>
    private static double DurationSeconds(string stderr)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            stderr, @"Duration: (\d+):(\d\d):(\d\d(?:\.\d+)?)");

        if (!match.Success)
        {
            return -1;
        }

        return (int.Parse(match.Groups[1].Value) * 3600)
            + (int.Parse(match.Groups[2].Value) * 60)
            + double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>造一段**只有画面、没有音轨**的 1 秒片段（＝ 预录开场段修复**之前**的样子）。</summary>
    private static async Task<string> EncodeClipWithoutAudioAsync(TempDir dir, string fileName)
    {
        var path = dir.File(fileName);
        var result = await new SystemProcessRunner().RunAsync(Ffmpeg, [
            "-hide_banner", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=1",
            "-c:v", "libx264", "-an",
            "-y", path,
        ]);

        Assert.True(result.Succeeded, $"测试素材编码失败：{result.StandardError}");
        return path;
    }

    /// <summary>造一段**画面 ＋ 静音音轨**的 1 秒片段（＝ 2026-10-12 之后预录开场段的样子）。</summary>
    /// <remarks>
    /// 输入顺序、参数照抄生产（<see cref="VidLog.Desktop.Core.Recording.FfmpegCameraCapture.SilentAudioInputArguments"/>
    /// ＋ 那四个 <c>-c:a/-ac/-ar/-b:a</c>）—— 两处参数一旦走岔，接缝就不是生产那条了。
    /// </remarks>
    private static async Task<string> EncodeClipWithSilentAudioAsync(TempDir dir, string fileName)
    {
        var path = dir.File(fileName);
        var result = await new SystemProcessRunner().RunAsync(Ffmpeg, [
            "-hide_banner", "-v", "error",
            "-f", "lavfi", "-i", "anullsrc=channel_layout=mono:sample_rate=44100",
            "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=1",
            "-c:v", "libx264",
            "-c:a", "aac", "-ac", "1", "-ar", "44100", "-b:a", "64k",
            "-shortest",
            "-y", path,
        ]);

        Assert.True(result.Succeeded, $"测试素材编码失败：{result.StandardError}");
        return path;
    }

    /// <summary>造一段**画面 ＋ 440Hz**的 1 秒片段，音频参数与生产逐字一致。</summary>
    private static async Task<string> EncodeClipWithToneAsync(TempDir dir, string fileName)
    {
        var path = dir.File(fileName);
        var result = await new SystemProcessRunner().RunAsync(Ffmpeg, [
            "-hide_banner", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=1",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-c:v", "libx264",
            "-c:a", "aac", "-ac", "1", "-ar", "44100", "-b:a", "64k",
            "-shortest",
            "-y", path,
        ]);

        Assert.True(result.Succeeded, $"测试素材编码失败：{result.StandardError}");
        return path;
    }

    /// <summary>成品里到底有没有音轨 —— `-map 0:a:0` 在没有音频流时直接失败。</summary>
    private static async Task<bool> HasAudioAsync(IProcessRunner runner, string path)
    {
        var audio = await runner.RunAsync(Ffmpeg,
            ["-v", "error", "-i", path, "-map", "0:a:0", "-f", "null", "-"]);

        return audio.Succeeded;
    }

    [RequiresFfmpegFact]
    public async Task 开场段没有音轨时_合并会把整条音轨吃掉_这就是那个缺陷()
    {
        // ⚠️ 这条**钉的是 ffmpeg 的行为**（concat 的流布局取自列表里第一个文件），
        // 它是绿的 = 「只要开场段没有音轨，成品就必然是哑的」——2026-10-12 真机实测到的
        // 那个缺陷的最小复现。留着它是为了挡住「也许合并本来就没问题」这种猜测。
        using var dir = new TempDir();

        var opening = await EncodeClipWithoutAudioAsync(dir, "segment-000.mkv");
        var body = await EncodeClipWithToneAsync(dir, "segment-001.mkv");
        var output = dir.File("merged.mp4");

        var runner = new SystemProcessRunner();
        var merge = await new RemuxPipeline(Ffmpeg, runner).MergeAsync([opening, body], output);
        Assert.True(merge.Succeeded, merge.FailureReason);

        Assert.False(await HasAudioAsync(runner, output));
    }

    [RequiresFfmpegFact]
    public async Task 预录段带上静音音轨之后_成品的音轨保住了()
    {
        // ★ 2026-10-12 那个缺陷的**修法**跑一遍：预录开场段带一条**静音**音轨
        // （`FfmpegCameraCapture.SilentAudioInputArguments`），与正片流布局一致 ⇒
        // concat 的 `copy` 不再把音轨整条丢掉。开场那 1 秒是静音，正片是有声的。
        using var dir = new TempDir();

        var opening = await EncodeClipWithSilentAudioAsync(dir, "segment-000.mkv");
        var body = await EncodeClipWithToneAsync(dir, "segment-001.mkv");
        var output = dir.File("merged.mp4");

        var runner = new SystemProcessRunner();
        var merge = await new RemuxPipeline(Ffmpeg, runner).MergeAsync([opening, body], output);
        Assert.True(merge.Succeeded, merge.FailureReason);

        Assert.True(await HasAudioAsync(runner, output), "成品必须有音轨 —— 开场段那条静音要把它占住");

        // 时长仍是两段之和 ⇒ 静音那一路上来没有把画面顶掉。
        var probe = await runner.RunAsync(Ffmpeg, ["-hide_banner", "-i", output]);
        var said = probe.StandardError + probe.StandardOutput;
        Assert.True(
            Math.Abs(DurationSeconds(said) - 2.0) < 0.35,
            $"合并后的时长不是两段之和：{DurationSeconds(said)} 秒\n{said}");
    }

    [RequiresFfmpegFact]
    public async Task 两段带音轨的片段合并成一条_音轨与时长都在()
    {
        // 这是「一个单号一个文件」那条改动的**真跑一遍**：预录开场段与正片是
        // 两个独立 ffmpeg 进程写的两个 MKV，收尾时要能接成一条。
        using var dir = new TempDir();

        var first = await EncodeClipWithAudioAsync(dir, "segment-000.mkv");
        var second = await EncodeClipWithAudioAsync(dir, "segment-001.mkv");
        var output = dir.File("merged.mp4");

        var runner = new SystemProcessRunner();
        var merge = await new RemuxPipeline(Ffmpeg, runner).MergeAsync([first, second], output);

        Assert.True(merge.Succeeded, merge.FailureReason);

        // 探一遍成品：时长 2 秒（两段接上了）、且**音轨还在**。
        var probe = await runner.RunAsync(Ffmpeg, ["-hide_banner", "-i", output]);
        var said = probe.StandardError + probe.StandardOutput;

        Assert.True(
            Math.Abs(DurationSeconds(said) - 2.0) < 0.35,
            $"合并后的时长不是两段之和：{DurationSeconds(said)} 秒\n{said}");
        Assert.Contains("Audio: ", said, StringComparison.Ordinal);
    }

    /// <summary>一个独立 ffmpeg 进程写的 1 秒 440Hz 音调（MKV / AAC 44100 单声道 64k）。</summary>
    /// <remarks>
    /// ⚠️ 音频参数**照抄生产**（<c>FfmpegCameraCapture.BuildArguments</c> 里那四个）——
    /// 参数不同的话接缝处的行为就不是生产那条了，这条量到的数也就不作数。
    /// </remarks>
    private static async Task<string> EncodeToneClipAsync(TempDir dir, string fileName)
    {
        var path = dir.File(fileName);

        var result = await new SystemProcessRunner().RunAsync(Ffmpeg, [
            "-hide_banner", "-v", "error",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-c:a", "aac", "-ac", "1", "-ar", "44100", "-b:a", "64k",
            "-f", "matroska", "-y", path,
        ]);

        Assert.True(result.Succeeded, $"测试素材编码失败：{result.StandardError}");
        return path;
    }

    [RequiresFfmpegFact]
    public async Task 两个进程各写一段音轨_合成后接缝处没有缺口()
    {
        // ★ 这是交接单里那条「**最大的未验证项**」的自动化量具：预录开场段与正片是
        // **两个独立 ffmpeg 进程**写的、各带自己的 AAC priming（约 21~43 ms），收尾走
        // `-c copy` 把它们接起来 —— 而这一步**不会报错**：接缝处真缺一小截声音，
        // 退出码照样是 0，用户只会觉得「开头那一下怪怪的」。
        //
        // 量具：两个进程各写 1 秒 440Hz。两段都是 440 个整周期、都从相位 0 起，
        // 接起来本该是一段**不断的** 2 秒音 ⇒ 合成后任何 ≥50 ms 的静默都是接缝被啃掉。
        //
        // ⚠️ 它抓的是**缺口**，抓不到「重了一小截 / 相位毛刺」那种听感上的「咔」——
        // 那一半要人耳，见交接单。
        using var dir = new TempDir();

        var first = await EncodeToneClipAsync(dir, "segment-000.mkv");
        var second = await EncodeToneClipAsync(dir, "segment-001.mkv");
        var output = dir.File("merged.mp4");

        var runner = new SystemProcessRunner();
        var merge = await new RemuxPipeline(Ffmpeg, runner).MergeAsync([first, second], output);
        Assert.True(merge.Succeeded, merge.FailureReason);

        var probe = await runner.RunAsync(Ffmpeg, [
            "-hide_banner", "-v", "info",
            "-i", output,
            "-af", "silencedetect=n=-50dB:d=0.05",
            "-f", "null", "-",
        ]);
        var said = probe.StandardError + probe.StandardOutput;

        Assert.True(
            Math.Abs(DurationSeconds(said) - 2.0) < 0.15,
            $"合成后的时长不是两段之和：{DurationSeconds(said)} 秒\n{said}");
        Assert.DoesNotContain("silence_start", said, StringComparison.Ordinal);
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
