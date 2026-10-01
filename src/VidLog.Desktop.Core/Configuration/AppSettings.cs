using System.Text.Json;
using System.Text.Json.Serialization;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Scanning;

namespace VidLog.Desktop.Core.Configuration;

/// <summary>
/// 用户设置。
/// </summary>
/// <remarks>
/// <para>
/// 这些值此前**全部是硬编码常量**，散在各自的类型里（分段时长在
/// <c>RecordingSessionOptions.Default</c>、回放端口在 <c>DesktopServices</c>、
/// 扫码阈值在 <c>ScannerOptions</c>）—— 用户改不了，测试也只能靠传参覆盖。
/// </para>
/// <para>
/// 全部字段都带默认值：**设置文件不存在、损坏、或某个字段越界时，
/// 回落默认值并给一条用户可见的警告，绝不阻断启动**（I4 的同一条精神）。
/// </para>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>
    /// 这台电脑的用途（设计图 `_11`–`_15`「选择这台电脑的用途」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>默认 <see cref="StationRole.RecordAndKeep"/>，而且必须</b>：
    /// 老设置文件里没有这个键，反序列化取枚举默认值 —— 而老机器装的都是
    /// 完整 VidLog（既录也存）。默认值换成别的，升级之后所有工位都会
    /// 突然不再录像，而界面上不会有任何迹象。
    /// </para>
    /// <para>
    /// ⚠️ 它决定**启动时装不装录像设备**：不要录像的两档
    /// （<see cref="StationRole.BackupHost"/> / <see cref="StationRole.ViewerOnly"/>）
    /// 连摄像头都不解析 —— 设计图卡片原文就是「**不初始化本机录像设备**」，
    /// 而那也正是把相机让出来的做法（dshow 上相机是独占的）。
    /// </para>
    /// <para>
    /// ⚠️ 改它**要重启才生效**，与摄像头、<see cref="CameraRecognition"/> 同一档：
    /// 三者都在 <c>AppHost.StartAsync</c> 的装配期定死，界面上写明了。
    /// </para>
    /// </remarks>
    public StationRole StationRole { get; init; } = StationRole.RecordAndKeep;

    /// <summary>
    /// 用途展开成的两个答案。**不落盘**，由 <see cref="StationRole"/> 算出来。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Camera"/> / <see cref="Archive"/> 同一路数：落盘存**一个**
    /// 枚举（好读好改），而装配期要问的是「录不录」「存不存」这两个问题 ——
    /// 由这一处换算，免得每个调用点各写一遍四分支的映射。
    /// </remarks>
    [JsonIgnore]
    public StationRoleChoice Role => StationRoleChoice.Of(StationRole);

    /// <summary>工作模式（规格 §3.3.1）。</summary>
    /// <remarks>
    /// 默认取<see cref="WorkMode.StopOnSameWaybill"/>，是三者里最保守的：
    /// 它不会因为扫到别的面单就自动换段，用户想停只需复扫同一张。
    /// </remarks>
    public WorkMode Mode { get; init; } = WorkMode.StopOnSameWaybill;

    /// <summary>
    /// 闲置提醒档位（规格 §3.3.3 **电脑端那半**）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>JSON 键名仍是 <c>StaticStop</c></b>（它原来是「静止停录档位」）——
    /// 键名不动是为了**老配置不丢**：本仓的枚举存的是**数字**，换了键名整条就回落默认值。
    /// 类型与语义都已经换了：电脑端现在只有**闲置提醒**，没有静止停录（§3.3.1）。
    /// </remarks>
    [JsonPropertyName("StaticStop")]
    public IdleReminderOption IdleReminder { get; init; } = IdleReminderOption.Three;

    /// <summary>
    /// 闲置提醒「自定义」档的分钟数。
    /// </summary>
    /// <remarks>
    /// 新增字段 ⇒ 老配置里没有它 ⇒ 取默认 3 分钟（与档位默认档一致）。
    /// 越界值由 <see cref="WorkModeOptions.Minutes(IdleReminderOption, int)"/> 回落，
    /// 所以这里不做夹取。
    /// </remarks>
    public int IdleReminderMinutes { get; init; } = 3;

    /// <summary>录制编码（规格 §3.1.7）。默认 H.264（兼容优先）。</summary>
    /// <remarks>
    /// 界面上一律写「H.265」，**不得出现 HEVC** —— 名字由
    /// <c>RecordingSpec.CodecLabel</c> 一处产出。
    /// </remarks>
    public VideoCodec Codec { get; init; } = VideoCodec.H264;

    /// <summary>录制分辨率（规格 §3.1.7）。默认 1080P。</summary>
    public VideoResolution Resolution { get; init; } = VideoResolution.P1080;

    /// <summary>时长兜底档位（规格 §3.3.4）。</summary>
    public DurationFallbackOption DurationFallback { get; init; } = DurationFallbackOption.Four;

    /// <summary>单个分段的时长上限（分钟）。规格 §3.1.1 要求切分不打断用户体验。</summary>
    public int SegmentMinutes { get; init; } = 1;

    /// <summary>回放服务端口。</summary>
    public int PlaybackPort { get; init; } = 8720;

    /// <summary>上次用的摄像头设备名；为空表示用枚举出来的第一个。</summary>
    public string? CameraDevice { get; init; }

    /// <summary>
    /// 画面从哪来：本机设备，还是网络摄像头（规格 §3.1.7 的设备维）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 默认<see cref="CameraSourceKind.Local"/>，**而且必须**：老设置文件里
    /// 没有这个字段，反序列化取枚举默认值；而老设置里的摄像头一定是本机设备。
    /// 默认值换成 Network 的话，升级之后所有人的摄像头都会变成
    /// 「网络摄像头、地址为空」。
    /// </remarks>
    public CameraSourceKind CameraSource { get; init; } = CameraSourceKind.Local;

    /// <summary>
    /// 网络摄像头地址（规格 §3.1.7；设计图上叫「网络摄像头（手动地址）」）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这里面的凭据是**用户自己的摄像头密码**，不是我们的密钥</b>
    /// （与百度网盘那组不是一回事）。它必须能存下来才用得起来，
    /// 所以落盘是明文；但**绝不能进日志**：<see cref="SettingsStore.DescribeChanges"/>
    /// 对它走的是抹掉凭据的那一份（见 <see cref="CameraSource.Redact"/>），
    /// 而进索引 / manifest 的是 <see cref="CameraSource.Identity"/>。
    /// </remarks>
    public string? CameraNetworkUrl { get; init; }

    /// <summary>
    /// 用摄像头取景识码（规格 §3.2.1 的第二种入口；配置向导第 3 步那个二选一）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 关掉时 <c>AppHost.StartAsync</c> **连取景进程都不建** —— 不是「建了不用」，
    /// 那样它照样占着相机（dshow 上相机是独占的），会挡住录制。
    /// </para>
    /// <para>
    /// ⚠️ 默认 <see langword="true"/>，**而且必须**：老设置文件里没有这个字段，
    /// 反序列化取 <see langword="bool"/> 的默认值 —— 而 <see langword="false"/>
    /// 会让升级之后的机器**全部静默失去**「放进画面就开录」这条入口，
    /// 用户只会觉得「摄像头识别怎么不好使了」。
    /// </para>
    /// <para>
    /// 改它要重启才生效（与摄像头同一档：两者都在启动装配期定死，界面上写明了）。
    /// </para>
    /// </remarks>
    public bool CameraRecognition { get; init; } = true;

    /// <summary>
    /// 摄像头配置（哪一个 + 地址合成一个）。**不落盘**，由上面那两个字段算出来。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Archive"/> 同一路数：两个字段合成一个判断对象，
    /// 免得每个调用点各写一遍「到底用哪个」。
    /// </remarks>
    [JsonIgnore]
    public Recording.CameraSource Camera => Recording.CameraSource.FromConfig(
        CameraSource, CameraDevice, CameraNetworkUrl);

    /// <summary>
    /// 录制声音（规格 §3.1.8）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 规格里那个设置项的名字就叫「录制声音」，界面上一字不改。
    /// <b>默认开着</b>（规格原话）。
    /// <para>
    /// ⚠️ 它开着**不等于**每一段都有音轨 —— 麦克风被拒/被占/接不上时按规格降级成
    /// 「照常录视频、只是这一段没有声音」（I4），并把原因报到界面上。
    /// </para>
    /// </remarks>
    public bool RecordAudio { get; init; } = true;

    /// <summary>上次用的麦克风设备名（规格 §3.1.8）；为空表示用枚举出来的第一个。</summary>
    public string? MicrophoneDevice { get; init; }

    /// <summary>
    /// 成片方向（规格 §3.1.7 的 2026-09-28 需求变更 —— 电脑端原本**不做方向**）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 依据是需求方两次裁决：① 设计图向导第 2 步有一个方向控件；
    /// ② 2026-09-29「**要完整三档方向**」+「电脑端按**转多少度**命名」。
    /// 合起来是**四档**（不转 / 左转 90° / 右转 90° / 转 180°），见
    /// <see cref="Media.CameraRotation"/>。
    /// </para>
    /// <para>
    /// ⚠️ <b>默认不转</b>：装歪的摄像头是少数，而默认转一下会让绝大多数人
    /// 第一次录出来的画面是歪的 —— 而且那是**整段录像都不能用**，
    /// 不是画质差一点。老设置文件里没有这个字段 ⇒ 反序列化取
    /// <see cref="Media.CameraRotation.None"/>（枚举显式写了 0），升级后行为不变。
    /// </para>
    /// </remarks>
    public Media.CameraRotation Rotation { get; init; } = Media.CameraRotation.None;

    /// <summary>扫码枪判定参数（规格 §3.2.1）。</summary>
    public ScannerOptions Scanner { get; init; } = new();

    /// <summary>日志保留天数。</summary>
    public int LogRetainDays { get; init; } = 14;

    /// <summary>
    /// 日志级别阈值。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>默认 <see cref="Diagnostics.LogLevel.Info"/>，不是 Debug。</b>
    /// 生产上开着 DEBUG，日志会被淹掉，而**淹掉的日志等于没有日志** ——
    /// 那时真正要看的那几条（失败、重试、收尾）全被埋了。
    /// </para>
    /// <para>
    /// ⚠️ <b>它存在是为了「真机上出问题时能临时打开 DEBUG」</b>：原来这个档由
    /// 构建配置写死（`#if DEBUG`），于是 Release 包**没有任何办法**开 DEBUG ——
    /// 只能等我们发一个新包，而那等于那条日志永远拿不到。
    /// </para>
    /// <para>
    /// 认不出来的值（手改坏了、或者将来改了枚举名）**退回 Info**，取保守的那一头。
    /// </para>
    /// </remarks>
    public Diagnostics.LogLevel LogMinLevel { get; init; } = Diagnostics.LogLevel.Info;

    /// <summary>
    /// 开机自启动（设计图 `_49`「高级设置」第一行）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>默认必须是 <see langword="false"/></b>：这一项的真身是往
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 里写一条。
    /// 装完就往那儿写一条自动启动，是**没经用户同意**就改变他的机器
    /// （与「清理必须是用户主动开的」是同一条道理，§6.2）。
    /// </para>
    /// <para>
    /// ⚠️ 这个字段是**意图**（用户勾没勾），注册表是**机制**。两者可能不一致
    /// （用户在任务管理器的「启动」页里禁用了它）—— 保存时按字段把注册表
    /// 重写一遍，让机制追上意图。见 <c>Platform/StartupRegistration</c>。
    /// </para>
    /// </remarks>
    public bool RunAtStartup { get; init; }

    /// <summary>关窗口时怎么办（设计图 `_49`「高级设置」第二行）。</summary>
    /// <remarks>
    /// 默认 <see cref="CloseWindowAction.MinimizeToTray"/> = 今天的行为，
    /// 而且那条枚举显式写了 0 —— 老设置文件里没有这个键时取到的正是它。
    /// </remarks>
    public CloseWindowAction CloseWindowAction { get; init; } = CloseWindowAction.MinimizeToTray;

    /// <summary>
    /// 启动后自动检查有没有新版本（设计图 `_49`「高级设置」第三行）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>只做「检查 + 提示」，绝不下载、绝不替换自己</b> ——
    /// 需求方 2026-09-30 裁决原话：「只做检查 + 提示」（不做启动器 exe、不自替换）。
    /// 所以这一项**不落任何可执行文件**，只在 <c>关于</c> 页与托盘气泡上说一句。
    /// </para>
    /// <para>
    /// ⚠️ 默认 <see langword="true"/>（图上那个开关就是开的）。检查失败
    /// **绝不能**影响任何事 —— 它连录制都不该碰（I4），更不该挡启动。
    /// </para>
    /// <para>
    /// ⚠️ 它与许可（L8）**没有关系**：拿不到更新信息不是「验证失败」，
    /// 不许因此锁任何东西。见 <c>UpdateChecker</c>。
    /// </para>
    /// </remarks>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>
    /// 归档层是本机磁盘、NAS、挂载盘、还是网盘（规格 §3.4.6 / §3.5.1）。
    /// </summary>
    /// <remarks>
    /// 默认<see cref="ArchiveBackendKind.LocalDisk"/> = 盘上这份是**唯一副本**，
    /// 于是规格 §3.5.1 不允许开启清理、界面上**不给保留期设置入口**。
    /// 这个默认值就是「什么都没配过」时的真实处境，不是保守起见。
    /// <para>
    /// ⚠️ 这一项**沿用老的 JSON 键名与数字值**（老配置里写的就是它），
    /// 所以它的类型从枚举窄化成「枚举 + 目录」那个记录时**没有动落盘形状** ——
    /// 只是多了一个兄弟字段 <see cref="ArchiveDirectory"/>。
    /// </para>
    /// </remarks>
    public ArchiveBackendKind ArchiveBackend { get; init; } = ArchiveBackendKind.LocalDisk;

    /// <summary>
    /// 目录型归档层的根（NAS 或挂载盘的路径）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这是**配置里的根**，不进索引：索引里存的永远是相对路径
    /// （<c>RelativePath</c> 拒绝绝对路径与 UNC，规格 §6.2）。
    /// 换一台机器把它挂到别的盘符时，索引照样读得出来。
    /// </remarks>
    public string? ArchiveDirectory { get; init; }

    /// <summary>归档层配置（枚举 + 目录合成一个）。**不落盘**，它是由那两个字段算出来的。</summary>
    [JsonIgnore]
    public ArchiveTarget Archive => ArchiveTarget.FromConfig(ArchiveBackend, ArchiveDirectories.FirstOrDefault());

    /// <summary>
    /// 归档层要落的那几个目录，**有序**（设计图 `_43` 的「录像备份位置」表）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>它是从两个字段算出来的，自己不落盘</b>：新配置写
    /// <see cref="BackupDisks"/>，而老的 <see cref="ArchiveDirectory"/>
    /// 仍然算数（那时列表里就是它一个）。这样**老设置文件一个字节都不用改**，
    /// 也不会出现「迁移写坏了、归档层没了」那类事。
    /// </para>
    /// <para>
    /// ⚠️ 第一个是**首选**：前一个写不进去时落下一下，
    /// 与保存位置那张表同一个规矩（图上原话「NAS 满时自动切换到下一个」）。
    /// </para>
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<string> ArchiveDirectories =>
        BackupDisks.Count > 0
            ? [.. BackupDisks.Select(s => s.Path)]
            : ArchiveDirectory is { Length: > 0 } legacy
                ? [legacy]
                : [];

    /// <summary>
    /// 录像保存位置（设计图 `_43`）—— 本机磁盘，**有序**，按顺序用、满了换下一个。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 空列表 = 老行为：成品写在 <c>DataLayout.ArchiveRoot</c>
    /// （<c>%LOCALAPPDATA%\VidLog\archive</c>）。**这个默认值不是保守起见，
    /// 是"什么都没配过"时的真实处境**，而且它保证升级过的机器一条录像都不会丢。
    /// </para>
    /// <para>
    /// ⚠️ 落盘的是**完整路径**（盘符或 UNC）。相对路径在这里没有意义 ——
    /// 工作目录是会变的，而录像必须落在用户指定的那块盘上。
    /// </para>
    /// </remarks>
    public IReadOnlyList<DiskSlot> SaveDisks { get; init; } = [];

    /// <summary>
    /// 录像备份位置（设计图 `_43`）—— NAS / 挂载盘这一档要落的目录，**有序**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它与 <see cref="ArchiveBackend"/> 分工：**枚举说这是哪一档**
    /// （决定允不允许开本地清理，规格 §3.5.1），**这个列表说落在哪儿**。
    /// 网盘那一档（<see cref="ArchiveBackendKind.Cloud"/>）不用它 ——
    /// 网盘没有「本地路径」这个概念。
    /// </remarks>
    public IReadOnlyList<DiskSlot> BackupDisks { get; init; } = [];

    /// <summary>
    /// 百度网盘上传（设计图 `_45` / `_46`）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>整组都是「什么都没配过」时的真实处境</b>：一个开关都不开。
    /// 打开「启用自动上传」意味着这台机器上的录像会开始往公网上传 ——
    /// 那是用户必须自己做的决定，装完就默默传起来是绝对不能接受的
    /// （与清理、开机自启同一条道理）。
    /// <para>
    /// ⚠️ <b>这里头没有任何凭据。</b>AppKey / AppSecret 走环境变量，
    /// 用户的令牌走应用数据目录里的单独文件。
    /// </para>
    /// </remarks>
    public CloudUploadSettings Cloud { get; init; } = CloudUploadSettings.Default;

    /// <summary>
    /// 保留期**四个数**：发货 / 退货 × 已备份 / 未备份（规格 §3.5.2.1）。
    /// </summary>
    /// <remarks>
    /// 默认四个都是「全部保留」：规格 §6.2「数据删除必须极度克制」，
    /// 清理必须是用户**主动开启**的。
    /// <para>
    /// ⚠️ 老设置文件里这个键下是 <c>{"Outbound":{"Mode":1,"KeepDays":7},"Return":{…}}</c>
    /// —— 那时它叫 <c>RetentionPolicies</c>。转换见
    /// <see cref="RetentionSettingJsonConverter"/>（它认得老形状）。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 重复单号检测回看几天（规格 §3.2.5：「识别到的单号若在**近 N 天内**已有
    /// 未删除记录，必须提示。**N 可配置**」）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>0</c> = 关闭</b>：规格说「N 可配置」，那就得允许关掉 ——
    /// 一个关不掉的提醒在连续扫的工位上就是噪声，而噪声会被无视。
    /// <para>
    /// ⚠️ 默认 <b>7 天</b>是**本仓标定**的（规格没给数），理由写在
    /// <see cref="Recording.CoordinatorOptions.DuplicateCheckDays"/>。
    /// </para>
    /// </remarks>
    public int DuplicateCheckDays { get; init; } = 7;

    public RetentionSettings Retention { get; init; } = RetentionSettings.KeepAll;

    public static AppSettings Default { get; } = new();

    /// <summary>允许的参数范围。越界即回落，不静默接受。</summary>
    /// <remarks>
    /// 分段时长定在 10 秒~10 分钟、端口定在 1024~65535：越界的值不会报错，
    /// 只会让行为变得莫名其妙（端口 0 起不来、分段 0 秒疯狂滚段），
    /// 那比拒绝更糟。
    /// </remarks>
    public static bool IsPlausible(AppSettings s) =>
        // ⚠️ 认不出的用途**整体回落默认值**，而不是猜一个 —— 猜错的后果是
        // 「这台机器突然不录像了」或「备份主机突然开始抢摄像头」，两种都很难查。
        Enum.IsDefined(s.StationRole)
        && Enum.IsDefined(s.Codec)
        && Enum.IsDefined(s.Resolution)
        && s.SegmentMinutes is >= 1 and <= 10
        && s.PlaybackPort is >= 1024 and <= 65535
        && s.LogRetainDays is >= 1 and <= 365
        // 重复单号检测：0 = 关闭，上限 365 天（一年前的单号翻出来没有意义）。
        && s.DuplicateCheckDays is >= 0 and <= 365
        && s.Scanner.MaxInterKeyIntervalMs is >= 10 and <= 500
        && s.Scanner.MinLength is >= 1 and <= 64
        && s.Scanner.MaxLength is >= 1 and <= 256
        && s.Scanner.MaxLength >= s.Scanner.MinLength
        // 保留期：**天数 + 一个上限**。上限不写死在规格里（§3.5.2.1 明说
        // 「这类阈值要真机标定」），本仓标定为 **10 年** —— 它远大于任何真实
        // 工位的保留期，而小于「手改成 999999 天」这种明显是打错的值。
        && PlausibleDays(s.Retention.ArchivedOutbound)
        && PlausibleDays(s.Retention.ArchivedReturn)
        && PlausibleDays(s.Retention.UnarchivedOutbound)
        && PlausibleDays(s.Retention.UnarchivedReturn)
        && PlausibleSlots(s.SaveDisks)
        && PlausibleSlots(s.BackupDisks)
        // 关窗口时那三档（设计图 `_49`）。认不出的值会让 `OnClosing` 落到
        // 哪个分支上全靠命令行 switch 的兜底 —— 而那里兜底错了的后果是
        // 「关不掉的窗口」或者「一不小心就退出了」。
        && Enum.IsDefined(s.CloseWindowAction)
        // 百度网盘那一组（设计图 `_45`/`_46`）。越界即整体回落默认值 ——
        // 「同时上传数」被手改成 100 的后果不是慢一点，而是**整个应用被风控限流**。
        && CloudUploadSettings.IsPlausible(s.Cloud);

    /// <summary>
    /// 一个保留期档位自洽吗。
    /// </summary>
    /// <remarks>
    /// 这里拦的是**手改设置文件**能造出来的坑：负数天数会让
    /// <c>now.AddDays(-(-5))</c> 变成「cutoff 在未来」，于是一律判超期 ⇒
    /// 除了被豁免的全删。这类值不会报错，只会把东西删光 —— 正是本方法存在的理由。
    /// <para>
    /// ⚠️ <b>上限 10 年，而且它是「实现标定」的，不是规格里写的</b> ——
    /// 规格 §3.5.2.1 原话「上限不写死在这里……由实现标定并记进
    /// <c>docs/实现决策.md</c>」。取 3650 的理由：它远大于任何真实工位的保留期，
    /// 而小于「手抖多打几个 9」那种值。
    /// </para>
    /// </remarks>
    private static bool PlausibleDays(RetentionSetting setting) =>
        setting.Days is null or (>= 0 and <= 3650);

    /// <summary>
    /// 那两张「录像保存 / 备份位置」的表自洽吗。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>判据是"这个路径能不能当根用"，不是"这个目录在不在"</b>：
    /// 目录在不在是 I/O，而配置校验是纯函数（设置页每敲一个字符都会问一次），
    /// 而且**盘没插、NAS 没开**都是正常状态 —— 那时该说的话在归档层回查那里
    /// （「查不了 ≠ 不存在」，见 <c>ArchiveTarget.ConfigurationProblem</c>）。
    /// </para>
    /// <para>
    /// ⚠️ 列表长度也要拦：一个无限长的列表不会报错，只会让每次选盘都要
    /// 挨个探一遍，而那是录制的热路径。
    /// </para>
    /// </remarks>
    private static bool PlausibleSlots(IReadOnlyList<DiskSlot>? slots)
    {
        if (slots is null || slots.Count > 32)
        {
            return false;
        }

        foreach (var slot in slots)
        {
            if (slot is null
                || string.IsNullOrWhiteSpace(slot.Path)
                || slot.Path.Length > 1024
                || !Path.IsPathFullyQualified(slot.Path)
                // 0 是允许的（「这块盘不预留」是用户的正当选择），负数不是。
                || slot.ReservedGb is < 0 or > 1_000_000)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>设置读取的结果。</summary>
/// <param name="Settings">可用的设置。文件不存在或损坏时为默认值。</param>
/// <param name="Warnings">需要让用户看见的问题（I3：不允许静默失效）。</param>
public sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Warnings);

/// <summary>
/// 设置的读写。
/// </summary>
/// <remarks>
/// 写盘走「临时文件 + 改名」，与 <see cref="Recording.RecordingWorkspace"/> 同一手法 ——
/// 进程在改名之前被杀，留下的是完整的旧版本或完整的新版本，不会有半个 JSON。
/// </remarks>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new SettingsLoadResult(AppSettings.Default, []);
        }

        AppSettings? parsed;
        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken);
            parsed = JsonSerializer.Deserialize<AppSettings>(json, Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 坏设置**不得**让程序起不来 —— 回落默认值，但要说出来。
            return new SettingsLoadResult(
                AppSettings.Default,
                [$"设置文件读不出来（{ex.Message}），已按默认值启动。原文件保留在 {_path}。"]);
        }

        if (parsed is null)
        {
            return new SettingsLoadResult(
                AppSettings.Default, [$"设置文件是空的，已按默认值启动。原文件保留在 {_path}。"]);
        }

        if (!AppSettings.IsPlausible(parsed))
        {
            // 越界不静默接受：那种值不会报错，只会让行为变得莫名其妙。
            return new SettingsLoadResult(
                AppSettings.Default,
                [$"设置文件里有超出允许范围的值，已整体回落为默认值。原文件保留在 {_path}。"]);
        }

        // ⚠️ 老配置：电脑端**删掉了**「扫码静止停录」（规格 §3.3.1，2026-09-24）——
        // 而本仓的枚举存的是**数字**，删掉那个成员之后，老配置里存着的 `2`
        // 反序列化出来就是个**越界值**：它不是任何成员，行为会落到各个 switch 的
        // 兜底分支上（恰好**看起来像**同码停）。靠巧合是对的，但**不是要求**——
        // 规格 §3.3.1 明说要**回落到「同码停」**，所以这里显式做掉，并说出来。
        var mode = Normalize(parsed.Mode, out var modeReplaced);
        var warnings = new List<string>();

        if (modeReplaced)
        {
            warnings.Add(
                "设置文件里的工作模式是「扫码静止停录」—— 电脑端已经删掉这个模式"
                + "（规格 §3.3.1，2026-09-24），已按最接近的「同码停」启动。");
            parsed = parsed with { Mode = mode };
        }

        // 保留期从两个数扩成四个数（规格 §3.5.2.1，2026-09-24）：
        // 老的 `Outbound` / `Return` 说的是「已备份后的本地保留期」，语义没变，
        // 所以搬进已备份那一列。不搬的话那两个数会被静默丢掉。
        if (parsed.Retention.LegacyOutbound is not null || parsed.Retention.LegacyReturn is not null)
        {
            parsed = parsed with { Retention = parsed.Retention.WithLegacyApplied() };

            warnings.Add(
                "设置文件里的保留期是旧格式（发货 / 退货各一个数），"
                + "已按「已备份保留时长」读入 —— 新增的「未备份」那一列默认是「全部保留」。"
                + "（未备份的那一列永不自动删，它到期只提醒。）");
        }

        return new SettingsLoadResult(parsed, warnings);
    }

    /// <summary>认不出来的模式一律当「同码停」；返回值与「是否换过」。</summary>
    /// <remarks>
    /// 选同码停而不是连续扫，理由与规格里那句一致：**同码停会自己停**
    /// （复扫同一张就收工），不会一路录到把盘写满。
    /// </remarks>
    private static WorkMode Normalize(WorkMode mode, out bool replaced)
    {
        replaced = !Enum.IsDefined(mode);
        return replaced ? WorkMode.StopOnSameWaybill : mode;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, Options);
        var temporary = _path + ".tmp";

        await File.WriteAllTextAsync(temporary, json, cancellationToken);

        try
        {
            File.Move(temporary, _path, overwrite: true);
        }
        catch (IOException)
        {
            // Windows 上改名可能被 Defender 之类的句柄短暂拒绝。
            await Task.Delay(50, cancellationToken);
            File.Move(temporary, _path, overwrite: true);
        }
    }

    /// <summary>
    /// 算出两份设置之间**哪些字段变了**，供变更留痕。
    /// </summary>
    /// <remarks>
    /// 只记字段名与「变了」，**不记值** —— 密钥类字段不能进日志。
    /// 今天没有密钥字段，但这条是 <c>AGENTS.md</c> §6 的硬要求，
    /// 等有了再补就晚了。
    /// </remarks>
    public static IReadOnlyList<string> DescribeChanges(AppSettings previous, AppSettings next)
    {
        var changes = new List<string>();

        void Compare<T>(string name, T a, T b)
        {
            if (!EqualityComparer<T>.Default.Equals(a, b))
            {
                changes.Add(IsSensitive(name) ? $"{name}{SensitiveName.Redacted}" : $"{name}: {a} → {b}");
            }
        }

        /// <summary>
        /// 比一个**值里含凭据**的字段：变化照样记一行，但两边的值都先抹掉凭据。
        /// </summary>
        /// <remarks>
        /// ⚠️ 不能用「名字命中就整条不记」（<see cref="Compare{T}"/> 那条路）：
        /// 这里抹得掉凭据，剩下的主机与路径对排查**有用**
        /// （「换了哪台摄像头」是很常见的一次改动）。
        /// 而值整个不记的话，日志上只会写一行「（已修改）」，
        /// 事后根本分不清是改了地址还是清空了地址。
        /// </remarks>
        void CompareRedacted(string name, string? a, string? b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                return;
            }

            var from = Recording.CameraSource.Redact(a ?? string.Empty);
            var to = Recording.CameraSource.Redact(b ?? string.Empty);

            changes.Add($"{name}: {Quote(from)} → {Quote(to)}");

            static string Quote(string value) => value.Length == 0 ? "（空）" : value;
        }

        // ⚠️ 用途排在最前：它一变，整台机器的行为都变（录不录、存不存），
        // 而日志是按写入顺序读的，把它埋在中间会让人先看到一堆次要改动。
        Compare(nameof(AppSettings.StationRole), previous.StationRole, next.StationRole);

        // 高级设置那三行（设计图 `_49`）。⚠️ 开机自启动尤其要留痕：
        // 它改了**操作系统**上的东西（Run 键），事后要能回答「这台机器是什么时候
        // 开始自己启动的、谁开的」。
        Compare(nameof(AppSettings.RunAtStartup), previous.RunAtStartup, next.RunAtStartup);
        Compare(nameof(AppSettings.CloseWindowAction), previous.CloseWindowAction, next.CloseWindowAction);
        Compare(nameof(AppSettings.CheckForUpdates), previous.CheckForUpdates, next.CheckForUpdates);
        Compare(nameof(AppSettings.Mode), previous.Mode, next.Mode);
        Compare(nameof(AppSettings.Codec), previous.Codec, next.Codec);
        Compare(nameof(AppSettings.Resolution), previous.Resolution, next.Resolution);
        // ⚠️ 留痕里写的是**新名字**（`IdleReminder`），而文件里的键名仍是 `StaticStop`
        // （见那一处属性的说明：键名不动是为了老配置不丢）。看日志的人和改文件的人
        // 会看到两个名字 —— 这是刻意的取舍，不是笔误。
        Compare(nameof(AppSettings.IdleReminder), previous.IdleReminder, next.IdleReminder);
        Compare(nameof(AppSettings.IdleReminderMinutes), previous.IdleReminderMinutes, next.IdleReminderMinutes);
        Compare(nameof(AppSettings.DurationFallback), previous.DurationFallback, next.DurationFallback);
        Compare(nameof(AppSettings.SegmentMinutes), previous.SegmentMinutes, next.SegmentMinutes);
        Compare(nameof(AppSettings.PlaybackPort), previous.PlaybackPort, next.PlaybackPort);
        Compare(nameof(AppSettings.CameraDevice), previous.CameraDevice, next.CameraDevice);
        Compare(nameof(AppSettings.CameraSource), previous.CameraSource, next.CameraSource);
        Compare(nameof(AppSettings.CameraRecognition), previous.CameraRecognition, next.CameraRecognition);

        // ⚠️ 网络摄像头地址**必须抹掉凭据再比**：它是用户自己的摄像头密码，
        // 而 Compare 默认会把两边的值整个写进日志（`X: 旧值 → 新值`）。
        // `SensitiveName.Is` 按**名字**挡，而 `CameraNetworkUrl` 这个名字
        // 不含 secret/password/key 任何一个词 —— 靠它挡不住。
        CompareRedacted(
            nameof(AppSettings.CameraNetworkUrl), previous.CameraNetworkUrl, next.CameraNetworkUrl);

        Compare(nameof(AppSettings.RecordAudio), previous.RecordAudio, next.RecordAudio);
        Compare(nameof(AppSettings.MicrophoneDevice), previous.MicrophoneDevice, next.MicrophoneDevice);
        Compare(nameof(AppSettings.Rotation), previous.Rotation, next.Rotation);
        Compare(nameof(AppSettings.LogRetainDays), previous.LogRetainDays, next.LogRetainDays);
        Compare(nameof(AppSettings.ArchiveBackend), previous.ArchiveBackend, next.ArchiveBackend);
        Compare(nameof(AppSettings.ArchiveDirectory), previous.ArchiveDirectory, next.ArchiveDirectory);

        // 两张磁盘表（设计图 `_43`）。⚠️ 记的是**合起来的一句话**，不是逐行比 ——
        // 上移/下移会让每一行的序号都变，逐行比会把一次「换个顺序」写成十几行噪音，
        // 而真正要看的是「从哪一串变成了哪一串」。
        Compare("录像保存位置", DescribeSlots(previous.SaveDisks), DescribeSlots(next.SaveDisks));
        Compare("录像备份位置", DescribeSlots(previous.BackupDisks), DescribeSlots(next.BackupDisks));

        // 四个数**各记一行** —— 合成一行的话，看日志的人分不清是哪一份动了。
        // 名字用「已备份 / 未备份」而不是属性名：日志是给人看的，
        // 而这两列的语义相反正是这一节最要紧的一句话（§3.5.2.1）。
        Compare("保留期.发货.已备份", previous.Retention.ArchivedOutbound, next.Retention.ArchivedOutbound);
        Compare("保留期.退货.已备份", previous.Retention.ArchivedReturn, next.Retention.ArchivedReturn);
        Compare("保留期.发货.未备份", previous.Retention.UnarchivedOutbound, next.Retention.UnarchivedOutbound);
        Compare("保留期.退货.未备份", previous.Retention.UnarchivedReturn, next.Retention.UnarchivedReturn);

        // 百度网盘那一组（设计图 `_45`/`_46`）。
        // ⚠️ 「启用自动上传」尤其要留痕：打开它的那一刻起，**这台机器上的录像
        // 会开始往公网上传**。事后一定要能回答「这是谁在什么时候开的」
        // —— 与开机自启动同一档（那一项改的是操作系统，这一项改的是数据出不出机器）。
        // ⚠️ 记的是中文字段名，因为看日志的人是用户，不是读代码的人。
        Compare("百度网盘.启用自动上传", previous.Cloud.AutoUpload, next.Cloud.AutoUpload);
        Compare("百度网盘.自动对比补传", previous.Cloud.CompareAndBackfill, next.Cloud.CompareAndBackfill);
        Compare("百度网盘.补传范围", previous.Cloud.BackfillScope, next.Cloud.BackfillScope);
        Compare("百度网盘.补传起始日", previous.Cloud.BackfillFrom, next.Cloud.BackfillFrom);
        Compare("百度网盘.同时上传数", previous.Cloud.ParallelUploads, next.Cloud.ParallelUploads);
        // 网盘目录名不是凭据（AppKey 才是，而它根本不在这里），可以照记。
        Compare("百度网盘.目录名", previous.Cloud.AppName, next.Cloud.AppName);

        return changes;
    }

    /// <summary>把一串磁盘槽位写成给人看的一行（留痕用）。</summary>
    /// <remarks>
    /// ⚠️ 路径**不脱敏**：它是用户自己选的目录名，不是凭据 ——
    /// 而留痕的意义正是「哪块盘被换掉了」，抹掉就白记了。
    /// （与 <c>CameraNetworkUrl</c> 不同，那个里面带着摄像头密码。）
    /// </remarks>
    private static string DescribeSlots(IReadOnlyList<DiskSlot>? slots) =>
        slots is null or { Count: 0 }
            ? "（默认目录）"
            : string.Join(" → ", slots.Select(s => $"{s.Path}[预留 {s.ReservedGb?.ToString() ?? "默认"}GB]"));

    /// <remarks>
    /// 判据搬到了 <see cref="SensitiveName.Is"/>（日志落盘前脱敏要用同一套）。
    /// 这里是转调，**行为一个字都没变**。
    /// </remarks>
    private static bool IsSensitive(string name) => SensitiveName.Is(name);
}
