using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Configuration;
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
    private readonly StorageLocations _storage;

    /// <param name="archiveRoot">只认一个根的那种写法（老调用点与测试最常用的那种）。</param>
    public CleanupExecutor(
        IArchiveBackend archive, string archiveRoot, CleanupAuditLog audit, IAppLogger logger)
        : this(archive, StorageLocations.Single(archiveRoot), audit, logger)
    {
    }

    /// <param name="storage">
    /// 本机这一份散落在哪些根上（设计图 `_43` 的多磁盘）。
    /// </param>
    public CleanupExecutor(
        IArchiveBackend archive, StorageLocations storage, CleanupAuditLog audit, IAppLogger logger)
    {
        _archive = archive;
        _storage = storage;
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

    /// <summary>
    /// 安全阀的门槛（T6）：拟删条数**超过计划里的一半**就算异常。
    /// </summary>
    /// <remarks>
    /// 形态借自 Frigate <c>POST /media/sync</c> 的 <c>SAFETY_THRESHOLD = 0.5</c>。
    /// </remarks>
    public const double SafetyThreshold = 0.5;

    /// <summary>
    /// 这份计划会触发安全阀吗。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>一次要删掉计划里一半以上的录像，通常不是真的要删这么多</b>，
    /// 而是算错了：保留期被改成了「不保留」、索引读漏了一截、
    /// 或者【按空间释放】碰上一个探错的剩余空间。而清理是**不可逆的** ——
    /// 一个索引 bug 就能把盘上所有录像清光。这条闸的成本是几行，挡住的是那件事。
    /// </para>
    /// <para>
    /// ⚠️ <b>公开出来是给界面用的</b>：好让它提前把「这一次要删掉一半以上」
    /// 摆到用户眼前，而不是让用户点了「是」之后才发现。但
    /// <b>执行层这道闸一道都不能少</b> —— 界面那道是「不让用户在不知情的情况下
    /// 点下去」，这道是「就算被绕过了也不许删」。与 <see cref="CanCleanup"/>
    /// 是同一种两道关的分工。
    /// </para>
    /// <para>
    /// 分母是「这份计划看过的全部录像」= 候选 + 豁免（两者互斥，加起来就是全集），
    /// 所以调用方不用另外报一个总数。
    /// </para>
    /// <para>
    /// ⚠️ <b>只删 1 条时不算触发</b>：盘上只有 1 条录像时比例必然是 100%（1/1），
    /// 而那不是异常信号。异常信号是「**一次删掉一大片**」，所以从一个下限起算。
    /// 这个下限还有个副作用是好的：单条的清理不会被平白拦一道。
    /// </para>
    /// </remarks>
    public static bool TripsSafetyValve(CleanupPlan plan)
    {
        var deleting = plan.Candidates.Count;
        var total = deleting + plan.Exempted.Count;

        return deleting >= 2 && deleting > total * SafetyThreshold;
    }

    /// <param name="force">
    /// 覆盖安全阀（<see cref="TripsSafetyValve"/>）。**只有用户在界面上看过
    /// 「这一次要删掉一半以上」那道再次确认之后才该传 true。**
    /// </param>
    /// <remarks>
    /// ⚠️ <paramref name="force"/> 排在 <paramref name="cancellationToken"/> **后面**
    /// 是刻意的：仓里现有的调用点全是 <c>ExecuteAsync(plan)</c> 这种位置传参，
    /// 插在前面会把 <c>CancellationToken</c> 错绑到 <c>bool</c> 上，7 处调用点一起编译不过。
    /// </remarks>
    public async Task<CleanupReport> ExecuteAsync(
        CleanupPlan plan, CancellationToken cancellationToken = default, bool force = false)
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

        // ★ T6 安全阀：一次要删掉计划里一半以上的录像 ⇒ **一条都不删**。
        // 形态与上面那道「归档层是本机」的闸一致：宁可什么都不做，也不能删错。
        if (TripsSafetyValve(plan))
        {
            var total = plan.Candidates.Count + plan.Exempted.Count;

            if (!force)
            {
                var why = $"这次要删 {plan.Candidates.Count} / {total} 条（超过一半），"
                    + "已按安全阀中止，一条都没删。";

                // ⚠️ 这里**不写审计**：一条都没删，`deleted` / `refused` / `failed`
                // 三个动作没有一个贴得上，硬写一批 aborted 反而是往审计里灌噪音。
                // 上面那道「归档层是本机」的闸同样只记日志、不写审计 —— 保持一致。
                // 「禁止静默清理」管的是**删了却不留痕**，这次什么都没删。
                // 用户看得到的地方有两处：这条 Warn 日志，以及返回去的 Refused 列表
                // （界面会照着它说「N 条仍在盘上」）。
                _logger.Log(LogLevel.Warn, "清理",
                    $"安全阀拦下一次清理：拟删 {plan.Candidates.Count} / {total} 条（超过一半），"
                    + "一条都没删。");

                return new CleanupReport([], [.. plan.Candidates.Select(c => (c.Entry, why))]);
            }

            // 覆盖要留痕 —— 事后只看到一串 deleted 的话，分不清那次是正常到期清理，
            // 还是有人点头放行了一次「删掉一半以上」。
            _logger.Log(LogLevel.Warn, "清理",
                $"安全阀被覆盖：拟删 {plan.Candidates.Count} / {total} 条（超过一半），"
                + "按用户的再次确认继续。");
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

            // ⚠️ 本机这一份可能在**任何一个**保存位置上（多磁盘，设计图 `_43`）。
            // 拼第一个根而不去找的话，另一块盘上的那些会「删不掉」，
            // 而报告里写的是「删除失败」—— 用户会以为是权限问题，去查错的方向。
            if (_storage.Locate(candidate.Location.Value) is not { } found)
            {
                refused.Add((candidate.Entry, "本机已经找不到这一份了（可能被手工挪走了）"));
                _logger.Log(LogLevel.Warn, "清理",
                    $"跳过 {candidate.Entry.Waybill.Value}：本机找不到这一份");

                continue;
            }

            var path = found.Path;

            // ★ T19：**审计写在删之前**。
            //
            // 原来是删完才写 `deleted`，于是写失败时：盘上少了一份、审计里一条都没有，
            // 而下面那个 catch 还会补一条 `failed` —— 报告说「删除失败」，
            // 文件其实已经进了回收站。**先写意图、再动手**，才不会出现「删了却没痕」。
            try
            {
                await _audit.AppendAsync(new CleanupAuditRecord(
                    candidate.Entry.EvidenceId, "deleting", candidate.Why, null));
            }
            catch (Exception ex)
            {
                // 意图都记不下 ⇒ 这一条不删。规格 §6.2 禁止静默清理：
                // 没有记录 = 静默，而这一步是真的要把东西删掉。
                refused.Add((candidate.Entry, $"清理审计写不进去（{ex.Message}），按「禁止静默清理」不删"));
                _logger.Log(LogLevel.Warn, "清理",
                    $"没删 {candidate.Entry.Waybill.Value}：清理审计写不进去（{ex.Message}），"
                    + "按「禁止静默清理」中止这一条");

                continue;
            }

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

                await TryAppendAsync(new CleanupAuditRecord(
                    candidate.Entry.EvidenceId, "deleted", candidate.Why, null));

                _logger.Log(LogLevel.Info, "清理",
                    $"已清理 {candidate.Entry.Waybill.Value}（{candidate.Why}）",
                    new Dictionary<string, object?> { ["字节"] = size });
            }
            catch (Exception ex)
            {
                // 删失败**保留**，不重试、不升级 —— 下次计划会再看到它。
                refused.Add((candidate.Entry, $"删除失败：{ex.Message}"));

                await TryAppendAsync(new CleanupAuditRecord(
                    candidate.Entry.EvidenceId, "failed", candidate.Why, ex.Message));

                // ⚠️ 日志也要记一条（`AGENTS.md` §6「清理动作」）——
                // 上面那两处（拒绝清理 :107、删除成功 :132）都记了，只有这里没记，
                // 而「**该删的删不掉**」恰恰是最该被人看见的一种：磁盘会一直满着，
                // 而审计表在 `%LOCALAPPDATA%` 里，没人在事故现场会去翻它。
                _logger.Log(LogLevel.Warn, "清理",
                    $"删不掉 {candidate.Entry.Waybill.Value}（{candidate.Why}）：{ex.Message}");
            }
        }

        return new CleanupReport(deleted, refused) { FreedBytes = freed };
    }

    /// <summary>补一条「已经动过手了」的审计 —— **写不进去只记日志，不再往上抛**。</summary>
    /// <remarks>
    /// T19：删除之后的这一步再抛，只会把这一条**真实的**结果盖掉
    /// （删成功却报成失败、删失败又报成别的），而文件已经动过了、重来一次也不会有别的结果。
    /// 与删除**之前**那一条不同：那一条写不进去就**不删**，因为删了就再也补不上了。
    /// </remarks>
    private async Task TryAppendAsync(CleanupAuditRecord record)
    {
        try
        {
            await _audit.AppendAsync(record);
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warn, "清理",
                $"审计补记不上 {record.EvidenceId}（{record.Action}）：{ex.Message}");
        }
    }
}

