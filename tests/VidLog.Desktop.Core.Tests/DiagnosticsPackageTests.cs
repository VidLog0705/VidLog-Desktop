using System.IO.Compression;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 诊断包导出（<c>AGENTS.md</c> §6）。
/// </summary>
public class DiagnosticsPackageTests
{
    [Fact]
    public async Task 包里含日志设置与环境_不含任何视频()
    {
        using var dir = new TempDir();
        var layout = new DataLayout(dir.Path);
        layout.EnsureCreated();

        await File.WriteAllTextAsync(
            Path.Combine(layout.LogDirectory, "vidlog-20260922-100000.log"), "一条日志");
        await File.WriteAllTextAsync(layout.IndexPath, """{"Waybill":"SF1"}""" + "\n" + """{"Waybill":"SF2"}""");

        var package = Build(layout);
        var zip = await package.ExportAsync(dir.Out);

        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("settings.json", names);
        Assert.Contains("environment.txt", names);
        Assert.Contains("index-summary.txt", names);
        Assert.Contains("logs/vidlog-20260922-100000.log", names);

        // 绝不带视频 —— 体积发不出去，而且那是用户的证据（§3.7.5 的精神）。
        Assert.DoesNotContain(names, n =>
            n.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 索引只给摘要不给逐条明细()
    {
        using var dir = new TempDir();
        var layout = new DataLayout(dir.Path);
        layout.EnsureCreated();

        // 逐条明细里带着真实单号（PII）—— 报个 bug 不该把它送出去。
        await File.WriteAllTextAsync(layout.IndexPath,
            """{"Waybill":"真实客户单号123"}""" + "\n" + """{"Waybill":"另一个真实单号456"}""");

        var zip = await Build(layout).ExportAsync(dir.Out);

        using var archive = ZipFile.OpenRead(zip);
        var summary = await ReadEntryAsync(archive, "index-summary.txt");

        Assert.Contains("条数: 2", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("真实客户单号123", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 启动警告会进包()
    {
        using var dir = new TempDir();
        var layout = new DataLayout(dir.Path);
        layout.EnsureCreated();

        var zip = await Build(layout, warnings: ["找不到 FFmpeg", "回放服务没起来"])
            .ExportAsync(dir.Out);

        using var archive = ZipFile.OpenRead(zip);
        var warnings = await ReadEntryAsync(archive, "warnings.txt");

        Assert.Contains("找不到 FFmpeg", warnings, StringComparison.Ordinal);
        Assert.Contains("回放服务没起来", warnings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 工作区的会话清单会被收进来()
    {
        using var dir = new TempDir();
        var layout = new DataLayout(dir.Path);
        layout.EnsureCreated();

        // 排查「孤儿为什么没收尾」要靠它。
        var sessionDir = Path.Combine(layout.WorkspaceRoot, "sess-1");
        Directory.CreateDirectory(sessionDir);
        await File.WriteAllTextAsync(Path.Combine(sessionDir, "session.json"), """{"SessionId":"sess-1"}""");

        var zip = await Build(layout).ExportAsync(dir.Out);

        using var archive = ZipFile.OpenRead(zip);
        Assert.Contains(archive.Entries, e => e.FullName == "sessions/sess-1.json");
    }

    [Fact]
    public async Task 空数据目录也能导出_不抛()
    {
        using var dir = new TempDir();
        var layout = new DataLayout(dir.Path);
        layout.EnsureCreated();

        // 新装的机器就是这个样子：没有日志、没有索引、没有工作区。
        var zip = await Build(layout).ExportAsync(dir.Out);

        Assert.True(File.Exists(zip));
    }

    private static DiagnosticsPackage Build(DataLayout layout, IReadOnlyList<string>? warnings = null) =>
        new(new DiagnosticsSources(
            layout,
            AppSettings.Default,
            warnings ?? [],
            _ => Task.FromResult<IReadOnlyList<string>>(["OS: 测试", "ffmpeg: 测试"])));

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);

        await using var stream = entry!.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public string Out => System.IO.Path.Combine(Path, "out");

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
