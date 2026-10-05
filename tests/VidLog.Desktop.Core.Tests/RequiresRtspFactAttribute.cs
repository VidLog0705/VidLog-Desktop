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
/// ⚠️ 换一路源时，若它的分辨率**不是** 1080P，还要设
/// <see cref="SizeEnvironmentVariable"/> —— 否则
/// <c>NetworkCameraProbeTests.真网络摄像头_读出对端真实的尺寸与编码</c> 会红，
/// 而红的是**基准**不是产品（2026-10-05 实测：换成一台 640x480 的笔记本摄像头时撞上）。
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

    /// <summary>可选：那一路源**真实**的尺寸（写成 <c>宽x高</c>），给「读出对端真实的尺寸」那条当基准。</summary>
    public const string SizeEnvironmentVariable = "VIDLOG_TEST_CAMERA_SIZE";

    /// <summary>没设 <see cref="SizeEnvironmentVariable"/> 时的基准 —— 2026-09-29 那台手机 IP 摄像头。</summary>
    public const string DefaultSize = "1920x1080";

    /// <summary>
    /// <see cref="SizeEnvironmentVariable"/> 解析出来的基准尺寸。
    /// </summary>
    /// <remarks>
    /// ⚠️ 写不成 <c>宽x高</c> 就**抛**，不回落 —— 回落成默认值会让那条用例对着**另一路源**的
    /// 尺寸比，绿得毫无意义（本仓最贵的那种失败：看起来通过了）。
    /// </remarks>
    public static (int Width, int Height) ExpectedCameraSize
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(SizeEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = DefaultSize;
            }

            var parts = raw.Split(['x', 'X']);
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var width)
                || !int.TryParse(parts[1], out var height))
            {
                throw new InvalidOperationException(
                    $"{SizeEnvironmentVariable} 要写成「宽x高」（例如 {DefaultSize}），实际是「{raw}」。");
            }

            return (width, height);
        }
    }

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

        // ⚠️ 尺寸基准**不在这儿校验** —— 校验它就等于给整组用例加了一道门槛：
        // 只跑别的用例、没设尺寸的人会连带被跳过，而他要的东西跟尺寸无关。
        // 谁要基准谁自己读（见 ExpectedCameraSize）。
    }
}
