namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 工作模式 —— 规格 §3.3.1 的三种。
/// </summary>
/// <remarks>
/// 三者的差别**全在「什么时候停」**，开录条件是一样的（都是识别到单号）。
/// </remarks>
public enum WorkMode
{
    /// <summary>
    /// 连续扫 —— 首次识别开录；扫到**不同**单号即换件，立刻以新单号开下一段。
    /// </summary>
    /// <remarks>
    /// 规格 2026-09-22 的需求变更把它从「只提示不停录」改成了**换段式**。
    /// 换件在这里是**正常路径**，不再播报「面单不同」。
    /// </remarks>
    Continuous,

    /// <summary>同码停 —— 复扫到**同一**单号才停；扫到异码只提示（§3.3.2 错码保护）。</summary>
    StopOnSameWaybill,

    /// <summary>
    /// 扫码静止停录 —— 复扫同码也停，**外加**一条自己的判据：
    /// 被跟踪的面单**离场后再入场并静止 2 秒**。
    /// </summary>
    /// <remarks>
    /// 那条 2 秒是**固定的**，与 <see cref="StaticStopOption"/> 那个档位
    /// （2~5 **分钟**）是**两条独立的规则**，本模式不读档位 ——
    /// 档位设成「关闭」时它照样生效（规格 2026-09-22 的语义澄清）。
    /// <para>
    /// 少了它，本模式在「复扫同码就停」之后与 <see cref="StopOnSameWaybill"/>
    /// **完全等价**，这个模式就只剩下一个名字了。
    /// </para>
    /// </remarks>
    StopOnStaticAfterRescan,
}

/// <summary>
/// 静止停录档位（规格 §3.3.3）。
/// </summary>
/// <remarks>
/// 单位是**分钟**。规格的表格写的是「关闭 / 2~5」。
/// </remarks>
public enum StaticStopOption
{
    Off,
    Two,
    Three,
    Four,
    Five,
}

/// <summary>
/// 时长兜底档位（规格 §3.3.4）。
/// </summary>
/// <remarks>
/// 命中后要**询问**用户是否继续，不是直接停 —— 所以它的值也是分钟。
/// </remarks>
public enum DurationFallbackOption
{
    Off,
    Four,
    Five,
    Six,
}

/// <summary>档位与枚举之间的换算。</summary>
public static class WorkModeOptions
{
    public static int? Minutes(this StaticStopOption option) => option switch
    {
        StaticStopOption.Off => null,
        StaticStopOption.Two => 2,
        StaticStopOption.Three => 3,
        StaticStopOption.Four => 4,
        StaticStopOption.Five => 5,
        _ => null,
    };

    public static int? Minutes(this DurationFallbackOption option) => option switch
    {
        DurationFallbackOption.Off => null,
        DurationFallbackOption.Four => 4,
        DurationFallbackOption.Five => 5,
        DurationFallbackOption.Six => 6,
        _ => null,
    };

    /// <summary>
    /// 扫码静止停录那 2 秒。
    /// </summary>
    /// <remarks>
    /// 规格写死是 2 秒且**不看档位**，所以这里刻意**不是**配置项 ——
    /// 把它做成可配置的，下一个读代码的人就会以为它跟 <see cref="StaticStopOption"/> 有关。
    /// </remarks>
    public static readonly TimeSpan RescanStaticHold = TimeSpan.FromSeconds(2);
}
