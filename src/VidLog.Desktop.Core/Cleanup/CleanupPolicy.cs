using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Cleanup;

/// <summary>归档层的种类（规格 §3.4.6 的**四种**）。</summary>
/// <remarks>
/// ⚠️ <b>枚举值落进设置文件（<c>settings.json</c> 的 <c>ArchiveBackend</c>），
/// 所以顺序即格式，别重排。</b> <see cref="MountedDrive"/> 是 2026-09-27 追加的，
/// 值取 3 而不是插在中间 —— 插进去会把所有老配置的意思改掉，而且是**静默**的。
/// </remarks>
public enum ArchiveBackendKind
{
    /// <summary>归档层就是**本机磁盘**。</summary>
    /// <remarks>
    /// 规格 §3.5.1：此时该副本是**唯一副本**，不提供清理选项 ——
    /// 「显示了但禁用」和「根本不显示」是两回事，后者才符合「不提供」。
    /// </remarks>
    LocalDisk = 0,

    /// <summary>NAS —— **目录型**，与挂载盘共用一份实现。</summary>
    Nas = 1,

    /// <summary>百度网盘 —— **网盘型**，走它的接口。</summary>
    Cloud = 2,

    /// <summary>
    /// 挂载网络驱动器（`Z:\` 或 `\\nas\vidlog`）。
    /// </summary>
    /// <remarks>
    /// 规格 §3.4.6：让用户填一个盘符或 UNC 路径，**网络那一层由用户自己在系统里解决**，
    /// 我们只当成一个目录用。⚠️ 这个路径**只当配置里的根**，绝不进索引
    /// （<c>RelativePath</c> 拒绝绝对路径与 UNC，规格 §6.2）。
    /// </remarks>
    MountedDrive = 3,
}

/// <summary>回查归档层的结果。</summary>
/// <param name="Exists">归档层上还在不在。</param>
/// <param name="FailureReason">回查失败的原因（查不了 ≠ 不存在）。</param>
public sealed record ArchiveVerifyResult(bool Exists, string? FailureReason)
{
    /// <summary>回查本身出错（网络断了、凭据失效）。</summary>
    /// <remarks>
    /// 这个状态**不能当成「不存在」** —— 那会导致误删（I8）。
    /// 查不了的时候唯一安全的动作是**不删**。
    /// </remarks>
    public bool CouldNotVerify => FailureReason is not null;
}

/// <summary>
/// 归档层。
/// </summary>
/// <remarks>
/// 规格 §3.5.4 是 I8 的唯一落点：清理前**必须**回查归档层，回查不通过**绝不删除**。
/// </remarks>
public interface IArchiveBackend
{
    ArchiveBackendKind Kind { get; }

    /// <summary>回查某条录像的成品在归档层上还在不在。</summary>
    Task<ArchiveVerifyResult> VerifyAsync(RelativePath location, CancellationToken cancellationToken = default);
}

/// <summary>保留期策略（规格 §3.5.2）。</summary>
/// <param name="Mode">按什么清理。</param>
/// <param name="KeepDays">保留天数；<see cref="RetentionMode.KeepAll"/> 时无意义。</param>
/// <param name="MinFreeBytes">磁盘剩余低于它就清；<see cref="RetentionMode.ByDays"/> 时无意义。</param>
public sealed record RetentionPolicy(RetentionMode Mode, int? KeepDays = null, long? MinFreeBytes = null)
{
    /// <summary>
    /// 默认：**什么都不清**。
    /// </summary>
    /// <remarks>
    /// 这是刻意的默认值。规格 §6.2「数据删除必须极度克制」——
    /// 清理是一个会造成不可逆后果的动作，它必须是用户**主动开启**的，
    /// 不能因为「装完就有个默认 30 天」而在用户不知情时删掉东西。
    /// </remarks>
    public static RetentionPolicy KeepAll { get; } = new(RetentionMode.KeepAll);
}

public enum RetentionMode
{
    /// <summary>全部保留（默认）。</summary>
    KeepAll,

    /// <summary>按天数。</summary>
    ByDays,

    /// <summary>按磁盘剩余空间。</summary>
    BySpace,
}

