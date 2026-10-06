using VidLog.Desktop.Core.Configuration;

namespace VidLog.Desktop.Core.Cleanup;

/// <summary>
/// 这一次要问的那件事：方案，以及**两句给人看的话**。
/// </summary>
/// <param name="Plan">算出来的方案 —— 弹框用的就是它。</param>
/// <param name="Headline">预告框开头那句（先说清这一次要清多少、腾多少）。</param>
/// <param name="Afterword">
/// 跑完之后接在结果后面的那句。按时间那条是空的（没有要补充的）；
/// 按空间那条要说「可能没腾够」，否则用户会以为程序白干了一趟。
/// </param>
public sealed record CleanupProposal(CleanupPlan Plan, string Headline, string Afterword);

/// <summary>
/// 「按时间清理 / 按空间释放」按下之后的结论：**要么有话要问，要么有话要说**。
/// </summary>
/// <remarks>
/// ⚠️ 这两种结果**必须区分开**，不能都塞进「一句话」里：前者要弹一个不可逆动作的
/// 确认框（用户点【是】才动手），后者只是往状态行上写一句话。把它们合成一个
/// <c>string</c> 的话，「该不该弹框」这个判断就又回到界面层去了。
/// </remarks>
/// <param name="Message">不用问就直接显示的那句话（没有要清的 / 算不出来）。</param>
/// <param name="Proposal">要问的时候那个方案；不用问时为 <see langword="null"/>。</param>
public sealed record CleanupPreview(string? Message, CleanupProposal? Proposal);

/// <summary>
/// 【按时间清理…】/【按空间释放…】这两颗按钮按下之后、弹框之前，**该算什么**。
/// </summary>
/// <remarks>
/// <para>
/// 规格 §3.5.5 原话：「**禁止静默清理**」「清理前必须给出预告（将删除多少条、多少容量）」。
/// 所以这条路只有两个出口：**要么带着方案去弹框，要么说一句「没有要清的」**。
/// </para>
/// <para>
/// ⚠️ <b>为什么搬到这里</b>（T26①）：这些「读设置、算保留期、要不要问、怎么问」
/// 此前全写在 WPF 的按钮点击事件里 —— 也就是母仓 §4 明令禁止的
/// 「**不要把逻辑塞进 UI 层 —— 可测试的逻辑应与界面分离**」。
/// 搬进 Core 之后它立刻能进测试工程；而那个外壳**至今没有测试工程**（T27②），
/// 留在那儿的话这一整套分支只能靠读代码确认。
/// </para>
/// <para>
/// ⚠️ <b>与 <see cref="CleanupService"/> 的分工</b>：那边管「怎么算、怎么删」，
/// 这边管「**这一次该算哪一种、算完该跟用户说什么**」。故意不合并 ——
/// 那边是执行层，不该知道界面上有几颗按钮、按钮旁边那行字写什么。
/// </para>
/// </remarks>
public sealed class CleanupFlow
{
    private readonly CleanupService _cleanup;
    private readonly StorageLocations _storage;

    public CleanupFlow(CleanupService cleanup, StorageLocations storage)
    {
        _cleanup = cleanup;
        _storage = storage;
    }

    /// <summary>【按时间清理…】—— 按<paramref name="retention"/>算一次。</summary>
    /// <param name="retention">
    /// ⚠️ **按调用传，不收在字段里**：设置页上改完保留期是立刻生效的，
    /// 而这个流程与设置窗同生共死。存一份快照的话，用户改完保留期再点
    /// 【按时间清理…】用的还是改之前那个值 —— 而且**不会报错**，只是清错了。
    /// </param>
    public async Task<CleanupPreview> ByTimeAsync(
        RetentionSettings retention, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var plan = await _cleanup.PreviewAsync(retention, now, cancellationToken);

        if (plan.Candidates.Count == 0)
        {
            // ⚠️ 与启动时那一次**不一样**：这里是用户主动点的，
            // 所以「没有要清的」必须说出来 —— 什么都没发生会让人以为按钮坏了。
            return new CleanupPreview("按现在的保留期设置，没有到期该清的。", null);
        }

        return new CleanupPreview(null, new CleanupProposal(
            plan,
            $"保留期到了的录像有 {plan.Candidates.Count} 条，"
            + $"约 {plan.TotalBytes / 1024 / 1024} MB。",
            string.Empty));
    }

    /// <summary>
    /// 【按空间释放…】—— 把这批录像清到活动那块盘至少还剩它的预留空间。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>它可能释放不出足够空间</b>，而那是**对**的：未归档 / 已锁定 /
    /// 24 小时内的三条豁免照常生效（见 <see cref="CleanupService.PreviewBySpaceAsync"/>），
    /// 所以「盘满了也清不动」是这个产品的既定取舍 —— 宁可盘满，也不删唯一副本。
    /// 那就得把「没清够」说出来，否则用户会以为程序没干活（见
    /// <see cref="CleanupProposal.Afterword"/>）。
    /// </remarks>
    public async Task<CleanupPreview> BySpaceAsync(
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var root = _storage.ActiveRoot;

        // ⚠️ 现探一次盘，不吃缓存：这个读数**马上就要写进给用户看的那句话里**，
        // 而用户很可能就是刚看到「磁盘快满了」才来点这颗按钮的。
        var space = _storage.Measure(root);

        if (space is null)
        {
            return new CleanupPreview(
                $"读不到 {root} 还剩多少空间，按空间释放算不出来。", null);
        }

        // 预留空间取**用户配的那个**，没配过就按默认现算。
        // ⚠️ 没配过那一支走 `DiskSpace.EffectiveReservedGb` 那句既有规矩，
        // 而不是自己再 `?? DefaultReservedGb(...)` 抄一遍 —— 原来界面里就是抄的，
        // 抄出来的那份一旦与 Core 分家，表现是「设置页说的预留线」与
        // 「清理实际用的预留线」对不上，而且两处都不报错。
        var slot = _storage.Slots.FirstOrDefault(
            one => string.Equals(one.Path, root, StringComparison.OrdinalIgnoreCase));

        var reserved = slot is null
            ? DiskSpace.DefaultReservedGb(space.TotalBytes, DiskSpace.IsSystemDrive(root))
            : DiskSpace.EffectiveReservedGb(slot, space);

        var plan = await _cleanup.PreviewBySpaceAsync(
            reserved * DiskSpace.Gigabyte, space.FreeBytes, now, cancellationToken);

        if (plan is null)
        {
            // 只可能发生在「归档层是本机磁盘」上 —— 那种时候这一整块在界面上是藏着的，
            // 所以走到这里说明设置在这一瞬间被改了。照实说，别装作算过。
            return new CleanupPreview(
                "归档层是本机磁盘 —— 盘上这份是唯一副本，按空间释放不提供。", null);
        }

        if (plan.Candidates.Count == 0)
        {
            return new CleanupPreview(
                $"{root} 还剩 {Display.Bytes(space.FreeBytes)}"
                + $"（预留线 {reserved} GB），没有要清的。",
                null);
        }

        return new CleanupPreview(null, new CleanupProposal(
            plan,
            $"{root} 还剩 {Display.Bytes(space.FreeBytes)}，预留线是 {reserved} GB。\n"
            + $"拟清 {plan.Candidates.Count} 条，约 {plan.TotalBytes / 1024 / 1024} MB。",
            $"豁免没动的那 {plan.Exempted.Count} 条里，未归档的永不自动删（那是唯一副本）"
            + "—— 所以可能没腾够。"));
    }
}
