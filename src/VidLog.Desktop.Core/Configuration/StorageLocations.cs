using VidLog.Desktop.Core.Cleanup;

namespace VidLog.Desktop.Core.Configuration;

/// <summary>
/// 一个「可以放录像」的位置，以及它要留出多少空间不写。
/// </summary>
/// <remarks>
/// <para>
/// 依据是需求方设计图 `_43`「存储与备份」：两张表，每行一个路径 + 一格「预留空间」
/// （图上 D 盘写 <c>28 GB</c>、C 盘写 <c>30 GB</c>），底下那句原话是
/// 「每个磁盘只剩下预留空间时，会自动换到下一个磁盘；系统盘默认至少预留 30GB 或 10%，
/// 其他磁盘至少预留 20GB 或 5%，**以较大值为准**」。
/// </para>
/// <para>
/// ⚠️ <b>预留空间不是硬下限，是默认值</b>：图上那两个数反推得出来 ——
/// 560GB 的盘 <c>max(20, 28) = 28</c>、系统盘 <c>max(30, 10%) = 30</c>，
/// 两个都对得上。所以用户填多少就是多少，见
/// <see cref="DiskSpace.DefaultReservedGb"/>。
/// </para>
/// </remarks>
/// <param name="Path">录像落盘的目录。**必须写完整路径**（盘符或 UNC）。</param>
/// <param name="ReservedGb">
/// 这块盘要留出多少 GB 不写。<see langword="null"/> = 还没设过，
/// 按 <see cref="DiskSpace.DefaultReservedGb"/> 现算 —— **不落盘**，
/// 因为盘换了容量那个默认值就得跟着变。
/// </param>
public sealed record DiskSlot(string Path, int? ReservedGb = null);

/// <summary>
/// 一块盘的容量读数。<see langword="null"/> 表示**读不到**。
/// </summary>
/// <remarks>
/// ⚠️ 读不到**不是**「没有空间」（I8 的同一条口径）：把探不到当成 0
/// 会让应用拒录，把探不到当成很大又会让它一直写一块快满的盘。
/// 所以它自己是一个状态，由调用方决定怎么落。
/// </remarks>
public sealed record VolumeSpace(long FreeBytes, long TotalBytes);

/// <summary>读卷容量。</summary>
/// <remarks>
/// 抽成接口只为一件事：<b>让「挑哪块盘」这件事不依赖真实磁盘就能测</b>。
/// 满盘换盘是这一批最容易写错的一处（判反了就是「一直往一块满盘上写」），
/// 而真实磁盘没法在测试里填满。
/// </remarks>
public interface IVolumeSpaceProbe
{
    /// <summary>读这个路径所在卷的余量与总量；读不到返回 <see langword="null"/>。</summary>
    VolumeSpace? Measure(string path);
}

/// <summary>真实实现。走 <see cref="DriveInfo"/>。</summary>
public sealed class DriveVolumeProbe : IVolumeSpaceProbe
{
    public VolumeSpace? Measure(string path)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));

            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);

            // ⚠️ `AvailableFreeSpace`（当前用户可用）而不是 `TotalFreeSpace` ——
            // 有配额时前者才是我们真能写的量。
            return new VolumeSpace(drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // UNC 路径、掉线的盘、没有权限 —— 一律「读不到」，由调用方决定怎么落。
            return null;
        }
    }
}

/// <summary>
/// 磁盘容量与「预留空间」换算到本类要用的那几个形状。
/// </summary>
/// <remarks>
/// ⚠️ <b>那两个默认值本身在 <see cref="ReservedSpace"/> 里，这里只是转调</b>
/// （字节 ↔ GB、以及「路径在不在系统盘上」）。各写一份的话，
/// 迟早出现「新建一条磁盘时预填 28GB、挑盘时按 30GB 判」那种自相矛盾 ——
/// 而它表现为「明明还有空间却不换盘」，没人查得出来。
/// </remarks>
public static class DiskSpace
{
    public const long Gigabyte = 1024L * 1024 * 1024;

    /// <summary>这块盘的默认预留空间（GB）。没设过时用它。</summary>
    /// <remarks>
    /// ⚠️ 读不到容量时 <see cref="ReservedSpace.DefaultFor"/> 回落到固定下限
    /// （30 / 20 GB）而不是 0 —— 0 意味着「可以把盘写满」，而写满意味着
    /// 收尾时没有空间 remux，正好产出一个不可播放的半成品。
    /// </remarks>
    public static int DefaultReservedGb(long? totalBytes, bool isSystemDrive) =>
        (int)(ReservedSpace.DefaultFor(isSystemDrive, totalBytes ?? 0) / Gigabyte);

