namespace VidLog.Desktop.Core.Media;

/// <summary>
/// 一次只许一个**录制规格探测**真开相机。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么需要它</b>（C2，2026-10-10）：探测要**真开一次相机**
/// （<c>FfmpegSpecProbe</c>：真录一小段再验解码），而相机是**独占的**（实测）。
/// 探测有**两个入口** —— 用户改了设置之后的首次开段，以及空闲时那个一秒一跳的
/// 心跳重探 —— 它们会撞在一起。撞上之后两条探测进程抢同一台相机，
/// 至少有一条开机失败 ⇒ **把本来能用的组合误判成跑不通**（回落 + 对用户说假话），
/// 而「实际将用哪个编码器」那个共享字段是**后写者赢**。
/// </para>
/// <para>
/// 实测痕迹：2026-10-09 15:31:04 有两条「录制规格已改为 …」落在**同一个 19 毫秒**里，
/// 而同一刻两条探测进程都是**被杀**（<c>-5</c>）退出的。
/// </para>
/// <para>
/// ⚠️ <b>为什么这一段在 Core 而不在装配层</b>：那个工程没有测试工程，
/// 于是「并发进来是不是真的串住了」只能靠读代码确认 —— 而互斥正是**最不能靠读代码**
/// 的那一类东西（写错了平时看不出，只在撞上的那一下错）。同一个理由，
/// <see cref="Recording.ReprobeGate"/> 当初也是从那儿搬过来的。
/// </para>
/// <para>
/// ⚠️ 等门**不等于**白等：等到了之后先看 <paramref name="alreadyDone"/>
/// —— 另一个探测很可能已经把**这一档**探完了，那时再探一次只是白开一次相机、
/// 白等几秒（探测一次十秒起步）。
/// </para>
/// </remarks>
public sealed class SpecProbeGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _skipped;

    /// <summary>
    /// 被「等门时发现已经探过了」挡掉的次数。**只是给测试与诊断看的**，
    /// 生产代码不读它（判断行为的是日志里那条「同一档刚探过」）。
    /// </summary>
    public int SkippedCount => Volatile.Read(ref _skipped);

    /// <summary>
    /// 跑一次探测，但保证**同时只有一个**。
    /// </summary>
    /// <param name="alreadyDone">
    /// 这一档是不是已经探过了（等门**之后**才问）。真 ⇒ 调用
    /// <paramref name="probe"/> 被跳过、返回 <see langword="false"/>。
    /// </param>
    /// <param name="probe">真探那一下。</param>
    /// <returns>
    /// 真探了给 <see langword="true"/>；因为已经探过而跳过给 <see langword="false"/>
    /// —— caller 拿这个来决定要不要记那条「不再重复探一次」。
    /// </returns>
    public async Task<bool> RunAsync(
        Func<bool> alreadyDone,
        Func<Task> probe,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (alreadyDone())
            {
                Interlocked.Increment(ref _skipped);
                return false;
            }

            await probe().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
