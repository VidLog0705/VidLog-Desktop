using System.Text.Json;
using System.Text.Json.Serialization;
using VidLog.Desktop.Core.Cleanup;
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

    /// <summary>静止停录档位（规格 §3.3.3）。</summary>
    public StaticStopOption StaticStop { get; init; } = StaticStopOption.Three;

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
    /// 归档层是本机磁盘、NAS、还是网盘（规格 §3.5.1）。
    /// </summary>
    /// <remarks>
    /// 默认<see cref="ArchiveBackendKind.LocalDisk"/> = 盘上这份是**唯一副本**，
    /// 于是规格 §3.5.1 不允许开启清理、界面上**不给保留期设置入口**。
    /// 这个默认值就是「什么都没配过」时的真实处境，不是保守起见。
    /// </remarks>
    public ArchiveBackendKind ArchiveBackend { get; init; } = ArchiveBackendKind.LocalDisk;

    /// <summary>归档成功后本地留多久，发货与退货各一份（规格 §3.5.2.1）。</summary>
    /// <remarks>
    /// 默认两份都是<see cref="RetentionPolicies.KeepAll"/>：规格 §6.2「数据删除必须极度克制」，
    /// 清理必须是用户**主动开启**的。
    /// </remarks>
    public RetentionPolicies Retention { get; init; } = RetentionPolicies.KeepAll;

    public static AppSettings Default { get; } = new();

    /// <summary>允许的参数范围。越界即回落，不静默接受。</summary>
    /// <remarks>
    /// 分段时长定在 10 秒~10 分钟、端口定在 1024~65535：越界的值不会报错，
    /// 只会让行为变得莫名其妙（端口 0 起不来、分段 0 秒疯狂滚段），
    /// 那比拒绝更糟。
    /// </remarks>
    public static bool IsPlausible(AppSettings s) =>
        s.SegmentMinutes is >= 1 and <= 10
        && s.PlaybackPort is >= 1024 and <= 65535
        && s.LogRetainDays is >= 1 and <= 365
        && s.Scanner.MaxInterKeyIntervalMs is >= 10 and <= 500
        && s.Scanner.MinLength is >= 1 and <= 64
        && s.Scanner.MaxLength is >= 1 and <= 256
        && s.Scanner.MaxLength >= s.Scanner.MinLength
        && Plausible(s.Retention.Outbound)
        && Plausible(s.Retention.Return);

    /// <summary>
    /// 一份保留期的参数自洽吗。
    /// </summary>
    /// <remarks>
    /// 这里拦的是**手改设置文件**能造出来的坑：负数天数会让
    /// <c>now.AddDays(-(-5))</c> 变成「cutoff 在未来」，于是一律判超期 ⇒
    /// 除了被豁免的全删。这类值不会报错，只会把东西删光 —— 正是本方法存在的理由。
    /// </remarks>
    private static bool Plausible(RetentionPolicy p) => p.Mode switch
    {
        RetentionMode.KeepAll => true,
        RetentionMode.ByDays => p.KeepDays is >= 0 and <= 365,
        RetentionMode.BySpace => p.MinFreeBytes is > 0,
        _ => false,
    };
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

        return new SettingsLoadResult(parsed, []);
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
                changes.Add(IsSensitive(name) ? $"{name}（已修改）" : $"{name}: {a} → {b}");
            }
        }

        Compare(nameof(AppSettings.Mode), previous.Mode, next.Mode);
        Compare(nameof(AppSettings.StaticStop), previous.StaticStop, next.StaticStop);
        Compare(nameof(AppSettings.DurationFallback), previous.DurationFallback, next.DurationFallback);
        Compare(nameof(AppSettings.SegmentMinutes), previous.SegmentMinutes, next.SegmentMinutes);
        Compare(nameof(AppSettings.PlaybackPort), previous.PlaybackPort, next.PlaybackPort);
        Compare(nameof(AppSettings.CameraDevice), previous.CameraDevice, next.CameraDevice);
        Compare(nameof(AppSettings.LogRetainDays), previous.LogRetainDays, next.LogRetainDays);
        Compare(nameof(AppSettings.ArchiveBackend), previous.ArchiveBackend, next.ArchiveBackend);

        // 两份保留期分开记 —— 合成一行的话，看日志的人分不清是哪一份动了。
        Compare($"{nameof(AppSettings.Retention)}.Outbound", previous.Retention.Outbound, next.Retention.Outbound);
        Compare($"{nameof(AppSettings.Retention)}.Return", previous.Retention.Return, next.Retention.Return);

        return changes;
    }

    private static bool IsSensitive(string name) =>
        name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("key", StringComparison.OrdinalIgnoreCase);
}
