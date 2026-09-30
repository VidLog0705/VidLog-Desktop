using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Rendering;
using VidLog.Desktop.Core.Scanning;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 屏幕上那两张命令条码的载荷（设计图 `_35`：扫码切换退货 / 扫码开始录像）。
/// </summary>
/// <remarks>
/// ⚠️ <b>这一组里最要紧的是「过得了扫码枪判定」那几条</b>：载荷不是自己造出来就算数，
/// 它得**真的能从扫码枪那一路走到协调器**。三个关卡各有一种静默失效
/// （长度不够 / 字符集不认 / 码太长扫不出来），而三种的表现一模一样：
/// **码画得漂漂亮亮，扫上去什么也不发生** —— 现场没人能看出是哪一关卡住的。
/// </remarks>
public class ScanCommandTests
{
    /// <summary>按扫码枪的速度（默认 5 ms/键）喂一整串，最后补结束符。</summary>
    /// <remarks>
    /// 与 <c>ScannerKeystrokeDetectorTests</c> 同一个手法 —— 这里要验的正是
    /// 「它**过得去**那一套判定」，绕过判定来测等于什么都没测。
    /// </remarks>
    private static ScanOutcome? Scan(string text, long intervalMs = 5, char terminator = '\r')
    {
        var detector = new ScannerKeystrokeDetector();
        var t = 1000L;
        ScanOutcome? outcome = null;

        foreach (var ch in text)
        {
            outcome = detector.Accept(new KeyStroke(ch, t)) ?? outcome;
            t += intervalMs;
        }

        return detector.Accept(new KeyStroke(terminator, t)) ?? outcome;
    }

