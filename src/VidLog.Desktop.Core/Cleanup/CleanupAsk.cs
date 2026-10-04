namespace VidLog.Desktop.Core.Cleanup;

/// <summary>预告框的严重程度。<b>界面那一层据此挑图标</b> —— 本层不认识 WPF。</summary>
/// <remarks>
/// ⚠️ 刻意不用 <c>MessageBoxImage</c>：Core 的 TFM 是纯 <c>net9.0</c>（不是
/// <c>-windows</c>），带了 WPF 类型就编不过。而界面只在
/// <c>CleanupPrompt</c> 那一处做一个两臂的 switch —— 比在 Core 里存一个 int
/// 再让两边各自记着「2 是 Stop」要安全。
/// </remarks>
public enum CleanupSeverity
{
    /// <summary>普通的到期清理。</summary>
    Warning,

    /// <summary>安全阀跳起来了（要删掉这份计划里一半以上）。</summary>
    Stop,
}

/// <summary>
/// 清理预告框要说的**全部内容**，以及「用户点了【是】之后该不该带 force」。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么这些东西在 Core、不在 <c>App</c></b>：它们本来长在
/// <c>CleanupPrompt</c>（WPF 外壳）里，而那个工程**至今没有测试工程**
/// （T27）—— 于是「界面究竟把哪个布尔传给了 <c>force</c>」这条线
/// **读得出、测不了**。真正测不了的东西只有 <c>MessageBox.Show</c> 那一句
/// API 调用本身；它周围的字符串、图标档位、force 的取值，全是我们自己的逻辑，
/// 换个工程放就立刻可测。为此**不新建 WPF 测试工程** ——
/// 那要拖进一整套 UI 装配，而收益只是让一句 API 调用进覆盖。
/// </para>
/// <para>
/// ⚠️ <b>这里的文案是「不可逆动作的唯一一句解释」</b>，逐字保留
/// （规格 §3.5.5「禁止静默清理」）。改动它之前先想清楚：用户是**看着这段话**
/// 点的【是】。
/// </para>
/// </remarks>
public sealed record CleanupAsk(
    string Title,
    string Body,
    CleanupSeverity Severity,
    bool TripsSafetyValve,
    int Candidates,
    int Total)
{
    /// <summary>
    /// 用户点了【是】之后，要不要带 <c>force</c> 覆盖安全阀。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它**恒等于** <see cref="TripsSafetyValve"/>，这不是巧合而是全部要点：
    /// 别的计划根本没有「覆盖」这回事，所以 force 只可能在安全阀真的跳起来时
    /// 是 true —— 没有「手滑传了 force」的余地。分成两个字段是为了让读代码的人
    /// 在**界面那一侧**看到问句（该不该覆盖？），在**执行那一侧**看到判据
    /// （阀跳了没有？），而两边的答案是同一个值。
    /// </remarks>
    public bool ForceOnAccept => TripsSafetyValve;

    /// <summary>用户点了【否】之后写在状态行/日志里的那一句。</summary>
    public string DeclinedMessage => TripsSafetyValve
        ? $"这次没清理：要删 {Candidates} / {Total} 条（超过一半），按安全阀中止，一条都没删。"
        : $"这次没清理（{Candidates} 条仍在盘上）。";

    /// <summary>
    /// 拼出这一整个预告框。**一个文件都不碰。**
    /// </summary>
    /// <param name="plan">已经算好的计划。</param>
    /// <param name="headline">
    /// 第一句，由调用方拼 —— 三个入口要说的**是同一件事的不同算法**
    /// （「保留期到了的 N 条」/「盘还剩多少、拟清 N 条」），而底下那三条一模一样。
    /// </param>
    public static CleanupAsk For(CleanupPlan plan, string headline)
    {
        var candidates = plan.Candidates.Count;
        // 分母取 Candidates + Exempted（两者互斥，加起来就是这份计划看过的全集），
        // 所以调用方不用另外报一个总数。
        var total = candidates + plan.Exempted.Count;

        // ⚠️ 判据走的是执行层同一个函数 —— **一处定义、两处用**。界面这道是
        // 「不让用户在不知情的情况下点下去」，执行层那道是「就算被绕过了也不许删」。
        // 两处各写一遍的话，改了门槛只改一处就会对不上。
        var trips = CleanupExecutor.TripsSafetyValve(plan);

        var body = $"{headline}\n\n"
            // ⚠️ 这段只在安全阀跳起来时插进去，**底下那三条逐字不动**。
            + (trips
                ? $"⚠️ 这一次要删 {candidates} 条，而这份计划里一共只有 {total} 条"
                  + " —— 超过一半。\n"
                  + "这个比例多半是算错了（保留期设成了「不保留」、索引读漏了一截、"
                  + "或者【按空间释放】碰上一个探错的剩余空间），不是真的该删这么多。\n"
                  + "建议先点【否】，回设置里核一下保留期，然后再来。\n\n"
                : "")
            + "要现在清理吗？\n"
            + "· 清理前会逐条回查归档层，查不到或查不了的那条不会删；\n"
            + "· 删掉的是本机上这一份，归档层上的那份不动；\n"
            + "· 已锁定与最近 24 小时内录的一条都不会动。";

        return new CleanupAsk(
            trips ? "删除比例异常，请再确认一次" : "清理本地副本",
            body,
            trips ? CleanupSeverity.Stop : CleanupSeverity.Warning,
            trips,
            candidates,
            total);
    }

    /// <summary>真删完之后写在状态行上的那一句。</summary>
    public static string CompletedMessage(CleanupReport report) =>
        $"清理完成：删了 {report.Deleted.Count} 条"
        + $"（约 {report.FreedBytes / 1024 / 1024} MB），"
        + $"回查没通过、因此保留的有 {report.Refused.Count} 条（明细见清理流水）。";
}
