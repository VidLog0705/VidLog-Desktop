using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VidLog.Desktop.Core.Configuration;

/// <summary>
/// 一条路径落在什么样的存储上。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>这是「判出来的」，不是「用户选的」。</b>2026-10-13 之前，归档层是哪一档
/// 由用户在设置页的下拉里**自己声明**（`ArchiveBackendKind`），而那个声明与路径本身
/// 没有任何校验 —— 声明成「NAS」、路径填 <c>C:\Users\…\Videos</c>，程序照收，
/// 清理入口照开。后果是「两份都在本机」被当成「别处还有一份」，删掉之后只剩零份。
/// </para>
/// <para>
/// 判据照抄自前身产品 <c>PackingProof</c> 的 <c>StorageVolumeInfo</c>（那台机器上
/// 已经跑了很久）：**盘符背后是不是物理磁盘设备**。常见虚拟盘驱动
/// （Dokan / WinFsp / CloudDrive2 —— 夸克、坚果云这类网盘挂载出来的盘）
/// 的 <c>GetFinalPathNameByHandle</c> 会失败，所以只能问内核设备名。
/// </para>
/// </remarks>
public enum StorageVolumeKind
{
    /// <summary>判不出来：路径不合法、盘不在、或者不是 Windows。**从严处理**。</summary>
    Unknown,

    /// <summary>明确是**本机的物理卷**（硬盘卷 / 光驱 / 软驱 / 内存盘）。</summary>
    LocalPhysical,

    /// <summary>网络：UNC 共享（<c>\\nas\share</c>），或映射出来的盘符（<c>Z:</c>）。</summary>
    Network,

    /// <summary>盘符背后不是物理磁盘 —— 网盘挂载出来的虚拟盘。</summary>
    VirtualDisk,
}

/// <summary>
/// 「这条路径能不能当录像备份位置」的裁定。
/// </summary>
public enum BackupPathDecision
{
    /// <summary>收。</summary>
    Accept,

    /// <summary>看着像网盘挂载出来的虚拟盘 —— 让用户确认一句再收。</summary>
    ConfirmVirtualDisk,

    /// <summary>判不出来 —— 让用户确认一句再收（**不拒**：拒了会挡住真能用的位置）。</summary>
    ConfirmUnknown,

    /// <summary>拒：这是本机的一块物理盘，它和录像存在同一台机器上。</summary>
    RejectLocalPhysical,
}

