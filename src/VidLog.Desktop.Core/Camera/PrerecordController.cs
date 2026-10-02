using System.Diagnostics;
using System.Globalization;
using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Camera;

/// <summary>
/// 待扫期间那一路预录的设置（装配层按设置填，**每次开始待扫时读一次**）。
/// </summary>
/// <param name="Buffer">
/// 缓冲时长（规格 §3.1.3「缓冲时长可配置」）。<see cref="TimeSpan.Zero"/> = 不预录
/// —— 那时这个进程退化成纯识码进程（不写文件、不编码）。
/// </param>
/// <param name="Encoder">开分片那一路用的编码器（与正式录制同一个，见 <c>FfmpegCameraCapture</c>）。</param>
/// <param name="Spec">
/// 录制规格。⚠️ <b>必须与正式录制同一档</b>：采纳下来的那一段会作为**开场段**
/// 成为这次录像的第一个分段，尺寸 / 方向 / 水印都与后面几段一致才对得上。
/// </param>
/// <param name="Directory">分片落哪儿（工作区下的 <c>_prerecord</c>）。</param>
public sealed record PrerecordSetup(
    TimeSpan Buffer,
    string? Encoder = null,
    RecordingSpec? Spec = null,
    string? Directory = null)
{
    /// <summary>这一次待扫要不要真的录一路文件。</summary>
    /// <remarks>
    /// 三个都齐了才录。缺一个就退回纯识码那一档 —— 宁可没有缓冲，
    /// 也不要写出一批采纳不了的半成品（它们的代价是磁盘和相机时间）。
    /// </remarks>
    public bool Records =>
        Buffer > TimeSpan.Zero
        && !string.IsNullOrWhiteSpace(Encoder)
        && !string.IsNullOrWhiteSpace(Directory);
}

/// <summary>
/// 待扫期的编排：**持有相机那一个进程**的起停、识码、以及扫到单号时**取回缓冲那段画面**。
/// </summary>
/// <remarks>
/// <para>
/// 规格 §3.2.1 的第二种识别入口（放到画面里就开录）＋ 规格 §3.1.3 的预录缓冲。
/// <b>两者共用同一个进程</b>，因为相机是独占的（§25 真机实测）——
/// 待扫期间它只能有一个占用者，所以「识码要的灰度帧」与「预录要的文件」
/// 必须是同一个 ffmpeg 的两路输出（§54）。
/// </para>
/// <para>
/// ⚠️ <b>它与正式录制的关系是「交接」</b>：扫到单号 → 停这个进程（**等它真退**，
/// 相机才放得开）→ 裁出最后 N 秒作为开场段 → 起正式采集。
/// 中间那 1.5~1.8 秒（dshow 重开设备）是已知的固定空档，见 <c>docs/实现决策.md</c>。
/// </para>
/// </remarks>
public sealed class PrerecordController : IAsyncDisposable
{
    /// <summary>
    /// 滚动分片的单片时长（秒）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>它只为约束磁盘，不是缓冲窗口</b>：缓冲窗口是
    /// <see cref="PrerecordSetup.Buffer"/>（裁窗口时用 <c>-sseof</c> 从**最新那一片**
    /// 的尾巴上取）。取 120 是「滚得别太勤」与「已知上限」之间的取舍 ——
    /// 刚滚过片时最新那一片可能还不够长（每 120 秒一次机会），这笔账记在
    /// <c>docs/实现决策.md</c> 里。
    /// </remarks>
    public const int ChunkSeconds = 120;

    /// <summary>留几片。3 片 × 120 秒 = 6 分钟，够长（缓冲档位最大 30 秒）。</summary>
    public const int KeepChunks = 3;

    /// <summary>水印字幕的覆盖时长。</summary>
    /// <remarks>
    /// ⚠️ <b>它必须按**进程寿命**给足</b>：水印是 <c>-vf ass=</c> 烧进编码那一次的，
    /// 而 ffmpeg 在**进程启动时**读定那份字幕 —— 一个进程跨很多片，中途换不了。
    /// 给足 12 小时之后仍没扫到的话，后面那一段画面就**没有水印**了
    /// （这是已知上限，见 <c>docs/实现决策.md</c>；分片仍在滚、仍能采纳）。
    /// </remarks>
    private static readonly TimeSpan WatermarkCoverage = TimeSpan.FromHours(12);

