namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 策略做出的一次决定。
/// </summary>
/// <remarks>
/// 策略**只决定、不执行** —— 这是它可测的原因。执行（起进程、写盘、收尾）
/// 在 <see cref="RecordingSession"/> 与协调器那边。
/// </remarks>
public abstract record WorkDecision
{
    /// <summary>以这个单号开一段。</summary>
    public sealed record StartSegment(WaybillNumber Waybill) : WorkDecision;

    /// <summary>连续扫换件：上一件到此为止，立刻以新单号开下一段。</summary>
    public sealed record SwitchTo(WaybillNumber Next) : WorkDecision;

    /// <summary>停当前段。</summary>
    public sealed record StopSegment(StopReason Reason) : WorkDecision;

    /// <summary>什么也不做。</summary>
    public sealed record Nothing : WorkDecision
    {
        public static Nothing Instance { get; } = new();
    }

    /// <summary>给用户的提示（错码保护等）。</summary>
    public sealed record Announce(AnnouncementKind Kind, WaybillNumber? Waybill = null) : WorkDecision;
}

/// <summary>要告诉用户的事。</summary>
public enum AnnouncementKind
{
    /// <summary>规格 §3.3.2：扫到不同单号时不停止录制，仅声音提示「面单错误，请扫描正确面单」。</summary>
    WrongWaybill,

    /// <summary>换件（连续扫）。这是**正常路径**，不播报错。</summary>
    SwitchedWaybill,

    /// <summary>
    /// 规格 §3.3.3（电脑端那半）：连续 N 分钟没有任何扫码或打点。
    /// <b>只提醒，绝不改录制状态</b> —— 用户不理就一直录。
    /// </summary>
    Idle,
}

/// <summary>策略做出决定的那一刻，它看到的局面。</summary>
/// <param name="SegmentOpen">当前有没有在录的段。</param>
/// <param name="Current">当前段的单号；空闲时为 null。</param>
/// <param name="IdleFor">
/// 距**最后一次扫码或打点**过了多久；还没开录时为 <see cref="TimeSpan.Zero"/>。
/// </param>
public sealed record WorkModeState(
    bool SegmentOpen = false,
    WaybillNumber? Current = null,
    TimeSpan IdleFor = default)
{
    public static WorkModeState Idle { get; } = new();
}

/// <summary>
/// 工作模式的判定（规格 §3.3.1 / §3.3.2 / §3.3.3 / §3.3.4）。
/// </summary>
/// <remarks>
/// <para>
/// 纯函数：同样的输入永远给同样的输出，不读时钟、不碰磁盘。
/// </para>
/// <para>
/// ⚠️ <b>电脑端的模式只有两种</b>（§3.3.1 于 2026-09-24 删掉了「扫码静止停录」），
/// 而「静止」这条判据整个不存在了：电脑端拿不到画面（§24/§25 实测否决）。
/// 这里只剩下「什么时候停」与「什么时候提醒」两类判定。
/// </para>
/// <para>
/// ⚠️ <b>闲置提醒的计时起点是「最后一次扫码或打点」，而 I12 另外要求它不早于开录时刻</b>
/// —— 那是调用方的事：<see cref="WorkModeState.IdleFor"/> 传进来之前，
/// 必须已经按「**本段已录时长**」封顶。少了这个封顶，
/// 「架机半小时后才按开始」会在开录那一刻就收到一条闲置提醒。
/// </para>
/// </remarks>
public sealed class WorkModePolicy
{
    public WorkModePolicy(WorkMode mode, IdleReminderOption idleReminder, int idleReminderMinutes = 0)
    {
        Mode = mode;
        IdleReminder = idleReminder;
        IdleReminderMinutes = idleReminderMinutes;
    }

