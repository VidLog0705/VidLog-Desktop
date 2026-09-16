using VidLog.Desktop.Core.Scanning;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 规格 §3.2.1：扫码枪判定 = 短时间内的连续按键 + 结束符，且不能与用户正常打字冲突。
/// </summary>
public class ScannerKeystrokeDetectorTests
{
    /// <summary>按扫码枪的速度（默认 5 ms/键）喂一整串，最后补结束符。</summary>
    private static ScanOutcome? Scan(
        ScannerKeystrokeDetector detector,
        string text,
        long intervalMs = 5,
        char terminator = '\r',
        long startMs = 1000)
    {
        var t = startMs;
        ScanOutcome? outcome = null;

        foreach (var ch in text)
        {
            outcome = detector.Accept(new KeyStroke(ch, t)) ?? outcome;
            t += intervalMs;
        }

        return detector.Accept(new KeyStroke(terminator, t)) ?? outcome;
    }

    [Fact]
    public void 识别扫码枪输入()
    {
        var detector = new ScannerKeystrokeDetector();

        var outcome = Scan(detector, "SF1234567890");

        Assert.NotNull(outcome);
        Assert.Equal("SF1234567890", outcome.Waybill.Value);
        Assert.Equal(12, outcome.KeystrokeCount);
    }

    [Theory]
    [InlineData('\r')]
    [InlineData('\n')]
    [InlineData('\t')]
    public void 三种结束符都认(char terminator)
    {
        var detector = new ScannerKeystrokeDetector();

        Assert.NotNull(Scan(detector, "SF1234567890", terminator: terminator));
    }

    [Fact]
    public void 识别结果已归一化()
    {
        var detector = new ScannerKeystrokeDetector();

        // 扫码枪扫出来的可能是小写（条码内容原样）
        var outcome = Scan(detector, "sf1234567890");

        Assert.NotNull(outcome);
        Assert.Equal("SF1234567890", outcome.Waybill.Value);
    }

    [Fact]
    public void 人类打字速度不识别_这是不吞正常打字的核心保证()
    {
        var detector = new ScannerKeystrokeDetector();

        // 150 ms/键 —— 比任何人类打字都慢，但重点是它远超扫码枪的阈值
        var outcome = Scan(detector, "SF1234567890", intervalMs: 150);

        Assert.Null(outcome);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("a/b/c")]
    [InlineData("user@example")]
    [InlineData("价格=100")]
    public void 含单号不可能出现的字符则不识别(string text)
    {
        var detector = new ScannerKeystrokeDetector();

        // 这些串即便以扫码枪速度敲出来也不算扫码 —— 正常打字里的高频字符不能让它们被吞掉
        Assert.Null(Scan(detector, text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("SF12")]
    public void 太短不识别(string text)
    {
        var detector = new ScannerKeystrokeDetector();

        Assert.Null(Scan(detector, text));
    }

    [Fact]
    public void 太长不识别()
    {
        var detector = new ScannerKeystrokeDetector();
        var tooLong = new string('A', 60);

        Assert.Null(Scan(detector, tooLong));
    }

    [Fact]
    public void 没有结束符就不算完成()
    {
        var detector = new ScannerKeystrokeDetector();

        var t = 1000L;
        ScanOutcome? outcome = null;
        foreach (var ch in "SF1234567890")
        {
            outcome = detector.Accept(new KeyStroke(ch, t)) ?? outcome;
            t += 5;
        }

        Assert.Null(outcome);
    }

    [Fact]
    public void 长间隔只作废前半段_后半段仍是完整扫码()
    {
        var detector = new ScannerKeystrokeDetector();

        // 前一段没有结束符就被长间隔截断 —— 不是一次完整扫码，必须放弃。
        // 后一段本身是完整的「快速连续按键 + 结束符」，应当识别。
        // （真实扫码枪必带结束符，所以前一段那种形态只可能来自人手误触。）
        var t = 1000L;
        ScanOutcome? outcome = null;
        foreach (var ch in "SF1234")
        {
            outcome = detector.Accept(new KeyStroke(ch, t)) ?? outcome;
            t += 5;
        }

        t += 500;

        foreach (var ch in "567890")
        {
            outcome = detector.Accept(new KeyStroke(ch, t)) ?? outcome;
            t += 5;
        }

        outcome = detector.Accept(new KeyStroke('\r', t)) ?? outcome;

        Assert.NotNull(outcome);
        Assert.Equal("567890", outcome.Waybill.Value);
    }

    [Fact]
    public void 按住键不放的自动重复不算扫码()
    {
        // 按住 'a' 不放：Windows 自动重复约 30 ms/次，落在 50 ms 阈值**之内**，
        // 若不拦，随后的回车就会伪造出一次扫码。
        var detector = new ScannerKeystrokeDetector();

        var t = 1000L;
        for (var i = 0; i < 12; i++)
        {
            detector.Accept(new KeyStroke('a', t));
            t += 30;
        }

        Assert.Null(detector.Accept(new KeyStroke('\r', t)));
    }

    [Fact]
    public void 自动重复的间隔确实落在扫码枪阈值之内()
    {
        // 给上面那条测试做前提背书：30 ms < 50 ms，所以自动重复在**速度维度**上
        // 与扫码枪无法区分 —— 只能靠「字符全同」这个特征区分。
        Assert.True(30 < new ScannerOptions().MaxInterKeyIntervalMs);
    }

    [Fact]
    public void 连续两次扫码都能识别()
    {
        var detector = new ScannerKeystrokeDetector();

        var first = Scan(detector, "SF1234567890");
        var second = Scan(detector, "YT9876543210", startMs: 60_000);

        Assert.Equal("SF1234567890", first?.Waybill.Value);
        Assert.Equal("YT9876543210", second?.Waybill.Value);
    }

    [Fact]
    public void 判定失败后不残留_下一次扫码仍能识别()
    {
        var detector = new ScannerKeystrokeDetector();

        // 先来一段人类打字（会被放弃）
        Scan(detector, "hello world", intervalMs: 150);

        // 紧接着一次正常扫码 —— 缓冲必须已经干净
        var outcome = Scan(detector, "SF1234567890", startMs: 90_000);

        Assert.Equal("SF1234567890", outcome?.Waybill.Value);
    }

    [Fact]
    public void 阈值可配置()
    {
        // 把阈值放到 200 ms，人类速度的输入也能被判成扫码 —— 证明阈值真的在起作用
        var detector = new ScannerKeystrokeDetector(new ScannerOptions { MaxInterKeyIntervalMs = 200 });

        Assert.NotNull(Scan(detector, "SF1234567890", intervalMs: 150));
    }
}
