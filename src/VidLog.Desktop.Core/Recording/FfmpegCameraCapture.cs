using System.Diagnostics;
using System.Text;
using VidLog.Desktop.Core.Camera;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 用 ffmpeg 从一路画面源采集（本机 DirectShow 设备，或网络摄像头地址）。
/// </summary>
/// <remarks>
/// 选 ffmpeg 而不是 Media Foundation：本项目**已经**把 ffmpeg 当外部工具用
/// （<see cref="Media.FfmpegLocator"/>、remux、解码校验、编码探测），
/// 采集沿用它是零新依赖。走 MF 要引 Windows 互操作层，还得自己接编码器 ——
/// 而编码能力探测本来就以 ffmpeg 的编码器名为准。
/// <para>
/// ⚠️ 网络摄像头（RTSP）也从这里走：ffmpeg 对两者是同一套流程，
/// 差别只在**输入参数**、以及**尺寸能不能在输入侧指定** ——
/// 那两件事都收在 <see cref="CameraSource"/> 里，本类不重复判断。
/// </para>
/// </remarks>
public sealed class FfmpegCameraCapture : ICameraCapture
{
    /// <summary>
    /// 采集侧缓冲。
    /// </summary>
    /// <remarks>
    /// DirectShow 交帧是突发的，而 H.264 编码不是。缓冲给小了会掉帧
    /// （表现为画面卡顿但时间轴照走）。实测 256M 在 640x480@30 下不丢帧。
    /// </remarks>
    private const string BufferSize = "256M";

    /// <summary>
    /// 滚动分片那一路的关键帧间隔（帧）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它决定了「从片子尾巴上裁最后 N 秒」这件事的**精度**：裁切走 <c>-c copy</c>，
    /// 只能从关键帧切。帧率固定 30（规格 §3.1.7）⇒ 30 帧正好 1 秒。
    /// 改它之前先读 <see cref="BuildArguments"/> 里 <c>segmentSeconds</c> 那一段。
    /// </remarks>
    private const int SegmentKeyframeInterval = 30;

    /// <summary>
    /// 「这一次采集真的起来了没有」的等待上限（规格 §3.1.8）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 正常路径上**用不到这个数**：判据是「产物出现」，而它一出现就返回
    /// （实测约 1.5 秒，就是开一次相机的时间）。这个上限只在「既没有产物、
    /// 进程又没退出」时才等满 —— 那种情况下等满也胜过直接判它成功。
    /// </remarks>
    private static readonly TimeSpan StartupProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>产物的轮询间隔。摄像头开设备期间给 50ms 足够密。</summary>
    private static readonly TimeSpan StartupProbeInterval = TimeSpan.FromMilliseconds(50);

    private readonly string _ffmpegPath;

    /// <summary>采集进程 stderr 的尾部。</summary>
    /// <remarks>
    /// 留它不是为了记日志，是为了两件具体的事：① 判定 <c>device already in use</c>
    /// （换件/重开时要用）；② 采集失败时能把 ffmpeg 真正说的话报给用户，
    /// 而不是一句无话可说的「失败了」（I3）。
    /// </remarks>
    private readonly BoundedTextTail _errorTail = new();

    /// <summary>录制规格（编码已由 <paramref name="encoder"/> 带，这里管尺寸与帧率）。</summary>
    /// <remarks>
    /// 为 <see langword="null"/> 时**不带 <c>-video_size</c> / <c>-framerate</c>**，
    /// 相机用它自己的默认档 —— 那是改动前的行为，也是「启动时那次规格探测失败、
    /// 但总得录得起来」时的兜底。
    /// </remarks>
    private readonly RecordingSpec? _spec;

    /// <summary>
    /// 预览画面的落点；<see langword="null"/> = 不要预览（那就**什么都不加**）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>它是第二路输出，不是第二个进程</b>（§62）：DirectShow 相机独占，
    /// 录制中另起 ffmpeg 开同一台相机只会拿到 <c>device already in use</c>。
    /// 所以画面只能从**正在录的这一路**上分出来。
    /// </para>
    /// <para>
    /// ⚠️ <b>为 <see langword="null"/> 时 argv 与本次改动之前逐字一致</b> ——
    /// 规格探测（<c>FfmpegSpecProbe</c>）、集成测试、以及任何不关心预览的调用点
    /// 都不该因为这件事多背一路管道。
    /// </para>
    /// </remarks>
    private readonly SingleSlotPreviewSink? _preview;

