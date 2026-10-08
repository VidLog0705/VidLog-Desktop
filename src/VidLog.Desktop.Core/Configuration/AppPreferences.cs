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
/// 「外观主题」那一行的三档（设计图 `_42`）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b><see cref="FollowSystem"/> 必须显式写 0，而且这是承重的</b>：老设置文件里
/// 没有这个字段 ⇒ 反序列化取枚举默认值。而本程序在做出这三档**之前**的行为就是
/// 「跟随系统」（<c>AppTheme</c> 从注册表读 `AppsUseLightTheme`）——
/// 把 0 让给别人，所有升级过的机器从某天起界面会突然**固定**成一种颜色，
/// 而用户什么都没改过。与 <see cref="CloseWindowAction"/>、<c>StationRole</c> 同一条规矩。
/// </para>
/// <para>
/// ⚠️ 枚举值落进 <c>settings.json</c>，而 <see cref="AppPreferences.Themes"/> 那张表的
/// **次序就是这里的次序**（那条由 <c>AppPreferencesTests</c> 钉着）⇒
/// <b>顺序即格式，别重排</b>。
/// </para>
/// </remarks>
public enum AppThemeMode
{
    /// <summary>
    /// 跟随 Windows 的「应用模式」（做出这三档之前的行为，也是升级后的默认）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它读的是 <c>AppsUseLightTheme</c>，**不是**标题栏那个
    /// <c>SystemUsesLightTheme</c> —— 这是「应用」那一档，见 <c>AppTheme</c>。
    /// </remarks>
    FollowSystem = 0,

    /// <summary>固定浅色 —— 不管 Windows 怎么设。</summary>
    Light = 1,

    /// <summary>固定深色 —— 不管 Windows 怎么设。</summary>
    Dark = 2,
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
/// ⚠️ <b>为什么它们原先不进 <see cref="AppSettings"/></b>：一个只有一种取值的设置
/// 是个假开关 —— 存下来、读回来、做什么都不变。所以那时它们**不落盘**，
/// 界面上把真做了的那一档显示出来、其余各挂一句为什么还不能用。
/// 等真的做出第二档，再往 <see cref="AppSettings"/> 里加字段。
/// </para>
/// <para>
/// ⚠️ <b>外观主题那一行 2026-10-08 走到了这一句的后半</b>：三档都做出来了
/// （<see cref="AppThemeMode"/>），于是它有了 <see cref="AppSettings.ThemeMode"/>、
/// 真的落盘、真的当场换色 —— 这一行那张档位表（<see cref="Themes"/>）现在三档全是「已做」。
/// <b>界面语言那一行仍停在原处</b>（只有中文一档）。
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
    /// <para>
    /// ⚠️ <b>三档现在都是真的</b>（2026-10-08）：<see cref="AppThemeMode"/> 那三档
    /// 各有一套配色，选中就真的换（亮暗两套色盘 + 标题栏，见 <c>AppTheme</c>）。
    /// 所以这张表**只剩「档位叫什么名字」这一个用途**，
    /// <see cref="PreferenceOption.Hint"/> 三档都是 <see langword="null"/>、
    /// 而「点了没做的那一档要退回来」那套机制对**这一张**表已经不适用了
    /// （它在 <see cref="Languages"/> 那头仍然是活的）。
    /// </para>
    /// <para>
    /// ⚠️ <b>表的次序 = <see cref="AppThemeMode"/> 的次序</b>，这条是**承重的**：
    /// 界面按下标填下拉、设置里按下标选档（<see cref="IndexOf"/> 就是
    /// <c>(int)mode</c>）。两者错开一位，「固定深色」会选中「浅色」那一档，
    /// 而屏幕上只是颜色不对 —— 不会有任何东西喊。<c>AppPreferencesTests</c> 钉着它。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PreferenceOption> Themes { get; } =
    [
        new("跟随系统", true, null),
        new("浅色", true, null),
        new("深色", true, null),
    ];

    /// <summary>某一档在下拉里是第几项。</summary>
    /// <remarks>
    /// ⚠️ 直接吃枚举的数值 —— 表的次序与 <see cref="AppThemeMode"/> 的次序必须一致，
    /// 理由与钉子都在 <see cref="Themes"/> 的 remarks 里。
    /// </remarks>
    public static int IndexOf(AppThemeMode mode) => (int)mode;

