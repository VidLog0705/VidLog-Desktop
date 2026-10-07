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
        // 门槛按**实测的下界**取（2026-10-07 复测：66 个 x:Key、268 处引用），
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

        // 2026-10-07 又拆了两支，同一条话对它们也成立 —— 亮色取的都是**原来的值**：
        //  `PrimaryDisabledInk` 亮色 = `#FFFFFF`，就是模板里原先写死的那个白；
        //  `ProgressFill` 亮色 = `#2563EB`，就是原来当填充用的 `AccentSolid`。
        // 这两条一变红就说明「顺手把亮色也改了」—— 那要有意为之，不是顺手的事。
        Assert.Equal("#FFFFFF", Rgb(LightColorKeys["PrimaryDisabledInk"]));
        Assert.Equal(LightColorKeys["AccentSolid"], LightColorKeys["ProgressFill"]);
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
        };

        // ⚠️ 这里**故意没有**「禁用主按钮上那行字」那一对：禁用态的墨色不需要
        // 达 4.5（WCAG 把禁用控件整条豁免掉了），它受另一条约束管 ——
        // **禁用不许比可用还显眼**，见 `禁用态不许比可用态还显眼`。

        foreach (var (label, fg, bg, min) in pairs)
        {
            var ratio = Contrast(fg, bg);

            Assert.True(ratio >= min, $"{label}（{fg} 压 {bg}）只有 {ratio:F2}:1，要求 ≥ {min}:1");
        }
    }

    [Fact]
    public void 进度填充压轨道每一对都达标()
    {
        // ⚠️ 2026-10-07 之前这一对是**明知差一点**的（填充压向导轨道 2.84:1，
        // 标准要 3.0）：当时填充与「选中底」共用 `AccentSolid`，而 blue-600 的
        // 亮度决定了轨道得接近纯黑才够 —— 那种黑会跟页底撞车（选中行直接消失）。
        // 拆出 `ProgressFill` 之后四条轨道逐条达标，这条从「钉住那个差值」
        // 变成「钉住达标」。
        //
        // ⚠️ 四条进度条的**轨道各用各的键**（见 `Theme.xaml` 各自那一处），
        // 所以四对都得钉 —— 只钉一对的话，其余三条里的任何一条掉下去都没人喊。
        // 亮色那支与 `AccentSolid` 同值，所以下面也把亮色的四条一起钉上。
        var tracks = new (string Label, string Key)[]
        {
            ("向导进度条", "AccentWeak"),
            ("容量条", "BorderStrong"),
            ("数据窗进度条", "SurfaceAlt"),
            ("启动窗那条", "SurfaceMuted"),
        };

        foreach (var (label, key) in tracks)
        {
            var dark = Contrast(Dark["ProgressFill"], Dark[key]);
            Assert.True(dark >= 3.0, $"暗色：进度填充压{label}（{Dark[key]}）只有 {dark:F2}:1，要求 ≥ 3.0:1");

            var light = Contrast(LightColorKeys["ProgressFill"], LightColorKeys[key]);
            Assert.True(light >= 3.0, $"亮色：进度填充压{label}（{LightColorKeys[key]}）只有 {light:F2}:1，要求 ≥ 3.0:1");
        }
    }

    [Fact]
    public void 禁用态不许比可用态还显眼()
    {
        // ⚠️ 这条是 2026-10-07 抓出来的：模板里禁用态那行字**写死 `White`**，
        // 亮色下压 `AccentDisabled`（blue-500 兑白）只有 2.08:1，看着是「褪色」，
        // 对；暗色下压 `AccentDisabled`（往深底兑 ⇒ blue-900）是 **10.36:1**，
        // 比可用态（5.17:1）**还高** —— 灰掉的那个按钮成了全界面最清楚的一颗。
        //
        // 药方**不是**给控件模板加 `Opacity`（那会改到亮色：2.08 会掉到 1.37），
        // 而是把墨色拆成一支：亮色取 `#FFFFFF`（= 原来的值，亮色一个像素不变），
        // 暗色取 `TextDisabled` 那一档。
        //
        // 下面同时钉两头：**不许更显眼**（主约束），**也不许低到读不出来**
        // （否则就是把「太清楚」治成「看不见」，那是另一个毛病）。
        foreach (var (theme, table) in new (string, IReadOnlyDictionary<string, string>)[]
                 { ("亮色", LightColorKeys), ("暗色", Dark) })
        {
            var disabled = Contrast(table["PrimaryDisabledInk"], table["AccentDisabled"]);
            var enabled = Contrast("#FFFFFF", table["AccentSolid"]);

            Assert.True(
                disabled < enabled,
                $"{theme}：禁用态 {disabled:F2}:1 不低于可用态 {enabled:F2}:1 —— 灰掉的按钮比能点的还显眼");

            // 下限：1.5:1 是「还看得出那儿有字」的量级，不是达标线
            // （WCAG 对禁用控件整条豁免，4.5 在这儿不适用）。
            Assert.True(disabled >= 1.5, $"{theme}：禁用态那行字只剩 {disabled:F2}:1，等于没字了");
        }
    }

    [Fact]
    public void 白字压危险与成功色那两对明知差一点的不许更差()
    {
        // 这两对**两套主题都不达标**，是有意留着的：亮色那边也一样
        // （3.76 / 2.54），所以不是暗色引入的。钉在这儿是为了
        // 「以后不许悄悄更差」—— 不是假装它们达标了。
        Assert.True(Contrast("#FFFFFF", Dark["Danger"]) >= 3.7);
        Assert.True(Contrast("#FFFFFF", Dark["Success"]) >= 2.5);

        // ⚠️ 两边的写法不一样（`Theme.xaml` 是 `#AARRGGBB`，暗色表是 `#RRGGBB`），
        // 直接比字符串会红 —— 比**同一个色**：剥掉透明度那一截再比。
        Assert.Equal(Rgb(Dark["Danger"]), Rgb(LightColorKeys["Danger"]));
        Assert.Equal(Rgb(Dark["Success"]), Rgb(LightColorKeys["Success"]));
    }

    // ─────────────────────────────────────────────
    // 亮色那边本来就有的几对（2026-10-07 一并改了）
    // ─────────────────────────────────────────────

    [Fact]
    public void 亮色下改过的这几对也达标()
    {
        // ⚠️ 这几对**不是暗色引入的**，是亮色主题里本来就有的、没人喊的缺陷
        // （清单第 5 批顺手收掉，见 `Theme.xaml` 各自那处的注释）：
        //
        //  ① `TextSecondary` 原来是 slate-500 `#64748B`：压在页底上 4.32:1，
        //     而它是 `Caption` 那个样式的主色、全界面用得最广的一支。
        //  ② `Warning` 原来是 amber-500：当**文字**用只有 2.15:1（XAML 里 10 处
        //     `Foreground`，另有 C# 那几支），
        //     这正是我在批次报告里**报错了**的那一条 —— 我说的是
        //     「琥珀字压琥珀底 2.07」，而代码里根本没有那一处（那两块警示块
        //     的字是 `TextPrimary`/`Caption`，琥珀只当描边）。真正不达标的是
        //     琥珀当文字。两处都已改成 orange-700 `#C2410C`。
        //
        // 门槛取标准值，不是取量出来的那个数 —— 一眼看得出达没达标。
        var pairs = new (string Label, string Fg, string Bg, double Min)[]
        {
            ("次级字 / 页底", "TextSecondary", "PageBackground", 4.5),
            ("次级字 / 卡片", "TextSecondary", "Surface", 4.5),
            ("次级字 / 侧栏", "TextSecondary", "SidebarBackground", 4.5),
            ("次级字 / 选中底", "TextSecondary", "AccentWeak", 4.5),

            // 警示色当**文字**用（这是那些站点真实的用法）。
            ("警示字 / 卡片", "Warning", "Surface", 4.5),
            ("警示字 / 页底", "Warning", "PageBackground", 4.5),

            // 警示色当 1px **描边**：图形门槛 3.0。
            ("警示描边 / 警示底", "Warning", "WarningSurface", 3.0),
        };

        foreach (var (label, fg, bg, min) in pairs)
        {
            var ratio = Contrast(LightColorKeys[fg], LightColorKeys[bg]);
            Assert.True(
                ratio >= min,
                $"亮色：{label}（{LightColorKeys[fg]} 压 {LightColorKeys[bg]}）只有 {ratio:F2}:1，要求 ≥ {min}:1");
        }
    }

    [Fact]
    public void 亮色这次的改动是换值不是换关系()
    {
        // ⚠️ 钉两头，缺一条就成了「反正改了都行」：
        //  ① 次级字**必须仍比正文浅**（它是 `Caption`，要跟正文分层）——
        //     顺手换成 slate-600 `#475569` 能到 6.89:1，但那会把层级压平。
        //  ② 它**必须仍比禁用字深**（禁用的那支更淡，不然两者分不出）。
        Assert.True(
            Luminance(LightColorKeys["TextSecondary"]) > Luminance(LightColorKeys["TextPrimary"]),
            "次级字比正文还深 ⇒ Caption 与正文的层级反了");
        Assert.True(
            Luminance(LightColorKeys["TextSecondary"]) < Luminance(LightColorKeys["TextDisabled"]),
            "次级字比禁用字还浅 ⇒ 「禁用」看着跟正常一样的淡");
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
        // `AccentWeak` 换成页底色**一条都不会红**（两块面撞在一起，
        // 对比度是 1.00:1，而当时唯一看着相关的那条门槛是「填充压轨道 ≥ 3.0」，
        // 那条比的是**填充**不是这两个面，拦不住）—— 可那样一来设置页
        // 「选中的那一行」在页底上就看不见了。
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
    // 取法：颜色一律得是「订阅」，不是「当场取一个对象」
    // ─────────────────────────────────────────────

    [Fact]
    public void App层的C_里不许把FindResource的结果存成画刷()
    {
        // ⚠️ 这条守的是**整类**缺陷，不是某几处：`FindResource` 是**当场把对象给你**
        //（拿到的是一支冻结的笔刷），赋给 DP 之后就跟资源字典脱钩了 ——
        // 换主题换不掉它，而且**没有任何东西会喊**。
        // 症状（2026-10-07 之前实际就这样）：「开机就是暗色没事，**开着程序去改
        // Windows 主题**时那几处停在亮色」—— 那几个窗口是之后才构造的，
        // 所以开机路径看着是对的，这正是它难被发现的原因。
        // 正确写法是 `element.SetResourceReference(dp, "Key")`（`AppTheme.SetDark` 的
        // `<remarks>` 里写死了这条）。
        var files = AppSourceFiles(".cs");
        var offenders = new List<string>();

        // 两种形状：`(Brush)X.FindResource("…")` / `(SolidColorBrush)…`，
        // 以及 `FindResource(…) as Brush`。
        var cast = new Regex(
            @"\(\s*(?:SolidColorBrush|Brush)\s*\)\s*[^;\r\n]{0,80}?(?:Find|TryFind)Resource\s*\(",
            RegexOptions.Compiled);
        var as_ = new Regex(
            @"(?:Find|TryFind)Resource\s*\([^;\r\n]*?\)\s*as\s+(?:SolidColorBrush|Brush)\b",
            RegexOptions.Compiled);

        foreach (var path in files)
        {
            // ⚠️ 注释行要剥掉：`WizardWindow` / `MultiViewWindow` 里各留了一句
            // 「别改回 `(Brush)FindResource(…)`」的说明，那不叫违规。
            foreach (var line in File.ReadAllLines(path).Where(one => !one.TrimStart().StartsWith("//")))
            {
                if (cast.IsMatch(line) || as_.IsMatch(line))
                {
                    offenders.Add($"{Path.GetFileName(path)}: {line.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "这些地方是**当场取**颜色而不是订阅它 —— 开着程序换 Windows 主题时不会跟着变，"
            + "而且不会报错。改成 `SetResourceReference(dp, \"Key\")`：\n"
            + string.Join('\n', offenders));

        // ⚠️ 空集永远绿 —— 路径写错、正则全不匹配，这一条照样过。
        Assert.True(files.Count >= 30, $"只扫到 {files.Count} 个 .cs，绊线的路径可能过期了");
    }

    [Fact]
    public void App的XAML里颜色键不许走StaticResource()
    {
        // ⚠️ 同一个病的 XAML 那一半：`StaticResource` 在加载那一刻就把值解析定了。
        // 实测（2026-10-07）：换完字典，**新建的 `PrimaryButton` 拿到的仍是亮色那支**
        // （`Style` 在 BAML 里已经 seal，`Setter` 里焊的是具体那支笔刷）。
        // 所以颜色键一律 `{DynamicResource …}`；`StaticResource` 只留给
        // 样式/模板/字号/圆角/转换器那些**本来就不该跟着主题走**的东西。
        var files = AppSourceFiles(".xaml");
        var offenders = new List<string>();

        foreach (var path in files)
        {
            foreach (var line in File.ReadAllLines(path))
            {
                foreach (Match match in Regex.Matches(line, @"\{StaticResource\s+(?<key>[A-Za-z0-9_]+)\s*\}"))
                {
                    var key = match.Groups["key"].Value;

                    if (LightColorKeys.ContainsKey(key))
                    {
                        offenders.Add($"{Path.GetFileName(path)}: {key}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "这些颜色键用了 StaticResource —— 换主题时它们停在旧值，而且不会报错。"
            + "改成 `{DynamicResource …}`：\n" + string.Join('\n', offenders));

        Assert.True(files.Count >= 10, $"只扫到 {files.Count} 个 .xaml，绊线的路径可能过期了");
    }

    /// <summary>
    /// <c>src/VidLog.Desktop.App</c> 下某个后缀的源码文件（<b>不含</b> <c>obj</c>/<c>bin</c>）。
    /// </summary>
    private static IReadOnlyList<string> AppSourceFiles(string extension) =>
        Directory
            .EnumerateFiles(
                Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App"),
                "*" + extension,
                SearchOption.AllDirectories)
            .Where(one => !one.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !one.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(one => one, StringComparer.Ordinal)
            .ToList();

    // ─────────────────────────────────────────────
    // 工具
    // ─────────────────────────────────────────────

    private static double Contrast(string fg, string bg)
    {
        var a = Luminance(fg);
        var b = Luminance(bg);

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>
    /// 剥掉透明度那一截，只留 <c>#RRGGBB</c> —— 亮色写 <c>#AARRGGBB</c>、
    /// 暗色表写 <c>#RRGGBB</c>，比「同一个色」时得先对齐写法。
    /// </summary>
    private static string Rgb(string hex) => "#" + hex.TrimStart('#')[^6..];

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
