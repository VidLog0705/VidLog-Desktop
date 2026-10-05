namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 启动时收尾孤儿分段。
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
/// </remarks>
public sealed class OrphanRecovery
{
    private readonly RecordingWorkspace _workspace;
    private readonly SessionFinalizer _finalizer;

    public OrphanRecovery(RecordingWorkspace workspace, SessionFinalizer finalizer)
    {
        _workspace = workspace;
        _finalizer = finalizer;
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
}
