using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Scanning;

/// <summary>扫到的这一串是一条**命令**，还是真的扫到了一张面单。</summary>
public enum ScanCommandKind
{
    /// <summary>不是命令 —— 按普通单号处理（**绝大多数扫码都是这一种**）。</summary>
    None = 0,

    /// <summary>把当前的业务类型切到发货。</summary>
    SwitchToOutbound = 1,

    /// <summary>把当前的业务类型切到退货。</summary>
    SwitchToReturn = 2,

    /// <summary>开始录像（等于按一下界面上的【开始录制】）。</summary>
    StartWork = 3,
}

/// <summary>
/// 屏幕上那两张条码的载荷（设计图 `_35` 右栏：**扫码切换退货** / **扫码开始录像**）。
/// </summary>
/// <remarks>
/// <para>
/// 用法是：用户在电脑屏幕上显示这张条码，操作员拿**扫码枪扫屏幕**。
/// 之所以要这么做：工位上的手正拿着包裹，鼠标够不着 ——
/// 而扫码枪就在手上。扫描枪是个「读」的设备，所以「用扫码枪按按钮」这个动作
/// 只能做成「屏幕上摆一张码，扫它」。
/// </para>
/// <para>
/// ⚠️ <b>载荷必须过得了 <see cref="ScannerKeystrokeDetector"/> 那三关</b>，
/// 否则表现是「码画得漂漂亮亮、扫上去什么也不发生」：
/// </para>
/// <list type="number">
/// <item><b>长度</b>：<see cref="ScannerOptions.MinLength"/> 是 5、上界 40 ⇒ 取 5 个字符。</item>
/// <item><b>字符集</b>：它的 <c>LooksLikeWaybill</c> 只认 ASCII 字母数字与 <c>-</c>
/// —— 所以**不能用</b> <c>:</c> / <c>_</c> 这类分隔符，用了就是静默失效。</item>
/// <item><b>条宽</b>：一维码的宽度正比于字符数。5 个字符（110 个模块）在
/// 2 像素/模块下是 220 像素，右边那一栏放得下；再长就顶出去或者只能画细，
/// 而那会扫不出来。</item>
/// </list>
/// <para>
/// ⚠️ 「<c>VL</c> + 三个字母」这个形状与真实面单撞车的概率可以忽略，
/// 而**认错了的后果是严重的**（把一张真面单当成命令 → 那件包裹没有录上）。
/// 所以这里的比较是**整串相等**，不是前缀匹配，而且有
/// <c>ScanCommandTests</c> 盯着「常见单号形状不会被误判」。
/// </para>
/// </remarks>
public static class ScanCommand
{
    /// <summary>切到发货（当前是退货时，屏幕上摆的就是这一张）。</summary>
    public const string SwitchToOutbound = "VLOUT";

    /// <summary>切到退货（当前是发货时，屏幕上摆的就是这一张 —— 图 `_35` 那张）。</summary>
    public const string SwitchToReturn = "VLRET";

    /// <summary>开始录像。</summary>
    public const string StartWork = "VLREC";

    /// <summary>全部命令码（画码、写文档、测试都从这一处取）。</summary>
    public static IReadOnlyList<string> All { get; } =
        [SwitchToOutbound, SwitchToReturn, StartWork];

    /// <summary>
    /// 这一串是什么命令；不是命令就是 <see cref="ScanCommandKind.None"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 大小写不敏感：单号在 <c>WaybillNumber.Normalize</c> 里已经被转成大写，
    /// 但本方法不该假设调用方一定先归一化过。
    /// </remarks>
    public static ScanCommandKind KindOf(string? scanned)
    {
        if (string.IsNullOrWhiteSpace(scanned))
        {
            return ScanCommandKind.None;
        }

        var text = scanned.Trim();

        if (text.Equals(SwitchToOutbound, StringComparison.OrdinalIgnoreCase))
        {
            return ScanCommandKind.SwitchToOutbound;
        }

        if (text.Equals(SwitchToReturn, StringComparison.OrdinalIgnoreCase))
        {
            return ScanCommandKind.SwitchToReturn;
        }

        return text.Equals(StartWork, StringComparison.OrdinalIgnoreCase)
            ? ScanCommandKind.StartWork
            : ScanCommandKind.None;
    }

    /// <summary>当前是这一档时，屏幕上该摆哪一张码的载荷（摆的永远是**另一档**）。</summary>
    public static string For(BusinessType type) =>
        type == BusinessType.Return ? SwitchToOutbound : SwitchToReturn;

    /// <summary>这一档的中文名（与检索页、导出单号同一个口径）。</summary>
    /// <remarks>
    /// 转调 <see cref="BusinessTypes.Describe"/> —— **产出只有那一处**。
    /// </remarks>
    public static string Describe(BusinessType type) => BusinessTypes.Describe(type);
}
