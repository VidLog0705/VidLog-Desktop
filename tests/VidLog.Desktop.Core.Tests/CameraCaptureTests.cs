using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 采集侧的两件纯粹的事：设备枚举的解析、采集命令的拼装。
/// </summary>
/// <remarks>
/// 这两块**不依赖摄像头** —— 这是刻意的。参数写错时，真机集成测试只会表现为
/// 「录不出来」，说不清是设备的问题还是参数的问题；把它们抽成纯函数就能直接断言。
/// </remarks>
public class CameraCaptureTests
{
    // ─────────────────────────────────────────────
    // 设备枚举的解析 —— 规格 §3.2.1「摄像头识码」的前提
    // ─────────────────────────────────────────────

    /// <summary>本机实测的真实输出（`ffmpeg -list_devices true -f dshow -i dummy`）。</summary>
    private const string RealOutput = """
        [dshow @ 000001df1754fe80] "Lenovo EasyCamera" (video)
        [dshow @ 000001df1754fe80]   Alternative name "@device_pnp_\\?\usb#vid_04f2&pid_b272&mi_00#7&261be5eb&0&0000#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global"
        [dshow @ 000001df1754fe80]   Alternative name "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{9FD6145A-C96B-407D-AC71-83D5BB6ACE47}"
        [dshow @ 000001df1754fe80] "麦克风 (USB Audio Device)" (audio)
        [dshow @ 000001df1754fe80]   Alternative name "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{45944422-6F3D-47DD-A731-B5EFB4143F31}"
        """;

    [Fact]
    public void 只挑视频设备_音频与alternative_name都要排除()
    {
        var devices = CameraDevices.ParseVideoDevices(RealOutput);

        // 只该剩摄像头。Alternative name 是同一台设备的另一种写法，
        // 不是第二台设备 —— 不过滤的话一台相机会被数成三个。
        var device = Assert.Single(devices);
        Assert.Equal("Lenovo EasyCamera", device);
    }

    [Fact]
    public void 没有摄像头时返回空表而不是抛()
    {
        // 「本机没摄像头」是正常的运行环境（上一台开发机就是），不是异常。
        Assert.Empty(CameraDevices.ParseVideoDevices("[dshow @ 0000] \"麦克风\" (audio)"));
        Assert.Empty(CameraDevices.ParseVideoDevices(string.Empty));
    }

    [Fact]
    public void 同一设备重复出现只算一个()
    {
        var devices = CameraDevices.ParseVideoDevices(
            "[dshow @ 0] \"Cam\" (video)\n[dshow @ 0] \"Cam\" (video)");

        Assert.Single(devices);
    }

    // ─────────────────────────────────────────────
    // 采集命令的拼装
    // ─────────────────────────────────────────────

    [Fact]
    public void 采集命令走dshow并带上设备名与目标路径()
    {
        var args = FfmpegCameraCapture.BuildArguments("Lenovo EasyCamera", @"C:\out\seg.mkv", "libx264");

        Assert.Contains("dshow", args);
        Assert.Contains("video=Lenovo EasyCamera", args);
        Assert.Contains("libx264", args);
        Assert.Contains(@"C:\out\seg.mkv", args);

        // 输入侧的选项必须在 -i 之前 —— 放错位置的选项会被 ffmpeg 当成输出选项，
        // 表现是「设备打不开」而看不出原因。
        Assert.True(
            args.ToList().IndexOf("-rtbufsize") < args.ToList().IndexOf("-i"),
            "-rtbufsize 是输入侧选项，必须在 -i 之前");
    }

    [Fact]
    public void 输出路径紧跟在y之后()
    {
        // 测试替身靠「-y 后面那个参数就是产物」来定位输出文件，换顺序会静默失效。
        var args = FfmpegCameraCapture.BuildArguments("Cam", @"C:\out\seg.mkv", "libx264").ToList();

        var yIndex = args.IndexOf("-y");
        Assert.True(yIndex >= 0, "采集命令必须有 -y");
        Assert.Equal(@"C:\out\seg.mkv", args[yIndex + 1]);
    }

    [Fact]
    public void 显式把像素格式转成yuv420p()
    {
        // 摄像头出的是 yuyv422，而 libx264 不接受它 —— 不显式转的话编码器直接失败。
        var args = FfmpegCameraCapture.BuildArguments("Cam", @"C:\out\seg.mkv", "libx264");

        var list = args.ToList();
        var pixIndex = list.IndexOf("-pix_fmt");
        Assert.True(pixIndex >= 0, "必须显式指定像素格式");
        Assert.Equal("yuv420p", list[pixIndex + 1]);
    }

