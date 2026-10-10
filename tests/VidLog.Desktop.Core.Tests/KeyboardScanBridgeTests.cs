using VidLog.Desktop.Core.Scanning;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 全局钩子到判定器之间的那一层：虚拟键码 → 字符 → 喂给判定。
/// </summary>
/// <remarks>
/// <see cref="ScannerKeystrokeDetector"/> 的判定逻辑一行不改，它自己的测试也不动。
/// 这里只验「投递」这一半 —— 那正是平台层唯一要做的事。
/// </remarks>
public class KeyboardScanBridgeTests
{
    private const int A = 0x41;
    private const int S = 0x53;
    private const int F = 0x46;
    private const int D1 = 0x31;
    private const int D2 = 0x32;
    private const int D6 = 0x36;

    // ─────────────────────────────────────────────
    // 字符换算
    // ─────────────────────────────────────────────

    [Fact]
    public void 字母不带Shift时是小写()
    {
        Assert.Equal('a', KeyboardScanBridge.Translate(A, shift: false));
        Assert.Equal('A', KeyboardScanBridge.Translate(A, shift: true));
    }

    [Fact]
    public void 短横从小键盘与OemMinus两个键来()
    {
        // 单号里只有 `-` 有意义。它的来源是这两个键 ——
        // ⚠️ **不是**主键盘 Shift+6，那是 `^`。写测试时按直觉把两者混过一次。
        Assert.Equal('-', KeyboardScanBridge.Translate(VirtualKeys.OemMinus, shift: false));
        Assert.Equal('-', KeyboardScanBridge.Translate(VirtualKeys.Subtract, shift: false));

        // 不带 Shift 时 `6` 就是数字六。
        Assert.Equal('6', KeyboardScanBridge.Translate(D6, shift: false));
    }

    [Fact]
    public void 数字行的Shift符号按美式布局()
    {
        Assert.Equal('!', KeyboardScanBridge.Translate(D1, shift: true));
        Assert.Equal('@', KeyboardScanBridge.Translate(D2, shift: true));
    }

    [Fact]
    public void 小键盘数字()
    {
        Assert.Equal('7', KeyboardScanBridge.Translate(0x67, shift: false));
    }

    [Fact]
    public void 回车与Tab是结束符()
    {
        Assert.Equal('\r', KeyboardScanBridge.Translate(VirtualKeys.Return, shift: false));
        Assert.Equal('\t', KeyboardScanBridge.Translate(VirtualKeys.Tab, shift: false));
    }

    [Theory]
    [InlineData(0x70)]  // F1
    [InlineData(0x25)]  // 左箭头
    [InlineData(0x11)]  // Ctrl
    public void 认不出的键返回空(int vk)
    {
        Assert.Null(KeyboardScanBridge.Translate(vk, shift: false));
    }

    // ─────────────────────────────────────────────
    // 投递
    // ─────────────────────────────────────────────

    [Fact]
    public void 一串快按键加回车识别为扫码()
    {
        var bridge = new KeyboardScanBridge();
        ScanOutcome? got = null;
        bridge.Scanned += o => got = o;

        var t = 0L;
        foreach (var ch in "SF1234567890")
        {
            bridge.Accept(new RawKeyEvent(Vk(ch), IsKeyDown: true), t);
            t += 5;   // 扫码枪相邻按键 1~10ms
        }

        bridge.Accept(new RawKeyEvent(VirtualKeys.Return, IsKeyDown: true), t);

        Assert.NotNull(got);
        Assert.Equal("SF1234567890", got!.Waybill.Value);
    }

    [Fact]
    public void 人在打字不会被当成扫码()
    {
        // 人类打字相邻间隔 80ms 以上，判定阈值是 50ms。
        var bridge = new KeyboardScanBridge();
        ScanOutcome? got = null;
        bridge.Scanned += o => got = o;

        var t = 0L;
        foreach (var ch in "SF1234567890")
        {
            bridge.Accept(new RawKeyEvent(Vk(ch), IsKeyDown: true), t);
            t += 120;
        }

        bridge.Accept(new RawKeyEvent(VirtualKeys.Return, IsKeyDown: true), t);

        // 打字的串不该被当成扫码枪（规格 §3.2.1：不能与用户正常打字冲突）。
        Assert.Null(got);
    }