    /// <summary>
    /// 工作模式（规格 §3.3.1）。
    /// </summary>
    /// <remarks>
    /// 做成可写属性而不是 readonly 字段：设置页写着「下次录段生效」，
    /// 而构造后就不再读设置的话那句话是假的（改完要重启才生效）。
    /// 判定发生在**每次扫码/每次更新**那一刻，所以改了立刻算数。
    /// </remarks>
    public WorkMode Mode { get; set; }

    /// <summary>闲置提醒档位（规格 §3.3.3 电脑端那半）。</summary>
    public IdleReminderOption IdleReminder { get; set; }

    /// <summary>自定义档位的分钟数；只有 <see cref="IdleReminderOption.Custom"/> 用得到。</summary>
    public int IdleReminderMinutes { get; set; }

    /// <summary>识别到一个单号（扫码枪或摄像头）。</summary>
    public WorkDecision OnScan(WorkModeState state, WaybillNumber scanned)
    {
        if (!state.SegmentOpen)
        {
            return new WorkDecision.StartSegment(scanned);
        }

        if (state.Current == scanned)
        {
            // 复扫同码。
            return Mode switch
            {
                // 连续扫：同一件又扫一次是误触，**不停也不提示**。
                WorkMode.Continuous => WorkDecision.Nothing.Instance,

                // 同码停：这是主要结束方式。
                _ => new WorkDecision.StopSegment(StopReason.SameWaybillRescan),
            };
        }

        // 扫到**不同**单号。
        return Mode switch
        {
            // 连续扫换段式（规格 2026-09-22 的需求变更）：换件是正常路径。
            // 那里**不播报「面单不同」** —— 每件都报一次既是错的，又会盖住下一件的开录播报。
            WorkMode.Continuous => new WorkDecision.SwitchTo(scanned),

            // 同码停：错码保护 —— 只提示，不停录（规格 §3.3.2）。
            _ => new WorkDecision.Announce(AnnouncementKind.WrongWaybill, scanned),
        };
    }

    /// <summary>
    /// 闲置提醒：连续 N 分钟没有任何扫码或打点。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 它**只产出一次播报**，绝不产出 <see cref="WorkDecision.StopSegment"/> ——
    /// 规格 §3.3.3 原话：「**它只提醒，绝不改录制状态**……用户不理就一直录。
    /// 最终把它停掉的是 §3.3.4 的时长兜底」。
    /// </para>
    /// <para>
    /// 到点之后**每次调用都返回提醒**（它是纯函数，不记「已经提醒过」）——
    /// 「一段闲置里只响一次」由调用方去重（见 <c>RecordingCoordinator</c> 的
    /// <c>_idleReminded</c>）。
    /// </para>
    /// <para>
    /// ⚠️ 语义与「画面静止」**不同，别混**（规格 §3.3.3 写明并接受）：
    /// 包裹一直摆在框里而人走开了，它**不会**提醒（一直在扫码）；人一直在搬东西
    /// 只是没扫码，它**反而会**提醒。
    /// </para>
    /// </remarks>
    public WorkDecision OnIdle(WorkModeState state)
    {
        if (!state.SegmentOpen)
        {
            // 还没开录就没什么可提醒的 —— 更实际的理由见 <see cref="WorkModeState.IdleFor"/>：
            // 调用方按「本段已录时长」封顶，所以这时它本来就是 0。
            return WorkDecision.Nothing.Instance;
        }

        var minutes = IdleReminder.Minutes(IdleReminderMinutes);
        if (minutes is null)
        {
            // 档位「关闭」。
            return WorkDecision.Nothing.Instance;
        }

        return state.IdleFor >= TimeSpan.FromMinutes(minutes.Value)
            ? new WorkDecision.Announce(AnnouncementKind.Idle)
            : WorkDecision.Nothing.Instance;
    }

    /// <summary>用户手动停。</summary>
    public static WorkDecision OnManualStop(WorkModeState state) =>
        state.SegmentOpen
            ? new WorkDecision.StopSegment(StopReason.Manual)
            : WorkDecision.Nothing.Instance;
}
