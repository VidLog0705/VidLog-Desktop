using System.Text.RegularExpressions;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// XAML 绑定的一类**只有把行渲染出来才会炸**的写法。
/// </summary>
/// <remarks>
/// <para>
/// 2026-10-17 0.15.11 装机实测：检索页一查出结果，整个 App 当场退出，
/// 日志里是
/// <c>XamlParseException: 无法对"SearchResultRow"类型的只读属性"RetrievalPercent"
/// 进行 TwoWay 或 OneWayToSource 绑定</c>。
/// </para>
/// <para>
/// 根因不在那个属性，在 <c>ProgressBar.Value</c>：它继承 <c>RangeBase</c>，
/// 而那个 DP 的元数据写着 <c>BindsTwoWayByDefault</c> —— 于是
/// <c>Value="{Binding X}"</c> 不带 <c>Mode=</c> 就是 TwoWay，绑到只读属性上直接抛。
/// <c>Slider.Value</c> 同理。
/// </para>
/// <para>
/// ⚠️ <b>为什么别的闸都没拦住</b>：<c>--smoke</c> 只解析 XAML，不放数据进去；
/// Core.Tests 不引用 App；假件测试里那一行压根不存在。只有真的查出结果才炸。
/// </para>
/// </remarks>
public class XamlBindingLintTests
{
    [Fact]
    public void 进度条与滑块的Value绑定必须显式写Mode()
    {
        var files = Directory
            .EnumerateFiles(
                Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App"),
                "*.xaml",
                SearchOption.AllDirectories)
            .ToList();

        var offenders = new List<string>();

        foreach (var path in files)
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in Regex.Matches(lines[i], @"Value=""\{Binding\s+(?<body>[^}]*)\}"""))
                {
                    if (!match.Groups["body"].Value.Contains("Mode=", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(path)}:{i + 1}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "这些 Value 绑定没写 Mode —— `RangeBase.Value` 默认 TwoWay，"
            + "绑到只读属性上会在渲染那一行时抛 XamlParseException（整个 App 退出）。"
            + "写成 `Mode=OneWay`：\n" + string.Join('\n', offenders));

        Assert.True(files.Count >= 10, $"只扫到 {files.Count} 个 .xaml，绊线的路径可能过期了");
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
