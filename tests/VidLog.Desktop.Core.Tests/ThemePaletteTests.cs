using System.Globalization;
using System.Text.RegularExpressions;

using VidLog.Desktop.Core.Theme;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 暗色色值表（改造清单第 5 批）。
/// </summary>
/// <remarks>
/// <para>
/// 这一层守的是**改了也不会有人喊**的那几件事：少一个键（那个颜色在暗色下
/// 保持亮色值 —— 白底黑字漏在暗色界面上，正是清单里点名的 ZoneMinder 那个形状）、
/// 色值写坏（<c>AppTheme.ToColor</c> 会抛，而那是启动路径）、
/// 以及**对比度掉到看不清**（界面上只是「有点糊」，没有任何东西会红）。
/// </para>
/// <para>
/// ⚠️ <b>下面那些数是量出来的，不是拍的</b>：改任何一个色值都要重跑这一组，
/// 掉了就得给理由（或者改回去）。
/// </para>
/// </remarks>
public class ThemePaletteTests
{
    private static readonly IReadOnlyDictionary<string, string> Dark = ThemePalette.Dark;

    /// <summary><c>Theme.xaml</c> 里那些「颜色」键 → 亮色值。笔刷与裸 Color 都收。</summary>
    private static readonly IReadOnlyDictionary<string, string> LightColorKeys = ReadThemeXamlColors();

    // ─────────────────────────────────────────────
    // 键集：一个都不能少、也不能多
    // ─────────────────────────────────────────────

    [Fact]
    public void 暗色表的键与Theme_xaml的颜色键逐个对上()
    {
        var missing = LightColorKeys.Keys.Except(Dark.Keys, StringComparer.Ordinal).Order().ToList();
        var extra = Dark.Keys.Except(LightColorKeys.Keys, StringComparer.Ordinal).Order().ToList();

        // ⚠️ 两个方向都要报：少了 ⇒ 那个颜色在暗色下**保持亮色值**；
        // 多了 ⇒ 静默无效，没有任何东西会喊。
        Assert.True(
            missing.Count == 0,
            $"Theme.xaml 里这些键在 ThemePalette.Dark 里没有 ⇒ 暗色下它们会保持亮色值：{string.Join("、", missing)}");
        Assert.True(
            extra.Count == 0,
            $"ThemePalette.Dark 里这些键 Theme.xaml 里没有 ⇒ 改了也不会有任何效果：{string.Join("、", extra)}");
    }

    [Fact]
    public void 色值都是_RRGGBB_或_AARRGGBB()
    {
        foreach (var (_, hex) in Dark)
        {
            // 少一位、多一位、带 h 后缀（`#F1F5F9h`）都会在这儿红，
            // 而不是等到运行时在 AppTheme.ToColor 里抛在启动路径上。
            Assert.Matches(@"^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", hex);
        }
    }

    // ─────────────────────────────────────────────
    // DynamicResource 那几百处的代价
    // ─────────────────────────────────────────────

