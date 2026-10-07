namespace VidLog.Desktop.Core.Media;

/// <summary>
/// 取景框上写的那两行字（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// <para>
/// 一行在**右上角**（可信时间的水印），一行在**中央**（为什么没有画面）。
/// 两行原先都长在 <c>MainWindow</c> 里，而那个工程没有测试工程 ——
/// 而这两句话各自都有一条**说错了很坏**的规矩，见下面两个方法。
/// </para>
/// <para>
/// ⚠️ 这里的措辞与颜色无关：颜色（`Warning` / `TextSecondary` 那类资源名）
/// 留在外壳，Core 的 TFM 是纯 <c>net9.0</c>，碰不到 `Brush`。
/// </para>
/// </remarks>
public static class PreviewTexts
{
    /// <summary>取景框右上角那个时间水印。</summary>
    /// <param name="isCalibrated">可信时钟校准过没有。</param>
    /// <param name="now">要写上去的时刻（<b>UTC 那个</b>，本方法自己转当地时间）。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>未校准时不许显示一个时间</b>：那时可信时钟的 <c>Now</c> 会静默回落到
    /// 系统墙钟，而一个**看起来正常、其实不可信**的时间比空着坏得多 ——
    /// 用户拿它去核对快递单上的手写时间，会照着它去改自己的记录。
    /// 用户信这个水印，正是因为它与**烧进录像里**的那个时间同源；
    /// 未校准时那个同源关系不成立，所以只能如实说未校准（规格 §3.6.4 的同一精神，
    /// 长句版本见 <see cref="Clock.CalibrationText"/>）。
    /// </para>
    /// <para>
    /// ⚠️ <b>这里自己做 <c>ToLocalTime()</c></b>：交给调用处做的话，
    /// 漏一次就会安安静静地印出一个 UTC 时间 —— 它和本地时间长得一模一样，
    /// 没人会看出来。所以「转成本地」是这个水印的一部分，不外包。
    /// </para>
    /// <para>
    /// ⚠️ 时区写成 <c>UTC+08</c>：用户要核对的是「这台机器认的时间对不对」，
    /// 而只写 <c>08:15:00</c> 的话，他不知道那是哪一个时区的时间。
    /// （⚠️ 偏移量取的是整点那一格，所以 <c>+05:30</c> 这类时区会印成 <c>UTC+05</c>；
    /// 交付地是 <c>+08:00</c>，此处**按原样搬过来，没有改**。）
    /// </para>
    /// </remarks>
    public static string Watermark(bool isCalibrated, DateTimeOffset now)
    {
        if (!isCalibrated)
        {
            return "时间未校准";
        }

        var local = now.ToLocalTime();

        return $"UTC{(local.Offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(local.Offset.Hours):00}: "
            + local.ToString("yyyy/MM/dd HH:mm:ss");
    }

    /// <summary>取景框中央那行说明。</summary>
    /// <param name="hasFfmpeg">本机找得到 FFmpeg 没有。</param>
    /// <param name="hasCamera">找得到摄像头没有。</param>
    /// <param name="isWorking">这会儿在不在工作。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>必须说清楚为什么没有画面</b>：一个空框，与「相机坏了」「还没开始」
    /// 「程序卡住了」三种情况长得一模一样。
    /// </para>
    /// <para>
    /// ⚠️ <b>三句的先后次序是有讲究的</b>，不是随便排的：FFmpeg 与摄像头是
    /// **这台机器上缺了东西**（用户得去装、去插），而「还没开始工作」是**正常的**。
    /// 把最后的正常态排到前面去，会让一台配置齐全、只是还没开工的机器
    /// 显示成像是坏了。
    /// </para>
    /// <para>
    /// ⚠️ 文案里点名的必须是**界面上真有的那颗按钮**：顶栏那颗叫【开始录制】
    /// （「开始工作」是代码里的叫法，用户看不见）。2026-10-02 截图核对时发现的
    /// —— 原来写的是【开始工作】，用户照着去找一个不存在的按钮。
    /// </para>
    /// <para>
    /// ⚠️ 第三句写着「原因会记在通知里」，所以**出这句话的那一刻必须真的记一条**
    /// （§6.1）—— 那一条留痕长在调用处，因为它要读「上一次还有没有画面」这种
    /// 只有界面这一侧才知道的东西。两者是**一对**，改这边记得看那边。
    /// </para>
    /// </remarks>
    public static string Hint(bool hasFfmpeg, bool hasCamera, bool isWorking) =>
        !hasFfmpeg ? "本机没有 FFmpeg，无法采集，也没有画面。"
        : !hasCamera ? "没有找到摄像头，所以这里没有画面。到【设置 → 设备与外观】里看看。"
        : isWorking ? "取景画面没出来。录制本身不受影响，原因会记在通知里。"
        : "还没开始工作。点【开始录制】之后，这里就会显示取景画面。";
}
