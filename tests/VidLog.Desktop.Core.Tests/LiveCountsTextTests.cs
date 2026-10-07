using VidLog.Desktop.Core.Live;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 多画面每格下面那两行字（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// ⚠️ 这里最坏的一种错是「**拿不到的数写成了 0**」：那读起来就是
/// 「这台机位今天一单没做」，而用户不会去怀疑那两个数字。原先长在
/// <c>MultiViewWindow</c> 里，那个工程没有测试工程。
/// </remarks>
public class LiveCountsTextTests
{
    [Fact]
    public void 没收到帧时写横杠不是零点零()
    {
        Assert.Equal("– fps", LiveCountsText.Fps(null));
    }

    [Fact]
    public void 帧率真的是零时要照实写零()
    {
        // ⚠️ 这一条与上一条是一对：0.0 与「拿不到」是两回事 ——
        // 写「–」的话，一条**确实卡死了**的管子看起来跟「还没开始」一样。
        Assert.Equal("0.0 fps", LiveCountsText.Fps(0));
    }

    [Fact]
    public void 帧率只留一位小数且小数点是点()
    {
        Assert.Equal("12.3 fps", LiveCountsText.Fps(12.34));
    }

    [Fact]
    public void 手机还没报数时两个计数都写横杠()
    {
        Assert.Equal("F –", LiveCountsText.Outbound(null));
        Assert.Equal("T –", LiveCountsText.Returned(null));
    }

    [Fact]
    public void 手机报过数时写那个数而且零也照写()
    {
        var counts = new LiveCounts(Outbound: 0, Returned: 3, ReportedQuality: null);

        Assert.Equal("F 0", LiveCountsText.Outbound(counts));
        Assert.Equal("T 3", LiveCountsText.Returned(counts));
    }

    [Fact]
    public void 两个计数长得不一样()
    {
        // ⚠️ 长一样的话用户分不出哪个是发货哪个是退货（绿的 F / 红的 T 靠的只是颜色）。
        var counts = new LiveCounts(Outbound: 7, Returned: 7, ReportedQuality: null);

        Assert.NotEqual(LiveCountsText.Outbound(counts), LiveCountsText.Returned(counts));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void 没丢帧时那一段不出现(long dropped)
    {
        // ⚠️ 0 也要藏掉（不是印个 0）：健康时这一行只有帧率，
        // 挂一串 0 会把「有东西要看了」这个信号淹掉。
        Assert.Null(LiveCountsText.Loss("这边丢", dropped));
    }

    [Fact]
    public void 丢了帧时把名字与数字一起写出来()
    {
        Assert.Equal("这边丢 12", LiveCountsText.Loss("这边丢", 12));
        Assert.Equal("手机丢 3", LiveCountsText.Loss("手机丢", 3));
    }

    [Fact]
    public void 两种丢帧要说得开()
    {
        // ⚠️ 「这边丢」是这台电脑没跟上，「手机丢」是手机编码器整段扔掉 ——
        // 处置完全不同，说串了用户会去换网线。
        Assert.NotEqual(LiveCountsText.Loss("这边丢", 5), LiveCountsText.Loss("手机丢", 5));
    }
}