    [Fact]
    public void 只处理按下_抬起事件不会把串翻倍()
    {
        var bridge = new KeyboardScanBridge();
        var count = 0;
        bridge.Scanned += _ => count++;

        var t = 0L;
        foreach (var ch in "SF1234567890")
        {
            bridge.Accept(new RawKeyEvent(Vk(ch), IsKeyDown: true), t);
            bridge.Accept(new RawKeyEvent(Vk(ch), IsKeyDown: false), t + 1);
            t += 5;
        }

        bridge.Accept(new RawKeyEvent(VirtualKeys.Return, IsKeyDown: true), t);

        Assert.Equal(1, count);
    }

    [Fact]
    public void 认不出的键会重置攒到一半的串()
    {
        // 一个 Ctrl 应该打断正在攒的串，而不是被忽略。
        // ⚠️ 后半段的字符要能**自己凑成一个合法单号**，否则这条测试就白测了 ——
        // 第一版用纯数字，数字串本身就是合法单号，于是「重置没生效」也照样过。
        var bridge = new KeyboardScanBridge();
        ScanOutcome? got = null;
        bridge.Scanned += o => got = o;

        bridge.Accept(new RawKeyEvent(Vk('S'), IsKeyDown: true), 0);
        bridge.Accept(new RawKeyEvent(Vk('F'), IsKeyDown: true), 5);
        bridge.Accept(new RawKeyEvent(0x11, IsKeyDown: true), 10);   // Ctrl：打断

        var t = 15L;
        foreach (var ch in "SF1234567890")
        {
            bridge.Accept(new RawKeyEvent(Vk(ch), IsKeyDown: true), t);
            t += 5;
        }

        bridge.Accept(new RawKeyEvent(VirtualKeys.Return, IsKeyDown: true), t);

        // 识别出的必须是 Ctrl **之后**那一串。
        //
        // 比 Waybill（归一化后）而不是 Raw：Raw 是**归一化之前**的原样串，
        // 打出来是小写，拿它比会把「大小写」当成错。长度一致就说明
        // 前面那半截没被带进来 —— 那才是这条测试要验的。
        //
        // 也不比 KeystrokeCount：那是「宿主回收已投递字符」用的计数，
        // 它对不对不影响识别结果，钉死它只会让测试变脆。
        Assert.NotNull(got);
        Assert.Equal("SF1234567890", got!.Waybill.Value);
        Assert.Equal(12, got.Raw.Length);
    }

    // ─────────────────────────────────────────────
    // 吞键（只吞结束符）
    // ─────────────────────────────────────────────

    [Fact]
    public void 扫码枪的结束符要吞掉_前面的字符不吞()
    {
        // 根因：结束符放行的话会落到焦点控件上，实测会「又点一次那颗按钮」。
        var bridge = new KeyboardScanBridge();

        var t = 0L;
        foreach (var ch in "SF1234567890")
        {
            var swallowed = bridge.Accept(new RawKeyEvent(Vk(ch), IsKeyDown: true), t);
            Assert.False(swallowed);   // 字符一律放行
            t += 5;
        }

        Assert.True(bridge.Accept(new RawKeyEvent(VirtualKeys.Return, IsKeyDown: true), t));
    }

    [Fact]
    public void 人类打字的回车不吞()
    {
        // 打字串凑不成一次扫码 ⇒ 那一下回车不该被吞 ——
        // 否则正常按回车（比如点确定）会失效。这是「不吞键」那条保证的落点。
        var bridge = new KeyboardScanBridge();

        var t = 0L;
        foreach (var ch in "SF1234567890")
        {
            bridge.Accept(new RawKeyEvent(Vk(ch), IsKeyDown: true), t);
            t += 120;   // 人类打字间隔
        }

        Assert.False(bridge.Accept(new RawKeyEvent(VirtualKeys.Return, IsKeyDown: true), t));
    }

    [Fact]
    public void 抬起事件与修饰键从不吞()
    {
        var bridge = new KeyboardScanBridge();

        Assert.False(bridge.Accept(new RawKeyEvent(VirtualKeys.Shift, IsKeyDown: true), 0));
        Assert.False(bridge.Accept(new RawKeyEvent(Vk('S'), IsKeyDown: false), 1));
        Assert.False(bridge.Accept(new RawKeyEvent(0x11, IsKeyDown: true), 2));   // Ctrl
    }

    private static int Vk(char c) => char.ToUpperInvariant(c);
}
