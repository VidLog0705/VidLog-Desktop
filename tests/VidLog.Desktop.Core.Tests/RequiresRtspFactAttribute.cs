using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 只在**真的有一路网络摄像头可连**时运行的用例。
/// </summary>
/// <remarks>
/// <para>
/// 地址从环境变量 <see cref="EnvironmentVariable"/> 读，**不写进仓库** ——
/// 它是内网地址，而且多半带着摄像头的用户名密码。
/// </para>
/// <para>
/// ⚠️ 与 <see cref="RequiresCameraFactAttribute"/> 同一条规矩：
/// **不许在方法体里静默 <c>return</c>**。跳过必须走 <c>Skip</c>，
/// 让「跳过」出现在结果里 —— 否则就是「看起来通过了」那种最贵的失败。
/// 而这一条尤其要紧：本仓此前**从来没有端到端验过 RTSP**，
/// 一条静默返回的绿会让人以为验过了。
/// </para>
/// <para>
/// 用法（地址是内网地址，别提交）：
/// <code>
/// $env:VIDLOG_TEST_RTSP_URL = 'rtsp://账号:密码@192.168.1.9:8554/live'
/// dotnet test --filter FullyQualifiedName~NetworkCameraIntegrationTests
/// </code>
/// </para>
/// <para>
/// ⚠️ 环境变量名里的 <c>RTSP</c> **是沿用下来的老名字**，收的其实是**任何网络摄像头地址**
/// —— `http://` 那一路（MJPEG over HTTP 那种手机 IP 摄像头）同样走这一组用例。
/// 名字不改是因为改了会**静默变成跳过**（变量没设 ⇒ 整组跳过 ⇒ 看起来通过了），
/// 那正是本类型存在要防的东西。
/// </para>
/// </remarks>
public sealed class RequiresRtspFactAttribute : FactAttribute
{
    /// <summary>放网络摄像头地址的环境变量名（名字沿用，收的不止 RTSP）。</summary>
    public const string EnvironmentVariable = "VIDLOG_TEST_RTSP_URL";

    public RequiresRtspFactAttribute()
    {
        if (FfmpegLocator.TryFind() is null)
        {
            // ⚠️ **不打**「允许跳过」标记：FFmpeg 本该有（CI 会装），
            // 缺了就说明环境配错了，守卫必须红。
            Skip = "本机没有 FFmpeg —— 网络摄像头集成测试跳过";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
        {
            Skip = SkipMarker.Allow(
                $"没有设置 {EnvironmentVariable} —— 网络摄像头集成测试跳过"
                + "（本机没有可连的网络摄像头；要跑就设成 rtsp://账号:密码@主机:端口/路径"
                + " 或 http://账号:密码@主机:端口）");
        }
    }
}
