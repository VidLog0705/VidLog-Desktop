using VidLog.Desktop.Core.Cleanup;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 补发间隔的涨落（3.2 掉线熔断）。
/// </summary>
/// <remarks>
/// ⚠️ 这一组**不掐表**。掐表那条路（「跑一秒数它试了几趟」）在忙的 CI 机器上会自己红 ——
/// 而那会让人去改阈值，不是去改逻辑。这里只量**输入与输出**：欠账几条进、间隔多长出。
/// </remarks>
public class ArchiveRetryBackoffTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(30);

    [Fact]
    public void 一条都没划掉_间隔翻倍()
    {
        // 归档层不在 / 写不进去：原样的 3 条还在 ⇒ 下趟隔久一点。
        Assert.Equal(TimeSpan.FromSeconds(120), ArchiveRetryBackoff.Next(Base, 3, 3, Base, Max));
    }

    [Fact]
    public void 划掉了几条_回到起点()
    {
        // 划得动 ⇒ 它回来了，剩下的要**尽快**跟上（不是慢下来）。
        Assert.Equal(Base, ArchiveRetryBackoff.Next(TimeSpan.FromMinutes(10), 5, 2, Base, Max));
    }

    [Fact]
    public void 全划掉了_回到起点()
    {
        Assert.Equal(Base, ArchiveRetryBackoff.Next(TimeSpan.FromMinutes(10), 2, 0, Base, Max));
    }

    [Fact]
    public void 本来就没欠_回到起点_闲着不许把间隔涨上去()
    {
        // ⚠️ 这条是**最容易被写反**的一条。判据若写成「`after >= before` 就翻倍」，
        // 这台什么都没欠的机器每趟都是 0 → 0 ⇒ 间隔一路涨到 30 分钟；
        // 而下一段录像真失败时，那一条欠账要等半小时才有人管 ——
        // 等于拿「不该敲」惩罚了「该敲」。
        Assert.Equal(Base, ArchiveRetryBackoff.Next(TimeSpan.FromMinutes(30), 0, 0, Base, Max));
    }

    [Fact]
    public void 翻到上限就不再涨()
    {
        Assert.Equal(Max, ArchiveRetryBackoff.Next(Max, 1, 1, Base, Max));
    }
}