    [Fact]
    public void 每一处DynamicResource都找得到定义()
    {
        // ⚠️ 这条是「`StaticResource` → `DynamicResource`」那 268 处改动的**代价**。
        // 旧写法：键名打错 ⇒ 加载那一刻就抛（窗口直接打不开，看得见）。
        // 新写法：找不到就**静默不给那个属性赋值** —— 界面上只是「那块颜色不对」
        // 或者退回默认色，没有任何东西会喊，也没有任何窗口会打不开。
        //
        // 所以把旧写法自带的这层保险手工补回来，而且是**全量**补：
        // 每一处 `{DynamicResource X}` 都得在某个 XAML 里 `x:Key="X"` 过。
        var appRoot = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App");
        Assert.True(Directory.Exists(appRoot), $"找不到 {appRoot} —— 这条绊线的路径过期了");

        var defined = new HashSet<string>(StringComparer.Ordinal);
        var used = new List<(string Key, string Where)>();

        foreach (var file in XamlFiles(appRoot))
        {
            var text = File.ReadAllText(file);
            var where = Path.GetFileName(file);

            foreach (Match m in Regex.Matches(text, @"x:Key=""([^""]+)"""))
            {
                defined.Add(m.Groups[1].Value);
            }

            foreach (Match m in Regex.Matches(text, @"\{DynamicResource ([^}]+)\}"))
            {
                used.Add((m.Groups[1].Value.Trim(), where));
            }
        }

        // 前提检查：一条都没扫到说明这条绊线自己坏了（正则过期、路径漂了），
        // 那时候它就是个恒真的绿 —— 比没有还坏。
        // 门槛按**实测的下界**取（2026-10-07：64 个 x:Key、268 处引用），
        // 取得很松，只为了抓「一处都没扫到」，不是为了锁住数量。
        Assert.True(defined.Count >= 40, $"只扫到 {defined.Count} 个 x:Key，正则或路径大概过期了");
        Assert.True(used.Count >= 200, $"只扫到 {used.Count} 处 DynamicResource，正则或路径大概过期了");

        var dangling = used
            .Where(u => !defined.Contains(u.Key))
            .Select(u => $"{u.Where} 的 {{{u.Key}}}")
            .Distinct()
            .Order()
            .ToList();

        Assert.True(dangling.Count == 0, "这些 DynamicResource 谁都没定义（会静默不生效）：\n  " + string.Join("\n  ", dangling));
    }

    /// <summary><c>VidLog.Desktop.App</c> 下所有手写的 XAML（跳过生成物）。</summary>
    private static IEnumerable<string> XamlFiles(string root) =>
        Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    // ─────────────────────────────────────────────
    // 「这次拆分没改到亮色」
    // ─────────────────────────────────────────────

    [Fact]
    public void 亮色下Accent与AccentSolid同值_所以这次拆分一个像素都没改到亮色()
    {
        // ⚠️ 拆成两支的**唯一**目的是让暗色能取两个相反的值（见下一条）。
        // 亮色下两支同值 ⇒ 这次拆分明面上只多了一个键、渲染结果逐像素不变。
        // 这条一变红就说明亮色被顺手改了 —— 那是有意为之才行的，不是顺手的事。
        Assert.Equal(LightColorKeys["Accent"], LightColorKeys["AccentSolid"]);
    }

    // ─────────────────────────────────────────────
    // 为什么 Accent 必须拆成两支
    // ─────────────────────────────────────────────

    [Fact]
    public void Accent那两支必须分开_合回去两头必有一头不达标()
    {
        Assert.NotEqual(Dark["Accent"], Dark["AccentSolid"]);

        // 当前景（文字/图标/描边）压卡片：blue-400 够（5.75:1）。
        Assert.True(
            Contrast(Dark["Accent"], Dark["Surface"]) >= 4.5,
            $"Accent({Dark["Accent"]}) 压卡片只有 {Contrast(Dark["Accent"], Dark["Surface"]):F2}:1");

        // 而当底的那个 blue-600 压卡片只有 2.83:1 —— 连图形要的 3:1 都不到。
        // 所以它**不能**当前景。这条就是「拆两支」的论据本身，钉住它。
        Assert.True(
            Contrast(Dark["AccentSolid"], Dark["Surface"]) < 3.0,
            "AccentSolid 当前景居然够了 —— 那这个拆分的前提就不成立了，得重新看");
    }

