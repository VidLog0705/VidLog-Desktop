using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Web;

/// <summary>
/// 回放服务那条**入站放行**（Windows 防火墙规则）现在还在不在。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要这个（2026-10-03 报上来的缺陷）：回放服务绑的是 <c>http://+:8720/</c>，
/// 走的是 <b>http.sys</b> —— 而 Windows 那个「允许访问」的弹窗**不会出现**，
/// 因为监听的是 System 进程，没人有点的机会。于是 urlacl 注册好了、界面一切正常、
/// 二维码也照画，<b>手机的请求却在防火墙这一层被丢掉</b>：手机报「连不上」，
/// 电脑端一个字都不显示。现场看起来就是「扫码后电脑端不提示同意」。
/// </para>
/// <para>
/// ⇒ 安装程序负责加这条规则（见 <c>installer/VidLog.iss</c>）；这里负责
/// <b>看得出来它在不在</b>，让界面能说出来 —— 而不是让用户对着一张好码干等（I3）。
/// </para>
/// <para>
/// ⚠️ 判据只用 <c>netsh</c> 的**退出码**，**不读它印出来的字**：那是跟着系统语言走的
/// （中文机器上写「没有与指定条件匹配的规则。」），读字等于给自己埋一颗随语言爆的雷。
/// 实测（2026-10-03，本机 Win10 19045）：规则不在 = 退出码 1，在 = 0。
/// </para>
/// </remarks>
public static class PlaybackFirewall
{
    /// <summary>安装程序加、卸载程序删的那条规则的**名字**。</summary>
    /// <remarks>
    /// ⚠️ 全小写、不含空格与中文：这个名字要在 <c>netsh</c> 的命令行上原样传过去，
    /// 而命令行走 <c>cmd</c> 时会被系统的 ANSI 代码页读 —— 中文名在别的机器上会花。
    /// <c>.iss</c> 里那一串必须与这里**逐字相同**，有绊线测试钉着（改一处忘另一处 =
    /// 界面说「没放行」而它其实放行了，或者反过来）。
    /// </remarks>
    public const string RuleName = "VidLog-Playback-8720";

    /// <summary>问一次「这条规则在不在」用的 argv（<c>netsh.exe</c> 那一份）。</summary>
    public static IReadOnlyList<string> ShowArguments(string ruleName = RuleName) =>
        ["advfirewall", "firewall", "show", "rule", $"name={ruleName}"];

    /// <summary>加那条规则该敲什么（界面上要原样念给用户听）。</summary>
    /// <remarks>
    /// 与 <c>.iss</c> 里 [Run] 那一行是同一件事的两种写法：安装程序自动做，
    /// 绿色包与「装完之后才被策略改掉」的机器只能手敲。
    /// <c>remoteip=localsubnet</c> 是**故意**的：只放行本网段，
    /// 不给整个人网开一道门（手机本来也只可能从局域网连过来）。
    /// <c>profile=any</c> 与 <c>.iss</c> 里那一份**逐项相同**：不写虽然也默认
    /// 「所有配置文件」，但这条修复的立身之本就是**两处说同一件事** ——
    /// 差一个只在某些机器上才生效的默认值，就是下一个「怎么也连不上」。
    /// </remarks>
    public static IReadOnlyList<string> AddArguments(int port, string ruleName = RuleName) =>
    [
        "advfirewall", "firewall", "add", "rule",
        $"name={ruleName}",
        "dir=in",
        "action=allow",
        "protocol=TCP",
        $"localport={port}",
        "remoteip=localsubnet",
        "profile=any",
    ];

    /// <summary>把上面那条 argv 拼成一行能直接粘进命令行的字。</summary>
    public static string AddCommandLine(int port, string ruleName = RuleName) =>
        "netsh " + string.Join(' ', AddArguments(port, ruleName));

    /// <summary>放行规则在不在。</summary>
    /// <returns>
    /// <see langword="true"/> 在 / <see langword="false"/> 不在 /
    /// <see langword="null"/> <b>问不出来</b>（netsh 起不来、退出码不是 0 或 1）。
    /// ⚠️ 「问不出来」**绝不能**当成「不在」—— 那会让界面在别的杀软/策略机器上
    /// 平白吓人一跳，而这正是「用户可见通道分得清吗」那条要求的反面。
    /// </returns>
    /// <remarks>
    /// 起不来（<c>netsh.exe</c> 不在、被执行策略挡住）时**让它抛**，
    /// 由调用方记一条日志后当作「问不出来」—— 不在这里吞掉。
    /// </remarks>
    public static async Task<bool?> IsRulePresentAsync(
        IProcessRunner runner,
        string ruleName = RuleName,
        CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync("netsh.exe", ShowArguments(ruleName), cancellationToken);

        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => null,
        };
    }
}
