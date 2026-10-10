using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Camera;

/// <summary>
/// **录制期识码**：录制那一路预览帧**顺带**再喂一次解码器。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要有它：相机是**独占**的，待扫那个进程在开段时被整个交出去
/// （见 <c>RecordingCoordinator.StartSegmentAsync</c> 的 <c>TakeBufferedAsync</c>），
/// 于是**开录之后就没有任何人在读码了**。现场表现是
/// 「同码停模式第二遍扫它不停止录制」「连扫模式换件换不了」——
/// 而根因是那一刻根本没人看得见面单，不是判定错。
/// </para>
/// <para>
/// 帧从哪来：录制进程本来就有一路 stdout 出彩色裸帧给主窗取景
/// （<c>FfmpegCameraCapture</c> 的 <c>preview</c>），本类只是**在同一帧上**
/// 再解一次码 —— 不多开进程、不多占相机（那是行不通的，独占）。
/// </para>
/// <para>
/// ⚠️ <b>解在后台循环里，决不在投帧那条线程上解</b>：投帧的是管道读端，
/// 把它拖慢等于堵住管道，而堵住管道是这台机器上唯一会丢录像的事故（§54.2）。
/// 所以 <see cref="OnFrame"/> 只做一次永不阻塞的投递。
/// </para>
/// <para>
/// ⚠️ <b>开段时必须先 <see cref="PrimeWith"/> 一下</b>：开段那一刻面单还在框里，
/// 不先喂进闸的话下一帧就会被当成「又扫了一次」—— 同码停模式**刚开录就停**。
/// </para>
/// </remarks>
public sealed class RecordingRecognition
{
    /// <summary>
    /// 每几帧解一次。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与待扫那条路（<c>PrerecordController.DecodeEveryNthFrame</c>）**同一个数**：
    /// 两处都是 12 fps 的帧、都只为了「又扫了一次」这一件事，取不同的数只会让
    /// 「待扫认得出、录制中认不出」这种差别显得像缺陷。
    /// </remarks>
    public const int DecodeEveryNthFrame = 3;

    private readonly IFrameScanner? _decoder;
    private readonly IAppLogger _logger;
    private readonly SingleSlotPreviewSink _sink = new();

    /// <summary>识码闸 —— 同一个码不重复上报，除非它先离场够久（见 <see cref="DecodeGate"/>）。</summary>
    private readonly DecodeGate _gate = new();

    private CancellationTokenSource? _loop;

    public RecordingRecognition(
        IFrameScanner? decoder,
        IAppLogger logger)
    {
        _decoder = decoder;
        _logger = logger;
    }

    /// <summary>识别到单号。</summary>
    public event Action<WaybillNumber>? Scanned;

    /// <summary>
    /// 采集进程把帧投到这里（挂法见 <c>FfmpegCameraCapture.FrameObserver</c>）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它跑在**管道读端那条线程**上，所以这里只许做永不阻塞的事。
    /// </remarks>
    public void OnFrame(PreviewFrame frame) => _sink.Publish(frame);

    /// <summary>起那个后台解码循环。重复调用什么都不做。</summary>
    public void Start()
    {
        if (_loop is not null || _decoder is null)
        {
            // 没挂解码器（设置里把摄像头识别关了）就**连循环都不起** ——
            // 一个每 30 毫秒醒一次的空转循环没有意义。
            return;
        }

        var loop = new CancellationTokenSource();
        _loop = loop;

        _logger.Log(LogLevel.Info, "识码",
            $"录制期识码已开（每 {DecodeEveryNthFrame} 帧看一次识别框）");

        var token = loop.Token;
        _ = Task.Run(() => LoopAsync(token), CancellationToken.None);
    }

    /// <summary>停那个循环并等它退出去。</summary>
    public async Task StopAsync()
    {
        if (_loop is not { } loop)
        {
            return;
        }

        _loop = null;
        await loop.CancelAsync().ConfigureAwait(false);
        loop.Dispose();

        _logger.Log(LogLevel.Info, "识码", "录制期识码已停");
    }

    /// <summary>
    /// 开段时把**刚落下来的那个单号**先喂进闸。
    /// </summary>
    /// <remarks>
    /// 这一步不是优化，是**正确性**：闸的语义是「第一次见到就上报」，
    /// 而开段那一刻面单正摆在框里。不喂这一下，同码停模式会在开录后的第一帧
    /// 就把这件包裹当成「又扫了一次」，当场把刚开的段停掉。
    /// <para>
    /// 喂进去之后语义正是要的那个：同一个码一直摆在框里**不再上报**，
    /// 拿开够久再放回来才算「又扫了一次」；框里换成**别的码**则立刻上报（换件）。
    /// </para>
    /// </remarks>
    public void PrimeWith(string? code) => _gate.Observe(code, Environment.TickCount64);

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        var tick = 0;

        // ⚠️ **局部变量**：这个循环一次录制一个，「报过几次错」是这一段的事。
        var complained = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = _sink.TakeLatest();

            if (frame is null)
            {
                // 没有新帧（没在录制 / 采集还没起来）—— 睡一小会而不是空转。
                await Task.Delay(30, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            if (++tick % DecodeEveryNthFrame != 0)
            {
                continue;
            }

            try
            {
                // 喂**识别框那一片**（需求方 2026-10-10）：框外的码连 ZXing 都到不了。
                var decoded = _decoder!.TryDecode(frame.ToGray(RecognitionRoi.Default));

                // ⚠️ 解出 null 也要喂闸 —— 「码离场了」这件事只有它能看见，
                // 而同码复扫要的就是「离场够久」那个判据。
                var text = _gate.Observe(decoded, Environment.TickCount64);

                // 解不出来是常态；不是合法单号（别的条码、误读）也不上报 —— 闸已经记下了。
                if (text is null || !WaybillNumber.TryParse(text, out var waybill, out _))
                {
                    continue;
                }

                Scanned?.Invoke(waybill!);
            }
            catch (Exception ex)
            {
                // ⚠️ 这个循环是 `_ = Task.Run(...)` 起的：异常**没人接**，任务就那么静静地死了，
                // 现场表现是「录制中怎么也扫不出来」，而日志里一个字都没有。
                // 记一次就够 —— 同一段里每帧报一次只会把日志刷满。
                if (!complained)
                {
                    complained = true;
                    _logger.Log(LogLevel.Warn, "识码", $"录制期识码出错：{ex.GetType().Name}：{ex.Message}");
                }
            }
        }
    }
}
