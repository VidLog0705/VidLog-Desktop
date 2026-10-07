namespace VidLog.Desktop.Core.Clock;

/// <summary>
/// 校准状态的一句话（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>未校准必须说清「为什么」</b>：笼统一句「未校准」的话，用户不知道是
/// 去联网，还是去点重新校准 —— 而这两条路的下一步完全不同。原因由
/// <c>TrustedClock.BlockedReason</c> 给（那边才是判断的地方），这里只保证
/// **它一定会出现在那句话里**。
/// </para>
/// <para>
/// ⚠️ 这一句原先在 <c>App/Platform/StatusSummaries</c> 里，而那个工程没有测试
/// 工程。它在三处显示（设置页、左侧栏、概览页），抽成一份正是因为
/// 「各写一份迟早会出现『侧栏说已校准、设置页说没校准』」——
/// 而这种自相矛盾比哪一边说错都更让人不敢信这个界面。
/// </para>
/// </remarks>
public static class CalibrationText
{
    /// <summary>界面上那一句。</summary>
    /// <param name="isCalibrated">校准过没有。</param>
    /// <param name="blockedReason">
    /// 没校准的原因（<c>TrustedClock.BlockedReason</c>）。
    /// </param>
    /// <param name="state">已落盘的校准状态。</param>
    /// <remarks>
    /// ⚠️ <paramref name="blockedReason"/> 收 <see langword="null"/> 只是因为
    /// <c>TrustedClock.BlockedReason</c> 的类型是这样（它在**能录**的时候才是
    /// <see langword="null"/>）—— 走到下面那一支时它总有话。
    /// 万一真是空的，这里**不替它编一个原因**：只有一个图标也比一句猜测强
    /// （猜错了用户会照着一个不存在的毛病去修）。
    /// </remarks>
    public static string Describe(bool isCalibrated, string? blockedReason, CalibrationState state)
    {
        if (!isCalibrated)
        {
            return $"⛔ {blockedReason}";
        }

        // ⚠️ 来源要写成中文的「公网时间 / 归档回执」：用户看到的是
        // 「我这台机器的时间是谁给的」，两个枚举名对他没有意义。
        var source = state.Source == CalibrationSource.PublicTime ? "公网时间" : "归档回执";

        // ⚠️ 校准时刻为 null 时给一个「—」而不是空着：那句话里空一格
        // 会看起来像**界面坏了**，而它其实是「校准过但没记下时刻」。
        var at = state.CalibratedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

        return $"✅ 已校准（来源：{source}，校准于 {at}）。可以录制。";
    }
}
