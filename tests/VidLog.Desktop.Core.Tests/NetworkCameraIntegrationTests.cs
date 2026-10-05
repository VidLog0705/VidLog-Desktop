using System.Diagnostics;
using System.Globalization;
using VidLog.Desktop.Core.Index;
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
/// <para>
/// ⚠️ 在 <see cref="NetworkCameraCollection"/> 里：它那两条录制用例在做 1080p 的
/// x264 实时编码，与预览吞吐那一条并行时会把对方拱红（2026-10-02 实测）。
/// </para>
/// </remarks>
[Collection(NetworkCameraCollection.Name)]
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
        var exitCode = await process.StopAsync(TimeSpan.FromSeconds(20));

        // ⚠️ 退出码这一条**不能省**：录制整段失败时产物压根不存在，下面那个
        // `-map 0:a:0` 探测也会失败 ⇒ `Assert.False(probe.Succeeded)` **照样通过**，
        // 而「没有音轨」这件事一次都没验到（与 PreviewThroughputTests 同一族假绿）。
        Assert.Equal(0, exitCode);
        Assert.True(new FileInfo(mkv).Length > 0, "产物不能是 0 字节");

        var probe = await runner.RunAsync(Ffmpeg,
            ["-hide_banner", "-v", "error", "-i", mkv, "-map", "0:a:0", "-f", "null", "-"]);

        Assert.False(
            probe.Succeeded,
            "「录制声音」关着，产物里却有一条音轨 —— 那是摄像头自带的那一路被自动挑进去了");
    }

    /// <summary>
    /// **T17 真机回归**：连录十分钟，分段的**媒体时长与墙钟耗时要对得上**（= 段边界没有空洞）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 需求方 2026-10-05 给的验收口径原话：**「真机录 10 分钟、各段时长加起来 ≈600 秒、
    /// 抽一处段边界看空洞」**。
    /// </para>
    /// <para>
    /// ⚠️ <b>「空洞」不能用眼睛看</b>：丢的那 1~2 秒是同一台摄像头对着同一个场景少收的几帧，
    /// 画面本身完全正常（静态场景尤其看不出来）。能看见它的是这一条恒等式 ——
    /// <b>媒体总时长 ≈ 墙钟耗时</b>。旧行为（每段收尾再重开一次 ffmpeg）在每段边界丢掉
    /// 重开摄像头的 1~1.5 秒，10 段就是 10~15 秒；改成「一个进程自己滚段」之后两者对齐。
    /// </para>
    /// <para>
    /// ⚠️ 走的是**生产录制那一段**（<see cref="RecordingSession"/> + 真
    /// <see cref="FfmpegCameraCapture"/> + 真 <see cref="SessionFinalizer"/>），
    /// 不是另拼一条 ffmpeg 命令 —— T17 改的正是这两层之间的接缝
    /// （分段交给 ffmpeg 自己滚、<c>-segment_start_number</c> 接着预录那一段往下排）。
    /// ⚠️ 但界面里**没有**填网络摄像头地址的入口（批次 3 才做），所以这一条是**绕开界面**
    /// 直接起 Core 会话；「界面 → 录制」那一路要等批次 3 之后才走得通。
    /// </para>
    /// <para>
    /// ⚠️ <b>2026-10-05 起这条判据不再按源分档</b>：同一天修掉了「MJPEG over HTTP
    /// 的媒体时钟比对端快 14%~28%」那个缺陷（<c>CameraSource.InputArguments</c> 里
    /// 那个 <c>-use_wallclock_as_timestamps 1</c>）⇒ 修好之后**任何**源的媒体时长
    /// 都该与墙钟对齐，这条恒等式于是成了**无条件硬闸**。
    /// 标定值（<see cref="MediaPerSecondAsync"/>）仍然量、仍然写进报告，但它现在只回答
    /// 「这条源自己声明得准不准」，不再决定判据走哪一档 —— 它量的是**不修**时的那个偏差，
    /// 所以反过来也是「修复还生效着吗」的旁证。
    /// </para>
    /// <para>
    /// 时长默认 600 秒；要冒烟就设 <see cref="SecondsVariable"/>（比如 <c>120</c>）。
    /// 量出来的数写进 <see cref="ReportPath"/> —— 通过时也写，因为要拿它当回归证据。
    /// </para>
    /// </remarks>
    [RequiresRtspFact]
    public async Task 真机连录十分钟_分段的媒体时长与墙钟对得上()
    {
        var seconds = int.TryParse(
            Environment.GetEnvironmentVariable(SecondsVariable), out var asked) && asked > 0
                ? asked
                : 600;

        using var dir = new TempDir();
        var runner = new SystemProcessRunner();

        var encoder = EncoderSelection.Select(await new FfmpegEncoderProbe(Ffmpeg, runner).ProbeAsync());
        Assert.NotNull(encoder);

        // 先标定这条源的「媒体秒 / 墙钟秒」……
        var rate = await MediaPerSecondAsync(runner, Source.Address);

        await using var session = new RecordingSession(
            new RecordingWorkspace(dir.File("work")),
            new FfmpegCameraCapture(Ffmpeg),
            new SessionFinalizer(
                new RemuxPipeline(Ffmpeg, runner),
                new DecodeVerifier(Ffmpeg, runner),
                new JsonLinesRecordingIndex(dir.File("index.jsonl")),
                dir.File("archive")),
            new DiskSpaceGuard(new DriveSpaceProbe()),
            Source,
            "device-net",
            RecordingSessionOptions.Default with { SegmentDuration = TimeSpan.FromSeconds(60) });

        // ⚠️ 与刚才那次标定之间要隔一段：那个客户端刚断，对端（单客户端的推流）
        // 要几秒才重新可连 —— 不等的话**这一次连接直接被拒**，整个会话起不来。
        await Task.Delay(TimeSpan.FromSeconds(ListenerRecoverySeconds));

        await session.StartAsync(WaybillNumber.Parse("SF-T17"), encoder!);

        // ⚠️ 秒表从**采集进程起来之后**开始算：`StartAsync` 之前那一段是开摄像头的时间，
        // 它本来就不该在录像里，算进去会让判据白松掉 1~2 秒。
        var wall = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var outcome = await session.StopAsync(StopReason.Manual);
        wall.Stop();

        Assert.True(outcome.Segments.Count > 0, outcome.FailureReason ?? "十分钟一段都没录到");

        // ⚠️ 量的是**成品 MP4**而不是 `work/` 里那个源 MKV：收尾成功后源 MKV 会被
        // **按设计丢掉**（`RecordingSession.StopAsync` 里那句 `DiscardSessionDirectory`，
        // 前提是归档层收下了）。remux 是无损换容器 ⇒ 两者时长同源，只是前者还在。
        var rows = new List<string>();
        var measured = new List<(int Sequence, double Duration)>();
        var total = 0.0;

        foreach (var segment in outcome.Segments.OrderBy(s => s.Source.Sequence))
        {
            var path = segment.PublishedPath ?? segment.Source.SourcePath;
            if (!File.Exists(path))
            {
                rows.Add($"{segment.Source.Sequence:000}\t文件不在：{path}（{segment.FailureReason}）");
                continue;
            }

            var probe = await runner.RunAsync(Ffprobe, [
                "-hide_banner", "-v", "error",
                "-show_entries", "format=duration", "-of", "csv=p=0",
                path,
            ]);

            if (!probe.Succeeded)
            {
                rows.Add($"{segment.Source.Sequence:000}\tffprobe 失败：{probe.StandardError.Trim()}");
                continue;
            }

            var duration = double.Parse(probe.StandardOutput.Trim(), CultureInfo.InvariantCulture);
            total += duration;
            measured.Add((segment.Source.Sequence, duration));
            rows.Add(
                $"{segment.Source.Sequence:000}\t{duration:0.000}s\t"
                + $"{segment.Source.StartedAt:O} ~ {segment.Source.EndedAt:O}\t"
                + $"{new FileInfo(path).Length / 1024 / 1024.0:0.0} MB");
        }

        var stopwatchSeconds = wall.Elapsed.TotalSeconds;

        // ⚠️ **产品自己记的录制窗口**：首段起 → 末段止。末段那个「止」是采集停下
        // **那一刻**取的（`CloseCurrentSegmentAsync` 里先取 `endedAt = Elapsed`、
        // 再放掉采集），所以它**不含收尾**。
        //
        // ⚠️ 这也正是上面那条秒表**不能直接当判据**的原因：秒表从 `StartAsync` 之后起、
        // 到 `StopAsync` **返回**为止，而 `StopAsync` 返回之前要把每一段逐个
        // remux + 解码校验 + 算哈希 + 写索引。2026-10-05 那轮 600 秒实测：
        // 秒表 605.954 秒、会话窗口 600.021 秒 —— **差的 5.933 秒全是收尾**（11 段）。
        // 拿秒表当判据会得到一条**假红**（那一轮就是这么红的）。
        var sessionSeconds = (
            outcome.Segments.Max(s => s.Source.EndedAt)
            - outcome.Segments.Min(s => s.Source.StartedAt)).TotalSeconds;

        var report =
            $"""
            源：{Source.Address}
            请求时长：{seconds}s
            秒表耗时（**含收尾**，别拿它当录制时长）：{stopwatchSeconds:0.000}s
            会话自己记的录制窗口（首段起 → 末段止）：{sessionSeconds:0.000}s
            源的媒体/墙钟比：{rate:0.000}（标定值；1.000 = 源的时间戳与墙钟一致）
            分段数：{outcome.Segments.Count}
            媒体总时长：{total:0.000}s
            差（媒体 − 请求时长）：{total - seconds:+0.000;-0.000}s
            差（媒体 − 会话窗口）：{total - sessionSeconds:+0.000;-0.000}s
            差（媒体 − 秒表，含收尾）：{total - stopwatchSeconds:+0.000;-0.000}s
            全部段都入库：{outcome.Segments.All(s => s.IsPublished)}
            会话状态：{outcome.State}

            段号	时长	起止（收尾登记）	大小
            {string.Join(Environment.NewLine, rows)}
            """;

        await File.WriteAllTextAsync(ReportPath, report);

        // 判据一：收尾那条路要走得通 —— T17 把「每段写一次 manifest」删了，
        // 收尾改成**扫盘 + 按序推算时刻**，这一段就是它的真机验证。
        Assert.True(
            outcome.Segments.All(s => s.IsPublished) && outcome.State == RecordingSessionState.Indexed,
            $"{outcome.FailureReason}{Environment.NewLine}{report}");

        // 判据二：分了两段以上，说明**真的滚过段**（一段到底的话这条恒等式什么都验不到）。
        Assert.True(
            outcome.Segments.Count >= 2,
            $"只录到 {outcome.Segments.Count} 段 —— 没滚过段，这条恒等式等于没验。{report}");

        // 判据三：除最后一段（还在写的时候被停掉）外，每段都该是整整 60 秒。
        foreach (var (sequence, duration) in measured.SkipLast(1))
        {
            Assert.True(
                duration is >= 59.0 and <= 61.0,
                $"第 {sequence} 段长 {duration:0.000} 秒，不是整整 60 秒。{report}");
        }

        // 判据四（**这条才是 T17 的核心**）：媒体总时长要与**录了多久**对得上。
        // 旧行为每段边界丢 1~1.5 秒（重开摄像头），10 段少 10~15 秒 ⇒ 当场红。
        // 允许 5 秒余量是留给「起流到第一帧」与分段关闭抖动，不是留给空洞的。
        //
        // ⚠️ 基准是**请求的时长**（`seconds`），**不是**上面那条秒表 —— 秒表含收尾
        // （见 `sessionSeconds` 那段说明）。报告里三个差都列着，谁在漂一眼能看出来。
        //
        // ⚠️ 从 2026-10-05 起这条是**无条件**的：修掉 MJPEG 那个时钟缺陷之后
        // （`-use_wallclock_as_timestamps 1`），**再歪的源**录出来也该与墙钟对齐
        // —— 那正是那个修复做的事。所以这里不再看 `rate`：源声明得准不准
        // 只写进报告，不放进判据（按源放松的话，修复一旦失效判据会跟着一起松，
        // 那才是真的没人看得出来）。
        Assert.True(
            Math.Abs(total - seconds) <= 5,
            $"媒体总时长与请求的 {seconds} 秒差了 {total - seconds:0.000} 秒 —— 段边界有空洞"
                + $"（或源自身的时钟没被按墙钟对齐；标定值 {rate:0.000}）。{report}");
    }

    /// <summary>
    /// 标定这条源**一秒墙钟产出多少秒媒体**，量的其实是它**自己声明**的帧率准不准。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ MJPEG over HTTP 这类源**自己不带时间戳**，ffmpeg 只能按流里声明的
    /// <c>r_frame_rate</c> 发时间戳，而 <c>mpjpeg</c> 解复用器**写死假定 25fps**、
    /// 对端实际推多少根本不看。2026-10-05 两条真源实测都是「声明 25 / 实际 28~32」
    /// ⇒ 媒体时钟比墙钟快 **14%~28%**，后果是文件**比事件长、播放时慢动作、
    /// 烧进画面的水印时钟比真实时间快**（不是「播快」——方向别记反，那是这条最容易被
    /// 想当然的一处）。
    /// </para>
    /// <para>
    /// ⚠️ <b>这里故意走**裸**的 <c>-i &lt;地址&gt;</c>，两个输入选项都不带</b>：
    /// 不带 <c>-rtsp_transport tcp</c>（那是 <c>CameraSource.IsRtsp</c> 在生产里加的），
    /// 也**不带** <c>-use_wallclock_as_timestamps 1</c>（那是同日修 MJPEG 那个缺陷时
    /// 加进 <c>CameraSource.InputArguments</c> 的）。因为这条要量的**正是「不修时会歪多少」**：
    /// 它现在只进报告、不决定判据，是那处修复**还生不生效**的旁证 ——
    /// 带上修复选项来量，它恒等于 1.000，什么都看不出来。
    /// </para>
    /// <para>
    /// ⚠️ 对 RTSP 源，裸 <c>-i</c> 走的是默认 UDP —— 本机用过的源都是 <c>http://</c>，
    /// 没撞上过；真拿 RTSP 相机跑这条时若标定失败，先看这里。
    /// </para>
    /// </remarks>
    private static async Task<double> MediaPerSecondAsync(IProcessRunner runner, string address)
    {
        // ⚠️ **先打一个丢弃的连接，把对端的积压排掉** —— 这不是可选的。
        //
        // 对端是 `-listen 1` 这类**单客户端**推流时，没人连着的那段时间它照样在采，
        // 帧堆在缓冲里；第一个客户端会被**一次灌一大坨**：2026-10-05 实测，
        // 250 帧（= 10 秒媒体）只用了 **1.556 秒** —— 折算 157fps，而那台摄像头
        // 真实只有 28~32fps。这条函数是「连上就掐表」，不排积压就会把源量成 100fps 以上。
        //
        // ⚠️ 对**多客户端**的源（比如手机那个 App）这一步是白打一次连接，
        // 6 秒的事，换掉一整类假读数。
        await SampleAsync(runner, address);

        // 对端断掉一个客户端之后要几秒才重新可用（实测 2~4 秒，取 6 留余量）。
        // 不等的话第二次连接直接 `Connection refused`。
        await Task.Delay(TimeSpan.FromSeconds(ListenerRecoverySeconds));

        var (media, wall) = await SampleAsync(runner, address);

        // 这次才是真节拍。（修了墙钟打戳之后它应当回到 1.000 附近；
        // 量出来明显偏离 1 说明这条源**自己声明的帧率**是错的 —— 写进报告。）
        return media / wall;
    }

    /// <summary>连一次、采固定一段，返回（媒体时长, 墙钟耗时）。</summary>
    private static async Task<(double Media, double Wall)> SampleAsync(
        IProcessRunner runner, string address)
    {
        var sample = Path.Combine(Path.GetTempPath(), "vidlog-rate-" + Guid.NewGuid().ToString("N") + ".mkv");

        try
        {
            var wall = Stopwatch.StartNew();
            var capture = await runner.RunAsync(Ffmpeg, [
                "-hide_banner", "-v", "error",
                "-i", address,
                "-t", "20", "-c", "copy", "-y", sample,
            ]);
            wall.Stop();

            Assert.True(capture.Succeeded, capture.StandardError);

            var probe = await runner.RunAsync(Ffprobe, [
                "-hide_banner", "-v", "error",
                "-show_entries", "format=duration", "-of", "csv=p=0", sample,
            ]);
            Assert.True(probe.Succeeded, probe.StandardError);

            var media = double.Parse(probe.StandardOutput.Trim(), CultureInfo.InvariantCulture);
            Assert.True(media > 0, "标定没量到媒体时长");

            return (media, wall.Elapsed.TotalSeconds);
        }
        finally
        {
            try { File.Delete(sample); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Ffprobe => Path.Combine(Path.GetDirectoryName(Ffmpeg)!, "ffprobe.exe");

    /// <summary>冒烟时把这一条录短一点（秒）。不设 = 600。</summary>
    private const string SecondsVariable = "VIDLOG_TEST_T17_SECONDS";

    /// <summary>
    /// 单客户端推流（`-listen 1` 这类）断掉一个客户端之后、重新可连所需的秒数。
    /// </summary>
    /// <remarks>2026-10-05 实测：断开后 +2s 没人听、+4s 起听得到。取 6 留余量。</remarks>
    private const int ListenerRecoverySeconds = 6;

    /// <summary>量出来的数落在这里 —— 通过时也写，回归证据要看它。</summary>
    private static string ReportPath =>
        Path.Combine(Path.GetTempPath(), "vidlog-t17-regression.txt");

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
