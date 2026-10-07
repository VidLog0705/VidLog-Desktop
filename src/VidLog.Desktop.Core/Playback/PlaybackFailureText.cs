using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Playback;

/// <summary>
/// 检索页里「这段录像播不了」到底该说哪句话（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>系统那句话不能照抄</b>。文件明明在盘上（选中时已经核过），而播放组件
/// 解不了这个编码时报的原话是「找不到媒体文件。」—— 照抄等于把用户指去查一个
/// **好端端在盘上**的文件（2026-10-02 在 H.265 的成品上实测到的就是这么一句）。
/// 所以说清楚三件事：**文件在**、问题在**这台电脑的解码能力**、哪一档编码最容易缺。
/// </para>
/// <para>
/// ⚠️ 真不在盘上时，系统那句话反倒**是对的** —— 照实说，别改。
/// </para>
/// <para>
/// ⚠️ 原先长在 <c>SearchWindow</c> 里，而那个工程没有测试工程 ——
/// 「文件在盘上却说不在」正是这里最坏的一种错，而它原来没有东西能挡。
/// </para>
/// </remarks>
public static class PlaybackFailureText
{
    /// <summary>界面上那一句。</summary>
    /// <param name="systemMessage">播放组件自己那句话（可能为空）。</param>
    /// <param name="path">这一条的路径；**没选中任何一条**时为 <see langword="null"/>。</param>
    /// <param name="fileOnDisk">那个路径上真有文件（调用处核过）。</param>
    /// <param name="storedCodec">库里记的编码（认不出来给 <see langword="null"/>）。</param>
    public static string Describe(
        string? systemMessage, string? path, bool fileOnDisk, string? storedCodec)
    {
        var system = systemMessage ?? "系统解码器不支持";

        if (path is null)
        {
            return $"这段录像播不了：{system}";
        }

        if (!fileOnDisk)
        {
            return $"这段录像播不了：{path} 不在盘上（{system}）";
        }

        return "这段录像播不了：文件在盘上，是这台电脑的播放组件打不开它"
            + CodecHint(storedCodec)
            + $"。（系统的原话：{system}）";
    }

    /// <summary>这一条是什么编码录的；认不出来就只说「多半是缺解码器」。</summary>
    /// <remarks>
    /// ⚠️ 名字走 <see cref="RecordingSpec.CodecLabel"/>（**唯一一处产出**）：
    /// 规格要求界面上**不得出现「HEVC」** —— 两个名字混用会让用户以为是两种编码。
    /// 所以连带指路时也只说「H.265 的解码器」与商店里那个「视频扩展」，
    /// **不印那个英文缩写**。
    /// </remarks>
    private static string CodecHint(string? storedCodec)
    {
        if (!Enum.TryParse<VideoCodec>(storedCodec, ignoreCase: true, out var codec))
        {
            return "，多半是缺这个编码的解码器（也可能是这个文件坏了）";
        }

        var label = new RecordingSpec(codec, VideoResolution.P1080).CodecLabel;

        return codec == VideoCodec.H265
            ? $"，这条录像是 {label} 编码的 —— Win10 默认不带 {label} 的解码器，"
              + "装上（Windows 商店里那个「视频扩展」就是干这个的）就能放"
            : $"，这条录像是 {label} 编码的，多半是缺解码器（也可能是这个文件坏了）";
    }
}
