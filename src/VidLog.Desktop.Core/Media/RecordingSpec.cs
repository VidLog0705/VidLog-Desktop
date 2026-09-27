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
/// 录制规格：编码 + 分辨率（规格 §3.1.7）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>方向不在这里</b> —— 规格 §3.1.7 ② 明确「方向**只在手机端**」：
/// 电脑端的摄像头方向由设备与安装决定，不提供选项。所以这一端的成片是**横屏 16:9**。
/// </para>
/// <para>
/// <b>帧率不在这里</b>：规格「上限 30 帧、**不提供选择**」，所以它是一个常量
/// （<see cref="FrameRate"/>），不是配置项。
/// </para>
/// </remarks>
public sealed record RecordingSpec(VideoCodec Codec, VideoResolution Resolution)
{
    /// <summary>默认档（规格 §3.1.7 的表格：H.264 + 1080P）。</summary>
    public static RecordingSpec Default { get; } = new(VideoCodec.H264, VideoResolution.P1080);

    /// <summary>帧率上限。规格：「最高 30 帧、不提供选择」。</summary>
    public const int FrameRate = 30;

    /// <summary>成片尺寸。**横屏 16:9**（电脑端没有方向选项，见类注释）。</summary>
    public (int Width, int Height) Size => Resolution switch
    {
        VideoResolution.Uhd4K => (3840, 2160),
        VideoResolution.P720 => (1280, 720),
        _ => (1920, 1080),
    };

    /// <summary>ffmpeg 的 <c>-video_size</c> 参数值。</summary>
    public string FfmpegSize => $"{Size.Width}x{Size.Height}";

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

    /// <summary>「H.264 1080P」这样的一句话 —— 回落的提示里就要这一句。</summary>
    public string Label => $"{CodecLabel} {ResolutionLabel}";

    /// <summary>
    /// 回落顺序：先用户选的那个，再逐级退。
    /// </summary>
    /// <remarks>
    /// 规格只说「回落到**真正能跑通的组合**」，没写顺序。这里的顺序是：
    /// ① 用户选的；② 同编码降分辨率（画质差一点，但用户的编码偏好保住了）；
    /// ③ 保 1080P 换回 H.264（兼容性最好的那一档）；④ 720P + H.264（最后的兜底）。
    /// 去掉重复项，所以用户选的就是默认档时不会重复试。
    /// </remarks>
    public static IReadOnlyList<RecordingSpec> FallbacksFrom(RecordingSpec wanted)
    {
        var wantedCodec = wanted.Codec;

        var ordered = new List<RecordingSpec>
        {
            wanted,
            new(wantedCodec, VideoResolution.P1080),
            new(wantedCodec, VideoResolution.P720),
            new(VideoCodec.H264, VideoResolution.P1080),
            new(VideoCodec.H264, VideoResolution.P720),
        };

        var seen = new HashSet<RecordingSpec>();
        return [.. ordered.Where(seen.Add)];
    }
}
