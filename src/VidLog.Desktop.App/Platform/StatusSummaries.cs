using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.License;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 校准与许可那两句话。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>抽出来是为了只有一份实现</b>。拆窗之前这两句话就已经在三处显示
/// （设置页、左侧栏、概览页），当时的注释写着「各写一份的话迟早会出现
/// 『侧栏说已校准、设置页说没校准』—— 而这种自相矛盾比哪一边说错都更让人
/// 不敢信这个界面」。拆窗之后主窗与设置窗各要一份，那个风险只会更大。
/// </para>
/// <para>
/// 放在 <c>Platform</c> 而不是 Core：它读的是 <see cref="AppHost"/>（装配层），
/// 而 Core 不认识 <see cref="AppHost"/>。
/// </para>
/// </remarks>
internal static class StatusSummaries
{
    /// <summary>
    /// 校准状态的一句话。
    /// </summary>
    /// <remarks>
    /// ⚠️ 未校准**要说清「为什么」**（<c>BlockedReason</c> 里已经写了），
    /// 而不是笼统一句「未校准」—— 用户得知道是去联网、还是去点重新校准。
    /// </remarks>
    public static string Calibration(AppHost host)
    {
        var clock = host.Services.TrustedClock;

        if (!clock.IsCalibrated)
        {
            return $"⛔ {clock.BlockedReason}";
        }

        var source = clock.State.Source == CalibrationSource.PublicTime ? "公网时间" : "归档回执";
        var at = clock.State.CalibratedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

        return $"✅ 已校准（来源：{source}，校准于 {at}）。可以录制。";
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