    /// <param name="logger">
    /// 只交给**预览那条读帧循环**用：它退出时记一条（正常关管 / 中途中断）。
    /// ⚠️ 不传就等于「预览黑掉时日志里不会有任何东西」—— 而录制中黑屏正是
    /// 2026-10-10 报上来的那条现象，所以**生产装配处必须传**
    /// （`AppHost` 两处都传了；测试与探测用不到就不传）。
    /// </param>
    public FfmpegCameraCapture(
        string ffmpegPath, RecordingSpec? spec = null, SingleSlotPreviewSink? preview = null,
        IAppLogger? logger = null)
    {
        _ffmpegPath = ffmpegPath;
        _spec = spec;
        _preview = preview;
        _logger = logger;
    }

    /// <summary>
    /// 预览帧的**第二个去处**（录制期识码用，见 <see cref="Camera.RecordingRecognition"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>它只是「顺带看一眼」，不是第二个消费者</b>：帧先照原样投进
    /// <see cref="SingleSlotPreviewSink"/>，这条回调在**之后**才被叫 ——
    /// 所以设不设它，主窗那幅画面的通路**一个字节都不变**。
    /// </para>
    /// <para>
    /// ⚠️ <b>它跑在管道读端那条线程上</b>，实现里只许做永不阻塞的事：
    /// 拖慢读端等于堵住管道，那是这台机器上唯一会丢录像的事故（§54.2）。
    /// </para>
    /// </remarks>
    public Action<PreviewFrame>? FrameObserver { get; set; }

    /// <summary>预览读帧循环的日志（可空，见构造函数那个参数）。</summary>
    private readonly IAppLogger? _logger;

    public async Task<ICaptureProcess> StartAsync(
        CameraSource source,
        string outputPath,
        string encoder,
        string? microphone = null,
        CancellationToken cancellationToken = default,
        int? segmentSeconds = null,
        int segmentStartNumber = 0)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 水印字幕（规格 §3.6.2）：**按约定**从输出路径推同一个名字。
        // 文件不在时 ffmpeg 会报错起不来 —— 所以只在它真的存在时才带上，
        // 让「没有水印」比「录不起来」先发生（会话那边写失败也是这个口径）。
        //
        // ⚠️ 滚段时 `outputPath` 是**模式**，推出来的也是模式
        // （`segment-%03d.mkv` → `segment-%03d.ass`）—— 一**整场**一份字幕，
        // 这正是 T17 要的（水印的秒针必须跨段接着走，不能每段跳回去）。
        var watermark = AssWatermark.PathFor(outputPath);
        var assPath = File.Exists(watermark) ? watermark : null;

        var wanted = string.IsNullOrWhiteSpace(microphone) ? null : microphone;
        var process = Launch(source, outputPath, encoder, wanted, assPath, segmentSeconds, segmentStartNumber);

        var warning = await ConfirmStartedAsync(
            process, ProducedPath(outputPath, segmentSeconds, segmentStartNumber), wanted, cancellationToken);

        if (warning is not null)
        {
            // ⚠️ 规格 §3.1.8 的降级：**照常录视频，只是这一段没有音轨**。
            // 重开、而不是把这一段的失败交给上层 —— I4 明令「绝不允许音频把
            // 整段录制弄失败」，而一段的失败在这里就等于**整场录像从这一段起全丢**
            // （每一段都会以同样的方式再死一遍）。
            //
            // ⚠️ 重开时**方向照旧**（它在 spec 里，与音频无关）：去掉音频是为了救
            // 这一段的画面，而画面该怎么转是用户的设置。
            //
            // ⚠️ 重开**覆盖**已经写出来的那几片（同一个模式、同一个起始号、带 `-y`）
            // —— 那正是要的：试出来的那几秒没有音轨，留着它只会让第一片的时间轴
            // 比实际长。相机的重开速度远快于「第一片攒够一块」，所以那些片本来就
            // 几乎是空的。
            Kill(process);
            process = Launch(source, outputPath, encoder, microphone: null, assPath, segmentSeconds, segmentStartNumber);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new FfmpegCaptureProcess(process, () => _errorTail.ToString(), warning);
    }

