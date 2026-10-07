namespace VidLog.Desktop.Core.Configuration;

/// <summary>
/// 存设置时要做的两处**纯判断**（T27② 第 3 批块 3）。
/// </summary>
/// <remarks>
/// <para>
/// 原先在 <c>AppHost.SaveSettingsAsync</c> 里。那个工程没有测试工程，
/// 于是这两条规矩**都只是注释** —— 而第一条当时是**坏的**：
/// 它调的是 <c>next.Cloud.WithAutoUpload(next.Cloud.AutoUpload, …)</c>，
/// 那个方法头一句就是「值没变就原样返回」，拿自己的值喂自己 ⇒ **永远不盖章**。
/// 原来的写法只有一处调用点、没有任何测试碰过它。
/// </para>
/// <para>
/// ⚠️ 只收**由新旧两份设置就能推出来**的判断。真正落盘、起进程、枚举设备那些
/// 留在壳里 —— 它们要 I/O，而且做没做成要报告给用户。
/// </para>
/// </remarks>
public static class SettingsSavePlan
{
    /// <summary>
    /// 「启用自动上传」被**拨开**的那一刻盖上时间戳；其余情况把那枚章原样带过去。
    /// </summary>
    /// <param name="current">已经存着的那一份 —— <b>拨没拨动是跟它比出来的</b>。</param>
    /// <param name="next">这一趟要存的。</param>
    /// <param name="now">拨开的那一刻。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ 设计图 `_45` 的原话是「仅此开关开启后**新开始录制**的视频会上传」，
    /// 判据就是这枚时间戳。没有它，「从现在起」会变成「把库里所有历史录像
    /// 一次全传上去」—— 那是往外发几个 GB，用户完全没同意过。
    /// </para>
    /// <para>
    /// ⚠️ <b>只在「关 → 开」那一下盖新的</b>，别的时候一律沿用
    /// <paramref name="current"/> 里那枚。两条路都别走开：
    /// 不盖章 ⇒ 上面那一段；每次存都重盖 ⇒ 用户改一下并发数，章就往后跳，
    /// 而中间录的那些**再也不会被自动传**（看着像「传漏了几段」，查不出来）。
    /// </para>
    /// <para>
    /// ⚠️ 沿用那一支是从 <paramref name="current"/> 取的，**不是**从
    /// <paramref name="next"/> —— 表单那边是 <c>current.Cloud with { … }</c>，
    /// <c>AutoUploadSince</c> 不在那张 `with` 表里，所以两条路今天等价；
    /// 这么写是为了哪天有人从头 new 一份 Cloud 时，这枚章不会**静默丢掉**
    /// （丢掉的表现就是跨重启再也不自动传旧账，而界面上一点提示都没有）。
    /// </para>
    /// <para>
    /// ⚠️ **关掉时不动**这枚时间戳：关着的时候它不会被用到（开关本身挡住了），
    /// 而重开时若还留着上一次那枚，就等于「只传上一次开后录的」——
    /// 那正是最保守的那一头。
    /// </para>
    /// </remarks>
    public static AppSettings WithAutoUploadStamp(
        AppSettings current, AppSettings next, DateTimeOffset now)
    {
        var turnedOn = next.Cloud.AutoUpload && !current.Cloud.AutoUpload;

        return next with
        {
            Cloud = next.Cloud with
            {
                AutoUploadSince = turnedOn ? now : current.Cloud.AutoUploadSince,
            },
        };
    }

    /// <summary>
    /// 音轨那两项（开不开、用哪个设备）有没有真变 —— 变了才值得重新枚举设备。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 枚举一次要起一个 ffmpeg 进程、约 0.3 秒（<c>ResolveMicrophoneAsync</c>），
    /// 而「存设置」是个高频动作 —— 界面上改**任何**一格都会走一遍
    /// （改完点【保存】），每次都枚举是白等。
    /// </para>
    /// <para>
    /// ⚠️ 两样**或**起来，不能只看一样：只比「开不开」的话，用户换一个麦克风
    /// 等于没换（还是拿老设备录）；只比设备名的话，关掉声音再重开时
    /// 挑不到设备（那时设备名可能压根没变过）。
    /// </para>
    /// </remarks>
    public static bool AudioChanged(AppSettings current, AppSettings next) =>
        next.RecordAudio != current.RecordAudio
        || !string.Equals(next.MicrophoneDevice, current.MicrophoneDevice, StringComparison.Ordinal);
}
