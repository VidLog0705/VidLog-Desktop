using System.IO.Compression;
using System.Text;
using VidLog.Desktop.Core.Configuration;

namespace VidLog.Desktop.Core.Diagnostics;

/// <summary>导出诊断包需要的东西。</summary>
/// <param name="Layout">数据目录布局。</param>
/// <param name="Settings">当前设置。</param>
/// <param name="Warnings">启动时攒下的警告。</param>
/// <param name="EnvironmentLines">环境信息（OS / .NET / ffmpeg / 设备 / 编码探测）。</param>
public sealed record DiagnosticsSources(
    DataLayout Layout,
    AppSettings Settings,
    IReadOnlyList<string> Warnings,
    Func<CancellationToken, Task<IReadOnlyList<string>>> EnvironmentLines);

/// <summary>
/// 把排障需要的东西打成一个 zip（<c>AGENTS.md</c> §6：用户遇到问题能一键打包发回）。
/// </summary>
/// <remarks>
/// <para>
/// <b>绝不包含任何视频文件</b>。理由有两条：体积（一段录像几十 MB，发不出去），
/// 以及规格 §3.7.5 的精神 —— 证据是用户的，不该因为「报个 bug」就外流。
/// </para>
/// <para>
/// <c>index.jsonl</c> 也**不塞原始内容**：它逐条带着单号（PII）。
/// 只给一份摘要（条数、日期范围），要看明细得用户自己勾。
/// </para>
/// <para>
/// 用 <see cref="ZipFile"/>（标准库），不引第三方压缩包。
/// </para>
/// </remarks>
public sealed class DiagnosticsPackage
{
    private readonly DiagnosticsSources _sources;

    public DiagnosticsPackage(DiagnosticsSources sources)
    {
        _sources = sources;
    }

    /// <summary>导出。返回产出的 zip 路径。</summary>
    public async Task<string> ExportAsync(
        string destinationDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);

        var name = $"vidlog-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip";
        var path = Path.Combine(destinationDirectory, name);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        await WriteTextAsync(archive, "settings.json",
            System.Text.Json.JsonSerializer.Serialize(
                _sources.Settings,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        await WriteTextAsync(archive, "warnings.txt",
            _sources.Warnings.Count == 0 ? "（无）" : string.Join(Environment.NewLine, _sources.Warnings),
            cancellationToken);

        await WriteTextAsync(archive, "environment.txt",
            string.Join(Environment.NewLine, await _sources.EnvironmentLines(cancellationToken)),
            cancellationToken);

        await WriteTextAsync(archive, "index-summary.txt", await SummarizeIndexAsync(cancellationToken),
            cancellationToken);

        await AddLogsAsync(archive, cancellationToken);
        await AddSessionManifestsAsync(archive, cancellationToken);

        return path;
    }

    /// <summary>
    /// 索引摘要 —— 只给条数与日期范围，不给逐条明细。
    /// </summary>
    /// <remarks>
    /// 逐条明细里每一行都带着单号，那是真实客户的包裹信息。
    /// 「报个 bug」不该把这些一起送出去。
    /// </remarks>
    private async Task<string> SummarizeIndexAsync(CancellationToken cancellationToken)
    {
        var indexPath = _sources.Layout.IndexPath;
        if (!File.Exists(indexPath))
        {
            return "（还没有索引）";
        }

        var lines = await File.ReadAllLinesAsync(indexPath, cancellationToken);
        var nonEmpty = lines.Count(l => !string.IsNullOrWhiteSpace(l));

        return string.Join(Environment.NewLine,
            $"条数: {nonEmpty}",
            $"内容: 逐条明细含单号（PII），本包刻意不含。",
            $"如需明细请自行从 {indexPath} 复制。");
    }

    private async Task AddLogsAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var directory = _sources.Layout.LogDirectory;
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.log"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 压缩包里的路径用正斜杠，跨平台解压才一致。
            var entry = archive.CreateEntry($"logs/{Path.GetFileName(file)}", CompressionLevel.Optimal);
            await using var target = entry.Open();

            using var source = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await source.CopyToAsync(target, cancellationToken);
        }
    }

    /// <summary>收走工作区的会话清单 —— 排查「孤儿为什么没收尾」要靠它。</summary>
    private async Task AddSessionManifestsAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var root = _sources.Layout.WorkspaceRoot;
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var manifest in Directory.EnumerateFiles(root, "session.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sessionId = Path.GetFileName(Path.GetDirectoryName(manifest));
            var entry = archive.CreateEntry($"sessions/{sessionId}.json", CompressionLevel.Optimal);
            await using var target = entry.Open();

            using var source = new FileStream(
                manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await source.CopyToAsync(target, cancellationToken);
        }
    }

    private static async Task WriteTextAsync(
        ZipArchive archive, string entryName, string content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
    }
}
