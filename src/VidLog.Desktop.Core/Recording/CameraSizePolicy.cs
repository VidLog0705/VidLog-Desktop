using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 对端发的尺寸与用户选的那一档，够不够。
/// </summary>
public enum CameraSizeVerdict
{
    /// <summary>对端够（或者比用户选的还大）⇒ 直接按用户选的走，缩下来。</summary>
    Enough = 0,

    /// <summary>
    /// 对端比用户选的小 ⇒ **要放大**，得问用户（那是会变大的文件，画面不会更清晰）。
    /// </summary>
    NeedsUpscale = 1,

    /// <summary>读不出对端的尺寸 ⇒ **不挡**，按用户选的走，但要如实说「没测出来」。</summary>
    Unknown = 2,
}

/// <summary>
/// 网络摄像头的分辨率判决。
/// </summary>
/// <remarks>
/// <para>
/// 依据是需求方 2026-09-29 的裁决（原话）：
/// 「**选择网络摄像头时先检测摄像头支持的像素，如果支持 1080P 就选 1080p，
/// 如果仅支持 720P 就弹选择，通知用户，并说明选 1080P 和 4K 的后果，
/// 由用户自己选择。**」
/// </para>
/// <para>
/// ⚠️ <b>为什么网络这一档需要这一套，而本机设备不需要</b>：dshow 设备可以「按这个尺寸
/// 打开」（<c>-video_size</c> 是输入选项），打不开就报错；而 RTSP **没法要求对端发多大**
/// —— 只能收下它发的，再缩放到你要的档。所以「够不够」这件事必须**问出来、说出来**，
/// 不能像本机那样靠「打开失败」自然暴露。
/// </para>
/// <para>
/// ⚠️ <b>老实说清一个天花板</b>：标准 RTSP **没有**「问它支持哪些分辨率」这回事。
/// 能拿到的只有**当前正在发的那条流**的尺寸。对端要改成别的档，
/// 得去它自己的 Web 界面改（本仓管不着）。所以文案里说的是「当前这条流」，
/// 不是「这台摄像头支持什么」—— 把前者说成后者是在承诺做不到的事。
/// </para>
/// </remarks>
public static class CameraSizePolicy
{
    /// <summary>
    /// 判一下够不够。
    /// </summary>
    /// <remarks>
    /// ⚠️ 判据用**高度**，不是像素总数：分辨率档位（720P / 1080P / 4K）
    /// 本来就是按高度命名的，用户心里那个「够不够清晰」也是高度。
    /// 用像素数的话，一个 2560×720 的宽幅流会被判成「比 1080P 大」——
    /// 而它只有 720 行，画面比 1080P 糊。
    /// </remarks>
    public static CameraSizeVerdict Judge(int? sourceWidth, int? sourceHeight, VideoResolution wanted)
    {
        if (sourceWidth is not > 0 || sourceHeight is not > 0)
        {
            return CameraSizeVerdict.Unknown;
        }

        // ⚠️ 取 **CaptureSize**（分辨率档位的原始尺寸）—— 这里比的是「对端发多大
        // vs 用户选的档」，与成片方向无关。
        var (_, wantedHeight) = new RecordingSpec(VideoCodec.H264, wanted).CaptureSize;

        return sourceHeight.Value >= wantedHeight
            ? CameraSizeVerdict.Enough
            : CameraSizeVerdict.NeedsUpscale;
    }

