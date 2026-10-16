using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 启动时收拾 <c>work/</c>：收尾孤儿分段，另外收掉**已经归档成功却还留着**的源目录。
/// </summary>
/// <remarks>
/// <para>
/// 规格 §3.1.1 / §8：进程被系统杀死或掉电后，**重启后必须能自动收尾孤儿分段**
/// （封闭文件、写入索引），不产生无法播放的半成品。
/// </para>
/// <para>
/// <b>这个类的全部意义在于「它没有自己的收尾逻辑」。</b>
/// 孤儿收尾不是特例，只是 <see cref="StopReason.ProcessKilled"/> 这个停法 ——
/// 走的是同一个 <see cref="SessionFinalizer"/>。这就是不变量 I9 在重启路径上的落点：
/// 如果哪天有人在这里加了一段「孤儿专用」的收尾代码，I9 就破了。
/// </para>
/// <para>
/// <see cref="SweepFinalizedAsync"/> 是**反过来的那一半**（已经是成品、只是源没丢），
/// 它**不碰归档层、不重传、也不写索引**，只删本机那份已经被归档层保住的源。
/// </para>
/// </remarks>
public sealed class OrphanRecovery
{
    private readonly RecordingWorkspace _workspace;
    private readonly SessionFinalizer _finalizer;
    private readonly IRecordingIndex _index;
    private readonly IArchiveBackend _archive;
    private readonly IAppLogger? _logger;

    public OrphanRecovery(
        RecordingWorkspace workspace,
        SessionFinalizer finalizer,
        IRecordingIndex index,
        IArchiveBackend archive,
        IAppLogger? logger = null)
    {
        _workspace = workspace;
        _finalizer = finalizer;
        _index = index;
        _archive = archive;
        _logger = logger;
    }

    /// <summary>
    /// 找出所有孤儿会话并收尾。
    /// </summary>
    /// <remarks>
    /// **只有成功的才打上 finalized 标记。** 失败的保持孤儿身份，下次启动会重试 ——
    /// 因为源 MKV 一定还在（I2），它可能只是这次碰上了瞬时故障。
    /// <para>
    /// 已知取舍：如果某个分段的 MKV 彻底损坏，它会在**每次启动**时重试一遍。
    /// 代价是几次 FFmpeg 调用，换来的是「可恢复的录像不会被当成不可恢复丢掉」。
    /// 如果将来孤儿积压到影响启动，再给 manifest 加一个尝试计数即可。
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<FinalizeOutcome>> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var orphans = await _workspace.ListOrphansAsync(cancellationToken);
        var outcomes = new List<FinalizeOutcome>(orphans.Count);

        foreach (var orphan in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await _finalizer.FinalizeAsync(
                orphan.SessionId,
                orphan.Waybill,
                orphan.SourceDeviceId,
                orphan.Segments,
                StopReason.ProcessKilled,
                cancellationToken,
                // ⚠️ **这一行是孤儿与正常收尾唯一的输入差别**，而它曾经是缺的：
                // 正常停录那边传 `_options.Spec`，这里不传 ⇒ **同一条收尾路径
                // 产出两种记录** —— 孤儿条目的编码 / 分辨率 / 方向三栏全空。
                // 类注释那句「这个类的全部意义在于它没有自己的收尾逻辑」说的是**结构**，
                // 而走岔的是**喂进去的输入**。（2026-10-02 检索页实证：
                // `…041052-…-000` 有 `Codec=H265 / Resolution=Uhd4K`，
                // `…042113-…-000~003` 三项全空。）
                //
                // ⚠️ 规格从 `session.json` 读回来，**不从内存里找** ——
                // 进程是**被杀**的，内存里那份早没了。老 manifest 没记它，
                // 那时是 null，与从前一样（宁可空着，也不编一个）。
                spec: orphan.Spec);

            if (outcome.Succeeded)
            {
                await _workspace.MarkFinalizedAsync(orphan.SessionId, cancellationToken);

                // ★ T21：与正常停录那边同一条判据（收尾成功 **且** 发了归档层）——
                // 否则这份源 MKV 会一直留在 `work/` 里，再也没人碰它。
                if (outcome.ArchiveComplete)
                {
                    _workspace.DiscardSessionDirectory(orphan.SessionId);
                }
            }

            outcomes.Add(outcome);
        }

