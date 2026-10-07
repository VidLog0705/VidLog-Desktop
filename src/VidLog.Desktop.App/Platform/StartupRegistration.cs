using Microsoft.Win32;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的同名类型都带进来。这里钉死成要用的那个。
using Registry = Microsoft.Win32.Registry;
using RegistryKey = Microsoft.Win32.RegistryKey;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 「开机自启动」（设计图 `_49`）的真身：<c>HKCU</c> 的 Run 键里那一条。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>写在 <c>HKCU</c>，不写 <c>HKLM</c></b>：后者要管理员权限，
/// 而一个工位程序要 UAC 才能开自启动，用户只会觉得它坏掉了。
/// 而且 <c>HKCU</c> 是「这个用户登录时启动」，正是工位要的语义。
/// </para>
/// <para>
/// ⚠️ <b>命令行要带引号</b>：<c>C:\Program Files\...</c> 这种带空格的路径
/// 不加引号的话，Windows 会把它切成两截，于是开机时启动的是
/// <c>C:\Program.exe</c> —— 要么什么都不发生，要么启动一个**别的程序**。
/// </para>
/// <para>
/// ⚠️ <b>本类没有测试能挡</b>（App 层没有测试工程，而注册表也不是能随便写的
/// 东西）。所以它只做两件事、每件事都短到看得清，并且在日志里留痕。
/// </para>
/// </remarks>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "VidLog";

    /// <summary>现在注册表里有没有这一条（读得到值且不为空）。</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);

            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // 读不到就当没注册：界面上那个开关显示成关，用户一勾就会重写一遍。
            // ⚠️ 反过来（读不到就当成"已注册"）会让开关显示成开、而实际什么都没配。
            return false;
        }
    }

    /// <summary>
    /// 让注册表与设置里的意图一致。<b>幂等</b>。
    /// </summary>
    /// <returns>
    /// 失败时给一句给人看的原因；成功（或本来就不用改）给 <see langword="null"/>。
    /// </returns>
    /// <remarks>
    /// ⚠️ <b>每次都重写一遍（而不是"只在变了的时候改"）</b>：程序可能被搬到
    /// 别的目录，那时注册表里那条指向的是**已经不存在的 exe** ——
    /// 重写是唯一能让它追上来的办法，而它的成本是几毫秒。
    /// </remarks>
    public static string? Apply(bool wanted, IAppLogger? logger = null)
    {
        var command = BuildCommand();

        if (command is null)
        {
            // 拿不到自己的 exe 路径（单文件发布、被宿主加载……）。
            // ⚠️ 这时候**什么都不写**，并如实说：猜一个路径写进去，
            // 后果是开机时启动一个不存在甚至不对的东西。
            return wanted ? "拿不到本程序自己的路径，没法登记开机自启动。" : null;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key is null)
            {
                return wanted ? "打不开开机自启动的注册表项。" : null;
            }

            if (wanted)
            {
                key.SetValue(ValueName, command, RegistryValueKind.String);
                logger?.Log(LogLevel.Info, "设置", $"已登记开机自启动：{command}");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                logger?.Log(LogLevel.Info, "设置", "已取消开机自启动。");
            }

            return null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            logger?.Log(LogLevel.Warn, "设置", $"改开机自启动失败：{ex.Message}");

            return wanted
                ? $"登记开机自启动失败（{ex.Message}）。"
                : $"取消开机自启动失败（{ex.Message}）。";
        }
    }

    /// <summary>
    /// 要写进 Run 键的那条命令行；拿不到 exe 路径时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 引号那件事在 <see cref="StartupCommand.Build"/> 里（T27② 第 4 批）——
    /// 它是这一整套里唯一一处错了会让 Windows 启动**别的程序**的地方，
    /// 所以有一条测试钉着它。这里只剩「本进程的 exe 是哪一个」。
    /// </remarks>
    private static string? BuildCommand() => StartupCommand.Build(Environment.ProcessPath);
}
