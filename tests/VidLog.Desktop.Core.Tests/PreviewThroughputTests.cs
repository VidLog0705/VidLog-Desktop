using System.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 实时预览那一档的**吞吐**：一路彩色帧从 ffmpeg 的 stdout 出来，.NET 读不读得过来。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么单独验这一条</b>：§54 已经验过「一路源两路输出」，
/// 但**那一档是 1 fps 灰度**（识码用），而**预览要十几 fps 彩色** ——
/// 产出速率差两个数量级，**不能外推**（§62 里就写着这一条）。
/// </para>
/// <para>
/// ⚠️ 而设计上**不用两路输出**：出一路彩色帧，C# 侧顺带降频取帧喂 ZXing ——
/// 比两路简单，而且预览与识码**必然同帧**（两路的话要自己去对齐）。
/// 代价是读端必须读得过来，而这条用例量的就是它。
/// </para>
/// <para>
/// ⚠️ 用真 RTSP 源（不是 dshow）：相机独占那条限制对网络源不成立，
/// 所以这台机器上**只有它能验**。dshow 那一档仍然只有真机知道。
/// </para>
/// </remarks>
public class PreviewThroughputTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PreviewThroughputTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    /// <summary>预览的帧率。</summary>
    /// <remarks>
    /// 12 fps 是「够看」与「别白烧 CPU」之间的取舍：人眼对预览的流畅感
    /// 大致从 10 fps 起，而这是**配摄像头时看一眼**用的，不是给人看片的。
    /// </remarks>
    private const int Fps = 12;

    private const int Width = 640;
    private const int Height = 480;

    /// <summary>一帧 RGB24 的字节数。</summary>
    private const int FrameBytes = Width * Height * 3;

    [RequiresRtspFact]
    public async Task 一路彩色帧读得过来_帧数对得上而且不掉()
    {
        var ffmpeg = FfmpegLocator.TryFind()!;
        var url = Environment.GetEnvironmentVariable(RequiresRtspFactAttribute.EnvironmentVariable)!;

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        foreach (var argument in new[]
        {
            "-hide_banner", "-v", "error",
            "-rtsp_transport", "tcp",
            "-i", url,
            "-map", "0:v:0", "-an",
            "-vf", $"scale={Width}:{Height}",
            "-r", Fps.ToString(),
            "-f", "rawvideo", "-pix_fmt", "rgb24",
            "pipe:1",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // ⚠️ stderr 必须排空（理由同采集：不读满就会把进程顶住）。
        _ = Task.Run(async () =>
        {
            try { await process.StandardError.ReadToEndAsync(); } catch { /* 退了就是结束 */ }
        });

        var buffer = new byte[FrameBytes];
        var stream = process.StandardOutput.BaseStream;

        var seconds = TimeSpan.FromSeconds(6);
        var clock = Stopwatch.StartNew();
        var frames = 0;

        // 前 2 秒是起流 + 首帧，**不计入**（RTSP 建连与首帧解码都要时间）。
        var warmup = TimeSpan.FromSeconds(2);

        while (clock.Elapsed < seconds)
        {
            if (!await ReadExactlyAsync(stream, buffer, FrameBytes))
            {
                break;   // 流断了
            }

            if (clock.Elapsed > warmup)
            {
                frames++;
            }
        }

        var measured = clock.Elapsed - warmup;

        // 收尾：送 q 优雅停（同采集那一档的理由）。
        try
        {
            await process.StandardInput.WriteLineAsync("q");
            await process.StandardInput.FlushAsync();
        }
        catch
        {
            // stdin 不通就直接杀。
        }

        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
        }

        var expected = measured.TotalSeconds * Fps;

        // ⚠️ 把实测打出来：这条用例的价值一半在「通过」、一半在这个数 ——
        // 余量小的话预览方案就悬，而「>= 75%」这一个布尔值看不出余量。
        _output.WriteLine(
            $"预览吞吐：读满 {seconds.TotalSeconds:0} 秒（扣掉 {warmup.TotalSeconds:0} 秒起流）"
            + $"读到 {frames} 帧 / {measured.TotalSeconds:0.0} 秒"
            + $"= {frames / measured.TotalSeconds:0.0} fps（目标 {Fps}）");

        // ⚠️ 判据留 25% 余量：管道吞吐、RTSP 抖动、以及 12 fps 与实际帧率的取整
        // 都会让实测略低于理论值。**卡得过紧的断言会在别人的机器上碎**。
        Assert.True(
            frames >= expected * 0.75,
            $"预览帧数不够：{measured.TotalSeconds:0.0} 秒里只读到 {frames} 帧"
            + $"（{Fps} fps 下应当约 {expected:0} 帧）—— 读端跟不上，预览会卡");
    }

    /// <summary>把 <paramref name="count"/> 个字节读满；流结束返回 false。</summary>
    /// <remarks>
    /// ⚠️ **不能只读一次 <c>ReadAsync</c>**：管道会按任意边界返回
    /// （实测一次可能只给几 KB），只读一次的话帧边界会错位，
    /// 于是「读到的帧数」这个判据本身就是错的。
    /// </remarks>
    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;

        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
