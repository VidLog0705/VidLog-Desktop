using VidLog.Desktop.Core.Clock;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 校准状态那一句话（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// ⚠️ 这一句在**三处**显示（设置页、左侧栏、概览页），而它原来各自写了一份、
/// 所在的 App 工程又**没有测试工程** —— 于是「侧栏说已校准、设置页说没校准」
/// 那种自相矛盾只能靠人眼盯。搬进 Core 就是为了让下面这几条能挡住它。
/// </remarks>
public class CalibrationTextTests
{
    private static CalibrationState 校准过(CalibrationSource source, DateTimeOffset? at) =>
        new() { Source = source, CalibratedAtUtc = at };

    [Fact]
    public void 没校准必须说清为什么()
    {
        // ⚠️ 笼统一句「未校准」用户不知道下一步：去联网？还是去点重新校准？
        // 这两条路的下一步完全不同。原因由 `TrustedClock` 给（那边才是判断的地方），
        // 这里只保证**它一定出现在那句话里**。
        var text = CalibrationText.Describe(
            isCalibrated: false, "本机时间被改过，请重新校准。", CalibrationState.Empty);

        Assert.Contains("本机时间被改过，请重新校准。", text);
        Assert.DoesNotContain("可以录制", text);
    }

    [Fact]
    public void 校准过要说能录()
    {
        var text = CalibrationText.Describe(
            isCalibrated: true, null, 校准过(CalibrationSource.PublicTime, DateTimeOffset.UtcNow));

        Assert.Contains("可以录制", text);
    }

    [Theory]
    [InlineData(CalibrationSource.PublicTime, "公网时间")]
    [InlineData(CalibrationSource.ArchiveReceipt, "归档回执")]
    public void 来源要写中文而不是枚举名(CalibrationSource source, string expected)
    {
        // 用户看到的是「我这台机器的时间是谁给的」，枚举名对他没有意义。
        var text = CalibrationText.Describe(true, null, 校准过(source, DateTimeOffset.UtcNow));

        Assert.Contains(expected, text);
        Assert.DoesNotContain(source.ToString(), text);
    }

    [Fact]
    public void 没记下校准时刻也要有个占位()
    {
        // ⚠️ 空着会看起来像**界面坏了**；它其实是「校准过、但没记下那一刻」。
        var text = CalibrationText.Describe(true, null, 校准过(CalibrationSource.PublicTime, null));

        Assert.Contains("—", text);
    }
}
