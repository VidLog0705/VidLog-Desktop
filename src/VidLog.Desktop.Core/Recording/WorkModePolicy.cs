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
}

/// <summary>策略做出决定的那一刻，它看到的局面。</summary>
/// <param name="SegmentOpen">当前有没有在录的段。</param>
/// <param name="Current">当前段的单号；空闲时为 null。</param>
/// <param name="TrackedWaybillLeftFrame">
/// 被跟踪的面单是否**已经离场过**（扫码静止那 2 秒的门槛，规格 §3.3.1 的语义澄清）。
/// </param>
/// <param name="StaticFor">画面已经静止多久。<b>调用方必须按 I12 用它已录时长封顶</b>。</param>
public sealed record WorkModeState(
    bool SegmentOpen = false,
    WaybillNumber? Current = null,
    bool TrackedWaybillLeftFrame = false,
    TimeSpan StaticFor = default)
{
    public static WorkModeState Idle { get; } = new();
}

/// <summary>
/// 三种工作模式的判定（规格 §3.3.1 / §3.3.2 / §3.3.3 / §3.3.4）。
/// </summary>
/// <remarks>
/// <para>
/// 纯函数：同样的输入永远给同样的输出，不读时钟、不碰磁盘。
/// </para>
/// <para>
/// <b>I12 的落点</b>：<see cref="WorkModeState.StaticFor"/> 由调用方传进来，
/// 而它必须已经按「本段已录时长」封顶。少了这个封顶，
/// 「架机半小时后才按开始」会在开录那一刻就判出静止、当场停录。
/// </para>
/// </remarks>
public sealed class WorkModePolicy
{
    public WorkModePolicy(WorkMode mode, StaticStopOption staticOption)
    {
        Mode = mode;
        StaticStop = staticOption;
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

    /// <summary>静止停录档位（规格 §3.3.3）。</summary>
    public StaticStopOption StaticStop { get; set; }

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

                // 同码停 / 扫码静止：这是主要结束方式。
                _ => new WorkDecision.StopSegment(StopReason.SameWaybillRescan),
            };
        }

        // 扫到**不同**单号。
        return Mode switch
        {
            // 连续扫换段式（规格 2026-09-22 的需求变更）：换件是正常路径。
            // 那里**不播报「面单不同」** —— 每件都报一次既是错的，又会盖住下一件的开录播报。
            WorkMode.Continuous => new WorkDecision.SwitchTo(scanned),

            // 另两个模式：错码保护 —— 只提示，不停录（规格 §3.3.2）。
            _ => new WorkDecision.Announce(AnnouncementKind.WrongWaybill, scanned),
        };
    }

    /// <summary>
    /// 画面静止状况更新。
    /// </summary>
    /// <param name="isStatic">当前画面是否静止。</param>
    public WorkDecision OnStatic(WorkModeState state, bool isStatic)
    {
        if (!state.SegmentOpen || !isStatic)
        {
            return WorkDecision.Nothing.Instance;
        }

        // 扫码静止停录**自己的** 2 秒判据（规格 2026-09-22 的语义澄清）：
        // 固定 2 秒、**不看档位**（档位设成「关闭」时它照样生效）。
        // 门槛是「被跟踪的面单曾离场又入场」—— 少了它，面单刚扫完就摆在框里，
        // 每段都会在开录 2 秒后自己结束。
        if (Mode == WorkMode.StopOnStaticAfterRescan)
        {
            if (state.TrackedWaybillLeftFrame
                && state.StaticFor >= WorkModeOptions.RescanStaticHold)
            {
                return new WorkDecision.StopSegment(StopReason.StaticTimeout);
            }

            // 本模式下档位不会先触发（2 秒必定早于任何档位，最小 2 分钟）——
            // 那是**结果**，不是把档位关掉了。
            return WorkDecision.Nothing.Instance;
        }

        // 另两个模式按档位走。
        var minutes = StaticStop.Minutes();
        if (minutes is null)
        {
            // 档位「关闭」。
            return WorkDecision.Nothing.Instance;
        }

        return state.StaticFor >= TimeSpan.FromMinutes(minutes.Value)
            ? new WorkDecision.StopSegment(StopReason.StaticTimeout)
            : WorkDecision.Nothing.Instance;
    }

    /// <summary>用户手动停。</summary>
    public static WorkDecision OnManualStop(WorkModeState state) =>
        state.SegmentOpen
            ? new WorkDecision.StopSegment(StopReason.Manual)
            : WorkDecision.Nothing.Instance;
}
