using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「这个字段名是不是密钥类」的判定（<c>AGENTS.md</c> §6）。
/// </summary>
/// <remarks>
/// 这条判据有两处消费者：配置变更的差量（<c>AppSettings.DescribeChanges</c>）与
/// <b>日志落盘前的脱敏</b>。两处用同一份判据是刻意的 ——
/// 各写一份的话，走岔的表现是「配置那边挡住的，日志这边漏了出去」。
/// </remarks>
public class SensitiveNameTests
{
    [Theory]
    [InlineData("secret")]
    [InlineData("Token")]
    [InlineData("password")]
    [InlineData("ApiKey")]
    [InlineData("credential")]
    [InlineData("Authorization")]
    public void 英文名命中(string name) => Assert.True(SensitiveName.Is(name), name);

    [Theory]
    [InlineData("凭据")]
    [InlineData("令牌")]
    [InlineData("密码")]
    [InlineData("密钥")]
    [InlineData("授权码")]
    public void 中文键名也命中(string name) =>
        // 这个项目里 data 的键名是中文（["会话"]、["原始"]），设置项的字段名是英文。
        // 只挡英文那一半等于没挡 —— 而入网那一套恰好可以用中文名写出来。
        Assert.True(SensitiveName.Is(name), name);

    [Theory]
    [InlineData("Mode")]
    [InlineData("StaticStop")]
    [InlineData("SegmentMinutes")]
    [InlineData("hostAddress")]
    [InlineData("deviceName")]
    [InlineData("会话")]
    public void 普通名字不命中(string name) => Assert.False(SensitiveName.Is(name), name);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空白名字不命中也不抛(string name) => Assert.False(SensitiveName.Is(name));

    [Fact]
    public void 它是子串匹配_所以会误伤_而这是可接受的()
    {
        // `key` 是子串匹配 ⇒ `monkey` 也会命中。**这是有意接受的**：
        // 命中的代价只是那个值被写成「（已修改）」（少一条诊断信息），
        // 而漏掉的代价是凭据落进诊断包外发。两边不对称，所以宁可误伤。
        Assert.True(SensitiveName.Is("monkey"));
        Assert.True(SensitiveName.Is("hockey"));
    }

    [Fact]
    public void 占位文本就是_AGENTS_里那一句()
    {
        // 措辞是 AGENTS.md §6 的原话，改它等于改约定。
        Assert.Equal("（已修改）", SensitiveName.Redacted);
    }
}
