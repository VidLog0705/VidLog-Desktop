namespace VidLog.Desktop.Core.Configuration;

/// <summary>
/// 这台电脑的用途（设计图 `_11`–`_15`「选择这台电脑的用途」）。
/// </summary>
/// <remarks>
/// <para>
/// 设计图把这件事问成**两个是非题**：
/// ① 这台电脑要用来录像吗？② 这台电脑要负责长期保存录像吗？
/// 两两组合出四种用途，由 <see cref="StationRoleChoice"/> 算出来。
/// </para>
/// <para>
/// ⚠️ <b>枚举值落进 <c>settings.json</c>，顺序即格式，别重排</b>
/// —— 与 <c>ArchiveBackendKind</c> 同一条规矩。
/// </para>
/// </remarks>
public enum StationRole
{
    /// <summary>要录像 + 要长期保存 ——「电脑录像并保存在本机」。</summary>
    /// <remarks>
    /// <b>显式写 0</b>：老设置文件里没有这个字段，反序列化取枚举默认值，
    /// 而老机器**装的都是完整 VidLog**（既录也存）⇒ 默认值必须是它，
    /// 否则升级之后所有人的工位都会突然变成「备份主机」。
    /// </remarks>
    RecordAndKeep = 0,

    /// <summary>要录像 + 不长期保存 ——「电脑录像并保存到其他电脑」。</summary>
    RecordAndUpload = 1,

    /// <summary>不录像 + 要长期保存 ——「录像文件备份主机」（设计图 `_39`）。</summary>
    BackupHost = 2,

    /// <summary>不录像 + 不长期保存 ——「只连接主机查看」。</summary>
    ViewerOnly = 3,
}

/// <summary>
/// 那两个是非题的答案。
/// </summary>
/// <remarks>
/// 它存在的理由只有一个：**「两个是非题 → 四种用途」这层映射只写一遍**。
/// 界面上是先有答案、后有用途（用户点的是卡片），而落盘、装配读的是用途
/// —— 中间那一步写反了（比如把「要录像 + 不要保存」映射成备份主机）
/// 不会报任何错，只会让装配走错分支，所以它值得一个能被测的纯函数。
/// </remarks>
/// <param name="Records">这台电脑要用来录像吗。</param>
/// <param name="Keeps">这台电脑要负责长期保存录像吗。</param>
public readonly record struct StationRoleChoice(bool Records, bool Keeps)
{
    /// <summary>没配过时的答案：既录也存（= <see cref="StationRole.RecordAndKeep"/>）。</summary>
    public static StationRoleChoice Default { get; } = new(true, true);

    /// <summary>两个答案组合出来的用途。</summary>
    public StationRole Role => (Records, Keeps) switch
    {
        (true, true) => StationRole.RecordAndKeep,
        (true, false) => StationRole.RecordAndUpload,
        (false, true) => StationRole.BackupHost,
        (false, false) => StationRole.ViewerOnly,
    };

    /// <summary>反查：某个用途对应哪两个答案（界面要拿它回填那两张卡）。</summary>
    public static StationRoleChoice Of(StationRole role) => role switch
    {
        StationRole.RecordAndUpload => new StationRoleChoice(true, false),
        StationRole.BackupHost => new StationRoleChoice(false, true),
        StationRole.ViewerOnly => new StationRoleChoice(false, false),
        _ => new StationRoleChoice(true, true),
    };
}

/// <summary>
/// 一种用途在界面上的说法（设计图 `_11`–`_15` 结果卡里的三样东西）。
/// </summary>
/// <param name="Title">结果卡上的大标题，例如「电脑录像并保存在本机」。</param>
/// <param name="Summary">标题下面那句说明。</param>
/// <param name="Abilities">「支持的能力」那几条<b>项目符号</b>。</param>
public sealed record StationRoleDescription(
    string Title,
    string Summary,
    IReadOnlyList<string> Abilities);

/// <summary>
/// 四种用途的界面文案。
/// </summary>
/// <remarks>
/// ⚠️ <b>下面每一句都是设计图上的原文，逐字照抄</b>（`_12`/`_13`/`_14`/`_15`
/// 四张结果卡）。改文案等于改需求方定的交付物 —— 要改先回去问。
/// <para>
/// 文案放 Core 而不是 XAML，理由与 <c>SpecSelectionPolicy.Describe</c> 一致：
/// 放这里才**测得到**（App 层没有测试工程，XAML 里的字只能靠人跑一遍看）。
/// </para>
/// </remarks>
public static class StationRoles
{
    /// <summary>这一档用途的说法。</summary>
    public static StationRoleDescription Describe(StationRole role) => role switch
    {
        StationRole.RecordAndUpload => new StationRoleDescription(
            "电脑录像并保存到其他电脑",
            "使用这台电脑录像，完成后安全上传到绑定的保存电脑",
            [
                "使用完整电脑录像能力",
                "录像先安全保存在本地缓存",
                "上传到绑定的保存电脑并等待完整性确认",
                "不接收或备份其他设备录像",
            ]),

        StationRole.BackupHost => new StationRoleDescription(
            "录像文件备份主机",
            "这台电脑不录像，专门接收并长期保存其他设备的录像",
            [
                "本机不使用摄像头录像",
                "接收并长期保存手机录像",
                "接收并长期保存其他录制电脑上传的录像",
                "提供局域网录像回放",
            ]),

        StationRole.ViewerOnly => new StationRoleDescription(
            "只连接主机查看",
            "这台电脑不录像也不保存录像，只连接现有主机使用",
            [
                "本机不录像、不长期保存录像",
                "连接现有主机查看录像",
                "保留订单联动和测试订单能力",
                "不接收其他设备录像",
            ]),

        _ => new StationRoleDescription(
            "电脑录像并保存在本机",
            "这台电脑既负责录像，也作为保存主机长期保管录像",
            [
                "使用完整电脑录像能力",
                "录像长期保存在本机",
                "可接收并备份手机录像",
                "可接收并备份其他录制电脑上传的录像",
                "提供局域网录像回放",
            ]),
    };
}
