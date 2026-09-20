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
/// 当前还没有任何许可实现（那是 M6），所以这组测试今天是**不会失败的** ——
/// 它的价值是当 M6 把许可接进来时的绊线：如果那时有人把门禁加进了检索或回放，
/// 这里会红。写在这里是为了让那条约束有个可执行的位置，而不是只活在文档里。
/// </para>
/// </remarks>
public class LicenseIndependenceTests
{
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