    /// <summary>
    /// 不够时**给用户看的那段话** —— 把「选它有什么后果」说清楚。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 裁决原话要求「**说明选 1080P 和 4K 的后果**」。所以这里**把所有比
    /// 对端大的档位逐个列出来**，而不是只说用户当前选的那一档 ——
    /// 用户要做的选择是「仍然选大的」还是「按原尺寸」，只讲一档等于没给他选择的依据。
    /// </para>
    /// <para>
    /// ⚠️ <b>这段字会**原样出现在界面上**，所以不许有 markdown 标记</b>
    /// （`**加粗**` 在 WPF 的 TextBlock 里只会显示成字面的星号）。
    /// 这也是为什么「一条规则放 Core」时要注意：它测得到，但它也直接面向用户。
    /// </para>
    /// <para>
    /// ⚠️ 放在 Core 而不是界面里，是为了**它测得到**（与
    /// <see cref="SpecSelectionPolicy.Describe"/> 同一个理由：App 层没有测试工程）。
    /// 弹框本身在电脑端的配置向导里（批次 3）。
    /// </para>
    /// </remarks>
    public static string Describe(int sourceWidth, int sourceHeight, VideoResolution wanted)
    {
        var sourcePixels = (long)sourceWidth * sourceHeight;

        // ⚠️ 从小到大列，且**只列比对端大的那几档** —— 比它小的档是缩小，不是问题，
        // 混进来只会让用户以为「选小档也有代价」。
        var larger = Enum.GetValues<VideoResolution>()
            .Where(r => SizeOf(r).Height > sourceHeight)
            .OrderBy(r => SizeOf(r).Height)
            .ToList();

        // 一档都不用放大 ⇒ 判决本来就是 Enough、**不该走到这里**。
        // 但真被叫到时必须说一句对的话 —— 无条件写「放大的代价」会在这种情况下
        // 变成**一句反话**（对端 4K、用户选 720P 时也劝他别放大）。
        if (larger.Count == 0)
        {
            return $"这个网络摄像头当前发的是 {sourceWidth}×{sourceHeight}，"
                + $"够 {Label(wanted)} 用（录像会把它缩到你要的尺寸，画质不会因此变差）。";
        }

        var native = NativeResolutionFor(sourceHeight);
        var (nativeWidth, nativeHeight) = SizeOf(native);

        var text = new System.Text.StringBuilder();

        text.Append($"这个网络摄像头当前发的是 {sourceWidth}×{sourceHeight}，"
            + $"你选的是 {Label(wanted)} —— 比它大，录像会把它放大到你要的尺寸。");
        text.Append('\n');
        text.Append('\n');
        text.Append("放大的代价：文件明显变大，而画面不会更清晰（丢掉的细节补不回来）。各档的代价：");

        foreach (var candidate in larger)
        {
            var (width, height) = SizeOf(candidate);
            var ratio = (double)((long)width * height) / sourcePixels;

            text.Append($"\n· 选 {Label(candidate)}（{width}×{height}）⇒ 画面像素变成 {ratio:0.#} 倍");
        }

        text.Append('\n');
        text.Append('\n');
        text.Append($"建议按 {Label(native)}（{nativeWidth}×{nativeHeight}）录 —— "
            + "与原画一样大，文件最小，画质不失真。");
        text.Append('\n');
        text.Append("（本机读到的只是你当前这条流的尺寸，管不了摄像头发多大。"
            + "要它发大一点，得去它自己的设置界面改。）");

        return text.ToString();
    }

    private static (int Width, int Height) SizeOf(VideoResolution resolution) =>
        new RecordingSpec(VideoCodec.H264, resolution).Size;

    private static string Label(VideoResolution resolution) =>
        new RecordingSpec(VideoCodec.H264, resolution).ResolutionLabel;

    /// <summary>
    /// 对端这个高度最接近哪一档。
    /// </summary>
    /// <remarks>
    /// 用来给「按原尺寸录」那一条一个**用户认得的档位名** ——
    /// 只写「按 960×540 录」的话，用户对不上自己界面上那三个选项。
    /// 就近取档，不要求它正好等于某一档。
    /// </remarks>
    private static VideoResolution NativeResolutionFor(int sourceHeight)
    {
        var best = VideoResolution.P720;
        var bestGap = int.MaxValue;

        foreach (var candidate in Enum.GetValues<VideoResolution>())
        {
            var gap = Math.Abs(SizeOf(candidate).Height - sourceHeight);

            if (gap < bestGap)
            {
                best = candidate;
                bestGap = gap;
            }
        }

        return best;
    }
}
