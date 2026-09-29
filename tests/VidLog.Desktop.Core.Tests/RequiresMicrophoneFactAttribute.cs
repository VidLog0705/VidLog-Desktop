using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 只在**本机真的有麦克风**时运行的用例。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="RequiresCameraFactAttribute"/> 分开是有意的：那一条查**视频**设备，
/// 这一条查**音频**设备 —— 「有摄像头」不等于「有麦克风」，而
/// <c>DshowDevices</c> 的注释已经记过这个坑（两类设备是两次各自独立的枚举）。
/// </para>
/// <para>
/// 它是 2026-09-29 才有的：在此之前，电脑端**从来没有一台带麦克风的机器**
/// 跑过音频那一半，于是 §3.1.8 的音轨只在「坏设备」方向上验过。
/// </para>
/// <para>
/// 和同族一样：**不许在方法体里静默 <c>return</c>**。跳过必须走 <c>Skip</c>，
/// 让「跳过」出现在结果里。
/// </para>
/// </remarks>
public sealed class RequiresMicrophoneFactAttribute : FactAttribute
{
    public RequiresMicrophoneFactAttribute()
    {
        var ffmpeg = FfmpegLocator.TryFind();
        if (ffmpeg is null)
        {
            // ⚠️ **不打**「允许跳过」标记：与摄像头那一条同一条规矩 ——
            // FFmpeg 本该有（CI 会装），缺了就是环境配错。
            Skip = "本机没有 FFmpeg —— 麦克风集成测试跳过";
            return;
        }

        // 枚举是同步等待一个短命进程；它只在特性构造时跑一次。
        var devices = DshowDevices
            .ListAudioAsync(ffmpeg)
            .GetAwaiter()
            .GetResult();

        if (devices.Count == 0)
        {
            // 这台机器**本来就没有**麦克风（CI 也没有）⇒ 打标记放行。
            Skip = SkipMarker.Allow(
                "本机没有可用的音频设备 —— 麦克风集成测试跳过（CI 上通常也没有）");
        }
    }
}
