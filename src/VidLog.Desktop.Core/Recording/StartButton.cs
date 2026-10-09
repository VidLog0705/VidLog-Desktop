namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 顶栏那颗按钮此刻是**哪一态** —— 决定了它写什么字、按下去做什么
/// （T27② 第 4 批抽出；B1 于 2026-10-09 加第三态）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 它从前只有两态（【开始录制】/【停止录制】），于是**待扫态是个死角**：
/// 扫一下屏幕上那张 <c>VLREC</c> 码就进了待扫态（<see cref="RecordingCoordinator.StartWork"/>），
/// 而那一刻单号框里是空的 ⇒ 那颗按钮是禁用的 —— **待扫态里没有任何办法结束工作**，
/// 只能先扫一张面单把录制开起来、再点【停止录制】绕一圈。
/// </para>
/// <para>
/// ⚠️ <b>这里只管「哪一态」，不管字与颜色</b>：字是文案（T28 那一档），底色是资源名
/// （<c>SuccessButton</c> / <c>DangerButton</c>），两样都留在外壳
/// （<c>MainWindow.RefreshStartButton</c>）。
/// </para>
/// </remarks>
public enum StartButtonKind
{
    /// <summary>点了就开始（框里有单号就顺带开录）——【开始录制】。</summary>
    Start,

    /// <summary>工作中、待扫、框里没单号 ——【结束工作】。从前没有这一态。</summary>
    EndWork,

    /// <summary>正在录 ——【停止录制】。</summary>
    StopRecording,
}

/// <summary>
/// 顶栏那颗「开始 / 结束」按钮此刻是哪一态、能不能点（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 抽出来只为让这一串判断有个东西钉着（原来写在 <c>MainWindow.RefreshStartButton</c> 里，
/// 而那个工程没有测试工程）。写成 <c>(recording || hasCamera) &amp;&amp; hasWaybill</c>
/// 的话，**录制中要是单号框空了，按钮就点不动了 —— 用户卡在录制里停不下来**。
/// 这不是排版问题，是「只能靠拔电源」那一类。
/// </para>
/// <para>
/// ⚠️ <b>拆窗之后摄像头取的是启动时定下的那一个</b>（主窗没有摄像头下拉了），
/// 所以这里收的是一个**布尔**而不是「哪一路摄像头」—— 判断「有没有」的地方
/// 在外壳（它读得到 <c>AppHost.Camera</c>）。
/// </para>
/// </remarks>
public static class StartButton
{
    /// <summary>此刻是哪一态。</summary>
    /// <param name="recording">这会儿在录没有（有当前单号）。</param>
    /// <param name="working">这会儿在工作没有（<see cref="RecordingCoordinator.IsWorking"/>）。</param>
    /// <param name="hasWaybill">单号框里有一个认得出来的单号没有。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>两条次序都是承重的</b>：
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>recording</c> 排第一：**录制中一律是【停】** —— 框里哪怕被清空、被改成别的单号，
    /// 也不能把这一颗变成别的东西。「停不下来」是这一屏里最坏的一种坏法。
    /// </description></item>
    /// <item><description>
    /// <c>hasWaybill</c> 排在 <c>working</c> 前面：**待扫态里那个框往往还留着上一段的单号**
    /// （全仓没有任何一处会在收段时清它）。那种情况下按下去该是「用这个单号再录一段」
    /// —— 也就是**今天一直在做的事**，不能被第三态吃掉；要【结束工作】得先把框清掉（✕）。
    /// ⚠️ 手动录入那条路因此毫发无伤：手打的单号一进框，这一颗立刻回到【开始录制】。
    /// </description></item>
    /// </list>
    /// </remarks>
    public static StartButtonKind Kind(bool recording, bool working, bool hasWaybill) =>
        recording ? StartButtonKind.StopRecording
        : working && !hasWaybill ? StartButtonKind.EndWork
        : StartButtonKind.Start;

    /// <summary>能不能点。</summary>
    /// <param name="kind">哪一态。</param>
    /// <param name="hasCamera">有摄像头没有。</param>
    /// <remarks>
    /// ⚠️ 只有「开始」那一态看摄像头 —— 没摄像头就录不了。
    /// <b>要停的两种一律能点</b>（与 <see cref="Kind"/> 里 <c>recording</c> 排第一同一条理由）。
    /// <para>
    /// ⚠️ 从前还要求框里已经有单号才能点，那正是 B1 要修的那条：扫码枪能进的门
    /// （扫 <c>VLREC</c> 进待扫态）按钮不让进。现在没单号也能点，点了就**开始工作**。
    /// </para>
    /// </remarks>
    public static bool Enabled(StartButtonKind kind, bool hasCamera) =>
        kind is StartButtonKind.EndWork or StartButtonKind.StopRecording || hasCamera;
}
