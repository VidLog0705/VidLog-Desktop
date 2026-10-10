namespace VidLog.Desktop.Core.Scanning;

/// <summary>
/// 单号**形状核查** —— 挡住「假码」（面单上不是单号的那些条码）。
/// </summary>
/// <remarks>
/// <para>
/// 需求方 2026-10-10：「扫码时要注意核查快递面单单号**位数**，防止扫到假码」。
/// 快递面单上除了单号条码，还有**分拣码 / 三段码 / 目的地码**那一类 ——
/// 摄像头对着面单时可能认到它们。它们的共同点是**数字很少、字母和分隔符很多**
/// （例如 <c>320D-D140BBB</c>），而真单号（顺丰 / 京东 / 四通一达 / EMS）基本都是
/// **10~15 个数字**。
/// </para>
/// <para>
/// ⚠️ <b>规则是保守的：只杀「明显不像单号」的，不追求卡死每一种真单号的精确位数。</b>
/// 真单号各家的位数不一样，把范围收窄到「只认某几家」会让别的家整个扫不进来 ——
/// 那比漏掉几个假码严重得多。<b>阈值见下面的常量，2026-10-10 由实现方取的默认值，
/// 等需求方按真面单核一遍。</b>
/// </para>
/// <para>
/// ⚠️ 它**只用在识别那两条路上**（扫码枪 / 摄像头），**不管手动输入** ——
/// 手打是用户明确要做的事，用户打的短码不该被程序拦下（规格 §3.2.2 的手动兜底）。
/// </para>
/// </remarks>
public static class WaybillPlausibility
{
    /// <summary>至少要含几个数字。分拣码那类数字很少，真单号至少十个上下。</summary>
    public const int MinDigits = 8;

    /// <summary>归一化后最长多少字符（真单号没有这么长的，超了就是读串了）。</summary>
    public const int MaxLength = 30;

    /// <summary>
    /// 归一化后的串「像不像单号」。
    /// </summary>
    /// <param name="normalized">
    /// <see cref="WaybillNumber.Normalize"/> 之后的串（大写、无空白）。
    /// </param>
    public static bool IsPlausible(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized) || normalized.Length > MaxLength)
        {
            return false;
        }

        var digits = 0;

        foreach (var ch in normalized)
        {
            if (ch is >= '0' and <= '9')
            {
                digits++;
                continue;
            }

            // 字母与分隔符是允许的（单号里可能带承运商前缀与 `-`）——
            // 关键是**别的字符一个都不许有**：`[` `]` `/` 这些正是分拣码的特征。
            if (ch is >= 'A' and <= 'Z' || ch == '-')
            {
                continue;
            }

            return false;
        }

        return digits >= MinDigits;
    }
}