    private readonly string _ffmpegPath;
    private readonly CameraSource _source;
    private readonly IFrameScanner? _decoder;
    private readonly IAppLogger _logger;
    private readonly IProcessRunner _runner;
    private readonly ITrustedClock? _trustedClock;
    private readonly SingleSlotPreviewSink? _preview;
    private readonly DecodeGate _gate = new();
    private readonly SingleSlotFrameSink _sink = new();

    private PrerecordProcess? _process;
    private CancellationTokenSource? _loop;
    private PrerecordSetup? _recording;
    private DateTimeOffset? _bufferStartedAt;
    private long _bufferStartedTick;
    private Timer? _sweep;
    private string? _reported;

    /// <summary>
    /// 主窗那幅预览画面的落点；<see langword="null"/> = 不投（测试与不关心界面的装配）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这一路是「顺手」，不是「另开一路输出」</b>：管子里的灰度帧本来就在流
    /// （识码要用），多投一次不多花 ffmpeg 一分力气。代价是这一档的预览是**灰的、3 fps**
    /// （它是识别用的帧，不是为了给人看而生的）。
    /// <para>
    /// ⚠️ 相机在「工作中」的绝大部分时间是被**这个**进程占着的（录制只在扫到单号
    /// 之后那一段）。不投这一路的话，主窗那个取景框在一天里绝大多数时候是空的。
    /// </para>
    /// <para>
    /// ⚠️ 投帧走的是**永不阻塞**的单槽（<see cref="SingleSlotPreviewSink.Publish"/>），
    /// 所以它**不会**把识码读端拖慢 —— 而拖慢读端等于堵住管道，
    /// 那是这台机器上唯一会丢录像的事故（§54.2）。
    /// </para>
    /// </remarks>
    public PrerecordController(
        string ffmpegPath,
        CameraSource source,
        IFrameScanner? decoder,
        IAppLogger logger,
        IProcessRunner runner,
        ITrustedClock? trustedClock = null,
        SingleSlotPreviewSink? preview = null)
    {
        _ffmpegPath = ffmpegPath;
        _source = source;
        _decoder = decoder;
        _logger = logger;
        _runner = runner;
        _trustedClock = trustedClock;
        _preview = preview;
    }

    /// <summary>
    /// 待扫时的方向（规格 §3.1.7）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>必须与录制那一档一致</b>：两边朝向不一致会出现「录出来是正的、
    /// 识码却要倒着认」（或者反过来），而那种毛病看起来像「识码坏了」。
    /// <para>
    /// ⚠️ 做成可写属性而不是 ctor 参数：它读的时刻是**每次开始工作**
    /// （那时才 <see cref="StartAsync"/>），所以「改了下次开始工作生效」
    /// 是这句话本来的语义，不需要重启。
    /// </para>
    /// </remarks>
    public CameraRotation Rotation { get; set; } = CameraRotation.None;

    /// <summary>识别到单号。</summary>
    public event Action<WaybillNumber>? Scanned;

    /// <summary>待扫进程起不来时的原因（I3：不允许静默失效）。</summary>
    public event Action<string>? Failed;

    /// <summary>现在是不是有一个进程在占着相机。</summary>
    public bool IsActive => _process is not null;

