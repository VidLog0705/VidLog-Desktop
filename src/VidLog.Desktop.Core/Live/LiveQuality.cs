namespace VidLog.Desktop.Core.Live;

/// <summary>
/// 实时画面的画质档（规格 §3.8，需求方 2026-10-01 定的三档）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>这一层只有值，没有文案。</b>「好处与坏处」那三句是**选择界面上的字**
/// （用户在多画面窗口全屏时看到的那三行），归 App 层。放在这里的话，
/// 一个领域枚举会背上一段只有界面才用得上的散文。
/// </para>
/// <para>
/// ⚠️ <b>数值必须与手机端 <c>LiveQuality</c> 逐字一致</b>（480 / 720 / 1080）——
/// 那是两端的报文约定，改一边就得改另一边。
/// </para>
/// </remarks>
public enum LiveQuality
{
    /// <summary>480P —— 九宫格里每一格用的那一档（规格写死）。</summary>
    P480 = 480,

    /// <summary>720P —— 全屏的默认档（规格写死）。</summary>
    P720 = 720,

    /// <summary>1080P —— 全屏，用户自己挑。</summary>
    P1080 = 1080,
}

/// <summary>画质档的两端换算。</summary>
public static class LiveQualityExtensions
{
    /// <summary>九宫格格子用的那一档（规格写死 480P）。</summary>
    public const LiveQuality Tile = LiveQuality.P480;

    /// <summary>全屏默认那一档（规格写死 720P）。</summary>
    public const LiveQuality FullscreenDefault = LiveQuality.P720;

    /// <summary>报文里用的那个数。</summary>
    public static int Height(this LiveQuality quality) => (int)quality;

    /// <summary>日志与诊断里用的写法（**不是给用户看的界面文字** —— 那在 App 层）。</summary>
    public static string Label(this LiveQuality quality) => $"{quality.Height()}P";
}
