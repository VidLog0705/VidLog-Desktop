using System.Globalization;
using System.Text.RegularExpressions;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 多画面那面墙的**三种空格子各长什么样**（T10 + T9，需求方 2026-10-07 定的）。
/// </summary>
/// <remarks>
/// <para>
/// 三种空法：<b>未接入</b>（压根没这么多台机位）· <b>已关闭</b>（用户自己按了眼睛）·
/// <b>有机位没画面</b>。前两种原来**长得一模一样**（同一块浅底、同一支灰字），
/// 区别只在字 —— 需求方当场点了名（「要一眼分得出」，2026-10-07）。
/// </para>
/// <para>
/// ⚠️ <b>这一格里最坏的一种错是「改着改着又变回同一个键」</b>：那种改动
/// 编译得过、跑得起来、屏幕上看起来也「挺正常」，而且**没有任何测试会喊** ——
/// 除非有人真的一格一格去比。所以钉在这儿的是三样：
/// </para>
/// <list type="number">
/// <item>两种空位的底色**不是同一个画刷**（那句话的原文）。</item>
/// <item>那个**叉掉的眼睛**只在「已关闭」那格露面 —— 它才是真正一眼看得见的那一笔
/// （两块浅底差的主要是色相，见下）。</item>
/// <item>「已关闭」那格的字**在它自己的底上够看得清**（WCAG AA 4.5:1）。
/// 这一条挡的是那种「顺手把字也换成琥珀、好更醒目」的改法 ——
/// 量过，琥珀压在淡琥珀上只有 <b>2.07:1</b>。</item>
/// </list>
/// <para>
/// ⚠️ <b>为什么只能看源码文本</b>：App 层没有测试工程（与 <c>DesktopServicesTests</c>
/// 里那几条同一条理由）。天花板也一样：这些是**字符串在不在**，
/// 挡不住「判断写反了」—— 那种由 <c>LiveWallTests</c> 里真跑对账的那几条挡。
/// </para>
/// </remarks>
public class MultiViewWindowCellTests
{
    /// <summary>AA 正文档那条线（4.5:1）。</summary>
    private const double AaNormalText = 4.5;

    [Fact]
    public void 两种空位不许用同一块浅底()
    {
        var window = WindowSource();
        var theme = ThemeSource();

        var offKey = KeyConstant(window, "OffSlotKey");
        var slotKey = KeyConstant(window, "SlotKey");

        // ⚠️ 这一条就是需求方那句话的原文：「同一块浅底，区别只在字」。
        Assert.NotEqual(slotKey, offKey);

        var off = Brush(theme, offKey);
        var slot = Brush(theme, slotKey);

        // ⚠️ 两个键对了、颜色写重了，照样是同一块底 —— 所以要**量到色值**上。
        Assert.NotEqual(slot, off);

        // ⚠️ 差的必须是**色相**（暖 ↔ 冷），不是深浅：两块浅底再怎么调也调不出
        // 大的明度差（满打满算 1.0x —— 见下面那条）。所以这里钉的就是这一件事。
        Assert.True(IsWarm(off), $"「已关闭」那格的底色 {offKey}（{off}）不是暖色 —— 与「未接入」的冷灰分不开。");
        Assert.True(IsCool(slot), $"「未接入」那格的底色 {slotKey}（{slot}）不是冷色。");
    }

    [Fact]
    public void 那一格的字在自己那块底上够看得清()
    {
        var window = WindowSource();
        var theme = ThemeSource();

        var ink = Brush(theme, KeyConstant(window, "MutedKey"));
        var off = Brush(theme, KeyConstant(window, "OffSlotKey"));

        var ratio = Contrast(ink, off);

        // ⚠️ 这条是给「顺手把字也换醒目的颜色」那种改法准备的：`Warning`（#F59E0B）
        // 压在 `WarningSurface`（#FFFBEB）上只有 2.07:1，远不够 —— 但项目里
        // 到处都用那一对，所以「颜色差走底子、不走字」这件事必须写死在这儿。
        Assert.True(
            ratio >= AaNormalText,
            $"「已关闭」那格的字色（{ink}）在它自己的底色（{off}）上只有 {ratio:F2}:1，"
                + $"低于 AA 正文档的 {AaNormalText} —— 让字更醒目不该拿可读性换。");
    }

