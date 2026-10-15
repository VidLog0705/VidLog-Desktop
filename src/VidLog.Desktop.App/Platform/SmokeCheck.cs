using System.IO;
using System.Reflection;

using System.Windows;

// 本工程同时开了 UseWPF 与 UseWindowsForms，ImplicitUsings 会把两边的同名类型
// 都带进来（`UserControl` 两边都有）。这里钉死成 WPF 的那一套 —— 与 `App.xaml.cs`
// 里给 `MessageBox` 钉的那几行是同一个坑。
using UserControl = System.Windows.Controls.UserControl;

// 同上：`Application` 两边也都有。契约那一节要问的是 WPF 那份资源表。
using Application = System.Windows.Application;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// <c>--smoke</c>：把每个窗口/控件的 XAML 载入一遍就退出 —— 批次 0 的闸。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>要防的是什么</b>：<c>{StaticResource}</c> 是 <b>BAML 载入期</b>解析的。
/// 样式键写错、<c>Theme.xaml</c> 里改了 <c>Style</c> 名而某个窗口还在引用 ——
/// <b>编译绿、<c>dotnet build</c> 绿、一千多条测试全绿</b>，
/// 只有<b>那个窗口被打开</b>那一刻才抛 <c>XamlParseException</c>。
/// 而测试工程只引用 <c>Core</c>（不引用 <c>App</c>），结构上盖不到。
/// </para>
/// <para>
/// ⚠️ <b>「只跑 <c>InitializeComponent</c>、跳过构造函数」那条路实测走不通</b>
/// （2026-10-15 试过）：从 <c>RuntimeHelpers.GetUninitializedObject</c> 拿到的空壳上
/// 调 <c>Application.LoadComponent</c>，<b>14 个类型全抛</b>
/// <c>InvalidOperationException：当前本地值枚举已过时…</c> ——
/// BAML 读入器要枚举本地值，而 <c>Window</c> 构造里设过的那些默认值一个都没有。
/// </para>
/// <para>
/// ⚠️ <b>所以走「照常构造，参数一律给 <c>null</c>」</b>：13 个窗口里 10 个的构造签名
/// 要 <c>AppHost</c>，而真装配一次要 4–6 秒、还要相机与回放端口。
/// 给 <c>null</c> 之后构造函数<b>后半截</b>（<c>ApplyStationRole</c> 那一类）必然抛 ——
/// 那是<b>假红</b>，会把真红淹掉。于是判据收到一条：
/// <b>只有 <c>XamlParseException</c> 判红</b>。
/// </para>
/// <para>
/// ⚠️ <b>这条判据为什么成立</b>：<c>InitializeComponent()</c> 在 13 个构造函数里
/// 都是<b>头一句</b>（<c>MainWindow</c> 是 <c>_host = host;</c> 之后紧接着那句）——
/// 也就是说 BAML 载入<b>一定早于</b>任何碰 <c>_host</c> 的代码。
/// 样式键写错必在那一步抛 <c>XamlParseException</c>，它永远先到。
/// 其余异常一律记 <c>NOTE</c> 并<b>印出来</b>，不判红、也不藏。
/// </para>
/// <para>
/// ⚠️ <b>盖不到 <c>DynamicResource</c> 漏键</b>：那个要窗口真 <c>Show()</c> 才解析。
/// 界面那几百处取色已全改成 <c>DynamicResource</c>（改造清单第 5 批），
/// 所以漏键这一类这里<b>盖不住</b> —— 本闸的目标只有「载入就炸」。
/// </para>
/// </remarks>
internal static class SmokeCheck
{
    /// <summary>命令行开关。</summary>
    public const string Switch = "--smoke";

