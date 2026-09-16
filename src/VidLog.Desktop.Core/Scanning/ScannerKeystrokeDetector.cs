using System.Text;

namespace VidLog.Desktop.Core.Scanning;

/// <summary>
/// 一次按键。时间戳由宿主提供，必须来自**单调时钟**（毫秒）。
/// </summary>
/// <remarks>
/// 用单调时钟而不是墙钟：规格 §3.6.3 要求时长基于单调时钟计算，
/// 而这里判定的是「键与键之间的间隔」—— 用户改系统时间不能影响判定（I11）。
/// </remarks>
public readonly record struct KeyStroke(char Character, long TimestampMs);

/// <summary>
/// 扫码枪判定参数。
/// </summary>
/// <remarks>
/// 规格 §3.2.1 只给了判定规则的**形状**（「短时间内的连续按键 + 结束符」），
/// 没给具体数值 —— 以下默认值是实现方定的，已记入 `docs/02-数据模型.md`。
/// <para>
/// 依据：USB HID 扫码枪相邻按键间隔通常 1~10 ms，偶尔到 30 ms；
/// 人类打字再快也在 80 ms 以上。取 50 ms 落在两者中间，两侧都有余量。
/// </para>
/// </remarks>
public sealed record ScannerOptions
{
    /// <summary>相邻按键的最大间隔。超过即判定为「人在打字」而非扫码枪。</summary>
    public int MaxInterKeyIntervalMs { get; init; } = 50;

    /// <summary>单号最短长度。太短的按键串不当作扫码。</summary>
    public int MinLength { get; init; } = 5;

    /// <summary>单号最长长度。超过即判定为误触。</summary>
    public int MaxLength { get; init; } = 40;

    /// <summary>结束符。扫码枪通常发 Enter（<c>\r</c> 或 <c>\n</c>），少数发 Tab。</summary>
    public string Terminators { get; init; } = "\r\n\t";
}

/// <summary>一次被判定为扫码枪输入的结果。</summary>
/// <param name="Waybill">归一化后的单号。</param>
/// <param name="Raw">归一化之前的原始按键串（诊断用）。</param>
/// <param name="KeystrokeCount">本次消耗的按键数，供宿主回收/抑制已投递的字符。</param>
public sealed record ScanOutcome(WaybillNumber Waybill, string Raw, int KeystrokeCount);

/// <summary>
/// 从按键流里识别「扫码枪输入」。
/// </summary>
/// <remarks>
/// <para>
/// 规格 §3.2.1 的判定规则：**短时间内的连续按键 + 结束符**，且
/// **不能与用户正常打字冲突**。
/// </para>
/// <para>
/// 本类只做判定，**不接触键盘、不吞按键** —— 那是平台层（Windows 全局钩子）的事。
/// 这样切分的理由：判定逻辑是最容易写错的部分（阈值、边界、误判），
/// 把它做成对按键流的纯函数就能完整测到；钩子只是投递。
/// </para>
/// <para>
/// <b>「不能吞掉正常打字」怎么保证</b>：判定是**保守**的 —— 只要有一个键的间隔
/// 超过阈值、或串里有单号不可能出现的字符、或长度越界，就整串放弃。
/// 人类以 50 ms 以内的间隔敲出 5 个以上合法单号字符再按回车，实际上不可能。
/// </para>
/// </remarks>
public sealed class ScannerKeystrokeDetector
{
    private readonly ScannerOptions _options;
    private readonly StringBuilder _buffer = new();

    private long _lastTimestampMs;
    private int _runLength;
    private bool _overflowed;

    public ScannerKeystrokeDetector(ScannerOptions? options = null)
    {
        _options = options ?? new ScannerOptions();
    }

    /// <summary>丢弃当前缓冲，重新开始一轮判定。</summary>
    public void Reset()
    {
        _buffer.Clear();
        _runLength = 0;
        _overflowed = false;
        _lastTimestampMs = 0;
    }

    /// <summary>
    /// 喂入一个按键。识别到一次完整的扫码枪输入时返回结果，否则返回 <see langword="null"/>。
    /// </summary>
    public ScanOutcome? Accept(KeyStroke stroke)
    {
        if (_options.Terminators.IndexOf(stroke.Character) >= 0)
        {
            var outcome = Complete();
            Reset();
            return outcome;
        }

        // 与上一个键间隔过长 → 前面那段是人在打字。断开重开，但**不影响**用户输入。
        if (_runLength > 0 && stroke.TimestampMs - _lastTimestampMs > _options.MaxInterKeyIntervalMs)
        {
            Reset();
        }

        _runLength++;
        if (_buffer.Length < _options.MaxLength)
        {
            _buffer.Append(stroke.Character);
        }
        else
        {
            _overflowed = true;
        }

        _lastTimestampMs = stroke.TimestampMs;
        return null;
    }

    private ScanOutcome? Complete()
    {
        if (_overflowed)
        {
            return null;
        }

        if (_runLength < _options.MinLength || _runLength > _options.MaxLength)
        {
            return null;
        }

        var raw = _buffer.ToString();
        if (!LooksLikeWaybill(raw))
        {
            return null;
        }

        if (AllSameCharacter(raw))
        {
            return null;
        }

        if (!WaybillNumber.TryParse(raw, out var waybill, out _))
        {
            return null;
        }

        return new ScanOutcome(waybill!, raw, _runLength);
    }

    /// <summary>
    /// 整串字符是否完全相同。
    /// </summary>
    /// <remarks>
    /// 防的是**键盘自动重复**：按住某键不放时 Windows 的重复间隔约 30 ms，
    /// 落在扫码枪阈值（<see cref="ScannerOptions.MaxInterKeyIntervalMs"/>）之内，
    /// 于是「按住一个键 + 敲回车」会伪造出一次扫码。
    /// <para>
    /// 自动重复必产生一串相同字符，而真实单号极少全同 —— 用它作区分特征。
    /// 代价是形如 <c>000000000000</c> 的单号会被漏判；这类单号极罕见，
    /// 且漏判只会退化成手动输入，不会误吞用户输入。
    /// </para>
    /// </remarks>
    private static bool AllSameCharacter(string raw)
    {
        for (var i = 1; i < raw.Length; i++)
        {
            if (raw[i] != raw[0])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 按键串是否「长得像单号」。
    /// </summary>
    /// <remarks>
    /// 含空格、斜杠、@ 等字符的串一律判为人类输入 —— 单号不会出现这些字符，
    /// 而它们恰恰是正常打字的高频字符。这条是「不吞正常打字」的主要防线。
    /// 归一化会把大小写与空白处理掉，但那发生在**判定通过之后**，
    /// 不能用来兜底判定本身。
    /// </remarks>
    private static bool LooksLikeWaybill(string raw)
    {
        foreach (var ch in raw)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
