using VidLog.Desktop.Core.Export;
using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 交付原视频（规格 §3.7）。**不转码、不压缩、不裁剪，也不打码。**
/// </summary>
public class EvidenceExporterTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vidlog-export-" + Guid.NewGuid().ToString("N"));

        public string ArchiveRoot => System.IO.Path.Combine(Path, "archive");

        public TempDir()
        {
            Directory.CreateDirectory(ArchiveRoot);
        }

        /// <summary>往归档目录里放一条「录像」，返回它的索引条目。</summary>
        public RecordingEntry Put(string evidenceId = "e1", string bytes = "video-bytes")
        {
            var relative = $"2026/09/27/SF1000000001/{evidenceId}.mp4";
            var full = System.IO.Path.Combine(
                ArchiveRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, bytes);

            return new RecordingEntry(
                evidenceId, "sess-1", WaybillNumber.Parse("SF1000000001"),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(1), RelativePath.Parse(relative),
                ContentHash.Parse(new string('a', 64)), "device-1");
        }

        public void Dispose()
        {
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（2026-09-27 实测：会话还在
            // 收尾时删含 `.ass` / `.mkv` 的临时目录）。清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ─────────────────────────────────────────────
    // ★ 不得落在归档根之下（规格 §3.7 点名的禁止项）
    // ─────────────────────────────────────────────

    [Fact]
    public void 归档根之下的路径一律不许用_含相等()
    {
        // 规格原话：导出到用户自选路径，「**不能是电脑端存放录像的那个路径**」。
        // 落在里面的话，导出件会被当成归档层里的一份、被算进容量、
        // 还会跟同名录像撞上 —— 而它**不是录像**（I7）。
        using var dir = new TempDir();
        var exporter = new EvidenceExporter(dir.ArchiveRoot);

        Assert.True(exporter.IsInsideArchiveRoot(dir.ArchiveRoot));            // 相等也不行
        Assert.True(exporter.IsInsideArchiveRoot(
            System.IO.Path.Combine(dir.ArchiveRoot, "2026", "x.mp4")));
        Assert.True(exporter.IsInsideArchiveRoot(
            System.IO.Path.Combine(dir.ArchiveRoot, "..", "archive", "x.mp4")));   // 绕一圈也算
    }

    [Fact]
    public void 归档根之外的路径可以用()
    {
        using var dir = new TempDir();
        var exporter = new EvidenceExporter(dir.ArchiveRoot);

        Assert.False(exporter.IsInsideArchiveRoot(System.IO.Path.Combine(dir.Path, "导出", "x.mp4")));
        Assert.False(exporter.IsInsideArchiveRoot(System.IO.Path.Combine(dir.Path, "archive2", "x.mp4")));
    }

    [Fact]
    public async Task 导出到归档目录里会被拒_而且说得出为什么()
    {
        using var dir = new TempDir();
        var entry = dir.Put();
        var exporter = new EvidenceExporter(dir.ArchiveRoot);

        var result = await exporter.ExportAsync(
            entry, System.IO.Path.Combine(dir.ArchiveRoot, "导出.mp4"));

        Assert.False(result.Exported);
        Assert.Contains("归档目录", result.FailureReason);
    }

    // ─────────────────────────────────────────────
    // 原样交付
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 导出是逐字节一样的原视频()
    {
        // 规格 §3.7.1：「无损完整」在这里只有一个意思 —— **不压缩、不转码、不裁剪**。
        // 所以判据是「内容一模一样」，不是「能播」。
        using var dir = new TempDir();
        var entry = dir.Put(bytes: "这是一段录像的字节");
        var exporter = new EvidenceExporter(dir.ArchiveRoot);
        var target = System.IO.Path.Combine(dir.Path, "交付", "SF1000000001.mp4");

        var result = await exporter.ExportAsync(entry, target);

        Assert.True(result.Exported);
        Assert.Equal("这是一段录像的字节", await File.ReadAllTextAsync(target));

        // 归档那一份**一个字都没动**（导出是复制，不是搬走）。
        var source = System.IO.Path.Combine(
            dir.ArchiveRoot, entry.Location.Value.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task 目标目录不存在时会建出来()
    {
        using var dir = new TempDir();
        var entry = dir.Put();
        var exporter = new EvidenceExporter(dir.ArchiveRoot);

        var target = System.IO.Path.Combine(dir.Path, "很", "深", "的", "目录", "x.mp4");

        Assert.True((await exporter.ExportAsync(entry, target)).Exported);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task 同名文件已存在时不覆盖()
    {
        // 用户选的可能是他自己的一份东西，我们没有任何理由替他决定「覆盖掉它」。
        using var dir = new TempDir();
        var entry = dir.Put();
        var exporter = new EvidenceExporter(dir.ArchiveRoot);

        var target = System.IO.Path.Combine(dir.Path, "x.mp4");
        await File.WriteAllTextAsync(target, "用户自己的东西");

        var result = await exporter.ExportAsync(entry, target);

        Assert.False(result.Exported);
        Assert.Contains("同名", result.FailureReason);
        Assert.Equal("用户自己的东西", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task 源文件不在时给得出原因()
    {
        using var dir = new TempDir();
        var entry = dir.Put();
        var exporter = new EvidenceExporter(dir.ArchiveRoot);

        File.Delete(System.IO.Path.Combine(
            dir.ArchiveRoot, entry.Location.Value.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        var result = await exporter.ExportAsync(entry, System.IO.Path.Combine(dir.Path, "x.mp4"));

        Assert.False(result.Exported);
        Assert.Contains("找不到了", result.FailureReason);
    }
}
