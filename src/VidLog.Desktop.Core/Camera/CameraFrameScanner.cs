using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Camera;

/// <summary>
/// 取景识码：跑一个轻量采集进程，读到单号就报出来。
/// </summary>
/// <remarks>
/// <para>
/// 规格 §3.2.1 的第二种识别入口。它**不录制** —— 只取低频灰度帧、
/// 解出码、报事件，然后自己退出。真正开录是另一条路（<see cref="RecordingSession"/>）。
/// </para>
/// <para>
/// 之所以必须是独立进程：DirectShow 相机**独占**（实测），录制进程开着的时候
/// 别的进程开不了同一台相机。而需求方裁定的正是「空闲时识码、扫到才开录」——
/// 两者本来就不重叠。进程退出后设备会干净释放（连开 5 次 5/5 成功，实测）。
/// </para>
/// </remarks>
public sealed class CameraFrameScanner : IAsyncDisposable
{
    private readonly string _ffmpegPath;
    private readonly string _device;
    private readonly IFrameScanner _decoder;
    private readonly IAppLogger _logger;
    private readonly DecodeGate _gate = new();
    private readonly SingleSlotFrameSink _sink = new();

    private ScannerProcess? _process;
    private CancellationTokenSource? _loop;

    public CameraFrameScanner(
        string ffmpegPath, string device, IFrameScanner decoder, IAppLogger logger)
    {
        _ffmpegPath = ffmpegPath;
        _device = device;
        _decoder = decoder;
        _logger = logger;
    }

    /// <summary>识别到单号。</summary>
    public event Action<WaybillNumber>? Scanned;

    /// <summary>识码进程起不来时的原因（I3：不允许静默失效）。</summary>
    public event Action<string>? Failed;

    public bool IsScanning => _process is not null;

    /// <summary>
    /// 开始取景识码。已在跑时什么都不做。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null)
        {
            return;
        }

        try
        {
            _gate.Reset();
            _process = await ScannerProcess.StartAsync(_ffmpegPath, _device, _sink, cancellationToken);

            _loop = new CancellationTokenSource();
            _ = Task.Run(() => DecodeLoopAsync(_loop.Token), CancellationToken.None);

            _logger.Log(LogLevel.Info, "识码", "开始取景识码");
        }
        catch (Exception ex)
        {
            _process = null;
            _logger.Log(LogLevel.Error, "识码", $"取景识码起不来：{ex.Message}");
            Failed?.Invoke($"摄像头识码起不来：{ex.Message}");
        }
    }

    /// <summary>停下取景识码，释放相机。</summary>
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

        var process = _process;
        _process = null;

        if (process is not null)
        {
            await process.StopAsync(cancellationToken);
            _logger.Log(LogLevel.Info, "识码", "取景识码已停，相机已释放");
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

            Scanned?.Invoke(waybill);
            return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
