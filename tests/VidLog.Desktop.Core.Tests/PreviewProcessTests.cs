using System.Diagnostics;
using VidLog.Desktop.Core.Camera;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 取景预览（配置向导第 2/3 步）。
/// </summary>
/// <remarks>
/// ⚠️ 在 <see cref="DshowDeviceCollection"/> 里：它要开**真相机**（下面那条 dshow 用例），
/// 而相机是独占的 —— 与采集那一组并行跑会互相抢设备。
/// </remarks>
[Collection(DshowDeviceCollection.Name)]
public class PreviewProcessTests
{
    // ─────────────────────────────────────────────
    // 命令的拼装（不依赖任何设备）
    // ─────────────────────────────────────────────

    [Fact]
    public void 出一路彩色帧_而且不带音频()
    {
        var args = PreviewProcess.BuildArguments(CameraSource.Local("Cam")).ToList();

        Assert.Equal("rgb24", args[args.IndexOf("-pix_fmt") + 1]);

        // ⚠️ 判据取「**相邻**的一对」，不是「第一个 -f 后面是什么」：
        // 本机设备那一档的 argv 里（`-f dshow`）还有别的 -f，按位置找会找错
        // —— 我第一版就是这么写的，断言实际在测 args[0]。
        Assert.Contains("-f", args);
        Assert.True(
            args.Zip(args.Skip(1)).Any(pair => pair.First == "-f" && pair.Second == "rawvideo"),
            "输出侧必须显式指定 rawvideo");

        // ⚠️ 预览不要声音，而且**必须显式 -an**：网络摄像头自带音轨时，
        // 不给 -map 的话 ffmpeg 会把音频也推进那条管道 —— 而读端按
        // 「每帧 width*height*3」切，多出来的音频字节会让**所有帧错位**（花屏）。
        Assert.Contains("-an", args);
        Assert.Equal("0:v:0", args[args.IndexOf("-map") + 1]);
    }

    [Fact]
    public void 预览尺寸固定_按比例缩放并补黑边()
    {
        var args = PreviewProcess.BuildArguments(CameraSource.Local("Cam")).ToList();
        var filters = args[args.IndexOf("-vf") + 1];

        // ⚠️ **不是** `scale=640:360`：那是拉伸，会把 4:3 的源拉变形 ——
        // 而用户在这一步正靠这个画面判断摄像头摆正了没有。
        Assert.Contains("force_original_aspect_ratio=decrease", filters, StringComparison.Ordinal);
        Assert.Contains($"pad={PreviewProcess.Width}:{PreviewProcess.Height}", filters, StringComparison.Ordinal);

        // 尺寸固定 ⇒ 读端能用固定的帧字节数（裸帧没有容器告诉它宽高）。
        Assert.Equal("12", args[args.IndexOf("-r") + 1]);
    }

