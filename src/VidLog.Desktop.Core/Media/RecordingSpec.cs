namespace VidLog.Desktop.Core.Media;

/// <summary>
/// 编码格式（规格 §3.1.7）。
/// </summary>
/// <remarks>
/// ⚠️ <b>界面上一律写「H.265」，任何地方都不得出现「HEVC」</b> ——
/// 规格原话：「两个名字混用会让用户以为是两种不同的编码」。
/// 这个名字由 <see cref="RecordingSpec.CodecLabel"/> 一处产出。
/// <para>
/// 枚举值落进设置文件，所以**顺序即格式**，别改。
/// </para>
/// </remarks>
public enum VideoCodec
{
    H264 = 0,
    H265 = 1,
}

/// <summary>分辨率档位（规格 §3.1.7）。枚举值落进设置文件，别改顺序。</summary>
public enum VideoResolution
{
    Uhd4K = 0,
    P1080 = 1,
    P720 = 2,
}

/// <summary>
/// 成片方向 —— 电脑端按「**转多少度**」命名（需求方 2026-09-29 裁决）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么不沿用手机端那三个名字（横左 / 竖屏 / 横右）</b>：
/// 那三档是按**手机持机方向**定义的（「横左 = 手机向左倒，听筒朝左」），
/// 而**电脑端的摄像头是固定的**，系统不知道它被装成什么样 ——
/// 「横左」在这里没有对应物。照搬名字会让用户选「横左」却拿到竖的成片。
/// </para>
/// <para>
/// ⚠️ <b>「左 / 右」指的是**画面内容**往哪边转</b>，不是「摄像头往哪边倒」：
/// </para>
/// <list type="bullet">
/// <item><see cref="Left90"/>：画面**逆时针**转 90°（ffmpeg <c>transpose=2</c>）。</item>
/// <item><see cref="Right90"/>：画面**顺时针**转 90°（ffmpeg <c>transpose=1</c>）。</item>
/// <item><see cref="UpsideDown"/>：画面上下颠倒（ffmpeg <c>hflip,vflip</c>）。</item>
/// </list>
/// <para>
/// ⚠️ 这一条**必须写死在这里**：手机端的同名枚举就踩过这个坑
/// （「iOS 的旋转角、安卓的 setOrientationHint，两边系统的命名习惯与此**正好相反**，
/// 别照着名字改」）。所以这里连**滤镜参数**都写进来，将来谁改都要对着这一处。
/// 转 90° 的那两个还会**交换宽高**（见 <see cref="RecordingSpec.Size"/>）。
/// </para>
/// </remarks>
public enum CameraRotation
{
    /// <summary>不转（默认）。装正了的摄像头走这一档。</summary>
    /// <remarks>
    /// ⚠️ <b>显式写 0</b>：老设置文件里没有这个字段，而老设置里的电脑端
    /// **一定是不转的**（那时候根本没有方向这一项）。
    /// </remarks>
    None = 0,

    /// <summary>画面逆时针转 90°（<c>transpose=2</c>）。成片**交换宽高**。</summary>
    Left90 = 1,

    /// <summary>画面顺时针转 90°（<c>transpose=1</c>）。成片**交换宽高**。</summary>
    Right90 = 2,

    /// <summary>画面上下颠倒（<c>hflip,vflip</c>）。成片宽高**不变**。</summary>
    UpsideDown = 3,
}

