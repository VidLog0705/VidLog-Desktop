namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 程序图标（2026-10-09 需求方要求：exe / 窗口标题栏 / 托盘**三处一起换**）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 这一族错误的形状是「**有的地方有图标、有的地方没有，而且一声不吭**」，
/// 不是编译错也不是崩溃 —— 所以只能靠钉住那几处**结构**来挡：
/// </para>
/// <list type="number">
/// <item>
/// <c>VidLog.ico</c> 要同时编进**两个**地方，去的是两个不同的世界：
/// <c>&lt;ApplicationIcon&gt;</c> → exe 的 Win32 图标资源（资源管理器、任务栏）；
/// <c>&lt;Resource&gt;</c> → 程序集里的 WPF 资源（<c>Window.Icon</c> 与托盘，
/// 那两处走 <c>Application.GetResourceStream</c> / pack URI）。
/// 少哪一条都是「一半有、一半没有」。
/// </item>
/// <item>
/// .ico 里**六档尺寸**（16/32/48/64/128/256）一个都不能少：托盘要 16、
/// 标题栏要 16 或 32、资源管理器大图标要 256 —— 少了某一档，那个尺寸就是系统
/// 从别档缩出来的，糊。这一条尤其挡「重新导一次图标、顺手只留一档」。
/// </item>
/// </list>
/// <para>
/// ⚠️ 天花板：这是**读文件文本 + 读 .ico 目录**，不是真的把窗口开起来看。
/// 「图标画上去长什么样」它挡不住（那要靠探针跑起来截图，见下一条）。
/// </para>
/// </remarks>
public class AppIconTests
{
    private static string AppDir() => Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App");

    [Fact]
    public void 图标文件编进了exe也编进了程序集资源()
    {
        var csproj = File.ReadAllText(Path.Combine(AppDir(), "VidLog.Desktop.App.csproj"));

        // 哨兵：读到的确实是那个 csproj，不是一个空文件。
        Assert.Contains("<Version>", csproj, StringComparison.Ordinal);

        Assert.Contains(
            "<ApplicationIcon>VidLog.ico</ApplicationIcon>", csproj, StringComparison.Ordinal);
        Assert.Matches(@"<Resource\s+Include=""VidLog\.ico""\s*/>", csproj);
    }

    [Fact]
    public void 图标里六档尺寸一个都不少()
    {
        var path = Path.Combine(AppDir(), "VidLog.ico");
        Assert.True(File.Exists(path), $"找不到 {path}");

        var bytes = File.ReadAllBytes(path);

        // ICONDIR：2 保留 + 2 类型(1=图标) + 2 张数；每张的目录项 16 字节，
        // 头两个字节是宽、高（**0 表示 256**，那是这套格式唯一放不下 256 的地方）。
        var type = BitConverter.ToUInt16(bytes, 2);
        var count = BitConverter.ToUInt16(bytes, 4);
        Assert.True(type == 1, $"这个文件的类型是 {type}，不是图标（1）。");
        Assert.True(count >= 6, $"只有 {count} 档，至少要有六档。");

        var sizes = Enumerable.Range(0, count)
            .Select(i => (int)bytes[6 + (16 * i)])
            .Select(w => w == 0 ? 256 : w)
            .ToArray();

        Assert.Equal(new[] { 16, 32, 48, 64, 128, 256 }, sizes);
    }

    /// <summary>
    /// 钉住「窗口图标**不是**靠 <c>Theme.xaml</c> 里的隐式样式挂的」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 这条是给「下一次有人觉得类处理器太绕、想换回隐式样式」准备的。
    /// 隐式样式那版**看着最省**（一个窗口都不用改），实际上 <b>WPF 不给
    /// <c>Window</c> 应用隐式样式</b> —— 2026-10-09 用探针实测：连
    /// <c>Window.Style</c> 都是 <c>null</c>，挂在同一条样式上的
    /// <c>Icon</c> / <c>Background</c> / <c>Title</c> 三个 Setter 一个都没落地，
    /// 而且**不报错、不警告**，只是标题栏那颗图安静地没有。
    /// </para>
    /// <para>
    /// ⚠️ 同样的道理，不能只在 12 个 <c>&lt;Window&gt;</c> 上各写一行
    /// <c>Icon="/VidLog.ico"</c>：那能生效，但 WPF 从那份多尺寸 .ico 里
    /// <b>只取第一帧</b>（本仓那份是 16），Alt+Tab 那颗 32 的是放大出来的。
    /// </para>
    /// </remarks>
    [Fact]
    public void 窗口图标走的是类处理器不是隐式样式()
    {
        var app = File.ReadAllText(Path.Combine(AppDir(), "App.xaml.cs"));
        Assert.Contains("EventManager.RegisterClassHandler", app, StringComparison.Ordinal);

        var theme = File.ReadAllText(Path.Combine(AppDir(), "Theme.xaml"));
        Assert.DoesNotContain(
            "<Style TargetType=\"Window\">", theme, StringComparison.Ordinal);
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

        throw new InvalidOperationException($"从 {AppContext.BaseDirectory} 往上找不到仓库根。");
    }
}
