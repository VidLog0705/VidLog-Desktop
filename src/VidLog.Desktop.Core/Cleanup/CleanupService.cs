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

    /// <summary>算一次清理计划。**一个文件都不碰。**</summary>
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
