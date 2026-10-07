namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 相机恢复之后**该不该**自动重探一次录制规格。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 这一档原先写在 <c>AppHost.MaybeReprobeAsync</c> 里（T27② 第 2 批搬过来）——
/// 那个工程没有测试工程，于是「什么情况下会白开一次相机 / 会漏探一次」
/// 只能靠读代码确认。这里全是纯判据，没有一样是 WPF 或进程。
/// </para>
/// <para>
/// ⚠️ <b>为什么需要「重探」这件事（不是这个类的事，但看代码会想问）</b>：
/// 2026-10-02 实测 —— 那次回落纯粹是相机那一刻不可达（几分钟后向导里实测
/// 「H.265 4K 跑得通」），可它**锁死了整个会话**：<c>ProbedSpec</c> 记的是
/// 「这一对探过了」，而开段路径只在「用户改了编码 / 分辨率」时才重探。
/// 于是用户配的 4K 静默变成 720P，直到重启程序。
/// </para>
/// </remarks>
public static class ReprobeGate
{
    /// <summary>同一件事最快多久试一次（相机一直不可达时的兜底）。</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 判一次，并给出**要记下的时刻**。
    /// </summary>
    /// <param name="fellBack">上一次探测**回落**了吗（没回落就没有要修的东西）。</param>
    /// <param name="recording">这会儿有没有人在录（有 <c>CurrentWaybill</c>）。</param>
    /// <param name="cameraHeld">这会儿相机是不是被**别人**持着（待扫时的预录进程）。</param>
    /// <param name="lastTriedAt">上一次**试过**的时刻。</param>
    /// <param name="now">此刻。</param>
    /// <returns>
    /// <c>ShouldProbe</c>：这次到底探不探。 <c>LastTriedAt</c>：要写回
    /// 「上一次试过的时刻」的那个值 —— 不探时**原样返回** <paramref name="lastTriedAt"/>。
    /// </returns>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>为什么要把那个时刻**返回**出来，而不是让调用方自己 <c>now</c> 一下</b>：
    /// 原来的写法是先判、再 <c>_lastReprobeAt = now</c>、再探。那个顺序是**要害**——
    /// 探的过程抛异常（取消、进程起不来）时也要算「试过了」，否则一次持续失败会让它
    /// 每次心跳都重来一遍。做成「判据把值交出来、调用方无条件写回」，这一条就不靠记性了
    /// （写回在 <c>await</c> 之前那一行）。
    /// </para>
    /// <para>
    /// ⚠️ <b>「没在录」不等于「相机是空的」</b>（2026-10-02 预录缓冲带来的新情况）：
    /// 待扫期间相机在**预录那个进程**手里，这时去探只会拿到 <c>device already in use</c>
    /// —— 于是把**能用的组合误判成跑不通**，而那正是这个方法存在的理由（上一次误判
    /// 就是这么来的）。所以问的是 <paramref name="cameraHeld"/>，不是
    /// <paramref name="recording"/>，两个都要问。
    /// </para>
    /// <para>
    /// ⚠️ 由主窗那个一秒一跳的时钟定时器捎带着调，所以这条路径必须**几乎不要钱**。
    /// </para>
    /// </remarks>
    public static (bool ShouldProbe, DateTimeOffset LastTriedAt) Decide(
        bool fellBack,
        bool recording,
        bool cameraHeld,
        DateTimeOffset lastTriedAt,
        DateTimeOffset now)
    {
        if (!fellBack || recording || cameraHeld)
        {
            return (false, lastTriedAt);
        }

        // 一分钟最多一次：相机一直不可达时，别让每一次心跳都去真开一次相机
        //（那是十秒的超时 + 一次设备占用）。
        if (now - lastTriedAt < MinInterval)
        {
            return (false, lastTriedAt);
        }

        return (true, now);
    }
}
