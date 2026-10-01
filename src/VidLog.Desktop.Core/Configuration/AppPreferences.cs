namespace VidLog.Desktop.Core.Configuration;

/// <summary>
/// 关窗口时怎么办（设计图 `_49`「关闭窗口时」那一行）。
/// </summary>
/// <remarks>
/// <para>
/// 图上三个选项逐字：「可以每次询问、最小化到托盘或直接退出」。
/// </para>
/// <para>
/// ⚠️ <b><see cref="MinimizeToTray"/> 必须显式写 0，而且这是承重的</b>：
/// 老设置文件里没有这个字段 ⇒ 反序列化取枚举默认值。而这个程序**今天的行为**
/// 就是「关窗 = 收进托盘」（`MainWindow.OnClosing` 一直是 `e.Cancel = true` + `Hide()`）。
/// 把 0 让给「每次询问」的话，**所有升级过的机器**从某天起关窗都会多弹一个框 ——
/// 而用户什么都没改过。与 <see cref="StationRole"/> 同一条规矩。
/// </para>
/// <para>
/// ⚠️ 枚举值落进 <c>settings.json</c>，**顺序即格式，别重排**。
/// </para>
/// </remarks>
public enum CloseWindowAction
{
    /// <summary>收进托盘，程序继续跑（沿用今天的行为，也是升级后的默认）。</summary>
    /// <remarks>
    /// ⚠️ 这一档**不能**顺手关掉程序：托盘存在的前提就是「窗口收起来但扫码照常工作」
    /// （规格 §3.2.1）。直接退出那一档是用户**明确选**的，不是默认。
    /// </remarks>
    MinimizeToTray = 0,

    /// <summary>每次都弹一个框问（最小化到托盘 / 直接退出 / 取消）。</summary>
    AskEveryTime = 1,

    /// <summary>直接退出（仍然会走「在录就先问一句」那条既有流程）。</summary>
    Exit = 2,
}

/// <summary>
/// 设置里那些「图上能点、而本程序还没做」的选项呈现成什么样。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>口径 2026-10-01 改过，两次都是需求方定的：</b>
/// </para>
/// <list type="number">
/// <item>
/// 先前：「设计图上能点、VidLog 没这功能的按钮 → **禁用 + 悬停写明原因**」。
/// </item>
/// <item>
/// 现在：「**只留窗口，用户如点开，显示正在开发中**」—— 所以这些档**不再禁用**，
/// 选中它们会**当场说一句实话并退回**。
/// </item>
/// </list>
/// <para>
/// ⚠️ <b>为什么改得有道理</b>：悬停提示**只有鼠标停上去才看得见** ——
/// 触屏、或者压根不悬停的人，永远不知道为什么点不动，只会以为程序坏了。
/// 点一下就有一句话，谁都能看懂。踩坑 #13 真正禁的是**第三种**：
/// 点了没反应、让用户以为是自己那边的问题。
/// </para>
/// <para>
/// ⚠️ <b>为什么这两行仍然不进 <see cref="AppSettings"/></b>：一个只有一种取值的设置
/// 是个假开关 —— 存下来、读回来、做什么都不变。所以它们**不落盘**，
/// 界面上把真做了的那一档显示出来、其余各挂一句为什么还不能用。
/// 等真的做出第二档（英文界面、深色主题），再往 <see cref="AppSettings"/> 里加字段。
/// </para>
/// <para>
/// ⚠️ 文案放 Core 而不是 XAML，理由与 <c>StationRoles.Describe</c> 一致：
/// 放这里才**测得到**（App 层没有测试工程，XAML 里的字只能靠人跑一遍看）。
/// </para>
/// </remarks>
/// <param name="Label">下拉里显示的字。</param>
/// <param name="Implemented">
/// 这一档**真做了**没有。
/// ⚠️ 不是「能不能点」—— 现在每一档都点得动，没做的那几档点下去会说一句实话再退回来。
/// </param>
/// <param name="Hint">还没做时那句实话；真做了的档为 <see langword="null"/>。</param>
public sealed record PreferenceOption(string Label, bool Implemented, string? Hint);

/// <summary>
/// 「设备与外观」页里那两行的选项表（设计图 `_42`）。
/// </summary>
public static class AppPreferences
{
    /// <summary>界面语言那一行的标题（图上原文）。</summary>
    public const string LanguageCaption = "默认跟随 Windows 显示语言，修改后重启生效。";

    /// <summary>外观主题那一行的标题（图上原文）。</summary>
    public const string ThemeCaption = "可跟随系统，或固定为浅色 / 深色主题。";

    /// <summary>界面语言的选项。</summary>
    /// <remarks>
    /// ⚠️ 真做了的只有「中文（简体）」那一档 —— 其余两档点下去会说一句实话再退回来。
    /// </remarks>
    public static IReadOnlyList<PreferenceOption> Languages { get; } =
    [
        new("跟随系统", false,
            "本程序只做了中文 —— 跟随系统没有第二个语言可切，等做了英文再说。"),
        new("中文（简体）", true, null),
        new("English", false,
            "英文界面还没做。需求方 2026-09-30 裁决：只做中文，英文项暂缓。"),
    ];

    /// <summary>外观主题的选项。</summary>
    /// <remarks>
    /// ⚠️ <b>真做了的只有浅色</b>：设计图整套是浅色（Tailwind 色板逐像素量出来的，
    /// 见 `docs/实现决策.md` §59），而需求方的裁决是「**严格照图**」。
    /// 深色主题要**整表**再来一套配色 —— 在那之前它点下去只回一句话，
    /// **不会真的切过去**（切一半的深色比不切更糟）。
    /// </remarks>
    public static IReadOnlyList<PreferenceOption> Themes { get; } =
    [
        new("跟随系统", false,
            "深色主题还没做，跟随系统没有可切的目标 —— 现在一律是设计图上的浅色。"),
        new("浅色", true, null),
        new("深色", false,
            "深色主题还没做。设计图全套是浅色，需求方定了「严格照图」，所以先只做浅色。"),
    ];
}
