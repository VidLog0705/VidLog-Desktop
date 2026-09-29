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
        var devices = DshowDevices.ParseVideoDevices(RealOutput);

        // 只该剩摄像头。Alternative name 是同一台设备的另一种写法，
        // 不是第二台设备 —— 不过滤的话一台相机会被数成三个。
        var device = Assert.Single(devices);
        Assert.Equal("Lenovo EasyCamera", device);
    }

    [Fact]
    public void 只挑音频设备_规格3_1_8的麦克风那一半()
    {
        // 与上面那条是**两件事**（2026-09-29 拆开的）：视频那条说的是「别把麦克风
        // 数成摄像头」，这条说的是「麦克风自己数得出来」—— 同一次输出、同一套引号解析，
        // 但漏了任何一半，配置向导的麦克风那一步就会是一个空气下拉。
        var devices = DshowDevices.ParseAudioDevices(RealOutput);

        var device = Assert.Single(devices);
        Assert.Equal("麦克风 (USB Audio Device)", device);
    }

    [Fact]
    public void 没有摄像头时返回空表而不是抛()
    {
        // 「本机没摄像头」是正常的运行环境（上一台开发机就是），不是异常。
        Assert.Empty(DshowDevices.ParseVideoDevices("[dshow @ 0000] \"麦克风\" (audio)"));
        Assert.Empty(DshowDevices.ParseVideoDevices(string.Empty));

        // 反过来也一样：只有摄像头时，麦克风那一半必须是空的 ——
        // 「没有麦克风」和「没有摄像头」都不是异常，只是不同的降级。
        Assert.Empty(DshowDevices.ParseAudioDevices("[dshow @ 0000] \"Cam\" (video)"));
        Assert.Empty(DshowDevices.ParseAudioDevices(string.Empty));
    }

    [Fact]
    public void 同一设备重复出现只算一个()
    {
        var devices = DshowDevices.ParseVideoDevices(
            "[dshow @ 0] \"Cam\" (video)\n[dshow @ 0] \"Cam\" (video)");

        Assert.Single(devices);
    }

    // ─────────────────────────────────────────────
    // 采集命令的拼装
    // ─────────────────────────────────────────────

    [Fact]
    public void 采集命令走dshow并带上设备名与目标路径()
    {
        var args = FfmpegCameraCapture.BuildArguments(CameraSource.Local("Lenovo EasyCamera"), @"C:\out\seg.mkv", "libx264");

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
        var args = FfmpegCameraCapture.BuildArguments(CameraSource.Local("Cam"), @"C:\out\seg.mkv", "libx264").ToList();

        var yIndex = args.IndexOf("-y");
        Assert.True(yIndex >= 0, "采集命令必须有 -y");
        Assert.Equal(@"C:\out\seg.mkv", args[yIndex + 1]);
    }

    [Fact]
    public void 显式把像素格式转成yuv420p()
    {
        // 摄像头出的是 yuyv422，而 libx264 不接受它 —— 不显式转的话编码器直接失败。
        var args = FfmpegCameraCapture.BuildArguments(CameraSource.Local("Cam"), @"C:\out\seg.mkv", "libx264");

        var list = args.ToList();
        var pixIndex = list.IndexOf("-pix_fmt");
        Assert.True(pixIndex >= 0, "必须显式指定像素格式");
        Assert.Equal("yuv420p", list[pixIndex + 1]);
    }

    [Fact]
    public void 中间容器是matroska()
    {
        // 规格 §3.1.4 的实现决策：录制期写 MKV（抗截断），停下再 remux 成 MP4。
        var args = FfmpegCameraCapture.BuildArguments(CameraSource.Local("Cam"), @"C:\out\seg.mkv", "libx264").ToList();

        var fIndex = args.IndexOf("-f", args.IndexOf("-i"));
        Assert.True(fIndex >= 0, "输出侧必须显式指定容器");
        Assert.Equal("matroska", args[fIndex + 1]);
    }

    [Fact]
    public void 枚举命令故意让它失败因为退出码在这里没有意义()
    {
        var args = DshowDevices.BuildListArguments();

        Assert.Contains("-list_devices", args);
        Assert.Contains("dshow", args);
        Assert.Contains("dummy", args);
    }

    // ─────────────────────────────────────────────
    // 音轨（规格 §3.1.8）
    // ─────────────────────────────────────────────

    [Fact]
    public void 没给麦克风时_命令里一个音频参数都没有()
    {
        // ⚠️ 这条守的是「不开声音的人与本次改动之前**逐字一致**」：
        // 多一个 `-c:a` 不会让 ffmpeg 报错，只会在没有音轨时白写一段参数，
        // 而真的危险是**多一个 `-i audio=`** —— 那会让一台没有麦克风的机器
        // 连录像都起不来。
        var args = FfmpegCameraCapture.BuildArguments(CameraSource.Local("Cam"), @"C:\out\seg.mkv", "libx264");

        Assert.DoesNotContain(args, a => a.StartsWith("audio=", StringComparison.Ordinal));
        Assert.DoesNotContain("-c:a", args);
        Assert.DoesNotContain("-ac", args);
    }

    [Fact]
    public void 带麦克风时_音频那一路排在视频之前()
    {
        // ⚠️ 顺序是承重的：麦克风打不开时 ffmpeg 在打开音频设备那一刻就退出，
        // 而摄像头实测要 1~1.5 秒才开得起来。音频排在后的话，
        // 每段开头判定「这一次起来了没有」都要多等一个开相机的时间。
        var args = FfmpegCameraCapture.BuildArguments(
            CameraSource.Local("Cam"), @"C:\out\seg.mkv", "libx264", microphone: "话筒").ToList();

        Assert.Contains("audio=话筒", args);
        Assert.Contains("video=Cam", args);
        Assert.True(
            args.IndexOf("audio=话筒") < args.IndexOf("video=Cam"),
            "音频那一路必须排在视频之前");
    }

    [Fact]
    public void 音轨参数逐字_规格3_1_8的四个数()
    {
        // AAC、单声道、44.1 kHz、64 kbps —— 规格原话。两端必须逐字一致：
        // 一边一套参数的话，同一段素材在两个端上转出来是两个体积，
        // 而「按空间清理」是按体积算的。
        var args = FfmpegCameraCapture.BuildArguments(
            CameraSource.Local("Cam"), @"C:\out\seg.mkv", "libx264", microphone: "话筒").ToList();

        Assert.Equal("aac", args[args.IndexOf("-c:a") + 1]);
        Assert.Equal("1", args[args.IndexOf("-ac") + 1]);
        Assert.Equal("44100", args[args.IndexOf("-ar") + 1]);
        Assert.Equal("64k", args[args.IndexOf("-b:a") + 1]);
    }

    [Fact]
    public void 带麦时输出路径仍紧跟在y之后()
    {
        // 判据与上面那条同源：测试替身靠「-y 后面那个参数就是产物」定位输出文件。
        var args = FfmpegCameraCapture.BuildArguments(
            CameraSource.Local("Cam"), @"C:\out\seg.mkv", "libx264", microphone: "话筒").ToList();

        Assert.Equal(@"C:\out\seg.mkv", args[args.IndexOf("-y") + 1]);
    }

    [Fact]
    public void 空白的麦克风名字视同没有麦克风()
    {
        // 设置文件是可以被手改的，`""` 与 `"   "` 都可能出现 ——
        // 而 `audio=` 后面跟一个空名字会让 ffmpeg 直接失败（就是 I4 禁止的那种）。
        foreach (var blank in new[] { "", "   ", null })
        {
            var args = FfmpegCameraCapture.BuildArguments(
                CameraSource.Local("Cam"), @"C:\out\seg.mkv", "libx264", microphone: blank);

            Assert.DoesNotContain(args, a => a.StartsWith("audio=", StringComparison.Ordinal));
        }
    }

    // ─────────────────────────────────────────────
    // 显式映射（2026-09-29：网络摄像头自带音轨被偷偷录进去）
    // ─────────────────────────────────────────────

    /// <summary>按出现顺序取出每个 <c>-i</c> 后面的输入描述。</summary>
    /// <remarks>
    /// <c>-map N</c> 里的 N 就是**这里的下标**（ffmpeg 按 <c>-i</c> 出现的顺序编号）。
    /// 所以判据不能写死数字，要**算出来** —— 算出来的才挡得住「改了顺序、数字没改」。
    /// </remarks>
    private static List<string> Inputs(IReadOnlyList<string> args)
    {
        var list = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-i")
            {
                list.Add(args[i + 1]);
            }
        }

        return list;
    }

    private static List<string> Maps(IReadOnlyList<string> args)
    {
        var list = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-map")
            {
                list.Add(args[i + 1]);
            }
        }

        return list;
    }

    [Theory]
    [InlineData(false)]   // 没开声音
    [InlineData(true)]    // 开了声音
    public void map指向的输入必须是那一路自己(bool withMicrophone)
    {
        // ⚠️ 这条守的是一个**静默指错输入**的坑：音频那一路刻意排在视频之前
        // （为了让麦克风打不开时快速失败），所以画面是 input 0 还是 1 取决于开没开声音。
        // 写死数字的话，改动输入顺序就会把画面映射成音频那一路 ——
        // 而 ffmpeg 只会报一句难懂的 `Stream map ... matches no streams`。
        var args = FfmpegCameraCapture.BuildArguments(
            CameraSource.Local("Cam"), @"C:\out\seg.mkv", "libx264",
            microphone: withMicrophone ? "话筒" : null).ToList();

        var inputs = Inputs(args);
        var videoIndex = inputs.FindIndex(a => a.StartsWith("video=", StringComparison.Ordinal));

        Assert.Contains($"-map", args);
        Assert.Contains($"{videoIndex}:v:0", Maps(args));

        if (withMicrophone)
        {
            var audioIndex = inputs.FindIndex(a => a.StartsWith("audio=", StringComparison.Ordinal));
            Assert.Equal(0, audioIndex);            // 音频确实排在前面
            Assert.Contains($"{audioIndex}:a:0", Maps(args));
            Assert.DoesNotContain("-an", args);
        }
        else
        {
            // ⚠️ 没开声音 ⇒ **一条音频映射都不许有**，而且要把「不要音频」写死。
            // 少了这一条，网络摄像头自带的那路音轨会被 ffmpeg 的默认选流规则
            // 自动录进产物（2026-09-29 实测到过）。
            Assert.DoesNotContain(Maps(args), m => m.Contains(":a:", StringComparison.Ordinal));
            Assert.Contains("-an", args);
        }
    }

    [Fact]
    public void 网络摄像头自带音轨也不许被偷偷录进去()
    {
        // 这一条与上一条的区别在于**源**：网络地址那一路，对端可能自己推音轨。
        var args = FfmpegCameraCapture.BuildArguments(
            CameraSource.Network("rtsp://h/s"), @"C:\out\seg.mkv", "libx264").ToList();

        var inputs = Inputs(args);
        Assert.Equal(["rtsp://h/s"], inputs);

        Assert.Equal(["0:v:0"], Maps(args));
        Assert.Contains("-an", args);
    }

    // ─────────────────────────────────────────────
    // 旋转 180°（规格 §3.1.7 的 2026-09-28 需求变更：电脑端加方向）
    // ─────────────────────────────────────────────

    [Fact]
    public void 不转时argv里没有旋转滤镜()
    {
        // ⚠️ 默认那一档必须与改动前**逐字一致** —— 装歪的摄像头是少数，
        // 默认转一下会让绝大多数人第一次录出来是倒的。
        var args = FfmpegCameraCapture.BuildArguments(
            CameraSource.Local("Cam"), @"C:\out\seg.mkv", "libx264",
            new RecordingSpec(VideoCodec.H264, VideoResolution.P1080));

        Assert.DoesNotContain(args, a => a.Contains("hflip", StringComparison.Ordinal));
    }

    [Fact]
    public void 旋转排在水印之前_否则水印的字也会被转倒()
    {
        // ⚠️ **顺序是承重的**：水印是压在画面上的字，先烧后转会把字也转 180°。
        // 而「画面正了、水印倒着」这种东西在预览里看不出来（预览不烧水印）。
        var args = FfmpegCameraCapture.BuildArguments(
            CameraSource.Network("rtsp://h/s"), @"C:\out\seg.mkv", "libx264",
            new RecordingSpec(VideoCodec.H264, VideoResolution.P1080),
            watermarkAssPath: @"C:\work\seg.ass", rotate180: true).ToList();

        var filters = args[args.IndexOf("-vf") + 1];

        var scale = filters.IndexOf("scale=", StringComparison.Ordinal);
        var rotate = filters.IndexOf("hflip", StringComparison.Ordinal);
        var ass = filters.IndexOf("ass=", StringComparison.Ordinal);

        Assert.True(scale >= 0 && rotate > scale, $"缩放先于旋转，实际：{filters}");
        Assert.True(ass > rotate, $"⚠️ 旋转必须排在 ass 之前（否则水印的字也是倒的），实际：{filters}");

        // 只有一条 -vf：写两个的话后一个会顶掉前一个，而且不报错。
        Assert.Equal(1, args.Count(a => a == "-vf"));
    }

    [Fact]
    public void 本机设备加旋转时滤镜就是那一对_没有多余的缩放()
    {
        var args = FfmpegCameraCapture.BuildArguments(
            CameraSource.Local("Cam"), @"C:\out\seg.mkv", "libx264",
            new RecordingSpec(VideoCodec.H264, VideoResolution.P1080), rotate180: true).ToList();

        Assert.Equal(FfmpegCameraCapture.RotateFilter, args[args.IndexOf("-vf") + 1]);
    }

    // ─────────────────────────────────────────────
    // 取景识码那一档（与录制必须同向）
    // ─────────────────────────────────────────────

    [Fact]
    public void 取景识码也要转_而且用同一个滤镜()
    {
        // ⚠️ 两边朝向不一致会出现「录出来是正的、识码却要倒着认」（或者反过来），
        // 而那种毛病看起来像「识码坏了」，不会有人想到是方向设置。
        var args = VidLog.Desktop.Core.Camera.ScannerProcess
            .BuildArguments(CameraSource.Local("Cam"), rotate180: true).ToList();

        var filters = args[args.IndexOf("-vf") + 1];

        Assert.Contains(FfmpegCameraCapture.RotateFilter, filters, StringComparison.Ordinal);

        // 几何先定（缩放 → 转方向），再降频、转灰度。
        Assert.True(
            filters.IndexOf("hflip", StringComparison.Ordinal)
                < filters.IndexOf("fps=", StringComparison.Ordinal),
            $"旋转要排在 fps/format 之前，实际：{filters}");
    }

    [Fact]
    public void 取景识码不转时滤镜与改动前逐字一致()
    {
        var plain = VidLog.Desktop.Core.Camera.ScannerProcess
            .BuildArguments(CameraSource.Local("Cam")).ToList();
        var network = VidLog.Desktop.Core.Camera.ScannerProcess
            .BuildArguments(CameraSource.Network("rtsp://h/s")).ToList();

        Assert.Equal("fps=3,format=gray", plain[plain.IndexOf("-vf") + 1]);
        Assert.Equal("scale=640:480,fps=3,format=gray", network[network.IndexOf("-vf") + 1]);
    }

    /// <summary>本机实测（2026-09-29）：拿一个不存在的麦克风名字开一路 dshow 的 stderr 尾部。</summary>
    private const string BadAudioOutput = """
        [in#0 @ 0000029927401440] Could not enumerate audio only devices (or none found).
            Last message repeated 1 times
        [in#0 @ 000002992738ea00] Error opening input: I/O error
        Error opening input file audio=不存在的麦克风.
        Error opening input files: I/O error
        """;

    [Fact]
    public void 报给用户的是提到这个设备名的那一行_不是最后一行()
    {
        // ⚠️ 最后一行 `Error opening input files: I/O error` 是**通用包装话** ——
        // 拿它当原因等于什么都没说，而 I3 要的是「用户看得到才有得治」。
        var picked = FfmpegCameraCapture.PickUsefulLine(BadAudioOutput, "不存在的麦克风");

        Assert.NotNull(picked);
        Assert.Contains("不存在的麦克风", picked);
        Assert.DoesNotContain("I/O error", picked);
    }

    [Fact]
    public void 挑不到提到设备名的那一行时返回null_而不是硬塞最后一行()
    {
        // ⚠️ 这一条是**刻意的**：拿最后一行当原因的话，摄像头坏了也会被说成
        // 「麦克风没能接上」—— 那是两句相反的话，用户会照着它去查一个没坏的东西。
        // 挑不出来就不指认，只报「没起来」。
        Assert.Null(FfmpegCameraCapture.PickUsefulLine("第一行\n第二行\n第三行", "话筒"));
        Assert.Null(FfmpegCameraCapture.PickUsefulLine("", "话筒"));
        Assert.Null(FfmpegCameraCapture.PickUsefulLine("   \n  \n", "话筒"));
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
        var device = (await DshowDevices.ListVideoAsync(Ffmpeg))[0];
        var runner = new SystemProcessRunner();

        // 用探测出来的编码器，而不是写死 libx264 —— 这正是 §3.1.5 要求的
        // 「实测本机可用的编码能力，而不是假定」。
        var encoder = await PickEncoderAsync(runner);
        var mkv = dir.File("segment-000.mkv");

        var capture = new FfmpegCameraCapture(Ffmpeg);
        var process = await capture.StartAsync(CameraSource.Local(device), mkv, encoder);

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
        var device = (await DshowDevices.ListVideoAsync(Ffmpeg))[0];
        var runner = new SystemProcessRunner();
        var encoder = await PickEncoderAsync(runner);
        var mkv = dir.File("graceful.mkv");

        var capture = new FfmpegCameraCapture(Ffmpeg);
        var process = await capture.StartAsync(CameraSource.Local(device), mkv, encoder);

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
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（2026-09-27 实测：会话还在
            // 收尾时删含 `.ass` / `.mkv` 的临时目录）。清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
