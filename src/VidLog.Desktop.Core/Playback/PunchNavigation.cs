using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Punches;

namespace VidLog.Desktop.Core.Playback;

/// <summary>
/// 一次打点在播放器里对应的目标位置。
/// </summary>
/// <param name="PunchId">打点 id。</param>
/// <param name="EvidenceId">打点所在的那个分段 —— 播放器据此取视频。</param>
/// <param name="SegmentIndex">在会话时间轴上是第几段。</param>
/// <param name="OffsetSeconds">
/// 在**这一段文件内部**的偏移，直接喂给播放器的 <c>currentTime</c>。
/// </param>
/// <param name="SessionOffsetSeconds">
/// 在**整个会话**里的偏移（跨段累加），用于排序与展示。
/// </param>
/// <param name="WaybillNumber">该打点的单号。</param>
/// <param name="PunchedAt">墙钟时刻，仅用于展示。</param>
public sealed record PunchTarget(
    string PunchId,
    string EvidenceId,
    int SegmentIndex,
    double OffsetSeconds,
    double SessionOffsetSeconds,
    string WaybillNumber,
    DateTimeOffset PunchedAt);

/// <summary>
/// 把打点换算成播放器能直接用的位置（规格 §3.8「跳转到打点位置」）。
/// </summary>
/// <remarks>
/// <para>
/// 打点记的是**会话内偏移**（规格 §3.2.4），播放器要的是「哪个文件、第几秒」。
/// 中间隔着一层：一次打包可能横跨多个分段文件。本类把索引、打点日志
/// 与 <see cref="RecordingTimeline"/> 接起来。
/// </para>
/// <para>
/// 不依赖许可状态（许可设计 L8）。
/// </para>
/// </remarks>
public sealed class PunchNavigation
{
    private readonly IRecordingIndex _index;
    private readonly IPunchLog _punches;

    public PunchNavigation(IRecordingIndex index, IPunchLog punches)
    {
        _index = index;
        _punches = punches;
    }

    /// <summary>
    /// 取某条证据所属会话的全部打点，映射成播放器位置。
    /// </summary>
    /// <returns>按会话内偏移升序；找不到证据或没有打点时返回空。</returns>
    public async Task<IReadOnlyList<PunchTarget>> ForEvidenceAsync(
        string evidenceId,
        CancellationToken cancellationToken = default)
    {
        var entries = await _index.LoadAllAsync(cancellationToken);
        var anchor = entries.FirstOrDefault(e => string.Equals(e.EvidenceId, evidenceId, StringComparison.Ordinal));

        if (anchor is null)
        {
            return [];
        }

        return await ForSessionAsync(anchor.SessionId, entries, cancellationToken);
    }

    /// <summary>取某个会话的全部打点并映射。只给 [ForEvidenceAsync] 用。</summary>
    private async Task<IReadOnlyList<PunchTarget>> ForSessionAsync(
        string sessionId,
        IReadOnlyList<RecordingEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var sessionEntries = entries
            .Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
            .OrderBy(e => e.StartedAt)
            .ToList();

        if (sessionEntries.Count == 0)
        {
            return [];
        }

        var timeline = new RecordingTimeline(
            sessionEntries.Select(e => new TimelineSegment(e.Location.Value, e.Duration)).ToList());

        var allPunches = await _punches.LoadAllAsync(cancellationToken);
        var targets = new List<PunchTarget>();

        foreach (var punch in allPunches.Where(p => string.Equals(p.SessionId, sessionId, StringComparison.Ordinal)))
        {
            var position = timeline.LocatePunch(punch);
            if (position is null)
            {
                continue;
            }

            // 时间轴按顺序排过，段索引直接对应 sessionEntries 的下标。
            var entry = sessionEntries[position.SegmentIndex];

            targets.Add(new PunchTarget(
                punch.PunchId,
                entry.EvidenceId,
                position.SegmentIndex,
                position.OffsetInSegment.TotalSeconds,
                punch.MonotonicOffsetMilliseconds / 1000.0,
                punch.WaybillNumber.Value,
                punch.PunchedAt));
        }

        // 必须按**会话全局**偏移排序，不能按段内偏移 ——
        // 否则「第 1 段的第 2 秒」会排到「第 0 段的第 5 秒」前面。
        targets.Sort((a, b) => a.SessionOffsetSeconds.CompareTo(b.SessionOffsetSeconds));
        return targets;
    }
}