/// <summary>
/// 按业务类型分开的保留期（规格 §3.5.2.1）。
/// </summary>
/// <remarks>
/// 需求方 2026-09-23 原话：「已备份后的本地保留期用户可自行选择，用下拉式选择
/// 不保留/3/5/7/10/15/30/，**发货和退货视频同样**」；追问后裁决
/// **发货与退货各自一份，不共用** —— 退货件争议多、体积小，
/// 实践上不会和发货用同一个天数。
/// </remarks>
public sealed record RetentionPolicies(RetentionPolicy Outbound, RetentionPolicy Return)
{
    /// <summary>两份都是「全部保留」—— 出厂默认（规格 §3.5.2 表格第一行）。</summary>
    public static RetentionPolicies KeepAll { get; } = new(RetentionPolicy.KeepAll, RetentionPolicy.KeepAll);

    /// <summary>这一条该用哪一份。</summary>
    public RetentionPolicy For(BusinessType type) =>
        type == BusinessType.Return ? Return : Outbound;

    /// <summary>
    /// 下拉里的选项，按界面上的先后（规格 §3.5.2.1）。
    /// </summary>
    /// <remarks>
    /// ⚠️ **「不保留」是 <see cref="RetentionMode.ByDays"/> 配 0 天，不是「立刻删」。**
    /// <see cref="CleanupPlanner.FreshWindow"/>（24 小时，规格 §3.5.3③）把它兜住了 ——
    /// 那条豁免**硬性、用户不可关闭**，所以实际生效是「备份后最快 24 小时清」。
    /// 界面**必须把这句话写出来**：用户选了「不保留」却看见东西还在，
    /// 不说清楚他会以为坏了（踩坑 #13「改了没反应的开关」）。
    /// </remarks>
    public static IReadOnlyList<(string Label, RetentionPolicy Policy)> Choices { get; } =
    [
        ("全部保留", RetentionPolicy.KeepAll),
        ("不保留", new RetentionPolicy(RetentionMode.ByDays, KeepDays: 0)),
        ("3 天", new RetentionPolicy(RetentionMode.ByDays, KeepDays: 3)),
        ("5 天", new RetentionPolicy(RetentionMode.ByDays, KeepDays: 5)),
        ("7 天", new RetentionPolicy(RetentionMode.ByDays, KeepDays: 7)),
        ("10 天", new RetentionPolicy(RetentionMode.ByDays, KeepDays: 10)),
        ("15 天", new RetentionPolicy(RetentionMode.ByDays, KeepDays: 15)),
        ("30 天", new RetentionPolicy(RetentionMode.ByDays, KeepDays: 30)),
    ];
}

/// <summary>一条可清理的候选。</summary>
public sealed record CleanupCandidate(RecordingEntry Entry, long SizeBytes, RelativePath Location, string Why);

/// <summary>一条被排除的录像，以及**为什么没清它**。</summary>
/// <remarks>
/// 规格 §3.5.5：用户要能问「这条为什么被删 / 为什么没删」。
/// 只给候选不给豁免，等于让人无从质疑。
/// </remarks>
public sealed record ExemptedEntry(RecordingEntry Entry, string Why);

/// <summary>一次清理的完整计划。</summary>
public sealed record CleanupPlan(
    IReadOnlyList<CleanupCandidate> Candidates,
    IReadOnlyList<ExemptedEntry> Exempted)
{
    public long TotalBytes => Candidates.Sum(c => c.SizeBytes);
}

