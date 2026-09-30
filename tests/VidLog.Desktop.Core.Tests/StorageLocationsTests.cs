using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 多磁盘：写挑哪一块、回查去哪几块找（设计图 `_43` 的两张表）。
/// </summary>
/// <remarks>
/// <para>
/// 这一批最容易写错的地方是**判反**，而判反都不会报错：
/// 把「读不到余量」当成「没空间」⇒ 一次网络抖动就让录像换盘；
/// 把「都没余量」当成「拒录」⇒ 明明该录的时候没录（I4）。
/// 所以下面钉得比较死。
/// </para>
/// <para>
/// 容量读数走 <see cref="IVolumeSpaceProbe"/> 的假实现 —— 真实磁盘在测试里填不满，
/// 而「满盘换盘」正是这一段的核心。**验得了的是判据，验不了的是真盘上的数值**。
/// </para>
/// </remarks>
public class StorageLocationsTests
{
    private const long Gb = DiskSpace.Gigabyte;

    private static VolumeSpace Free(long freeGb, long totalGb) => new(freeGb * Gb, totalGb * Gb);

    // ─────────────────────────────────────────────
    // 挑哪一块写
    // ─────────────────────────────────────────────

    [Fact]
    public void 没配过盘时写在兜底根_而且说得出来()
    {
        // 升级过的机器就是这样：`SaveDisks` 是空的，而老录像全在
        // `%LOCALAPPDATA%\VidLog\archive` 里。这条不清不楚的话，
        // 「我的录像去哪了」就没有答案。
        var storage = new StorageLocations(null, @"C:\兜底", new FakeProbe());

        Assert.Equal(@"C:\兜底", storage.ActiveRoot);
        Assert.True(storage.IsFallbackActive);
        Assert.Contains("默认目录", storage.ActiveNote);

        // ⚠️ 泛型参数要**写出来**：`Assert.Equal<T>(T, T)` 比
        // `Assert.Equal<T>(IEnumerable<T>, IEnumerable<T>)` 更匹配，
        // 不写的话这里会落回**引用相等**，断言就成了摆设（本仓踩过）。
        Assert.Equal<string>([@"C:\兜底"], storage.ReadRoots);
    }

    [Fact]
    public void 按列表顺序挑第一个还有余量的()
    {
        var probe = new FakeProbe
        {
            [@"D:\一"] = Free(4, 560),      // 只剩 4GB，低于预留的 5GB
            [@"E:\二"] = Free(500, 1000),
        };

        var storage = new StorageLocations(
            [new DiskSlot(@"D:\一", 5), new DiskSlot(@"E:\二", 5)], @"C:\兜底", probe);

        Assert.Equal(@"E:\二", storage.ActiveRoot);
        Assert.Contains("第 2 个位置", storage.ActiveNote);
    }

    [Fact]
    public void 余量正好等于预留时不算还有余量()
    {
        // 判据是**严格大于**。写 `>=` 的话「刚好只剩预留线」的那一块会被选中，
        // 而它正是「再写一条就吃掉预留」的那一块 —— 换盘要发生在**吃掉之前**。
        var probe = new FakeProbe
        {
            [@"D:\刚好"] = Free(30, 560),
            [@"E:\宽松"] = Free(31, 560),
        };

        var storage = new StorageLocations(
            [new DiskSlot(@"D:\刚好", 30), new DiskSlot(@"E:\宽松", 30)], @"C:\兜底", probe);

        Assert.Equal(@"E:\宽松", storage.ActiveRoot);
    }

    [Fact]
    public void 读不到余量的那块不当作满_但也不抢()
    {
        var probe = new FakeProbe
        {
            [@"D:\掉线"] = null,            // 探不到：网络断了 / 盘掉了 / 没权限
            [@"E:\正常"] = Free(500, 560),
        };

        var storage = new StorageLocations(
            [new DiskSlot(@"D:\掉线", 30), new DiskSlot(@"E:\正常", 30)], @"C:\兜底", probe);

        // ⚠️ 跳过而不是「当成满」：真满了要换盘，探不到只是这一会儿问不着 ——
        // 但后面有一块**探得到且有余量**的盘时，写它是更稳的选择
        // （掉线的那块到底是满还是空，谁也说不准）。
        Assert.Equal(@"E:\正常", storage.ActiveRoot);
    }