/// <summary>
/// 方向 → ffmpeg 滤镜。**产出只有这一处。**
/// </summary>
/// <remarks>
/// ⚠️ 抽出来是因为**两个地方**都要它：录制那一档（`FfmpegCameraCapture` 的滤镜链）
/// 与取景识码那一档（`ScannerProcess`）。抄两份的话两边迟早不一致 ——
/// 而「录出来是正的、识码却要倒着认」正是那样来的，且看起来像「识码坏了」。
/// <para>
/// ⚠️ 参数含义在这里**写死**（与 <see cref="CameraRotation"/> 的说明配套）：
/// <c>transpose=1</c> 是**顺时针**、<c>transpose=2</c> 是**逆时针**。
/// 180° 用 <c>hflip,vflip</c> 而不是 <c>transpose=2,transpose=2</c>：
/// 后者多绕一层宽高交换、还要带参数，而「上下颠倒」正是 180° 的样子。
/// </para>
/// <para>
/// ⚠️ 转 90° 的那两个会**交换宽高** —— 调用方要么按 <see cref="RecordingSpec.Size"/>
/// 算成片尺寸，要么（识码那一档）把采集侧缩到「转完正好是读端要的尺寸」。
/// </para>
/// </remarks>
public static class CameraRotationFilters
{
    /// <summary>不转时返回 <see langword="null"/>（调用方据此不往滤镜链里加东西）。</summary>
    public static string? For(CameraRotation rotation) => rotation switch
    {
        CameraRotation.Left90 => "transpose=2",
        CameraRotation.Right90 => "transpose=1",
        CameraRotation.UpsideDown => "hflip,vflip",
        _ => null,
    };
}