    /// <summary>
    /// 这个路径是不是在系统盘上。
    /// </summary>
    /// <remarks>
    /// ⚠️ 判据是**盘符**（<c>%SystemDrive%</c>），不是「装 Windows 的那个目录」——
    /// 图上的区别正是「系统盘留 30GB、别的盘留 20GB」这条按盘分的规矩。
    /// UNC 路径没有盘符 ⇒ 一律算「别的盘」（NAS 不装 Windows）。
    /// </remarks>
    public static bool IsSystemDrive(string path)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            var system = System.IO.Path.GetPathRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows));

            return !string.IsNullOrEmpty(root)
                && !string.IsNullOrEmpty(system)
                && string.Equals(
                    root.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                    system.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>一个槽位**当下**的预留空间（GB）。没设过就现算。</summary>
    public static int EffectiveReservedGb(DiskSlot slot, VolumeSpace? space) =>
        slot.ReservedGb ?? DefaultReservedGb(space?.TotalBytes, IsSystemDrive(slot.Path));
}

/// <summary>
/// 录像成品落在哪些根上 —— 有序、可回查、写的时候按列表顺序挑一个还有余量的。
/// </summary>
/// <remarks>
/// <para>
/// <b>它解决的是一个真问题，不是为多磁盘而多磁盘</b>：在它之前，「本机这一份在哪」
/// 是一个写死的字符串（<c>DataLayout.ArchiveRoot</c>），而索引里存的是**相对路径**
/// （规格 §6.2）。于是「录像分布在几块盘上」就永远查不回来了。
/// 有了它，相对路径 + 这一串根 = 任意一条录像的绝对路径。
/// </para>
/// <para>
/// ⚠️ <b>兜底根永远在列表里，而且永远排最后</b>：它是
/// <c>DataLayout.ArchiveRoot</c>（<c>%LOCALAPPDATA%\VidLog\archive</c>），
/// 老配置、老录像全在那儿。**只有列表为空时它才当写入口** ——
/// 一旦用户配了盘，新录像就写那些盘，而老录像照样回查得到。
/// </para>
/// <para>
/// ⚠️ <b>读的根比写的根多</b>，这是刻意的：写只能挑一个，读必须找得到全部。
/// 把两者合成一个「当前根」的话，用户加一块盘之后上一块盘上的录像就全「不见了」
/// —— 那种「东西还在但界面说没有」是最容易让人做出错误决定的一种坏法。
/// </para>
/// </remarks>
public sealed class StorageLocations
{
    private readonly DiskSlot[] _slots;
    private readonly IVolumeSpaceProbe _probe;

    /// <param name="slots">
    /// 用户配的落盘位置，<b>有序</b> —— 按列表顺序挑，第一个还有余量的就用它。
    /// </param>
    /// <param name="fallbackRoot">
    /// 兜底根（<c>DataLayout.ArchiveRoot</c>）。**必须给**：没有它的话
    /// 一台从没配过盘的机器就没地方写录像了。
    /// </param>
    public StorageLocations(
        IReadOnlyList<DiskSlot>? slots,
        string fallbackRoot,
        IVolumeSpaceProbe? probe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackRoot);