/// <summary>
/// 清理计划的制定。
/// </summary>
/// <remarks>
/// <para>
/// 纯函数：给定「有哪些录像、多大、锁没锁、归档成功于何时、策略、现在几点」，
/// 算出该清谁。它**不碰文件系统** —— 真正删除在别处，而且要先回查归档层（I8）。
/// </para>
/// <para>
/// ⚠️ <b>谁来提供那份归档时刻表，目前还没有答案。</b>
/// 回执里就有（<c>ReceiptPayload.TimeAnchor</c>），但 <c>ReceiptStore</c> 只有
/// 按 id 查一个的 <c>FindAsync</c>，没有「一次读全表」。所以要真接上，
/// 第一步是给 <c>ReceiptStore</c> 加一个 <c>LoadAllAsync</c>。
/// <b>现在故意不加</b>：在本类有生产调用点之前，那会是一段没人调用、
/// 也没法验证的代码 —— 电脑端刚因为「写完了、测过了、没插电」吃过一次亏
/// （母仓 <c>HANDOFF.md</c> §6 第 19 条）。
/// </para>
/// </remarks>
public sealed class CleanupPlanner
{
    /// <summary>
    /// 最近这段时间内的录像**一律不清**。
    /// </summary>
    /// <remarks>
    /// 规格 §3.5.3 的第三条豁免。刚录完的东西往往还在被检查、被导出 ——
    /// 而清理是不可逆的，所以给它一个冷静期。
    /// </remarks>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// 按一份策略算一批候选与豁免（规格 §3.5.2.1 / §3.5.3）。
    /// </summary>
    /// <param name="archiveAnchors">
    /// <c>evidenceId</c> → **归档成功时刻**（回执里的 <c>timeAnchor</c>，外部时间锚）。
    /// <b>表里没有的那条就是「还没成功归档」，必然被豁免</b>（规格 §3.5.3①）——
    /// 所以这个参数是必填的，没有「不知道归档状态」这个中间态。
    /// </param>
    public CleanupPlan Plan(
        IReadOnlyList<RecordingEntry> entries,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> labels,
        IReadOnlyDictionary<string, DateTimeOffset> archiveAnchors,
        RetentionPolicy policy,
        DateTimeOffset now,
        long? freeBytes = null)
    {
        var candidates = new List<CleanupCandidate>();
        var exempted = new List<ExemptedEntry>();

        // 策略是「全部保留」时，谁都别动。
        if (policy.Mode == RetentionMode.KeepAll)
        {
            return new CleanupPlan(
                [], [.. entries.Select(e => new ExemptedEntry(e, "保留策略是「全部保留」"))]);
        }

        var eligible = new List<(RecordingEntry Entry, DateTimeOffset Anchor)>();

        foreach (var entry in entries)
        {
            // 规格 §3.5.3 的三条豁免，一条都不能少，**按它自己的编号顺序**判 ——
            // 顺序影响的是「理由怎么写」：一条既没归档又锁着的录像，
            // 两端都该说「唯一副本」，而不是一边说锁、一边说没归档。
            //
            // ① 未成功归档的 —— 唯一副本（I2）。
            if (!archiveAnchors.TryGetValue(entry.EvidenceId, out var anchor))
            {
                exempted.Add(new ExemptedEntry(entry, "还没成功归档到归档层，这是唯一副本"));
                continue;
            }

            // ② 被锁定（规格 §3.5.3②，硬豁免）。
            if (IsLocked(entry, labels))
            {
                exempted.Add(new ExemptedEntry(entry, "已被用户锁定"));
                continue;
            }

            // ③ 最近 24 小时内**录**的（不是归档的）。
            if (now - entry.EndedAt < FreshWindow)
            {
                exempted.Add(new ExemptedEntry(entry, $"录完还不到 {FreshWindow.TotalHours:0} 小时"));
                continue;
            }

            eligible.Add((entry, anchor));
        }

        switch (policy.Mode)
        {
            case RetentionMode.ByDays when policy.KeepDays is { } days:
                {
                    var cutoff = now.AddDays(-days);

                    foreach (var (entry, anchor) in eligible)
                    {
                        // ★ 起算点是**归档成功时刻**，不是录完时刻（规格 §3.5.2.1）。
                        // 依据是 §4.3 的合取式「归档成功 **且** 超过保留期」：
                        // 一台离线 35 天的机器若按录完时刻算，会在**刚归档那一瞬间**
                        // 就被删掉 —— 那等于绕开了「至少一份副本」（I2）的意图。
                        // 手机端 `lifecycle.dart` 用的是同一个字段、同一个算法。
                        if (anchor < cutoff)
                        {
                            candidates.Add(new CleanupCandidate(
                                entry, EstimateBytes(entry), entry.Location, $"备份于 {anchor:yyyy-MM-dd}，超过 {days} 天"));
                        }
                        else
                        {
                            exempted.Add(new ExemptedEntry(entry, $"还在 {days} 天保留期内"));
                        }
                    }

                    break;
                }

            case RetentionMode.BySpace when policy.MinFreeBytes is { } floor && freeBytes is { } free:
                {
                    if (free >= floor)
                    {
                        foreach (var (entry, _) in eligible)
                        {
                            exempted.Add(new ExemptedEntry(entry, "磁盘剩余充足，不需要清"));
                        }

                        break;
                    }

                    // 空间不够 —— **从最旧的开始**清，清到够了就停。
                    // 这里按录制时刻排，不按归档时刻：空间紧张时先腾掉最老的素材，
                    // 与「删哪个都不心疼」的直觉一致。（保留期的起算点仍是归档时刻。）
                    var need = floor - free;
                    long freed = 0;

                    foreach (var (entry, _) in eligible.OrderBy(e => e.Entry.EndedAt))
                    {
                        if (freed >= need)
                        {
                            exempted.Add(new ExemptedEntry(entry, "已经清够了"));
                            continue;
                        }

                        var size = EstimateBytes(entry);
                        candidates.Add(new CleanupCandidate(
                            entry, size, entry.Location, "磁盘剩余不足，按最旧的先清"));
                        freed += size;
                    }

                    break;
                }

            default:
                foreach (var (entry, _) in eligible)
                {
                    exempted.Add(new ExemptedEntry(entry, "策略参数不完整，什么都不清"));
                }

                break;
        }

        return new CleanupPlan(candidates, exempted);
    }

