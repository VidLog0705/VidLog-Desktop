using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Export;

/// <summary>导出一次的结果。</summary>
/// <param name="Exported">文件有没有到用户选的那个位置。</param>
/// <param name="TargetPath">导出的目标（失败时为 null）。</param>
/// <param name="FailureReason">没成的原因（给人看的）。</param>
public sealed record ExportResult(bool Exported, string? TargetPath, string? FailureReason)
{
    public static ExportResult Ok(string path) => new(true, path, null);

    public static ExportResult Failed(string reason) => new(false, null, reason);
}

/// <summary>
/// 导出/交付**原视频**（规格 §3.7）。
/// </summary>
/// <remarks>
/// <para>
/// 规格原话（2026-09-24 需求变更）：「改掉分享连接，只分享视频本身**无损完整**视频」，
/// 并裁决：电脑端导出到**用户自选路径**（**不能是电脑端存放录像的那个路径**）。
/// </para>
/// <para>
/// ⚠️ <b>「无损完整」在本规格里只有一个意思：不压缩、不转码、不裁剪</b>
/// （§3.7.1）。所以这个类里**没有一次 ffmpeg 调用** —— 它是纯粹的**文件复制**。
/// 任何「顺手转个码」的念头都是把这条规格读错了。
/// </para>
/// <para>
/// ⚠️ <b>导出件不是录像</b>（I7 改写后的落点）：它**不进索引**，
/// 因此不参与检索、回放、归档回查与清理判定。这个类也不写任何索引/标签。
/// </para>
/// <para>
/// ⚠️ §3.6.6：**打码整条不做**。所以导出的成品里面单上的姓名、电话、地址
/// **会原样跟出去** —— 那是需求方权衡后的选择（证据完整优先），
/// 不许有人「顺手补上」一个打码。
/// </para>
/// </remarks>
public sealed class EvidenceExporter
{
    private readonly string _archiveRoot;
    private readonly IAppLogger _logger;

    /// <param name="archiveRoot">
    /// 归档根。**导出目标不得落在它下面** —— 那条判据是本类存在的理由之一（见
    /// <see cref="IsInsideArchiveRoot"/>）。
    /// </param>
    public EvidenceExporter(string archiveRoot, IAppLogger? logger = null)
    {
        _archiveRoot = archiveRoot;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 用户选的那个位置能不能用来放导出件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规格原话：「**不能是电脑端存放录像的那个路径**」。理由是它与**清理**有关：
    /// 导出件落在归档根之下的话，它会被
    /// ①当成归档层里的一份（回查会看见它）、②被「按空间清理」算进容量、
    /// ③下次同名单号导出时被覆盖或冲突。**导出件不是录像**（I7），
    /// 它必须落在录像那套体系之外。
    /// </para>
    /// <para>
    /// ⚠️ 判据是**路径包含关系**（含**相等**）—— 目标**正好等于**归档根
    /// 同样不行（那等于把导出件丢进录像堆里）。比较前两边都 <c>GetFullPath</c>，
    /// 挡住 <c>..</c> 与相对路径绕过去。
    /// </para>
    /// </remarks>
    public bool IsInsideArchiveRoot(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(_archiveRoot))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(_archiveRoot);
            var target = Path.GetFullPath(targetPath);

            if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            return target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 路径本身不合法 —— 当作「不能用」，由调用方去报错。
            return true;
        }
    }

    /// <summary>
    /// 把这一条**原样**交到 <paramref name="targetPath"/>。
    /// </summary>
    /// <remarks>
    /// 已存在同名文件时**不覆盖**：用户选的可能是他自己的一份东西，
    /// 而我们没有任何理由替他决定「覆盖掉它」。
    /// </remarks>
    public async Task<ExportResult> ExportAsync(
        RecordingEntry entry, string targetPath, CancellationToken cancellationToken = default)
    {
        var source = Path.Combine(_archiveRoot, entry.Location.Value);

        if (!File.Exists(source))
        {
            return ExportResult.Failed($"这一条在本机归档目录里已经找不到了：{source}");
        }

        if (IsInsideArchiveRoot(targetPath))
        {
            // 见 `IsInsideArchiveRoot` 的说明：这是规格点名的禁止项，
            // 而不是「顺手加的一条校验」。
            return ExportResult.Failed(
                "不能导出到归档目录里 —— 那会和录像本身混在一起（规格 §3.7）。请另选一个位置。");
        }

        try
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // ⚠️ **纯复制**：不转码、不压缩、不裁剪（规格 §3.7.1）。
            await using (var input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var output = new FileStream(
                targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            // AGENTS.md §6：交付动作要留痕（谁把哪一条交到哪儿去了）。
            _logger.Log(LogLevel.Info, "交付", $"导出了一条录像",
                new Dictionary<string, object?>
                {
                    ["单号"] = entry.Waybill.Value,
                    ["evidenceId"] = entry.EvidenceId,
                    ["目标"] = targetPath,
                });

            return ExportResult.Ok(targetPath);
        }
        catch (IOException) when (File.Exists(targetPath))
        {
            return ExportResult.Failed($"那个位置已经有一个同名文件了：{targetPath}（没有覆盖它）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ExportResult.Failed($"写不进去：{ex.Message}");
        }
    }
}