    [Fact]
    public void 一块都探不到时仍然写在第一个_不是拒录()
    {
        var probe = new FakeProbe();   // 两块都探不到

        var storage = new StorageLocations(
            [new DiskSlot(@"D:\甲"), new DiskSlot(@"E:\乙")], @"C:\兜底", probe);

        Assert.Equal(@"D:\甲", storage.ActiveRoot);
        Assert.Contains("都读不到", storage.ActiveNote);
        Assert.Contains("第", storage.ActiveNote);      // 说得出「按列表第一个来」
    }

    [Fact]
    public void 都没余量时仍然写在第一个_录像优先()
    {
        // ⚠️ 这条是 **I4**（降级绝不能弄失败录制）在这一层的落点：
        // 预留空间是我们**自己**给自己定的保守阈值，而录像是主线 ——
        // 为了守一个阈值把录像挡掉是本末倒置。
        // 代价是得**说出来**，否则用户只会看到盘满了、不知道为什么还在录。
        var probe = new FakeProbe
        {
            [@"D:\甲"] = Free(1, 560),
            [@"E:\乙"] = Free(1, 560),
        };

        var storage = new StorageLocations(
            [new DiskSlot(@"D:\甲", 30), new DiskSlot(@"E:\乙", 30)], @"C:\兜底", probe);

        Assert.Equal(@"D:\甲", storage.ActiveRoot);
        Assert.Contains("仍然写在第一个", storage.ActiveNote);
        Assert.Contains("录像优先", storage.ActiveNote);
    }

    [Fact]
    public void 每次问都重新探盘_不缓存余量()
    {
        // 换盘要能在一台机器不停机的情况下发生（录了一天，第一块盘满了）。
        // 缓存住的余量会一直骗自己。
        var probe = new FakeProbe { [@"D:\甲"] = Free(100, 560), [@"E:\乙"] = Free(100, 560) };
        var storage = new StorageLocations(
            [new DiskSlot(@"D:\甲", 30), new DiskSlot(@"E:\乙", 30)], @"C:\兜底", probe);

        Assert.Equal(@"D:\甲", storage.ActiveRoot);

        probe[@"D:\甲"] = Free(1, 560);   // 录着录着满了

        Assert.Equal(@"E:\乙", storage.ActiveRoot);
    }

    // ─────────────────────────────────────────────
    // 回查去哪几块找
    // ─────────────────────────────────────────────

    [Fact]
    public void 回查的根是有序的_兜底根永远在最后()
    {
        // ⚠️ 读的根比写的根多，这是刻意的：写只能挑一个，读必须找得到全部。
        // 兜底根排最后保证「先看用户配的盘，再看老录像待着的那个目录」。
        var storage = new StorageLocations(
            [new DiskSlot(@"D:\甲"), new DiskSlot(@"E:\乙")], @"C:\兜底", new FakeProbe());

        Assert.Equal<string>([@"D:\甲", @"E:\乙", @"C:\兜底"], storage.ReadRoots);
    }

    [Fact]
    public void 根里的重复项要去掉_大小写不同也算同一个()
    {
        // 用户完全可能手打一遍路径、又从文件夹对话框选一遍 ——
        // 重复的根会让回查多读一遍盘，而按列表计数的界面会出现重复行。
        var storage = new StorageLocations(
            [new DiskSlot(@"D:\甲"), new DiskSlot(@"d:\甲"), new DiskSlot(@"E:\乙")],
            @"C:\兜底", new FakeProbe());

        Assert.Equal<string>([@"D:\甲", @"E:\乙", @"C:\兜底"], storage.ReadRoots);
    }

