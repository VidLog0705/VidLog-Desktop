using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

using Microsoft.Win32;

using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Theme;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的同名类型都带进来。这里钉死成要用的那些。
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 跟随系统的亮/暗主题（改造清单第 5 批）。
/// </summary>
/// <remarks>
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
/// （那边有测试钉着「一个键都不能少、色值是量出来的」）。这里只负责三件事：
/// 读注册表、合并/撤掉那份覆盖字典、听系统换主题。
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
    /// 读一次系统偏好、应用一遍，并挂上「系统换了主题」的回调。
    /// </summary>
    /// <remarks>
    /// ⚠️ 放在<b>所有窗口之前</b>调用（<c>App.OnStartup</c> 的第三行）：
    /// <c>DynamicResource</c> 其实允许之后再换，但先换掉能少闪一下亮色。
    /// </remarks>
    public static void Start(IAppLogger? logger = null)
    {
        var (dark, failure) = ReadSystemPreference();

        _logger = logger;
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
        // 得每个窗口显式告诉 DWM 一句。挂 `Loaded` 的类处理器 = 一处盖住全部 14 个窗口，
        // 不用挨个改窗口类（漏掉一个就是一条白边）。
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));
    }

    /// <summary>
    /// 系统说换了主题。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这个回调在别的线程上</b>（SystemEvents 自己那条），所以动资源之前
    /// 必须丢回 UI 线程。<see cref="UserPreferenceChangedEventArgs.Category"/> 会收到
    /// 一大堆类别（字体平滑、鼠标……），只有 <c>General</c> 是主题这一类 ——
    /// 不过滤的话每次系统偏好变动都会白跑一遍。
    /// </remarks>
    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not UserPreferenceCategory.General)
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

        // 已经开着的那些窗口的标题栏也得跟着翻（`Loaded` 类处理器只管**之后**新建的）。
        // ⚠️ 放在两个分支**之外**：切回亮色时那句一样要跑，否则白字标题栏留在暗色上。
        if (Application.Current is { } app)
        {
            foreach (Window window in app.Windows)
            {
                ApplyTitleBar(window);
            }
        }

        return true;
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
    /// ⚠️ 它**不许把启动弄崩**：这是一句装饰性的调用，调用点又在 `Loaded` 上，
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
                // 还没建好 HWND（`Loaded` 之外的地方会这样）。别硬来。
                return;
            }

            var dark = _darkOverlay is not null ? 1 : 0;

            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
            }
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