    /// <summary>
    /// 按业务类型各算一遍，再合起来（规格 §3.5.2.1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 刻意**不动 <see cref="Plan"/>**：它已经是「给一份策略、算一批候选」这个
    /// 正确的原语了，各算一遍再合并就够 —— 而且**分开算顺手保证了两份互不串**
    /// （改发货的档位不可能碰到退货的判断）。
    /// </para>
    /// <para>
    /// <b>没有业务类型标签的一律不清</b>，并把原因写进豁免列表。
    /// 与规格 §6.2「数据删除必须极度克制」同源：判不出它是发货还是退货，
    /// 就说不出它该用哪一份保留期。**猜错的代价是删掉证据，猜不出的代价只是占地方**
    /// —— 而后者是**看得见的**（就在豁免列表里，用户查得到「这条为什么没删」）。
    /// </para>
    /// <para>
    /// ⚠️ 两份策略都必须是「全部保留」或「按天数」。**「按空间」是全局的** ——
    /// 磁盘满不满跟业务类型无关 —— 那种情况仍走单份的 <see cref="Plan"/>。
    /// 需求方要的下拉只给这两类，所以正常路径到不了这里。
    /// </para>
    /// </remarks>
    public CleanupPlan PlanPerBusinessType(
        IReadOnlyList<RecordingEntry> entries,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> labels,
        IReadOnlyDictionary<string, DateTimeOffset> archiveAnchors,
        RetentionPolicies policies,
        DateTimeOffset now,
        long? freeBytes = null)
    {
        var candidates = new List<CleanupCandidate>();
        var exempted = new List<ExemptedEntry>();

        foreach (var group in entries.GroupBy(e => BusinessTypeOf(e, labels)))
        {
            if (group.Key is not { } type)
            {
                exempted.AddRange(group.Select(e => new ExemptedEntry(
                    e, "没有业务类型标签，判不出该用哪一份保留期，按「全部保留」处理")));

                continue;
            }

            var plan = Plan([.. group], labels, archiveAnchors, policies.For(type), now, freeBytes);

            candidates.AddRange(plan.Candidates);
            exempted.AddRange(plan.Exempted);
        }

        return new CleanupPlan(candidates, exempted);
    }

    /// <summary>
    /// 这条录像的业务类型；标签里没有就返回 null。
    /// </summary>
    /// <remarks>
    /// **不猜**：<see cref="BusinessTypes.TryParse"/> 认不出来时会给出 Outbound，
    /// 那个默认值对检索够用，但拿它去决定「删不删」不行 —— 见
    /// <see cref="PlanPerBusinessType"/> 的注释。
    /// </remarks>
    private static BusinessType? BusinessTypeOf(
        RecordingEntry entry, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> labels) =>
        labels.TryGetValue(entry.EvidenceId, out var entryLabels)
        && entryLabels.TryGetValue(LabelKeys.BusinessType, out var raw)
        && BusinessTypes.TryParse(raw, out var type)
            ? type
            : null;

