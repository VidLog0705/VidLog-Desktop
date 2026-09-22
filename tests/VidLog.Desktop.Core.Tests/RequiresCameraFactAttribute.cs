using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 只在**本机真的有摄像头**时运行的用例。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="RequiresFfmpegFactAttribute"/> 的分工：那个只查 ffmpeg 在不在，
/// 而「有 ffmpeg」不等于「有摄像头」—— 上一台开发机两样都有，就是枚举不到设备，
/// 摄像头采集正是因此被搁置的（电脑端 <c>docs/实现决策.md</c> §6）。
/// </para>
/// <para>
/// 和它一样：**不许在方法体里静默 <c>return</c>**。跳过必须走 <c>Skip</c>，
/// 让「跳过」出现在结果里 —— 否则就是「看起来通过了」那种最贵的失败。
/// </para>
/// </remarks>
public sealed class RequiresCameraFactAttribute : FactAttribute
{
    public RequiresCameraFactAttribute()
    {
        var ffmpeg = FfmpegLocator.TryFind();
        if (ffmpeg is null)
        {
            Skip = "本机没有 FFmpeg —— 摄像头集成测试跳过";
            return;
        }

        // 枚举是同步等待一个短命进程，这里同步等它是可接受的：
        // 它只在特性构造时跑一次，且 ffmpeg 会立刻打印完设备表退出。
        var devices = CameraDevices
            .ListAsync(ffmpeg)
            .GetAwaiter()
            .GetResult();

        if (devices.Count == 0)
        {
            Skip = "本机没有可用的视频设备 —— 摄像头集成测试跳过（CI 上通常也没有）";
        }
    }
}