/// <summary>
/// 判断一条路径落在什么样的存储上（规格 §3.5.1「归档层是不是就在这台电脑上」）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 这个类**不做任何 I/O 之外的事**，也不缓存：盘符会在运行期间被插拔。
/// 每次现算。
/// </para>
/// <para>
/// ⚠️ <b>判不出来时是 <see cref="StorageVolumeKind.Unknown"/>，不是
/// <see cref="StorageVolumeKind.LocalPhysical"/>。</b>两者在裁定里都**不会**被直接放行，
/// 但说出去的话不一样：「明确是本机盘」要敢拒，「判不出来」只能让用户自己确认 ——
/// 把判不出来说成「这是本机盘」，会把一块真能用的 NAS 拦在门外。
/// </para>
/// </remarks>
public static class StorageVolumeClassifier
{
    /// <summary>这条路径落在什么样的存储上。</summary>
    public static StorageVolumeKind Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return StorageVolumeKind.Unknown;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path.Trim().Trim('"')));

            if (string.IsNullOrWhiteSpace(root))
            {
                return StorageVolumeKind.Unknown;
            }

            // UNC（`\\server\share`）没有盘符，一望即知。
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return StorageVolumeKind.Network;
            }

            if (!OperatingSystem.IsWindows())
            {
                // 本产品只有 Windows 端。别的系统上一律「判不出来」，由用户确认。
                return StorageVolumeKind.Unknown;
            }

            var drive = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            try
            {
                if (new DriveInfo(root).DriveType == DriveType.Network)
                {
                    return StorageVolumeKind.Network;
                }
            }
            catch (Exception ex)
                when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // 盘不在、读不到 —— 继续往下判。
            }

            // 有些映射盘 `DriveType` 报 `Unknown`，再问一次 WNet。
            if (MappedTarget(drive) is not null)
            {
                return StorageVolumeKind.Network;
            }

            var device = DeviceName(drive);

            return device is null
                ? StorageVolumeKind.Unknown
                : IsPhysicalDevice(device)
                    ? StorageVolumeKind.LocalPhysical
                    : StorageVolumeKind.VirtualDisk;
        }
        catch (Exception ex)
            when (ex is ArgumentException or NotSupportedException or PathTooLongException
                  or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return StorageVolumeKind.Unknown;
        }
    }

    /// <summary>把卷的种类裁定成「能不能当备份位置」。</summary>
    public static BackupPathDecision DecideForBackup(StorageVolumeKind kind) => kind switch
    {
        StorageVolumeKind.Network => BackupPathDecision.Accept,
        StorageVolumeKind.VirtualDisk => BackupPathDecision.ConfirmVirtualDisk,
        StorageVolumeKind.LocalPhysical => BackupPathDecision.RejectLocalPhysical,
        _ => BackupPathDecision.ConfirmUnknown,
    };

    /// <summary>
    /// 给用户看的那句话。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>不带任何标记符号</b>：它最终落到设置页那行 <c>TextBlock</c> 与
    /// <c>MessageBox</c> 上，两处都**不认 Markdown**，写了 <c>**</c> 会照字面显示出来
    /// （与 <see cref="ArchivePublishResult.Conflict"/> 那条同一个理由）。
    /// </remarks>
    public static string Explain(BackupPathDecision decision, string path) => decision switch
    {
        BackupPathDecision.Accept =>
            "这是网络位置，可以用。",
        BackupPathDecision.ConfirmVirtualDisk =>
            $"{path}\n\n这个盘符背后不是一块物理硬盘 —— 看着像网盘挂载出来的虚拟盘。"
            + "如果是，它和本机不在同一台机器上，可以当备份位置；"
            + "如果你不确定它是怎么来的，先别用。",
        BackupPathDecision.ConfirmUnknown =>
            $"{path}\n\n说不出这块盘是什么来路（可能刚插上、也可能是虚拟盘）。"
            + "要拿它当备份位置的话，请自己确认它不在本机这块硬盘上。",
        _ =>
            $"{path}\n\n这是本机的一块物理盘。备份位置必须是别处 —— "
            + "否则两份会坏在同一台机器上，而清理时程序会以为「别处已经有一份」，"
            + "把本机这份删掉。请改用 NAS、网络位置，或百度网盘。",
    };

    /// <summary>盘符映射到了哪个网络共享，没映射返回 <see langword="null"/>。</summary>
    private static string? MappedTarget(string drive)
    {
        try
        {
            var buffer = new StringBuilder(512);
            var length = buffer.Capacity;

            return WNetGetConnection(drive, buffer, ref length) == 0 ? buffer.ToString() : null;
        }
        catch (Exception ex)
            when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>盘符（<c>Z:</c>）背后的内核设备名，问不到返回 <see langword="null"/>。</summary>
    private static string? DeviceName(string drive)
    {
        try
        {
            var buffer = new StringBuilder(512);

            return QueryDosDevice(drive, buffer, (uint)buffer.Capacity) == 0
                ? null
                : buffer.ToString();
        }
        catch (Exception ex)
            when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// 设备名算不算**物理本地卷**（硬盘卷 / 光驱 / 软驱 / 内存盘）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 除了这四种，其余自定义设备名一律当「虚拟盘挂载」—— 宁可多问用户一句，
    /// 也不要把一个网盘挂载盘当成本机盘**拒掉**（那会让用户根本用不了它）。
    /// </remarks>
    private static bool IsPhysicalDevice(string devicePath)
    {
        var name = devicePath.Trim();

        return name.StartsWith(@"\Device\HarddiskVolume", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(@"\Device\CdRom", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(@"\Device\Floppy", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(@"\Device\RamDisk", StringComparison.OrdinalIgnoreCase);
    }

    // ponytail: 只判**盘符根**，不穿透目录联接 / 符号链接。所以「C: 上一个指向虚拟盘的
    // 联接目录」会被当成普通本地盘（前身产品还要 GetFinalPathNameByHandle 再判一道）。
    // 已知上限、够用：本产品要求用户填的是 `\\nas\vidlog` 或 `Z:\vidlog` 这种根路径。
    // 真遇上联接挂载了再加 `ResolveFinalPathOfNearestExistingAncestor`。

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern uint WNetGetConnection(
        string lpLocalName, StringBuilder lpRemoteName, ref int lpnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern uint QueryDosDevice(
        string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);
}
