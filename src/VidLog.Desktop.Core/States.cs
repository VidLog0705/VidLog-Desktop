namespace VidLog.Desktop.Core;

/// <summary>
/// 录制会话状态。规格 §4.1。
/// </summary>
/// <remarks>
/// 转移表（由本枚举显式建模，M2 实现时不得绕过）：
/// <code>
/// 空闲 ──识别到单号──────> 录制中
/// 录制中 ──手动停止──────> 收尾中
/// 录制中 ──同码复扫──────> 收尾中
/// 录制中 ──静止超时──────> 收尾中
/// 录制中 ──时长兜底──────> 收尾中
/// 录制中 ──进程被杀/掉电──> (重启后) 孤儿收尾 ──> 已入库
/// 收尾中 ──校验通过──────> 已入库 ──> 进入上传队列
/// 收尾中 ──成品校验失败──> 收尾失败
/// </code>
/// 不变量 I9：任何进入「收尾中」的路径都必须走同一套收尾逻辑，不得有旁路。
/// </remarks>
public enum RecordingSessionState
{
    /// <summary>空闲 —— 尚未识别到单号。</summary>
    Idle,

    /// <summary>录制中。</summary>
    Recording,

    /// <summary>收尾中 —— 封文件、算哈希、写索引。</summary>
    Finalizing,

    /// <summary>已入库，等待进入上传队列。</summary>
    Indexed,

    /// <summary>
    /// 成品校验失败。规格 §4.1：不得当作正常入库。
    /// </summary>
    FinalizeFailed,
}

/// <summary>
/// 一次录制**为什么**停下来。
/// </summary>
/// <remarks>
/// 这个枚举的每一项都必须走<see cref="Recording.SessionFinalizer"/> 的同一条收尾路径
/// —— 这正是**不变量 I9**（录制收尾只有一条路径，不存在旁路）要保证的事。
/// <para>
/// 把「为什么停」做成显式枚举而不是散落的 if，是为了让「有没有旁路」这件事
/// 在编译期就能看出来：新增一种停法 = 往这里加一项，而收尾逻辑一行都不用改。
/// </para>
/// </remarks>
public enum StopReason
{
    /// <summary>用户手动停止。</summary>
    Manual,

    /// <summary>同码复扫（规格 §3.3.2 错码保护通过后）。</summary>
    SameWaybillRescan,

    /// <summary>画面静止超时（规格 §3.3.3）。</summary>
    StaticTimeout,

    /// <summary>时长兜底（规格 §3.3.4）。</summary>
    DurationFallback,

    /// <summary>进程被杀 / 掉电后，重启时的孤儿收尾（规格 §3.1.1）。</summary>
    ProcessKilled,

    /// <summary>存储将满，主动安全收尾（规格 §3.1.1）。</summary>
    StorageLow,
}

/// <summary>
/// 上传任务状态。规格 §4.2。
/// </summary>
/// <remarks>
/// 约束：
/// <list type="bullet">
/// <item><see cref="Failed"/> 必须是用户可见的状态（不变量 I3，禁止静默失败）。</item>
/// <item>只有 <see cref="Archived"/> 才可能进入「可清理」。</item>
/// <item>进程重启后 <see cref="Uploading"/> 必须能回到 <see cref="Pending"/>，不能卡死。</item>
/// </list>
/// </remarks>
public enum UploadState
{
    /// <summary>待上传。</summary>
    Pending,

    /// <summary>上传中。</summary>
    Uploading,

    /// <summary>可重试失败后的等待退避期（有限次）。</summary>
    Backoff,

    /// <summary>已归档 —— 接收方明确回执且内容校验通过。</summary>
    Archived,

    /// <summary>上传失败 —— 不可重试失败或重试耗尽，用户可见且可手动重试。</summary>
    Failed,
}

/// <summary>
/// 证据生命周期状态。规格 §4.3。
/// </summary>
public enum EvidenceState
{
    /// <summary>活跃。</summary>
    Active,

    /// <summary>已锁定 —— 用户标记为争议，永不被自动清理（规格 §3.6.5）。</summary>
    Locked,

    /// <summary>待清理 —— 归档成功且超过保留期。</summary>
    CleanupPending,

    /// <summary>已清理 —— 回查归档层通过后才允许到达此状态。</summary>
    Cleaned,

    /// <summary>
    /// 归档缺失 —— 回查归档层不通过。规格 §3.5.4：
    /// 绝不删除本地副本，并告警（不变量 I8）。
    /// </summary>
    ArchiveMissing,
}