/// <summary>一条清理审计记录。</summary>
/// <param name="EvidenceId">哪条录像。</param>
/// <param name="Action">
/// <c>deleting</c>（意图，写在动手之前）/ <c>deleted</c>（真删掉了）/
/// <c>refused</c>（回查不通过，没删）/ <c>failed</c>（动手了但没删掉）。
/// 一次成功的清理会留下 <c>deleting</c> + <c>deleted</c> 两条。
/// </param>
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
        return (await LoadPageAsync(cancellationToken)).Records;
    }

    /// <summary>
    /// 与 <see cref="LoadAllAsync"/> 同一件事，但**把读不动的行数也带出来**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 上面那个方法**静默跳过**坏行（那是对的：不能因为一行坏了就丢掉整份审计）。
    /// 但 T24 的「清理流水」窗口是给人看的，**静默跳过 = 悄悄藏了几条**，
    /// 而这个窗口存在的全部意义就是「不许静默」。所以给界面另开一个入口，
    /// 让它能把「另有几行读不动」也说出来。
    /// <para>
    /// 两个方法共用一条实现，不是两份 —— 否则哪天改了解析，只有一边会跟着改。
    /// </para>
    /// </remarks>
    public async Task<CleanupAuditPage> LoadPageAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new CleanupAuditPage([], 0);
        }

        var records = new List<CleanupAuditRecord>();
        var unreadable = 0;

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
                else
                {
                    unreadable++;
                }
            }
            catch (JsonException)
            {
                // 半截的一行跳过 —— 不能因为一行坏了就丢掉整份审计。
                // 但**要数出来**：界面得告诉用户「这里少了几条」。
                unreadable++;
            }
        }

        return new CleanupAuditPage(records, unreadable);
    }
}

/// <summary>读出来的清理流水，外加**读不动的行数**。</summary>
/// <param name="Records">读出来的记录。</param>
/// <param name="UnreadableLines">坏掉 / 写了一半、被跳过的行数。</param>
public sealed record CleanupAuditPage(
    IReadOnlyList<CleanupAuditRecord> Records, int UnreadableLines);
