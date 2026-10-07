namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 顶栏那颗「开始 / 停止录制」按钮能不能点（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 抽出来只为让 <c>recording || hasCamera &amp;&amp; hasWaybill</c> 这一串的
/// **运算次序**有个东西钉着（原来写在 <c>MainWindow.RefreshStartButton</c> 里，
/// 而那个工程没有测试工程）。写成 <c>(recording || hasCamera) &amp;&amp; hasWaybill</c>
/// 的话，**录制中要是单号框空了，按钮就点不动了 —— 用户卡在录制里停不下来**。
/// 这不是排版问题，是「只能靠拔电源」那一类。
/// </para>
/// <para>
/// ⚠️ <b>拆窗之后摄像头取的是启动时定下的那一个</b>（主窗没有摄像头下拉了），
/// 所以这里收的是一个**布尔**而不是「哪一路摄像头」—— 判断「有没有」的地方
/// 在外壳（它读得到 <c>AppHost.Camera</c>）。
/// </para>
/// <para>
/// ⚠️ 按钮上的字与底色**没有搬**：字是文案（T28 那一档），底色是资源名
/// （`SuccessButton` / `DangerButton`），两样都留在外壳。这里只管能不能点。
/// </para>
/// </remarks>
public static class StartButton
{
    /// <summary>能不能点。</summary>
    /// <param name="recording">这会儿在录没有。</param>
    /// <param name="hasCamera">有摄像头没有。</param>
    /// <param name="hasWaybill">单号框里有一个认得出来的单号没有。</param>
    /// <remarks>
    /// ⚠️ <b>录制中一律能点</b>（第一个条件短路掉后面两个）：那颗按钮在录制中
    /// 是【停止录制】，而「停不下来」是这一整屏里最坏的一种坏法。
    /// </remarks>
    public static bool Enabled(bool recording, bool hasCamera, bool hasWaybill) =>
        recording || hasCamera && hasWaybill;
}
