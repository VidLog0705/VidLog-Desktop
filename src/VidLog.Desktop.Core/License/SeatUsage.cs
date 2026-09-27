namespace VidLog.Desktop.Core.License;

/// <summary>
/// 机位占用公式（`docs/04-许可设计.md` §5.1）。
/// </summary>
/// <remarks>
/// <para>
/// <b>机位 = 手机台数 + max(0, 摄像头数 − 1)</b>。那个 <c>− 1</c> 不是笔误：
/// <b>第一路摄像头是白送的</b> —— 本产品只有一个摄像头，它同时喂预览、识码与录制，
/// 买机器时本来就带着。第二路起才另算一台。
/// </para>
/// <para>
/// ⚠️ 本产品今天**只有一路**摄像头，所以这个公式**恒等于手机台数**。
/// 那为什么还要把它写成公式而不是直接数手机：将来真接了第二路相机时，
/// 它会**自动占一个机位**，而不是因为「当初写的是数手机」就悄悄白送一台。
/// </para>
/// </remarks>
public static class SeatUsage
{
    /// <summary>本机摄像头路数。**今天恒为 1** —— 那一路同时喂预览、识码与录制。</summary>
    public const int Cameras = 1;

    /// <summary>占了几个机位。</summary>
    /// <param name="phones">已接入的手机端台数。</param>
    /// <param name="cameras">本机摄像头路数。</param>
    public static int For(int phones, int cameras) => phones + Math.Max(0, cameras - 1);
}