    [Fact]
    public void 中间容器是matroska()
    {
        // 规格 §3.1.4 的实现决策：录制期写 MKV（抗截断），停下再 remux 成 MP4。
        var args = FfmpegCameraCapture.BuildArguments("Cam", @"C:\out\seg.mkv", "libx264").ToList();

        var fIndex = args.IndexOf("-f", args.IndexOf("-i"));
        Assert.True(fIndex >= 0, "输出侧必须显式指定容器");
        Assert.Equal("matroska", args[fIndex + 1]);
    }

    [Fact]
    public void 枚举命令故意让它失败因为退出码在这里没有意义()
    {
        var args = CameraDevices.BuildListArguments();

        Assert.Contains("-list_devices", args);
        Assert.Contains("dshow", args);
        Assert.Contains("dummy", args);
    }
}

/// <summary>
/// 真摄像头端到端：录 → 落盘 → remux → 真实解码校验。
/// </summary>
/// <remarks>
/// 这是 M2 验收第一条「电脑端录一段，产物能被系统播放器直接播放」的自动化形式。
/// 用 <see cref="RequiresCameraFactAttribute"/> 守着：没有摄像头就**跳过并说明**，
/// 绝不在方法体里静默 return。
/// </remarks>
public class FfmpegCameraCaptureIntegrationTests
{
    private static string Ffmpeg => FfmpegLocator.TryFind()!;

    [RequiresCameraFact]
    public async Task 录一段_产物非空且能通过真实解码校验()
    {
        using var dir = new TempDir();
        var device = (await CameraDevices.ListAsync(Ffmpeg))[0];
        var runner = new SystemProcessRunner();

        // 用探测出来的编码器，而不是写死 libx264 —— 这正是 §3.1.5 要求的
        // 「实测本机可用的编码能力，而不是假定」。
        var encoder = await PickEncoderAsync(runner);
        var mkv = dir.File("segment-000.mkv");

        var capture = new FfmpegCameraCapture(Ffmpeg);
        var process = await capture.StartAsync(device, mkv, encoder);

        // dshow 打开设备有 1~1.5 秒延迟，录太短会得到空文件。
        await Task.Delay(TimeSpan.FromSeconds(3));

        var exitCode = await process.StopAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(mkv), "采集必须产出文件");
        Assert.True(new FileInfo(mkv).Length > 0, "产物不能是 0 字节");

        // 真正的判据：remux 后**真解一遍**（规格 §3.1.4），而不是只看文件存在。
        var mp4 = dir.File("out.mp4");
        var remux = await new RemuxPipeline(Ffmpeg, runner).RemuxAsync(mkv, mp4);
        Assert.True(remux.Succeeded, remux.FailureReason);

        var verification = await new DecodeVerifier(Ffmpeg, runner).VerifyAsync(mp4);
        Assert.True(verification.IsPlayable, verification.FailureReason);
    }

    [RequiresCameraFact]
    public async Task 优雅停止留下的MKV是完整的_不是靠杀进程()
    {
        using var dir = new TempDir();
        var device = (await CameraDevices.ListAsync(Ffmpeg))[0];
        var runner = new SystemProcessRunner();
        var encoder = await PickEncoderAsync(runner);
        var mkv = dir.File("graceful.mkv");

        var capture = new FfmpegCameraCapture(Ffmpeg);
        var process = await capture.StartAsync(device, mkv, encoder);

        await Task.Delay(TimeSpan.FromSeconds(3));
        await process.StopAsync(TimeSpan.FromSeconds(15));

        // 实测过：往 stdin 发 q 才会写好 MKV 尾部；杀进程树会丢尾。
        // 所以未经 remux 的**原始 MKV 本身**就该能解码 —— 这条断言正是那个区别。
        var verification = await new DecodeVerifier(Ffmpeg, runner).VerifyAsync(mkv);
        Assert.True(verification.IsPlayable,
            $"优雅停止后的 MKV 应当完整可解：{verification.FailureReason}");
    }

    private static async Task<string> PickEncoderAsync(IProcessRunner runner)
    {
        var probe = await new FfmpegEncoderProbe(Ffmpeg, runner).ProbeAsync();
        var selected = EncoderSelection.Select(probe);

        Assert.NotNull(selected);
        return selected!;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-cam-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
