using System.Windows;
using VidLog.Desktop.Core.Diagnostics;

// 本工程同时开了 UseWPF 与 UseWindowsForms，ImplicitUsings 会把两边的同名类型
// 都带进来 —— `Application` 是其中一个（另一个在 System.Windows.Forms 里）。
// 与 App.xaml.cs 同一条规矩：钉死成 WPF 的那套。
using Application = System.Windows.Application;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 未捕获异常的兜底：**至少留下一条带堆栈的记录**。
/// </summary>
/// <remarks>
/// <para>
/// 2026-09-26 之前这里**一个钩子都没有**：这个应用是 <c>WinExe</c>（没有控制台），
/// 未捕获异常的表现是**窗口直接消失，磁盘上一个字都没有**。
/// 用户报「它自己没了」时，没有任何可查的东西。
/// </para>
/// <para>
/// ⚠️ <b>挂在 App 工程而不是 Core</b>：需要 WPF 的
/// <see cref="DispatcherUnhandledException"/>，而 Core 是 <c>net9.0</c>、引不了 WPF。
/// </para>
/// <para>
/// ⚠️ <b>崩溃那一行用 <see cref="FileLogger.WriteSync"/> 同步写</b>，
/// 不走日志器的队列 —— 进程随时会被终止，队列那两秒的等待根本等不到，
/// 于是最要紧的那条遗言恰好落不下。
/// </para>
/// </remarks>
internal static class CrashGuard
{
    /// <summary>装上三个钩子。**要在任何业务代码之前调**（见 <c>App.OnStartup</c>）。</summary>
    public static void Install(FileLogger logger)
    {
        // ① 非 UI 线程上的未捕获异常 —— 进程**肯定会死**，这里只来得及留遗言。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            logger.WriteSync(LogLevel.Error, "崩溃", "未捕获异常，进程即将结束", new Dictionary<string, object?>
            {
                // 完整堆栈：只说「出错了」等于什么也没说。
                ["异常"] = Describe(e.ExceptionObject as Exception),
                ["是否终止"] = e.IsTerminating,
            });
        };

        // ② UI 线程上的未捕获异常。
        //
        // ⚠️ **不设 `e.Handled = true`**：那是「让它继续跑」，
        // 而 UI 线程上炸过一次之后控件树可能已经是坏的，继续跑比退出更危险。
        // 这次改的是「留下记录」，不是「让它活下去」—— 行为与改动前一致，
        // 多的只是磁盘上那一条。
        Application.Current.DispatcherUnhandledException += (_, e) =>
        {
            logger.WriteSync(LogLevel.Error, "崩溃", "界面线程未捕获异常", new Dictionary<string, object?>
            {
                ["异常"] = Describe(e.Exception),
            });
        };

        // ③ 没人 await 的 Task。.NET Core 默认把这类异常**静默丢掉**，
        // 而本仓有好几处 `_ = Task.Run(...)`（回放服务的接收循环、
        // 摄像头识码），吞掉的正是那几处的失败。
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.WriteSync(LogLevel.Error, "崩溃", "没人处理的 Task 异常", new Dictionary<string, object?>
            {
                ["异常"] = Describe(e.Exception),
            });

            // 标记已观察：与 .NET Core 的默认行为一致（本来也不崩），
            // 这里只是顺手把状态收干净。
            e.SetObserved();
        };
    }

    /// <summary>启动失败也要留痕 —— 那条路今天只弹一个 MessageBox。</summary>
    public static void LogStartupFailure(FileLogger logger, Exception error) =>
        logger.WriteSync(LogLevel.Error, "启动", "启动失败", new Dictionary<string, object?>
        {
            ["异常"] = Describe(error),
        });

    /// <summary>把异常摊成一段可读的文本：**类型 + 消息 + 完整堆栈 + 内层**。</summary>
    /// <remarks>
    /// 用 <see cref="Exception.ToString"/> 而不是 <c>Message</c> ——
    /// 这个仓库此前 22 处日志**全都在记 <c>Message</c>**，
    /// 而「哪个方法、哪一行」正是事后最想知道的那件事。
    /// </remarks>
    private static string Describe(Exception? error) =>
        error?.ToString() ?? "（没有异常对象）";
}
