using System.Reflection;
using System.Text.RegularExpressions;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 钉住 <c>AGENTS.md</c> §6.1 里那条**一直没有检查**的规矩：日志器可选的构造点，
/// **调用处必须传**。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么要有它</b>：§6.1.1 自己写着「根因不是「忘了」，是<b>这条规矩没有能失败的检查</b>」——
/// 那张表上 L8、假开关、版本号都有闸，只有 §6.1 是「❌ 一个字都没有」。已漏过三次
/// （2026-09-29 两轮、2026-10-01 六处）。而缺口的形状是**编译器完全不说话**的：
/// <c>IAppLogger? logger = null</c> ⇒ 不传就静默不落盘，一个字都不会报。
/// </para>
/// <para>
/// ⚠️ <b>为什么不能靠现有那几条绊线</b>：<c>DesktopServicesTests</c> 里那几条
/// （「组合根把日志器递给了每个边界」「界面调设备枚举时真的把日志器传了」）
/// 是**一处一处手写上去的** —— 每一条都来自一次已经发生的漏记，写完只管住那一处。
/// 下一个人新写一个 logger 可选的类、在别处 <c>new</c> 出来，它们全都看不见。
/// 这条反过来：**构造点在 Core 程序集里自己报出来**（反射），
/// 于是「新加的类」自动进闸，不需要谁来登记。
/// </para>
/// <para>
/// ⚠️ <b>为什么是文本 + 反射，而不是行为测试</b>：那些构造函数是「传了 logger 就记、
/// 不传就不记」，行为测出来永远是绿的（<c>BoundaryLoggingTests</c> 就是直接构造、
/// 自己传 logger 的 —— 它证明的是「这些类会记」，不是「生产上有人递给它」）。
/// 而 App 层是 <c>net9.0-windows</c>、本测试工程是 <c>net9.0</c>，
/// 引不到它 ⇒ 那一跳只能扫源码文本。与 §«装配的最后一跳» 是同一类毛病。
/// </para>
/// <para>
/// ⚠️ <b>天花板（明写在这儿，别指望它管）</b>：
/// <list type="number">
/// <item><b>只管「传没传」，管不住「传的对不对」</b> —— <c>logger: NullLogger.Instance</c>
/// 它照样放行。</item>
/// <item><b>实例方法叫不到</b>：只认 <c>new T(</c> 与 <c>T.M(</c>（静态）。
/// 从字段 / 变量上调的实例方法（<c>_registry.SomethingAsync(...)</c>）它看不见 ——
/// 现在 Core 里带可选日志器的公开实例方法**一个都没有**（2026-10-11 核过），
/// 哪天有了，这条得跟着扩。</item>
/// <item><b>App 自己定义的那几个</b>（<c>GlobalHotKeys</c> / <c>WindowsKeyboardHook</c> /
/// <c>AppTheme</c> / <c>StartupRegistration</c> / <c>MultiViewWindow</c>）
/// 反射看不到（它们在 Windows 专用程序集里），仍由
/// <c>DesktopServicesTests</c> 那几条手写的绊线管着。</item>
/// <item>同名类型（不同命名空间）按**并集**放行，可能漏掉其中一支。</item>
/// </list>
/// </para>
/// </remarks>
public class LoggingWiringTests
{
    /// <summary>`new T(` 与 `X.Y(` 两种调用形状。</summary>
    /// <remarks>
    /// ⚠️ 前者认构造点、后者认静态方法；后者会匹配到源码里**每一处** <c>a.B(</c>，
    /// 这是有意的 —— 靠字典查一下就知道要不要管，比先精确匹配再回溯简单得多。
    /// </remarks>
    private static readonly Regex Calls = new(
        @"new\s+(?<ctor>[A-Za-z_]\w*)\s*\(|(?<owner>[A-Za-z_]\w*)\.(?<member>[A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled);

    /// <summary>参数写成 `logger: ...` 吗（按名字传的，位置就不作数了）。</summary>
    private static readonly Regex NamedLogger = new(@"^\s*logger\s*:", RegexOptions.Compiled);

    [Fact]
    public void 日志器可选的那些构造点_生产代码里一处都不许漏传()
    {
        var sites = LoggerOptionalSites();

        // 哨兵①：反射真的量到东西了。（量不到时下面会一条不报地绿。）
        Assert.True(
            sites.Count >= 30,
            $"只从 Core 程序集里量到 {sites.Count} 个「日志器可选」的构造点 —— "
                + "反射大概走偏了（类型改成 internal、或者程序集换了）。");

        var offenders = new List<string>();
        var reached = 0;

        foreach (var file in ProductionFiles())
        {
            var code = BlankComments(File.ReadAllText(file));
            var relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');

            foreach (Match call in Calls.Matches(code))
            {
                var key = call.Groups["ctor"].Success
                    ? call.Groups["ctor"].Value
                    : call.Groups["owner"].Value + "." + call.Groups["member"].Value;

                if (!sites.TryGetValue(key, out var loggerAt))
                {
                    continue;
                }

                reached++;
                var args = ArgumentsAt(code, call.Index + call.Length - 1);

                // 位置够得着 → 传了；写成 `logger:` → 也传了（位置就不作数）。
                if (args.Count > loggerAt.Min() || args.Any(one => NamedLogger.IsMatch(one)))
                {
                    continue;
                }

                offenders.Add(
                    $"{relative}:{LineOf(code, call.Index)} → {key}({string.Join(", ", args.Select(one => one.Trim()))})");
            }
        }

        // 哨兵②：扫到了真的调用点。少了这一句，正则写坏（比如路径过期、扫到 0 个文件）
        // 照样绿 —— 与「许可那句话只有一份」那条同一个教训（2026-10-07 当场撞到过）。
        Assert.True(reached >= 20, $"只认到 {reached} 处调用 —— 扫描路径或正则过期了，这条会空着绿。");

        Assert.True(
            offenders.Count == 0,
            "下面这些地方构造出来的对象，**日志器是可选的**（`IAppLogger? logger = null`）——"
                + "不传就静默不落盘，而编译器一个字都不会说（`AGENTS.md` §6.1 / §6.1.1）。"
                + "把 `AppHost.Logger`（Core 里是 `logger`）递下去：\n  "
                + string.Join("\n  ", offenders));
    }

    // ─────────────────────────────────────────────
    // 反射：哪些构造点的日志器是可选的
    // ─────────────────────────────────────────────

    /// <summary>
    /// Core 程序集里**每一个**带「可选日志器参数」的公开构造点。
    /// 键是源码里会写出来的那个名字（构造点用类型名、静态方法用 `类型.方法`）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 一个类型有几支重载、日志器落在不同位置时，这里存的是**并集** ——
    /// 判断时「够得着其中任一支」就算传了。这是**故意放宽**的：误红一条就会有人
    /// 把它改成白名单，那这条就废了；而真正的缺口（**一处都不传**）照样抓得住。
    /// </remarks>
    private static Dictionary<string, int[]> LoggerOptionalSites()
    {
        var sites = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        void Add(string key, int index)
        {
            if (!sites.TryGetValue(key, out var positions))
            {
                sites[key] = positions = [];
            }

            if (!positions.Contains(index))
            {
                positions.Add(index);
            }
        }

        var assembly = typeof(IAppLogger).Assembly;

        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var constructor in type.GetConstructors())
            {
                var parameters = constructor.GetParameters();

                for (var i = 0; i < parameters.Length; i++)
                {
                    if (IsOptionalLogger(parameters[i]))
                    {
                        Add(type.Name, i);
                    }
                }
            }

            // 只认静态的：只有它们是写成 `类型.方法(...)` 的。
            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var parameters = method.GetParameters();

                for (var i = 0; i < parameters.Length; i++)
                {
                    if (IsOptionalLogger(parameters[i]))
                    {
                        Add(type.Name + "." + method.Name, i);
                    }
                }
            }
        }

        return sites.ToDictionary(one => one.Key, one => one.Value.ToArray(), StringComparer.Ordinal);
    }

    private static bool IsOptionalLogger(ParameterInfo parameter) =>
        parameter.IsOptional && parameter.ParameterType == typeof(IAppLogger);

    // ─────────────────────────────────────────────
    // 扫源码
    // ─────────────────────────────────────────────

    /// <summary>生产源码（`src/` 下的 `.cs`，跳过 `bin/` `obj/`）。</summary>
    private static IEnumerable<string> ProductionFiles()
    {
        var src = Path.Combine(RepoRoot(), "src");

        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(file =>
            !Path.GetRelativePath(src, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"));
    }

    /// <summary>
    /// 从那个左括号开始，取出**顶层**逗号分开的每一个实参。
    /// </summary>
    /// <remarks>
    /// ⚠️ 嵌套得按深度算：<c>new Foo(Bar(1, 2), 3)</c> 是**两个**实参，不是三个。
    /// 字符串字面量里的逗号更要跳过去（<c>"a, b"</c>）。
    /// </remarks>
    private static List<string> ArgumentsAt(string code, int open)
    {
        var args = new List<string>();
        var start = open + 1;
        var depth = 0;
        var inString = false;
        var verbatim = false;

        for (var i = start; i < code.Length; i++)
        {
            var c = code[i];
            var next = i + 1 < code.Length ? code[i + 1] : '\0';

            if (inString)
            {
                if (verbatim)
                {
                    if (c == '"' && next == '"')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                        verbatim = false;
                    }

                    continue;
                }

                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '@' when next == '"':
                    inString = true;
                    verbatim = true;
                    i++;
                    continue;

                case '"':
                    inString = true;
                    continue;

                case '(' or '[' or '{':
                    depth++;
                    continue;

                case ')' when depth == 0:
                    args.Add(code[start..i]);
                    return args.Count == 1 && args[0].Trim().Length == 0 ? [] : args;

                case ')' or ']' or '}':
                    depth--;
                    continue;

                case ',' when depth == 0:
                    args.Add(code[start..i]);
                    start = i + 1;
                    continue;
            }
        }

        throw new InvalidOperationException($"源码里那个左括号（位置 {open}）没有闭合。");
    }

    /// <summary>
    /// 把注释换成**等长空白**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 换空白、**不是删掉**：删了长度就变了，后面所有下标与行号全部错位，
    /// 报出来的行号会点到别的行上（<c>TestHygieneTests</c> 的 <c>Blank</c> 同一条理由）。
    /// 字符串字面量照原样留着 —— 参数切分要靠它跳过引号里的逗号。
    /// </remarks>
    private static string BlankComments(string source)
    {
        var chars = source.ToCharArray();
        var inLine = false;
        var inBlock = false;
        var inString = false;
        var verbatim = false;

        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            var next = i + 1 < chars.Length ? chars[i + 1] : '\0';

            if (inLine)
            {
                if (c == '\n')
                {
                    inLine = false;
                }
                else
                {
                    chars[i] = ' ';
                }

                continue;
            }

            if (inBlock)
            {
                if (c == '*' && next == '/')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    inBlock = false;
                    i++;
                }
                else if (c != '\n')
                {
                    chars[i] = ' ';
                }

                continue;
            }

            if (inString)
            {
                if (verbatim)
                {
                    if (c == '"' && next == '"')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                        verbatim = false;
                    }
                }
                else if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '/' && next == '/')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                inLine = true;
                i++;
            }
            else if (c == '/' && next == '*')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                inBlock = true;
                i++;
            }
            else if (c == '@' && next == '"')
            {
                inString = true;
                verbatim = true;
                i++;
            }
            else if (c == '"')
            {
                inString = true;
            }
        }

        return new string(chars);
    }

    /// <summary>第几行（从 1 数）。</summary>
    private static int LineOf(string text, int index)
    {
        var line = 1;

        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
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
