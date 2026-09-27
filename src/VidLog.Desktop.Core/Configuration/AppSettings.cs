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

    /// <summary>扫码枪判定参数（规格 §3.2.1）。</summary>
    public ScannerOptions Scanner { get; init; } = new();

    /// <summary>日志保留天数。</summary>
    public int LogRetainDays { get; init; } = 14;

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
    public ArchiveTarget Archive => ArchiveTarget.FromConfig(ArchiveBackend, ArchiveDirectory);

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
    public RetentionSettings Retention { get; init; } = RetentionSettings.KeepAll;

    public static AppSettings Default { get; } = new();

    /// <summary>允许的参数范围。越界即回落，不静默接受。</summary>
    /// <remarks>
    /// 分段时长定在 10 秒~10 分钟、端口定在 1024~65535：越界的值不会报错，
    /// 只会让行为变得莫名其妙（端口 0 起不来、分段 0 秒疯狂滚段），
    /// 那比拒绝更糟。
    /// </remarks>
    public static bool IsPlausible(AppSettings s) =>
        Enum.IsDefined(s.Codec)
        && Enum.IsDefined(s.Resolution)
        && s.SegmentMinutes is >= 1 and <= 10
        && s.PlaybackPort is >= 1024 and <= 65535
        && s.LogRetainDays is >= 1 and <= 365
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
        && PlausibleDays(s.Retention.UnarchivedReturn);

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
                + "（未备份的那一列**永不自动删**，它到期只提醒。）");
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
        Compare(nameof(AppSettings.LogRetainDays), previous.LogRetainDays, next.LogRetainDays);
        Compare(nameof(AppSettings.ArchiveBackend), previous.ArchiveBackend, next.ArchiveBackend);
        Compare(nameof(AppSettings.ArchiveDirectory), previous.ArchiveDirectory, next.ArchiveDirectory);

        // 四个数**各记一行** —— 合成一行的话，看日志的人分不清是哪一份动了。
        // 名字用「已备份 / 未备份」而不是属性名：日志是给人看的，
        // 而这两列的语义相反正是这一节最要紧的一句话（§3.5.2.1）。
        Compare("保留期.发货.已备份", previous.Retention.ArchivedOutbound, next.Retention.ArchivedOutbound);
        Compare("保留期.退货.已备份", previous.Retention.ArchivedReturn, next.Retention.ArchivedReturn);
        Compare("保留期.发货.未备份", previous.Retention.UnarchivedOutbound, next.Retention.UnarchivedOutbound);
        Compare("保留期.退货.未备份", previous.Retention.UnarchivedReturn, next.Retention.UnarchivedReturn);

        return changes;
    }

    /// <remarks>
    /// 判据搬到了 <see cref="SensitiveName.Is"/>（日志落盘前脱敏要用同一套）。
    /// 这里是转调，**行为一个字都没变**。
    /// </remarks>
    private static bool IsSensitive(string name) => SensitiveName.Is(name);
}