        return outcomes;
    }

    /// <summary>
    /// 收掉「**已经归档成功、却还留着工作目录**」的会话源分段
    /// （需求方 2026-10-16 裁定「方案 a」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>为什么收尾那一刻判不了</b>：那一刻归档层可能还没成 ——「启用自动上传」关着，
    /// 或者网盘正传不上去 ⇒ <c>FinalizeOutcome.ArchiveComplete</c> 为假 ⇒
    /// 源分段整份留在 <c>work/</c>（那是 I2「宁可多占地方」的设计后果，不是漏做）。
    /// 而**事后补传成功那一头没有人回来收**：
    /// <see cref="RecordingWorkspace.ListOrphansAsync"/> 见 <c>finalized.json</c> 就跳过它
    /// ⇒ 它**永久**占着地方（2026-10-16 实测：14:14 那场 173 MB 躺了两天，
    /// 连带 10-13 两场 29 MB）。
    /// </para>
    /// <para>
    /// ⚠️ <b>判据是回查归档层，不是读 <c>published.jsonl</c></b>：事后补传走的是
    /// 上传队列，**不经过中继** —— 写那份流水的只有 <c>ArchiveRelay</c>（收尾那条路）。
    /// 照它判会把「其实已经传上去了」的判成没传，于是永远清不掉。
    /// </para>
    /// <para>
    /// ⚠️ <b>任何一处查不实就一个字节都不动</b>（I8 的同一条精神）：索引里找不到这条
    /// 会话、有一条成品在归档层上回查不到、或者回查本身出错（网断了、凭据废了）
    /// —— 三种情况全部只记日志、**不删**。会动手的只有「索引里每一条成品都真找到了」
    /// 的会话。所以这个清扫的失败方向恒为「多占地方」，不是「误删唯一副本」。
    /// </para>
    /// <para>
    /// ⚠️ 删的只是**源 MKV**（<c>&lt;root&gt;/&lt;会话&gt;/</c> 整份），成品在归档层上
    /// —— 索引、标签、清理流水一概不动，回放与检索读的都不是这个目录。
    /// </para>
    /// </remarks>
    public async Task SweepFinalizedAsync(CancellationToken cancellationToken = default)
    {
        var sessionIds = _workspace.ListFinalizedSessionIds();
        if (sessionIds.Count == 0)
        {
            return;
        }

        // ⚠️ 整段包一层：这是**启动路径**，而它是纯粹的「顺手多腾点地方」。
        // 索引读不出来、回查抛异常、目录被别的进程占着 —— 任何一种都不该
        // 把整台机器卡在「起不来」上。这一趟不收拾，下一次启动还会再试一遍。
        try
        {
            // 索引只读**一遍**：一台积压了很多场的机器上，每个会话各读一次就是几百次
            // 全量读盘，而数据源明明是同一份（与右栏那几处「不重算一遍」同一个理由）。
            var bySession = (await _index.LoadAllAsync(cancellationToken))
                .GroupBy(one => one.SessionId, StringComparer.Ordinal)
                .ToDictionary(one => one.Key, one => one.ToList(), StringComparer.Ordinal);

            var swept = 0;

            foreach (var sessionId in sessionIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!bySession.TryGetValue(sessionId, out var entries) || entries.Count == 0)
                {
                    // 索引里没有它 ⇒ 回查不了 ⇒ 不动。**要记**：这一条会永远占着地方，
                    // 而事后看到「work/ 里有一堆东西没收掉」时要能问出来为什么。
                    // （只说「查不到」，不替它猜「是不是没传上去」—— 那会儿正查不着。）
                    _logger?.Log(LogLevel.Warn, "录制",
                        $"会话 {sessionId} 收尾过、源分段还在 work/ 里，"
                        + "但索引里查不到它，回查不了 ⇒ 不动它");
                    continue;
                }

                var at = string.Empty;

                var allThere = true;
                foreach (var entry in entries)
                {
                    var result = await _archive.VerifyAsync(entry.Location, cancellationToken);

                    if (!result.Exists)
                    {
                        allThere = false;

                        // ⚠️ 两种「不在」要分得开：查得到但确实没有（还没传上去）、
                        // 与**根本查不了**（网断了）。后者才是意外，前者是等它补传。
                        at = result.CouldNotVerify
                            ? $"回查归档层出错：{result.FailureReason}"
                            : $"归档层上没有 {entry.Location.Value}";
                        break;
                    }
                }

                if (!allThere)
                {
                    _logger?.Log(LogLevel.Info, "录制",
                        $"会话 {sessionId} 的成品还没全在归档层上（{at}）⇒ 源分段先留着");
                    continue;
                }

                _workspace.DiscardSessionDirectory(sessionId);
                swept++;
            }

            if (swept > 0)
            {
                _logger?.Log(LogLevel.Info, "录制",
                    $"清掉 {swept} 个会话的工作目录：它们的成品都已经在归档层上了"
                    + "（收尾当时还没传成，是事后补传上去的）");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Log(LogLevel.Warn, "录制",
                $"收拾 work/ 里那些「已经归档成功」的源分段时出错，这一趟不动它们：{ex.Message}");
        }
    }
}
