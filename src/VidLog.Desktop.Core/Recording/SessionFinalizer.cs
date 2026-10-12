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

/// <summary>
/// 一片源分段最终变成了什么 —— <see cref="Source"/> 是**真的那一片**，
/// 后面几个字段描述它落进的那个成品。
/// </summary>
/// <remarks>
/// ⚠️ <b>2026-10-12 起一场录制只有一个成品</b>（需求方裁定「一个单号一个文件」），
/// 所以 <see cref="FinalizeOutcome.Segments"/> 里会有**好几条指向同一个</b>
/// <see cref="PublishedPath"/> / <see cref="Location"/> / <see cref="ContentHash"/>。
/// 那不是重复记账，是「这几片拼成了它」—— 它保住了「哪几片、各自的起止时刻」
/// 这件事的可观测性，而少一片就是成品里少一截画面、**且不会报错**。
/// </remarks>
public sealed record FinalizedSegment(
    SegmentProduct Source,
    string? PublishedPath,
    RelativePath? Location,
    ContentHash? ContentHash,
    string? FailureReason)
{
    /// <summary>
    /// 这一片是否**完整走完了收尾**：产出可播成品 + 算过哈希 + 已写入索引。
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

    /// <summary>这一段有没有成功发到归档层（**没配归档层——归档层就是本机——时恒真**）。</summary>
    /// <remarks>
    /// 发布于否**不影响** <see cref="IsPublished"/>：本机那一份已经在索引里、能播能检索，
    /// 归档层没上去的代价是「这条还不能被清理」，不是「这条没了」。
    /// <para>
    /// 它是 T21 的判据之一：收尾后要不要丢掉 <c>work/</c> 里的源 MKV，
    /// 就看这里（发不上去就先留着，见 <see cref="FinalizeOutcome.ArchiveComplete"/>）。
    /// </para>
    /// </remarks>
    public bool ArchivePublished { get; init; } = true;
}

