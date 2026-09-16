namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 规格 §6.2 硬约束：路径只存相对路径。
/// </summary>
public class RelativePathTests
{
    [Theory]
    [InlineData(@"C:\recordings\a.mp4")]
    [InlineData("D:/recordings/a.mp4")]
    [InlineData("/var/lib/vidlog/a.mp4")]
    [InlineData(@"\recordings\a.mp4")]
    [InlineData(@"\\nas\share\a.mp4")]
    [InlineData("//nas/share/a.mp4")]
    public void 拒绝绝对路径与UNC路径(string raw)
    {
        Assert.False(RelativePath.TryParse(raw, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("../outside.mp4")]
    [InlineData(@"..\outside.mp4")]
    [InlineData("sessions/../../outside.mp4")]
    [InlineData(@"sessions\..\..\outside.mp4")]
    public void 拒绝向上越级路径(string raw)
    {
        Assert.False(RelativePath.TryParse(raw, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 拒绝空白路径(string? raw)
    {
        Assert.False(RelativePath.TryParse(raw, out _, out _));
    }

    [Theory]
    [InlineData("2026/09/16/SF1234567890.mp4")]
    [InlineData(@"sessions\abc\segment-000.mkv")]
    [InlineData("a.mp4")]
    public void 接受合法的相对路径(string raw)
    {
        var path = RelativePath.Parse(raw);

        Assert.Equal(raw, path.Value);
    }

    [Fact]
    public void 同值相等_可直接用于去重()
    {
        Assert.Equal(RelativePath.Parse("a/b.mp4"), RelativePath.Parse("a/b.mp4"));
        Assert.NotEqual(RelativePath.Parse("a/b.mp4"), RelativePath.Parse("a/c.mp4"));
    }
}

/// <summary>
/// 不变量 I5：单号是唯一事实标识。
/// 规格 §3.2.3：归一化后的结果才是单号，一切关联以此为准。
/// </summary>
public class WaybillNumberTests
{
    [Theory]
    [InlineData("SF 1234567890", "SF1234567890")]
    [InlineData("SF\t1234567890", "SF1234567890")]
    [InlineData("SF1234567890\n", "SF1234567890")]
    [InlineData("  SF1234567890  ", "SF1234567890")]
    public void 归一化去除空白(string raw, string expected)
    {
        Assert.Equal(expected, WaybillNumber.Normalize(raw));
    }

    [Theory]
    [InlineData("sf1234567890", "SF1234567890")]
    [InlineData("Sf1234567890", "SF1234567890")]
    public void 归一化统一为大写(string raw, string expected)
    {
        Assert.Equal(expected, WaybillNumber.Normalize(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void 归一化后为空则视为非法(string? raw)
    {
        Assert.Null(WaybillNumber.Normalize(raw));
        Assert.False(WaybillNumber.TryParse(raw, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Parse_先归一化再构造()
    {
        Assert.Equal("SF1234567890", WaybillNumber.Parse(" sf 1234567890\n").Value);
    }

    [Fact]
    public void 不同写法必须归一到同一单号()
    {
        // 扫码枪带换行、人工输入带空格或小写 —— 归一化后必须是同一个单号，
        // 否则同一件包裹会被记成两条证据（违反 I5）。
        var fromScanner = WaybillNumber.Parse("SF1234567890\r\n");
        var fromTyping = WaybillNumber.Parse(" sf 1234567890 ");

        Assert.Equal(fromScanner, fromTyping);
    }

    [Fact]
    public void 不同单号不相等()
    {
        Assert.NotEqual(WaybillNumber.Parse("SF1234567890"), WaybillNumber.Parse("SF1234567891"));
    }

    [Theory]
    [InlineData("SF-1234567890")]
    [InlineData("SF1234567890-1")]
    public void 校验位与分隔符保留原样_这是刻意的(string raw)
    {
        // §3.2.3 要求「处理校验位」，但规格没给适用算法。
        // 凭空剥离会改变单号的同一性（I5），所以这里断言的是「不动它」。
        // 等拿到具体承运商的校验位规则再改这条测试。
        Assert.Equal(raw, WaybillNumber.Normalize(raw));
    }
}

/// <summary>
/// 规格 §3.6.1：指纹 = 内容哈希 + 单号 + 录制时间。
/// </summary>
public class ContentHashTests
{
    private const string Valid = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void 拒绝长度不对的哈希()
    {
        Assert.False(ContentHash.TryParse(Valid[..63], out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void 拒绝非十六进制字符()
    {
        var raw = "z" + Valid[1..];

        Assert.False(ContentHash.TryParse(raw, out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 拒绝空哈希(string? raw)
    {
        Assert.False(ContentHash.TryParse(raw, out _, out _));
    }

    [Fact]
    public void 统一为小写_免得同一份内容被判成两条证据()
    {
        var upper = ContentHash.Parse(Valid.ToUpperInvariant());
        var lower = ContentHash.Parse(Valid);

        Assert.Equal(lower, upper);
        Assert.Equal(Valid, upper.Value);
    }
}
