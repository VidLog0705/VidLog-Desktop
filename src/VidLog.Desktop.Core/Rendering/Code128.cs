namespace VidLog.Desktop.Core.Rendering;

/// <summary>
/// 一维条码（Code 128，子集 B）。
/// </summary>
/// <remarks>
/// <para>
/// 用途是配置向导第 6 步「扫码枪（可选）」：屏幕上显示一个测试条码，
/// 用户拿扫码枪扫它，看到单号出现在下面的输入框里 —— 就说明枪能用。
/// </para>
/// <para>
/// ⚠️ <b>为什么自己做而不是引库</b>：本仓**已经在用 ZXing.Net**（识码那一档），
/// 而 ZXing 的**写**侧要配图像库绑定（`System.Drawing` / `ImageSharp` …），
/// 那正是 `VidLog.Desktop.Core.csproj` 里刻意避开的东西
/// （Core 是 `net9.0`，喂裸灰度像素就够）。Code 128 的编码本身是**查表 + 校验位**，
/// 几十行，不欠这个依赖。
/// </para>
/// <para>
/// ⚠️ <b>码表是手写的，所以必须有往返测试</b>：编出来的矩阵**用 ZXing 解回去**、
/// 与原文比对（见 <c>Code128Tests</c>）。抄错一格的表现是「图形看着像条码、
/// 但扫出来是别的单号或扫不出来」—— 而那种错**肉眼看不出来**。
/// </para>
/// <para>
/// ⚠️ 只做**子集 B**（ASCII 32–126）：我们的载荷是快递单号（大写字母 + 数字），
/// 用不上 A（控制字符）与 C（数字压缩）。做全三个子集要一套切换状态机，
/// 而它换来的只是「条码短一点」。
/// </para>
/// </remarks>
public static class Code128
{
    /// <summary>
    /// 两侧的静区（留白）宽度，单位是模块。
    /// </summary>
    /// <remarks>
    /// ⚠️ 规格要求 **≥ 10×**，比二维码那 4 个模块宽得多 —— 一维码靠**水平**方向的
    /// 明暗边界定位，两侧没留够的话读码器会把「屏幕边缘」当成一条空。
    /// 自己补，不依赖编码库（与 <see cref="EnrollQr"/> 同一条理由）。
    /// </remarks>
    public const int QuietZoneModules = 10;

    /// <summary>起始符（子集 B）。</summary>
    private const int StartB = 104;

    /// <summary>停止符。</summary>
    private const int Stop = 106;

    /// <summary>校验位的模。</summary>
    private const int CheckModulus = 103;

    /// <summary>
    /// 把内容编成模块矩阵：<c>[x, 0]</c> 为 <see langword="true"/> 表示深色。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>高度固定为 1 个模块</b>，因为条码的全部信息都在**横向**的条空序列里 ——
    /// 高度是「画多高」的渲染细节，由界面那一层拉伸（WPF 用最近邻）。
    /// 返回 <c>bool[,]</c> 而不是 <c>bool[]</c> 是为了与
    /// <see cref="EnrollQr.Modules"/> **同形**，界面那一层就能用同一套渲染代码 ——
    /// 硬塞一个二维形状不算撒谎：它确实是个「宽 × 1」的图形。
    /// </para>
    /// <para>
    /// ⚠️ 载荷超出子集 B 的范围（ASCII 32–126）时**抛**，不静默降级：
    /// 编不出来的字符要是被悄悄丢掉，那条码扫出来会是个**别的单号**，
    /// 而用户拿它当「扫码枪测试通过」的证据 —— 那是最坏的一种假象。
    /// </para>
    /// </remarks>
    public static bool[,] Modules(string payload, int quietZone = QuietZoneModules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentOutOfRangeException.ThrowIfNegative(quietZone);

        var symbols = Symbols(payload);
        var modules = new List<bool>();

        for (var i = 0; i < quietZone; i++)
        {
            modules.Add(false);
        }

        foreach (var symbol in symbols)
        {
            AppendSymbol(modules, symbol);
        }

        for (var i = 0; i < quietZone; i++)
        {
            modules.Add(false);
        }

        var matrix = new bool[modules.Count, 1];
        for (var x = 0; x < modules.Count; x++)
        {
            matrix[x, 0] = modules[x];
        }

        return matrix;
    }

    /// <summary>摊成灰度图（与二维码用同一个约定，见 <see cref="ModuleBitmap"/>）。</summary>
    public static byte[] Pixels(bool[,] modules) => ModuleBitmap.Pixels(modules);

    /// <summary>
    /// 一串码字：起始 + 数据 + **校验位** + 停止。
    /// </summary>
    /// <remarks>
    /// 校验位是 Code 128 的一部分（不是可选的）：少了它，读码器会**直接拒读**
    /// （或者更糟 —— 在某一种相近图形上读出别的码字）。
    /// 算法：<c>(起始值 + Σ(位置 × 数据值)) mod 103</c>，位置从 1 起。
    /// </remarks>
    public static IReadOnlyList<int> Symbols(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var symbols = new List<int> { StartB };
        var checksum = StartB;

        for (var i = 0; i < payload.Length; i++)
        {
            var c = payload[i];

            // 子集 B 对应 ASCII 32–126 ⇒ 值 = 字符 - 32。
            if (c is < ' ' or > '~')
            {
                throw new ArgumentOutOfRangeException(
                    nameof(payload), c,
                    $"Code 128 子集 B 编不了这个字符（U+{(int)c:X4}）：只支持 ASCII 32–126。");
            }

            var value = c - ' ';
            symbols.Add(value);

            // ⚠️ 位置从 **1** 起（起始符不参与加权，但参与初始和）。
            checksum += value * (i + 1);
        }

        symbols.Add(checksum % CheckModulus);
        symbols.Add(Stop);

        return symbols;
    }

    /// <summary>把一个码字追加成条空模块（第一个元素是**条**，之后条空交替）。</summary>
    private static void AppendSymbol(List<bool> modules, int symbol)
    {
        var pattern = Patterns[symbol];
        var dark = true;

        foreach (var c in pattern)
        {
            var width = c - '0';

            for (var i = 0; i < width; i++)
            {
                modules.Add(dark);
            }

            dark = !dark;
        }
    }

    /// <summary>
    /// Code 128 的码表：每个码字 6 个数字，依次是**条、空、条、空、条、空**的宽度
    /// （单位是模块）。停止符是唯一的例外（7 个数字、13 个模块）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这是 ISO/IEC 15417 那张表，**手抄的** ⇒ 必须由往返测试盯着
    /// （<c>Code128Tests</c> 把编出来的图用 ZXing 解回去）。
    /// 索引 103/104/105 分别是起始符 A/B/C，106 是停止符。
    /// </remarks>
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",   // 0–9
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",   // 10–19
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",   // 20–29
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",   // 30–39
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",   // 40–49
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",   // 50–59
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",   // 60–69
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",   // 70–79
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",   // 80–89
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",   // 90–99
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",                                // 100–106
    ];
}
