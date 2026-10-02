using System.Diagnostics;
using VidLog.Desktop.Core.Camera;
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
/// ⚠️ 在 <see cref="NetworkCameraCollection"/> 里：它要真连那台手机，而录制那两条
/// 同时在跑 1080p 的 x264 编码 —— 并行时这条会读到 7 fps（判据要 ≥ 9）。
/// </remarks>
[Collection(NetworkCameraCollection.Name)]
public class PreviewThroughputTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PreviewThroughputTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    /// <summary>预览的帧率。</summary>
    /// <remarks>
    /// ⚠️ 从 <see cref="PreviewProcess"/> 取，**不在这里各写一份** —— 原来这里写死 640×480，
    /// 而生产预览出的是 640×**360**：量的帧比生产大三分之一，那个数就替不了生产背书。
    /// </remarks>
    private const int Fps = PreviewProcess.Fps;

    /// <summary>一帧 RGB24 的字节数（生产预览的尺寸，见 <see cref="PreviewProcess.Width"/>）。</summary>
    private const int FrameBytes = PreviewProcess.Width * PreviewProcess.Height * 3;

    /// <summary>等一帧的上限。</summary>
    /// <remarks>
    /// ⚠️ 12 fps 下一帧间隔 83 ms，10 秒是它的一百多倍 —— 只有真卡住才够得着。
    /// <para>
    /// ⚠️ <b>这一条不是可选的</b>：地址**不可达**（主机在、只是不回）时 ffmpeg 会
    /// 一直卡在 TCP 连接上，**既不产出也不退出** ⇒ 没有上限的话这条用例永远挂着。
    /// 2026-10-02 反证时实测撞到（`rtsp://…192.168.101.55:8554` 手机没在推流）。
    /// 而它现在**独占跑**（见 <c>NetworkCameraCollection</c>），挂住就是**整套挂住**。
    /// </para>
    /// </remarks>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

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

        // ⚠️ 命令**从生产那一条取**（`PreviewProcess.BuildArguments`），不在这里另抄一份。
        //
        // 抄一份就一定会漂，而且漂了**没人会知道**：2026-10-02 实测到的就是它漂成了
        // `-rtsp_transport tcp` —— 那是 **rtsp 解复用器的私有选项**，加在 http 源上
        // ffmpeg 直接 `Option rtsp_transport not found`、**起都起不来**
        // （生产早按 scheme 分好了，见 `CameraSource.IsRtsp`）。
        // 于是这条用例量到的其实是**它自己那份 argv**，不是生产的。
        foreach (var argument in PreviewProcess.BuildArguments(CameraSource.Network(url)))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // ⚠️ stderr 必须排空（理由同采集：不读满就会把进程顶住），而且**留尾部** ——
        // 起不来时 ffmpeg 的原因只在它那儿，而下面那条断言要把它原样说出来。
        var errors = new BoundedTextTail();
        _ = Task.Run(async () =>
        {
            try
            {
                var stderrBuffer = new char[4096];
                int read;
                while ((read = await process.StandardError.ReadAsync(stderrBuffer)) > 0)
                {
                    errors.Append(stderrBuffer, read);
                }
            }
            catch { /* 退了就是结束 */ }
        });

        var seconds = TimeSpan.FromSeconds(6);

        // 前 2 秒是起流 + 首帧，**不计入**（RTSP 建连与首帧解码都要时间）。
        var warmup = TimeSpan.FromSeconds(2);

        // ⚠️ 读循环跑在 `Task.Run` 上 —— 与生产**同构**：`PreviewProcess` 那条读循环就是
        // `Task.Run(() => ReadAsync(...))` 起的，全程 `.ConfigureAwait(false)`。
        //
        // 这不是风格问题，是「这条用例到底在量谁」的问题。2026-10-02 实测，**同一台机、
        // 同一条 argv、同一份读码、同一个 MJPEG 源**：
        //
        // • 独立 .NET 9 进程（Debug 与 Release 都试过，连 stderr 排空与
        //   `Task.WhenAny` 都照抄）读到 **12.0–13.5 fps**，跑满 12 秒一次卡顿都没有；
        // • 同一个循环跑在 xunit/VSTest 宿主里只有 **5.5–6.0 fps**，
        //   循环本该跑 6 秒、实际跑了 11.1 秒。
        //
        // 慢的是宿主，不是产品 —— 而这条用例的职责是量产品。`.ConfigureAwait(false)`
        // 单独加过，**没用**（宿主不是靠同步上下文拖慢的），所以要整段挪出测试方法。
        var (elapsed, frames, ended, timedOut) = await Task.Run(async () =>
        {
            var buffer = new byte[FrameBytes];
            var stream = process.StandardOutput.BaseStream;

            var clock = Stopwatch.StartNew();
            var count = 0;
            var streamEnded = false;
            var readTimedOut = false;

            while (clock.Elapsed < seconds)
            {
                var read = ReadExactlyAsync(stream, buffer, FrameBytes);

                // ⚠️ 每次读都要有上限：源**不可达**（主机在、只是不回）时 ffmpeg 卡在
                // TCP 连接上，既不产出也不退出 —— 没有这一条，循环永远不退出。
                if (await Task.WhenAny(read, Task.Delay(ReadTimeout)).ConfigureAwait(false) != read)
                {
                    readTimedOut = true;
                    break;
                }

                if (!await read.ConfigureAwait(false))
                {
                    streamEnded = true;   // 流断了
                    break;
                }

                if (clock.Elapsed > warmup)
                {
                    count++;
                }
            }

            return (clock.Elapsed, count, streamEnded, readTimedOut);
        }).ConfigureAwait(false);

        // ⚠️ 收尾**必须排在下面那条断言之前**：断言一抛，后面这段就不执行了，
        // 而 ffmpeg 会**叼着管道继续跑**（超时那条路上它是卡在 TCP 连接里的，
        // `q` 也读不到）—— 2026-10-02 实测：断言在清理之前时，反证跑出来的那个
        // ffmpeg 成了孤儿，`dotnet test` 的管道迟迟不 EOF。
        //
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

        // ⚠️ 这一条是整条用例的守门人。
        //
        // 没有它的时候这条用例是**假绿**的：源连不上时 ffmpeg 当场退出，循环立刻
        // break，于是 `frames = 0` 而 `measured = 0.4s − 2s` **为负** ⇒
        // `frames >= expected * 0.75` 变成「0 >= 负数」——**恒真**。
        // 2026-10-02 就是这样：真源是 http，而被抄坏的 argv 带着 `-rtsp_transport`，
        // 一次都没跑起来，用例却照绿。
        Assert.False(
            ended || timedOut,
            timedOut
                ? $"读一帧超过 {ReadTimeout.TotalSeconds:0} 秒没回来 —— 源卡住了"
                  + $"（地址不可达时 ffmpeg 会一直重试连接，既不产出也不退出）。"
                  + $"ffmpeg 说：{errors.ToString().Trim()}"
                : $"流在 {elapsed.TotalSeconds:0.0} 秒就断了（要满 {seconds.TotalSeconds:0} 秒）——"
                  + $"源没起来，谈不上吞吐。ffmpeg 说：{errors.ToString().Trim()}");

        // ⚠️ 到这里 `ended` 与 `timedOut` 都是 false ⇒ 循环是**跑满** 6 秒才退的，
        // 所以这个减法必为正（守门人挡掉了它曾经为负的那条路）。
        var measured = elapsed - warmup;

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
            // ⚠️ 同上：`.ConfigureAwait(false)` 是这条用例能不能量到产品的前提。
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset))
                .ConfigureAwait(false);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
