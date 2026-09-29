using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Cleanup;

/// <summary>
/// 把「算该清什么」与「真去删」接起来（规格 §3.5.4 / §3.5.5）。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是清理链路的第一个生产调用点。</b>在此之前
/// <see cref="CleanupPlanner"/>、<see cref="CleanupExecutor"/>、
/// <see cref="CleanupAuditLog"/> 都只有测试在调 —— 一套写好了、测过了、
/// 从没插过电的代码（母仓 <c>HANDOFF.md</c> §6 第 19 条记的就是这种病）。
/// </para>
/// <para>
/// 它分成两步是**刻意的**，因为规格 §3.5.5 要求「**禁止静默清理**、
/// 清理前必须给出预告（将删除多少条、多少容量）」：
/// <see cref="PreviewAsync"/> 只算不删，用户看过之后才轮到 <see cref="RunAsync"/>。
/// 合成一个方法的话，「先给用户看」这一步就靠调用方记得，
/// 而那种「靠记得」的约定在这类不可逆动作上不该存在。
/// </para>
/// </remarks>
public sealed class CleanupService
{
    private readonly IRecordingIndex _index;
    private readonly ILabelStore _labels;
    private readonly ReceiptStore _receipts;
    private readonly CleanupExecutor _executor;
    private readonly IAppLogger _logger;

