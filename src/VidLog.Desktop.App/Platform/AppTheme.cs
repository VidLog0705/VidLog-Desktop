using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

using Microsoft.Win32;

using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Theme;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的同名类型都带进来。这里钉死成要用的那些。
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 亮/暗主题（改造清单第 5 批；2026-10-08 起「跟随系统 / 固定浅色 / 固定深色」三档）。
/// </summary>
/// <remarks>
/// <para>
/// <b>哪一档由设置说了算</b>（<see cref="AppSettings.ThemeMode"/>）：这一层不自作主张，
/// 它只回答「这一档现在意味着暗色吗、要不要真的换」。三处入口：
/// <see cref="Start"/>（启动时先按跟随系统画出来，免得启动窗闪一下）、
/// <c>AppHost.StartAsync</c> 与 <c>AppHost.SaveSettingsAsync</c>（读/存了设置之后
/// 各调一次 <see cref="Apply"/>）。
/// </para>
/// <para>
/// ⚠️ <b>「系统换了主题」那条路只在跟随系统那一档是活的</b> —— 见
/// <see cref="OnUserPreferenceChanged"/>。固定深色的用户在亮色系统上，
/// 系统换不换主题都不该动他的界面。
/// </para>
///
/// <para>
/// <b>做法是「最后再合并一份暗色覆盖字典」，不是「改笔刷的颜色」。</b>
/// 后者的路子走不通，实测过（2026-10-07）：
/// <list type="bullet">
///   <item><c>Theme.xaml</c> 是从 <c>ResourceDictionary.Source</c> 装进合并字典的，
///         里面的 <c>Freezable</c> <b>23 支笔刷全部被冻结</b> ⇒ 写 <c>brush.Color</c> 直接抛
///         （第一版就是这么崩的：双击图标，窗口闪一下就没了，日志里一条
///         <c>InvalidOperationException: …处于只读状态</c>）；</item>
///   <item>那换一支不冻的放回去？也不行 —— <c>ResourceDictionary</c> <b>插入时就把
///         <c>Freezable</c> 冻上</b>（同一个对象放回来 <c>IsFrozen</c> 就变 true，实测）。</item>
/// </list>
/// 所以只剩「换字典」这一条路。而它要成立，界面那几百处取色**必须是
/// <c>DynamicResource</c>** —— <c>StaticResource</c> 在加载那一刻就把值解析定了，
/// 实测：换完字典，<b>新建的 <c>PrimaryButton</c> 拿到的仍是亮色那支</b>
/// （<c>Style</c> 在 BAML 里已经 seal，<c>Setter</c> 里焊的是具体那支笔刷）。
/// 第 5 批把 267 处 <c>{StaticResource 颜色键}</c> 一并改成了 <c>DynamicResource</c>。
/// </para>
/// <para>
/// ⚠️ <b>改哪些键、改成什么，全在 <c>VidLog.Desktop.Core/Theme/ThemePalette.cs</c></b>
/// （那边有测试钉着「一个键都不能少、色值是量出来的」）。这里只负责四件事：
/// 读注册表、算出「这一档现在该是暗色吗」、合并/撤掉那份覆盖字典、听系统换主题。
/// </para>
/// </remarks>
internal static class AppTheme
{
    /// <summary>
    /// 「应用用深色还是浅色」那一个值所在的注册表路径（<c>HKCU</c>）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这是 Windows 10 1809 起「设置 → 个性化 → 颜色 → 选择默认应用模式」
    /// 那个开关的真身。它<b>不管标题栏</b>（那是 <c>SystemUsesLightTheme</c>），
    /// 而这里要的正是「应用」。
    /// </remarks>
    private const string PersonalizeKeyPath =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>现在合并着的那份暗色覆盖字典；亮色时是 <see langword="null"/>。</summary>
    private static ResourceDictionary? _darkOverlay;