    // ─────────────────────────────────────────────
    // 关① 关②：长度与字符集（真喂一遍检测器）
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData(ScanCommand.SwitchToOutbound, ScanCommandKind.SwitchToOutbound)]
    [InlineData(ScanCommand.SwitchToReturn, ScanCommandKind.SwitchToReturn)]
    [InlineData(ScanCommand.StartWork, ScanCommandKind.StartWork)]
    public void 三个命令码都过得了扫码枪判定(string payload, ScanCommandKind expected)
    {
        var outcome = Scan(payload);

        Assert.NotNull(outcome);
        Assert.Equal(payload, outcome.Waybill.Value);

        // ⚠️ 这才是界面真正依赖的那一步：扫进来之后认得出是哪条命令。
        Assert.Equal(expected, ScanCommand.KindOf(outcome.Waybill.Value));
    }

    [Theory]
    [InlineData("vlret")]          // 扫码枪不带 Shift 时打出来是小写
    [InlineData("vLoUt")]
    public void 扫码枪打出来是小写也认(string typed)
    {
        // ⚠️ 归一化发生在这之后，所以**判定本身**就不能区分大小写 ——
        // 否则某把枪的默认配置一变，这张码就悄悄失灵了。
        var outcome = Scan(typed);

        Assert.NotNull(outcome);
        Assert.NotEqual(ScanCommandKind.None, ScanCommand.KindOf(outcome.Waybill.Value));
    }

    [Fact]
    public void 三种结束符都认()
    {
        foreach (var terminator in "\r\n\t")
        {
            var outcome = Scan(ScanCommand.SwitchToReturn, terminator: terminator);

            Assert.NotNull(outcome);
            Assert.Equal(ScanCommandKind.SwitchToReturn, ScanCommand.KindOf(outcome.Waybill.Value));
        }
    }

    [Fact]
    public void 人类打字速度打出来的不算命令()
    {
        // 与单号同一条规矩：慢慢敲出来的一串不是扫码（否则用户在单号框里
        // 敲出五个字母就会莫名其妙切换业务类型）。
        Assert.Null(Scan(ScanCommand.SwitchToReturn, intervalMs: 150));
    }

    // ─────────────────────────────────────────────
    // 关③：条宽放得下右边那一栏
    // ─────────────────────────────────────────────

    [Fact]
    public void 每张码都窄到放得进右边那一栏()
    {
        // 一维码的全部信息在横向的条空序列里 ⇒ **印窄了就扫不出来**。
        // 界面按 2 像素/模块画，右边那一栏是 360 宽（去掉 ScrollViewer 的内边距
        // 与卡片的 padding 之后还剩约 288）。
        //
        // ⚠️ 这一条是给「把载荷改长」的人看的：改成 `VLOGOUTBOUND` 这类
        // 十个字符的载荷会到 330 像素，**当场顶出栏外**。
        foreach (var payload in ScanCommand.All)
        {
            var modules = Code128.Modules(payload).GetLength(0);

            Assert.Equal(110, modules);
            Assert.True(modules * 2 <= 288, $"{payload} 画出来 {(modules * 2)} 像素，放不下 288");
        }
    }

    // ─────────────────────────────────────────────
    // 认错了的后果很严重 ⇒ 只认整串相等
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData("VLOUT123")]          // 前缀相同
    [InlineData("VLRET-1")]           // 前缀相同 + 后缀
    [InlineData("XVLOUT")]            // 后缀相同
    [InlineData("VL OUT")]            // 中间一个空格
    [InlineData("VL-RE")]             // 凑长度
    [InlineData("VLOU")]              // 差一个字符
    [InlineData("VLOUTS")]
    [InlineData("OUT")]               // 太短
    public void 常见单号形状不会被误判成命令(string waybill)
    {
        // ⚠️ **认错的后果是不对称的**：把一张真面单当成命令 ⇒ 那件包裹扫了没反应、
        // 货没录上，而操作员只会以为「枪又抽风了」，多半再扫一次、再不行就算了。
        // 所以宁可漏认（那张码扫不出来）也不能错认，比较用的是**整串相等**。
        Assert.Equal(ScanCommandKind.None, ScanCommand.KindOf(waybill));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空的一律不是命令(string? scanned) =>
        Assert.Equal(ScanCommandKind.None, ScanCommand.KindOf(scanned));

    [Fact]
    public void 首尾空白抹掉再比()
    {
        // 手敲进单号框时很容易多带一个空格（`WaybillBox` 收手输）。
        Assert.Equal(ScanCommandKind.SwitchToReturn, ScanCommand.KindOf("  VLRET  "));
    }

    // ─────────────────────────────────────────────
    // 界面上那张码画的是**另一档**
    // ─────────────────────────────────────────────

    [Fact]
    public void 摆出来的永远是切到另一档的那张()
    {
        // 现在录的是发货 ⇒ 屏幕上摆的是「切到退货」那张。图 `_35` 画的正是这一态。
        Assert.Equal(ScanCommand.SwitchToReturn, ScanCommand.For(BusinessType.Outbound));
        Assert.Equal(ScanCommand.SwitchToOutbound, ScanCommand.For(BusinessType.Return));
    }

    [Fact]
    public void 摆出来的那张码认出来的档与标题一致()
    {
        // ⚠️ 界面上的标题是「扫码切换退货」，而码的载荷必须真的切到退货 ——
        // 只改标题不重画码（或者把 `For` 写反）的表现是
        // 「照着标题扫，结果切到了相反的那一档」，而标签就那么错下去了。
        foreach (var current in new[] { BusinessType.Outbound, BusinessType.Return })
        {
            var title = ScanCommand.Describe(
                current == BusinessType.Return ? BusinessType.Outbound : BusinessType.Return);

            var kind = ScanCommand.KindOf(ScanCommand.For(current));

            Assert.Equal(title, ScanCommand.Describe(BusinessTypeOf(kind)));
        }
    }

    [Fact]
    public void 中文名只有发货与退货两种()
    {
        Assert.Equal("发货", ScanCommand.Describe(BusinessType.Outbound));
        Assert.Equal("退货", ScanCommand.Describe(BusinessType.Return));

        // ⚠️ 与标签值是同一个口径（`BusinessTypes`），不是两套说法。
        Assert.Equal(2, Enum.GetValues<BusinessType>().Length);
    }

    private static BusinessType BusinessTypeOf(ScanCommandKind kind) => kind switch
    {
        ScanCommandKind.SwitchToReturn => BusinessType.Return,
        ScanCommandKind.SwitchToOutbound => BusinessType.Outbound,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "不是切换命令"),
    };
}