    [Fact]
    public void 用户把兜底根也加进列表时_它留在用户放的那个位置()
    {
        // 去重保留**先出现的那个**，所以这里兜底根排第一而不是最后 ——
        // 用户明确写了顺序，就不该被程序悄悄挪。它仍然只出现一次。
        var storage = new StorageLocations(
            [new DiskSlot(@"C:\兜底"), new DiskSlot(@"D:\甲")], @"C:\兜底", new FakeProbe());

        Assert.Equal<string>([@"C:\兜底", @"D:\甲"], storage.ReadRoots);
    }

    [Fact]
    public void 相对路径在哪块盘上就还原到哪块盘()
    {
        // 这是多磁盘存在的**理由**：索引里存的是相对路径（规格 §6.2），
        // 而「录像分布在几块盘上」得靠这一串根才查得回来。
        using var dir = new TempDir();
        var first = System.IO.Path.Combine(dir.Path, "第一块");
        var second = System.IO.Path.Combine(dir.Path, "第二块");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        // 只把文件放在第二块盘上 —— 与「上次录的还在原来的盘上」是同一件事。
        var rel = System.IO.Path.Combine("2026", "09", "30", "SF1", "e-000.mp4");
        var full = System.IO.Path.Combine(second, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, [1, 2, 3]);

        var storage = new StorageLocations(
            [new DiskSlot(first), new DiskSlot(second)],
            System.IO.Path.Combine(dir.Path, "兜底"), new FakeProbe());

        Assert.Equal(full, storage.Resolve(rel));

        var located = storage.Locate(rel);
        Assert.NotNull(located);
        Assert.Equal(second, located.Value.Root);
        Assert.Equal(full, located.Value.Path);
    }

    [Fact]
    public void 哪儿都找不到时给null_不是拼一个兜底根下的路径()
    {
        // ⚠️ 这条是这里最容易犯的错，而后果不小：永远返回一个路径的话，
        // 「这条录像不在了」与「它在别的盘上」就变成同一件事，
        // 界面只会说「找不到」，而用户没被告诉去看别的盘。
        using var dir = new TempDir();
        var root = System.IO.Path.Combine(dir.Path, "第一块");
        Directory.CreateDirectory(root);

        var storage = new StorageLocations(
            [new DiskSlot(root)], System.IO.Path.Combine(dir.Path, "兜底"), new FakeProbe());

        Assert.Null(storage.Resolve(System.IO.Path.Combine("2026", "缺.mp4")));
        Assert.Null(storage.Locate(System.IO.Path.Combine("2026", "缺.mp4")));

        // 空路径也不许造出一个「根目录」来。
        Assert.Null(storage.Resolve("   "));

        // `ResolveOrActive` 是**明说要一个路径**的那一个（给报错文案用），
        // 它给的是活动根下的 —— 名字里那个 Or 就是这个意思。
        Assert.Equal(
            System.IO.Path.Combine(root, "缺.mp4"),
            storage.ResolveOrActive(System.IO.Path.Combine("缺.mp4")));
    }

    // ─────────────────────────────────────────────
    // 预留空间的默认值（图上那句脚注）
    // ─────────────────────────────────────────────

