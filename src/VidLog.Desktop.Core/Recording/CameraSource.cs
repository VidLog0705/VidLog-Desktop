using System.Globalization;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 画面从哪来（规格 §3.1.7 的设备维）。
/// </summary>
/// <remarks>
/// ⚠️ 2026-09-29 起设备维不再只是「一个 dshow 名字」：设计图的配置向导第 2 步里，
/// 摄像头下拉的最后一项是「**网络摄像头（手动地址）**」，选中后出现地址框
/// （占位符 `例如：rtsp://账号:密码@192.168.1.64:554/stream`）与【测试连接】。
/// 所以「设备」变成一个**描述符**：是本机设备，还是一个网络地址。
/// </remarks>
public enum CameraSourceKind
{
    /// <summary>本机 DirectShow 设备（USB / 内置摄像头）。</summary>
    /// <remarks>
    /// <b>显式写 0</b>：老设置文件里没有这个字段，反序列化时取枚举默认值 ——
    /// 而老设置里的摄像头**一定是本机设备**，所以默认值必须是它，
    /// 否则升级之后所有人的摄像头都会变成「网络摄像头、地址为空」。
    /// </remarks>
    Local = 0,

    /// <summary>网络摄像头（RTSP / HTTP 地址，用户手填）。</summary>
    Network = 1,
}

/// <summary>
/// 一路画面源。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b><see cref="Address"/> 与 <see cref="Identity"/> 是两个东西，不能混用。</b>
/// 网络地址里**带凭据**（`rtsp://账号:密码@主机:554/流`），而
/// <see cref="Identity"/> 会被写进 `session.json` 与检索索引（`entries.jsonl`，
/// 那是用户要长期留着、还要交付出去的**证据元数据**）——
/// 把密码写进去等于把摄像头密码刻进每一份档案里。
/// </para>
/// <list type="bullet">
/// <item><see cref="Address"/>：**只给 ffmpeg 用**，别的地方一个都不要碰。</item>
/// <item><see cref="Identity"/>：进索引、进 manifest、进日志、上界面 —— 凭据已抹掉。</item>
/// </list>
/// </remarks>
public sealed record CameraSource(CameraSourceKind Kind, string Address)
{
    /// <summary>没有摄像头。</summary>
    public static CameraSource None { get; } = new(CameraSourceKind.Local, string.Empty);

    public static CameraSource Local(string? deviceName) =>
        new(CameraSourceKind.Local, deviceName?.Trim() ?? string.Empty);

    public static CameraSource Network(string? url) =>
        new(CameraSourceKind.Network, url?.Trim() ?? string.Empty);

    /// <summary>
    /// 由设置构造（与 <c>ArchiveTarget.FromConfig</c> 同一路数）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 选了「网络摄像头」但地址是空的，**不回落到本机设备** ——
    /// 回落的后果是「用户明明选了网络摄像头，录出来的却是本机那台」，
    /// 而画面上不会有任何迹象。空就是空，界面按「还没配好」处理。
    /// </remarks>
    public static CameraSource FromConfig(CameraSourceKind kind, string? deviceName, string? networkUrl) =>
        Enum.IsDefined(kind) && kind == CameraSourceKind.Network
            ? Network(networkUrl)
            : Local(deviceName);

    /// <summary>这一路是不是网络摄像头。</summary>
    public bool IsNetwork => Kind == CameraSourceKind.Network;

    /// <summary>没配（或配了个空的）。</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Address);

    /// <summary>
    /// 能进索引 / manifest / 日志 / 界面的那个身份 —— **凭据已抹掉**。
    /// </summary>
    /// <remarks>
    /// 本机设备就是设备名（里面没有秘密）。网络地址走 <see cref="Redact"/>。
    /// 返回值同时用于 <c>SourceDeviceId</c>：它要求**同一路视频每次算出来一样**
    /// （换件/续录要能认出是同一个来源），而「抹掉凭据」不影响这一点。
    /// </remarks>
    public string Identity => IsNetwork ? Redact(Address) : Address;

    /// <summary>
    /// 界面上显示的名字。
    /// </summary>
    /// <remarks>
    /// 网络那一档带上来源说明：用户同时接着本机摄像头与 IP 摄像头时，
    /// 光看一个 IP 认不出来它是哪一路。
    /// </remarks>
    public string Display => IsNetwork ? $"网络摄像头 · {Identity}" : Address;

