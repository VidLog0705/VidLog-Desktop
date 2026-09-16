using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 只在装了 FFmpeg 的机器上跑的测试。
/// </summary>
/// <remarks>
/// 没装 FFmpeg 时**显式标为跳过**，而不是让测试悄悄提前 return ——
/// 后者会显示绿色但其实什么都没验，是「看起来通过了」那种最贵的失败。
/// 跳过原因会出现在测试输出里，看得见。
/// <para>
/// ubuntu-latest 与 windows-latest 的 GitHub runner 都自带 FFmpeg，
/// 所以这些测试在 CI 上是真跑的。
/// </para>
/// </remarks>
public sealed class RequiresFfmpegFactAttribute : FactAttribute
{
    public RequiresFfmpegFactAttribute()
    {
        if (FfmpegLocator.TryFind() is null)
        {
            Skip = "本机没有 FFmpeg —— 集成测试跳过（CI 上装了，会真跑）";
        }
    }
}
