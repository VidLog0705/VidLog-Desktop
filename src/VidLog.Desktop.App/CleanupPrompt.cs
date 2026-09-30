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
/// ⚠️ 预告那三行**逐字保留**：
/// </para>
/// <list type="bullet">
/// <item>回查归档层（I8：查不到或查不了都不删）</item>
/// <item>删的只是本机这份（归档层那份不动）</item>
/// <item>已锁定 / 24 小时内的不动（§3.5.3② 的硬豁免）</item>
/// </list>
/// </remarks>
/// <param name="headline">
/// 第一句，由调用方拼 —— 三个入口要说的**是同一件事的不同算法**
/// （「保留期到了的 N 条」/「盘还剩多少、拟清 N 条」），而底下那三条一模一样。
/// </param>
internal static class CleanupPrompt
{
    public static async Task<CleanupOutcome> AskAndRunAsync(
        Window owner, AppHost host, CleanupPlan plan, string headline)
    {
        var answer = MessageBox.Show(
            owner,
            $"{headline}\n\n"
            + "要现在清理吗？\n"
            + "· 清理前会逐条回查归档层，查不到或查不了的那条不会删；\n"
            + "· 删掉的是本机上这一份，归档层上的那份不动；\n"
            + "· 已锁定与最近 24 小时内录的一条都不会动。",
            "清理本地副本",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            // 默认是「否」——不可逆的动作不该让回车键替用户点头。
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return new CleanupOutcome(false, $"这次没清理（{plan.Candidates.Count} 条仍在盘上）。");
        }

        var report = await host.Services.Cleanup.RunAsync(plan);

        return new CleanupOutcome(true,
            $"清理完成：删了 {report.Deleted.Count} 条"
            + $"（约 {report.FreedBytes / 1024 / 1024} MB），"
            + $"回查没通过、因此保留的有 {report.Refused.Count} 条（明细见清理流水）。");
    }
}
