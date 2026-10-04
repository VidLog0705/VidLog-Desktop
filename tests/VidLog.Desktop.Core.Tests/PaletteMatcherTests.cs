using VidLog.Desktop.Core.Commands;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 命令面板的匹配（T12）。
/// </summary>
/// <remarks>
/// ⚠️ 这一组是 T12 **唯一**的判据。命令面板坏掉的样子是「有时候搜得到、
/// 有时候搜不到」—— 那种缺陷没人会专门报，而它会一直烂在那儿。
/// </remarks>
public class PaletteMatcherTests
{
    private static readonly List<PaletteCandidate> Table =
    [
        new("search", "检索录像", "回放,查找,单号,播放"),
        new("data", "打开数据", "备份,存档,总览,统计"),
        new("settings", "打开设置", "配置,选项,许可,激活"),
        new("wall", "实时多画面", "九格,监控,机位"),
        new("enroll", "连接手机", "二维码,扫码,入网"),
    ];

    [Fact]
    public void 空查询把全部原样返回()
    {
        // 面板刚打开、一个字都还没敲 —— 这时候列不全才是坏的。
        Assert.Equal(Table, PaletteMatcher.Rank(null, Table));
        Assert.Equal(Table, PaletteMatcher.Rank("   ", Table));
    }

    [Fact]
    public void 显示名以它开头的排最前()
    {
        var hits = PaletteMatcher.Rank("打开", Table);

        Assert.Equal(["打开数据", "打开设置"], hits.Select(h => h.Label).ToArray());
    }

    [Fact]
    public void 显示名里包含它的也能命中()
    {
        var hits = PaletteMatcher.Rank("录像", Table);

        Assert.Equal("检索录像", hits[0].Label);
    }

    [Fact]
    public void 靠关键词也命得中()
    {
        // 「回放」在表里根本不是任何一个 Label 的一部分 ——
        // 只按显示名匹配的话，用户按界面上的说法去搜会一条都搜不到。
        var hits = PaletteMatcher.Rank("回放", Table);

        Assert.Equal("检索录像", Assert.Single(hits).Label);
    }

    [Fact]
    public void 少打几个字也找得到_按顺序散落着就行()
    {
        // 「实画」在「实时多画面」里不相邻 —— 子序列那一档兜的就是这个。
        var hits = PaletteMatcher.Rank("实画", Table);

        Assert.Equal("实时多画面", Assert.Single(hits).Label);
    }

    [Fact]
    public void 一个字都对不上就什么都不返回_而不是硬凑一个()
    {
        // ⚠️ 这条是**回车**那条路径的护栏：回车按的是第一条，
        // 硬凑一条出来就等于「搜一个不存在的东西，然后回车打开了一个别的窗口」。
        Assert.Empty(PaletteMatcher.Rank("zzzz", Table));
    }

    [Fact]
    public void 顺序也吃字的先后_反过来拼不算命中()
    {
        // 「面画多时实」把「实时多画面」倒过来了 —— 顺序不对就是没命中。
        Assert.Empty(PaletteMatcher.Rank("面画多", Table));
    }

    [Fact]
    public void 同分时保持表里的顺序()
    {
        // 「打开」在「打开数据」与「打开设置」里都是第 0 位、两个 Label 还一样长，
        // 所以同分。这时按表里的先后（表是手写的，靠前的是更要紧的入口）。
        //
        // ⚠️ 换成按显示名排的话，中文排出来的是**码点顺序**（数 U+6570 会排到
        // 设 U+8BBE 前面）—— 那个顺序对人毫无意义，而且看起来像随机。
        var hits = PaletteMatcher.Rank("打开", Table);

        Assert.Equal(["打开数据", "打开设置"], hits.Select(h => h.Label).ToArray());
    }
}
