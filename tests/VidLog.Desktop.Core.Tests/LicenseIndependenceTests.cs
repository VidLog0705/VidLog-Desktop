using System.Reflection;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Playback;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Search;
using VidLog.Desktop.Core.Web;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 许可设计 §5 / L8：试用到期 / 未激活 / 校验失败，**都不得锁住用户已有的录像** ——
/// 历史录像必须仍可查看、检索、导出。
/// </summary>
/// <remarks>
/// <para>
/// 设计文档把它写成硬约束，理由也写清楚了：「用户的数据是他的，不是人质。」
/// 这是商业伦理约束，不是技术选择。
/// </para>
/// <para>
/// ⚠️ <b>2026-09-27：许可真的接上了，所以这组测试从「今天不会失败」变成了真守卫。</b>
/// 在此之前它是一段**不会失败**的代码（那时根本没有许可实现）——
/// 现在 `LicenseService` / `LicenseVerifier` 都在，任何一处把门禁加进
/// 检索或回放都会让这里红。
/// </para>
/// <para>
/// 两条防线，一条结构、一条文本：
/// <list type="number">
/// <item><see cref="检索与回放的类型不得触碰许可"/> —— 反射：那些类型的
/// **成员签名**里不许出现许可类型。</item>
/// <item><see cref="检索_回放_导出那几条路的源码里不许出现许可"/> —— 读源码文本：
/// 方法**体**里的调用也能挡住（反射看不见方法体，而「顺手加一句 if」正是最常见的写法）。</item>
/// </list>
/// 天花板照旧：文本那条挡不住「绕个弯调」（比如经过一个不叫 License 的中间层）。
/// 真正根治要靠人——但**绊线存在的意义是让人在写那一句时先停一下**。
/// </para>
/// </remarks>
public class LicenseIndependenceTests
{
    /// <summary>这几条路的源码里**不许出现许可**（L8）。</summary>
    /// <remarks>
    /// <para>
    /// <b>名单的口径</b>：L8 原文点名的是三件事 ——「历史录像必须仍可**查看**、
    /// **检索**、**导出**」。所以名单 = 这三件事各自要走的整条路，
    /// <b>入口和它调用的每一层都要在</b>（只钉中间层的话，门禁加在入口上照样绕过去了）。
    /// 清理链是**宁宽勿窄**一并纳进来的：它不在这三件事里，但它决定数据**还在不在**，
    /// 与「用户的数据是他的」同一件事。
    /// </para>
    /// <para>
    /// ⚠️ <b>2026-09-27 补了四个</b>（许可真的接上之后重盘了一遍这条线，
    /// 原来是 8 个、现在是 12 个）：
    /// </para>
    /// <list type="bullet">
    /// <item><c>Media/ThumbnailCache.cs</c> —— 回放页列表里那格图</item>
    /// <item><c>Playback/RecordingTimeline.cs</c> —— 时间轴</item>
    /// <item><c>Playback/PunchNavigation.cs</c> —— 按打点跳转</item>
    /// <item><c>Cleanup/CleanupService.cs</c> —— 清理这条链的**入口**
    /// （<c>CleanupPolicy</c> / <c>CleanupExecutor</c> 早在名单里，入口反而不在，
    /// 那正是个洞）</item>
    /// </list>
    /// <para>
    /// 前三个是**用户看得见的「查看已有录像」** —— 门禁加在它们身上，表现是
    /// 「录像还在、但翻不动、图是空的、点打点没反应」，而那正是 L8 要挡的「锁住」。
    /// </para>
    /// <para>
    /// ⚠️ <b>刻意不在名单里的</b>（别以为是漏了）：
    /// <c>Recording/RecordingCoordinator.cs</c> —— 它**就是要看许可的**
    /// （未激活不得开始新的录制，L5）；
    /// <c>License/*</c> —— 许可本身的实现。
    /// </para>
    /// <para>
    /// ⚠️ 天花板照旧（与类注释里那条相同）：文本只挡得住**这个文件里**的写法，
    /// 挡不住「绕个弯调」（经过一个不叫 License 的中间层）。
    /// </para>
    /// </remarks>
    private static readonly string[] MustNotMentionLicense =
    [
        "Search/RecordingSearch.cs",
        "Web/PlaybackServer.cs",
        "Playback/RecordingTimeline.cs",
        "Playback/PunchNavigation.cs",
        "Media/ThumbnailCache.cs",
        "Export/EvidenceExporter.cs",
        "Cleanup/CleanupService.cs",
        "Cleanup/CleanupPolicy.cs",
        "Cleanup/CleanupExecutor.cs",
        "Index/RecordingIndex.cs",
        "Labels/LabelStore.cs",
        "Upload/UploadReceiver.cs",
    ];

    [Theory]
    [InlineData("Search/RecordingSearch.cs")]
    [InlineData("Web/PlaybackServer.cs")]
    [InlineData("Playback/RecordingTimeline.cs")]
    [InlineData("Playback/PunchNavigation.cs")]
    [InlineData("Media/ThumbnailCache.cs")]
    [InlineData("Export/EvidenceExporter.cs")]
    [InlineData("Cleanup/CleanupService.cs")]
    [InlineData("Cleanup/CleanupPolicy.cs")]
    [InlineData("Cleanup/CleanupExecutor.cs")]
    [InlineData("Index/RecordingIndex.cs")]
    [InlineData("Labels/LabelStore.cs")]
    [InlineData("Upload/UploadReceiver.cs")]
    public void 检索_回放_导出那几条路的源码里不许出现许可(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.Core",
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(path), $"找不到 {relativePath} —— 这条绊线的路径过期了");