    [Fact]
    public void 叉掉的眼睛只在真关掉的那一格里露面()
    {
        var window = WindowSource();

        // 哨兵：扫到的是真文件（读错目录时下面几条会假绿）。
        Assert.Contains("MultiViewWindow", window, StringComparison.Ordinal);

        // ⚠️ 空位那块浅底之间差的只是色相，明度几乎一样（淡琥珀 #FFFBEB 压
        // 冷灰 #F8FAFC 只有 1.01:1）—— 真正「一眼」的是这一笔：一块 34 像素、
        // 带斜杠的眼睛。它要是常驻（或者反过来从不露面），这一格的脸色系统就塌了。
        Assert.Contains(
            "_offMark.Visibility = turnedOff ? Visibility.Visible : Visibility.Collapsed;",
            window,
            StringComparison.Ordinal);

        // ⚠️ 按钮上那颗眼睛关掉时**得换形状**（多一道斜杠），不是只换颜色：
        // 两个状态换成同一个字形的话，用户看着一颗好好的眼睛、点下去没反应
        //（其实是有反应的 —— 画面停了）。
        Assert.Contains("_eyeShape.Data = EyeShape(_off);", window, StringComparison.Ordinal);

        // 大那颗与按钮上那颗共用同一份几何（抄第二份的话，下次改形状只会改到一处）。
        Assert.Contains("_offMark = IconBox(", window, StringComparison.Ordinal);
        Assert.Contains("EyeSlash", window, StringComparison.Ordinal);
    }

    [Fact]
    public void 那颗转九十度的字不许靠继承拿墨色()
    {
        var window = WindowSource();
        var theme = ThemeSource();

        Assert.Contains("MultiViewWindow", window, StringComparison.Ordinal);

        // ⚠️ 它原来是**一个字都没给**的：那颗按钮只有 `Content = "⟳"`，墨色靠 WPF
        // 继承拿（默认前景＝黑），而它**只在近黑底上露面** ⇒ 黑压黑 **1.18:1**，
        // 等于没有这颗按钮（2026-10-07 核 diff 时量出来的，需求方当场点了名：
        // 「要让用户明显看到这个按钮」）。这一条钉的是**给了、而且跟着底走** ——
        // 写死一个颜色（`Brushes.White` 或 `Brushes.Black`）它照样红。
        Assert.Contains(
            "_rotate.Foreground = empty ? _textMain : Brushes.White;",
            window,
            StringComparison.Ordinal);

        var dark = Brush(window, theme, "_videoBg");
        var light = Brush(window, theme, "_slotBg");
        var inkOnLight = Brush(window, theme, "_textMain");

        // 「明显看到」那句量化下来就是 AA 正文档这条线，两种底各量一次。
        var onDark = Contrast("#FFFFFFFF", dark);

        Assert.True(
            onDark >= AaNormalText,
            $"深底（{dark}）上那颗 ⟳ 用白色只有 {onDark:F2}:1，低于 {AaNormalText}。");

        var onLight = Contrast(inkOnLight, light);

        Assert.True(
            onLight >= AaNormalText,
            $"浅底（{light}）上那颗 ⟳ 用 {inkOnLight} 只有 {onLight:F2}:1，低于 {AaNormalText}。");
    }

    private const string AppFolder = "VidLog.Desktop.App";