    // ─────────────────────────────────────────────
    // 判据
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData(1, false)]    // 系统说浅色
    [InlineData(0, true)]     // 系统说深色
    [InlineData(null, false)] // 这个值不在（老系统 / 被组策略删了）⇒ 浅色
    [InlineData(2, false)]    // 不认识的取值 ⇒ 浅色
    [InlineData(-1, false)]   // 同上
    public void 只有零才算深色(int? registryValue, bool expected)
    {
        // ⚠️ 默认必须是浅色：这套界面是从需求方的亮色设计图量出来的，
        // 而「读不到系统偏好」不是「用户要深色」。
        Assert.Equal(expected, ThemePalette.IsDark(registryValue));
    }

    // ─────────────────────────────────────────────
    // 对比度：压字的几对
    // ─────────────────────────────────────────────

    [Fact]
    public void 暗色下压字的每一对都达标()
    {
        // 门槛取标准值（正文 4.5:1），不是取我量出来的那个数 ——
        // 这样一眼看得出「达没达标」，而具体数字在失败信息里。
        var pairs = new (string Label, string Fg, string Bg, double Min)[]
        {
            ("正文 / 卡片", Dark["TextPrimary"], Dark["Surface"], 4.5),
            ("次级字 / 卡片", Dark["TextSecondary"], Dark["Surface"], 4.5),
            ("正文 / 页底", Dark["TextPrimary"], Dark["PageBackground"], 4.5),
            ("次级字 / 页底", Dark["TextSecondary"], Dark["PageBackground"], 4.5),
            ("正文 / 侧栏", Dark["TextPrimary"], Dark["SidebarBackground"], 4.5),
            ("次级字 / 侧栏", Dark["TextSecondary"], Dark["SidebarBackground"], 4.5),

            // 强调色当前景（文字/图标/描边）—— 压卡片与压页底都要够。
            ("强调字 / 卡片", Dark["Accent"], Dark["Surface"], 4.5),
            ("强调字 / 页底", Dark["Accent"], Dark["PageBackground"], 4.5),

            // 白字压在实心底上（主按钮、开关轨道、TagPill）。
            ("白字 / 主按钮底", "#FFFFFF", Dark["AccentSolid"], 4.5),

            // 选中行（设置页选中的那一行、日历上「有录像的那天」）。
            ("强调字 / 选中底", Dark["Accent"], Dark["AccentWeak"], 4.5),
            ("正文 / 选中底", Dark["TextPrimary"], Dark["AccentWeak"], 4.5),
            ("次级字 / 选中底", Dark["TextSecondary"], Dark["AccentWeak"], 4.5),

            // 警示块。
            ("警示字 / 警示底", Dark["Warning"], Dark["WarningSurface"], 4.5),
            ("正文 / 警示底", Dark["TextPrimary"], Dark["WarningSurface"], 4.5),

            // ⚠️ 这颗**看着**像漏网之鱼，其实是「暗色下禁用态白字反而更清楚」那个
            // 悖论的正面：要求是「禁用态的字仍然读得出来」（10.36:1）。
            // 它比可用态（5.17:1）还显眼这件事，药方是给控件模板加 Opacity，
            // 会改到亮色，本批没动 —— 见 ThemePalette 那条注释。
            ("白字 / 禁用主按钮底", "#FFFFFF", Dark["AccentDisabled"], 4.5),
        };

        foreach (var (label, fg, bg, min) in pairs)
        {
            var ratio = Contrast(fg, bg);

            Assert.True(ratio >= min, $"{label}（{fg} 压 {bg}）只有 {ratio:F2}:1，要求 ≥ {min}:1");
        }
    }

    [Fact]
    public void 暗色下那两对明知差一点的不许更差()
    {
        // 这两对**都不达标**，是有意留着的（改不动：见 ThemePalette 各自的注释）。
        // 把它们钉在这儿，是为了「以后不许悄悄更差」—— 不是假装它们达标了。

        // ① 进度填充压进度轨道（`WizardProgress`）：blue-600 的亮度决定了轨道得
        //    接近纯黑才够 3:1，而那种黑会跟页底撞车（选中行直接消失）。
        //    量出来 2.84:1，blue-900 只有 2.00:1。
        var track = Contrast(Dark["AccentSolid"], Dark["AccentWeak"]);
        Assert.True(track >= 2.8, $"进度填充压轨道只剩 {track:F2}:1（当前 2.84）");

        // ② 白字压危险/成功色：亮色那边也一样不达标（3.76 / 2.54），
        //    所以不是暗色引入的，也不该在这一批里偷偷改亮色。
        Assert.True(Contrast("#FFFFFF", Dark["Danger"]) >= 3.7);
        Assert.True(Contrast("#FFFFFF", Dark["Success"]) >= 2.5);
    }

    // ─────────────────────────────────────────────
    // 关系（不是压字）：两块面之间看不看得出边界
    // ─────────────────────────────────────────────

    [Fact]
    public void 暗色下悬停面比卡片亮_方向不许反()
    {
        // ⚠️ 亮色里 SurfaceAlt（slate-100）比卡片**暗**一档，看着是「压下去」；
        // 暗色里若照抄更暗的，悬停就变成「越悬停越黑」—— 看不见。
        // 所以这一支的方向是**反的**，这条钉的就是那个反。
        var hover = Contrast(Dark["SurfaceAlt"], Dark["Surface"]);

        Assert.True(
            Luminance(Dark["SurfaceAlt"]) > Luminance(Dark["Surface"]),
            "SurfaceAlt 比卡片暗了 ⇒ 悬停会越悬越黑");
        Assert.True(hover >= 1.2, $"悬停面与卡片只差 {hover:F2}:1，看不出来");
    }

    [Fact]
    public void 暗色下这几对面不许撞车()
    {
        // ⚠️ 这几对**都不压在字上**，所以 WCAG 那套 4.5/3.0 的门槛对它们没意义
        // （它们本来就只差 1.1~1.2）。这里守的是另一件事：
        // **两块面同色 ⇒ 那块面整个消失**。界面上没有任何东西会喊。
        //
        // 这一条是证伪时补出来的：原先只有「侧栏 vs 页底」一对，而把
        // `AccentWeak` 换成页底色**一条都不会红**（它的对比度反而从 2.84 涨到 3.45，
        // 轨道那条门槛拦不住）—— 可那样一来设置页「选中的那一行」在页底上就看不见了。
        var pairs = new (string Label, string A, string B)[]
        {
            ("侧栏 / 页底", Dark["SidebarBackground"], Dark["PageBackground"]),
            ("卡片 / 页底", Dark["Surface"], Dark["PageBackground"]),
            ("选中行的底 / 页底", Dark["AccentWeak"], Dark["PageBackground"]),
        };

        foreach (var (label, a, b) in pairs)
        {
            Assert.NotEqual(a, b);

            var ratio = Contrast(a, b);
            Assert.True(ratio >= 1.10, $"{label} 只差 {ratio:F2}:1（{a} / {b}）—— 这块面会看不见");
        }
    }

    // ─────────────────────────────────────────────
    // 工具
    // ─────────────────────────────────────────────

    private static double Contrast(string fg, string bg)
    {
        var a = Luminance(fg);
        var b = Luminance(bg);

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>WCAG 相对亮度。<c>#AARRGGBB</c> 时忽略透明度那一截。</summary>
    private static double Luminance(string hex)
    {
        var digits = hex.TrimStart('#');

        if (digits.Length == 8)
        {
            digits = digits[2..];
        }

        var r = Channel(digits[0..2]);
        var g = Channel(digits[2..4]);
        var b = Channel(digits[4..6]);

        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);

        static double Channel(string pair)
        {
            var value = int.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>
    /// 把 <c>Theme.xaml</c> 里所有颜色资源解析成「键 → 亮色值」。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只认 <c>&lt;SolidColorBrush ... Color="…"/&gt;</c> 与
    /// <c>&lt;Color ...&gt;…&lt;/Color&gt;</c> 两种写法。仓里现在一律是单行内联值，
    /// 所以这两个正则够用；哪天有人改成 <c>{StaticResource …}</c> 别名，
    /// 这条会**解析不到那个键** ⇒ 键集那条立刻红，不会静默漏过去。
    /// </remarks>
    private static IReadOnlyDictionary<string, string> ReadThemeXamlColors()
    {
        var path = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App", "Theme.xaml");
        Assert.True(File.Exists(path), $"找不到 {path} —— 这条绊线的路径过期了");

        var brush = new Regex(
            @"<SolidColorBrush\s+x:Key=""(?<key>[^""]+)""\s+Color=""(?<value>#[0-9A-Fa-f]+)""",
            RegexOptions.Compiled);
        var bare = new Regex(
            @"<Color\s+x:Key=""(?<key>[^""]+)"">(?<value>#[0-9A-Fa-f]+)</Color>",
            RegexOptions.Compiled);

        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.ReadAllLines(path))
        {
            foreach (var match in brush.Matches(line).Cast<Match>().Concat(bare.Matches(line).Cast<Match>()))
            {
                found[match.Groups["key"].Value] = match.Groups["value"].Value;
            }
        }

        return found;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }
}
