using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Cleanup;

// ── 归档层的回查实现在 `ArchiveBackends.cs` ──
//
// 原来这里有一个 `LocalFolderArchiveBackend`（只认本机磁盘）。
// 2026-09-27 起换成 `DirectoryArchiveBackend`：**同一份代码**吃本机磁盘、
// NAS、挂载网络驱动器三种（规格 §3.4.6 点名的「目录型」）。
// 本机那一档的语义没变（发布是空操作、回查看文件在不在），
// 而「归档层是本机时不删」那条 gate 仍在 `CleanupExecutor.CanCleanup` 里。

/// <summary>一次清理的结果。</summary>
/// <param name="Deleted">真删掉的。</param>
/// <param name="Refused">回查不通过、因此**没删**的，以及原因。</param>
public sealed record CleanupReport(
    IReadOnlyList<RecordingEntry> Deleted,
    IReadOnlyList<(RecordingEntry Entry, string Why)> Refused)
{
    public long FreedBytes { get; init; }
}

/// <summary>
/// 执行清理。
/// </summary>
/// <remarks>
/// <para>
/// <b>I8 的唯一落点</b>：每一条候选在删之前都要回查归档层；
/// 回查不通过（不存在 **或查不了**）就**绝不删除**。
/// 「查不了」也必须算不通过 —— 网络断了的时候当成「不存在」，
/// 删的可能是最后一份副本（I2）。
/// </para>
/// <para>
/// <b>归档层是本机磁盘时根本不删</b>（规格 §3.5.1：那时该副本是唯一副本）。
/// 这条由 <see cref="CanCleanup"/> 把关，而不是靠调用方记得别调。
/// </para>
/// </remarks>
public sealed class CleanupExecutor
{
    private readonly IArchiveBackend _archive;
    private readonly CleanupAuditLog _audit;
    private readonly IAppLogger _logger;
    private readonly string _archiveRoot;

    public CleanupExecutor(
        IArchiveBackend archive, string archiveRoot, CleanupAuditLog audit, IAppLogger logger)
    {
        _archive = archive;
        _archiveRoot = archiveRoot;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// 这个归档层允许清理吗。
    /// </summary>
    /// <remarks>
    /// 归档层 = 本机磁盘时**不允许** —— 那时它就是唯一副本。
    /// 界面上应当据此**根本不显示**清理选项，而不是显示了再禁用。
    /// </remarks>
    public bool CanCleanup => _archive.Kind != ArchiveBackendKind.LocalDisk;

    public async Task<CleanupReport> ExecuteAsync(
        CleanupPlan plan, CancellationToken cancellationToken = default)
    {
        var deleted = new List<RecordingEntry>();
        var refused = new List<(RecordingEntry, string)>();
        long freed = 0;

        if (!CanCleanup)
        {
            // 不该走到这里 —— 调用方应当先看 CanCleanup。
            // 但真走到这里也**绝不删**：宁可什么都不做，也不能删掉唯一副本。
            _logger.Log(LogLevel.Warn, "清理",
                $"归档层是{_archive.Kind}，按规格 §3.5.1 不提供清理。已忽略本次清理请求。");

            return new CleanupReport([], [.. plan.Candidates.Select(c => (c.Entry, "归档层是唯一副本，不清理"))]);
        }

        foreach (var candidate in plan.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ★ I8：删之前必须回查归档层。
            ArchiveVerifyResult verification;
            try
            {
                verification = await _archive.VerifyAsync(candidate.Location, cancellationToken);
            }
            catch (Exception ex)
            {
                verification = new ArchiveVerifyResult(false, $"回查抛异常：{ex.Message}");
            }

            if (!verification.Exists || verification.CouldNotVerify)
            {
                // ★ 回查不通过 ⇒ 绝不删除。**包括查不了的情况** ——
                // 把它当成「不存在」会删掉最后一份副本（I2）。
                var why = verification.CouldNotVerify
                    ? $"回查不了（{verification.FailureReason}），按 I8 不删"
                    : "归档层上找不到这一份，按 I8 不删";

                refused.Add((candidate.Entry, why));
                _logger.Log(LogLevel.Warn, "清理", $"拒绝清理 {candidate.Entry.Waybill.Value}：{why}");
                await _audit.AppendAsync(new CleanupAuditRecord(
                    candidate.Entry.EvidenceId, "refused", why, null));

                continue;
            }

            var path = Path.Combine(_archiveRoot, candidate.Location.Value);
            try
            {
                var size = new FileInfo(path).Length;

                // 用回收站而不是直接抹 —— 清理是不可逆的动作，
                // 多一层「还能捞回来」比省那点空间重要得多。
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                deleted.Add(candidate.Entry);
                freed += size;

                await _audit.AppendAsync(new CleanupAuditRecord(
                    candidate.Entry.EvidenceId, "deleted", candidate.Why, null));

                _logger.Log(LogLevel.Info, "清理",
                    $"已清理 {candidate.Entry.Waybill.Value}（{candidate.Why}）",
                    new Dictionary<string, object?> { ["字节"] = size });
            }
            catch (Exception ex)
            {
                // 删失败**保留**，不重试、不升级 —— 下次计划会再看到它。
                refused.Add((candidate.Entry, $"删除失败：{ex.Message}"));

                await _audit.AppendAsync(new CleanupAuditRecord(
                    candidate.Entry.EvidenceId, "failed", candidate.Why, ex.Message));
            }
        }

        return new CleanupReport(deleted, refused) { FreedBytes = freed };
    }
}

/// <summary>一条清理审计记录。</summary>
/// <param name="EvidenceId">哪条录像。</param>
/// <param name="Action">deleted / refused / failed。</param>
/// <param name="Reason">为什么。</param>
/// <param name="FailureReason">失败时的原因。</param>
public sealed record CleanupAuditRecord(
    string EvidenceId, string Action, string Reason, string? FailureReason)
{
    public string At { get; init; } = DateTimeOffset.UtcNow.ToString("O");
}

/// <summary>
/// 清理审计（JSON Lines，形态与索引一致）。
/// </summary>
/// <remarks>
/// 规格 §6.2 要求「禁止静默清理 —— 清理前必须预告，且保留可查的清理记录」。
/// 这就是那份记录：删了什么、拒了什么、各是为什么。
/// <para>
/// 追加写，不重写 —— 与录像索引同一理由：不存在「重写中崩溃导致整库损坏」。
/// </para>
/// </remarks>
public sealed class CleanupAuditLog
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _path;

    public CleanupAuditLog(string path)
    {
        _path = path;
    }

    public async Task AppendAsync(
        CleanupAuditRecord record, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(record, Options) + Environment.NewLine;

        try
        {
            await File.AppendAllTextAsync(_path, line, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 审计写不进去**不能**让清理继续 —— 规格 §6.2 禁止静默清理，
            // 而没有记录 = 静默。所以这里往上抛，由调用方决定怎么办。
            throw new InvalidOperationException(
                $"清理审计写不进去（{ex.Message}），按「禁止静默清理」中止。", ex);
        }
    }

    public async Task<IReadOnlyList<CleanupAuditRecord>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var records = new List<CleanupAuditRecord>();

        foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<CleanupAuditRecord>(line, Options);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // 半截的一行跳过 —— 不能因为一行坏了就丢掉整份审计。
            }
        }

        return records;
    }
}
