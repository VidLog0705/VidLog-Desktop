using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 归档路径的形状（需求方 2026-10-12 换的那一版）。
/// </summary>
/// <remarks>
/// 这个类底下的东西**错了不会报错**：文件照样写下去、索引照样查得到，
/// 只是目录慢慢变得不是需求方要的那套，而按日期清理（§3.5.2）会跟着错。
/// 所以形状、时区、单号里的脏字符三件事都钉在这里。
/// </remarks>
public class ArchiveLayoutTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 10, 12, 14, 5, 0, TimeSpan.FromHours(8));

    [Fact]
    public void 新形状是归档根加日期加分类加单号()
    {
        Assert.Equal(
            "VidLog-Video/2026-10-12/发货/SF1000000001.mp4",
            ArchiveLayout.BuildLocation(
                WaybillNumber.Parse("SF1000000001"), BusinessType.Outbound, Started).Value);

        Assert.Equal(
            "VidLog-Video/2026-10-12/退货/SF1000000001.mp4",
            ArchiveLayout.BuildLocation(
                WaybillNumber.Parse("SF1000000001"), BusinessType.Return, Started).Value);
    }

    [Fact]
    public void 文件名里没有会话号也没有序号()
    {
        // 「一个单号一个文件」是需求方这一版的核心要求：一场录制收尾时已经
        // 合成一个成品，所以文件名**不能**再带 `{会话}_{序号}`。
        var value = ArchiveLayout.BuildLocation(
            WaybillNumber.Parse("SF1000000001"), BusinessType.Outbound, Started).Value;

        Assert.Equal("SF1000000001.mp4", value[(value.LastIndexOf('/') + 1)..]);
    }

    [Fact]
    public void 日期取开始那一刻自带的时区_不换算成本机时区()
    {
        // ⚠️ 本地录制与远端上传给的都是各自带时区的时间戳，两条路径必须落在
        // **同一个日期目录**里（§6.2 那张图的前提）。就地 `ToLocalTime()` 会让
        // 同一个时刻在两条路径下分进两个日子，于是有一条永远落在按日期清理的范围外。
        //
        // 这里刻意挑一个「UTC 日期与它自己那个日期不同」的时刻：
        // 本机（+08:00）跑时 `ToLocalTime()` 会得到 10-01，CI（UTC）跑时也会得到 10-01
        // —— 两个环境都咬得住，不是只在某台机器上才有区分度。
        var eveningInNewYork = new DateTimeOffset(2026, 9, 30, 22, 30, 0, TimeSpan.FromHours(-5));

        Assert.Equal(
            "VidLog-Video/2026-09-30/发货/SF1000000001.mp4",
            ArchiveLayout.BuildLocation(
                WaybillNumber.Parse("SF1000000001"), BusinessType.Outbound, eveningInNewYork).Value);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("SF/1000000001")]
    public void 单号里的分隔符清得掉_跑不出归档目录(string waybill)
    {
        // ⚠️ 单号是**外部输入**（扫码来的），而 `WaybillNumber.Parse` 只管归一化、
        // 不管字符集（它自己的注释里写着「分隔符一律保留原样」）—— 所以下面两种
        // 都能一路走到这里：
        //
        //   「../../etc/passwd」→ 被 `RelativePath.Parse` 拦住（它拒绝 `..` 段），
        //                        但拦住的方式是**抛异常** ⇒ 收尾当场崩；
        //   「SF/1000000001」   → 谁都不拦，安静地多出一层目录，文件落进
        //                        `…/发货/SF/1000000001.mp4`，而回查是按
        //                        「目录里有个叫单号的文件」找的 ⇒ 那条录像搜不到。
        //
        // `Sanitize` 是唯一能把这两种都变成**一个能落盘的名字**的地方。
        var hostile = ArchiveLayout.BuildLocation(
            WaybillNumber.Parse(waybill), BusinessType.Outbound, Started).Value;

        Assert.StartsWith("VidLog-Video/", hostile, StringComparison.Ordinal);
        Assert.DoesNotContain("..", hostile, StringComparison.Ordinal);
        Assert.Equal(4, hostile.Split('/').Length);
        Assert.EndsWith(".mp4", hostile, StringComparison.Ordinal);
    }

    [Fact]
    public void 撞名后缀插在扩展名之前_而且第零份就是原名()
    {
        var first = RelativePath.Parse("VidLog-Video/2026-10-12/发货/SF1000000001.mp4");

        Assert.Equal(first, ArchiveLayout.Deduplicated(first, 0));
        Assert.Equal(
            "VidLog-Video/2026-10-12/发货/SF1000000001_2.mp4",
            ArchiveLayout.Deduplicated(first, 1).Value);
        Assert.Equal(
            "VidLog-Video/2026-10-12/发货/SF1000000001_3.mp4",
            ArchiveLayout.Deduplicated(first, 2).Value);
    }
}
