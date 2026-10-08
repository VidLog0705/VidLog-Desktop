using System.Text.RegularExpressions;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 读 ffmpeg 文本的那些管道**必须声明 UTF-8**。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 这条绊线是 2026-10-08 那个缺陷留下的。ffmpeg 往管道里写的是 **UTF-8**，
/// 而 .NET 不给 <c>StandardErrorEncoding</c> / <c>StandardOutputEncoding</c> 时
/// 用的是**进程的控制台码页**（中文 Windows = 936）。
/// </para>
/// <para>
/// 后果不是「日志里字难看」，是**功能坏掉**：`DshowDevices` 把 `-list_devices`
/// 读成 <c>楹﹀厠椋?(USB Audio Device)</c>，而这个乱码名字**又被回传给 ffmpeg
/// 当设备名**（设置里存的就是它）⇒ <c>Could not find audio only device</c>
/// ⇒ 音频那一路永远起不来、静默降级成没有音轨的一段。
/// </para>
/// <para>
/// ⚠️ 它藏了很久是因为**纯 ASCII 的设备名在任何码页下都活**（`PC CAMERA-` 就是），
/// 所以视频那一路从来没露过馅 —— 只在设备名里有汉字时炸。
/// </para>
/// <para>
/// 不去断言「凡重定向 stdout 就要声明」是有意的：`PrerecordProcess` /
/// `PreviewProcess` / `LiveTileProcess` 那三路的 stdout 是**裸帧 / MJPEG 流**，
/// 走 <c>BaseStream</c>，与编码无关 —— 硬要求它们声明只会逼出三行假声明。
/// </para>
/// </remarks>
public class FfmpegPipeEncodingTests
{
    /// <summary>眼下重定向 stderr 的那几处（防「空集永远绿」，见下面第一条断言）。</summary>
    private static readonly string[] Redirections =
    [
        "DshowDevices.cs",
        "FfmpegCameraCapture.cs",
        "ProcessRunner.cs",
        "PrerecordProcess.cs",
        "PreviewProcess.cs",
        "LiveTileProcess.cs",
        "MicrophoneLevelMonitor.cs",
    ];

    [Fact]
    public void 每个重定向_stderr_的进程块都要声明_UTF8()
    {
        var blocks = StderrBlocks();

        // ⚠️ **空集永远绿**（本仓踩过一次：预检先 commit 再跑 = 扫个空集，照样绿）。
        // 所以先钉住「确实扫到了这几处」—— 少一处也不许静默变成「一条都没扫到」。
        foreach (var expected in Redirections)
        {
            Assert.Contains(expected, blocks.Select(b => b.File));
        }

        var missing = blocks
            .Where(block => !block.Text.Contains("StandardErrorEncoding", StringComparison.Ordinal))
            .Select(block => block.File)
            .Distinct()
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void 读_stdout_文本的那两处也要声明_UTF8()
    {
        // ⚠️ 这两处是**点名**的，不是「凡重定向 stdout 都要」：它们读的是 ffmpeg
        // 打在 stdout 上的**文本**（电平 `ametadata=print`、外部进程的输出），
        // 而上面那条 remarks 里说的三处读的是裸流。点了名将来才拦得住。
        foreach (var name in new[] { "ProcessRunner.cs", "MicrophoneLevelMonitor.cs" })
        {
            var code = Code(Find(name));

            Assert.Contains("StandardOutputEncoding = Encoding.UTF8", code, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 扫 <c>src/</c> 下每一个 <c>new ProcessStartInfo</c> 初始化块，取其中重定向了
    /// stderr 的那些。
    /// </summary>
    /// <remarks>
    /// ⚠️ 先剥掉**整行注释**：注释里写着 <c>RedirectStandardError = true</c>
    /// 这种例子的地方不许被当成真声明（本仓的文本绊线都这么干）。
    /// </remarks>
    private static IReadOnlyList<(string File, string Text)> StderrBlocks()
    {
        var blocks = new List<(string, string)>();

        foreach (var path in SourceFiles())
        {
            var code = Code(path);

            foreach (Match match in Regex.Matches(
                code, @"new ProcessStartInfo.*?\};", RegexOptions.Singleline))
            {
                if (match.Value.Contains("RedirectStandardError = true", StringComparison.Ordinal))
                {
                    blocks.Add((Path.GetFileName(path), match.Value));
                }
            }
        }

        return blocks;
    }

    private static string Find(string fileName) => SourceFiles()
        .First(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.Ordinal));

    /// <summary><c>src/</c> 下的全部 <c>.cs</c>（不含 <c>obj</c> / <c>bin</c>）。</summary>
    private static IReadOnlyList<string> SourceFiles() =>
        Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(one => !one.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !one.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(one => one, StringComparer.Ordinal)
            .ToList();

    /// <summary>读一份源码，剥掉**整行注释**。</summary>
    private static string Code(string path) => string.Join(
        '\n',
        File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//")));

    /// <summary>仓库根目录（往上找到有 <c>src</c> 的那一层）。</summary>
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
