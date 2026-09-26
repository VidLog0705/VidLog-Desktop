namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 工作模式 —— 规格 §3.3.1 的**两种**（电脑端）。
/// </summary>
/// <remarks>
/// <para>
/// 两者的差别**全在「什么时候停」**，开录条件是一样的（都是识别到单号）。
/// </para>
/// <para>
/// ⚠️ <b>电脑端只有两种</b>：规格 §3.3.1 于 2026-09-24 裁定「电脑端不要静止停录，
/// 电脑端只保留连续扫码和同码停录模式」，原来那个 <c>StopOnStaticAfterRescan</c>
/// 成员已经删掉。<b>手机端仍然是三种</b> —— 那一端有它自己的一套
/// （`VidLog-Mobile/lib/recording/work_mode.dart`），不要照着这边改成两种。
/// </para>
/// <para>
/// ⚠️ 枚举**存进设置文件的是数字**（本仓没挂 <c>JsonStringEnumConverter</c>），
/// 所以删掉末尾那个成员不会挪动前两个的值；而老配置里那个 <c>2</c> 现在是个
/// **越界值** —— 由 <c>AppSettings</c> 显式回落成「同码停」，
/// 不是靠「越界枚举恰好行为像同码停」（规格 §3.3.1 的老配置兼容那一条）。
/// </para>
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
}

/// <summary>
/// 闲置提醒档位（规格 §3.3.3 **电脑端那半**）。
/// </summary>
/// <remarks>
/// <para>
/// 单位是**分钟**。规格那张表写的是「关闭 / 2 / 3 / 4 / 5 分钟 / **自定义**」。
/// </para>
/// <para>
/// ⚠️ <b>它与「静止停录」是两件事，别把名字看成历史遗留就顺手改回去</b>：
/// 电脑端判的是「**连续 N 分钟没有任何扫码或打点**」（不看画面），
/// **到点只播报提醒、绝不改录制状态** —— 真正把录制停掉的是时长兜底（§3.3.4）。
/// 电脑端**没有**静止停录：那条路已被实测否决（录制中相机独占，取不到画面，
/// 见 <c>docs/实现决策.md</c> §24/§25/§28.5）。
/// </para>
/// </remarks>
public enum IdleReminderOption
{
    Off,
    Two,
    Three,
    Four,
    Five,

    /// <summary>自定义分钟数，值在 <see cref="WorkModeOptions.IdleReminderMinutes"/> 里。</summary>
    Custom,
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
    /// <summary>闲置提醒的自定义分钟数范围。越界一律回落（见下）。</summary>
    /// <remarks>
    /// 下限 1 而不是 0：「0 分钟」等于一开录就提醒，那不是提醒是骚扰。
    /// 上限 24 小时：再长就没有意义了 —— 时长兜底最多 6 分钟就会先把它停掉
    /// （§3.3.4），而闲置提醒**从不**停录，所以它压根轮不到响。
    /// </remarks>
    public const int MinIdleMinutes = 1;
    public const int MaxIdleMinutes = 24 * 60;

    /// <summary>档位对应的分钟数；「关闭」返回 <see langword="null"/>。</summary>
    /// <param name="customMinutes">只有 <see cref="IdleReminderOption.Custom"/> 用得到它。</param>
    /// <remarks>
    /// 越界的自定义值**回落到默认档（3 分钟）**，不是夹到边界 ——
    /// 与 <c>AppSettings</c> 一贯的「越界回落默认值」同一条规矩（I4 的同一条精神：
    /// 一个被手改坏的配置不该让用户录不了像，也不该让他收到一个莫名其妙的提醒）。
    /// </remarks>
    public static int? Minutes(this IdleReminderOption option, int customMinutes = 0) => option switch
    {
        IdleReminderOption.Off => null,
        IdleReminderOption.Two => 2,
        IdleReminderOption.Three => 3,
        IdleReminderOption.Four => 4,
        IdleReminderOption.Five => 5,
        IdleReminderOption.Custom => customMinutes is >= MinIdleMinutes and <= MaxIdleMinutes
            ? customMinutes
            : 3,
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
}
