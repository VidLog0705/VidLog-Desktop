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
/// <para>
/// ⚠️ 2026-10-12 换了归档形状（<c>VidLog-Video/&lt;yyyy-MM-dd&gt;/&lt;发货|退货&gt;/&lt;单号&gt;.mp4</c>），
/// 但**老形状的路径必须继续算得出远端路径** —— 盘上与网盘上都还躺着按老形状传上去的录像。
/// 算不出来的话回查一路说「网盘上没有」，于是那些录像**永远清不掉**。
/// 老形状那几条因此不是怀旧，是闸。
/// </para>
/// </remarks>
public class BaiduPanLayoutTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 9, 30, 14, 5, 0, TimeSpan.FromHours(8));

    /// <summary>新形状（2026-10-12 起）：<c>VidLog-Video/&lt;yyyy-MM-dd&gt;/&lt;发货|退货&gt;/&lt;单号&gt;.mp4</c>。</summary>
    private static RelativePath Built(BusinessType type = BusinessType.Outbound) =>
        ArchiveLayout.BuildLocation(WaybillNumber.Parse("SF1000000001"), type, Started);

    /// <summary>
    /// 老形状（2026-10-12 之前录的）：<c>&lt;年&gt;/&lt;月&gt;/&lt;日&gt;/&lt;单号&gt;/&lt;会话&gt;_&lt;序号&gt;.mp4</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它是**字面量**，不是用 <see cref="ArchiveLayout"/> 拼出来的 ——
    /// 用现在这套 API 拼已经拼不出老形状了（这正是这次改动的意思）。
    /// 这条路径代表的是**盘上已经存在的那些文件**，只能照实写死。
    /// </remarks>
    private static readonly RelativePath Legacy =
        RelativePath.Parse("2026/09/30/SF1000000001/ab12_000.mp4");

    [Fact]
    public void 新形状的远端路径照需求方定的形状()
    {
        // 需求方 2026-10-12 写死：/apps/<应用名>/<年-月-日>/<发货|退货>/<单号>.mp4
        var layout = new BaiduPanLayout("VidLog");

        Assert.Equal(
            "/apps/VidLog/2026-09-30/发货/SF1000000001.mp4",
            layout.RemotePath(Built(BusinessType.Outbound), BusinessType.Outbound));

        Assert.Equal(
            "/apps/VidLog/2026-09-30/退货/SF1000000001.mp4",
            layout.RemotePath(Built(BusinessType.Return), BusinessType.Return));
    }

    [Fact]
    public void 新形状下分类那一层跟着路径走_不跟着标签走()
    {
        // ⚠️ 路径里的分类是**开始工作时选的那一档**（冻结的），标签是可以改的（I5）。
        // 两边拿各自的值去算，用户改一次标签就会换出一个新的远端路径 ——
        // 而 `UploadQueue` 里存的是入队那一刻算出来的那个，于是同一份录像
        // 会被当成「没传过」重传一遍。所以这里钉住：标签说了不算。
        var layout = new BaiduPanLayout("VidLog");
        var location = Built(BusinessType.Outbound);

        Assert.Equal(
            layout.RemotePath(location, BusinessType.Outbound),
            layout.RemotePath(location, BusinessType.Return));
    }

    [Fact]
    public void 老形状的远端路径一个字都不变()
    {
        // ★ 这一条是「老录像永远清不掉」的闸。远端已经按这个形状传上去过了，
        // 这里算出来的值与当年传上去时那个必须逐字相同。
        var layout = new BaiduPanLayout("VidLog");

        Assert.Equal(
            "/apps/VidLog/2026/09/30/发货/SF1000000001_ab12_000.mp4",
            layout.RemotePath(Legacy, BusinessType.Outbound));

        Assert.Equal(
            "/apps/VidLog/2026/09/30/退货/SF1000000001_ab12_000.mp4",
            layout.RemotePath(Legacy, BusinessType.Return));
    }

    [Fact]
    public void 换分类只动目录那一段_文件名一个字不变()
    {
        // 回查靠文件名判在不在（列表接口只回名字），所以它**必须**与分类无关 ——
        // 文件名也跟着变的话，改过一次标签之后两个目录都找不到那一份。
        var layout = new BaiduPanLayout("VidLog");

        foreach (var location in new[] { Built(), Legacy })
        {
            var asOutbound = layout.RemotePath(location, BusinessType.Outbound);
            var asReturn = layout.RemotePath(location, BusinessType.Return);

            Assert.Equal(BaiduPanLayout.FileNameOf(asOutbound), BaiduPanLayout.FileNameOf(asReturn));
            Assert.Equal(layout.FileNameFor(location), BaiduPanLayout.FileNameOf(asReturn));
        }
    }

    [Fact]
    public void 回查要问两个分类目录()
    {
        // 老形状：分类不在路径里，两个目录都可能是它对家 ⇒ 两个都得问。
        var legacy = new BaiduPanLayout("VidLog").DirectoriesFor(Legacy);

        Assert.Equal(2, legacy.Count);
        Assert.Contains("/apps/VidLog/2026/09/30/发货/", legacy);
        Assert.Contains("/apps/VidLog/2026/09/30/退货/", legacy);
    }

    [Fact]
    public void 新形状只问一个目录_不白问一遍网盘()
    {
        // 新形状下分类就在路径里，两个候选算出来是同一个目录。
        // ⚠️ 去重不是洁癖：网盘有限流，问两遍等于把「一次对比同步」的请求数翻倍
        // （`CloudUploadService` 那段「能把账号问进风控」说的就是这件事）。
        var current = new BaiduPanLayout("VidLog").DirectoriesFor(Built(BusinessType.Return));

        Assert.Equal(["/apps/VidLog/2026-09-30/退货/"], current);
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
