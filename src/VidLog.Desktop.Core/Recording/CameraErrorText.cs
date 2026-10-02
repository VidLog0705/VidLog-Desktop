namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 一次「打不开画面」究竟卡在哪一段 —— 它决定**还要不要再试别的档**。
/// </summary>
/// <remarks>
/// ⚠️ <b>认不出时落到 <see cref="Unknown"/>，也就是「继续试」</b>：
/// 两者代价不对称 —— 多试一档只是慢一点，而把「其实与规格有关」误判成
/// 「与规格无关」会把**本来能用的组合判死**（那正是 §3.1.7 这个探测要防的事）。
/// </remarks>
public enum CameraErrorKind
{
    /// <summary>认不出来。保守当成「与规格有关」。</summary>
    Unknown,

    /// <summary>
    /// 画面源本身打不开（连不上 / 认证不过 / 设备不在 / 被占用）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这一档换任何录制规格都一样</b>：输入侧压根没打开，
    /// 分辨率与编码器都还没轮到上场。所以它一出现就该**收工**，
    /// 而不是把每一档再试一遍 —— 2026-10-02 实测：一路连不上的网络地址
    /// 让冷启动在**显示窗口之前**耗掉 134 秒（每一档都要等满 10 秒的
    /// <see cref="Media.FfmpegSpecProbe.DefaultNetworkTimeout"/>）。
    /// </remarks>
    SourceUnavailable,

    /// <summary>设备不支持这一档参数（分辨率或帧率）。换一档**可能**就跑得通。</summary>
    UnsupportedParameters,
}

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
    public static string Describe(string stderr) => Explain(stderr).Text;

    /// <summary>
    /// 认出来的一句中文，**连同**它卡在哪一段。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这两件事认的是同一遍关键词</b> —— <see cref="Describe"/> 只取那句话，
    /// 「还要不要再试别的档」取 <see cref="CameraErrorKind"/>。分成两份关键词表的话，
    /// 改一处忘一处就会**静默**走岔（那时的话还是对的，只有「要不要继续」错），
    /// 与 <c>FfmpegNetworkCameraProbe.ParseStreams</c> 那条「全仓唯一一处解析」同一条理由。
    /// </remarks>
    public static (CameraErrorKind Kind, string Text) Explain(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            // ⚠️ **空 stderr 不算「源打不开」**，尽管它多半就是。
            // 从「ffmpeg 一个字都没说」推不出「对端没有回应」：进程被掐掉、
            // 起不来、或我们根本没读到管道，都是同一个样子 —— 而这句中文
            // 会**直接贴到界面上**（`NetworkCameraProbe` 也走这个方法）。
            // 说一个没验过的原因，比说「原因见日志」坏得多。
            //
            // ⚠️ 超时那一路**不靠这里**判定：`FfmpegSpecProbe.ProbeOneAsync`
            // 自己把 `OperationCanceledException` 认成 SourceUnavailable
            //（它是我们亲手掐的，那一下是确定的），所以放走这一条不会把 134 秒放回来。
            return (CameraErrorKind.Unknown, "没能打开这一路画面（原因见日志）。");
        }

        // ── 网络摄像头（RTSP / HTTP）──────────────────────────────
        //
        // ⚠️ 顺序「先具体后笼统」：下面几条都可能与别的字样同时出现，
        // 所以最明确的认证与路径排在最前。
        //
        // ⚠️ 网络这一档的失败**一律是「源打不开」**：我们是拿 `-i` 直接读对端，
        // 分辨率与帧率都还没轮到上场，所以换哪一档录制规格都是同一句话。

        if (Has(stderr, "401") || Has(stderr, "Unauthorized"))
        {
            return (CameraErrorKind.SourceUnavailable, "网络摄像头的用户名或密码不对。");
        }

        if (Has(stderr, "404") || Has(stderr, "Not Found") || Has(stderr, "Stream not found"))
        {
            return (CameraErrorKind.SourceUnavailable, "地址里的路径不对（连上了，但那条流不存在）。");
        }

        if (Has(stderr, "403") || Has(stderr, "Forbidden"))
        {
            return (CameraErrorKind.SourceUnavailable, "对端拒绝了这个访问（账号没有看这一路的权限）。");
        }

        // 「回的不是视频流」单独一条：2026-09-29 实测过 —— 把一个 **HTTP** 端口
        // 当 RTSP 用时报的就是这句，而它与「连不上」是两回事
        // （端口是通的、服务也在，只是说不到一块去）。
        if (Has(stderr, "Invalid data found"))
        {
            return (CameraErrorKind.SourceUnavailable, "这个端口回的不是视频流（端口通，但对面不是 RTSP 服务）。");
        }

        if (Has(stderr, "Connection refused") || Has(stderr, "Connection timed out")
            || Has(stderr, "Network is unreachable") || Has(stderr, "No route to host")
            || Has(stderr, "Operation timed out"))
        {
            return (CameraErrorKind.SourceUnavailable, "没能连上（地址或端口不对，或者对端不通）。");
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
            return (CameraErrorKind.SourceUnavailable, "没有找到这个设备（可能被拔掉了，或者名字变了）。");
        }

        if (Has(stderr, "already in use") || Has(stderr, "Device in use"))
        {
            return (CameraErrorKind.SourceUnavailable, "这个摄像头正被别的程序占用。");
        }

        // ★ **只有这一条是「换一档可能就好」**：dshow 的 `-video_size` 是**输入侧**选项，
        // 设备不支持这一档时换一档就真能跑通（2026-09-30 真机实测：一台只认
        // 640×480 / 320×240 / 160×120 的相机，三档全都报这一句，而它换档之后录得出来）。
        // 所以它**不能**被当成「源打不开」收工 —— 那会把本该回落的那一档判死。
        if (Has(stderr, "Could not set video options") || Has(stderr, "Could not set audio options"))
        {
            return (CameraErrorKind.UnsupportedParameters, "这个设备不支持这一档参数（分辨率或帧率）。");
        }

        // 认不出来：给通用句，**不把英文塞回去**。
        return (CameraErrorKind.Unknown, "没能打开这一路画面（原因见日志）。");
    }

    private static bool Has(string text, string needle) =>
        text.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
