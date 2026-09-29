namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 把 ffmpeg **关于画面源**的失败翻成一句中文。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>界面提示必须是中文</b>（需求方 2026-09-29 写死的硬要求）。
/// 而 ffmpeg 报的是英文，还常常是一串对用户毫无指向性的东西 ——
/// 2026-09-29 实测到的原话：
/// <code>
/// rtsp://…: Server returned 401 Unauthorized (authorization failed)
/// </code>
/// 把它原样贴到界面上，用户既看不懂也不知道该改什么。
/// </para>
/// <para>
/// ⚠️ <b>英文原文不丢</b>：它进日志与诊断包（<c>SystemProcessRunner</c> 失败时记 stderr）。
/// I3「不许静默失败」靠的是**说清了原因**，不是把英文贴到界面上 ——
/// 界面上那句中文（「用户名或密码不对」）已经把事情说清楚了，
/// 而追查细节的人去日志里能看到 ffmpeg 的原话。
/// </para>
/// <para>
/// ⚠️ <b>天花板（诚实说清）</b>：这里是**按关键词认**，认不出就给一句通用的。
/// 认不出的那类**必须**落到通用句上，**绝不能把英文塞回去** ——
/// 那等于这条规则没生效。所以规则只有「加一条认得的关键词」这一种扩展方式。
/// </para>
/// <para>
/// ⚠️ 两套措辞（网络 / 本机 DirectShow）**跑同一遍**，不区分来源：
/// 它们的关键词不重叠，而分来源反而要求调用方在还没判断出原因之前就先知道原因。
/// </para>
/// </remarks>
public static class CameraErrorText
{
    /// <summary>认出来的一句中文；认不出给一句通用的。</summary>
    public static string Describe(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "没能打开这一路画面（对端没有回应）。";
        }

        // ── 网络摄像头（RTSP / HTTP）──────────────────────────────
        //
        // ⚠️ 顺序「先具体后笼统」：下面几条都可能与别的字样同时出现，
        // 所以最明确的认证与路径排在最前。

        if (Has(stderr, "401") || Has(stderr, "Unauthorized"))
        {
            return "网络摄像头的用户名或密码不对。";
        }

        if (Has(stderr, "404") || Has(stderr, "Not Found") || Has(stderr, "Stream not found"))
        {
            return "地址里的路径不对（连上了，但那条流不存在）。";
        }

        if (Has(stderr, "403") || Has(stderr, "Forbidden"))
        {
            return "对端拒绝了这个访问（账号没有看这一路的权限）。";
        }

        // 「回的不是视频流」单独一条：2026-09-29 实测过 —— 把一个 **HTTP** 端口
        // 当 RTSP 用时报的就是这句，而它与「连不上」是两回事
        // （端口是通的、服务也在，只是说不到一块去）。
        if (Has(stderr, "Invalid data found"))
        {
            return "这个端口回的不是视频流（端口通，但对面不是 RTSP 服务）。";
        }

        if (Has(stderr, "Connection refused") || Has(stderr, "Connection timed out")
            || Has(stderr, "Network is unreachable") || Has(stderr, "No route to host")
            || Has(stderr, "Operation timed out"))
        {
            return "没能连上（地址或端口不对，或者对端不通）。";
        }

        // ── 本机摄像头（DirectShow）──────────────────────────────
        //
        // ⚠️ 这几条措辞取自 `FfmpegCameraCapture` / `ScannerProcess` 的注释里
        // 记下来的真实报错（那两处的注释就是为了「把 ffmpeg 真正说的话报出来」）。
        // 本机**没有 dshow 设备**（实测 2026-09-29），所以这几条**没有真机验过** ——
        // 认不出最多落到通用句，不会给出一个**错的**原因，这是刻意的保守。

        if (Has(stderr, "Could not find video device")
            || Has(stderr, "Could not find audio only device")
            || Has(stderr, "Could not enumerate video devices")
            || Has(stderr, "Could not enumerate audio only devices"))
        {
            return "没有找到这个设备（可能被拔掉了，或者名字变了）。";
        }

        if (Has(stderr, "already in use") || Has(stderr, "Device in use"))
        {
            return "这个摄像头正被别的程序占用。";
        }

        if (Has(stderr, "Could not set video options") || Has(stderr, "Could not set audio options"))
        {
            return "这个设备不支持这一档参数（分辨率或帧率）。";
        }

        // 认不出来：给通用句，**不把英文塞回去**。
        return "没能打开这一路画面（原因见日志）。";
    }

    private static bool Has(string text, string needle) =>
        text.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