    [Fact]
    public void 默认预留空间按盘分档_系统盘30GB其他盘20GB_以较大值为准()
    {
        // 图上原话：「系统盘默认至少预留 30GB 或 10%，其他磁盘至少预留 20GB 或 5%，
        // **以较大值为准**」。图上 D 盘（560GB）写 28GB —— 正好是 560×5%，
        // 所以那是**现算**出来的，不是两个写死的常数。
        Assert.Equal(30, DiskSpace.DefaultReservedGb(200 * Gb, isSystemDrive: true));
        Assert.Equal(20, DiskSpace.DefaultReservedGb(200 * Gb, isSystemDrive: false));

        Assert.Equal(28, DiskSpace.DefaultReservedGb(560 * Gb, isSystemDrive: false));    // 560×5% = 28
        Assert.Equal(56, DiskSpace.DefaultReservedGb(1120 * Gb, isSystemDrive: false));   // 1120×5% = 56
        Assert.Equal(30, DiskSpace.DefaultReservedGb(280 * Gb, isSystemDrive: true));     // 280×10% = 28 < 30
        Assert.Equal(100, DiskSpace.DefaultReservedGb(1000 * Gb, isSystemDrive: true));   // 1000×10% = 100

        // ⚠️ 读不到容量 ⇒ 回落到固定下限，**不是 0**：0 意味着「可以把盘写满」，
        // 而写满意味着收尾时没有空间 remux，正好产出一个不可播放的半成品。
        Assert.Equal(30, DiskSpace.DefaultReservedGb(null, isSystemDrive: true));
        Assert.Equal(20, DiskSpace.DefaultReservedGb(null, isSystemDrive: false));
    }

    [Fact]
    public void 用户填过的预留空间就用自己的_没填过才现算()
    {
        var space = Free(100, 560);

        // 填过（哪怕填 0）就以他填的为准 —— 0 是「这块盘不预留」，正当选择。
        Assert.Equal(28, DiskSpace.EffectiveReservedGb(new DiskSlot(@"E:\甲", 28), space));
        Assert.Equal(0, DiskSpace.EffectiveReservedGb(new DiskSlot(@"E:\甲", 0), space));

        // 没填过（null）⇒ 按这块盘现算。⚠️ 这个数**不落盘**：
        // 盘换了容量，默认值就得跟着变。
        Assert.Equal(28, DiskSpace.EffectiveReservedGb(new DiskSlot(@"E:\甲"), space));
    }

    [Fact]
    public void 系统盘是按盘符认的_UNC一律算别的盘()
    {
        var system = System.IO.Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

        Assert.True(DiskSpace.IsSystemDrive(System.IO.Path.Combine(system, "VidLog")));
        Assert.False(DiskSpace.IsSystemDrive(@"\\nas\vidlog\archive"));
    }

    // ─────────────────────────────────────────────
    // 给设置页用的那张表
    // ─────────────────────────────────────────────

    [Fact]
    public void 描述得出每一块盘当下的状态_包括到预留线了与读不到()
    {
        var probe = new FakeProbe
        {
            [@"D:\到线"] = Free(10, 560),
            [@"E:\掉线"] = null,
        };

        var rows = new StorageLocations(
            [new DiskSlot(@"D:\到线", 30), new DiskSlot(@"E:\掉线", 30)], @"C:\兜底", probe)
            .Describe();

        Assert.Equal(2, rows.Count);

        // 到预留线不是「读不到」，也不是「满」——它是**该换盘了**的那一档。
        Assert.Equal(30, rows[0].ReservedGb);
        Assert.NotNull(rows[0].Problem);
        Assert.Contains("预留线", rows[0].Problem);

        // ⚠️ 读不到就是读不到，不许折成 0 或者一个巨大的数。
        Assert.Null(rows[1].Space);
        Assert.Equal(30, rows[1].ReservedGb);          // 用户填过，所以还报得出
        Assert.Contains("读不到", rows[1].Problem);
    }