/// <summary>一次收尾的结果。</summary>
public sealed record FinalizeOutcome(
    RecordingSessionState State,
    StopReason Reason,
    IReadOnlyList<FinalizedSegment> Segments,
    string? FailureReason)
{
    public bool Succeeded => State == RecordingSessionState.Indexed;

    /// <summary>每一段都成功发到归档层了（没配归档层时恒真）。</summary>
    /// <remarks>
    /// ⚠️ 只有 <see cref="Succeeded"/> **且**它成立，源 MKV 才真的可以丢 ——
    /// 缺了它，`work/` 里那份就是归档层上那份的替身（T21）。
    /// </remarks>
    public bool ArchiveComplete => Segments.All(s => s.ArchivePublished);
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

        cancellationToken.ThrowIfCancellationRequested();

        var ordered = segments.OrderBy(s => s.Sequence).ToList();

        // ★ 2026-10-12：**一场录制收尾成一条视频**（需求方裁定「一个单号一个文件」）。
        // 原来是逐段各出一个 MP4（`_000` 预录开场段 + `_001` 正式段 + 滚出来的 `_002`…），
        // 用户拿到的是「一个包裹好几个文件」。
        var product = await FinalizeProductAsync(
            sessionId,
            waybill,
            sourceDeviceId,
            ordered,
            // 整场的时间轴取**包络**：最早那片的起点 → 最晚那片的终点。
            startedAt: ordered.Min(s => s.StartedAt),
            endedAt: ordered.Max(s => s.EndedAt),
            spec,
            businessType,
            cancellationToken);

        // 结果按**源分段一对多展开**：每一片都指向同一个成品（见 `FinalizedSegment`）。
        var finalized = ordered.Select(s => product with { Source = s }).ToList();

        // 只要有一个分段没通过校验，整个会话就**不得**标记为正常入库（规格 §4.1）。
        // 合并之后这件事的形态更硬：整场就是一次 remux，它不成就是全都不成。
        var allPublished = product.IsPublished;
        var state = allPublished
            ? RecordingSessionState.Indexed
            : RecordingSessionState.FinalizeFailed;

        // 兜底：理论上不会走到 ??，但「标记为失败却给不出原因」
        // 会让诊断包变得没用，宁可给一句笼统的。
        var firstFailure = allPublished
            ? null
            : product.FailureReason ?? "收尾失败（未给出原因）";

        // 收尾是「一次录制到底有没有变成可检索的证据」的那条线，
        // 而它是这个应用**唯一会丢证据**的地方 —— 成败都留痕。
        // 失败那条带上原因：remux 失败 / 解码校验不过 / 写索引失败，
        // 三种要修的东西完全不同，而用户在界面上一律只看到「收尾失败」。
        _logger.Log(
            allPublished ? LogLevel.Info : LogLevel.Error,
            "收尾",
            allPublished
                ? $"{waybill.Value} 收尾完成（由 {finalized.Count} 段合成一条，停因 {reason}）"
                : $"{waybill.Value} 收尾失败：{firstFailure}",
            new Dictionary<string, object?>
            {
                ["会话"] = sessionId,
                ["停因"] = reason.ToString(),
                ["分段数"] = finalized.Count,
                // ⚠️ 记的是**盘上真实存在的那份**（`PublishedPath`），不是 `Location`：
                // 失败分支里有几处 `Location` 是「本来打算落的名字」（校验没过 / 算哈希
                // 失败 / 撞名堆满），那个名字下面根本没有文件 —— 事后照它去找会扑空。
                // 而 `PublishedPath` 在每个分支里都指向真实存在的那一份（没通过校验的
                // 那个临时名、已落盘但没进索引的成品），落不成时才是 null。
                ["成品"] = product.PublishedPath,
            });

        return new FinalizeOutcome(state, reason, finalized, firstFailure);
    }

    /// <summary>
    /// 把**一整场**的分段合成一个成品，再走收尾的后半段（校验 → 哈希 → 落盘 → 索引 → 标签 → 发布）。
    /// </summary>
    /// <param name="segments">**已按 <see cref="SegmentProduct.Sequence"/> 排好序**的源分段。</param>
    private async Task<FinalizedSegment> FinalizeProductAsync(
        string sessionId,
        WaybillNumber waybill,
        string sourceDeviceId,
        IReadOnlyList<SegmentProduct> segments,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        RecordingSpec? spec,
        BusinessType? businessType,
        CancellationToken cancellationToken)
    {
        // 先看每一片在不在 —— **排在最前面**，因为合并是从盘上读，少一片时
        // ffmpeg 只会对着列表文件报一句「No such file」，还得倒推是哪一片。
        //
        // ⚠️ **一片不齐就整场失败**，这不只是 §4.1 的口径（有一个分段没通过校验，
        // 整场就不得标记为正常入库），更是合并的物理事实：少一片 = 成品里**少一截
        // 画面**，而这件事**不会报错** —— 事后谁也看不出来中间缺了一块。
        // 宁可整场失败让它下次重试（源 MKV 全在）。
        foreach (var segment in segments)
        {
            if (!File.Exists(segment.SourcePath))
            {
                return new FinalizedSegment(
                    segments[0], null, null, null, $"分段文件不存在：{segment.SourcePath}");
            }
        }

        // §6.2：进索引的只有相对路径。
        //
        // ⚠️ 「发货 / 退货」那一层取的是**开始工作时选的那一档**：`businessType`
        // 是会话建好那一刻冻结下来的（见 `RecordingSession.BusinessType`），不是
        // 此刻的档位 —— 换件之后旧的那场还在后台收尾，用当前档会把它标错。
        // 没给（老调用点 / 测试）时回落到「发货」，与 `CloudUploadService` 的回退一致。
        var relative = ArchiveLayout.BuildLocation(
            waybill, businessType ?? BusinessType.Outbound, startedAt);

        // ⚠️ **一场收尾时现挑一次盘**，不是装配时定死一个（设计图 `_43` 的原话
        // 「每个磁盘只剩下预留空间时，会自动换到下一个」）。合并之后一场只落一次盘，
        // 所以这个选择从「每段一次」变成「一场一次」—— 语义不变，只是次数少了。
        var root = _storage.ActiveRoot;
        var parent = Path.GetDirectoryName(Path.Combine(root, relative.Value)) ?? root;

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ⚠️ 与下面 `File.Move` 那处同一条：这一层原本就裸着，但「盘拔了 /
            // 归档盘变成只读 / 那块盘临时掉线」在这里是**真实会发生的**（多磁盘那档
            // 正是可移动盘），而它抛出去同样会穿透 `OrphanRecovery` 的循环。
            // 源 MKV 一片没动 ⇒ 下次启动照旧按孤儿重试。
            return new FinalizedSegment(
                segments[0], null, null, null, $"归档目录建不出来（{parent}）：{ex.Message}");
        }

        // 留痕（`AGENTS.md` §6）：**写到哪块盘上了**是这一批新增的事实，
        // 而它恰恰是事后唯一能解释「这条录像在 D 盘、那条在 C 盘」的东西。
        // ⚠️ 只在多磁盘时才记 —— 单根是绝大多数机器，每场都记一条纯属噪音。
        if (_storage.Slots.Count > 0)
        {
            _logger.Log(LogLevel.Info, "落盘", $"{waybill.Value} 这一场写在 {root}",
                new Dictionary<string, object?>
                {
                    ["会话"] = sessionId,
                    ["分段数"] = segments.Count,
                    ["原因"] = _storage.ActiveNote,
                });
        }

        // ⚠️ **先合到同目录下一个临时名，再原子改名**，而不是「先探一下撞不撞、
        // 再直接往最终名字写」。理由（2026-10-12 换形状时新出现的）：
        //
        // 新文件名里**没有会话号** ⇒ 同一个单号的第二场**必然**撞名；而收尾
        // 确实可能有两份同时在跑 —— 换件那条路是在后台收尾的
        // （`RecordingCoordinator.PendingFinalization`），用户随手重扫同一个单号
        // 就会让两场同号录像几乎同时收尾。探一下再写的话两边都会看到「不撞」，
        // 后一个把前一个**整份覆盖掉**：那是 I2 说的「少一份证据」，而且事后
        // 在两边的账里都完全看不出来（索引各记各的，盘上只剩一个文件）。
        //
        // `File.Move(overwrite: false)` 是原子的：谁抢到算谁的，输的那个换 `_2`。
        // 临时名带会话号 ⇒ 两份并发收尾各写各的临时文件，不会互相踩。
        var staging = Path.Combine(parent, $".{sessionId}.partial.mp4");

        // 1. 无损合并：多片 MKV 中间容器 → 一个 MP4 成品（只有一片时等价于原来的 remux）
        var merge = await _remux.MergeAsync(
            [.. segments.Select(s => s.SourcePath)], staging, cancellationToken);
        if (!merge.Succeeded)
        {
            // 源 MKV 一律保留 —— 它是此刻唯一的副本（I2）。
            return new FinalizedSegment(segments[0], null, null, null, merge.FailureReason);
        }

        // 2. 实际解码校验 —— 校验失败不得入库为「正常」（§3.1.4）
        // ⚠️ 校验与哈希都在**临时名**上做，内容一样，省一次改名后再读。
        var verification = await _verifier.VerifyAsync(staging, cancellationToken);
        if (!verification.IsPlayable)
        {
            // 没通过校验的这一份**不搬去正式名字**（搬过去就成了一个能骗过
            // 「回查」的坏成品）。它留在临时名下，不产生索引条目。
            return new FinalizedSegment(segments[0], staging, relative, null, verification.FailureReason);
        }

        // 3. 内容哈希（§3.6.1：指纹 = 内容哈希 + 单号 + 录制时间）
        ContentHash contentHash;
        try
        {
            contentHash = await ContentHasher.ComputeFileHashAsync(staging, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FinalizedSegment(segments[0], staging, relative, null, $"算哈希失败：{ex.Message}");
        }

        // 3.5 定名落盘：从 `<单号>.mp4` 起试，撞名就 `_2`、`_3`…（需求方 2026-10-12 裁定）。
        // 原子性来自 `File.Move(overwrite: false)` —— 判「撞没撞」与「占住」是同一步，
        // 中间没有缝（见上面为什么不能先探再写）。
        var location = relative;
        string? destination = null;
        for (var ordinal = 0; ordinal <= ArchiveLayout.MaxDuplicates; ordinal++)
        {
            // ⚠️ 后缀只进**文件名**，不进索引里的 EvidenceId（那个仍是 `{会话}-000`）：
            // 同单号的两场因此是两条**互相独立**的证据，各自可回查、各自可清理。
            location = ArchiveLayout.Deduplicated(relative, ordinal);
            var candidate = Path.Combine(root, location.Value);

            try
            {
                File.Move(staging, candidate, overwrite: false);
                destination = candidate;
                break;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // 撞名 —— 换下一个后缀重试（不是错误）。
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ⚠️ **非撞名**的落盘故障（盘满 / 目标目录被删 / 路径过长 / 文件被占）
                // 不许穿出去变成异常：契约是「返回失败原因 + 源 MKV 留着 + 下次启动
                // 按孤儿重试」（I2）。异常会跳过 `State`/`Outcome` 的落定，而孤儿回收
                // 那条循环会**当场中断后面所有孤儿**（`RecoverAsync` 里没有 catch）。
                // 临时名那一份还在盘上，所以 `PublishedPath` 照实指它。
                return new FinalizedSegment(
                    segments[0], staging, relative, contentHash, $"落盘失败：{ex.Message}");
            }
        }

        if (destination is null)
        {
            // 兜底：同一个单号堆了这么多份还不清，那是用户该处理的事。
            // **绝不覆盖已有的那一个** —— 宁可这一场收尾失败让它下次重试。
            return new FinalizedSegment(
                segments[0], staging, relative, contentHash,
                $"同一单号的成品已经堆到 {ArchiveLayout.MaxDuplicates + 1} 份，先清理再录（不肯覆盖已有的证据）。");
        }

        if (location != relative)
        {
            // 落到了 `_2` 之类的名字上 —— 这是「同一个单号录了不止一场」的**唯一**痕迹，
            // 事后想搞清楚「这个 _2 是哪来的」只能看这条日志。
            _logger.Log(LogLevel.Info, "落盘", $"{waybill.Value} 撞名，这一场落到 {location.Value}",
                new Dictionary<string, object?>
                {
                    ["会话"] = sessionId,
                    ["单号"] = waybill.Value,
                });
        }

        // 4. 写索引
        //
        // ⚠️ **证据 ID 的形状一个字节都没变**（仍是 `{会话}-000`）：一场恒一个成品，
        // 序号那一格不再有第二个值。标签、清理流水、上传队列、索引的所有消费者
        // 因此全都不用改 —— 这一批改动刻意没有波及它们。
        var entry = new RecordingEntry(
            EvidenceId: $"{sessionId}-000",
            SessionId: sessionId,
            Waybill: waybill,
            StartedAt: startedAt,
            EndedAt: endedAt,
            Duration: endedAt - startedAt,
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
            return new FinalizedSegment(segments[0], destination, location, contentHash, $"写索引失败：{ex.Message}");
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
                    $"{waybill.Value} 的业务类型标签没写上：{ex.Message}",
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
        // ⚠️ 结果**要带出去**（T21）：没发上去的话 `work/` 里那份源 MKV 得留着。
        // 原来是丢掉的 —— 于是会话那一头根本无从判断能不能丢工作目录。
        var archived = true;
        if (_relay is not null)
        {
            var published = await _relay.PublishAsync(
                entry.EvidenceId, location, destination, cancellationToken);
            archived = published.Published;
        }

        return new FinalizedSegment(segments[0], destination, location, contentHash, null)
        {
            ArchivePublished = archived,
        };
    }
}