    /// <summary>读多画面那个窗口的源码（整行注释已剥掉，与别的绊线同一个读法）。</summary>
    private static string WindowSource() =>
        string.Join(
            '\n',
            Directory.EnumerateFiles(
                    Path.Combine(RepoRoot(), "src", AppFolder), "MultiViewWindow*.cs")
                .OrderBy(one => one, StringComparer.Ordinal)
                .SelectMany(one => File.ReadAllLines(one)
                    .Where(line => !line.TrimStart().StartsWith("//"))));

    private static string ThemeSource() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", AppFolder, "Theme.xaml"));

    /// <summary>把一个 <c>private const string XxxKey = "…";</c> 的值量出来。</summary>
    private static string KeyConstant(string source, string name)
    {
        // ⚠️ 前面那个 `(?<!\w)`：`SlotKey` 是 `OffSlotKey` 的尾巴，不挡住的话
        // 量 `SlotKey` 会量到 `OffSlotKey` 的值上去 —— 而两个键恰好相等正是这条要抓的。
        var found = Regex.Match(source, $@"(?<!\w){Regex.Escape(name)}\s*=\s*""([^""]+)""");

        Assert.True(found.Success, $"MultiViewWindow 里没量到 {name}。");

        return found.Groups[1].Value;
    }

    /// <summary>格子上那个字段（<c>_videoBg</c> 这种）取的是哪个画刷、色值多少。</summary>
    private static string Brush(string window, string theme, string field)
    {
        var found = Regex.Match(
            window,
            $@"{Regex.Escape(field)}\s*=\s*\(Brush\)[A-Za-z_]\w*\.FindResource\(([^)]+)\)");

        Assert.True(found.Success, $"MultiViewWindow 里没量到 {field} 用的是哪个画刷。");

        var argument = found.Groups[1].Value.Trim();

        // ⚠️ 这里两种写法都得认：早先那几笔写的是**画刷键的字面量**（`"VideoBackground"`），
        // 后加的两笔写的是**画刷键的常量**（`SlotKey` / `MutedKey`）—— 常量那支要先解一层，
        // 不认的话量出来的是常量名，去 Theme.xaml 里当然找不到。
        var key = argument.StartsWith('"') ? argument.Trim('"') : KeyConstant(window, argument);

        return Brush(theme, key);
    }

    /// <summary>那个画刷在 Theme.xaml 里的颜色（<c>#AARRGGBB</c>）。</summary>
    private static string Brush(string theme, string key)
    {
        var found = Regex.Match(
            theme, $@"x:Key=""{Regex.Escape(key)}""\s+Color=""#([0-9A-Fa-f]{{8}})""");

        Assert.True(found.Success, $"Theme.xaml 里没有 {key} 这个画刷。");

        return found.Groups[1].Value;
    }

    /// <summary>暖色（琥珀那一侧）：红 ≥ 绿 &gt; 蓝。</summary>
    private static bool IsWarm(string argb) => Rgb(argb) is var (r, g, b) && r >= g && g > b;

    /// <summary>冷色（slate 那一侧）：蓝 ≥ 绿 ≥ 红。</summary>
    private static bool IsCool(string argb) => Rgb(argb) is var (r, g, b) && b >= g && g >= r;

    private static (int R, int G, int B) Rgb(string argb)
    {
        var rgb = argb[^6..];

        return (
            int.Parse(rgb[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(rgb[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(rgb[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>WCAG 的相对亮度。</summary>
    private static double Luminance(string argb)
    {
        var (r, g, b) = Rgb(argb);

        double Channel(int value)
        {
            var c = value / 255.0;

            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }

    /// <summary>WCAG 的对比度（亮度比，与谁深谁浅无关）。</summary>
    private static double Contrast(string argbA, string argbB)
    {
        var a = Luminance(argbA);
        var b = Luminance(argbB);

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>从测试程序集往上找到仓库根（含 <c>src</c> 与 <c>tests</c> 的那一层）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 往上找不到仓库根（含 src 与 tests 的目录）。");
    }
}