    public CleanupService(
        IRecordingIndex index,
        ILabelStore labels,
        ReceiptStore receipts,
        CleanupExecutor executor,
        IAppLogger? logger = null)
    {
        _index = index;
        _labels = labels;
        _receipts = receipts;
        _executor = executor;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>归档层这一档到底允不允许清理（规格 §3.5.1）。</summary>
    /// <remarks>
    /// 界面上据此决定要不要摆这个入口 —— 而 <see cref="CleanupExecutor"/> 那边
    /// 还有一道同样判据的闸：**两边都要有**，界面那道是「不给用户一个改了没反应的
    /// 开关」，执行层那道是「就算被绕过了也不许删」。
    /// </remarks>
    public bool CanCleanup => _executor.CanCleanup;

    /// <summary>
    /// 算一次「按时间清理」的计划。**一个文件都不碰。**
    /// </summary>
    /// <remarks>
    /// 设计图 `_43` 上那个【按时间清理…】按钮走这里。
    /// 另一个按钮【按空间释放…】走 <see cref="PreviewBySpaceAsync"/> ——
    /// **两者刻意分开**：它们要算的东西不一样（一个是保留期，一个是磁盘剩余），
    /// 合成一个方法就得先判断「这次是哪种」，而那正是两个入口本来的区别。
    /// </remarks>
    public async Task<CleanupPlan> PreviewAsync(
        RetentionSettings settings,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var entries = await _index.LoadAllAsync(cancellationToken);
        var labels = await _labels.LoadAllAsync(cancellationToken);

        // 起算点是**归档成功时刻**（回执里的 timeAnchor，外部时间锚，用户改不了）——
        // 不是录完时刻。见 §3.5.2.1 与 `CleanupPlanner` 上的说明。
        var anchors = await _receipts.LoadAnchorMapAsync(cancellationToken);

        return new CleanupPlanner().PlanPerBusinessType(entries, labels, anchors, settings, now);
    }

    /// <summary>
    /// 按空间释放：把这批录像清到**磁盘至少还剩 <paramref name="minFreeBytes"/>**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 依据是设计图 `_43` 上那个【**按空间释放…**】按钮（与【按时间清理…】并列）。
    /// </para>
    /// <para>
    /// ⚠️ <b>它走的是**单份**策略，不走「按业务类型」那条路</b> ——
    /// 磁盘满不满跟业务类型无关（`PlanPerBusinessType` 的注释里已经写着这一条），
    /// 所以「按空间」不分成发货/退货两份。
    /// </para>
    /// <para>
    /// ⚠️ <b>归档层就在本机时**不给这个入口**</b>（规格 §3.5.1）：那种情况下
    /// 本地这一份是**唯一副本**，清掉就是删证据。这里挡一道，界面上还有一道 ——
    /// 界面那道是「不给用户一个点了就报错的按钮」，这道是「就算被绕过了也不许删」。
    /// </para>
    /// <para>
    /// ⚠️ 三条豁免（未归档 / 已锁定 / 24 小时内）**照样生效**：它们在
    /// <see cref="CleanupPlanner.Plan"/> 里判，与策略模式无关。所以
    /// 「按空间释放」**清不到**那些 —— 于是它可能**释放不出足够空间**，
    /// 而那是**对**的：宁可盘满，也不删唯一副本。
    /// </para>
    /// </remarks>
    /// <param name="minFreeBytes">
    /// 要留出多少空间。来自「预留空间」那个设置（默认值见 <see cref="ReservedSpace"/>）。
    /// </param>
    /// <param name="freeBytes">
    /// 那块盘现在还剩多少。**由调用方探**（`IDiskSpaceProbe`），与
    /// <see cref="CleanupPlanner.Plan"/> 同形 —— 因为调用方**本来就要探**：
    /// 设计图 `_43` 顶上那条「0.0 / 504.4 GB」的容量条用的就是同一个数。
    /// </param>
    /// <returns>
    /// 计划；**<see langword="null"/> 表示「不允许清理」**（归档层就在本机）。
    /// </returns>
    /// <remarks>
    /// ⚠️ <b>用可空返回而不是「返回一个空的计划」</b>：「不许清」与「没得清」
    /// 是两回事 —— 前者要说一句原因（否则用户点了【按空间释放】什么都没发生），
    /// 而后者不该说话。混成一个的话，界面只能靠 `CanCleanup` 再判一次，
    /// 而那正是「同一件事写两遍」。
    /// </remarks>
    public async Task<CleanupPlan?> PreviewBySpaceAsync(
        long minFreeBytes,
        long freeBytes,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!CanCleanup)
        {
            // ⚠️ 与界面上「摆不摆这个入口」是同一条判据（`CanCleanup`）。
            // 只挡界面的话，一个绕过界面的调用点就能把唯一副本删掉。
            return null;
        }

        var entries = await _index.LoadAllAsync(cancellationToken);
        var labels = await _labels.LoadAllAsync(cancellationToken);
        var anchors = await _receipts.LoadAnchorMapAsync(cancellationToken);

        var plan = new CleanupPlanner().Plan(
            entries, labels, anchors,
            new RetentionPolicy(RetentionMode.BySpace, MinFreeBytes: minFreeBytes),
            now,
            freeBytes);

        // ⚠️ **预告也要留痕**（`AGENTS.md` §6 的「清理动作」）。
        // `RunAsync` 那边已经记了「真删了什么」，而这一步记的是
        // 「**为什么要删**」—— 事后只看删除记录的话，分不清那次清理是
        // 按时间到期还是盘快满了（两者的处置与责任完全不同）。
        _logger.Log(LogLevel.Info, "清理", $"按空间释放的预告：剩余 {freeBytes / 1024 / 1024} MB、"
            + $"要留 {minFreeBytes / 1024 / 1024} MB ⇒ 拟删 {plan.Candidates.Count} 条"
            + $"（约 {plan.Candidates.Sum(c => c.SizeBytes) / 1024 / 1024} MB），"
            + $"豁免 {plan.Exempted.Count} 条");

        return plan;
    }

    /// <summary>按计划真删。逐条回查归档层，查不到或查不了都**不删**（I8）。</summary>
    public async Task<CleanupReport> RunAsync(
        CleanupPlan plan, CancellationToken cancellationToken = default)
    {
        var report = await _executor.ExecuteAsync(plan, cancellationToken);

        if (report.Deleted.Count > 0 || report.Refused.Count > 0)
        {
            // 不许静默清理的另一半：真正的动作要留痕（`CleanupExecutor` 已经
            // 逐条写了审计流水，这里再记一条便于从日志侧一眼看到）。
            _logger.Log(LogLevel.Info, "清理", $"清理完成：删了 {report.Deleted.Count} 条"
                + $"（约 {report.FreedBytes / 1024 / 1024} MB），拒了 {report.Refused.Count} 条");
        }

        return report;
    }
}