        _slots = [.. slots ?? []];
        _probe = probe ?? new DriveVolumeProbe();
        FallbackRoot = fallbackRoot;
    }

    /// <summary>只认一个根的那种（老行为、以及测试里最常用的那种）。</summary>
    public static StorageLocations Single(string root) => new(null, root);

    /// <summary>兜底根。老录像与「一个盘都没配」的机器都靠它。</summary>
    public string FallbackRoot { get; }

    /// <summary>用户配的落盘位置，有序。</summary>
    public IReadOnlyList<DiskSlot> Slots => _slots;

    /// <summary>回查时要挨个试的根，<b>有序</b>：用户配的在前，兜底根在最后。</summary>
    /// <remarks>
    /// ⚠️ <b>去重</b>：用户完全可能把 <c>%LOCALAPPDATA%\VidLog\archive</c>
    /// 自己也加进列表，那样同一个根会出现两次 —— 回查要多读一遍盘，
    /// 而「两个根装着同一批文件」还会让按列表计数的界面出现重复行。
    /// </remarks>
    public IReadOnlyList<string> ReadRoots
    {
        get
        {
            var seen = new List<string>(_slots.Length + 1);

            foreach (var path in _slots.Select(s => s.Path).Append(FallbackRoot))
            {
                if (!seen.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    seen.Add(path);
                }
            }

            return seen;
        }
    }

    /// <summary>这一次该往哪儿写。按列表顺序挑第一个还有余量的。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>一个都没余量时仍然返回第一个槽位，而不是抛、也不是拒录</b>。
    /// 预留空间是我们**自己**给自己定的规矩，而录像是这个产品的主线 ——
    /// 为了守一个保守阈值把录像挡掉是本末倒置（I4 的同一条精神：
    /// 降级绝不能弄失败录制）。代价写进 <see cref="ActiveNote"/> 让界面说出来。
    /// </para>
    /// <para>
    /// ⚠️ 它**每次调用都重新探盘**：换盘要能在一台机器不停机的情况下发生
    /// （录了一天，第一块盘满了）。缓存住的余量会一直骗自己。
    /// </para>
    /// </remarks>
    public string ActiveRoot => PickActive(out _, out _);

    /// <summary>
    /// 为什么是它 —— 给人看的一句话。界面与日志都用这一句。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它会**再探一次盘**：余量是随时在变的，把上一次 <see cref="ActiveRoot"/>
    /// 的读数缓存下来，说出来的话就可能与刚写下去的东西对不上。
    /// 一次 <c>DriveInfo</c> 查询是毫秒级，而这句话只在写日志与画界面时取。
    /// </remarks>
    public string ActiveNote
    {
        get
        {
            PickActive(out _, out var note);
            return note;
        }
    }

    /// <summary>当前用的是不是兜底根（即：一块盘都没配）。</summary>
    public bool IsFallbackActive => _slots.Length == 0;

    private string PickActive(out int index, out string note)
    {
        if (_slots.Length == 0)
        {
            index = -1;
            note = "还没有配置录像保存位置，先写在默认目录里。";
            return FallbackRoot;
        }

        var unreadable = 0;

        for (var i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            var space = _probe.Measure(slot.Path);

            if (space is null)
            {
                // 读不到余量**不当作没空间**（I8 的同一条口径）—— 按「能用」处理，
                // 否则一次网络抖动就能让录像换到另一块盘上去，而用户什么都没干。
                unreadable++;
                continue;
            }

            var reserved = DiskSpace.EffectiveReservedGb(slot, space) * DiskSpace.Gigabyte;

            if (space.FreeBytes > reserved)
            {
                index = i;
                note = $"写在列表第 {i + 1} 个位置（{slot.Path}），"
                    + $"余量 {Format(space.FreeBytes)}，预留 {reserved / DiskSpace.Gigabyte} GB。";
                return slot.Path;
            }
        }

        index = 0;
        note = unreadable == _slots.Length
            ? $"{_slots.Length} 个位置的余量都读不到，按列表第一个来：{_slots[0].Path}。"
            : $"列表里每个位置都只剩预留空间了，仍然写在第一个：{_slots[0].Path}"
              + " —— 录像优先，请尽快腾地方。";

        return _slots[0].Path;
    }

    /// <summary>把相对路径还原成绝对路径；<b>哪个根上有就用哪个</b>。</summary>
    /// <returns>找不到返回 <see langword="null"/>（**不是**「返回兜底根下的路径」）。</returns>
    /// <remarks>
    /// ⚠️ 「找不到就拼兜底根」是这里最容易犯的错：那样调用方拿到的永远是一个路径，
    /// 于是「这条录像不在了」与「这条录像在别的盘上」变成同一件事，
    /// 界面只会说「找不到」，而用户没被告诉去看别的盘。
    /// </remarks>
    public string? Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        foreach (var root in ReadRoots)
        {
            var candidate = System.IO.Path.Combine(root, relativePath);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>相对路径应该落在哪个根上 —— 找不到时给活动根下的路径，供报错用。</summary>
    public string ResolveOrActive(string relativePath) =>
        Resolve(relativePath) ?? System.IO.Path.Combine(ActiveRoot, relativePath);

    private static string Format(long bytes) => bytes switch
    {
        >= DiskSpace.Gigabyte => $"{bytes / (double)DiskSpace.Gigabyte:0.#} GB",
        >= 1024 * 1024 => $"{bytes / (double)(1024 * 1024):0.#} MB",
        _ => $"{bytes} 字节",
    };

    /// <summary>
    /// 一次 <see cref="Resolve"/> 的结论，带上「在哪个根上」。
    /// </summary>
    /// <remarks>
    /// 清理要它：删本机那一份之前得知道它到底在哪块盘上，
    /// 而删完之后那块盘就是可以报给用户的一句话（「D 盘释放了 3.2 GB」）。
    /// </remarks>
    public (string Path, string Root)? Locate(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        foreach (var root in ReadRoots)
        {
            var candidate = System.IO.Path.Combine(root, relativePath);

            if (File.Exists(candidate))
            {
                return (candidate, root);
            }
        }

        return null;
    }

    /// <summary>把每个根当下的实情列出来，给设置页的两张表和容量条用。</summary>
    public IReadOnlyList<DiskSlotStatus> Describe()
    {
        var result = new List<DiskSlotStatus>(_slots.Length);

        foreach (var slot in _slots)
        {
            var space = _probe.Measure(slot.Path);
            var reserved = DiskSpace.EffectiveReservedGb(slot, space);

            result.Add(new DiskSlotStatus(
                slot,
                space,
                reserved,
                space is null
                    ? "读不到这块盘的容量"
                    : space.FreeBytes <= reserved * DiskSpace.Gigabyte
                        ? $"只剩 {Format(space.FreeBytes)}，已经到预留线了"
                        : null));
        }

        return result;
    }
}

/// <summary>一个槽位当下的状态（给界面用）。</summary>
/// <param name="Slot">配置里那一条。</param>
/// <param name="Space">容量读数；<see langword="null"/> = 读不到。</param>
/// <param name="ReservedGb">**当下生效**的预留空间（可能是现算的默认值）。</param>
/// <param name="Problem">给人看的问题；没问题为 <see langword="null"/>。</param>
public sealed record DiskSlotStatus(
    DiskSlot Slot,
    VolumeSpace? Space,
    int ReservedGb,
    string? Problem);