    [Theory]
    [InlineData(CameraRotation.None, null)]
    [InlineData(CameraRotation.Left90, "transpose=2")]
    [InlineData(CameraRotation.Right90, "transpose=1")]
    [InlineData(CameraRotation.UpsideDown, "hflip,vflip")]
    public void 预览也要跟方向_而且与录制识码同一处产出(CameraRotation rotation, string? expected)
    {
        // ⚠️ 预览**必须也转**：用户在这一步就是靠看着画面把摄像头摆正的，
        // 预览不转的话他会把一个本来就正的摄像头摆歪。
        var args = PreviewProcess.BuildArguments(CameraSource.Local("Cam"), rotation).ToList();
        var filters = args[args.IndexOf("-vf") + 1];

        if (expected is null)
        {
            Assert.DoesNotContain("transpose", filters, StringComparison.Ordinal);
            Assert.DoesNotContain("hflip", filters, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(expected, filters, StringComparison.Ordinal);

            // ⚠️ **方向排在最前，缩放与补边排最后** —— 2026-10-02 改的，原来反着。
            //
            // 反着（先归一、后转）的话，转 90° 那两档的产出是 360×640，
            // 而读端**按定长切帧**（裸帧没有容器告诉它宽高，`ReadCoreAsync` 写死
            // 640×360×3）：字节数**恰好一样**（转置不改总面积），所以既不报错、
            // 也不会卡住，只是把每一帧**横竖读反** —— 表现出来是一幅斜的糊图，
            // 而「相机是不是摆正了」正是用户看这幅图的唯一目的。
            //
            // 先转再归一之后，**任何方向都落在同一个尺寸上**，读端那条假设才成立。
            Assert.True(
                filters.IndexOf(expected, StringComparison.Ordinal)
                    < filters.IndexOf("scale=", StringComparison.Ordinal),
                $"方向要排在 scale 之前，实际：{filters}");
        }

        // 不论哪个方向，最后两环都是 scale → pad（产出恒为预览尺寸）。
        Assert.True(
            filters.LastIndexOf("pad=", StringComparison.Ordinal)
                > filters.IndexOf("scale=", StringComparison.Ordinal),
            $"pad 要收在最后，实际：{filters}");
    }

    [Fact]
    public void 网络摄像头那一档一个输入侧选项都不带()
    {
        // 与采集、识码两处同一条规矩（RTSP 认不了 -video_size）。
        var args = PreviewProcess.BuildArguments(CameraSource.Network("rtsp://h/s")).ToList();

        Assert.DoesNotContain("-video_size", args);
        Assert.DoesNotContain("dshow", args);
    }

    [Fact]
    public void 灰度帧也能当预览帧_每个亮度抄三份()
    {
        // 取景识码那一档的画面是**顺手**投过来的灰度帧（`CameraFrameScanner`）。
        // 转成 rgb24 是为了让界面**只认一种像素格式** —— 多一条 Gray8 分支
        // 就多一处「尺寸/格式对不上」的静默错位，而那是花屏、不报错。
        var gray = new byte[] { 0, 128, 255, 7 };
        var preview = PreviewFrame.FromGray(new CameraFrame(gray, 2, 2, 1234));

        Assert.Equal(2, preview.Width);
        Assert.Equal(2, preview.Height);
        Assert.Equal(6, preview.Stride);
        Assert.Equal(1234, preview.CapturedAtMs);

        // 三个通道相等，且顺序与灰度一致。
        Assert.Equal(
            new byte[] { 0, 0, 0, 128, 128, 128, 255, 255, 255, 7, 7, 7 },
            preview.Rgb);
    }

    // ─────────────────────────────────────────────
    // ★ 像素级：那条滤镜链真的「按比例 + 补黑边」吗
    // ─────────────────────────────────────────────

    /// <summary>
    /// 用一个 4:3 的合成源跑**同一条滤镜链**，看两侧有没有补出黑边。
    /// </summary>
    /// <remarks>
    /// ⚠️ 值得单独验：`scale` 那句我写的是 <c>force_original_aspect_ratio=decrease</c>，
    /// 而**去掉它**（或写成 `scale=640:360`）的表现是「画面被拉宽」——
    /// 一张会动的预览图看着**完全正常**，只是比例不对，而用户正靠它判断摄像头摆正了没有。
    /// 参数名写错时 ffmpeg 会报错，所以这里验的是**效果**不是拼写。
    /// </remarks>
    [RequiresFfmpegFact]
    public async Task 四比三的源会补出黑边_而不是被拉宽()
    {
        var ffmpeg = FfmpegLocator.TryFind()!;

        // 640×480（4:3）全白源 —— 按比例缩到高 360 时宽只有 480，两侧各留 80 的黑。
        var pixels = await RenderOneFrameAsync(ffmpeg, "640x480");

        var left = pixels[0];                                        // 第 0 行最左
        var middle = pixels[PreviewProcess.Width / 2 * 3];           // 第 0 行正中

        // ⚠️ 4:3 缩到高 360 时宽只有 480 ⇒ **两侧补黑**，所以最左是**黑**。
        // 我第一版把这条写成「期望白」—— 那正是「没补黑边（被拉宽）」的样子，
        // 也就是说**判据写反了之后，它期望的恰好是要防的那个 bug**。
        //
        // ⚠️ 判据用「明 / 暗」而不是精确的 0 / 255：ffmpeg 的 YUV↔RGB 往返
        // 会把纯白变成 253 之类（实测踩到过），写等值的话会因为取整而红 ——
        // 那是测试写得脆，不是实现错了。
        Assert.True(left < 16, $"左侧应当是黑边，实际 {left}");
        Assert.True(middle > 240, $"画面中间应当是亮的，实际 {middle}");

        // 而 16:9 的源正好填满，**两侧不该有明显的黑边**。
        // ⚠️ 判据放宽到「前三个像素里有一个是白的」是**刻意的**：
        // `scale` 的取整会让「恰好填满」差一两个像素，而写死「第 0 个像素必须是白」
        // 会因为那一两个像素而红 —— 那是测试写得脆，不是实现错了。
        var wide = await RenderOneFrameAsync(ffmpeg, "1280x720");

        Assert.True(
            wide[0] > 240 || wide[3] > 240 || wide[6] > 240,
            "16:9 的源不该有明显的黑边");
    }

    /// <summary>
    /// 转 90° 的那两档也照样产出预览尺寸 —— 而且黑边是**转过之后**补的。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这条盯的是「先转还是后转」这个顺序。顺序反了（先归一、后转）产出的是
    /// 360×640，而读端按 640×360 切 —— **字节数恰好一样**（转置不改总面积），
    /// 所以既不报错也不卡住，只是每帧横竖读反，屏幕上是一幅斜的糊图。
    /// <para>
    /// ⚠️ 判据取「转之前会不会正好填满」：16:9 的源先归一就正好填满 640×360
    /// （没有黑边可补），转完自然也没有 —— 于是最左那个像素是**白**的。
    /// 先转再归一才有左右那两条黑边。
    /// </para>
    /// </remarks>
    [RequiresFfmpegFact]
    public async Task 转九十度照样补出黑边_证明是转完再归一()
    {
        var ffmpeg = FfmpegLocator.TryFind()!;

        // 1280×720 先转 ⇒ 720×1280；按比例缩到高 360 时宽只有 202，左右各补 219。
        var pixels = await RenderOneFrameAsync(ffmpeg, "1280x720", CameraRotation.Left90);

        var left = pixels[0];
        var middle = pixels[PreviewProcess.Width / 2 * 3];

        Assert.True(left < 16, $"左侧应当是黑边，实际 {left}");
        Assert.True(middle > 240, $"画面中间应当是亮的，实际 {middle}");
    }

    private const byte ModuleBitmapDark = 0;

    private const byte ModuleBitmapLight = 255;

    /// <summary>用给定的合成源尺寸出一帧预览，返回 RGB24 字节。</summary>
    /// <remarks>
    /// ⚠️ 滤镜链取的是**生产那一份**（<see cref="PreviewProcess.PreviewFilters"/>），
    /// 不是在这儿另抄一条 —— 2026-10-02 改的：原来这里是抄的，于是这几条像素级
    /// 用例验的是一条**没人跑的链子**，链子改了它照样绿（本仓在「测了不跑的东西」
    /// 上已经栽过一次，见 `TrustedClockTests` 里那段）。
    /// </remarks>
    private static Task<byte[]> RenderOneFrameAsync(string ffmpeg, string sourceSize)
        => RenderOneFrameAsync(ffmpeg, sourceSize, CameraRotation.None);

    private static async Task<byte[]> RenderOneFrameAsync(
        string ffmpeg, string sourceSize, CameraRotation rotation)
    {
        var frameSize = PreviewProcess.Width * PreviewProcess.Height * 3;
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var arguments = new List<string>
        {
            "-hide_banner", "-v", "error",
            "-f", "lavfi", "-i", $"color=c=white:s={sourceSize}:d=1",
            "-vf", string.Join(',', PreviewProcess.PreviewFilters(rotation)),
            "-frames:v", "1",
            "-f", "rawvideo", "-pix_fmt", "rgb24",
            "pipe:1",
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        _ = Task.Run(async () => { try { await process.StandardError.ReadToEndAsync(); } catch { } });

        var buffer = new byte[frameSize];
        var offset = 0;
        var stream = process.StandardOutput.BaseStream;

        while (offset < frameSize)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, frameSize - offset));
            if (read <= 0)
            {
                break;
            }

            offset += read;
        }

