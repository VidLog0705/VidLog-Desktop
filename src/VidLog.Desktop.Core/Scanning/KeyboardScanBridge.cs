namespace VidLog.Desktop.Core.Scanning;

/// <summary>
/// 从全局键盘钩子里识别扫码枪输入。
/// </summary>
/// <remarks>
/// <para>
/// 把「平台层投递按键」与「判定是不是扫码枪」这两件事分开：平台层只负责产出
/// 虚拟键码，这个类负责换算成字符、跟踪修饰键、再喂给
/// <see cref="ScannerKeystrokeDetector"/> —— 判定逻辑一行都不用改。
/// </para>
/// <para>
/// <b>不做任何按键抑制</b>。规格 §3.2.1 要求「不能与用户正常打字冲突」，
/// 最可靠的做法是**根本不拦**：钩子照常把键转发给前台窗口，
/// 我们只是旁听。用户打字照旧进他的输入框，扫码枪的串被我们识别出来。
/// </para>
/// </remarks>
public sealed class KeyboardScanBridge
{
    private readonly ScannerKeystrokeDetector _detector;
    private bool _shift;

    public KeyboardScanBridge(ScannerOptions? options = null)
    {
        _detector = new ScannerKeystrokeDetector(options);
    }

    /// <summary>识别到一次完整的扫码枪输入。</summary>
    public event Action<ScanOutcome>? Scanned;

    /// <summary>喂一个按键事件。修饰键只更新状态，不进判定。</summary>
    public void Accept(RawKeyEvent key, long timestampMs)
    {
        if (key.VirtualKey is VirtualKeys.Shift or VirtualKeys.LeftShift or VirtualKeys.RightShift)
        {
            _shift = key.IsKeyDown;
            return;
        }

        if (!key.IsKeyDown)
        {
            // 只处理按下。扫码枪的字符是按下事件，抬起事件重复喂会让判定串翻倍。
            return;
        }

        var character = Translate(key.VirtualKey, _shift);
        if (character is null)
        {
            // 认不出的键（功能键、Ctrl 组合等）当作「不是扫码枪」——
            // 重置而不是忽略，否则一个 Ctrl 打断不了正在攒的串。
            _detector.Reset();
            return;
        }

        var outcome = _detector.Accept(new KeyStroke(character.Value, timestampMs));
        if (outcome is not null)
        {
            Scanned?.Invoke(outcome);
        }
    }

    public void Reset()
    {
        _detector.Reset();
        _shift = false;
    }

    /// <summary>
    /// 虚拟键码 → 字符。
    /// </summary>
    /// <remarks>
    /// 手写映射而不是用 <c>ToUnicodeEx</c>：后者的结果取决于**当前键盘布局与死键状态**，
    /// 而它在低级钩子的回调线程上取到的输入状态并不可靠，还可能污染键盘状态
    /// （顺带把用户的输入弄坏）。单号的字符集是封闭的（字母数字加 <c>-</c> 与结束符），
    /// 手写够用且没有副作用。
    /// </remarks>
    public static char? Translate(int virtualKey, bool shift)
    {
        // 字母：VK 就是 ASCII 大写。
        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            var c = (char)virtualKey;
            return shift ? c : char.ToLowerInvariant(c);
        }

        // 主键盘数字行。Shift 时是符号 —— 单号里只有 `-` 有意义，它在
        // **OemMinus 与 Shift+6 两个位置**上都能打出来，所以两个都要认。
        if (virtualKey is >= 0x30 and <= 0x39)
        {
            var digitIndex = virtualKey - 0x30;
            if (!shift)
            {
                return (char)('0' + digitIndex);
            }

            // 美式布局的 Shift 符号，位置对应 `1 2 3 4 5 6 7 8 9 0`：
            // `! @ # $ % ^ & * ( )`
            //
            // ⚠️ 下标要从 0 起：`1` 是**第一个**字符。原来直接用 `digitIndex`
            // 去查（`1` → 下标 1），整张表错位一格 —— 于是 Shift+6 打出来的
            // 不是 `^` 而是 `&`。这类错位不会报错，只会让字符静默变样。
            var shiftIndex = digitIndex == 0 ? 9 : digitIndex - 1;
            return "!@#$%^&*()"[shiftIndex];
        }

        // 小键盘数字。
        if (virtualKey is >= 0x60 and <= 0x69)
        {
            return (char)('0' + (virtualKey - 0x60));
        }

        return virtualKey switch
        {
            VirtualKeys.OemMinus or VirtualKeys.Subtract => '-',
            // 主键盘回车与小键盘回车在 Windows 上**是同一个 VK**（0x0D），
            // 所以只能是同一个 case。
            VirtualKeys.Return => '\r',
            VirtualKeys.Tab => '\t',
            VirtualKeys.Space => ' ',
            _ => null,
        };
    }
}

/// <summary>一次原始的按键事件（平台层产出）。</summary>
public readonly record struct RawKeyEvent(int VirtualKey, bool IsKeyDown);

/// <summary>用到的虚拟键码。</summary>
/// <remarks>
/// 只列我们认得出的那些。用常量而不是 <c>System.Windows.Forms.Keys</c>：
/// Core 不引用 WinForms（那会把 UI 框架拖进领域层）。
/// </remarks>
public static class VirtualKeys
{
    public const int Shift = 0x10;
    public const int LeftShift = 0xA0;
    public const int RightShift = 0xA1;

    /// <summary>主键盘回车。小键盘回车是**同一个码**，不另立常量。</summary>
    public const int Return = 0x0D;

    public const int Tab = 0x09;
    public const int Space = 0x20;
    public const int OemMinus = 0xBD;
    public const int Subtract = 0x6D;
}
