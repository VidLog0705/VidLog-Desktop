using System.Reflection;
using VidLog.Desktop.Core.Labels;
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
    /// 挑的是「用户要拿回自己数据」必走的几条：检索、回放服务、导出、清理判定、
    /// 索引与标签的读写。
    /// </remarks>
    private static readonly string[] MustNotMentionLicense =
    [
        "Search/RecordingSearch.cs",
        "Web/PlaybackServer.cs",
        "Export/EvidenceExporter.cs",
        "Cleanup/CleanupPolicy.cs",
        "Cleanup/CleanupExecutor.cs",
        "Index/RecordingIndex.cs",
        "Labels/LabelStore.cs",
        "Upload/UploadReceiver.cs",
    ];

    [Theory]
    [InlineData("Search/RecordingSearch.cs")]
    [InlineData("Web/PlaybackServer.cs")]
    [InlineData("Export/EvidenceExporter.cs")]
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

    /// <summary>M3 的检索与回放全部对外类型。</summary>
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
