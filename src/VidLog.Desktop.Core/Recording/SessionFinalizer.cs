using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
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
    private readonly StorageLocations _storage;
    private readonly IAppLogger _logger;
    private readonly ArchiveRelay? _relay;
    private readonly ILabelStore? _labels;

    /// <param name="archiveRoot">
    /// 成品落在哪儿。**只认一个根的那种写法**（老调用点与测试最常用的那种）——
    /// 多磁盘的装配走下面那个收 <see cref="StorageLocations"/> 的重载。
    /// </param>
    public SessionFinalizer(
        RemuxPipeline remux,
        DecodeVerifier verifier,
        IRecordingIndex index,
        string archiveRoot,
        IAppLogger? logger = null,
        ArchiveRelay? relay = null,
        ILabelStore? labels = null)
        : this(remux, verifier, index, StorageLocations.Single(archiveRoot), logger, relay, labels)
    {
    }

    /// <param name="storage">
    /// 成品落盘位置（设计图 `_43` 的多磁盘）。
    /// </param>
    /// <param name="relay">
    /// 把成品再发一份到归档层（规格 §3.4.6 的 NAS / 挂载盘 / 网盘）。
    /// <b>归档层就是本机时传 <see langword="null"/></b> —— 那时本机这一份就是归档层那一份，
    /// 发布是空操作，建一个对象只会让「发过没有」这个问题多一个没意义的答案。
    /// </param>
    /// <param name="labels">
    /// 标签存储。**只用来写「发货/退货」**（设计图 `_35` 左上角那个切换）。
    /// <b>不传 = 不给这段录像打类型标签</b>（老装配与测试）——
    /// 那时检索页按类型筛会把它当成「未标注」，与从前完全一样。
    /// </param>
    public SessionFinalizer(
        RemuxPipeline remux,
        DecodeVerifier verifier,
        IRecordingIndex index,
        StorageLocations storage,
        IAppLogger? logger = null,
        ArchiveRelay? relay = null,
        ILabelStore? labels = null)
    {
        _remux = remux;
        _verifier = verifier;
        _index = index;
        _storage = storage;
        _logger = logger ?? NullLogger.Instance;
        _relay = relay;
        _labels = labels;
    }

    /// <summary>规格 §4.1 的收尾序列：封闭 → remux → 实际解码校验 → 算哈希 → 写索引。</summary>
    /// <param name="spec">
    /// 本次录制的规格（编码 / 分辨率），写进索引（规格 §3.1.7 的连带项）。
    /// 排在最后且可选 —— 时长档位那些调用点（大多是测试）不该被它牵连着全改一遍。
    /// </param>
    /// <param name="businessType">
    /// 这一段的业务类型（发货 / 退货，设计图 `_35` 左上角那个切换）。
    /// <para>
    /// ⚠️ 它是**分段开始时**记下来的那一档，不是收尾时的当前档 ——
    /// 换件之后旧分段还在后台收尾，用当前档会把退回件的旧段标成发货。
    /// </para>
    /// <para>
    /// <see langword="null"/> = 不打标签（老调用点与测试），检索页按类型筛时
    /// 算「未标注」，与从前完全一样。
    /// </para>
    /// </param>
    public async Task<FinalizeOutcome> FinalizeAsync(
        string sessionId,
        WaybillNumber waybill,
        string sourceDeviceId,
        IReadOnlyList<SegmentProduct> segments,
        StopReason reason,
        CancellationToken cancellationToken = default,
        RecordingSpec? spec = null,
        BusinessType? businessType = null)
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
                sessionId, waybill, sourceDeviceId, segment, spec, businessType, cancellationToken);

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
        RecordingSpec? spec,
        BusinessType? businessType,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(segment.SourcePath))
        {
            return new FinalizedSegment(segment, null, null, null, $"分段文件不存在：{segment.SourcePath}");
        }

        // §6.2：进索引的只有相对路径。
        var location = BuildLocation(waybill, sessionId, segment);

        // ⚠️ **每一段收尾时现挑一次盘**，不是装配时定死一个（设计图 `_43` 的原话
        // 「每个磁盘只剩下预留空间时，会自动换到下一个」）。一次打包可能横跨
        // 好几段、跨好几个小时，定死一个的话第一块盘满了之后**所有**后续录像
        // 都会写失败 —— 而那时用户什么都没改过。
        var destination = Path.Combine(_storage.ActiveRoot, location.Value);

        // 留痕（`AGENTS.md` §6）：**写到哪块盘上了**是这一批新增的事实，
        // 而它恰恰是事后唯一能解释「这条录像在 D 盘、那条在 C 盘」的东西。
        // ⚠️ 只在多磁盘时才记 —— 单根是绝大多数机器，每段都记一条纯属噪音。
        if (_storage.Slots.Count > 0)
        {
            _logger.Log(LogLevel.Info, "落盘", $"{waybill.Value} 这一段写在 {_storage.ActiveRoot}",
                new Dictionary<string, object?>
                {
                    ["会话"] = sessionId,
                    ["段号"] = segment.Sequence,
                    ["原因"] = _storage.ActiveNote,
                });
        }

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
            SourceDeviceId: sourceDeviceId,
            // 规格 §3.1.7 的连带项：索引要记编码 / 分辨率 / **方向**。
            // 规格为空（老调用点、或探测没跑成）时是 null —— 容量估算那边
            // 靠 null 才敢回落到保守值（见 CleanupPlanner.EstimateBytes）。
            Codec: spec?.Codec.ToString(),
            Resolution: spec?.Resolution.ToString(),
            // ⚠️ 2026-09-29 起**不再恒为 null**：需求方裁决「要完整三档方向」
            // ⇒ 电脑端也写它（规格 §3.1.7 的那条连带要求随之对电脑端生效）。
            //
            // ⚠️ 存的是**电脑端自己的枚举名**（`None` / `Left90` / `Right90` /
            // `UpsideDown`），与手机端那三个（`landscapeLeft` / `portrait` /
            // `landscapeRight`）**不是同一套值** —— 两端的「方向」含义本来就不同
            // （手机是持机方向、电脑是画面转多少度）。手机端读不出来会**回落默认档**
            // （`RecordingOrientation.fromConfig` 的既有行为），不会炸。
            Orientation: spec?.Rotation.ToString());

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

        // 4.5 打「发货 / 退货」标签（设计图 `_35` 的那个切换）。
        //
        // ⚠️ **放在这里而不是 RecordingCoordinator.ReportFinalize**：证据 ID
        // （`{会话}-{段号:000}`）就是在上面几行算出来的，换个地方写就要把这条公式
        // 再抄一遍 —— 而抄歪了不会报错，只会出现「标签挂在一个不存在的证据上」。
        //
        // ⚠️ **失败不算收尾失败。** 标签是「可修正的描述」（I5），录像本身才是证据：
        // 标签没写上，用户还能在检索页补；为了一个描述性字段把一次已经落盘、
        // 已经进索引的录制判成失败，那是**用次要的东西毁掉主要的东西**。
        // 所以这里只 Warn 留痕，不 return 失败。
        if (_labels is not null && businessType is not null)
        {
            try
            {
                await _labels.SetAsync(
                    entry.EvidenceId,
                    LabelKeys.BusinessType,
                    BusinessTypes.ToValue(businessType.Value),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Log(
                    LogLevel.Warn,
                    "标签",
                    $"{waybill.Value} 第 {segment.Sequence} 段的业务类型标签没写上：{ex.Message}",
                    new Dictionary<string, object?>
                    {
                        ["会话"] = sessionId,
                        ["证据"] = entry.EvidenceId,
                        ["业务类型"] = BusinessTypes.ToValue(businessType.Value),
                    });
            }
        }

        // 5. 发一份到归档层（规格 §3.4.6）。
        //
        // ⚠️ **失败不算收尾失败，也不清本机这一份。** 本机这一份已经在索引里、
        // 能播能检索 —— 它是这个系统的第一份。归档层那份没上去的代价是
        // 「这条还不能被清理」（回查会不通过 ⇒ 拒删），而不是「这条录像没了」。
        // 这正是 I2 的方向：**宁可多占地方，不可少一份证据**。
        if (_relay is not null)
        {
            await _relay.PublishAsync(location, destination, cancellationToken);
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
