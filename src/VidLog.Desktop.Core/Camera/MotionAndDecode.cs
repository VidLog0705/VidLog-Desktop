namespace VidLog.Desktop.Core.Camera;

/// <summary>静止判定的旋钮。</summary>
/// <param name="SampleStride">取样步长。4 表示每 4 个像素取一个（角落也取得到）。</param>
/// <param name="NoiseFloor">
/// 单像素差异低于这个值就算「没变」。用来吃掉传感器噪声与压缩抖动。
/// </param>
/// <param name="ChangedFraction">
/// 变化的取样点占比低于这个值就算「静止」。
/// </param>
public sealed record MotionOptions(
    int SampleStride = 4,
    double NoiseFloor = 6.0,
    double ChangedFraction = 0.02);

/// <summary>一次静止判定的结果。</summary>
/// <param name="IsStatic">画面是否静止。</param>
/// <param name="MeanAbsoluteDifference">本次与上帧的平均差（诊断与标定用）。</param>
/// <param name="ChangedRatio">变化取样点的占比。</param>
public sealed record MotionSample(
    bool IsStatic, double MeanAbsoluteDifference, double ChangedRatio);

/// <summary>
/// 画面静止检测（规格 §3.3.3 与扫码静止的那 2 秒）。
/// </summary>
/// <remarks>
/// <para>
/// <b>先减均值再比</b>，这一步是承重的：摄像头有自动曝光与自动白平衡，
/// 完全没人动的时候整幅画面的亮度也会缓慢漂移。直接用绝对差比，
/// 会把这种漂移当成「在动」，于是**永远判不出静止**。
/// 减掉均值之后，全局的明暗变化被消掉，留下的才是「画面内容变了没有」。
/// </para>
/// <para>
/// ⚠️ <b>阈值必须真机标定</b>。自动曝光漂移有多大、噪声有多高，取决于具体摄像头。
/// <see cref="MotionOptions"/> 是留出来的旋钮，不要把它当常数。
/// </para>
/// </remarks>
public sealed class FrameMotionDetector
{
    private readonly MotionOptions _options;
    private byte[]? _previous;

    public FrameMotionDetector(MotionOptions? options = null)
    {
        _options = options ?? new MotionOptions();
    }

    public MotionSample Observe(CameraFrame frame)
    {
        var current = frame.Gray;

        // 第一帧没有可比的对象 —— 当作静止，从第二帧起才真正判定。
        if (_previous is null || _previous.Length != current.Length)
        {
            _previous = (byte[])current.Clone();
            return new MotionSample(IsStatic: true, MeanAbsoluteDifference: 0, ChangedRatio: 0);
        }

        var stride = Math.Max(1, _options.SampleStride);
        var count = 0;
        long sum = 0;
        var changed = 0;

        // 两遍：先算均值，再比。均值那一遍只取同样的取样点，
        // 所以这里把取值循环写两遍 —— 比先物化一份取样数组省一次分配。
        long previousSum = 0;
        long currentSum = 0;

        for (var i = 0; i < current.Length; i += stride)
        {
            previousSum += _previous[i];
            currentSum += current[i];
            count++;
        }

        if (count == 0)
        {
            return new MotionSample(IsStatic: true, MeanAbsoluteDifference: 0, ChangedRatio: 0);
        }

        var previousMean = (double)previousSum / count;
        var currentMean = (double)currentSum / count;

        for (var i = 0; i < current.Length; i += stride)
        {
            // 各自减去自己的均值 —— 消掉全局明暗漂移。
            var delta = Math.Abs((current[i] - currentMean) - (_previous[i] - previousMean));

            sum += (long)delta;

            if (delta > _options.NoiseFloor)
            {
                changed++;
            }
        }

        var changedRatio = (double)changed / count;
        var meanDifference = (double)sum / count;

        _previous = (byte[])current.Clone();

        return new MotionSample(
            IsStatic: changedRatio <= _options.ChangedFraction,
            MeanAbsoluteDifference: meanDifference,
            ChangedRatio: changedRatio);
    }

    public void Reset() => _previous = null;
}

/// <summary>识码闸的旋钮。</summary>
/// <param name="AbsenceBeforeRepeat">
/// 同一个码要「消失」多久之后再次读到，才算一次**新的**识别。
/// </param>
public sealed record DecodeGateOptions(TimeSpan AbsenceBeforeRepeat)
{
    /// <summary>
    /// 与手机端取齐的默认值。
    /// </summary>
    /// <remarks>
    /// 规格里「扫码静止停录」的门槛就是「面单**离场后**再入场」，
    /// 而这条 2 秒同时充当那个离场判据 —— 两处用同一个数，不是巧合：
    /// 少了它，面单一直摆在框里会被连续上报，而**每上报一次就换一次段**。
    /// </remarks>
    public static DecodeGateOptions Default { get; } = new(TimeSpan.FromSeconds(2));
}

/// <summary>
/// 识码闸：同一个码不重复上报，除非它先消失了一阵。
/// </summary>
/// <remarks>
/// 摄像头是**连续**读码的，而业务要的是「又扫了一次」。
/// 照字面实现的话，包裹放上去就会被自己的持续识别反复触发 ——
/// 手机端踩过这个坑，这里是同一道闸。
/// </remarks>
public sealed class DecodeGate
{
    private readonly DecodeGateOptions _options;

    private string? _current;
    private long _lastSeenAtMs;
    private long _absentSinceMs = -1;

    public DecodeGate(DecodeGateOptions? options = null)
    {
        _options = options ?? DecodeGateOptions.Default;
    }

    /// <summary>当前视野里有没有被认过的码（扫码静止那 2 秒的门槛要看它）。</summary>
    public bool IsPresent => _current is not null;

    /// <summary>被跟踪的码是否**离场过**。</summary>
    public bool HasLeft => _absentSinceMs >= 0;

    /// <summary>
    /// 喂一次解码结果。
    /// </summary>
    /// <param name="decoded">本帧读到的文本；没读到传 null。</param>
    /// <param name="nowMs">单调时间戳（毫秒）。</param>
    /// <returns>该上报时返回文本，否则返回 null。</returns>
    public string? Observe(string? decoded, long nowMs)
    {
        var absence = (long)_options.AbsenceBeforeRepeat.TotalMilliseconds;

        if (decoded is null)
        {
            if (_current is not null && _absentSinceMs < 0)
            {
                // 从「有」变成「没有」—— 开始计时。
                _absentSinceMs = nowMs;
            }

            return null;
        }

        // 读到了东西。
        if (_current is null)
        {
            // 之前是空的。够久没见才算新的，否则是刚离开又回来。
            _current = decoded;
            _absentSinceMs = -1;
            _lastSeenAtMs = nowMs;
            return decoded;
        }

        if (_current == decoded)
        {
            // 还是同一个码。
            var wasAwayLongEnough = _absentSinceMs >= 0 && nowMs - _absentSinceMs >= absence;

            _lastSeenAtMs = nowMs;
            _absentSinceMs = -1;

            // 只有「离开够久又回来」才算一次新的识别。
            return wasAwayLongEnough ? decoded : null;
        }

        // 换了一个码 —— 那就是新的一次。
        _current = decoded;
        _absentSinceMs = -1;
        _lastSeenAtMs = nowMs;
        return decoded;
    }

    public void Reset()
    {
        _current = null;
        _absentSinceMs = -1;
        _lastSeenAtMs = 0;
    }
}
