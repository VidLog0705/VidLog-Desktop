using System.Diagnostics;
using VidLog.Desktop.Core.Camera;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// ★ 主窗内联预览（§62）：**录制进程的第二路输出**在真源上到底成不成立。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么必须单独验一次</b>：§54 验过「一路源两路输出」，但那一档是
/// <b>1 fps 灰度</b>（识码用），而预览出的是 <b>12 fps 彩色</b> —— 产出速率差两个
/// 数量级，**不能外推**（§62 里就写着这一条）。
/// </para>
/// <para>
/// ⚠️ <b>本机没有摄像头</b>（§91.7 已记），所以这一组走**网络源**：
/// 相机独占那条限制对网络源不成立，因此这台机器上**只有它能验**。
/// dshow 那一档仍然只有真机知道 —— 见本文件末尾那段边界说明。
/// </para>
/// <para>
/// ⚠️ 在 <see cref="NetworkCameraCollection"/> 里：它们要真连那台手机，
/// 而且每条都在跑 1080p 编码 —— 并行时互相抢 CPU，量出来的数不作数。
/// </para>
/// </remarks>
[Collection(NetworkCameraCollection.Name)]
public class RecordingPreviewTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public RecordingPreviewTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static string Ffmpeg => FfmpegLocator.TryFind()!;

    private static string Source => Environment.GetEnvironmentVariable(
        RequiresRtspFactAttribute.EnvironmentVariable)!;

    /// <summary>预览帧的字节数（尺寸取生产的常量，不在这里各写一份）。</summary>
    private const int FrameBytes = PreviewProcess.Width * PreviewProcess.Height * 3;

    // ─────────────────────────────────────────────
    // 场景 ①：**及时读** —— 预览读得到，而且录制照长、产物可解码
    // ─────────────────────────────────────────────

    /// <summary>
    /// 一边录一边读预览：帧读得到、录制产物完好。
    /// </summary>
    /// <remarks>
    /// ⚠️ 走的是**生产那条路**（<c>FfmpegCameraCapture.StartAsync</c> + 生产 argv），
    /// 不是在这里另拼一份 —— 另拼一份验的是测试自己的 argv，不是产品的。
    /// 本仓在「测了不跑的东西」上已经栽过两次（见 <c>PreviewProcessTests</c> 里那段）。
    /// </remarks>
    [RequiresRtspFact]
    public async Task 边录边读_预览有帧_而且录制产物照长能解码()
    {
        using var dir = new TempDir();
        var runner = new SystemProcessRunner();
        var encoder = await PickEncoderAsync(runner);

        var sink = new SingleSlotPreviewSink();
        var mkv = dir.File("segment-000.mkv");

        var capture = new FfmpegCameraCapture(Ffmpeg, RecordingSpec.Default, sink);
        var process = await capture.StartAsync(CameraSource.Network(Source), mkv, encoder);

        int? exit;

        try
        {
            // 起流 + 首帧（与向导那两条同宽的 15 秒）。
            var deadline = Stopwatch.StartNew();
            PreviewFrame? frame = null;

            while (deadline.Elapsed < TimeSpan.FromSeconds(15) && frame is null)
            {
                await Task.Delay(100);
                frame = sink.TakeLatest();
            }

            Assert.NotNull(frame);
            Assert.Equal(PreviewProcess.Width, frame!.Width);
            Assert.Equal(PreviewProcess.Height, frame.Height);
            Assert.Equal(FrameBytes, frame.Rgb.Length);

            // ★ 画面得是**一直在流**的，不是开头漏了一帧就没了。
            // ⚠️ 数的是「投出来的帧」= 我取走的 + 被新帧顶掉的 —— 只数我取到几帧的话，
            // 上限就是「取帧次数」，永远够不着真帧率（向导那条 dshow 用例踩过这条）。
            var droppedBefore = sink.DroppedCount;
            var taken = 0;
            var window = Stopwatch.StartNew();

            while (window.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(100);

                if (sink.TakeLatest() is not null)
                {
                    taken++;
                }
            }

            var published = taken + (sink.DroppedCount - droppedBefore);
            var fps = published / window.Elapsed.TotalSeconds;

            _output.WriteLine(
                $"录制中的预览：{published} 帧 / {window.Elapsed.TotalSeconds:0.0} 秒"
                + $" = {fps:0.0} fps（目标 {PreviewProcess.Fps}）");

            // ⚠️ 判据压得很低（3 fps）是**刻意的**：这条用例问的是**机制**
            // ——「第二路在产、读端在读、两条输出互不拖垮」，不是吞吐。
            // 吞吐有专门的 `PreviewThroughputTests`，而且实测测试宿主的调度压力
            // 会让读循环掉到 5~6 fps（那次量下来独立进程是 12~13.5）。
            // 在这里卡 12 会因为宿主而红 —— 那是测试写得脆，不是实现错了。
            Assert.True(published >= 9, $"录制中预览只投出 {published} 帧 / 3 秒 —— 第二路没在流");
        }
        finally
        {
            // 送 q 优雅停（同采集那一档：杀进程树会丢 MKV 尾部）。
            // ⚠️ 收尾**只停不断言**：断言写在这里的话，上面某条断言一抛，
            // 这一条会**盖掉**它，报出来的原因是错的（`PreviewThroughputTests`
            // 在收尾与断言的先后上踩过这条）。
            exit = await process.StopAsync(TimeSpan.FromSeconds(20));
        }

        // ★ 送 q 之后它得**自己**停下来。停不下来就是管道被堵、走了强杀，
        // 而强杀会丢 MKV 尾部（§54.2）—— 那正是下面那条解码校验要防的事。
        Assert.Equal(0, exit);

        Assert.True(File.Exists(mkv) && new FileInfo(mkv).Length > 0, "采集必须产出文件");

        // ★ 真正的判据（规格 §3.1.4）：**真解一遍**，不是只看文件存在。
        // 录制期间多了一路输出、多了一个读端，产物完好是这里要守的东西。
        var verification = await new DecodeVerifier(Ffmpeg, runner).VerifyAsync(mkv);
        Assert.True(verification.IsPlayable, verification.FailureReason);
    }

    // ─────────────────────────────────────────────
    // 场景 ②：**故意不读**的对照 —— 证明上面那条测得出「堵」
    // ─────────────────────────────────────────────

    /// <summary>
    /// 同样的 argv，**故意不读 stdout**：ffmpeg 会被堵住，连 <c>q</c> 都处理不了。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>没有这一条，上面那条是假绿。</b> 上面断言的是「读到帧、而且送 q 能停下来」——
    /// 而如果「停下来」是不管读不读都成立的事，那条断言就什么都没证明。
    /// 这一条把那个「不管怎样」排除掉：**除了读不读，两边的 argv 逐字相同**。
    /// </para>
    /// <para>
    /// ⚠️ 它复现的是 §54.2 真机看到过的现象：管道被堵 ⇒ ffmpeg 连 stdin 上的
    /// <c>q</c> 都处理不了 ⇒ 只能强杀 ⇒ **MKV 尾部丢掉**。所以生产侧的读端
    /// 才是「永不阻塞地及时读」（<c>PreviewProcess.ReadFramesAsync</c> 的说明）。
    /// </para>
    /// </remarks>
    [RequiresRtspFact]
    public async Task 对照_不读那一路时ffmpeg真的会被堵住()
    {
        using var dir = new TempDir();
        var encoder = await PickEncoderAsync(new SystemProcessRunner());
        var mkv = dir.File("blocked.mkv");

        using var process = StartCapture(mkv, encoder, preview: true, durationSeconds: null);

        var blocked = Task.Run(async () =>
        {
            try
            {
                // ⚠️ 读**但不处理**：只为了让 stderr 那条管子不满（它不是我们要堵的那条）。
                await process.StandardError.ReadToEndAsync();
            }
            catch
            {
                // 退了就是结束。
            }
        });

        try
        {
            // 灌满管道要多久：第二路是 640×360 rgb24 @12 fps ≈ 8 MB/s，而管道
            // 缓冲只有几十到几百 KB —— 8 秒是它的一百倍以上，只有真想不出办法堵住才够不着。
            await Task.Delay(TimeSpan.FromSeconds(8));

            // ── 守门人：源真的起来了，这一条才有意义 ──
            Assert.False(process.HasExited, "源没起来它就退了 —— 这条对照证明不了任何事");
            Assert.True(
                File.Exists(mkv) && new FileInfo(mkv).Length > 0,
                "产物是空的 —— 源没起来，这条对照证明不了任何事");

            // ── 正题：送 q，看它理不理 ──
            // 读得动的时候这一个字符就能让它干净退出（`FfmpegCaptureProcess` 的说明）。
            await process.StandardInput.WriteLineAsync("q");
            await process.StandardInput.FlushAsync();

            Assert.False(
                process.WaitForExit(8000),
                "不读 stdout 它还停得下来 —— 那上面那条「送 q 能停」就不是读端的功劳，"
                + "这条对照也就没证明「堵」这件事。");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // 已经退了，竞态。
            }

            await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromSeconds(2)));
        }
    }

    // ─────────────────────────────────────────────
    // 场景 ③：**两路输出各自限长** —— 到点 ffmpeg 自己退出（§54.4）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 两路都带 <c>-t</c> 时，到点 ffmpeg **自己退出**，不用谁去送 <c>q</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 这条盯的是 §54.4 真机踩出来的那个坑：<b><c>-t</c> 是输出选项，不是全局的</b>。
    /// 只给文件那一路写 <c>-t</c> 的话，第二路没有终点 —— 网络源永不结束，
    /// 于是到点了 ffmpeg 也不退，**挂到天荒地老**。而规格探测那一步正是在等它自己退出
    /// （等不到就超时、这一次探测作废）。
    /// </para>
    /// <para>
    /// ⚠️ 这一条**必须一边读一边等**：不读的话它会被堵住（场景 ② 刚证明了这一点），
    /// 那「没退出」就分不清是「少了一个 -t」还是「读端没跟上」。
    /// </para>
    /// <para>
    /// ⚠️ 起进程用的是生产的 <c>BuildArguments</c> 与生产的
    /// <c>PreviewProcess.ReadFramesAsync</c>；没有走 <c>StartAsync</c> 只是因为
    /// 那里写死了 <c>durationSeconds: null</c>（真实录制确实不限长）。
    /// </para>
    /// </remarks>
    [RequiresRtspFact]
    public async Task 两路各自限长_到点ffmpeg自己退出()
    {
        using var dir = new TempDir();
        var encoder = await PickEncoderAsync(new SystemProcessRunner());
        var mkv = dir.File("limited.mkv");

        const int seconds = 5;
        using var process = StartCapture(mkv, encoder, preview: true, durationSeconds: seconds);

        var sink = new SingleSlotPreviewSink();

        // 生产那一条读端，原样搬过来。
        var reading = Task.Run(() => PreviewProcess.ReadFramesAsync(
            process.StandardOutput.BaseStream, sink, PreviewProcess.Width, PreviewProcess.Height));

        _ = Task.Run(async () =>
        {
            try
            {
                await process.StandardError.ReadToEndAsync();
            }
            catch
            {
                // 同上。
            }
        });

        try
        {
            // ⚠️ **不送 q** —— 要问的就是「它自己会不会退」。
            // 上限给到 30 秒：5 秒的片子 + 建连与首帧的几秒，够宽了。
            var exited = process.WaitForExit(30000);

            Assert.True(
                exited,
                $"限了 {seconds} 秒而 ffmpeg 没有自己退出 —— 多半是第二路漏了 -t（§54.4）："
                + "网络源永不结束，它就永远等下去");

            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
            }

            await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(2)));
        }

        // 顺带：限长那一段时间里预览确实流过 —— 说明第二路是**真在跑**的，
        // 不是「被 -t 掐掉所以进程才退」。
        var lastFrame = sink.TakeLatest();

        Assert.True(
            lastFrame is not null || sink.DroppedCount > 0,
            "第二路一帧都没出 —— 它可能压根就没起来，那这条用例证不出限长的事");

        Assert.True(File.Exists(mkv) && new FileInfo(mkv).Length > 0, "限长也应当留下产物");
    }

    /// <summary>按**生产那一条 argv** 起一个采集进程（不经过会话层）。</summary>
    private static Process StartCapture(
        string outputPath, string encoder, bool preview, int? durationSeconds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 承重：不重定向 stdin 就没法用 q 优雅停止。
            RedirectStandardInput = true,
        };

        foreach (var argument in FfmpegCameraCapture.BuildArguments(
            CameraSource.Network(Source), outputPath, encoder, RecordingSpec.Default,
            watermarkAssPath: null, microphone: null, durationSeconds: durationSeconds,
            preview: preview))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
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
                System.IO.Path.GetTempPath(), "vidlog-recpreview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不该让测试红（Windows 在文件被持有时报的是 UAA）。
            }
        }
    }
}