    /// <summary>
    /// 「起来了没有」要看的那个**具体文件**。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>滚段时不能直接看 <paramref name="outputPath"/></b>：那是个模式
    /// （<c>segment-%03d.mkv</c>），盘上永远不会有这么一个文件 ⇒
    /// <c>Produced()</c> 恒假 ⇒ <b>接了麦克风时每一次都被判「没起来」</b>，
    /// 被降级成无声重录（规格 §3.1.8 要的音轨等于没生效）。
    /// 所以把 `%03d` 换成起始号，看的还是「产物出现」这个判据本身。
    /// </remarks>
    private static string ProducedPath(string outputPath, int? segmentSeconds, int startNumber) =>
        segmentSeconds is null
            ? outputPath
            : outputPath.Replace(
                "%03d",
                startNumber.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

    /// <summary>起一个 ffmpeg 采集进程，并把两条管道排空。</summary>
    private Process Launch(
        CameraSource source, string outputPath, string encoder, string? microphone,
        string? watermarkAssPath, int? segmentSeconds = null, int segmentStartNumber = 0)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 承重：不重定向 stdin 就没法用 q 优雅停止（见 FfmpegCaptureProcess 的说明）。
            RedirectStandardInput = true,

            // ⚠️ 承重：ffmpeg 写的是 UTF-8，不给这一条就是按控制台码页（中文 Windows
            // = 936）读。stderr 尾部有两个**按内容判定**的用处 ——
            // `device already in use` 那类文本，以及 `PickUsefulLine` 拿**设备名**
            // 去认「这一路是不是麦克风的锅」—— 读错编码就两个都认不出来。
            // 详见 `DshowDevices.ListAllAsync` 那一段的实测记录。
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in BuildArguments(
            source, outputPath, encoder, _spec, watermarkAssPath, microphone,
            preview: _preview is not null, durationSeconds: null,
            segmentSeconds: segmentSeconds, segmentStartNumber: segmentStartNumber))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();

        // 两条管道都必须**无条件排空**，且**读端绝不接受可取消的读**。
        //
        // 这里踩过一个会丢录像的坑：原来写的是
        //     _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        // 一旦调用方传进来一个真会取消的 token，读端就停了 → 管道满 →
        // ffmpeg 写不进去 → 它连 stdin 上的 q 都处理不了 → StopAsync 超时 →
        // 只能强杀 → **MKV 尾部丢掉**。今天侥幸没出事，只是因为调用方一直传 None。
        // 停机只能走 q（FfmpegCaptureProcess 里那条唯一路径），不能靠取消读。
        //
        // ⚠️ 要预览时 stdout 走的是**裸帧读端**（不是文本排空）——
        // 同一条管子，同一份「必须及时读、永不阻塞」的责任，见 §54.3。
        if (_preview is { } preview)
        {
            _ = Task.Run(() => PreviewProcess.ReadFramesAsync(
                process.StandardOutput.BaseStream, preview,
                PreviewProcess.Width, PreviewProcess.Height, _logger, onFrame: FrameObserver));
        }
        else
        {
            _ = Task.Run(() => DrainAsync(process.StandardOutput, sink: null));
        }

        // stderr 同样没人读的话，长时间录制里一次异常刷屏就能把它灌满，后果同上。
        // 但它有内容价值（`device already in use` 这类判定文本、诊断包素材），
        // 所以排进一个有上限的环形缓冲，而不是丢掉。
        _ = Task.Run(() => DrainAsync(process.StandardError, _errorTail));