    /// <summary>
    /// 开始待扫：起那个持有相机的进程（识码 + 可选预录）。已在跑时什么都不做。
    /// </summary>
    public async Task StartAsync(PrerecordSetup setup, CancellationToken cancellationToken = default)
    {
        if (_process is not null)
        {
            return;
        }

        var recording = setup.Records ? setup : null;
        var startedAt = DateTimeOffset.Now;

        try
        {
            _gate.Reset();

            if (recording is { } wanted)
            {
                // ⚠️ **目录必须先建出来**（2026-10-02 在装出来的 0.2.0 上实测的缺陷）。
                // 工作区那边只给**路径**、从不建目录（`RecordingWorkspace.PrerecordDirectory`
                // 就是一句 `Path.Combine`），于是 `WriteWatermark` 抛
                // DirectoryNotFoundException、ffmpeg 紧接着以
                // `Could not create a libass track … Error opening output files: Invalid argument`
                // 起不来 —— 而这一个 ffmpeg 同时扛着**取景识码**，所以现场看到的是
                // 「预录坏了**和**待扫也不识码了」两件事一起发生。
                // 原来那几条用例抓不到它：它们传的是 `TempDir`（目录已经在了）。
                Directory.CreateDirectory(wanted.Directory!);

                // ⚠️ 上一轮剩下的片**必须清掉**：不清的话「最新那一片」可能是几小时前
                // 那一次待扫留下的，而它会**冒充这一轮的缓冲**被采纳 ——
                // 开场画面因此是一段与这一件包裹毫无关系的画面，且没人看得出来。
                Sweep(wanted.Directory!, keep: 0);

                // 水印锚在**缓冲起点的可信时刻**（I11：不得取自墙钟 ——
                // 没接可信时钟时（测试路径）才退回墙钟，与 `RecordingSession` 同一个口径）。
                startedAt = _trustedClock?.Now ?? DateTimeOffset.UtcNow;
                WriteWatermark(wanted, startedAt);
            }

            _process = await PrerecordProcess.StartAsync(
                _ffmpegPath, BuildArguments(recording), _sink, cancellationToken);

            _recording = recording;
            _bufferStartedAt = recording is null ? null : startedAt;
            _bufferStartedTick = Stopwatch.GetTimestamp();

            if (NeedsFrames)
            {
                _loop = new CancellationTokenSource();
                _ = Task.Run(() => DecodeLoopAsync(_loop.Token), CancellationToken.None);
            }

            if (recording is not null)
            {
                _sweep = new Timer(
                    _ => Sweep(recording.Directory!, KeepChunks),
                    state: null,
                    dueTime: TimeSpan.FromSeconds(ChunkSeconds),
                    period: TimeSpan.FromSeconds(ChunkSeconds));
            }

            _logger.Log(LogLevel.Info, "识码", recording is null
                ? "开始取景识码"
                : $"开始取景识码（预录缓冲 {recording.Buffer.TotalSeconds:0.#} 秒）");
        }
        catch (Exception ex)
        {
            _process = null;
            _recording = null;
            _bufferStartedAt = null;
            _logger.Log(LogLevel.Error, "识码", $"取景识码起不来：{ex.Message}");
            Failed?.Invoke($"摄像头识码起不来：{ex.Message}");
        }
    }

    /// <summary>
    /// 停下并**取回缓冲那一段画面**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>它一定停进程</b>（不管有没有缓冲）：相机是独占的，调用方扫到单号之后
    /// 紧接着就要起正式采集，没放干净的话那边拿到 <c>device already in use</c>。
    /// 所以「停」和「取缓冲」是同一件事的两半，不拆成两个调用点 ——
    /// 拆开就有一天会有人只调了「取」忘了「停」。
    /// </para>
    /// <para>
    /// ⚠️ <b>采纳的是一份**新文件**（裁出来的），不是分片本身</b>：分片还在被 ffmpeg
    /// 持有、而且它是滚动缓冲的一部分（下一轮会被清掉）。裁出来的那份归调用方，
    /// 由它会话目录搬。
    /// </para>
    /// </remarks>
    public async Task<AdoptedClip?> TakeBufferedAsync(CancellationToken cancellationToken = default)
    {
        var recording = _recording;
        var startedAt = _bufferStartedAt;

        // ⚠️ 窗口要在**停之前**算：`StopAsync` 最坏会等到强杀（10 秒），
        // 拿停完之后的时刻去算，会把那 10 秒也算进「已经录了多久」里。
        var window = recording is null || startedAt is null
            ? TimeSpan.Zero
            : Min(recording.Buffer, Stopwatch.GetElapsedTime(_bufferStartedTick));

        await StopAsync(cancellationToken).ConfigureAwait(false);

        if (recording is null || startedAt is not { } bufferStart || window <= TimeSpan.Zero)
        {
            return null;
        }

        var newest = NewestChunk(recording.Directory!);
        if (newest is null)
        {
            _logger.Log(LogLevel.Warn, "预录", "没找到可用的预录分片，这一段没有开场画面");
            return null;
        }

        var destination = Path.Combine(recording.Directory!, $"adopted-{Guid.NewGuid():N}.mkv");

        // `-sseof`（从**结尾**往回数）而不是 `-ss`：免去「先量出这一片多长」这一步。
        // ⚠️ `-c copy` 的裁切是**关键帧对齐**的，精度由 `-g` 决定 ——
        // 所以预录那一路在 argv 里钉了 1 秒一个关键帧（`FfmpegCameraCapture`）。
        var result = await _runner.RunAsync(
            _ffmpegPath,
            [
                "-hide_banner", "-v", "error",
                "-sseof", "-" + window.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", newest,
                "-c", "copy",
                "-y", destination,
            ],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded
            || !File.Exists(destination)
            || new FileInfo(destination).Length == 0)
        {
            _logger.Log(LogLevel.Warn, "预录",
                $"裁缓冲没成（ffmpeg 退出码 {result.ExitCode}），这一段没有开场画面");
            TryDelete(destination);
            return null;
        }

        _logger.Log(LogLevel.Info, "预录",
            $"采纳了 {window.TotalSeconds:0.0} 秒缓冲作为开场（分片 {Path.GetFileName(newest)}）");

        return new AdoptedClip(destination, bufferStart, window);
    }