/// <summary>
/// 录制规格：编码 + 分辨率 + 方向（规格 §3.1.7）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 2026-09-29 起**方向也在这里**（原来是「方向只在手机端」）。
/// 依据是需求方两次裁决：① 设计图向导第 2 步有方向控件；
/// ② 「要完整三档方向」+ 电脑端按「转多少度」命名（共四档，见
/// <see cref="CameraRotation"/>）。
/// </para>
/// <para>
/// <b>帧率不在这里</b>：规格「上限 30 帧、**不提供选择**」，所以它是一个常量
/// （<see cref="FrameRate"/>），不是配置项。
/// </para>
/// </remarks>
public sealed record RecordingSpec(
    VideoCodec Codec,
    VideoResolution Resolution,
    CameraRotation Rotation = CameraRotation.None)
{
    /// <summary>默认档（规格 §3.1.7 的表格：H.264 + 1080P + 不转）。</summary>
    public static RecordingSpec Default { get; } = new(VideoCodec.H264, VideoResolution.P1080);

    /// <summary>帧率上限。规格：「最高 30 帧、不提供选择」。</summary>
    public const int FrameRate = 30;

    /// <summary>
    /// **采集**尺寸 —— 相机要按这个模式打开 / 缩放的目标。恒为横屏 16:9。
    /// </summary>
    /// <remarks>
    /// ⚠️ 三档分辨率都是按**横着**定义的（1080P = 1920×1080），
    /// 因为摄像头本身只会按它自己的模式出图 —— 方向是**采集之后**才做的事。
    /// 所以这个值**与方向无关**。
    /// </remarks>
    public (int Width, int Height) CaptureSize => Resolution switch
    {
        VideoResolution.Uhd4K => (3840, 2160),
        VideoResolution.P720 => (1280, 720),
        _ => (1920, 1080),
    };

    /// <summary>
    /// **成片**尺寸 —— 播放器看到的那一个。转 90° / 270° 时**交换宽高**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与 <see cref="CaptureSize"/> 分开是**刻意的**，别合成一个：
    /// 用错的那一处不会报错，只会「水印按错误的尺寸排版」
    /// （字跑到画面外或挤成一团）或者「容量按错误的像素数估」
    /// —— 两种都是静默的。所以用哪个**必须在调用点写清楚**。
    /// </remarks>
    public (int Width, int Height) Size => Rotation is CameraRotation.Left90 or CameraRotation.Right90
        ? (CaptureSize.Height, CaptureSize.Width)
        : CaptureSize;

    /// <summary>ffmpeg 的 <c>-video_size</c> 参数值（**采集**侧）。</summary>
    public string FfmpegSize => $"{CaptureSize.Width}x{CaptureSize.Height}";

    /// <summary>界面上写的方向名（**唯一一处产出**，与 <see cref="CodecLabel"/> 同一条规矩）。</summary>
    public string RotationLabel => Rotation switch
    {
        CameraRotation.Left90 => "左转 90°",
        CameraRotation.Right90 => "右转 90°",
        CameraRotation.UpsideDown => "转 180°",
        _ => "不转",
    };

    /// <summary>转方向的 ffmpeg 滤镜；不转时是 <see langword="null"/>。</summary>
    /// <remarks>
    /// 转调 <see cref="CameraRotationFilters.For"/> —— **产出只有那一处**：
    /// 录制那一档与取景识码那一档都要它，抄两份的话两边迟早不一致，
    /// 而「录出来是正的、识码却要倒着认」正是那样来的。
    /// </remarks>
    public string? RotationFilter => CameraRotationFilters.For(Rotation);

    /// <summary>这个编码该先试哪些编码器（顺序 = 偏好，真正决定用谁的是实测）。</summary>
    /// <remarks>
    /// H.265 那一组是本机**实测编不出来的**（无 N 卡/A 卡，而 HD 630 上 qsv 编不了 HEVC）
    /// —— 那正是「回落必须可见」存在的理由：列出来 ≠ 能用（规格 §3.1.7 原话）。
    /// </remarks>
    public IReadOnlyList<string> EncoderCandidates => Codec switch
    {
        VideoCodec.H265 => ["hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265"],
        _ => ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"],
    };

    /// <summary>界面上写的编码名（**一处产出**，见 <see cref="VideoCodec"/> 的说明）。</summary>
    public string CodecLabel => Codec == VideoCodec.H265 ? "H.265" : "H.264";

    /// <summary>界面上写的分辨率名。</summary>
    public string ResolutionLabel => Resolution switch
    {
        VideoResolution.Uhd4K => "4K",
        VideoResolution.P720 => "720P",
        _ => "1080P",
    };

    /// <summary>
    /// 「H.264 1080P」这样的一句话 —— 回落的提示里就要这一句。
    /// </summary>
    /// <remarks>
    /// ⚠️ 方向**只在非默认时**才附上：回落提示里带一个不参与回落的东西是噪声，
    /// 而默认档那句话（现有日志与测试都在断言它）一个字都不该变。
    /// </remarks>
    public string Label => Rotation == CameraRotation.None
        ? $"{CodecLabel} {ResolutionLabel}"
        : $"{CodecLabel} {ResolutionLabel} {RotationLabel}";

    /// <summary>
    /// 回落顺序：先用户选的那个，再逐级退。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规格只说「回落到**真正能跑通的组合**」，没写顺序。这里的顺序是：
    /// ① 用户选的；② 同编码降分辨率（画质差一点，但用户的编码偏好保住了）；
    /// ③ 保 1080P 换回 H.264（兼容性最好的那一档）；④ 720P + H.264（最后的兜底）。
    /// 去掉重复项，所以用户选的就是默认档时不会重复试。
    /// </para>
    /// <para>
    /// ⚠️ <b>方向不参与回落 —— 回落表里每一档都保住用户选的那个方向。</b>
    /// 与手机端同一条理由（那边原话：「它是『怎么拿手机』，不是设备能力 ——
    /// 手机转个身而已，没有『这台手机转不了』这回事」）：
    /// 电脑端的方向是「摄像头装成什么样」，也与编码能力无关。
    /// 回落里换掉方向只会让用户莫名其妙地拿到一段方向不对的录像 ——
    /// 而方向错了的画面**可能整段都不能用**（不是画质差一点）。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<RecordingSpec> FallbacksFrom(RecordingSpec wanted)
    {
        var wantedCodec = wanted.Codec;
        var wantedRotation = wanted.Rotation;

        var ordered = new List<RecordingSpec>
        {
            wanted,
            new(wantedCodec, VideoResolution.P1080, wantedRotation),
            new(wantedCodec, VideoResolution.P720, wantedRotation),
            new(VideoCodec.H264, VideoResolution.P1080, wantedRotation),
            new(VideoCodec.H264, VideoResolution.P720, wantedRotation),
        };

        var seen = new HashSet<RecordingSpec>();
        return [.. ordered.Where(seen.Add)];
    }
}