    /// <summary>
    /// 现在生效的那一档（设置里那个值；<see cref="Start"/> 之前是
    /// <see cref="AppThemeMode.FollowSystem"/> = 做出这三档之前的行为）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它决定 <see cref="OnUserPreferenceChanged"/> 要不要理系统那条消息 ——
    /// 固定成浅色/深色的用户不该被系统主题变动碰到。
    /// </remarks>
    private static AppThemeMode _mode = AppThemeMode.FollowSystem;

    private static bool _following;

    /// <summary>
    /// <see cref="Start"/> 拿到的那支日志器。改动发生在<b>运行期</b>（系统换主题）时
    /// 也要留痕，而那些回调拿不到调用方的参数。
    /// </summary>
    private static IAppLogger? _logger;

    /// <summary>
    /// 标题栏那次 P/Invoke 失败过没有。<b>只记一条</b> ——
    /// <see cref="ApplyTitleBar"/> 每个窗口都会被调一次（14 个），换主题时还会再来一轮。
    /// </summary>
    private static bool _titleBarFailureLogged;

    /// <summary>
    /// 读一次系统偏好、按「跟随系统」画出来，并挂上「系统换了主题」的回调。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 放在<b>所有窗口之前</b>调用（<c>App.OnStartup</c> 的第三行）：
    /// <c>DynamicResource</c> 其实允许之后再换，但先换掉能少闪一下亮色。
    /// </para>
    /// <para>
    /// ⚠️ <b>这里还读不到设置</b>（`AppHost` 还没构造），所以先按「跟随系统」画 ——
    /// 万一用户固定了另一档，真正的那一档由 <c>AppHost</c> 读完设置后调的
    /// <see cref="Apply"/> 补上。代价是那一下切换看得见；收益是启动窗
    /// **至少是其中一种对的颜色**，而不是先亮后亮再变暗。
    /// </para>
    /// </remarks>
    public static void Start(IAppLogger? logger = null)
    {
        var (dark, failure) = ReadSystemPreference();

        _logger = logger;
        _mode = AppThemeMode.FollowSystem;
        SetDark(dark);

        if (failure is null)
        {
            logger?.Log(
                LogLevel.Info, "主题",
                $"跟随系统主题：{(dark ? "暗色" : "亮色")}。");
        }
        else
        {
            // ⚠️ 读不到就按**亮色**走，并且要说出来 —— 闷着的话，
            // 用户报「我系统是暗色它却是亮的」时没有任何可查的。
            // （这个值不在，只是老系统或组策略删了它，算正常，走上面那支 Info。）
            logger?.Log(LogLevel.Warn, "主题", $"读不到系统主题设置，这次按亮色走：{failure}");
        }

        if (_following)
        {
            return;
        }

        _following = true;

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // ⚠️ 标题栏是 DWM 画的，**不归 WPF 管也不跟着我们那份覆盖字典走**：
        // 实测（2026-10-07）在 `AppsUseLightTheme = 0` 下窗口标题栏仍是白的。
        // 得每个窗口显式告诉 DWM 一句。
        //
        // ⚠️ **挂钩点是承重的，而「越早越好」不够——`Loaded` 就已经太晚了。**
        // 机制：**那个属性只在标题栏那张面被建出来的时候被读一次**，之后写
        // 什么都没用。于是「窗口建出来时还不是深色、之后才切深色」这条路上，
        // 标题栏会留在错的那一色上 —— 挂 `Loaded` 量到的就是**白底 + 一块
        // 纯黑矩形**（正好围着图标与标题，右上三颗钮白字压白底）。
        //
        // ⚠️ 顺带记一笔 2026-10-15 需求方报的「浅色改深色时『设置』两个字有黑框」：
        // **他说的那个黑框是根上的问题，不是 Win10 深色标题栏的样子** ——
        // 标题栏那张面在**还亮着的时候**就建出来了。他那两张抓图量下来：
        // 主窗（启动时已经是深色，面建得对）标题栏是**整条 `#000000`**；
        // 而**设置窗**（跟着主窗一起、在亮色下建的面）是「白底 + 一块纯黑矩形」，
        // 正好围着图标与标题 —— 就是上面 `Loaded` 那条路上量到的形态。
        // 所以**不是两个缺陷**，本文件下面那套「换主题就把面重建一遍」正是冲它去的。
        //
        // ⭐ 2026-10-15 实测复现 + 复验（dev 构建：亮色启动 → 设置窗选「深色」→ 点「应用」）：
        // 主窗标题栏纯白 **39022 px → 0**；设置窗标题栏纯白 **3524 px → 64**
        // （剩下那 64 是图标自己的笔画）、纯黑 288 → 3748。**白条与黑框都没了。**
        //
        // ⚠️ **但「标题栏跟窗口同色」做不到**：实测 `DwmSetWindowAttribute` 的
        // 34/35/36（边框色 / 标题栏色 / 标题栏文字色）在本机（Win10 19045）
        // **一律返回 `E_INVALIDARG`（0x80070057）**、抓图逐像素不变 ——
        // 那几支只有 Win11 有。Win10 的深色标题栏恒是 `#000000`（活跃），
        // 跟窗口的 `#0F172A` / `#1E293B` 差着一档，看着仍像一条黑边。
        // 想同色就只能自己画标题栏 —— **2026-10-15 需求方裁定「不改，保持系统的」，别再提。**
        //
        // 每条都有抓图，试过的错路一并记在这儿，别再走：
        //   1. 真屏 BitBlt 与 PrintWindow 抓到的是同一张，不是抓图工具的锅；
        //   2. 最小化再还原 ⇒ 整条变成正确的全黑 ⇒ **那张面会被重建**，
        //      重建时读到的才是属性的当前值；
        //   3. 已经排除掉、别再试：`SetWindowPos(SWP_FRAMECHANGED)`（量过，
        //      纹丝不动）、`RedrawWindow(RDW_FRAME|RDW_UPDATENOW)`（同上）、
        //      把它点成前台（前后抓图逐字节相同）、以及**直接对已经开着的窗口
        //      句柄写 0 再写 1** —— 最后这条拿 pwsh 直接调 dwmapi 做的，
        //      两张抓图**逐像素完全相同**，属性根本没被读第二次；
        //   4. **`Loaded` 也不够早**：实测那时窗口**已经在屏上了**
        //      （`IsWindowVisible` = true），而 `SizeChanged` 那一刻还是 false
        //      —— 同为「备份主机」那扇窗，挂在 `SizeChanged` 上写出来的
        //      标题栏是**整条 `#000000`、38194 个像素**（挂 `Loaded` 上就是黑块）。
        //      所以挂 `SizeChanged`，它比 `Loaded` 早、且在 `ShowWindow` 之前。
        //
        // ⚠️ 为什么不挂 `SourceInitialized`（它其实更早）：**它是 CLR 事件、
        // 不是路由事件**，`EventManager.RegisterClassHandler` 挂不上
        // （编译直接报 CS0117）；而窗口有 16 处 `new` / 13 个窗口类，
        // 逐个手挂必然会漏一个，漏掉的那个就是一条白标题栏。
        // 也想过盯 `Application.Windows` 的集合变动 —— 走不通，
        // `WindowCollection` 是密封类、**不实现 `INotifyCollectionChanged`**
        // （这条是编译器挡的，不是猜的）。
        //
        // ⚠️ 窗口改尺寸会再走到这儿，多写几次同样的值 —— 无所谓，实测过：
        // 值没变时那句调用的效果是零（连重画都不会发生）。加个「写过没有」
        // 的标记反而多一份要维护的状态。
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.SizeChangedEvent,
            new SizeChangedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));
    }

    /// <summary>
    /// 按设置里那一档把界面换成对应颜色。<b>幂等</b>。
    /// </summary>
    /// <param name="mode">要生效的那一档（<see cref="AppSettings.ThemeMode"/>）。</param>
    /// <param name="logger">只有第一次传得上用场（见 <see cref="_logger"/> 那句）。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ 两个调用处都在 <c>AppHost</c>：读完设置那一次、每次存完设置那一次 ——
    /// 放在这里而不是界面上，是为了让**每一条存设置的路径**（设置窗、向导、
    /// 用途切换）都算数，而不是只让设置窗那一页算数。
    /// </para>
    /// <para>
    /// ⚠️ 只在**界面真的换了色**的时候记一句（判据就是 <see cref="SetDark"/> 的返回值，
    /// 与系统那条路同一条）。所以「固定深色」当选时按保存**不记**（什么都没发生），
    /// 同一档位重复保存也不记 —— 不用另加条件。
    /// 「设置里那一档从甲变成乙」是另一件事，那句由 <c>SettingsStore.DescribeChanges</c> 记。
    /// </para>
    /// </remarks>
    public static void Apply(AppThemeMode mode, IAppLogger? logger = null)
    {
        _logger ??= logger;
        _mode = mode;

        var dark = mode switch
        {
            // ⚠️ 固定那两档**不读注册表** —— 这正是它们存在的意义。
            AppThemeMode.Light => false,
            AppThemeMode.Dark => true,

            // 跟随系统（以及今后万一加了别的值，取保守的那一头）
            _ => ReadSystemPreference().Dark,
        };

        if (SetDark(dark))
        {
            _logger?.Log(
                LogLevel.Info, "主题",
                $"外观主题改成{AppPreferences.Describe(mode)}，界面切到{(dark ? "暗色" : "亮色")}。");
        }
    }

    /// <summary>
    /// Windows 现在是不是深色（<c>AppsUseLightTheme</c>；读不到算浅色）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 给设置页那句「跟随系统，现在是深色」用的。**不缓存** ——
    /// 它只在用户点开那一页、换那一档时被问一次，而缓存就意味着可能报一个
    /// 已经过时的现状（那正是那一句话必须避免的）。
    /// </remarks>
    public static bool SystemPrefersDark() => ReadSystemPreference().Dark;

    /// <summary>
    /// 系统说换了主题。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这个回调在别的线程上</b>（SystemEvents 自己那条），所以动资源之前
    /// 必须丢回 UI 线程。<see cref="UserPreferenceChangedEventArgs.Category"/> 会收到
    /// 一大堆类别（字体平滑、鼠标……），只有 <c>General</c> 是主题这一类 ——
    /// 不过滤的话每次系统偏好变动都会白跑一遍。
    /// <para>
    /// ⚠️ <b>而且只在「跟随系统」那一档才理它</b>（2026-10-08）：用户把外观固定成
    /// 浅色/深色之后，系统怎么变都不该动他的界面 —— 但系统照旧会发这条消息，
    /// 不挡的话「改 Windows 主题」会把他固定好的界面掀掉，而那看起来像设置没生效。
    /// </para>
    /// </remarks>
    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not UserPreferenceCategory.General
            || _mode is not AppThemeMode.FollowSystem)
        {
            return;
        }

        var dark = ReadSystemPreference().Dark;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            // ⚠️ 只在**真的换了**的时候记（`SetDark` 幂等，返回有没有动过）：
            // `General` 这一类下面还挂着字体大小、鼠标设置等等，每条都记的话
            // 日志会被一句「主题没变」刷满 —— 而**被刷满的日志等于没有日志**。
            if (SetDark(dark))
            {
                _logger?.Log(LogLevel.Info, "主题", $"系统换了主题，界面切到{(dark ? "暗色" : "亮色")}。");
            }
        });
    }

    /// <summary>
    /// 合并（或撤掉）那份暗色覆盖字典。<b>幂等</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 覆盖字典<b>加在最后</b>：合并字典是倒着找的，最后那份赢。
    /// 别去改 <c>Theme.xaml</c> 那份 —— 它是 <c>Source</c> 装的、只读。
    /// </para>
    /// <para>
    /// ⚠️ <b>取颜色别用 <c>FindResource</c>。</b> 它是**当场把对象给你**，
    /// 不是 <c>DynamicResource</c> 那种订阅：拿到的是一支**冻结的**笔刷，
    /// 赋给 DP 之后就跟这份字典脱钩了 —— 换主题换不掉它，
    /// 而且没有任何东西会喊。C# 里要取主题色，一律走
    /// <c>element.SetResourceReference(dp, "Key")</c> 或
    /// <c>new DynamicResourceExtension("Key").ProvideValue(...)</c>。
    /// ⚠️ 它**只对已经进了可视树**的元素重解析：挂在一棵没上屏的树上的元素
    /// 不会收到字典变动（2026-10-07 用真 `Window` 正反各量过一次）。本仓的用法全在窗口里，够。
    /// </para>
    /// <para>
    /// ⚠️ 上面那句「重解析」是**量过的**，不是照文档抄的（2026-10-07，同一台机器上）：
    /// 同一个键，`SetResourceReference` 那支 <c>#FF00FF00</c> → <c>#FFFF00FF</c>；
    /// `FindResource` 取到再赋给 DP 的那支，字典换完**仍是** <c>#FF00FF00</c>。
    /// </para>
    /// <para>
    /// 这条**是修出来的**：2026-10-07 之前有九格底色/计数色（<c>MultiViewWindow.Cell</c>
    /// 的九个 <c>readonly Brush</c> 字段）、<c>WizardWindow</c> 的四支、
    /// <c>SearchWindow.DayMarkConverter</c>、以及 <c>MainWindow</c> /
    /// <c>ImportWindow</c> / <c>SettingsWindow</c> 里几处状态字色都这么写。
    /// 症状是「开机就是暗色没事，**开着程序去改 Windows 主题**时那几处停在亮色」
    /// （那些窗口是之后才构造的，所以开机路径看着是对的 —— 这正是它难被发现的原因）。
    /// 现在全仓 C# 里已经没有一处那么取了。
    /// </para>
    /// </remarks>
    /// <returns>这次真的改动了没有（调用方靠它决定要不要记一条）。</returns>
    private static bool SetDark(bool dark)
    {
        var resources = Application.Current?.Resources;

        if (resources is null || dark == (_darkOverlay is not null))
        {
            return false;
        }

        if (dark)
        {
            var overlay = new ResourceDictionary();

            foreach (var (key, hex) in ThemePalette.Dark)
            {
                if (!resources.Contains(key))
                {
                    // ThemePaletteTests 钉着「一个键都不能少」，正常运行到不了这儿。
                    // 真到了也**不改也不喊**：宁可这个键保持亮色值，也不能让改主题把程序弄崩。
                    continue;
                }

                var color = ToColor(hex);

                // ⚠️ 跟着**原资源本身的类型**走，不是跟着键名走：
                // `PageBackgroundColor` 与 `ShadowColor` 是 <Color>（`DropShadowEffect.Color`
                // 要的就是它，塞笔刷进去会在运行时抛），其余 23 个是 <SolidColorBrush>。
                // 这个判据是量出来的，不是列名单 —— 名单会漂。
                overlay[key] = resources[key] is Color ? color : new SolidColorBrush(color);
            }

            resources.MergedDictionaries.Add(overlay);
            _darkOverlay = overlay;
        }
        else
        {
            resources.MergedDictionaries.Remove(_darkOverlay!);
            _darkOverlay = null;
        }

        // 已经开着的那些窗口的标题栏也得跟着翻（`Start()` 里那个类处理器只管**之后**新建的）。
        // ⚠️ 放在两个分支**之外**：切回亮色时那句一样要跑，否则白字标题栏留在暗色上。
        if (Application.Current is { } app)
        {
            foreach (Window window in app.Windows)
            {
                RebuildTitleBar(window);
            }
        }

        return true;
    }

    /// <summary>
    /// 「主题换了」时给<b>已经开着</b>的窗口重做标题栏。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>光写属性是没用的</b>：实测（2026-10-15，拿 pwsh 直接调 dwmapi
    /// 对着一扇开着的窗口写 <c>0</c> 再写 <c>1</c>）—— 前后两张抓图
    /// <b>逐像素完全相同</b>，属性根本没被读第二次。
    /// <c>SetWindowPos(SWP_FRAMECHANGED)</c> 也一样，量过，纹丝不动。
    /// </para>
    /// <para>
    /// ⚠️ 唯一能把那张面重建的杠杆是<b>让窗口离开屏幕再回来</b>
    /// （对应人工「最小化再还原」）：量到 <c>SW_HIDE</c> + <c>SW_SHOW</c> 之后
    /// 整条标题栏的颜色就跟着属性走了（<c>#FFFFFF</c> / <c>#000000</c> 各一张，
    /// 都是**整条干净**、没有黑块）。
    /// 后面那个命令换成 <c>SW_SHOWNOACTIVATE</c> 一样管用（也量过：写完 `0`
    /// 再 hide + noactivate，整条变 `#FFFFFF`、37718 个像素），所以「哪个窗口
    /// 用哪个」可以只管前台，不影响这张面重不重建。
    /// </para>
    /// <para>
    /// ⚠️ 代价照实说：**这一下窗口会真的闪一次**。只有「用户改了外观那一档」
    /// 和「跟随系统时系统换了主题」两条路会走到这儿（都是罕见动作、而且是他自己
    /// 刚点出来的），换来的是标题栏不留在错颜色上。除此之外没有别的办法——
    /// 上面两条不闪的路都实测过、都没用。
    /// </para>
    /// <para>
    /// ⚠️ 用 Win32 的 <c>ShowWindow</c> 而不是 WPF 的 <c>Window.Hide/Show</c>：
    /// <c>ShowDialog</c> 开出来的窗口上再调 WPF 的 <c>Show()</c> 会抛
    /// （`_showingAsDialog`），而模态窗口恰恰是最需要跟着换色的那类。
    /// </para>
    /// </remarks>
    private static void RebuildTitleBar(Window window)
    {
        ApplyTitleBar(window);

        // 还没显示出来的窗口不用管：它之后 `ShowWindow` 的那一刻会自己读到新值。
        //
        // ⚠️ **最小化的窗口也要跳过**，这一条不是省事、是安全：`SW_SHOW` 会把窗口
        // 显示出来，对着一扇缩在任务栏里的窗口做一下，等于把它弹出来。而它本来
        // 也不需要这一下 —— 从最小化还原本身就是一次「离开屏幕再回来」，
        // 那张面会在那一刻重建、读到的就是刚写进去的新值。
        if (!window.IsVisible || window.WindowState is WindowState.Minimized)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero)
        {
            return;
        }

        // ⚠️ 显示的那一下**别去抢前台**：这里是遍历所有开着的窗口，全用 `SW_SHOW`
        // 的话，最后遍历到的那扇会跳到最前面 —— 用户只是在设置里换了一档颜色，
        // 别的窗口不该因此翻上来。只有**本来就是前台**的那扇用 `SW_SHOW` 把焦点
        // 拿回来（用户刚点的那一下不该被顶掉），其余用 `SW_SHOWNOACTIVATE`。
        // ⚠️ 这句要在 `SW_HIDE` **之前**问：隐藏之后它就不是前台了。
        var command = GetForegroundWindow() == handle ? SwShow : SwShowNoActivate;

        _ = ShowWindow(handle, SwHide);
        _ = ShowWindow(handle, command);
    }

    /// <summary>
    /// 让 Windows 把<b>标题栏</b>也画成深色。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>不调用它，标题栏在暗色下就是白的</b> —— 它是 DWM 画的，既不归 WPF 也不看
    /// 我们那份覆盖字典（实测：`AppsUseLightTheme = 0` 时标题栏仍白）。
    /// </para>
    /// <para>
    /// 属性号有两代：<c>20</c> 是 Windows 10 20H1（build 19041）起，
    /// 在那之前是 <c>19</c>。先按新的调，失败了再按老的试 —— 一个 <c>int</c> 的差别，
    /// 但不试的话在这个世纪初的系统上就是一条白边。
    /// </para>
    /// <para>
    /// ⚠️ 它**不许把启动弄崩**：这是一句装饰性的调用，调用点又在窗口刚建出 HWND 那一刻，
    /// 抛出去就是「窗口刚画出来就没了」。
    /// </para>
    /// </remarks>
    private static void ApplyTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;

            if (handle == IntPtr.Zero)
            {
                // 还没建好 HWND。`SizeChanged` 那条路上不该出现（那时窗口已经排过版了），
                // 但 `RebuildTitleBar` 那条路是在任意时刻遍历窗口的 —— 别硬来。
                return;
            }

            var dark = _darkOverlay is not null ? 1 : 0;

            Write(handle, dark);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            // ⚠️ 这里**不能**只是闷掉（`AGENTS.md` §6.1：「`catch` 块不许静默吞掉」）。
            // 但也不能每次都记 —— 每个窗口一条、换一次主题再一轮，那是刷屏。
            // 所以：这次调用当它不存在（界面其余部分照常），只留**第一条**痕。
            if (!_titleBarFailureLogged)
            {
                _titleBarFailureLogged = true;
                _logger?.Log(
                    LogLevel.Warn, "主题",
                    $"标题栏没切成深色（dwmapi 那句调用失败了），界面其余部分是好的：{ex.Message}");
            }
        }
    }

    /// <summary>让窗口离开屏幕。</summary>
    private const int SwHide = 0;

    /// <summary>再让它回来（顺带拿回前台，用户刚点的那一下不该被别的窗口顶掉）。</summary>
    private const int SwShow = 5;

    /// <summary>回来，但**不碰前台** —— 给那些顺带被重做的窗口用。</summary>
    private const int SwShowNoActivate = 4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>把「这条标题栏是深色的吗」写给 DWM（两代属性号各试一次）。</summary>
    private static void Write(IntPtr handle, int value)
    {
        var v = value;

        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref v, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref v, sizeof(int));
        }
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Windows 说没说「应用用深色」，以及读的时候有没有出错。</summary>
    private static (bool Dark, string? Failure) ReadSystemPreference()
    {
        try
        {
            var raw = Registry.GetValue(PersonalizeKeyPath, AppsUseLightThemeValue, null);

            // ⚠️ 只有 0 才算深色，别的（值不在、被改成 2、读出来是字符串）
            // 一律当浅色 —— 判据在 ThemePalette.IsDark 那一处。
            return (ThemePalette.IsDark(raw as int?), null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// <c>#RRGGBB</c> / <c>#AARRGGBB</c> 转成 WPF 的颜色。
    /// </summary>
    /// <remarks>
    /// 不用 <c>ColorConverter</c>：它连 <c>#RGB</c>、颜色名都收，而本仓的
    /// <c>ThemePalette</c> 只写这两种形状 —— 手写这两支能把「写错了」变成
    /// 一个**会抛的解析失败**，而不是被宽容地吃掉。
    /// </remarks>
    private static Color ToColor(string hex)
    {
        var digits = hex.StartsWith('#') ? hex[1..] : hex;
        var value = uint.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return digits.Length == 8
            ? Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : Color.FromArgb(0xFF, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}