    /// <summary>
    /// 这条录像的成品占多大。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用**时长推算**而不是去问文件系统：计划制定这一步刻意不碰磁盘
    /// （那样会有「算的过程中文件被别的东西删了」这类竞态）。
    /// 真实大小在执行阶段复核 —— 那里本来就要 stat 一次。
    /// </para>
    /// <para>
    /// ⚠️ <b>必须按录制规格估，不能用一个写死的系数</b>（规格 §3.5.5 的连带项）：
    /// 原话「分辨率/编码一变，每个文件的体积差好几倍……拿一个写死的系数算，
    /// 4K 下会错得离谱 —— 而电脑端「按空间清理」**正是用它决定删到够为止**，
    /// 估错就是『删了还不够』」。
    /// </para>
    /// <para>
    /// ⚠️ <b>表里的是纸面值</b>（按各档的常见码率取**偏大**的一侧），**没有真机标定** ——
    /// 与 §26 的静止阈值同一条账。取偏大是**有意的**：估小了不够删，
    /// 估大了只是多删一条而已（而多删的那条仍然要过回查与豁免）。
    /// </para>
    /// <para>
    /// ⚠️ 老条目没有那两个字段（追加字段之前录的）⇒ 按**默认档**（H.264 1080P）估，
    /// 而不是回到原来那个 640x480 的系数 —— 那个只对旧的低清录像准，
    /// 而「默认档」至少对得上今天真实录出来的东西。
    /// </para>
    /// </remarks>
    public static long EstimateBytes(RecordingEntry entry)
    {
        var seconds = Math.Max(0, entry.Duration.TotalSeconds);
        return (long)(seconds * BytesPerSecond(entry.Codec, entry.Resolution));
    }

    /// <summary>每秒字节数（偏大估）。</summary>
    /// <remarks>
    /// 认不出的规格一律走 <see cref="Media.RecordingSpec.Default"/> 那一格 ——
    /// 与设置层「越界回落默认值」同一条规矩。
    /// </remarks>
    private static double BytesPerSecond(string? codec, string? resolution)
    {
        var kind = Enum.TryParse<Media.VideoCodec>(codec, out var parsedCodec)
            ? parsedCodec
            : Media.RecordingSpec.Default.Codec;

        var size = Enum.TryParse<Media.VideoResolution>(resolution, out var parsedResolution)
            ? parsedResolution
            : Media.RecordingSpec.Default.Resolution;

        // 单位 KB/s。
        var kilobytesPerSecond = (kind, size) switch
        {
            (Media.VideoCodec.H265, Media.VideoResolution.Uhd4K) => 2500,
            (Media.VideoCodec.H265, Media.VideoResolution.P1080) => 700,
            (Media.VideoCodec.H265, Media.VideoResolution.P720) => 350,
            (Media.VideoCodec.H264, Media.VideoResolution.Uhd4K) => 4000,
            (Media.VideoCodec.H264, Media.VideoResolution.P720) => 550,
            _ => 1100, // H.264 1080P = 默认档
        };

        return kilobytesPerSecond * 1024;
    }


    /// <summary>
    /// 这条录像有没有被用户锁上（规格 §3.5.3②，硬豁免）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>认不出来的写法一律当锁着</b>（<c>"1"</c>、空串、被人手改坏的值）——
    /// 朝<b>少删</b>的那头落，与 <c>RetentionSetting.fromConfig</c> 解析失败
    /// 回落到「全部保留」、<c>ArchiveRecord</c> 认不出状态当 <c>pending</c>
    /// 是同一条规矩。理由也一样：<b>把锁读丢了的代价是删掉用户锁上的证据</b>，
    /// 而反过来只是少清一条（而且它在豁免列表里看得见，用户查得到）。
    /// </para>
    /// <para>
    /// ⚠️ 但「标签不存在」必须判成<b>没锁</b> —— 那是绝大多数证据的常态，
    /// 少了这一条，库会被永久锁死、什么都清不掉。
    /// </para>
    /// <para>
    /// ⚠️ 手机端 <c>lifecycle.dart</c> 的 <c>_isLocked</c> 必须与这里<b>同向</b>，
    /// 否则两端对同一条录像的锁判定会不一致。
    /// </para>
    /// </remarks>
    private static bool IsLocked(
        RecordingEntry entry, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> labels) =>
        labels.TryGetValue(entry.EvidenceId, out var entryLabels)
        && entryLabels.TryGetValue(LabelKeys.Locked, out var raw)
        && (!bool.TryParse(raw, out var locked) || locked);
}
