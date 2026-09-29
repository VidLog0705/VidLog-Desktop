using VidLog.Desktop.Core.Cleanup;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「预留空间」的默认值（设计图 `_43` 上那句话）。
/// </summary>
/// <remarks>
/// ⚠️ 这一组里有两条是**反推验证**：图上 D 盘写着 `28 GB`、C 盘写着 `30 GB` ——
/// 如果我对那句话的解读是对的，那两个数就该能被算出来。
/// 解读错了的话，这两条会红，而不是等到界面上线才被用户发现。
/// </remarks>
public class ReservedSpaceTests
{
    private static long Gb(double n) => (long)(n * 1024 * 1024 * 1024);

    /// <summary>设计图上 D 盘那一格：一块 560GB 的**非系统盘**。</summary>
    [Fact]
    public void 图中D盘的28GB_正是560GB盘的5个百分点()
    {
        // 图上写的就是 28 GB。而 max(20GB, 560GB×5%) = max(20, 28) = 28 ✓
        Assert.Equal(Gb(28), ReservedSpace.DefaultFor(isSystemDrive: false, totalBytes: Gb(560)));
    }

    /// <summary>设计图上 C 盘那一格：系统盘，图上写 30 GB。</summary>
    [Fact]
    public void 图中C盘的30GB_是系统盘那一档()
    {
        // max(30GB, 10%) —— 一块 200GB 的系统盘，10% = 20GB < 30GB ⇒ 取 30 ✓
        Assert.Equal(Gb(30), ReservedSpace.DefaultFor(isSystemDrive: true, totalBytes: Gb(200)));
    }

    [Fact]
    public void 盘小的时候按固定下限走()
    {
        // 100GB 的非系统盘：5% = 5GB < 20GB ⇒ 取 20GB。
        // 小盘上按比例算会留得太少，而盘满的后果比多留一点严重得多。
        Assert.Equal(Gb(20), ReservedSpace.DefaultFor(isSystemDrive: false, totalBytes: Gb(100)));

        // 100GB 的系统盘：10% = 10GB < 30GB ⇒ 取 30GB。
        Assert.Equal(Gb(30), ReservedSpace.DefaultFor(isSystemDrive: true, totalBytes: Gb(100)));
    }

    [Fact]
    public void 盘大的时候按比例走()
    {
        // 1TB 的非系统盘：5% = 51.2GB > 20GB ⇒ 取比例那一档。
        // ⚠️ 期望值写成算式而不是手算的常量 —— 我第一版手算成 50GB，
        // 而 1024×0.05 其实是 51.2GB。常数写错会红，但那种红是**测试写错**，
        // 与实现无关，白费一轮排查。
        Assert.Equal(
            (long)(Gb(1024) * 0.05),
            ReservedSpace.DefaultFor(isSystemDrive: false, totalBytes: Gb(1024)));

        // 1TB 的系统盘：10% = 102.4GB > 30GB ⇒ 同样取比例那一档。
        Assert.Equal(
            (long)(Gb(1024) * 0.10),
            ReservedSpace.DefaultFor(isSystemDrive: true, totalBytes: Gb(1024)));
    }

    [Fact]
    public void 系统盘永远比同样大的其他盘留得多()
    {
        // 系统盘写满会出各种与本程序无关的怪问题（分页文件、更新、甚至起不来），
        // 而用户不会想到是它造成的 —— 所以两档的固定值与比例都更高。
        foreach (var total in new[] { Gb(64), Gb(256), Gb(512), Gb(2048) })
        {
            Assert.True(
                ReservedSpace.DefaultFor(true, total) > ReservedSpace.DefaultFor(false, total),
                $"{total / 1024 / 1024 / 1024}GB 盘上系统盘那一档没有留得更多");
        }
    }

    [Fact]
    public void 问不出容量时回落到固定下限()
    {
        // ⚠️ 比例算不出来时**宁可多留**：拿不到总容量还按比例算的话，
        // 算出来是 0 —— 而「预留 0」等于允许把盘写满。
        Assert.Equal(Gb(20), ReservedSpace.DefaultFor(isSystemDrive: false, totalBytes: 0));
        Assert.Equal(Gb(30), ReservedSpace.DefaultFor(isSystemDrive: true, totalBytes: 0));

        // 负数（手改坏的值）同样按「拿不到」处理，不许算出负的预留量。
        Assert.Equal(Gb(30), ReservedSpace.DefaultFor(isSystemDrive: true, totalBytes: -1));
    }
}
