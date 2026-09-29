using System.Diagnostics;
using VidLog.Desktop.Core.Camera;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 取景预览（配置向导第 2/3 步）。
/// </summary>
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

            // 方向排在缩放与补边**之后**：先归一到预览尺寸，再转。
            Assert.True(
                filters.IndexOf(expected, StringComparison.Ordinal)
                    > filters.IndexOf("pad=", StringComparison.Ordinal),
                $"方向要排在 pad 之后，实际：{filters}");
        }
    }

    [Fact]
    public void 网络摄像头那一档一个输入侧选项都不带()
    {
        // 与采集、识码两处同一条规矩（RTSP 认不了 -video_size）。
        var args = PreviewProcess.BuildArguments(CameraSource.Network("rtsp://h/s")).ToList();

        Assert.DoesNotContain("-video_size", args);
        Assert.DoesNotContain("dshow", args);
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

    private const byte ModuleBitmapDark = 0;

    private const byte ModuleBitmapLight = 255;

    /// <summary>用给定的合成源尺寸出一帧预览，返回 RGB24 字节。</summary>
    private static async Task<byte[]> RenderOneFrameAsync(string ffmpeg, string sourceSize)
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

        foreach (var argument in new[]
        {
            "-hide_banner", "-v", "error",
            "-f", "lavfi", "-i", $"color=c=white:s={sourceSize}:d=1",
            "-vf", $"scale={PreviewProcess.Width}:{PreviewProcess.Height}:force_original_aspect_ratio=decrease,"
                + $"pad={PreviewProcess.Width}:{PreviewProcess.Height}:(ow-iw)/2:(oh-ih)/2",
            "-frames:v", "1",
            "-f", "rawvideo", "-pix_fmt", "rgb24",
            "pipe:1",
        })
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
}
