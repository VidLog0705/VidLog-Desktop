namespace VidLog.Desktop.Core.Configuration;

/// <summary>
/// 写进 Run 键的那条命令行（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 抽出来只为让「引号加对了没有」这件事**看得见** —— 这是整套里唯一一处
/// 写错了会让 Windows 启动**别的程序**的地方：路径里有空格
/// （<c>C:\Program Files\…</c>）而不加引号时，系统会去启动 <c>C:\Program</c>，
/// 而那条命令**登记成功**、开机时也**真的尝试了**，报错在别的地方。
/// </para>
/// <para>
/// ⚠️ 拿不到 exe 路径时回 <see langword="null"/>，**不猜一个**：猜的那个
/// 后果与上面一样（开机启动一个不存在甚至不对的东西）。
/// </para>
/// </remarks>
public static class StartupCommand
{
    /// <summary>要写进 Run 键的那条命令行；拿不到 exe 路径时为 <see langword="null"/>。</summary>
    public static string? Build(string? exePath) =>
        string.IsNullOrWhiteSpace(exePath) ? null : $"\"{exePath}\"";

    // ── 换完用途自动重启（T31）─────────────────────────────────────
    //
    // ⚠️ 新进程为什么要「等上一个进程退」：相机在 dshow 上是**独占**的、
    // 回放端口（8720）也是，而新进程一进装配期就要拿这两样。
    // 旧进程是**最后**那一下才放掉它们的（`AppHost.ReleaseAsync`），
    // 抢在前头去拿，拿不到就是「启动失败」—— 而用户刚点的是【切换用途】。

    /// <summary>自动重启时传给新进程的那个开关。</summary>
    public const string WaitForPidSwitch = "--wait-for-pid";

    /// <summary>起新进程时要带的那串参数（让新进程先等 <paramref name="pid"/> 退干净）。</summary>
    public static string RestartArguments(int pid) => $"{WaitForPidSwitch} {pid}";

    /// <summary>
    /// 从命令行参数里取「等哪个进程退」（T31 自动重启）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不是自动重启（参数里没有那个开关）、或者后面的数读不出来时回
    /// <see langword="null"/> —— 那就**按普通启动走**。这里不抛异常：它跑在启动路径的
    /// 最前头，为一个手写的坏参数把应用拦在门外，比忽略它更糟。
    /// </remarks>
    /// <param name="args">不含 exe 路径的那一段参数（`StartupEventArgs.Args`）。</param>
    public static int? WaitForPid(IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return null;
        }

        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (!string.Equals(args[i], WaitForPidSwitch, StringComparison.Ordinal))
            {
                continue;
            }

            // ⚠️ `pid > 0`：0 不是一个能被等的进程，而 `GetProcessById(0)` 会去拿
            // 「系统空闲进程」—— 一个永远不退出、也永远拿不到退出事件的东西。
            return int.TryParse(args[i + 1], out var pid) && pid > 0 ? pid : null;
        }

        return null;
    }
}
