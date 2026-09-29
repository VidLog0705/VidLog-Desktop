namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 一个正在跑的采集进程。
/// </summary>
/// <remarks>
/// <para>
/// 和 <see cref="Media.IProcessRunner"/> 分开，是因为两者的**生命周期形状相反**：
/// 那个是「跑完再返回结果」（remux、解码校验、编码探测都属此类），
/// 这个是「一直跑，由我方决定何时停」。
/// </para>
/// <para>
/// 本类型存在的**唯一理由**是停止必须优雅：实测过，往 ffmpeg 的 stdin 发一个
/// <c>q</c> 会让它退出码为 0 且 MKV 尾部完整；直接杀进程树则会丢掉尾部。
/// 中断的 MKV 仍能被 remux（实测），但那是**兜底**不是正常路径。
/// </para>
/// </remarks>
public interface ICaptureProcess
{
    /// <summary>
    /// 起这一路时发生的、**用户需要知道**的事；没有就是 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 规格 §3.1.8：麦克风接不上时「照常录视频，只是那一段没有音轨」——
    /// 那是一次**降级**，而 I3 不允许静默降级。它发生时进程还活着（正是为了不中断
    /// 录制），所以这句只能由进程对象捎回来，由会话并进 <c>LastProblem</c>。
    /// </remarks>
    string? StartupWarning { get; }

    /// <summary>
    /// 优雅停止：发 <c>q</c>，等它自己写完尾部退出。
    /// </summary>
    /// <param name="timeout">等待上限；超时后升级为强杀。</param>
    /// <returns>进程的退出码；未能拿到（超时后强杀）时为 <see langword="null"/>。</returns>
    Task<int?> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