    private sealed class FakeProbe : Dictionary<string, VolumeSpace?>, IVolumeSpaceProbe
    {
        public VolumeSpace? Measure(string path) =>
            TryGetValue(path, out var space) ? space : null;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-storage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（本仓实测过）。
            // 清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// 磁盘表这一层配置校验（<see cref="AppSettings.IsPlausible"/>）。
/// </summary>
/// <remarks>
/// 判据是「这个路径能不能当根用」，**不是「这个目录在不在」** ——
/// 目录在不在是 I/O，而配置校验是纯函数（设置页每敲一个字符都会问一次），
/// 而且盘没插、NAS 没开都是正常状态。
/// </remarks>
public class DiskSlotConfigTests
{
    [Fact]
    public void 完整路径才收_相对路径与空白都不收()
    {
        Assert.True(AppSettings.IsPlausible(AppSettings.Default with
        {
            SaveDisks = [new DiskSlot(@"D:\快递打包视频", 28)],
            BackupDisks = [new DiskSlot(@"\\nas\vidlog"), new DiskSlot(@"Z:\vidlog", 20)],
        }));

        // 相对路径在这儿没有意义：工作目录是会变的，而录像必须落在
        // 用户指定的那块盘上。
        Assert.False(Plausible(SaveDisks: [new DiskSlot(@"录像")]));
        Assert.False(Plausible(SaveDisks: [new DiskSlot("   ")]));

        // 0 是允许的（「这块盘不预留」是用户的正当选择），负数不是 ——
        // 负的预留等于「允许写到负空间」，换算出来是个荒唐的阈值。
        Assert.True(Plausible(SaveDisks: [new DiskSlot(@"D:\v", 0)]));
        Assert.False(Plausible(SaveDisks: [new DiskSlot(@"D:\v", -1)]));

        Assert.False(Plausible(BackupDisks: [new DiskSlot(@"Z:\v", 1_000_001)]));
    }

    [Fact]
    public void 表长到离谱时整体判为不可信()
    {
        // 一个无限长的列表不会报错，只会让每次选盘都挨个探一遍 ——
        // 而选盘在录制的热路径上。
        var many = Enumerable.Range(1, 33).Select(i => new DiskSlot($@"D:\v{i}")).ToList();

        Assert.False(Plausible(SaveDisks: many));
        Assert.True(Plausible(SaveDisks: [.. many.Take(32)]));
    }

    [Fact]
    public void 老的单个归档目录仍然算数_新的备份表优先()
    {
        // ⚠️ 这条是**升级路径**：老设置文件里只有 `ArchiveDirectory`，
        // 那时「备份位置」这张表的全部内容就是它一个 —— 用户不该因为升级
        // 而看到自己的 NAS 凭空消失（那会让他重新配一遍）。
        var legacy = AppSettings.Default with
        {
            ArchiveBackend = ArchiveBackendKind.Nas,
            ArchiveDirectory = @"\\nas\vidlog",
        };

        Assert.Equal<string>([@"\\nas\vidlog"], legacy.ArchiveDirectories);
        Assert.Equal(@"\\nas\vidlog", legacy.Archive.DirectoryPath);
        Assert.Null(legacy.Archive.ConfigurationProblem);

        // 一旦这张表里有东西，它**优先**，老字段不再参与 ——
        // 否则「把最后一行删掉」会从老字段里复活出一条已经删掉的路径。
        var both = legacy with
        {
            ArchiveDirectory = @"\\nas\老路径",
            BackupDisks = [new DiskSlot(@"Z:\vidlog")],
        };

        Assert.Equal<string>([@"Z:\vidlog"], both.ArchiveDirectories);
        Assert.Equal(@"Z:\vidlog", both.Archive.DirectoryPath);
    }

    [Fact]
    public void 一张表两个字段都不落盘_只有源字段落盘()
    {
        // ⚠️ 合成的属性写进文件就会出现**两份真相**：改了源字段而合成的那份
        // 没跟着变，或者反过来。这条绊线在 `Archive` 上已经有一条，
        // 这里把两张表也钉住。
        var settings = AppSettings.Default with { SaveDisks = [new DiskSlot(@"D:\v", 28)] };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);

        Assert.Contains("\"SaveDisks\":", json, StringComparison.Ordinal);
        Assert.Contains("\"BackupDisks\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ArchiveDirectories\":", json, StringComparison.Ordinal);
    }

    private static bool Plausible(
        IReadOnlyList<DiskSlot>? SaveDisks = null,
        IReadOnlyList<DiskSlot>? BackupDisks = null) =>
        AppSettings.IsPlausible(
            AppSettings.Default with { SaveDisks = SaveDisks ?? [], BackupDisks = BackupDisks ?? [] });
}
