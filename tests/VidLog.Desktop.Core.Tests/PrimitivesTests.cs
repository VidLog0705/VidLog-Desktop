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
/// </summary>
public class WaybillNumberTests
{
    [Theory]
    [InlineData("SF 1234567890")]
    [InlineData("SF\t1234567890")]
    [InlineData("SF1234567890\n")]
    public void 拒绝含空白字符的未归一化单号(string raw)
    {
        // 归一化（§3.2.3）负责去除空白；到达本类型时应当已经去过。
        Assert.False(WaybillNumber.TryParse(raw, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 拒绝空单号(string? raw)
    {
        Assert.False(WaybillNumber.TryParse(raw, out _, out _));
    }

    [Fact]
    public void 接受归一化后的单号()
    {
        var waybill = WaybillNumber.Parse("SF1234567890");

        Assert.Equal("SF1234567890", waybill.Value);
    }

    [Fact]
    public void 同值相等_可作为标识使用()
    {
        Assert.Equal(WaybillNumber.Parse("SF1234567890"), WaybillNumber.Parse("SF1234567890"));
        Assert.NotEqual(WaybillNumber.Parse("SF1234567890"), WaybillNumber.Parse("SF1234567891"));
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