        // 注释里提到许可**是允许的**（事实上好几处注释就在解释 L8），
        // 所以先把注释剥掉再看剩下的代码。
        var code = StripComments(File.ReadAllText(path));

        foreach (var word in new[] { "License", "许可" })
        {
            Assert.False(
                code.Contains(word, StringComparison.Ordinal),
                $"{relativePath} 的**代码**里出现了「{word}」。" +
                "许可设计 L8：未激活 / 试用到期 / 校验失败都不得挡住检索、回放、导出 —— " +
                "这几条路上不得有任何门禁。");
        }
    }

    /// <summary>剥掉注释（`//` 与 `/* */`）。</summary>
    /// <remarks>
    /// ⚠️ 只在**行首**剥 `//` 是不够的（行尾内联注释会漏过去，这个坑在另一条绊线上踩过）——
    /// 这里逐个字符扫，字符串字面量也跳过。
    /// </remarks>
    private static string StripComments(string source)
    {
        var builder = new System.Text.StringBuilder(source.Length);
        var inLine = false;
        var inBlock = false;
        var inString = false;

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLine)
            {
                if (c == '\n')
                {
                    inLine = false;
                    builder.Append(c);
                }

                continue;
            }

            if (inBlock)
            {
                if (c == '*' && next == '/')
                {
                    inBlock = false;
                    i++;
                }

                continue;
            }

            if (!inString && c == '/' && next == '/')
            {
                inLine = true;
                i++;
                continue;
            }

            if (!inString && c == '/' && next == '*')
            {
                inBlock = true;
                i++;
                continue;
            }

            if (c == '"' && (i == 0 || source[i - 1] != '\\'))
            {
                inString = !inString;
            }

            builder.Append(c);
        }

        return builder.ToString();
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

    /// <summary>L8 保护的那条路上的对外类型（检索 / 回放 / 导出 / 清理）。</summary>
    /// <remarks>
    /// <para>
    /// 这条与文本那条是**同一道防线的两面**，针对的是两种不同的注入方式：
    /// </para>
    /// <list type="bullet">
    /// <item><b>反射这条</b>：挡「成员**签名**里出现许可类型」——
    /// 也就是<b>依赖注入</b>那种写法（构造参数里塞一个 <c>LicenseService</c>）。
    /// 那是这种事最可能的形态。</item>
    /// <item><b>文本那条</b>：挡「方法**体**里的调用」—— 反射看不见方法体，
    /// 而「顺手加一句 if」正是最常见的写法。</item>
    /// </list>
    /// <para>
    /// ⚠️ <b>2026-09-27 补了三个</b>：<c>ThumbnailCache</c>、
    /// <c>PunchNavigation</c>、<c>CleanupService</c>。
    /// 理由与文本那条同一个（见 <see cref="MustNotMentionLicense"/> 的说明）——
    /// 尤其 <c>CleanupService</c>：它的**构造参数**正是最可能被注入许可的地方。
    /// </para>
    /// <para>
    /// 原来这里的名字是「M3 的检索与回放全部对外类型」—— M3 是里程碑编号，
    /// 而这条防线的边界是**规格里的 L8**，不是哪个里程碑，所以改了名字。
    /// </para>
    /// </remarks>
    public static TheoryData<Type> M3Types => new()
    {
        typeof(RecordingSearch),
        typeof(RecordingQuery),
        typeof(RecordingHit),
        typeof(PlaybackServer),
        typeof(PlaybackServerOptions),
        typeof(PlaybackSearchItem),
        typeof(RecordingTimeline),
        typeof(TimelineSegment),
        typeof(TimelinePosition),
        typeof(JsonLinesLabelStore),
        typeof(JsonLinesPunchLog),
        // 2026-09-27 补：回放页那两块的对外类型 + 清理链的入口。
        typeof(ThumbnailCache),
        typeof(PunchNavigation),
        typeof(CleanupService),
    };

    [Theory]
    [MemberData(nameof(M3Types))]
    public void 检索与回放的类型不得触碰许可(Type type)
    {
        var referenced = CollectReferencedTypes(type);

        var offenders = referenced
            .Where(t => t.Name.Contains("License", StringComparison.OrdinalIgnoreCase)
                     || t.Name.Contains("Licence", StringComparison.OrdinalIgnoreCase)
                     || t.Name.Contains("Activation", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName ?? t.Name)
            .Distinct()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{type.Name} 引用了许可相关类型：{string.Join(", ", offenders)}。" +
            "许可设计 L8 要求未激活 / 校验失败时历史录像仍可检索、回放 —— 这些路径不得有门禁。");
    }

    private static IEnumerable<Type> CollectReferencedTypes(Type type)
    {
        const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(All))
        {
            foreach (var t in Flatten(field.FieldType))
            {
                yield return t;
            }
        }

        foreach (var constructor in type.GetConstructors(All))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                foreach (var t in Flatten(parameter.ParameterType))
                {
                    yield return t;
                }
            }
        }

        foreach (var method in type.GetMethods(All))
        {
            foreach (var t in Flatten(method.ReturnType))
            {
                yield return t;
            }

            foreach (var parameter in method.GetParameters())
            {
                foreach (var t in Flatten(parameter.ParameterType))
                {
                    yield return t;
                }
            }
        }

        foreach (var property in type.GetProperties(All))
        {
            foreach (var t in Flatten(property.PropertyType))
            {
                yield return t;
            }
        }
    }

    /// <summary>把泛型类型展开成它自己加所有泛型实参（比如 <c>IReadOnlyList&lt;LicenseInfo&gt;</c>）。</summary>
    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Flatten(argument))
            {
                yield return nested;
            }
        }
    }
}
