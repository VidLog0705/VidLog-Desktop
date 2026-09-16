using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Recording;

/// <summary>磁盘余量的探测口。</summary>
public interface IDiskSpaceProbe
{
    /// <summary>返回指定路径所在卷的剩余可用字节数。</summary>
    long GetFreeBytes(string path);
}

/// <summary>真实实现。</summary>
public sealed class DriveSpaceProbe : IDiskSpaceProbe
{
    public long GetFreeBytes(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);

        if (string.IsNullOrEmpty(root))
        {
            throw new IOException($"无法确定路径所在卷：{path}");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }
}

/// <summary>
/// 磁盘余量阈值。
/// </summary>
/// <remarks>
/// 规格 §3.1.1：这三项（存储将满 / 低电量 / 过热）的阈值**由配置下发**，
/// 且**必须有本地硬兜底值**。这里的默认值就是那个硬兜底 ——
/// 远端配置缺失或坏掉时用它，录制照常（I4）。
/// <para>
/// 数值依据：1080p H.264 大约 4~8 Mbps，即 0.5~1 MB/s。
/// 警告线 2 GB 留出约 30~60 分钟；停止线 512 MB 留出约 8~17 分钟，
/// 足够收尾 —— 而收尾本身还需要空间（remux 时 MKV 与 MP4 会同时存在）。
/// </para>
/// </remarks>
public sealed record DiskSpaceOptions
{
    /// <summary>低于此值就告警（不停止录制）。</summary>
    public long WarningFreeBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>低于此值就主动安全收尾，不等写失败。</summary>
    public long StopFreeBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>
    /// 收尾本身需要的额外空间。
    /// </summary>
    /// <remarks>
    /// remux 期间中间容器（MKV）与成品（MP4）**同时存在**，所以停止线之上
    /// 还要再留一份。不留的话会出现「为了安全而收尾，结果收尾到一半磁盘满了」——
    /// 那正好产出一个不可播放的半成品，与收尾的目的相反。
    /// </remarks>
    public long FinalizeHeadroomBytes { get; init; } = 512L * 1024 * 1024;
}

/// <summary>一次磁盘检查的结论。</summary>
public sealed record DiskSpaceVerdict(
    bool ShouldWarn,
    bool ShouldFinalize,
    long FreeBytes,
    string? Message);

/// <summary>
/// 存储将满的提前告警与主动安全收尾。
/// </summary>
/// <remarks>
/// 规格 §3.1.1：存储将满时**提前告警，并主动安全收尾**（正常关闭当前分段、
/// 写指纹、入库），**而不是等崩溃**。
/// <para>
/// 「主动安全收尾」在本实现里就是返回 <see cref="DiskSpaceVerdict.ShouldFinalize"/>，
/// 由调用方以 <see cref="StopReason.StorageLow"/> 走
/// <see cref="SessionFinalizer"/> —— **还是那条唯一路径**，不是旁路。
/// </para>
/// </remarks>
public sealed class DiskSpaceGuard
{
    private readonly IDiskSpaceProbe _probe;
    private readonly DiskSpaceOptions _options;

    public DiskSpaceGuard(IDiskSpaceProbe probe, DiskSpaceOptions? options = null)
    {
        _probe = probe;
        _options = options ?? new DiskSpaceOptions();
    }

    public DiskSpaceVerdict Check(string path)
    {
        long free;
        try
        {
            free = _probe.GetFreeBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 探测不了磁盘余量**不能**让录制停下来（I4 的同一条精神）——
            // 就当它没问题继续录，比误停一个正在正常工作的录制要好。
            return new DiskSpaceVerdict(false, false, -1, $"无法探测磁盘余量：{ex.Message}");
        }

        if (free < _options.StopFreeBytes + _options.FinalizeHeadroomBytes)
        {
            return new DiskSpaceVerdict(
                true,
                true,
                free,
                $"磁盘余量 {Format(free)}，不足以安全收尾（需要 {Format(_options.StopFreeBytes + _options.FinalizeHeadroomBytes)}）");
        }

        if (free < _options.WarningFreeBytes)
        {
            return new DiskSpaceVerdict(
                true,
                false,
                free,
                $"磁盘余量偏低：{Format(free)}");
        }

        return new DiskSpaceVerdict(false, false, free, null);
    }

    private static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (double)(1024 * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (double)(1024 * 1024):0.#} MB",
        _ => $"{bytes} 字节",
    };
}