        process.WaitForExit(10000);

        Assert.Equal(frameSize, offset);
        return buffer;
    }

    // ─────────────────────────────────────────────
    // 真源端到端
    // ─────────────────────────────────────────────

    [RequiresRtspFact]
    public async Task 真网络摄像头_能起来并读到帧_而且能优雅停()
    {
        var sink = new SingleSlotPreviewSink();
        await using var preview = await PreviewProcess.StartAsync(
            FfmpegLocator.TryFind()!,
            CameraSource.Network(Environment.GetEnvironmentVariable(
                RequiresRtspFactAttribute.EnvironmentVariable)!),
            sink);

        // 起流 + 首帧解码要一点时间（吞吐那条用例用的是同样的 2 秒暖机）。
        var deadline = Stopwatch.StartNew();
        PreviewFrame? frame = null;

        while (deadline.Elapsed < TimeSpan.FromSeconds(15) && frame is null)
        {
            await Task.Delay(200);
            frame = sink.TakeLatest();
        }

        Assert.NotNull(frame);
        Assert.Equal(PreviewProcess.Width, frame!.Width);
        Assert.Equal(PreviewProcess.Height, frame.Height);
        Assert.Equal(PreviewProcess.Width * PreviewProcess.Height * 3, frame.Rgb.Length);

        await preview.StopAsync();
    }

    [RequiresRtspFact]
    public async Task 真网络摄像头加方向也能起来()
    {
        // 只验「滤镜链被 ffmpeg 接受」—— 画面转没转对，要人眼对着实景看。
        var sink = new SingleSlotPreviewSink();
        await using var preview = await PreviewProcess.StartAsync(
            FfmpegLocator.TryFind()!,
            CameraSource.Network(Environment.GetEnvironmentVariable(
                RequiresRtspFactAttribute.EnvironmentVariable)!),
            sink,
            CameraRotation.Right90);

        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < TimeSpan.FromSeconds(15) && sink.TakeLatest() is null)
        {
            await Task.Delay(200);
        }

        Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), "带方向的预览起不来");

        // 起不来的话 stderr 尾巴会有话说 —— 这里是空的才正常。
        await preview.StopAsync();
    }

    // ─────────────────────────────────────────────
    // ★ 真**本机**设备那一档（dshow）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 真相机预览：起得来、帧读得动、**停了之后相机真的放开了**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 上面两条真源用例走的是 **RTSP**（`RequiresRtspFact`）。而预览这一档
    /// 在本机设备上**从来没验过**：§54 验的「一路源两路输出」是 **1 fps 灰度**，
    /// 预览出的是 **12 fps 彩色**，两条路径的参数完全不同。
    /// </para>
    /// <para>
    /// ★ <b>最要紧的是最后那一段</b>：DirectShow 相机是**独占**的（§25 实测），
    /// 预览停了之后没释放干净的话，紧接而来的录制会拿到
    /// <c>device already in use</c> —— 而那是**用户刚配完摄像头、点开始工作**的
    /// 那一刻，表现是「刚配好就录不了」。
    /// </para>
    /// </remarks>
    [RequiresCameraFact]
    public async Task 真相机预览_起得来_帧读得动_而且停了之后相机真的放开了()
    {
        var ffmpeg = FfmpegLocator.TryFind()!;
        var video = (await DshowDevices.ListVideoAsync(ffmpeg))[0];
        var sink = new SingleSlotPreviewSink();

        await using var preview = await PreviewProcess.StartAsync(
            ffmpeg, CameraSource.Local(video), sink);

        // 起流 + 首帧要一点时间（与 RTSP 那两条同样给 15 秒）。
        var deadline = Stopwatch.StartNew();
        PreviewFrame? frame = null;

        while (deadline.Elapsed < TimeSpan.FromSeconds(15) && frame is null)
        {
            await Task.Delay(200);
            frame = sink.TakeLatest();
        }

        Assert.NotNull(frame);
        Assert.Equal(PreviewProcess.Width, frame!.Width);
        Assert.Equal(PreviewProcess.Height, frame.Height);
        Assert.Equal(PreviewProcess.Width * PreviewProcess.Height * 3, frame.Rgb.Length);

        // 吞吐：数 5 秒里**真的投出来**多少帧。
        // ⚠️ 不能只数「我取到几帧」—— 取帧是 100ms 一次，而帧是 12 fps，
        // 那样量到的上限就是 10，永远够不着 12。所以要把**丢弃计数**算进去：
        // 投出来的帧 = 我取走的 + 被新帧顶掉的。
        var droppedBefore = sink.DroppedCount;
        var taken = 0;
        var window = Stopwatch.StartNew();

        while (window.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(100);

            if (sink.TakeLatest() is not null)
            {
                taken++;
            }
        }

        var published = taken + (sink.DroppedCount - droppedBefore);
        var fps = published / 5.0;

        // ⚠️ 判据放宽到 8 fps 是**刻意的**：预览要的是「够看」而不是「到 12」，
        // 而真机上第一条流刚起来时相机还在自动曝光，头一两秒本来就慢。
        // 写死 ≥12 会因为那一下而红 —— 那是测试写得脆，不是实现错了。
        Assert.True(fps >= 8, $"预览吞吐只有 {fps:F1} fps（目标 {PreviewProcess.Fps}）");

        await preview.StopAsync();

        // ★ 关键：**紧接着**在同一台相机上开一次真录制。
        // 相机没放开的话这里会失败（`device already in use`）。
        var dir = Path.Combine(
            Path.GetTempPath(), "vidlog-preview-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var mkv = Path.Combine(dir, "after-preview.mkv");

        try
        {
            var runner = new SystemProcessRunner();
            var probe = await new FfmpegEncoderProbe(ffmpeg, runner).ProbeAsync();
            var encoder = EncoderSelection.Select(probe);
            Assert.NotNull(encoder);

            var capture = new FfmpegCameraCapture(ffmpeg);
            var process = await capture.StartAsync(CameraSource.Local(video), mkv, encoder!);

            await Task.Delay(TimeSpan.FromSeconds(3));
            var exit = await process.StopAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(0, exit);
            Assert.True(File.Exists(mkv) && new FileInfo(mkv).Length > 0,
                "预览停了之后录制起不来 —— 相机多半没放开");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不该让测试红（Windows 在文件被持有时报的是 UAA）。
            }
        }
    }
}
