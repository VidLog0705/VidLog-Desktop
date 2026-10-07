namespace VidLog.Desktop.Core.Media;

/// <summary>
/// 把「实际会按什么规格录」说成**一句人话**（规格 §3.1.7）。
/// </summary>
/// <remarks>
/// <para>
/// 规格 §3.1.7 原话：「**回落必须可见**……**不得静默回落**」。
/// </para>
/// <para>
/// ⚠️ <b>2026-10-07 从 <c>SettingsWindow.ShowEffectiveSpec</c> 整段搬过来的</b>
/// （T27② 第 2 批）。搬的理由是那一块原先长在 <c>VidLog.Desktop.App</c> 里，
/// 而那个工程**没有测试工程** —— 这段分支（三档情形）只能靠读代码确认。
/// 搬过来之后判据与文案都进得了测试，外壳只剩「把结果接到控件上」。
/// </para>
/// <para>
/// ⚠️ 搬的是**行为**，一个字都没改。下面那三段情形的理由与
/// 「2026-09-30 印过一句假话」那件事，都是原地的注释，原样留着。
/// </para>
/// </remarks>
public static class EffectiveSpecNotice
{
    /// <summary>
    /// 该显示哪一句话，以及它是不是一条**警告**。
    /// </summary>
    /// <param name="wanted">用户**此刻**在设置里选的那一对（含方向）。</param>
    /// <param name="effective">最近一次真开相机得出的结论（<c>AppHost.EffectiveSpec</c>）。</param>
    /// <param name="probed">**上一次实测过**的那一对（<c>AppHost.ProbedSpec</c>）。</param>
    /// <param name="fallbackReason">回落的原因，没有就是 <see langword="null"/>。</param>
    /// <returns>
    /// <c>Text</c> 是要显示的那句，<c>Warning</c> 为真时界面该把它画成警告色。
    /// </returns>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>三档情形，别用一句话糊过去</b>（2026-09-30 实测撞到过下面第二种）：
    /// </para>
    /// <list type="number">
    /// <item>用户选的那一对**就是**上次探过的那一对 ⇒ 说结论。</item>
    /// <item>用户刚改过、那一对**还没探过** ⇒ <paramref name="effective"/> 说的是
    /// **上一次**的结论。拿它去跟新选的比会印出一句「这台电脑跑不通」的**假话**
    /// （那一档根本还没测），所以要说清「下次开始工作时才实测」。</item>
    /// <item>探过且回落了 ⇒ 说结论 + 原因（§3.1.7 的「回落必须可见」）。</item>
    /// </list>
    /// <para>
    /// ⚠️ 方向**要一起比**：它也在 spec 里，不带上它的话，一个方向设成「转 180°」的
    /// 机器每次开这一页都会看到那句回落警告（两个 spec 的 <c>Rotation</c> 不一样）。
    /// 所以最后一档用的是**整条记录**的相等，而不是只看编码与分辨率。
    /// </para>
    /// <para>
    /// ⚠️ 这一句在**主窗口上看不见**（设计图的录制台上没有这个位置）——
    /// 它挪进了设置里。规格要的是「可见」，不是「必须印在首页」，
    /// 但它确实比以前难看见了，这一笔记在 <c>docs/实现决策.md</c>。
    /// </para>
    /// </remarks>
    public static (string Text, bool Warning) Describe(
        RecordingSpec wanted,
        RecordingSpec effective,
        RecordingSpec probed,
        string? fallbackReason)
    {
        // 情形 2：用户刚改的这一对还没实测过。**先判它**，否则会拿旧结论说新组合的不是。
        if (!probed.ProbeMatches(wanted))
        {
            return (
                $"「{wanted.Label}」还没实测过 —— 下次开始工作时会真开一次相机验一遍，"
                + $"验不过会自动回落并当场告诉你。"
                + $"当前按 {effective.Label} 录制（那是上一次实测的结论）。",
                false);
        }

        if (effective == wanted)
        {
            return ($"这台电脑按 {effective.Label} 录制。", false);
        }

        // 情形 3：探过，回落了。
        return (
            $"⚠️ 你选的是 {wanted.Label}，这台电脑实际按 {effective.Label} 录制。"
            + (string.IsNullOrWhiteSpace(fallbackReason)
                ? string.Empty
                : $"原因：{fallbackReason}"),
            true);
    }
}