    /// <summary>下拉里第几项对应哪一档；认不出来时回最保守的那一档。</summary>
    /// <remarks>
    /// ⚠️ 认不出来（下拉被清空、下标是 -1、将来枚举加了值而这个方法没跟上）
    /// 退的是 <see cref="AppThemeMode.FollowSystem"/>：它是**升级前的行为**，
    /// 也是「用户没表态」时唯一说得通的那一档。与
    /// <c>SettingsWindow.DescribeCloseAction</c> 退「最小化到托盘」同一个理由。
    /// </remarks>
    public static AppThemeMode ThemeOf(int index) =>
        Enum.IsDefined((AppThemeMode)index) ? (AppThemeMode)index : AppThemeMode.FollowSystem;

    /// <summary>某一档在下拉里显示的字。</summary>
    /// <remarks>
    /// ⚠️ 从 <see cref="Themes"/> 取，**不另写一份**：写两份的下场是同一边写「跟随系统」、
    /// 另一边写「跟随 Windows」，用户会以为是两件事（与 <c>StationRoles.Describe</c> 同）。
    /// </remarks>
    public static string Describe(AppThemeMode mode) => Themes[IndexOf(mode)].Label;

    /// <summary>
    /// 某一档的**后果**那句话（显示在那一行下面）。
    /// </summary>
    /// <param name="mode">选中的那一档。</param>
    /// <param name="systemIsDark">Windows 现在是不是深色（只有跟随系统那一档用得上）。</param>
    /// <remarks>
    /// ⚠️ <b>「跟随系统」那一档必须把系统的现状说出来</b>：不说的话，
    /// 系统是亮色的用户选了「跟随系统」会看到**什么都没变** ——
    /// 那正是踩坑 #13 要防的「点了像没反应」。这一句就是本功能存在的一半理由。
    /// </remarks>
    public static string ThemeNote(AppThemeMode mode, bool systemIsDark) => mode switch
    {
        AppThemeMode.Light => "固定浅色：不管 Windows 怎么设，界面一直是浅色。",
        AppThemeMode.Dark => "固定深色：不管 Windows 怎么设，界面一直是深色。",
        _ => systemIsDark
            ? "跟随系统：Windows 现在是深色，界面就是深的。"
            : "跟随系统：Windows 现在是浅色，界面就是浅的。",
    };

    /// <summary>下拉该选中第几档 —— **真做了的那一档**（照实显示现状）。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 取的是**最后一个** <see cref="PreferenceOption.Implemented"/>，不是第一个：
    /// 表里眼下只有一档是真的（只有<see cref="Languages"/>还用得着这个方法），
    /// 所以两种取法今天等价；写成「最后一个」是因为将来真做出第二档时，
    /// 那一档多半排在后面，而「选中第一个真的」会静默选错。
    /// </para>
    /// <para>
    /// ⚠️ <b>外观主题那一行已经不用它了</b>（2026-10-08 起三档都真了）——
    /// 那一行选中的是「设置里存着的那一档」，没有「真做了的」可挑。
    /// </para>
    /// <para>
    /// ⚠️ 一档都没做时回 <c>0</c>（与搬走之前的写法一致）：那不是正常情形，
    /// 而那时选第一个至少是**看得见的**，比不回话强。
    /// </para>
    /// </remarks>
    public static int RealIndex(IReadOnlyList<PreferenceOption> options)
    {
        var real = 0;

        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].Implemented)
            {
                real = i;
            }
        }

        return real;
    }

    /// <summary>
    /// 点了某一档之后要说的那句话；**该放行时给 <see langword="null"/>**。
    /// </summary>
    /// <param name="options">这张表。</param>
    /// <param name="selected">用户点中的第几档（下拉框选中项的下标）。</param>
    /// <param name="real">真做了的那一档（<see cref="RealIndex"/>）。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ 回 <see langword="null"/> 就是**放行**（选中的是真做了的那一档，
    /// 或者压根没有选中项）—— 退回时又会进来一次，那时下标已经对了，
    /// 这里必须放行，否则会递归。
    /// </para>
    /// <para>
    /// ⚠️ <b>说这句话等于承诺「会退回来」</b>：那一档**不落盘**
    /// （见 <see cref="PreferenceOption"/> 的类型注释），所以点了不真的那一档必须
    /// 当场退回原位 —— 留在那里就是一个**骗人的假开关**（踩坑 #13）。
    /// 两件事是一对：回 note 的调用处就得负责退回。
    /// </para>
    /// </remarks>
    public static string? RejectNote(IReadOnlyList<PreferenceOption> options, int selected, int real)
    {
        if (selected < 0 || selected == real || selected >= options.Count)
        {
            return null;
        }

        var picked = options[selected];

        return $"「{picked.Label}」还在开发中 —— {picked.Hint}";
    }
}