        return process;
    }

    /// <summary>
    /// 这一次采集真的起来了没有。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 规格 §3.1.8 的硬约束：判据必须是「**这一路真的接起来了没有**」，
    /// 而不是「设置里开着没有」。麦克风被别的程序占着、被系统权限拒掉、
    /// 或者 USB 中途掉了，都表现为 ffmpeg 在**写出任何东西之前**整个退出。
    /// </para>
    /// <para>
    /// ⚠️ <b>判据取的是成功的证据（产物出现），不是「等它死」</b>：ffmpeg 要先把
    /// 所有 <c>-i</c> 打开才会建输出文件，所以「产物出现」就等价于「每一路都接上了」。
    /// 反过来等它死则**等不准** —— 开一次相机实测要 1~1.5 秒，死的时刻因此可能晚于
    /// 任何写死的等待窗口，等不到就漏判，而漏判的后果是整场录像全丢。
    /// </para>
    /// <para>
    /// ⚠️ 也不能写成规格里点名的那条陷阱「等音频那一路准备好再开段」：麦克风被拒时
    /// 那个等待**永远等不到**，第一段根本开不了。这里等的是产物，不是音频。
    /// </para>
    /// </remarks>
    /// <returns>起来了返回 <see langword="null"/>；否则返回一句给用户看的话。</returns>
    private async Task<string?> ConfirmStartedAsync(
        Process process, string outputPath, string? microphone, CancellationToken cancellationToken)
    {
        // 没要音频 ⇒ 行为与本次改动**逐字一致**：不等、不判、不重开。
        if (microphone is null)
        {
            return null;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // 取消不是「没起来」。交给调用方最后那句 ThrowIfCancellationRequested。
                return null;
            }

            if (Produced(outputPath))
            {
                return null;
            }

            if (HasExited(process))
            {
                return Describe(microphone);
            }

            if (clock.Elapsed >= StartupProbeTimeout)
            {
                // 措辞与 Describe 同一条口径：没有证据就不指认麦克风。
                return "采集迟迟没有开始（麦克风可能没能就绪），这一段改成没有音轨重录了。";
            }

            try
            {
                await Task.Delay(StartupProbeInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    /// <summary>产物已经落盘且有内容 —— 这就是「每一路都接上了」的证据。</summary>
    private static bool Produced(string outputPath)
    {
        try
        {
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件正被 ffmpeg 持有而读不到属性 —— 那是「它正在写」，不是「没有」。
            return true;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void Kill(Process process)
    {
        try
        {
            if (!HasExited(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// 把这一路没起来的原因说成一句给用户看的话。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>只有 ffmpeg 那句话真的提到这个麦克风时，才把锅算到它头上。</b>
    /// 否则摄像头坏了也会被说成「麦克风没接上」—— 那是两句相反的话，
    /// 而用户会照着它去查一个没坏的东西。
    /// </para>
    /// <para>
    /// ⚠️ <b>「提到设备名」这个判据仍然要看 ffmpeg 的原话，但原话不进这句话。</b>
    /// 界面提示必须是中文（需求方 2026-09-29 写死），所以只保留中文结论；
    /// ffmpeg 的原话进日志与诊断包（<c>SystemProcessRunner</c> 已经记了 stderr）。
    /// 换句话说 <see cref="PickUsefulLine"/> 从「挑出要展示的那句话」降级成
    /// **判据**（是不是这一路的锅），输出一个字都不用它。
    /// </para>
    /// <para>
    /// ⚠️ 判据取「提到设备名的那一行」而不是最后一行：2026-09-29 实测，一次麦克风
    /// 打不开的输出尾部长这样，最后一行是**通用的包装话**，什么都没说。
    /// <code>
    /// [in#0 @ …] Could not enumerate audio only devices (or none found).
    /// [in#0 @ …] Error opening input: I/O error
    /// Error opening input file audio=不存在的麦克风.
    /// Error opening input files: I/O error     ← 最后一行
    /// </code>
    /// </para>
    /// </remarks>
    private string Describe(string microphone) =>
        PickUsefulLine(_errorTail.ToString(), microphone) is not null
            ? "麦克风没能接上，这一段没有音轨。"
            // 没提到麦克风 ⇒ 这一路没起来的原因**不是它**（多半是摄像头），
            // 所以只说「没起来」。真正的原因会由重开那一次的退出码报出来。
            : "采集没能起来，这一段改成没有音轨重录了。";

    /// <summary>
    /// ffmpeg 的输出里有没有**提到这个设备**的那一行。
    /// </summary>
    /// <remarks>
    /// 抽成静态的纯函数、并跟着 <see cref="BuildArguments"/> 一起 <c>public</c>，
    /// 只为一件事：让「判得对不对」**不依赖麦克风**被断言到 ——
    /// 本机一台 dshow 设备都没有（实测 2026-09-29），拆不开就没法验。
    /// <para>
    /// 返回**那一行文本**而不是 <c>bool</c>：判据是「有没有」，但调用方想知道的是
    /// 「是哪一句」的时候（诊断、日志）也能用；而 <see cref="Describe"/> 只用它判空。
    /// </para>
    /// </remarks>
    public static string? PickUsefulLine(string errorTail, string microphone) =>
        errorTail
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .LastOrDefault(line => line.Contains(microphone, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 麦克风那一路的**输入**参数（<c>-i</c> 以及它之前那些选项）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>抽出来是因为有两个地方要它</b>：录制时拼采集命令，和配置向导第 5 步
    /// 「选择麦克风」那条**音量条**（那个要单独开一个 ffmpeg 读电平）。
    /// 抄两份的话，两份迟早会有一份漏掉 <c>-rtbufsize</c> 之类 ——
    /// 而漏掉的表现是「偶尔丢样本」，很难查。
    /// </para>
    /// <para>
    /// ⚠️ 麦克风**永远走 dshow**（本机的音频设备），与画面那一路是什么源无关 ——
    /// 网络摄像头也接本机麦克风。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> AudioInputArguments(string microphone, string bufferSize) =>
    [
        "-f", "dshow",
        "-rtbufsize", bufferSize,
        "-i", $"audio={microphone}",
    ];

    /// <summary>把管道读干。读到流结束为止，异常吞掉（进程退了就是结束，不是错误）。</summary>
    private static async Task DrainAsync(StreamReader reader, BoundedTextTail? sink)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                sink?.Append(buffer, read);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 进程被收掉时管道会断。这不是错误，只是没有更多输出了。
        }
    }

    /// <summary>
    /// 拼出采集命令。
    /// </summary>
    /// <remarks>
    /// 单独抽出来是为了让「参数拼得对不对」能**不依赖摄像头**被断言到。
    /// 集成测试需要真设备，而参数写错时集成测试只会表现为「录不出来」，
    /// 说不清是设备的问题还是参数的问题。
    /// <para>
    /// <c>-y</c> 后面紧跟输出路径 —— 测试替身靠这个位置定位产物，
    /// 换顺序会让一批测试静默失效。
    /// </para>
    /// </remarks>
    /// <param name="spec">
    /// 录制规格（规格 §3.1.7）。为 <see langword="null"/> 时不带尺寸与帧率 —— 见那个字段的说明。
    /// </param>
    /// <param name="watermarkAssPath">
    /// 水印字幕文件的路径（规格 §3.6.2）。为 <see langword="null"/> 时不烧水印
    /// （那些不关心它的调用点与测试）。
    /// <para>
    /// ⚠️ <b>必须在采集这一次就烧进去</b>：收尾是 <c>-c copy</c> 的 remux，
    /// 那一步加不了滤镜（加了就得重编码，违反「不转码」）。
    /// </para>
    /// </param>
    /// <param name="durationSeconds">
    /// 只录这么多秒就自己停（<c>-t</c>）。**只有规格探测用它**（规格 §3.1.7 要「真录 1 秒」）。
    /// <see langword="null"/> = 一直录到我们叫停。
    /// </param>
    /// <param name="preview">
    /// 要不要多出一路**预览画面**（裸 rgb24 走 stdout，见 §62）。
    /// 默认 <see langword="false"/> —— 那时 argv 与本次改动之前**逐字一致**。
    /// </param>
    /// <param name="segmentSeconds">
    /// 非 <see langword="null"/> 时，文件那一路改成**滚动分片**（<c>-f segment</c>，
    /// <paramref name="outputPath"/> 因此是一个**模式**，如 <c>pre-%03d.mkv</c>），
    /// 单片的秒数就是它。规格 §3.1.3 的预录缓冲用它，T17 起按时长滚段也用它。
    /// <see langword="null"/>（默认）= 今天那种「一个输出路径一个文件」，argv 逐字不变。
    /// </param>
    /// <param name="segmentStartNumber">
    /// 第一片从几号起（<c>-segment_start_number</c>）。只在
    /// <paramref name="segmentSeconds"/> 非空时有意义。
    /// <para>
    /// ⚠️ <b>它不是「让人看着舒服」的可选项</b>：采纳了预录缓冲时（规格 §3.1.3）
    /// 会话目录里**已经躺着** <c>segment-000.mkv</c>（那几秒预录画面），
    /// 而 ffmpeg 默认从 0 起、这个输出又带 <c>-y</c> —— 2026-10-05 本机实测：
    /// 不加这个参数，开场那几秒会被**直接覆盖**，退出码 0、没有任何报错。
    /// </para>
    /// </param>
    /// <param name="frameTap">
    /// 要不要多出一路**彩色裸帧**（640×480，待扫的取景框与识码共用）。
    /// 与 <paramref name="preview"/> **不能同时为真**（都要 stdout），同时给会抛异常。
    /// </param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>方向不用单独传</b>：它在 <paramref name="spec"/> 里
    /// （<see cref="RecordingSpec.Rotation"/> 与 <c>RotationFilter</c>）——
    /// 而规格探测共用这个方法，所以「探测的组合」连方向都自动与录制一致。
    /// </para>
    /// <para>
    /// ⚠️ <b>规格探测必须走这个方法，不许自己拼一份 argv。</b>
    /// 2026-09-29 修掉的就是这条：探测原来自己拼了一份，把 <c>-video_size</c> 写在了
    /// <c>-i</c> **之后** —— 真 ffmpeg 实测那是**输出侧**选项，于是它被**静默忽略**
    /// （让它录 1280×720，产物是 320×240），退出码 0、没有任何报错。
    /// 后果是探测**没在验它声称要验的东西**：用户选 4K 而相机不支持时，探测报「通过」，
    /// 接着真录制（那边是对的）打不开设备、整段录不出来 —— 正是探测本该拦住的。
    /// 拼一份就等于把「探测的组合」与「录制的组合」变成两件事，它们迟早会走岔。
    /// </para>
    /// <para>
    /// ⚠️ <b>滤镜链的顺序是承重的</b>：<c>scale</c> → <c>方向</c> → <c>ass</c>。
    /// 每一环排在哪儿都有理由，挪动前先读下面那两处的注释。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> BuildArguments(
        CameraSource source, string outputPath, string encoder, RecordingSpec? spec = null,
        string? watermarkAssPath = null, string? microphone = null, int? durationSeconds = null,
        bool preview = false, int? segmentSeconds = null, bool frameTap = false,
        int segmentStartNumber = 0)
    {
        // ⚠️ 两路都要 `pipe:1` —— 而**一个 Process 只有一条 stdout**
        // （2026-10-02 定下的架构）。放过去的话不是报错，是两条输出互相咬：
        // 读端按预览的 640×360 rgb24 切，实际流里混着 640×480 的帧
        // ⇒ 两路都是花屏，而且不会有任何报错。所以宁可在这里炸掉。
        if (preview && frameTap)
        {
            throw new InvalidOperationException(
                "预览与取景那两路都要 stdout，一个进程只有一条 —— 这两种输出不能同时要。");
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            "-v", "error",
        };

        // ⚠️ 音频那一路排在**视频之前**：麦克风打不开时 ffmpeg 在打开音频设备那一刻
        // 就退出了（远快于开相机），而「这一次起来了没有」的判定要等它 ——
        // 音频排在后的话，每段开头都要多等一个开相机的时间。
        //
        // ⚠️ 正因为顺序是这样，画面是第几个输入要**在这里算**（见下面的 -map）。
        // 两处各写一个数字的话，改了顺序就会静默指错输入。
        var videoInput = 0;

        if (!string.IsNullOrWhiteSpace(microphone))
        {
            arguments.AddRange(AudioInputArguments(microphone, BufferSize));

            videoInput = 1;
        }

        // 画面那一路的输入参数由源自己给 —— 本机设备与网络地址的形状不一样，
        // 而那个差别只该有一处（见 CameraSource.InputArguments 的说明）。
        // ⚠️ 传的是 `PinnedFfmpegSize`（原生档时是 null ⇒ 不带 -video_size/-framerate，
        // 相机用它自己的默认档）。见 `RecordingSpec.NativeCaptureSize` 的说明。
        arguments.AddRange(source.InputArguments(BufferSize, spec?.PinnedFfmpegSize));

        // ⚠️ **必须显式 -map，不能靠 ffmpeg 的默认选流。**
        //
        // 只给一个 `-map` 就会关掉默认选流，于是「录什么」变成我们说了算。
        // 不写的话 2026-09-29 实测到的后果是：**网络摄像头自己推了一条 AAC**
        // （那台是 H.264 + AAC），ffmpeg 会自动把「最好的音频流」挑进产物 ——
        // 于是用户把「录制声音」关掉、甚至本机根本没有麦克风，
        // 录出来的文件**照样有声音**，而且是摄像头那头的现场音。
        // 那是与用户的选择相反、而他完全不知道的一件事。
        arguments.AddRange(["-map", $"{videoInput}:v:0"]);

        if (!string.IsNullOrWhiteSpace(microphone))
        {
            // 只取**本地麦克风**那一路（input 0）。对端自带的音轨不映射 ⇒ 丢掉。
            arguments.AddRange(["-map", "0:a:0"]);
        }
        else
        {
            // 双保险：把「不要音频」写死。上面的 `-map` 已经排除了音频，
            // 但这一条让意图一眼可见 —— 将来有人动了输入顺序也不会悄悄放出声音。
            arguments.Add("-an");
        }

        // ⚠️ 滤镜链只有**一条** `-vf`：写两个 `-vf` 的话后一个会顶掉前一个
        // （没有报错，只是少了一个效果）。所以先攒起来，最后一起拼。
        var filters = new List<string>();

        if (source.IsNetwork && spec?.PinnedFfmpegSize is { } target)
        {
            // 网络那一路的尺寸**只能在输出侧**做（RTSP 不能按尺寸开流）。
            // ⚠️ 缩到的是 **CaptureSize**（采集尺寸，恒为横屏）——
            // 方向是**之后**那一步的事，见下面。
            //
            // ⚠️ 原生档（`PinnedFfmpegSize` 为 null）时**不缩** —— 那就是「照它发的收」，
            // 与相机原生档同一个意思：宁可录到一个我们没选的尺寸，也不要录不出来。
            filters.Add($"scale={target}");
        }

        if (spec?.RotationFilter is { } rotation)
        {
            // ⚠️ **必须排在水印之前**：水印是压在画面上的字，先烧后转会把字也转倒。
            // ⚠️ 也必须排在 scale **之后**：缩放的目标是**采集**尺寸（横的），
            // 先缩再转，转完才是成片尺寸（转 90° 时宽高已经换过来了）。
            filters.Add(rotation);
        }

        if (!string.IsNullOrWhiteSpace(watermarkAssPath))
        {
            // ⚠️ 滤镜是**输出选项**（放在 `-i` 之后、输出路径之前）。
            // 写成输入选项的话 ffmpeg 会把它当成对输入的处理，行为完全不同。
            filters.Add($"ass={AssWatermark.EscapeFilterPath(watermarkAssPath)}");
        }

        if (filters.Count > 0)
        {
            arguments.Add("-vf");
            arguments.Add(string.Join(',', filters));
        }

        arguments.AddRange(
        [
            "-c:v", encoder,
            // 摄像头出的是 yuyv422，H.264 要 4:2:0。让 ffmpeg 显式转，
            // 而不是指望编码器自己接受 —— libx264 接受不了 yuyv422 会直接失败。
            "-pix_fmt", "yuv420p",
        ]);

        if (source.IsNetwork)
        {
            // ⚠️ 规格 §3.1.7「帧率固定 30，不提供选择」。本机设备靠输入侧的
            // `-framerate 30` 钉住，而网络那一路**没有输入侧选项可用**
            // ⇒ 只能在输出侧补一个，否则「固定 30」在网络摄像头上就是句空话
            // （对端发 25 就录成 25）。
            arguments.Add("-r");
            arguments.Add(RecordingSpec.FrameRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(microphone))
        {
            // ⚠️ 规格 §3.1.8 **逐字**：AAC、单声道、44.1 kHz、64 kbps。
            // 两端必须一致 —— 一边一套参数的话，同一段素材在两个端上
            // 转出来是两个体积，而「按空间清理」是按体积算的。
            arguments.AddRange(
            [
                "-c:a", "aac",
                "-ac", "1",
                "-ar", "44100",
                "-b:a", "64k",
            ]);
        }

        if (durationSeconds is { } seconds)
        {
            // ⚠️ 输出侧选项：要落在 `-i` **之后**、输出路径之前。
            arguments.AddRange(
            [
                "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ]);
        }

        // ⚠️⚠️ **每包都刷**（2026-09-29 真机踩出来的，不是优化）。
        //
        // ffmpeg 的输出默认**按块刷**，实测每块正好 262144 字节（256 KiB）。
        // 而 `ConfirmStartedAsync` 判定「这一次起来了没有」的判据是
        // **产物出现**（`Produced()`：文件存在且长度 > 0），窗口 8 秒。
        // 低码率场景（夜里、画面基本不动）填满第一块要 **16 秒** ——
        // 在那之前 `FileInfo.Length` 一直是 0（FileInfo / 开句柄 / 拷贝文件
        // 三种读法读数完全一致，所以不是读法的问题，是文件真的还没写）。
        //
        // 后果有两层，都是**带麦克风时**才发作（不带麦克风时
        // `ConfirmStartedAsync` 直接返回，根本不等）：
        //   ① 一次**健康**的采集被判成「没起来」⇒ 杀掉 ⇒ 不带音频重开
        //      ⇒ 这一段**没有音轨**（§3.1.8 要的音轨等于没生效）；
        //   ② 那次重开与相机释放抢跑（`Kill` 之后设备还没放开），
        //      实测拿到退出码 -5、**0 字节** —— 整段丢，而不是降级。
        //
        // 钉住它之后第一块立刻落盘（实测 2.0 秒就有表头），
        // 「产物出现」这个判据才真的成立。⚠️ 它也是**输出**选项，
        // 落在 `-i` 之前会被当成输入选项静默失效。
        //
        // 顺带收益：断电/强杀时最多丢一块而不是 256 KiB 的已录画面。
        arguments.AddRange(
        [
            "-flush_packets", "1",
        ]);

        if (segmentSeconds is { } segmentLength)
        {
            // ── 滚动分片（规格 §3.1.3 的预录缓冲用它） ──────────────────
            //
            // ⚠️ <b>`-g` 是承重的，不是画质选项</b>：采纳缓冲时要从片子的**尾巴**上
            // 裁最后 N 秒，而 `-c copy` 只能从**关键帧**切 ⇒ 裁出来的长度精度
            // 就等于关键帧间隔。全仓没有别的地方设过 `-g`，ffmpeg 的默认 GOP
            // 约 250 帧 ≈ 8 秒 —— 设 5 秒的缓冲会裁出十几秒。
            // 帧率固定 30（规格 §3.1.7）⇒ 30 帧正好 1 秒。
            arguments.AddRange(
            [
                "-g", SegmentKeyframeInterval.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ]);

            arguments.AddRange(
            [
                "-f", "segment",
                "-segment_format", "matroska",
                "-segment_time",
                segmentLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                // ⚠️ 每片从 0 起（与正式分段同形）：不重置的话第二片起时间戳接着上一片，
                // 单独丢给 `RemuxPipeline` / `DecodeVerifier` 会被当成「缺了开头」。
                // 它**只动封装器写出的时间戳**，滤镜看到的 pts 照旧是连续的
                // —— 2026-10-05 本机实测（`showinfo`：3.966667 → 4.0 → 4.033333
                // 跨过切点无跳变）⇒ 一整场一份 `.ass` 的秒针跨段接着走。
                "-reset_timestamps", "1",
                // ⚠️ 起始号**必须由调用方给**，不能吃默认的 0：采纳了预录缓冲时
                // 目录里已经有 `segment-000.mkv`，而这个输出带 `-y` ——
                // 2026-10-05 实测，重号会把那几秒**静默覆盖**掉。
                "-segment_start_number",
                segmentStartNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                // ⚠️ <b>输出的这个位置是**模式**，不是文件名</b>（`pre-%03d.mkv`）。
                // `-y` 仍然紧挨着它 —— 测试替身靠那个位置定位产物。
                "-y", outputPath,
            ]);
        }
        else
        {
            arguments.AddRange(
            [
                "-f", "matroska",
                "-y", outputPath,
            ]);
        }

        // ── 第二路输出：预览画面（§62） ────────────────────────────────
        //
        // ⚠️ 它**必须**是「同一个进程的第二路输出」，不能是第二个进程：
        // 相机是独占的（§25）。
        // ⚠️ 形状与 `PreviewProcess` **同一个**（它那边出的也是 640×360 rgb24），
        // 因为界面只该有一条渲染路径 —— 两点不同就会出现「录制中画面对、
        // 别的状态花屏」这种只在一种状态下发作的毛病。
        // 尺寸跟着方向走（转 90° 之后宽高换过来），由
        // `PreviewProcess.PreviewFilters` 一并算出来，读端按同一个值切帧。
        if (preview)
        {
            arguments.AddRange(
            [
                "-map", $"{videoInput}:v:0",
                "-an",
                "-vf", string.Join(',', PreviewProcess.PreviewFilters(
                    spec?.Rotation ?? CameraRotation.None)),
                "-r", PreviewProcess.Fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-f", "rawvideo",
                "-pix_fmt", "rgb24",
            ]);

            // ⚠️⚠️ **每一路输出都要各自限长**（§54.4 真机踩出来的）：
            // `-t` 是**输出选项**，不是全局的。只给文件那一路写 `-t` 的话，
            // 预览这一路没有终点 —— 到点了 ffmpeg 也不退，而规格探测那一步
            // 正是在等它自己退出（等不到就挂到超时）。
            if (durationSeconds is { } limit)
            {
                arguments.Add("-t");
                arguments.Add(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            arguments.Add("pipe:1");
        }

        // ── 第二路输出：彩色裸帧（待扫的取景框与识码共用，§54） ──────────
        //
        // ⚠️ 与上面预览那一路**不能共存**（开头就拦住了）——两路都要 stdout。
        // 预录那一路要的是「文件 ＋ 帧」：文件给缓冲，帧给待扫的取景框（并降频转灰度识码）。
        if (frameTap)
        {
            arguments.AddRange(
            [
                "-map", $"{videoInput}:v:0",
                "-an",
                // ⚠️ <b>一律要过那两环几何</b>：这一路的输入是按**用户的录制规格**开的
                // （可能是 1920×1080），而读端按 640×480 定长切裸帧 ——
                // 不处理的话切出来是错位的花屏、识码永远认不出来，**且不报任何错**。
                // 见 `PrerecordProcess.FrameFilters`。
                "-vf", string.Join(',', PrerecordProcess.FrameFilters(
                    spec?.Rotation ?? CameraRotation.None)),
                "-pix_fmt", "rgb24",
                "-f", "rawvideo",
            ]);

            // ⚠️ 同上面预览那一路：**每一路输出都要各自限长**（§54.4）——
            // `-t` 是输出选项，不是全局的。
            if (durationSeconds is { } frameLimit)
            {
                arguments.Add("-t");
                arguments.Add(frameLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            arguments.Add("pipe:1");
        }

        return arguments;
    }
}

/// <summary>
/// 只留最后 N 个字符的文本缓冲。线程安全。
/// </summary>
/// <remarks>
/// 用来排空子进程的 stderr：既要**读干**（不读就会把管道灌满、把进程堵死），
/// 又不能无限攒（长录制会吃光内存）。留尾部而不是头部，是因为出问题时
/// 有用的那几行通常就在最后。
/// </remarks>
public sealed class BoundedTextTail
{
    private const int Capacity = 16 * 1024;

    private readonly Lock _gate = new();
    private readonly System.Text.StringBuilder _tail = new();

    public void Append(char[] buffer, int count) => Append(new string(buffer, 0, count));

    /// <summary>追加一段（一般是一行）。</summary>
    public void Append(string text)
    {
        lock (_gate)
        {
            _tail.Append(text);

            if (_tail.Length > Capacity)
            {
                _tail.Remove(0, _tail.Length - Capacity);
            }
        }
    }

    public override string ToString()
    {
        lock (_gate)
        {
            return _tail.ToString();
        }
    }
}
