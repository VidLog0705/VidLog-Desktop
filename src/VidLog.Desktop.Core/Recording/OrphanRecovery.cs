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
                cancellationToken);

            if (outcome.Succeeded)
            {
                await _workspace.MarkFinalizedAsync(orphan.SessionId, cancellationToken);
            }

            outcomes.Add(outcome);
        }

        return outcomes;
    }
}
