namespace VidLog.Desktop.Core.Camera;

/// <summary>一帧灰度画面（紧凑的 w×h 亮度，stride == width）。</summary>
/// <remarks>
/// ffmpeg 的 <c>rawvideo</c> + <c>pix_fmt gray</c> 正好产出这个形状，
/// ZXing 的 <c>Gray8</c> 也正好吃它 —— 中间不需要任何图像库。
/// </remarks>
public sealed record CameraFrame(byte[] Gray, int Width, int Height, long CapturedAtMs);

/// <summary>
/// 单槽帧缓冲：**只留最新一帧，旧的直接丢**。
/// </summary>
/// <remarks>
/// 这是整条帧通路**不回压**的关键。消费者（ZXing 解码）比生产者（管道读）慢时，
/// 有界队列会堵住；而堵住管道会一路顶到 ffmpeg ——
/// 它连 stdin 上的 <c>q</c> 都处理不了，只能强杀，**MKV 尾部就丢了**。
/// <para>
/// 丢帧在这里完全可接受：识码只需要「画面里现在有没有码」，
/// 静止检测只需要「和上一帧比变没变」——两者都不关心丢掉的中间帧。
/// </para>
/// </remarks>
public sealed class SingleSlotFrameSink
{
    private readonly Lock _gate = new();
    private CameraFrame? _latest;
    private long _dropped;

    /// <summary>投一帧。永不阻塞。</summary>
    public void Publish(CameraFrame frame)
    {
        lock (_gate)
        {
            if (_latest is not null)
            {
                _dropped++;
            }

            _latest = frame;
        }
    }

    /// <summary>取最新一帧并清空槽。没有新帧时返回 null。</summary>
    public CameraFrame? TakeLatest()
    {
        lock (_gate)
        {
            var frame = _latest;
            _latest = null;
            return frame;
        }
    }

    /// <summary>因为消费者没跟上而丢掉的帧数（诊断用）。</summary>
    public long DroppedCount
    {
        get { lock (_gate) { return _dropped; } }
    }
}

/// <summary>
/// 从管道里读裸灰度帧。
/// </summary>
/// <remarks>
/// <b>读循环不接受可取消的读</b>，理由与采集进程的 stdout 排空完全一样：
/// 读端一停，管道就满，ffmpeg 就被顶住。停机靠**关掉管道**（进程退出），
/// 不是靠取消这个循环。
/// </remarks>
public sealed class RawGrayFrameReader
{
    private readonly Stream _stream;
    private readonly SingleSlotFrameSink _sink;

    public RawGrayFrameReader(Stream stream, SingleSlotFrameSink sink, int width, int height)
    {
        _stream = stream;
        _sink = sink;
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }

    public long FramesRead { get; private set; }
    public long BytesRead { get; private set; }

    /// <summary>
    /// 一直读到流结束。
    /// </summary>
    /// <returns>读到的完整帧数。</returns>
    public async Task<long> RunAsync()
    {
        var frameSize = Width * Height;
        var buffer = new byte[frameSize];
        var filled = 0;

        try
        {
            while (true)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(filled, frameSize - filled))
                    .ConfigureAwait(false);

                if (read <= 0)
                {
                    // 管道断了（进程退了）。正常结束，不是错误。
                    break;
                }

                filled += read;
                BytesRead += read;

                if (filled == frameSize)
                {
                    // 必须拷一份 —— buffer 会被下一帧复用。
                    _sink.Publish(new CameraFrame(
                        (byte[])buffer.Clone(), Width, Height, Environment.TickCount64));

                    FramesRead++;
                    filled = 0;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 进程被收掉时管道会断。同上，不是错误。
        }

        return FramesRead;
    }
}