    /// <summary>停下待扫，释放相机。幂等。</summary>
    /// <remarks>
    /// <b>必须等进程真的退出</b> —— 相机是独占的，没释放干净的话
    /// 紧接着的录制进程会拿到 <c>device already in use</c>。
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null)
        {
            await _loop.CancelAsync();
            _loop.Dispose();
            _loop = null;
        }

        _sweep?.Dispose();
        _sweep = null;

        _recording = null;
        _bufferStartedAt = null;

        var process = _process;
        _process = null;

        if (process is not null)
        {
            var forced = await process.StopAsync(cancellationToken).ConfigureAwait(false);

            // ⚠️ 进程说过话就记一条（I3）：起不来 / 半路死掉的原因**只在它那儿**，
            // 而它一死外面只看到「没有新帧」——那与「画面里就是没有码」长得一模一样。
            // 空的时候不记（正常停下它没有话说）。
            if (process.ErrorTail.Trim() is { Length: > 0 } said)
            {
                _logger.Log(LogLevel.Warn, "识码", $"待扫进程说过：{said}");
            }

            // ⚠️ 强杀也要记（§6.1「不可逆动作要留痕」）：它不是「慢」，是**收了尾才叫停**的
            // 反面 —— 强杀之后 muxer 没写索引，刚滚过去那一片的尾巴就没了（§54.2）。
            if (forced)
            {
                _logger.Log(LogLevel.Warn, "识码",
                    "待扫进程没能优雅停下（只能强杀）—— 最近那一片预录分片的尾部可能缺一点");
            }

            _logger.Log(LogLevel.Info, "识码", "取景识码已停，相机已释放");
        }
    }

    /// <summary>灰度那一路要不要读（识码或预览任一需要）。</summary>
    private bool NeedsFrames => _decoder is not null || _preview is not null;

    /// <summary>
    /// 拼这一次待扫的命令行。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>两种形状都不是在这里拼的</b>：纯识码那一档是
    /// <see cref="PrerecordProcess.ScannerArguments"/>（今天那一份，逐字不变），
    /// 带预录那一档是<see cref="FfmpegCameraCapture.BuildArguments"/>（录制那一份
    /// ＋ 滚动分片 ＋ 灰度那一路）—— 本仓的规矩是 argv **只有一个来源**
    /// （见那个方法的说明：规格探测与真录制走岔过一次，代价是探测没在验它声称要验的东西）。
    /// </remarks>
    private IReadOnlyList<string> BuildArguments(PrerecordSetup? recording)
    {
        if (recording is not { } setup)
        {
            return PrerecordProcess.ScannerArguments(_source, Rotation);
        }

        var directory = setup.Directory!;

        return FfmpegCameraCapture.BuildArguments(
            _source,
            // `-f segment` 的输出是一个**模式**，不是一个文件名。
            Path.Combine(directory, "pre-%03d.mkv"),
            setup.Encoder!,
            setup.Spec,
            watermarkAssPath: Path.Combine(directory, WatermarkFileName),
            // ⚠️ 预录**不接麦克风**：① 规格 §3.1.3 要的是「缓冲期内的**画面**」；
            // ② 麦克风接不上时 ffmpeg 会在打开音频设备那一刻整个退出 ——
            //    而一个起不来的待扫进程等于**识码也一起没了**（今天那一档不需要
            //    降级重开的复杂度，因为那一路的代价太大）。
            microphone: null,
            durationSeconds: null,
            preview: false,
            segmentSeconds: ChunkSeconds,
            grayTap: NeedsFrames);
    }

    private const string WatermarkFileName = "pre.ass";

    /// <summary>
    /// 给这一轮待扫写一份水印字幕（规格 §3.6.2）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>只有时间行，没有单号行</b>：预录时还不知道是哪一个单号
    /// （单号正是扫描的**结果**），而水印是烧进编码那一次的 —— 烧进去就改不了。
    /// 所以开场那几秒的水印比后面几段**少一行**，这是已知取舍。
    /// </para>
    /// <para>
    /// ⚠️ 尺寸取**录制规格**那一对（与 <c>RecordingSession.WriteWatermark</c> 同源），
    /// 否则水印的字号在开场段与后面几段会不一样。
    /// </para>
    /// <para>
    /// 写失败**不是错误**：字幕写不出来（磁盘满、目录没了）时采集照旧要起来 ——
    /// 那几秒没有水印是遗憾，待扫起不来是事故（连识码一起没了）。
    /// </para>
    /// </remarks>
    private void WriteWatermark(PrerecordSetup setup, DateTimeOffset start)
    {
        try
        {
            var (width, height) = setup.Spec?.Size ?? (1280, 720);

            File.WriteAllText(
                Path.Combine(setup.Directory!, WatermarkFileName),
                AssWatermark.Build(start, waybill: string.Empty, WatermarkCoverage, width, height));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log(LogLevel.Warn, "预录", $"预录那一路没有水印（字幕文件写不出来：{ex.Message}）");
        }
    }

    private async Task DecodeLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = _sink.TakeLatest();

            if (frame is null)
            {
                // 没有新帧 —— 睡一小会而不是空转。
                await Task.Delay(30, CancellationToken.None);
                continue;
            }

            // 顺手把这一帧投给主窗的取景框（见 `_preview` 的说明）。
            // ⚠️ 放在识码**之前**：解不出来是常态，而画面该照常更新。
            _preview?.Publish(PreviewFrame.FromGray(frame));

            if (_decoder is null)
            {
                // 识码关着、只要画面。⚠️ **仍然要转到**这里把帧取走 ——
                // 不取的话单槽里永远是同一帧（它只会「留着」，不会堵管道，
                // 但这个循环会白转）。识码关着时这就是个投帧循环。
                continue;
            }

            string? decoded;
            try
            {
                decoded = _decoder.TryDecode(frame);
            }
            catch (Exception)
            {
                // 解不出来是常态。一帧失败绝不能把循环带下去。
                decoded = null;
            }

            var text = _gate.Observe(decoded, Environment.TickCount64);
            if (text is null)
            {
                continue;
            }

            if (!WaybillNumber.TryParse(text, out var waybill, out _))
            {
                // 读出来的东西不是合法单号（别的条码、误读）。不上报，
                // 但闸已经把它记下了 —— 同一个误读不会被反复上报。
                continue;
            }

            _logger.Log(LogLevel.Info, "识码", $"识别到 {waybill!.Value}",
                new Dictionary<string, object?> { ["原始"] = text });

            // ⚠️ **报出去之后这个循环就退了，但 ffmpeg 还在跑**（它在写分片）：
            // 调用方（协调器）拿到单号之后要先停这个进程、取回缓冲，再起正式采集。
            Scanned?.Invoke(waybill);
            return;
        }
    }

    /// <summary>最新那一片的路径。</summary>
    /// <remarks>
    /// ⚠️ **按序号取，不按时间戳取**：`pre-%03d.mkv` 的序号只增不减
    /// （清旧片不影响这一点），而「最后写入时间」在文件正被持有时是启发式的。
    /// </remarks>
    private static string? NewestChunk(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory.EnumerateFiles(directory, ChunkPattern)
            .Select(path => (path, index: ChunkIndex(path)))
            .Where(pair => pair.index >= 0)
            .OrderByDescending(pair => pair.index)
            .Select(pair => pair.path)
            .FirstOrDefault();
    }

    private const string ChunkPattern = "pre-*.mkv";

    /// <summary>从 <c>pre-007.mkv</c> 里取那个序号；认不出返回 -1。</summary>
    private static int ChunkIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        return name.Length > "pre-".Length
            && int.TryParse(name.AsSpan("pre-".Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out var index)
            ? index
            : -1;
    }

    /// <summary>
    /// 只留最新 <paramref name="keep"/> 片，其余删掉。<paramref name="keep"/> 为 0 时全删。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>删除失败不是错误</b>：Windows 上文件正被 ffmpeg 持有时报的可能是
    /// <see cref="UnauthorizedAccessException"/>（2026-09-27 实测），
    /// 而下一轮 sweep 会再来一次。删不掉的后果只是多占一点磁盘。
    /// </para>
    /// <para>
    /// ⚠️ <b>但删掉这件事要留痕</b>（§6.1「不可逆动作」）：被删掉的正是「上一轮那件包裹
    /// 的画面」，而它删掉之后谁也说不清它原本在不在、有几片。
    /// 两档的区别是**要不要每次都说**：开始新一轮待扫那次（<paramref name="keep"/> 为 0）
    /// 是一件事，记 <c>Info</c>；滚动保留那次每 120 秒一次，记 <c>Debug</c> ——
    /// （一来一回一班能刷 300 行，而它每次都一样）。
    /// </para>
    /// <para>
    /// ⚠️ 最外面那个 <see cref="Exception"/> 是**必须的**：这个方法是
    /// <see cref="Timer"/> 的回调，而定时器回调里漏出去的异常会**直接杀掉进程**
    /// （.NET 的既定行为，没有中间人接）。宁可记一条 + 留个目录不干净，
    /// 也不能让「扫个旧分片」把整个录像程序带走。
    /// </para>
    /// </remarks>
    private void Sweep(string directory, int keep)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var chunks = Directory.EnumerateFiles(directory, ChunkPattern)
                .Select(path => (path, index: ChunkIndex(path)))
                .Where(pair => pair.index >= 0)
                .OrderByDescending(pair => pair.index)
                .Skip(keep)
                .ToList();

            var deleted = 0;
            foreach (var (path, _) in chunks)
            {
                if (TryDelete(path))
                {
                    deleted++;
                }
            }

            if (deleted > 0)
            {
                _logger.Log(
                    keep == 0 ? LogLevel.Info : LogLevel.Debug,
                    "预录",
                    keep == 0
                        ? $"开始新一轮待扫，先把上一轮留下的 {deleted} 个预录分片清掉"
                        : $"滚动保留 {keep} 片，清掉 {deleted} 个更旧的分片");
            }
        }
        catch (Exception ex)
        {
            Report($"预录分片扫不动（{directory}：{ex.Message}）—— 旧片会占着磁盘，下一轮再试");
        }
    }

    /// <summary>删一片；删掉了返回 <see langword="true"/>。</summary>
    /// <remarks>删不掉要留痕（删文件不可逆），但同一个原因只报一次 —— 见 <see cref="Report"/>。</remarks>
    private bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 见 Sweep 的说明：删不掉不是错误（下一轮会再来），但**要留痕**。
            Report($"预录那一路的文件删不掉（{Path.GetFileName(path)}：{ex.Message}）—— 它还会占着磁盘，下一轮再试");
            return false;
        }
    }

    /// <summary>
    /// 报一个**会反复出现**的问题：只在「变了」的时候记。
    /// </summary>
    /// <remarks>
    /// 扫盘每 120 秒一次，一个被锁死的文件会报整整一班 —— 而重复的问题刷满日志
    /// 等于把日志变成没有日志（§6.1 的隐蔽要求）。
    /// 去重的键就是那句话本身（带着文件名/原因），所以换一个文件会重新报一次。
    /// </remarks>
    private void Report(string message)
    {
        if (message == _reported)
        {
            return;
        }

        _reported = message;
        _logger.Log(LogLevel.Warn, "预录", message);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
