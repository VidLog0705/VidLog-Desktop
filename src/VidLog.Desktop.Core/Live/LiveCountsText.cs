using System.Globalization;

namespace VidLog.Desktop.Core.Live;

/// <summary>
/// 多画面每一格下面那两行字（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>两行是两件事</b>（T11）：上面那行是「这条管子通不通」（推流健康度），
/// 下面那行 F/T 是「今天扫了多少发货/退货」（**业务计数**，手机报的）。
/// 用户嘴里那句「画面卡」只有看着上面那行才分得出是**手机编不出来**还是
/// **网络不行** —— 而这两件事的处置完全不同。
/// </para>
/// <para>
/// ⚠️ <b>拿不到的数一律写「–」，不许写 0。</b>手机还没报过数时写 0 的话，
/// 那读起来就是「这台机位今天一单没做」—— 而用户不会去怀疑那两个数字。
/// </para>
/// </remarks>
public static class LiveCountsText
{
    /// <summary>上面那行的帧率；还没收到帧时是「– fps」。</summary>
    /// <remarks>
    /// ⚠️ 走 <see cref="CultureInfo.InvariantCulture"/>：小数点是**小数点**，
    /// 不跟着系统区域变成逗号（那会和千分位混起来）。
    /// </remarks>
    public static string Fps(double? receiveFps) =>
        receiveFps is null
            ? "– fps"
            : receiveFps.Value.ToString("0.0", CultureInfo.InvariantCulture) + " fps";

    /// <summary>下面那行的发货数（绿的 F）；手机还没报过数时是「F –」。</summary>
    public static string Outbound(LiveCounts? counts) =>
        "F " + (counts is null ? "–" : counts.Outbound.ToString(CultureInfo.InvariantCulture));

    /// <summary>下面那行的退货数（红的 T）；手机还没报过数时是「T –」。</summary>
    public static string Returned(LiveCounts? counts) =>
        "T " + (counts is null ? "–" : counts.Returned.ToString(CultureInfo.InvariantCulture));

    /// <summary>丢帧数那一段；<b>为 0 时返回 <see langword="null"/>（这一段不出现）</b>。</summary>
    /// <param name="label">「这边丢」还是「手机丢」。</param>
    /// <param name="dropped">丢了多少帧。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>0 也要藏掉，不能印出来。</b>健康时这一行只有帧率 ——
    /// 挂一串 0 会把「有东西要看了」这个信号淹掉，而这行存在的全部意义
    /// 就是让人**一眼**看出哪一格不对劲。
    /// </para>
    /// <para>
    /// ⚠️ 两个名字要分得开：「这边丢」是**这台电脑没跟上**（解码/贴图慢了，
    /// 与网线无关），「手机丢」是**手机编码器整段扔掉**（网线再好也救不回来）。
    /// </para>
    /// <para>
    /// ⚠️ 返回的只是那一句话，**颜色留在界面层**（那几个 <c>Run</c> 各有各的色）。
    /// </para>
    /// </remarks>
    public static string? Loss(string label, long dropped) =>
        dropped <= 0 ? null : $"{label} {dropped.ToString(CultureInfo.InvariantCulture)}";
}