    /// <summary>
    /// 这一路配得对不对；<see langword="null"/> = 没问题。
    /// </summary>
    /// <remarks>
    /// 给界面用（本仓惯例：配不了的东西**禁用 + 悬停写明原因**，
    /// 踩坑 #13「绝不渲染一个没有任何作用的入口」）。措辞要能直接贴到提示上。
    /// </remarks>
    public string? ConfigurationProblem
    {
        get
        {
            if (!IsNetwork)
            {
                return null;
            }

            if (IsEmpty)
            {
                return "还没填网络摄像头地址。";
            }

            // ⚠️ 必须有 scheme：ffmpeg 不认裸的 `192.168.1.64/stream`，
            // 而它报出来的错（`Protocol not found`）对用户毫无指向性。
            return Address.Contains("://", StringComparison.Ordinal)
                ? null
                : "地址要以 rtsp:// 或 http:// 开头，例如 rtsp://账号:密码@192.168.1.64:554/stream。";
        }
    }

    /// <summary>
    /// 拼 ffmpeg 的**输入**参数（<c>-i</c> 以及它之前那些选项）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>两只的输入参数形状不一样，这不是笔误</b>：
    /// </para>
    /// <list type="bullet">
    /// <item><b>本机设备</b>：`-video_size` / `-framerate` 是**输入**选项 ——
    /// 对 dshow 来说是「按这个模式打开设备」，打不开就报错（那正是探测要的）。</item>
    /// <item><b>网络地址</b>：**一个都不能带**。RTSP 没法要求对端发多大就是多大，
    /// 而 `-video_size` 对 rtsp 解复用器来说是个**不存在的选项** ⇒
    /// 写上它 ffmpeg 直接报 `Option not found`、起都起不来。
    /// 所以网络那一路的尺寸改在**输出侧**做（<c>-vf scale</c>，见
    /// <see cref="FfmpegCameraCapture.BuildArguments"/>）。</item>
    /// </list>
    /// <para>
    /// ⚠️ <c>-rtsp_transport tcp</c>：UDP 在很多现场网络里被防火墙丢掉，
    /// 表现为「偶尔能连、多数时候连不上」—— 那种故障极难排查。
    /// TCP 慢一点但稳，工位上要的是稳。
    /// </para>
    /// </remarks>
    /// <param name="bufferSize">本机设备那一路的 DirectShow 缓冲（两个调用点取值不同）。</param>
    /// <param name="videoSize">
    /// 本机设备要按哪一档打开（<c>1280x720</c> 这种）；<see langword="null"/> = 用设备自己的默认档。
    /// <para>
    /// ⚠️ 收的是**字符串**而不是 <see cref="RecordingSpec"/>：两个调用点要的尺寸不一样
    /// （录制要用户选的那档，取景识码要固定的 640×480），而 640×480
    /// 根本不是 <see cref="RecordingSpec"/> 能表达的三档之一。
    /// 让本类型认识录制规格，只为省一个参数，是笔亏本买卖。
    /// </para>
    /// </param>
    public IReadOnlyList<string> InputArguments(string bufferSize, string? videoSize = null)
    {
        if (IsNetwork)
        {
            return ["-rtsp_transport", "tcp", "-i", Address];
        }

        var arguments = new List<string> { "-f", "dshow", "-rtbufsize", bufferSize };

        if (!string.IsNullOrWhiteSpace(videoSize))
        {
            arguments.Add("-video_size");
            arguments.Add(videoSize);
            // 帧率跟尺寸一起给：两者都是「按这个模式打开设备」的一部分，
            // 而规格 §3.1.7 说帧率固定 30、不提供选择。
            arguments.Add("-framerate");
            arguments.Add(RecordingSpec.FrameRate.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add("-i");
        arguments.Add($"video={Address}");

        return arguments;
    }

    /// <summary>
    /// 把 URL 里的 <c>账号:密码@</c> 抹掉。
    /// </summary>
    /// <remarks>
    /// 转调 <see cref="Diagnostics.UrlCredentials.Strip"/> —— 那一份是共用的：
    /// <c>SystemProcessRunner</c> 在失败时要把整条 argv 写进日志，
    /// 而网络摄像头的地址就在那里面。两处各写一份的话迟早有一处漏掉，
    /// 而漏掉的那一处正好是日志（见那个类的说明）。
    /// </remarks>
    public static string Redact(string url) => Diagnostics.UrlCredentials.Strip(url);
}
