namespace VidLog.Desktop.Core.Configuration;

/// <summary>
/// 「补传范围」（设计图 `_46`）。
/// </summary>
/// <remarks>
/// ⚠️ 枚举值落进设置文件，**顺序即格式，别重排**。
/// </remarks>
public enum BackfillScope
{
    /// <summary>全部：本机所有可上传的录像。</summary>
    All = 0,

    /// <summary>自定起始日：只补该日及之后**开录**的（以开录自然日为准）。</summary>
    FromDate = 1,
}

/// <summary>
/// 百度网盘那一页的设置（设计图 `_45` / `_46`）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>这里头没有任何凭据。</b>AppKey / AppSecret 走环境变量
/// （<c>BaiduPanCredentials</c>），用户的令牌走应用数据目录里的单独文件
/// （<c>BaiduPanTokenStore</c>）。设置文件是会被复制、被贴进工单的那一份，
/// 所以它一个秘密都不许装。
/// </para>
/// <para>
/// ⚠️ <b>默认一个开关都不开。</b>打开就意味着「这台机器上的录像会开始往公网上传」，
/// 而那是一个用户必须**自己**做的决定 —— 装完就默默传起来是绝对不能接受的。
/// </para>
/// </remarks>
public sealed record CloudUploadSettings
{
    /// <summary>启用自动上传（设计图 `_45` 那个开关）。</summary>
    public bool AutoUpload { get; init; }

    /// <summary>
    /// 这个开关是**哪一刻**打开的。
    /// </summary>
    /// <remarks>
    /// 「仅此开关开启后**新开始录制**的视频会上传」—— 判据就是它。
    /// 没有它的话，「打开开关」会把**库里所有历史录像**一次全传上去，
    /// 而用户以为只是「从现在起」。
    /// <para>
    /// ⚠️ 为 <see langword="null"/> 而开关又是开的（手改过设置文件），
    /// 一律按**服务启动那一刻**算 —— 朝「少传」的那头落，与
    /// <c>ArchiveTarget.FromConfig</c> 认不出就回落到本机是同一个规矩。
    /// </para>
    /// </remarks>
    public DateTimeOffset? AutoUploadSince { get; init; }

    /// <summary>
    /// 自动对比补传（设计图 `_46` 第二个开关）。
    /// </summary>
    /// <remarks>
    /// 定期与网盘实际内容对比：网盘已有的自动去重，只补传网盘上没有的；
    /// 开启后约每分钟检查一次队列。
    /// </remarks>
    public bool CompareAndBackfill { get; init; }

    /// <summary>补传范围。</summary>
    public BackfillScope BackfillScope { get; init; } = BackfillScope.All;

    /// <summary>自定起始日（<see cref="BackfillScope.FromDate"/> 时才算数）。</summary>
    public DateTimeOffset? BackfillFrom { get; init; }

    /// <summary>
    /// 同时上传数。
    /// </summary>
    /// <remarks>
    /// ⚠️ 上限压到 8：图上的说明写着「过大可能触发网盘风控」，
    /// 而风控的代价不是「慢一点」—— 是**整个应用被限流**，那时连登录都难。
    /// 这个数不是一个「越大越好」的性能旋钮。
    /// </remarks>
    public int ParallelUploads { get; init; } = 2;

    /// <summary>
    /// 网盘目录应用名 —— 开放平台后台填的**产品名称**。
    /// </summary>
    /// <remarks>
    /// 远端根是 <c>/apps/&lt;这个值&gt;/</c>。它不是 AppKey，
    /// 所以它出现在设置文件里没有问题。
    /// </remarks>
    public string AppName { get; init; } = "VidLog";

    public static CloudUploadSettings Default { get; } = new();

    /// <summary>允不允许这个配置。</summary>
    public static bool IsPlausible(CloudUploadSettings s) =>
        s.ParallelUploads is >= 1 and <= 8
        && Enum.IsDefined(s.BackfillScope)
        && !string.IsNullOrWhiteSpace(s.AppName);

    /// <summary>
    /// 把「启用自动上传」拨到某个位置，<b>并顺手盖上前时间戳</b>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 拨开的那一刻必须留下 <see cref="AutoUploadSince"/>，否则
    /// 「仅此开关开启后新开始录制的视频会上传」这句话就是假的：
    /// 没有时间戳时服务只能按「库里全都要传」算，而那是往外发几个 GB。
    /// 关掉时**不动**时间戳（开关本身已经挡住了一切）。
    /// </remarks>
    public CloudUploadSettings WithAutoUpload(bool on, DateTimeOffset now) =>
        AutoUpload == on
            ? this
            : this with { AutoUpload = on, AutoUploadSince = on ? now : AutoUploadSince };

    /// <summary>
    /// 补传的下界（含）。
    /// </summary>
    /// <remarks>
    /// 以**开录自然日**为准（图上原话）：起始日选 9 月 20 日，
    /// 那一天 00:00 之后开录的都算，而不是「从 9 月 20 日那一刻之后」——
    /// 后者会把当天上午录的排除掉，而用户选的就是那一天。
    /// </remarks>
    public DateTimeOffset Floor =>
        BackfillScope == BackfillScope.FromDate && BackfillFrom is { } from
            ? new DateTimeOffset(from.Date, from.Offset)
            : DateTimeOffset.MinValue;
}
