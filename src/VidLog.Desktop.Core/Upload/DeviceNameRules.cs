using System.Text;

namespace VidLog.Desktop.Core.Upload;

/// <summary>
/// 机位名的宽度规矩（规格 §3.4.3 ② / §3.4.5 ③）。
/// </summary>
/// <remarks>
/// <para>
/// 需求方 2026-09-23 原话：「机位名只允许 12 个字符内，一个汉字两个字符，
/// 一个字母 1 个字符，可以 12 个字母或者 6 个汉字」。
/// 所以上限是**显示宽度**，不是 <see cref="string.Length"/>。
/// </para>
/// <para>
/// ⚠️ <b>与手机端 <c>device_identity.dart</c> 逐字同构</b>（常量、宽度函数、
/// 截断函数三样）。规格 §3.4.3 ② 原话：「上限属于『机位名』<b>这个字段</b>……
/// 从<b>任何路径</b>写进来的名字都得是<b>同一把尺子</b>」——
/// 手改过的配置文件、旧版本存下的长名字也一样。
/// 另一端放行、这一端显示不全，就是「同一把尺子」不成立。
/// </para>
/// </remarks>
public static class DeviceNameRules
{
    /// <summary>宽度上限：**12 格，一个汉字算 2 格**。</summary>
    public const int MaxWidth = 12;

    /// <summary>
    /// 这段文本占几格：汉字（含全角标点、假名、韩文、emoji）算 2，其余算 1。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>不能直接用 <see cref="string.Length"/></b>：C# 的 <c>Length</c> 数的也是
    /// UTF-16 code unit —— 「未命名机位」是 <b>5</b> 不是 10（于是 6 个汉字的名字
    /// 会被判成 6、全部放行，正好放宽一倍）；emoji 反过来算 <b>2</b>（代理对），
    /// 一个 emoji 会吃掉两个字母的额度。哪个方向和需求方的意思都不一致。
    /// </remarks>
    public static int Width(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            width += IsWideRune(rune.Value) ? 2 : 1;
        }

        return width;
    }

    /// <summary>
    /// 把名字截到 <see cref="MaxWidth"/> 格以内。
    /// </summary>
    /// <remarks>
    /// ⚠️ 按 <b>rune</b>（Unicode 标量）走，所以**不会把字劈成半个** ——
    /// 按 <c>char</c> 砍在代理对中间会留下一个孤零零的半字符。
    /// <para>
    /// 超出部分直接丢掉。手机端那边打字时就被拦住了，这里兜的是
    /// <b>「从任何路径写进来的」那些</b>：手改过的配置文件、旧版本存下的长名字。
    /// </para>
    /// </remarks>
    public static string Clamp(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var width = 0;
        var builder = new StringBuilder(name.Length);

        foreach (var rune in name.EnumerateRunes())
        {
            var next = width + (IsWideRune(rune.Value) ? 2 : 1);

            if (next > MaxWidth)
            {
                break;
            }

            width = next;
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// East Asian Width 的 W/F 类（按常用 <c>wcwidth</c> 的区间近似，够用即可）。
    /// </summary>
    /// <remarks>
    /// 不查 Unicode 数据表是有意的：这里只关心「看着占两格」，
    /// 而真正会出现的就那几类。⚠️ 这张表**必须与手机端逐字一致** ——
    /// 差一个区间，两端对同一个名字的格子数就不同。
    /// </remarks>
    private static bool IsWideRune(int rune) =>
        (rune >= 0x1100 && rune <= 0x115F) ||
        (rune >= 0x2E80 && rune <= 0xA4CF && rune != 0x303F) ||
        (rune >= 0xAC00 && rune <= 0xD7A3) ||
        (rune >= 0xF900 && rune <= 0xFAFF) ||
        (rune >= 0xFE30 && rune <= 0xFE6F) ||
        (rune >= 0xFF00 && rune <= 0xFF60) ||
        (rune >= 0xFFE0 && rune <= 0xFFE6) ||
        (rune >= 0x1F300 && rune <= 0x1FAFF) ||
        (rune >= 0x20000 && rune <= 0x3FFFD);
}
