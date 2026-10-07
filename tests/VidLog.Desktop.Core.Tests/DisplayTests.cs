using VidLog.Desktop.Core;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 把数字写成人看的样子（T27② 第 4 批补上 <see cref="Display.Timer"/>）。
/// </summary>
/// <remarks>
/// ⚠️ 这个类型原先**一条测试都没有**，而它的两个方法全在「给人看的字」这条线上。
/// 补测试的由头是搬 <see cref="Display.Timer"/>：那一段原先在 App 层**五处**
/// 各写一份（其中两处一模一样）。
/// </remarks>
public class DisplayTests
{
    [Theory]
    [InlineData(0, 0, 0, "00:00:00")]
    [InlineData(0, 0, 9, "00:00:09")]
    [InlineData(0, 1, 1, "00:01:01")]
    [InlineData(1, 2, 3, "01:02:03")]
    [InlineData(9, 59, 59, "09:59:59")]
    public void 计时写成一格不差的时分秒(int hours, int minutes, int seconds, string expected)
    {
        Assert.Equal(expected, Display.Timer(new TimeSpan(hours, minutes, seconds)));
    }

    [Fact]
    public void 超过一天不回绕()
    {
        // ⚠️ 一整天没停过的工位是真实存在的。回绕成 01:00:00 的话，
        // 「录了多久」会比实际少一整天 —— 而那正是这个数字唯一的用途。
        Assert.Equal("25:00:00", Display.Timer(TimeSpan.FromHours(25)));
        Assert.Equal("100:30:15", Display.Timer(new TimeSpan(100, 30, 15)));
    }

    [Fact]
    public void 不够一秒的零头直接抹掉()
    {
        // 它每一秒重画一次，位数必须不变 —— 有零头的话数字会左右跳。
        Assert.Equal("00:00:00", Display.Timer(TimeSpan.FromMilliseconds(999)));
        Assert.Equal("00:00:01", Display.Timer(TimeSpan.FromMilliseconds(1999)));
    }
}
