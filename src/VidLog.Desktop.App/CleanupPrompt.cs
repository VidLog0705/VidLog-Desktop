using System.Windows;
using VidLog.Desktop.Core.Cleanup;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的同名类型都带进来。这里钉死成 WPF 的那套。
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace VidLog.Desktop.App;

/// <summary>一次清理的结果，以及**给人看的那一句话**。</summary>
/// <param name="Ran">真去删了没有（用户点了【否】就是没删）。</param>
/// <param name="Message">接在时间戳后面、或者写在设置页状态行上的那句话。</param>
internal sealed record CleanupOutcome(bool Ran, string Message);

/// <summary>
/// 「先给预告、用户点过头才动手」这一段，**只写一遍**。
/// </summary>
/// <remarks>
/// <para>
/// 规格 §3.5.5 的原话：「**禁止静默清理**」「清理前必须给出预告
/// （将删除多少条、多少容量）」。所以每一步都得先弹这个框。
/// </para>
/// <para>
/// ⚠️ <b>为什么抽出来</b>：它现在有三个调用点 —— 启动时算一次（<c>MainWindow</c>）、
/// 设置页的【按时间清理…】与【按空间释放…】。三处各写一遍的话，
/// 那句「删掉的是本机上这一份，归档层上的那份不动」迟早有一处被改漏，
/// 而它是**不可逆动作的唯一一句解释**。
/// </para>
/// <para>
/// ⚠️ <b>本文件是外壳，不是逻辑</b>：预告框说什么、图标挑哪一档、点【是】之后
/// 该不该带 <c>force</c>，全都在 <see cref="CleanupAsk.For"/> 里算好了。
/// 留在这里的只有 <c>MessageBox.Show</c> 这一句 API 调用、以及它的参数怎么摆。
/// 原因是这个工程没有测试工程（T27）—— 把决定留在这一层，
/// 「界面把 force 传下去」这条线就永远是读出来对的、不是测出来的。
/// </para>
/// <para>
/// ⚠️ 预告那三行**逐字保留**（见 <see cref="CleanupAsk"/>）：
/// </para>
/// <list type="bullet">
/// <item>回查归档层（I8：查不到或查不了都不删）</item>
/// <item>删的只是本机这份（归档层那份不动）</item>
/// <item>已锁定 / 24 小时内的不动（§3.5.3② 的硬豁免）</item>
/// </list>
/// </remarks>
internal static class CleanupPrompt
{
    public static async Task<CleanupOutcome> AskAndRunAsync(
        Window owner, AppHost host, CleanupPlan plan, string headline)
    {
        var ask = CleanupAsk.For(plan, headline);

        var answer = MessageBox.Show(
            owner,
            ask.Body,
            ask.Title,
            MessageBoxButton.YesNo,
            ask.Severity == CleanupSeverity.Stop ? MessageBoxImage.Stop : MessageBoxImage.Warning,
            // 默认是「否」——不可逆的动作不该让回车键替用户点头。
            MessageBoxResult.No);

        // ⚠️ 只有安全阀真的跳起来时这个布尔才可能为 true（`CleanupAsk.ForceOnAccept`
        // 恒等于 `TripsSafetyValve`），所以这里不需要第三个分支，
        // 也就没有「手滑传了 force」的余地。
        if (answer != MessageBoxResult.Yes)
        {
            return new CleanupOutcome(false, ask.DeclinedMessage);
        }

        var report = await host.Services.Cleanup.RunAsync(plan, force: ask.ForceOnAccept);

        return new CleanupOutcome(true, CleanupAsk.CompletedMessage(report));
    }
}
