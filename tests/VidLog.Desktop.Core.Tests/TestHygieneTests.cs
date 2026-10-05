using System.Text.RegularExpressions;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 测试卫生：测试源码里**不许留下恒真的断言**（改造清单 T27 ④）。
/// </summary>
/// <remarks>
/// <para>
/// 2026-10-04 的审计在仓里抓到过两条**恒真**断言
/// （<c>SettingsStoreTests.cs:372-379</c>、<c>PreviewThroughputTests.cs:187</c>），
/// 都当场修了。修完之后**没有任何东西拦着下一条** —— 这条就是那个东西。
/// </para>
/// <para>
/// ⚠️ <b>一个恒真的断言比没有断言更坏</b>：它绿着，还占着「这条有人守」的位置，
/// 谁去删它反而要解释一遍为什么。这与仓里另外几条绊线是同一条道理
/// （「因为跳过所以绿」的测试同理，见 <c>RequiresFfmpegFactAttribute</c> 的类注释）。
/// </para>
/// <para>
/// <b>天花板（明写在这儿）</b>：这条只认「两边都是字面量」的形状
/// —— <c>Assert.True(true)</c> / <c>Assert.False(false)</c>。
/// <c>Assert.True(某个恒为真的变量)</c> 它看不见，而**那才是真正常见的形态**
/// （比如在空集合上断言「不含 X」）。要挡那种得靠人看，或者靠
/// 「这条断言**能红**吗」这个问句 —— 绊线管不了，别指望它。
/// </para>
/// </remarks>
public class TestHygieneTests
{
    /// <summary>永远是绿的两种字面形状。</summary>
    private static readonly Regex AlwaysPasses = new(
        @"Assert\.(?:True\(\s*true|False\(\s*false)\s*[,)]",
        RegexOptions.Compiled);

    [Fact]
    public void 测试源码里不许有恒真的断言()
    {
        var testsRoot = Path.Combine(RepoRoot(), "tests");
        Assert.True(Directory.Exists(testsRoot), $"找不到 {testsRoot} —— 这条绊线的路径过期了");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(testsRoot, file).Replace('\\', '/');

            // 生成物不看（`obj/` 里那份是 XAML 编译出来的，`bin/` 里是拷贝）。
            if (relative.StartsWith("bin/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            var code = StripComments(File.ReadAllText(file));

            foreach (Match match in AlwaysPasses.Matches(code))
            {
                offenders.Add($"{relative}:{LineOf(code, match.Index)} → {match.Value}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "这些断言两边都是字面量，**永远是绿的**，等于没写：\n  "
                + string.Join("\n  ", offenders)
                + "\n把它换成一条真会失败的断言 —— 先问「它坏了会红吗」。");
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

    /// <summary>把注释换成等长的空白。</summary>
    /// <remarks>
    /// ⚠️ 这里用的是**朴素正则**，不是 <c>LicenseIndependenceTests.StripComments</c>
    /// 里那个逐字符的扫描器（它还会跳过字符串字面量）。这条绊线只找字面形状，
    /// 少剥一点也只会抹掉真注释、不会凭空造出一个匹配 —— 为此再抄一份 40 行的
    /// 扫描器不值当。
    /// <para>
    /// 换掉的是**空白不是删掉**：位置和行号都要保住，报错要能直接点到那一行。
    /// </para>
    /// </remarks>
    private static string StripComments(string source) =>
        Regex.Replace(source, @"//[^\n]*|/\*.*?\*/", Blank, RegexOptions.Singleline);

    /// <summary>原样保留换行、其余换成空格。</summary>
    private static string Blank(Match match) =>
        new(match.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray());

    /// <summary>仓库根目录（往上找到有 `src` 的那一层）。</summary>
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
