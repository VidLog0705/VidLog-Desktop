using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 录像在百度网盘上的落点（批次 5，设计图 `_46`）。
/// </summary>
/// <remarks>
/// 这一批里最容易写错、而且**写错了也不报错**的是「发货 / 退货」那一层：
/// 它取自可改的标签。只按当前分类去查云端有没有，用户改过一次分类之后
/// 那条录像在清理的前置闸（I8）眼里就永远是「归档层上没有」——
/// 于是它**永远清不掉**，而没人知道为什么。所以下面把两个目录钉死。
/// </remarks>
public class BaiduPanLayoutTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 9, 30, 14, 5, 0, TimeSpan.FromHours(8));

    private static string Built(string session = "ab12", int sequence = 0) =>
        ArchiveLayout.BuildLocation(WaybillNumber.Parse("SF1000000001"), session, sequence, Started).Value;

    [Fact]
    public void 远端路径照设计图的形状()
    {
        // 图上原话：「录像上传到百度网盘"我的应用数据/[应用名]/(日期)/发货或退货/单号.mp4"」。
        var layout = new BaiduPanLayout("VidLog");
        var location = RelativePath.Parse(Built());

        Assert.Equal(
            "/apps/VidLog/2026/09/30/发货/SF1000000001_ab12_000.mp4",
            layout.RemotePath(location, BusinessType.Outbound));

        Assert.Equal(
            "/apps/VidLog/2026/09/30/退货/SF1000000001_ab12_000.mp4",
            layout.RemotePath(location, BusinessType.Return));
    }

    [Fact]
    public void 换分类只动目录那一段_文件名一个字不变()
    {
        // 回查靠文件名判在不在（列表接口只回名字），所以它**必须**与分类无关 ——
        // 文件名也跟着变的话，改过一次标签之后两个目录都找不到那一份。
        var layout = new BaiduPanLayout("VidLog");
        var location = RelativePath.Parse(Built());

        var asOutbound = layout.RemotePath(location, BusinessType.Outbound);
        var asReturn = layout.RemotePath(location, BusinessType.Return);

        Assert.Equal(BaiduPanLayout.FileNameOf(asOutbound), BaiduPanLayout.FileNameOf(asReturn));
        Assert.Equal(layout.FileNameFor(location), BaiduPanLayout.FileNameOf(asReturn));
    }

    [Fact]
    public void 回查要问两个分类目录()
    {
        var directories = new BaiduPanLayout("VidLog")
            .DirectoriesFor(RelativePath.Parse(Built()));

        Assert.Equal(2, directories.Count);
        Assert.Contains("/apps/VidLog/2026/09/30/发货/", directories);
        Assert.Contains("/apps/VidLog/2026/09/30/退货/", directories);
    }

    [Fact]
    public void 拆不出的相对路径退化成分类目录加原路径_不抛()
    {
        // 形状不是本仓写的那种（从别处导入的、手工摆进去的）时**不能抛**：
        // 为了一条路径不好看而让这条录像永远传不上去，是更坏的交换。
        var layout = new BaiduPanLayout("VidLog");

        Assert.Equal(
            "/apps/VidLog/退货/随手放的一段.mp4",
            layout.RemotePath(RelativePath.Parse("随手放的一段.mp4"), BusinessType.Return));
    }

    [Fact]
    public void 应用名里的斜杠与控制字符会被清掉()
    {
        // 一个 `/` 就能让落点跑到应用目录之外 —— 网盘那边的权限会拒，
        // 但报出来的错是「目录不存在」，查起来完全看不出是自己拼错了。
        var layout = new BaiduPanLayout("  Vi/d\\Log\u0001  ");

        Assert.Equal("VidLog", layout.AppName);
        Assert.Equal("/apps/VidLog/", layout.Root);
    }

    [Fact]
    public void 应用名清完一个字符都不剩时退回一个能用的名字()
    {
        // 抛异常会让设置页在图还没画出来时先崩，而这里是**配置**不是**数据**。
        Assert.Equal("/apps/VidLog/", new BaiduPanLayout("///").Root);
    }
}