    /// <summary>参数里带没带那个开关。</summary>
    public static bool Requested(IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return false;
        }

        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], Switch, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 逐个载入，全过退 0、任一 XAML 炸退 1。
    /// </summary>
    /// <remarks>
    /// 一个一个来、不做短路：一次跑完能看到<b>所有</b>坏窗口，改一轮就够。
    /// </remarks>
    public static int Run()
    {
        // 输出按 UTF-8 走：默认编码在这台机器上是 GBK，中文错误信息
        // 进了 CI 日志就是一串乱码（这一步的产出<b>只有</b>这几行文字，乱码等于白跑）。
        // 拿不到控制台时设不上，那也只是回到乱码 —— 不该因此把整个闸带下去。
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
        }

        var failed = new List<string>();
        var notes = new List<string>();

        foreach (var type in Roots())
        {
            var ctor = type.GetConstructors()
                .OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (ctor is null)
            {
                Write($"SKIP {type.Name}（没有可见的构造函数）");
                continue;
            }

            // 参数一律给 null：这里要的只是「构造函数有没有走到 InitializeComponent」，
            // 而它要的那些协作对象（`AppHost` 那一类）在这个闸里一个都不该被造出来。
            var args = new object?[ctor.GetParameters().Length];

            try
            {
                ctor.Invoke(args);
                Write($"OK   {type.Name}");
            }
            catch (Exception ex)
            {
                var root = Unwrap(ex);

                // ⚠️ 判据走**整条链**，不看剥到最里面那一层 —— 这一条是**实测**改过来的：
                // 先写的那版剥完再判 `root is XamlParseException`，于是永远判不中
                // （样式键写错时，外面那层 `XamlParseException` 被剥掉，
                // 露出来的是一句普通的 `无法找到名为「X」的资源`），
                // 探针实测：故意把 `StartupWindow.xaml` 的 `PageTitle` 改成一个不存在的键，
                // 闸照样报「全部通过」、退出码 0。
                if (IsLoadFailure(ex))
                {
                    failed.Add($"{type.Name}: {root.GetType().Name}: {root.Message}");
                    Write($"FAIL {type.Name}: {root.GetType().Name}: {root.Message}");
                    continue;
                }

                // 不是载入错误 ⇒ BAML 已经读进来了，抛的是构造函数后半截
                // 碰 `null` 协作对象那一下。**留着光**：真要是哪天有个窗口
                // 把 InitializeComponent 挪到后面去了，从这里能看出来。
                notes.Add($"{type.Name}: {root.GetType().Name}: {root.Message}");
                Write($"NOTE {type.Name}: {root.GetType().Name}: {root.Message}");
            }
        }

        // 载入那一步跑完再钉契约：载入炸了的话，下面问样式资源也问不出什么名堂。
        Contracts(failed);

        if (failed.Count > 0)
        {
            Write($"界面载入 smoke：{failed.Count} 个失败（另有 {notes.Count} 个构造需要真协作对象，已跳过）");
            return 1;
        }

        Write($"界面载入 smoke：全部通过（{notes.Count} 个构造需要真协作对象，已跳过）");
        return 0;
    }

    /// <summary>
    /// 钉住**需求方亲自裁定过的那几个值**（批次 0b）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>只钉他点过名的，不钉我随手定的。</b>锁的是<b>契约</b>（他裁过的），
    /// 不是<b>设计</b>（我挑的数）—— 全量属性断言（每个控件都钉一遍）<b>明确不做</b>：
    /// 那样每次调设计都要改测试，成本大于收益。
    /// </para>
    /// <para>
    /// ⚠️ 走<b>反射查类型</b>而不是<b>构造实例再读属性</b>：`SettingsWindow` 那类窗口
    /// 的构造签名要 `AppHost`，本闸给的是 `null`，构造<b>必然半路抛</b>（上面循环里那些
    /// `NOTE` 就是），拿不到实例。而这一节要钉的东西<b>全都能在类型与样式资源上问到</b>。
    /// </para>
    /// </remarks>
    private static void Contracts(List<string> failed)
    {
        // ① 输入控件那几支隐式样式必须还在、且钉着 13 字 / 36 高。
        //    ⚠️ 钉**样式**而不是钉某个页面上的某个控件：批次 A1-b 是拿隐式样式一次性
        //    归队全程序的（ComboBox / TextBox / DatePicker / RadioButton 四支），
        //    数据页那 4 个筛选控件正是**因为**吃到它才是 13 —— 从这里钉，一处覆盖全程序。
        DemandStyle<System.Windows.Controls.TextBox>(failed, 13, 36);
        DemandStyle<System.Windows.Controls.ComboBox>(failed, 13, 36);
        DemandStyle<System.Windows.Controls.DatePicker>(failed, 13, 36);

        // ② 扫码的「工作模式」必须是两颗单选，不是下拉（批次 A4，他 2026-10-15 裁的）。
        //    ⚠️ 从**类型**上查 `ModeCombo` 还在不在：XAML 里那三行改回去，
        //    编译照样绿（`TagOf(ComboBox)` 与 `TagOf(IEnumerable<RadioButton>)`
        //    两个重载都在），只有这里会发现。
        var settings = typeof(SettingsWindow);
        if (Found(settings, "ModeCombo") is not null)
        {
            failed.Add("SettingsWindow.ModeCombo 又回来了 —— 工作模式裁定是两颗单选（批次 A4）");
            Write("FAIL 契约：SettingsWindow.ModeCombo 又回来了");
        }

        foreach (var name in new[] { "ModeContinuous", "ModeStopOnSame" })
        {
            var field = Found(settings, name);
            if (field?.FieldType == typeof(System.Windows.Controls.RadioButton))
            {
                continue;
            }

            failed.Add($"SettingsWindow.{name} 不是 RadioButton —— 工作模式裁定是两颗单选（批次 A4）");
            Write($"FAIL 契约：SettingsWindow.{name} 不是 RadioButton");
        }

        // ③ 卡片那一层必须给字色（2026-10-15 需求方报「暗色下这几个字是暗的、
        //    为什么不改成亮色」查出来的那一条）。
        //    ⚠️ 钉**卡片这一层**，不是钉他截图里那两个 `TextBlock`：漏的是
        //    「所有卡片里没显式给颜色的 TextBlock」，那两行只是被拍到的那两个。
        //    这一句一松，卡片里的裸字就退回链上最近那个 `Control` 的默认墨色
        //    （纯黑）—— 亮色下看不出来（黑字压白卡片正好对），**只有暗色下是黑的**。
        DemandCardInk(failed);
    }

    /// <summary>卡片样式给没给 <c>TextElement.Foreground</c>。</summary>
    private static void DemandCardInk(List<string> failed)
    {
        if (Application.Current?.TryFindResource("Card") is not Style card)
        {
            failed.Add("找不到 Card 样式 —— 卡片里那层字色就收口在它身上");
            Write("FAIL 契约：找不到 Card 样式");
            return;
        }

        var ink = card.Setters.OfType<Setter>().FirstOrDefault(
            setter => setter.Property == System.Windows.Documents.TextElement.ForegroundProperty);

        if (ink?.Value is not null)
        {
            return;
        }

        failed.Add("Card 样式没给 TextElement.Foreground —— 卡片里的裸 TextBlock 会退回纯黑（暗色下就是黑的）");
        Write("FAIL 契约：Card 样式没给 TextElement.Foreground");
    }

    /// <summary>某一个类型的隐式样式在不在、字号与最小高度对不对。</summary>
    /// <remarks>
    /// 隐式样式在资源字典里是**拿类型本身当键**存的（没有 `x:Key`），所以能这样直接问。
    /// </remarks>
    private static void DemandStyle<T>(List<string> failed, double fontSize, double minHeight)
        where T : System.Windows.FrameworkElement
    {
        var style = Application.Current?.TryFindResource(typeof(T)) as Style;

        if (style is null)
        {
            failed.Add($"{typeof(T).Name} 没有隐式样式了（批次 A1-b 归队用的就是它）");
            Write($"FAIL 契约：{typeof(T).Name} 没有隐式样式");
            return;
        }

        Demand(style, System.Windows.Controls.Control.FontSizeProperty, fontSize);
        Demand(style, System.Windows.FrameworkElement.MinHeightProperty, minHeight);

        void Demand(Style s, System.Windows.DependencyProperty property, double want)
        {
            var got = s.Setters.OfType<Setter>()
                .FirstOrDefault(setter => setter.Property == property)?.Value;

            if (got is double value && Math.Abs(value - want) < 0.01)
            {
                return;
            }

            failed.Add($"{typeof(T).Name} 的 {property.Name} 是 {got ?? "（没设）"}，应该是 {want}");
            Write($"FAIL 契约：{typeof(T).Name}.{property.Name} = {got ?? "（没设）"}，应该是 {want}");
        }
    }

    private static System.Reflection.FieldInfo? Found(Type type, string name) =>
        type.GetField(name, System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Public);

    /// <summary>要载入的类型：本程序集里所有窗口与用户控件，按名字排（输出稳定）。</summary>
    private static IEnumerable<Type> Roots() =>
        typeof(App).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition)
            .Where(t => typeof(Window).IsAssignableFrom(t) || typeof(UserControl).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    /// <summary>异常链里有没有 <c>XamlParseException</c>（= BAML 载入那一步炸了）。</summary>
    private static bool IsLoadFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Windows.Markup.XamlParseException)
            {
                return true;
            }
        }

        return false;
    }

    private static Exception Unwrap(Exception ex)
    {
        while ((ex is TargetInvocationException || ex is System.Windows.Markup.XamlParseException)
               && ex.InnerException is { } inner)
        {
            ex = inner;
        }

        return ex;
    }

    /// <remarks>
    /// ⚠️ 走 <c>Console</c> 而不是日志：这是 build/CI 的一步，
    /// 输出要跟着 stdout 走才收得到（WinExe 没有控制台，但句柄被重定向时写得出去）。
    /// </remarks>
    private static void Write(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }
}
