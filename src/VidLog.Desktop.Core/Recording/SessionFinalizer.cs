using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Recording;

/// <summary>录制期落下的一个分段（MKV 中间容器）。</summary>
/// <param name="Sequence">同一会话内的顺序。</param>
/// <param name="SourcePath">磁盘上的绝对路径。**只在这里用绝对路径**，不进索引。</param>
public sealed record SegmentProduct(
    int Sequence,
    string SourcePath,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt);

/// <summary>单个分段的收尾结果。</summary>
public sealed record FinalizedSegment(
    SegmentProduct Source,
    string? PublishedPath,
    RelativePath? Location,
    ContentHash? ContentHash,
    string? FailureReason)
{
    /// <summary>
    /// 这个分段是否**完整走完了收尾**：产出可播成品 + 算过哈希 + 已写入索引。
    /// </summary>
    /// <remarks>
    /// 三个条件缺一不可，<see cref="FailureReason"/> 必须为空。
    /// <para>
    /// 特别地，**写索引失败也算失败**：文件在盘上、哈希也对，但用户检索不到 ——
    /// 那对「找得到自己的证据」这个产品承诺来说等于没入库。
    /// 少了 <see cref="FailureReason"/> 这个条件时正是这个 bug：
    /// 索引写失败的会话会被标成「已入库」（测试抓到的）。
    /// </para>
    /// <para>
    /// 注意 <see cref="PublishedPath"/> 与 <see cref="ContentHash"/> 仍然带着值 ——
    /// 调用方要判断「文件到底在不在盘上」时看它们，不受本属性影响。
    /// </para>
    /// </remarks>
    public bool IsPublished =>
        PublishedPath is not null && ContentHash is not null && FailureReason is null;
}

/// <summary>一次收尾的结果。</summary>
public sealed record FinalizeOutcome(
    RecordingSessionState State,
    StopReason Reason,
    IReadOnlyList<FinalizedSegment> Segments,
    string? FailureReason)
{
    public bool Succeeded => State == RecordingSessionState.Indexed;
}

/// <summary>
/// 录制收尾 —— **不变量 I9 的唯一落点**。
/// </summary>
/// <remarks>
/// <para>
/// 规格 §4.1 收紧尾约束：任何进入「收尾中」的路径，都必须走**同一套收尾逻辑**
/// （封文件、算哈希、写索引），**不得有旁路**。
/// </para>
/// <para>
/// 所以这个类只有一个公开入口 <see cref="FinalizeAsync"/>，且它**不接收**
/// 「为什么停」以外的任何分支信息 —— <see cref="StopReason"/> 只被原样带进结果，
/// 不参与任何 if。想看有没有旁路，看这个方法有没有第二个入口就够了。
/// </para>
/// <para>
/// 五条停录路径（手动 / 同码复扫 / 静止超时 / 时长兜底 / 孤儿收尾）加上两条
/// 主动安全收尾（存储将满 / 设备异常），全部走这里。
/// </para>
/// </remarks>
public sealed class SessionFinalizer
{
    private readonly RemuxPipeline _remux;
    private readonly DecodeVerifier _verifier;
    private readonly IRecordingIndex _index;
    private readonly string _archiveRoot;
    private readonly IAppLogger _logger;

