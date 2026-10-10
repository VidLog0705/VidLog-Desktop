using VidLog.Desktop.Core.Scanning;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 单号形状核查：挡住面单上**不是单号**的那些条码（分拣码那类）。
/// </summary>
/// <remarks>
/// 需求方 2026-10-10：「核查快递面单单号位数，防止扫到假码」。
/// <para>
/// ⚠️ 这个判据**两头都会错**，所以两个方向都钉住：
/// 放过假码 = 需求没做；把真单号判死 = **包裹直接扫不进来**（比前者严重）。
/// </para>
/// </remarks>
public class WaybillPlausibilityTests
{
    [Theory]
    // 常见的真单号（各家位数不同，都在这里过一遍）
    [InlineData("SF1234567890")]        // 顺丰，带字母前缀
    [InlineData("123456789012")]        // 12 位纯数字
    [InlineData("JD0123456789")]        // 京东
    [InlineData("78123456789")]         // 11 位
    [InlineData("YT1234567890123")]     // 13 位
    [InlineData("12345678901234567890")] // 20 位（够长但没超上限）
    [InlineData("SF-1234567890")]       // 带连字符
    public void 真单号要放过去(string scanned)
    {
        Assert.True(WaybillPlausibility.IsPlausible(scanned));
    }

    [Theory]
    [InlineData("320D-D140BBB")]    // 分拣码：数字只有 6 个
    [InlineData("D14")]             // 三位码
    [InlineData("ABC")]             // 一个数字都没有
    [InlineData("12-345")]          // 数字不够
    [InlineData("")]                // 空
    [InlineData("[1234567890]")]    // 含非法字符
    [InlineData("1234567890123456789012345678901234")] // 34 字符，超上限 = 读串了
    public void 假码要挡住(string scanned)
    {
        Assert.False(WaybillPlausibility.IsPlausible(scanned));
    }

    [Fact]
    public void 空值挡住而不是抛()
    {
        Assert.False(WaybillPlausibility.IsPlausible(null));
    }

    /// <summary>
    /// ⚠️ 这条是**为什么那道闸必须排在命令码检查之后**的绊线。
    /// </summary>
    /// <remarks>
    /// 三条命令码一个数字都没有 —— 形状核查单独看**必然**把它们判成假码。
    /// 要是哪天有人把核查挪到命令码前面，屏幕那两张码就整个失灵，
    /// 而表现是「扫了没反应」，查起来要多久就看运气了。
    /// </remarks>
    [Theory]
    [InlineData("VLOUT")]
    [InlineData("VLRET")]
    [InlineData("VLREC")]
    public void 命令码在形状上就是假码_所以闸必须排在命令码之后(string command)
    {
        Assert.False(WaybillPlausibility.IsPlausible(command));

        // 但命令码**本身**认得出来（`SubmitAsync` 里那道闸排在最前）。
        Assert.NotEqual(ScanCommandKind.None, ScanCommand.KindOf(command));
    }
}
