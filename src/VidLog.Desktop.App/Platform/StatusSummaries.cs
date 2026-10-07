using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.License;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 校准与许可那两句话**取值**的地方。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>抽出来是为了只有一份实现</b>。拆窗之前这两句话就已经在三处显示
/// （设置页、左侧栏、概览页），当时的注释写着「各写一份的话迟早会出现
/// 『侧栏说已校准、设置页说没校准』—— 而这种自相矛盾比哪一边说错都更让人
/// 不敢信这个界面」。拆窗之后主窗与设置窗各要一份，那个风险只会更大。
/// </para>
/// <para>
/// ⚠️ <b>句子本身现在在 Core</b>（T27② 第 4 批）：校准那句搬去了
/// <see cref="CalibrationText"/>，许可那句早就在
/// <see cref="LicenseStatus.SummaryText"/>（T30）。留在这里的只有
/// 「从 <see cref="AppHost"/> 的哪一处去取」—— 而 Core 不认识
/// <see cref="AppHost"/>，所以**取值这一步只能在这边**。
/// </para>
/// </remarks>
internal static class StatusSummaries
{
    /// <summary>校准状态的一句话（写法的规矩在 <see cref="CalibrationText"/>）。</summary>
    public static string Calibration(AppHost host)
    {
        var clock = host.Services.TrustedClock;

        return CalibrationText.Describe(clock.IsCalibrated, clock.BlockedReason, clock.State);
    }

    /// <summary>
    /// 许可状态的一句话。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>「本机软件没配好公钥」与「没激活」是两回事</b>，必须分开说 ——
    /// 混成一句话，用户会拿着机器码一直去找提供方换码，而换了也没用。
    /// </para>
    /// <para>
    /// ⚠️ 那句话本身<b>不在这里</b>（T30）：它在 <see cref="LicenseStatus.SummaryText"/> 上。
    /// 这里原本也写了一份、设置页又写了一份，两份都靠人记住「试用要先判」——
    /// 现在 Core 上只有一处，<c>LicenseTests</c> 盯着它。
    /// </para>
    /// </remarks>
    public static string License(AppHost host) =>
        host.Services.License is { } license
            ? license.Status.SummaryText
            : "⛔ 本机软件没配好许可公钥（部署时漏了）";
}