    public SessionFinalizer(
        RemuxPipeline remux,
        DecodeVerifier verifier,
        IRecordingIndex index,
        string archiveRoot,
        IAppLogger? logger = null)
    {
        _remux = remux;
        _verifier = verifier;
        _index = index;
        _archiveRoot = archiveRoot;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>规格 §4.1 的收尾序列：封闭 → remux → 实际解码校验 → 算哈希 → 写索引。</summary>
    public async Task<FinalizeOutcome> FinalizeAsync(
        string sessionId,
        WaybillNumber waybill,
        string sourceDeviceId,
        IReadOnlyList<SegmentProduct> segments,
        StopReason reason,
        CancellationToken cancellationToken = default)
    {
        if (segments.Count == 0)
        {
            return new FinalizeOutcome(
                RecordingSessionState.FinalizeFailed,
                reason,
                [],
                "会话没有任何分段可收尾");
        }

        var finalized = new List<FinalizedSegment>(segments.Count);
        string? firstFailure = null;

        foreach (var segment in segments.OrderBy(s => s.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await FinalizeSegmentAsync(
                sessionId, waybill, sourceDeviceId, segment, cancellationToken);

            if (!result.IsPublished && firstFailure is null)
            {
                // 兜底：理论上不会走到 ??，但「标记为失败却给不出原因」
                // 会让诊断包变得没用，宁可给一句笼统的。
                firstFailure = result.FailureReason ?? "收尾失败（未给出原因）";
            }

            finalized.Add(result);
        }

        // 只要有一个分段没通过校验，整个会话就**不得**标记为正常入库（规格 §4.1）。
        var allPublished = finalized.All(s => s.IsPublished);
        var state = allPublished
            ? RecordingSessionState.Indexed
            : RecordingSessionState.FinalizeFailed;

        // 收尾是「一次录制到底有没有变成可检索的证据」的那条线，
        // 而它是这个应用**唯一会丢证据**的地方 —— 成败都留痕。
        // 失败那条带上原因：remux 失败 / 解码校验不过 / 写索引失败，
        // 三种要修的东西完全不同，而用户在界面上一律只看到「收尾失败」。
        _logger.Log(
            allPublished ? LogLevel.Info : LogLevel.Error,
            "收尾",
            allPublished
                ? $"{waybill.Value} 收尾完成（{finalized.Count} 段，停因 {reason}）"
                : $"{waybill.Value} 收尾失败：{firstFailure}",
            new Dictionary<string, object?>
            {
                ["会话"] = sessionId,
                ["停因"] = reason.ToString(),
                ["分段数"] = finalized.Count,
                ["成功段数"] = finalized.Count(s => s.IsPublished),
            });

        return new FinalizeOutcome(state, reason, finalized, allPublished ? null : firstFailure);
    }

    private async Task<FinalizedSegment> FinalizeSegmentAsync(
        string sessionId,
        WaybillNumber waybill,
        string sourceDeviceId,
        SegmentProduct segment,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(segment.SourcePath))
        {
            return new FinalizedSegment(segment, null, null, null, $"分段文件不存在：{segment.SourcePath}");
        }

        // §6.2：进索引的只有相对路径。
        var location = BuildLocation(waybill, sessionId, segment);
        var destination = Path.Combine(_archiveRoot, location.Value);

        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        // 1. 无损 remux：MKV 中间容器 → MP4 成品
        var remux = await _remux.RemuxAsync(segment.SourcePath, destination, cancellationToken);
        if (!remux.Succeeded)
        {
            // 源 MKV 一律保留 —— 它是此刻唯一的副本（I2）。
            return new FinalizedSegment(segment, null, null, null, remux.FailureReason);
        }

        // 2. 实际解码校验 —— 校验失败不得入库为「正常」（§3.1.4）
        var verification = await _verifier.VerifyAsync(destination, cancellationToken);
        if (!verification.IsPlayable)
        {
            return new FinalizedSegment(segment, destination, location, null, verification.FailureReason);
        }

        // 3. 内容哈希（§3.6.1：指纹 = 内容哈希 + 单号 + 录制时间）
        ContentHash contentHash;
        try
        {
            contentHash = await ContentHasher.ComputeFileHashAsync(destination, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FinalizedSegment(segment, destination, location, null, $"算哈希失败：{ex.Message}");
        }

        // 4. 写索引
        var entry = new RecordingEntry(
            EvidenceId: $"{sessionId}-{segment.Sequence:000}",
            SessionId: sessionId,
            Waybill: waybill,
            StartedAt: segment.StartedAt,
            EndedAt: segment.EndedAt,
            Duration: segment.EndedAt - segment.StartedAt,
            Location: location,
            ContentHash: contentHash,
            SourceDeviceId: sourceDeviceId);

        try
        {
            await _index.AddAsync(entry, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 成品已经落盘且可播，但没进索引 —— 不能算收尾成功，
            // 否则用户检索不到这条，等于「看起来存在其实找不到」。
            return new FinalizedSegment(segment, destination, location, contentHash, $"写索引失败：{ex.Message}");
        }

        return new FinalizedSegment(segment, destination, location, contentHash, null);
    }

    /// <summary>
    /// 归档层里的相对路径。规格 §6.2：**绝对路径在应用重装、容器变更后必然失效**，
    /// 所以进索引的只有这一段。
    /// </summary>
    /// <remarks>
    /// 规则本身搬去了 <see cref="ArchiveLayout"/> —— 远端上传（M5）要把手机传上来的
    /// 录像落在同一个布局上，两处各写一遍必然走岔，而走岔了不会报错。
    /// </remarks>
    private static RelativePath BuildLocation(WaybillNumber waybill, string sessionId, SegmentProduct segment)
        => ArchiveLayout.BuildLocation(waybill, sessionId, segment.Sequence, segment.StartedAt);
}
