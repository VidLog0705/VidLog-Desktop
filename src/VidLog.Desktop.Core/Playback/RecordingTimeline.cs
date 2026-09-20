using VidLog.Desktop.Core.Punches;

namespace VidLog.Desktop.Core.Playback;

/// <summary>时间轴上的一个分段。</summary>
/// <param name="FilePath">分段文件（成品 MP4）的绝对路径。</param>
/// <param name="Duration">该段的时长。</param>
public sealed record TimelineSegment(string FilePath, TimeSpan Duration);

/// <summary>在时间轴上定位到的位置。</summary>
/// <param name="SegmentIndex">第几段（从 0 起）。</param>
/// <param name="FilePath">该段的文件路径。</param>
/// <param name="OffsetInSegment">在这段文件内部的偏移 —— 播放器 seek 用的就是它。</param>
public sealed record TimelinePosition(int SegmentIndex, string FilePath, TimeSpan OffsetInSegment);

/// <summary>
/// 一次录制会话的时间轴 —— 把「会话内的偏移」映射到「第几段的第几秒」。
/// </summary>
/// <remarks>
/// <para>
/// 规格 §3.8 要求回放支持「跳转到打点位置」。难点在于**一次打包是区间，可能横跨多个分段文件**
/// （母仓 `docs/02-数据模型.md` §2）—— 打点记的是一个会话内的偏移，
/// 而播放器需要的是「打开哪个文件、seek 到第几秒」。本类就做这个换算。
/// </para>
/// <para>
/// 分段按时长顺序累加，假定**首尾相接无空隙** —— 这与规格 §3.1.1
/// 「不因切分而中断用户体验（用户感知为『一直在录』）」一致，连续分段录制不留空档。
/// </para>
/// </remarks>
public sealed class RecordingTimeline
{
    private readonly TimelineSegment[] _segments;
    private readonly TimeSpan[] _segmentStarts;

    public RecordingTimeline(IReadOnlyList<TimelineSegment> segments)
    {
        _segments = [.. segments];
        _segmentStarts = new TimeSpan[_segments.Length];

        var cursor = TimeSpan.Zero;
        for (var i = 0; i < _segments.Length; i++)
        {
            _segmentStarts[i] = cursor;
            cursor += _segments[i].Duration;
        }

        TotalDuration = cursor;
    }

    public IReadOnlyList<TimelineSegment> Segments => _segments;

    /// <summary>整条录像的总时长。</summary>
    public TimeSpan TotalDuration { get; }

    /// <summary>
    /// 把会话内的偏移定位到具体分段。
    /// </summary>
    /// <returns>越界（负、或超出总时长）时返回 <see langword="null"/>。</returns>
    public TimelinePosition? Locate(TimeSpan offset)
    {
        if (offset < TimeSpan.Zero || offset >= TotalDuration || _segments.Length == 0)
        {
            return null;
        }

        // 从后往前找，边界（offset 恰好落在两段之间）归到前一段的末尾 ——
        // 这样拖到分界点时看到的是上一段的最后一帧，而不是下一段的第一帧，
        // 符合「继续往后拖」的操作直觉。
        for (var i = _segments.Length - 1; i >= 0; i--)
        {
            if (offset >= _segmentStarts[i])
            {
                return new TimelinePosition(i, _segments[i].FilePath, offset - _segmentStarts[i]);
            }
        }

        return null;
    }

    /// <summary>
    /// 把一次打点定位到时间轴上。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Locate"/> 的区别：**越界时钳到最近的一端**，而不是返回 null。
    /// 理由：打点时刻可能略晚于最后一段的收尾（收尾本身要花时间），
    /// 这时该跳到末尾让用户看到画面，而不是弹一个「定位失败」。
    /// </remarks>
    public TimelinePosition? LocatePunch(Punch punch)
    {
        if (_segments.Length == 0)
        {
            return null;
        }

        var offset = TimeSpan.FromMilliseconds(punch.MonotonicOffsetMilliseconds);

        if (offset < TimeSpan.Zero)
        {
            return new TimelinePosition(0, _segments[0].FilePath, TimeSpan.Zero);
        }

        if (offset >= TotalDuration)
        {
            var last = _segments.Length - 1;
            return new TimelinePosition(last, _segments[last].FilePath, _segments[last].Duration);
        }

        return Locate(offset);
    }
}
