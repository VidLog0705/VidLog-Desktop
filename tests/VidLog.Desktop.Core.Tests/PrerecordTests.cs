using System.Diagnostics;
using VidLog.Desktop.Core.Camera;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// ★ 扫码预录缓冲（规格 §3.1.3）：**待扫那一路上真源，整套机制在不在**。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么必须真跑一次</b>：这一批的架构是「一个进程、一路源、两路输出」
/// （①滚动分片 ②灰度裸帧走 stdout），而 §54 只验过「1 fps 灰度 + 文件」那一档，
/// §62 只验过「12 fps 彩色 + 文件」那一档。**「文件 + 灰度管」这个组合没验过**，
/// 而它正是预录用的那一档 —— 两路输出的产出速率都不一样，不能外推。
/// </para>
/// <para>
/// ⚠️ <b>本机没有 dshow 摄像头</b>（§91.7），所以这一组走**网络源**：
/// 「相机独占」那条限制对网络源不成立，因此这台机器上**只有它能验**。
/// 独占下「停预录 → 起采集」能不能真的拿到相机、那 1.5~1.8 秒空档到底多长，
/// 仍然只有真机知道 —— 见本文件末尾那段边界说明，**别把这里通过说成真机通过**。
/// </para>
/// <para>
/// ⚠️ 在 <see cref="NetworkCameraCollection"/> 里：它们要真连那台手机，
/// 而且每条都在跑 1080p 的 x264 实时编码 —— 并行时互相抢 CPU，量出来的数不作数。
/// </para>
/// </remarks>
[Collection(NetworkCameraCollection.Name)]
public class PrerecordTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PrerecordTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static string Ffmpeg => FfmpegLocator.TryFind()!;

    private static string Source => Environment.GetEnvironmentVariable(
        RequiresRtspFactAttribute.EnvironmentVariable)!;

    // ─────────────────────────────────────────────
    // 场景 ①：端到端 —— 起待扫、分片在滚、灰帧在流、取回的缓冲是一段能解码的开场
    // ─────────────────────────────────────────────

    /// <summary>
    /// 待扫那一路同时出**分片**与**灰帧**，扫到时取回的缓冲是一段**能解码的开场**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 走的是**生产那两个类**（<see cref="PrerecordController"/> +
    /// <see cref="PrerecordProcess"/> + 生产的 argv），不是在这里另拼一份 ——
    /// 另拼一份验的是测试自己的 argv，不是产品的。
    /// </para>
    /// <para>
    /// ⚠️ <b>取回那一段必须真解一遍</b>（<see cref="DecodeVerifier"/>）：它是
    /// <c>-sseof -N -i 分片 -c copy</c> 从**一个还在被写的文件**上裁下来的，
    /// 「文件存在且非空」证明不了它是一段能播的画面 —— 而这一段会作为**开场段**
    /// 写进会话清单（`segment-000.mkv`），坏了就是整次录像的开头坏了。
    /// </para>
    /// <para>
    /// ⚠️ 水印那一档也在这一条里：控制器**每轮待扫都写一份 <c>pre.ass</c>**
    /// 并把它交给 <c>-vf ass=</c>。断言那份字幕真的存在，等于断言这一段产物
    /// 是**烧过水印之后**仍然解得开的（§62 的 PTS 结论在这一档同样要成立）。
    /// </para>
    /// </remarks>
    [RequiresRtspFact]
    public async Task 待扫那一路同时出分片与灰帧_取回的缓冲是一段能解码的开场()
    {
        using var dir = new TempDir();
        var logger = new CapturingLogger();
        var runner = new SystemProcessRunner(logger);
        var encoder = await PickEncoderAsync(runner);

        var sink = new SingleSlotPreviewSink();
        var buffer = TimeSpan.FromSeconds(5);

        await using var controller = new PrerecordController(
            Ffmpeg, CameraSource.Network(Source), decoder: null, logger, runner, preview: sink);

        await controller.StartAsync(new PrerecordSetup(
            buffer, encoder, RecordingSpec.Default, dir.Path));

        Assert.True(controller.IsActive, $"待扫进程没起来：{Text(logger)}");

        // ── 两路都在跑 ──────────────────────────────
        // ⚠️ 分片要等到 ffmpeg 写进第一块（`-flush_packets 1` 让它快得多，但建连
        // 与首帧仍要几秒）。窗口给 20 秒：MJPEG 建连实测 1~3 秒，而交付里这台
        // 测试宿主还会被别的用例压着。
        var start = Stopwatch.StartNew();
        string? chunk = null;

        while (start.Elapsed < TimeSpan.FromSeconds(20) && chunk is null)
        {
            await Task.Delay(200);

            chunk = Directory.EnumerateFiles(dir.Path, "pre-*.mkv")
                .FirstOrDefault(path => new FileInfo(path).Length > 0);
        }

        Assert.NotNull(chunk);

        // ★ 灰度那一管也在流：预览落点收到的是 640×480 的帧（识别用的尺寸，
        // 与批次 A 那个 640×360 彩色预览**不是同一档** —— 见控制器的说明）。
        PreviewFrame? frame = null;
        var frameDeadline = Stopwatch.StartNew();

        while (frameDeadline.Elapsed < TimeSpan.FromSeconds(15) && frame is null)
        {
            await Task.Delay(100);
            frame = sink.TakeLatest();
        }

        Assert.True(
            frame is not null,
            $"两路里只有文件那一路在动 —— 灰度那一管没有帧。日志：{Text(logger)}");

        Assert.Equal(PrerecordProcess.Width, frame!.Width);
        Assert.Equal(PrerecordProcess.Height, frame.Height);
        Assert.Equal(PrerecordProcess.Width * PrerecordProcess.Height * 3, frame.Rgb.Length);

        _output.WriteLine($"首片 {Path.GetFileName(chunk)} 已出现，用时 {start.Elapsed.TotalSeconds:0.0} 秒");

        // 水印字幕真的写出来了（写不出来只记一条 Warn、不拦采集，所以这里要自己看）。
        var ass = Path.Combine(dir.Path, "pre.ass");
        Assert.True(File.Exists(ass) && new FileInfo(ass).Length > 0, "预录那一路没写水印字幕");

        // ── 攒够一点缓冲再取 ────────────────────────
        // ⚠️ 窗口是 `Min(缓冲时长, 已经录了多久)` —— 一起完就取的话窗口接近 0，
        // 那一段会**合法地**取不回来（`TakeBufferedAsync` 返回 null），
        // 而这里要验的是「取回来了」，所以先等它录够。
        await Task.Delay(TimeSpan.FromSeconds(4));

        var clip = await controller.TakeBufferedAsync();

        Assert.False(controller.IsActive, "取完缓冲之后相机必须已经放开（否则正式采集拿不到设备）");

        Assert.True(clip is not null, $"缓冲取不回来。日志：{Text(logger)}");

        Assert.True(File.Exists(clip!.Path), "取回的缓冲必须是一份**落在盘上**的文件");
        Assert.True(new FileInfo(clip.Path).Length > 0, "取回的缓冲是空文件");

        // 窗口就是「缓冲时长」与「已经录了多久」里小的那个（这里两边都约等于 5 秒）。
        Assert.True(
            clip.Duration > TimeSpan.Zero && clip.Duration <= buffer,
            $"取回的窗口 {clip.Duration} 不在 (0, {buffer}] 里");

        var verification = await new DecodeVerifier(Ffmpeg, runner).VerifyAsync(clip.Path);
        Assert.True(verification.IsPlayable, verification.FailureReason);

        // 裁出来的那一段必须**明显短于**它所在的分片 —— 否则 `-sseof` 等于没生效
        // （那会把整片当开场，而整片里混着上一件包裹的画面，没人看得出来）。
        var length = await MeasureAsync(runner, clip.Path);
        var chunkLength = await MeasureAsync(runner, chunk!);

        _output.WriteLine(
            $"取回 {clip.Duration.TotalSeconds:0.0} 秒（窗口）／实量 {length?.TotalSeconds:0.0} 秒；"
            + $"分片 {Path.GetFileName(chunk)} 已录 {chunkLength.GetValueOrDefault().TotalSeconds:0.0} 秒");

        Assert.True(length is not null, "裁出来的那一段读不出时长");

        var cut = length!.Value;

        // ⚠️ 判据给到 1 秒 ~ 窗口+2 秒，是**刻意的宽**：`-c copy` 只能从关键帧切，
        // 而精度由 `-g 30`（1 秒一个）决定 —— 卡死到 0.1 秒会因为关键帧的位置红，
        // 那是测试写得脆，不是实现错了。
        Assert.True(
            cut >= TimeSpan.FromSeconds(1) && cut <= clip.Duration + TimeSpan.FromSeconds(2),
            $"裁出来的长度 {cut.TotalSeconds:0.00} 秒与窗口 {clip.Duration.TotalSeconds:0.0} 秒差太远"
            + " —— 关键帧间隔没钉住（`-g`），或者 `-sseof` 没生效");
    }

    // ─────────────────────────────────────────────
    // 场景 ②：**故意不读**的对照 —— 证明上面那条测得出「堵」
    // ─────────────────────────────────────────────

    /// <summary>
    /// 同样的 argv，**故意不读 stdout**：ffmpeg 会被堵住，连 <c>q</c> 都处理不了。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>没有这一条，上面那条是假绿。</b> 上面断言的是「两路都在动、而且取缓冲
    /// 取得到」—— 而如果「两路都在动」是不管读不读都成立的事，那条断言就什么都没证明。
    /// 这一条把那个「不管怎样」排除掉：**除了读不读，两边的 argv 逐字相同**。
    /// </para>
    /// <para>
    /// ⚠️ 它复现的是 §54.2 真机看到过的现象，而预录这一档**比预览那一档更怕这件事**：
    /// 管道被堵 ⇒ ffmpeg 连 stdin 上的 <c>q</c> 都处理不了 ⇒ 只能强杀 ⇒
    /// **滚动分片的尾部丢掉**，而尾部正是我们要采纳的那几秒。
    /// 所以 <see cref="PrerecordProcess"/> 的读端是「永不阻塞地及时读」。
    /// </para>
    /// <para>
    /// ⚠️ 这里**不能**用 <see cref="PrerecordProcess"/> 自己起（它的读端是生产的一部分，
    /// 一起就在读）；所以照 <c>RecordingPreviewTests</c> 那个对照的做法，
    /// 用**生产那份 argv** 手起一个进程，只是不读它的 stdout。
    /// </para>
    /// </remarks>
    [RequiresRtspFact]
    public async Task 对照_不读那条管子时送q也停不下来()
    {
        using var dir = new TempDir();
        var encoder = await PickEncoderAsync(new SystemProcessRunner());

        using var process = StartPrerecord(
            Path.Combine(dir.Path, "pre-%03d.mkv"), encoder, grayTap: true, durationSeconds: null);

        var drained = Task.Run(async () =>
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
            // 灌满管道要多久：640×480 灰度 @3 fps ≈ 0.9 MB/s，管道缓冲只有几十到几百 KB
            // —— 8 秒是它的一百倍以上，只有真想不出办法堵住才够不着。
            await Task.Delay(TimeSpan.FromSeconds(8));

            // ── 守门人：源真的起来了，这一条才有意义 ──
            Assert.False(process.HasExited, "源没起来它就退了 —— 这条对照证明不了任何事");

            var chunks = Directory.EnumerateFiles(dir.Path, "pre-*.mkv").ToList();
            Assert.True(
                chunks.Any(path => new FileInfo(path).Length > 0),
                "分片是空的 —— 源没起来，这条对照证明不了任何事");

            // ── 正题：送 q，看它理不理 ──
            await process.StandardInput.WriteLineAsync("q");
            await process.StandardInput.FlushAsync();

            Assert.False(
                process.WaitForExit(8000),
                "不读 stdout 它还停得下来 —— 那上面那条「两路都在动、取得到缓冲」"
                + "就不是读端的功劳，这条对照也就没证明「堵」这件事。");
        }
        finally
        {
            Kill(process);
            await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(2)));
        }
    }

    // ─────────────────────────────────────────────
    // 场景 ③：**两路各自限长**（§54.4）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 预录这个**新组合**（文件 + 灰度管）两路都带 <c>-t</c> 时，到点 ffmpeg **自己退出**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 这条盯的是 §54.4 真机踩出来的那个坑：<b><c>-t</c> 是输出选项，不是全局的</b>。
    /// 而这一次多了一个**新的**输出形状（<c>-f segment</c> 那一路）——
    /// 它也是靠 <c>-t</c> 截止的，漏了就是「到点了也不退」。
    /// </para>
    /// <para>
    /// ⚠️ 这一条**必须一边读一边等**：不读的话它会被堵住（场景 ② 刚证明了这一点），
    /// 那「没退出」就分不清是「少了一个 <c>-t</c>」还是「读端没跟上」。
    /// </para>
    /// <para>
    /// ⚠️ 生产路径上预录**不设 -t**（待扫多久就录多久，是真的不限长）；
    /// 这里传它只是为了问「两路会不会各自到点」这件事本身。
    /// </para>
    /// </remarks>
    [RequiresRtspFact]
    public async Task 预录那两路各自限长_到点ffmpeg自己退出()
    {
        using var dir = new TempDir();
        var encoder = await PickEncoderAsync(new SystemProcessRunner());

        const int seconds = 5;
        using var process = StartPrerecord(
            Path.Combine(dir.Path, "pre-%03d.mkv"), encoder, grayTap: true, durationSeconds: seconds);

        var sink = new SingleSlotFrameSink();

        // 生产那一条读端，原样搬过来（`PrerecordProcess` 里用的就是它）。
        var reading = Task.Run(() => new RawGrayFrameReader(
            process.StandardOutput.BaseStream, sink, PrerecordProcess.Width, PrerecordProcess.Height)
            .RunAsync());

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
            // ⚠️ **不送 q** —— 要问的就是「它自己会不会退」。上限给到 30 秒。
            var exited = process.WaitForExit(30000);

            Assert.True(
                exited,
                $"限了 {seconds} 秒而 ffmpeg 没有自己退出 —— 多半是某一路漏了 -t（§54.4）："
                + "网络源永不结束，它就永远等下去");

            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            Kill(process);
            await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(2)));
        }

        // 顺带：限长那一段时间里灰度那一路确实流过 —— 说明它是**真在跑**的，
        // 不是「被 -t 掐掉所以进程才退」。
        Assert.True(
            sink.DroppedCount > 0 || sink.TakeLatest() is not null,
            "灰度那一路一帧都没出 —— 它可能压根就没起来，那这条用例证不出限长的事");

        Assert.True(
            Directory.EnumerateFiles(dir.Path, "pre-*.mkv").Any(path => new FileInfo(path).Length > 0),
            "限长也应当留下分片");
    }

    // ─────────────────────────────────────────────
    // 场景 ④：上一轮的残留分片必须被清掉
    // ─────────────────────────────────────────────

    /// <summary>
    /// 开始待扫时，目录里**上一轮剩下的分片必须全清掉** —— 否则它会冒充这一轮的缓冲。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>这条守的是一个「没人看得出来」的错</b>：不清的话「最新那一片」可能是
    /// 几小时前那一次待扫留下的（序号更大 ⇒ 按序号取就是它），于是开场画面是
    /// **一段与这一件包裹毫无关系的画面**，而录像照录、清单照写、谁都不报错。
    /// 规律是「每开始一轮待扫先全清」（等价于计划里那一步「启动清理残留」，
    /// 但每轮都做 —— 残留多半是**上一轮**留下的，不是上次开机留下的）。
    /// </para>
    /// <para>
    /// ⚠️ 顺带把「按**序号**取最新那一片」也钉住：清完之后新片是 <c>pre-000.mkv</c>，
    /// 而日志里会写明采纳的是哪一片。按「最后写入时间」取的话，正被 ffmpeg 持有的
    /// 文件那个时间戳是启发式的 —— 那种实现在这一条下会飘。
    /// </para>
    /// <para>
    /// ⚠️ <b>120 秒的滚动边界本身没有用例</b>：真跑一次要 4 分钟（`ChunkSeconds` × 2），
    /// 而它只是 ffmpeg 的 <c>-f segment -segment_time</c> 行为，argv 那一条已经在
    /// <c>CameraCaptureTests</c> 里逐字钉住了。这一段里能验的是**清理与选片**。
    /// </para>
    /// </remarks>
    [RequiresRtspFact]
    public async Task 上一轮剩下的分片会被清掉_否则它会冒充这一轮的缓冲()
    {
        using var dir = new TempDir();
        var logger = new CapturingLogger();
        var runner = new SystemProcessRunner(logger);
        var encoder = await PickEncoderAsync(runner);

        // 上一轮的残留：一个序号**更大**的假分片（按序号取必然取到它）。
        var stale = Path.Combine(dir.Path, "pre-007.mkv");
        await File.WriteAllTextAsync(stale, "上一轮留下的，不是视频");

        await using var controller = new PrerecordController(
            Ffmpeg, CameraSource.Network(Source), decoder: null, logger, runner);

        await controller.StartAsync(new PrerecordSetup(
            TimeSpan.FromSeconds(3), encoder, RecordingSpec.Default, dir.Path));

        Assert.True(controller.IsActive, $"待扫进程没起来：{Text(logger)}");

        // ⚠️ 假分片是纯文本，**清不掉的话它会立刻被采纳**（`-sseof` 会报错退非 0，
        // 于是这一段没有开场画面 —— 但那是**碰巧**，不是判据）。所以这里直接断言它没了。
        Assert.False(File.Exists(stale), "上一轮的残留分片没被清掉");

        // 攒够一点再取，好让窗口 > 0。
        await Task.Delay(TimeSpan.FromSeconds(4));

        var clip = await controller.TakeBufferedAsync();

        Assert.True(clip is not null, $"缓冲取不回来。日志：{Text(logger)}");

        var verification = await new DecodeVerifier(Ffmpeg, runner).VerifyAsync(clip!.Path);
        Assert.True(verification.IsPlayable, verification.FailureReason);

        // ★ 「取的是哪一片」写在日志里 —— 那是**按序号取**唯一看得见的落点。
        Assert.Contains("pre-000.mkv", Text(logger), StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────
    // 场景 ⓪：开工前的现场准备 —— **不连真源、不跑 ffmpeg**（本文件里只有这一条）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 交给控制器的预录目录**还不存在**时，它必须自己建出来，并把水印字幕写进去。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>这条是补一个已经装出去、又没人抓到的缺陷</b>（2026-10-02 在装出来的
    /// 0.2.0 上实测）：生产上 <c>RecordingWorkspace.PrerecordDirectory</c> 只给**路径**、
    /// 从不建目录（它就是一句 <c>Path.Combine</c>），于是 <c>WriteWatermark</c> 抛
    /// <c>DirectoryNotFoundException</c>，ffmpeg 紧接着以
    /// 「Could not create a libass track … Error opening output files: Invalid argument」
    /// 起不来 —— 而这一个 ffmpeg 同时扛着**取景识码**，所以现场看到的是
    /// 「预录坏了**和**待扫也不识码了」。
    /// </para>
    /// <para>
    /// ⚠️ <b>上面那几条抓不到它</b>：它们传的是 <see cref="TempDir"/>（目录已经在了）。
    /// 这条故意传一个**不存在的子目录**。
    /// </para>
    /// <para>
    /// ⚠️ 不必真跑 ffmpeg：给一个**不存在**的 exe，<c>StartAsync</c> 会走到「起不来」
    /// 那条路（I3 的既有行为），而**建目录与写水印都发生在起进程之前** —— 那正是要验的。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 预录目录不存在时由控制器自己建出来()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Path, "_prerecord");

        // 前提：它真的不存在。不然这条用例会在「什么都没改」的实现下也绿。
        Assert.False(Directory.Exists(missing));

        var logger = new CapturingLogger();

        await using var controller = new PrerecordController(
            "不存在的-ffmpeg.exe",
            CameraSource.Network("rtsp://192.0.2.1:1/x"),
            decoder: null,
            logger,
            new SystemProcessRunner(logger));

        await controller.StartAsync(new PrerecordSetup(
            TimeSpan.FromSeconds(5), "libx264", RecordingSpec.Default, missing));

        Assert.True(Directory.Exists(missing), $"预录目录没被建出来：{Text(logger)}");
        Assert.True(
            File.Exists(Path.Combine(missing, "pre.ass")),
            $"水印字幕没写出来（预录那一路会因此起不来）：{Text(logger)}");
    }

    /// <summary>按**生产那一条 argv** 起一个预录进程（不经过控制器）。</summary>
    private static Process StartPrerecord(
        string pattern, string encoder, bool grayTap, int? durationSeconds)
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
            CameraSource.Network(Source), pattern, encoder, RecordingSpec.Default,
            watermarkAssPath: null, microphone: null, durationSeconds: durationSeconds,
            preview: false, segmentSeconds: PrerecordController.ChunkSeconds, grayTap: grayTap))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }

    /// <summary>量一个成品文件的容器时长（读不出来返回 <see langword="null"/>）。</summary>
    /// <remarks>
    /// ⚠️ 用 <c>ffmpeg -i</c> 而不是 <c>ffprobe</c>：本仓的部署只保证有 <c>ffmpeg.exe</c>
    /// 一个二进制（见 <c>NetworkCameraProbe</c> 里那段），测试也不该破这条 ——
    /// 解析器复用生产那份（<c>NetworkCameraProbe.ParseStreams</c>），不在这里另写一个。
    /// </remarks>
    private static async Task<TimeSpan?> MeasureAsync(IProcessRunner runner, string path)
    {
        var result = await runner.RunAsync(Ffmpeg, ["-hide_banner", "-i", path]);

        return FfmpegNetworkCameraProbe.ParseStreams(result.StandardError).Duration;
    }

    private static async Task<string> PickEncoderAsync(IProcessRunner runner)
    {
        var probe = await new FfmpegEncoderProbe(Ffmpeg, runner).ProbeAsync();
        var selected = EncoderSelection.Select(probe);

        Assert.NotNull(selected);
        return selected!;
    }

    private static string Text(CapturingLogger logger) =>
        string.Join(" ／ ", logger.Entries.Select(entry => $"[{entry.Level}] {entry.Message}"));

    private static void Kill(Process process)
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
    }

    private sealed class CapturingLogger : IAppLogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void Log(LogLevel level, string category, string message) =>
            Entries.Add((level, message));

        public void Log(
            LogLevel level, string category, string message, IReadOnlyDictionary<string, object?> data) =>
            Entries.Add((level, message));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-prerecord-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

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
