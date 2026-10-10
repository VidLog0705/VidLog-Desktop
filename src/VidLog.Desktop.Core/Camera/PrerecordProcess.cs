using System.Diagnostics;
using System.Text;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Camera;

/// <summary>
/// **待扫期间持有相机的那个进程**：出一路**彩色**帧（主窗取景给人看，
/// 降频转灰度喂 ZXing），开了预录缓冲时**同时**写一路滚动分片。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>它存在的唯一理由是「相机是独占的」</b>（§25 真机实测 <c>device already in use</c>）：
/// 待扫期间相机就只能有一个占用者，所以「取景要的那一路帧」与「预录要的那一路文件」
/// 必须是**同一个进程的两路输出**，不能是第二个进程（§54 一路源两路输出的形状）。
/// </para>
/// <para>
/// 缓冲档位是「关闭」时它退化成纯取景进程（<see cref="ScannerArguments"/>：不编码、
/// 不写文件，越便宜越好）—— 名字里的预录说的是它**能**做的另一件事，不是每次都在做。
/// </para>
/// <para>
/// 帧走 <b>stdout</b>，所以写文件那一路与它**不能共用**：一路输出一条管道，
/// 写文件那一路另有自己的输出（见 <see cref="Recording.FfmpegCameraCapture.BuildArguments"/>
/// 的 <c>frameTap</c>）。stdout 的读循环**不接受可取消的读**（理由同采集：读端一停
/// 管道就满，会把 ffmpeg 顶住 —— 它连 stdin 上的 <c>q</c> 都处理不了，只能强杀，
/// 而强杀会丢掉分片的尾部）。停机靠送 <c>q</c> 让进程退出，管道自然断。
/// </para>
/// </remarks>
public sealed class PrerecordProcess
{
    /// <summary>取景的画面尺寸。</summary>
    /// <remarks>
    /// 640x480 是本机摄像头的能力上限（实测只有 3 档：160x120 / 320x240 / 640x480）。
    /// 取最大是因为小面单上的条码在小尺寸下读不出来。
    /// <para>
    /// ⚠️ <b>它比录制那一路的预览（640×360）高</b>：这一路同时是**识码**用的帧，
    /// 而降到一个 16:9 的框里会让条码线性缩到 75%。取景框那两种尺寸
    /// <c>MainWindow.RefreshPreview</c> 本来就都认（位图跟着帧的尺寸重建）。
    /// </para>
    /// </remarks>
    public const int Width = 640;

    public const int Height = 480;

    /// <summary>
    /// 取帧频率。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>从前是 3</b>（「识码不需要流畅画面」）。2026-10-09 改成 12：这一路
    /// **同时是主窗那个取景框**，而 3 fps 在屏幕上就是一顿一顿的，加上它从前还只出灰度
    /// （ffmpeg 直接出 <c>gray</c>）——现场看到的是「取景画面又灰又卡」。
    /// </para>
    /// <para>
    /// ⚠️ <b>提高到 12 不会让 ZXing 忙四倍</b>：喂给它的仍然是**每第 3 帧**
    /// （见 <c>PrerecordController.DecodeEveryNthFrame</c>），也就是 4 次/秒 ——
    /// 与从前的 3 次/秒同量级。人眼看流畅大致从 10 fps 起，12 是
    /// <see cref="PreviewProcess.Fps"/> 那一档早就定下的同一个取舍。
    /// </para>
    /// </remarks>
    public const int Fps = 12;

    private readonly Process _process;
    private readonly Task _readLoop;
    private readonly BoundedTextTail _errors;

    private PrerecordProcess(Process process, Task readLoop, BoundedTextTail errors)
    {
        _process = process;
        _readLoop = readLoop;
        _errors = errors;
    }

    /// <summary>ffmpeg 说的最后一句话（进程死掉时用它给用户一个原因，I3）。</summary>
    public string ErrorTail => _errors.ToString();

    /// <summary>
    /// 起一个待扫进程。
    /// </summary>
    /// <param name="arguments">
    /// 命令行。**由调用方给**：两种形状（纯识码 / 带预录）都在
    /// <see cref="ScannerArguments"/> 与
    /// <see cref="Recording.FfmpegCameraCapture.BuildArguments"/> 里拼好 ——
    /// 本类只管「起、读、停」，不猜自己要跑什么。
    /// </param>
    /// <param name="sink">帧的落点（主窗取景与识码共用一帧，见 <see cref="PrerecordController"/>）。</param>
    /// <param name="logger">
    /// 只交给**读帧循环**用：它退出时（正常关闭 / 中途中断）记一条带帧数的日志。
    /// <b>不是</b>本类自己记日志 —— 进程死没死那件事仍由
    /// <see cref="PrerecordController"/> 在停它的时候记（见下面那条注意事项）。
    /// </param>
    /// <remarks>
    /// ⚠️ <b>本类不持有 logger</b>：它说过的那句话留在 <see cref="ErrorTail"/> 上，
    /// 由持有它的 <see cref="PrerecordController"/> 在停它的时候记。
    /// 原来两边各记一条**同样的话**（`AGENTS.md` §6.1 收尾审计查出来的）——
    /// 同一次死亡在日志里出现两遍，只会让人以为死了两次。
    /// <para>
    /// <paramref name="logger"/> 那条与上面那条**不是同一件事**：它说的是
    /// 「读帧循环什么时候停的、读到过几帧」，而进程死没死是另一句 ——
    /// 2026-10-10 的预览黑屏正是「进程活着、读循环却停了」，那条只能由读循环自己说。
    /// </para>
    /// </remarks>
    /// <param name="sourceSize">
    /// 输入侧的真实尺寸；**未知就留 null**。给了就在每一帧上标出画面那一块
    /// （见 <see cref="PreviewFrame.Picture"/>）—— 这一路同时是识码用的帧，
    /// 识别框的比例靠它才算得对。
    /// </param>
    public static Task<PrerecordProcess> StartAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        SingleSlotPreviewSink sink,
        CancellationToken cancellationToken = default,
        IAppLogger? logger = null,
        (int Width, int Height)? sourceSize = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 承重：不重定向 stdin 就没法用 q 优雅停止（见 FfmpegCaptureProcess 的说明）。
            RedirectStandardInput = true,

            // ⚠️ 承重：ffmpeg 写的是 UTF-8，不声明就按控制台码页（中文 Windows = 936）读
            // —— stderr 是「设备被占用」那类判定的唯一素材。stdout 那一路是**裸帧**，
            // 走 BaseStream，与编码无关，所以只声明这一条。
            // 见 `DshowDevices.ListAllAsync` 的实测记录。
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();

        // stderr 必须排空，否则一次刷屏就能把管道灌满、把进程顶住。
        //
        // ⚠️ **而且内容要留下来**（2026-09-29 审计查出来的缺口）：原来这里是
        // `ReadToEndAsync()` 把结果**丢掉**，于是识码进程起来之后死掉
        // （`device in use`、地址打不开）时，`_process` 不动声色地跑完，
        // `PrerecordController` 只看到「没有新帧」—— **日志与 `Failed` 事件都不响**。
        // 它的两个兄弟（`PreviewProcess` / `MicrophoneLevelMonitor`）都是留尾部的，
        // 只有它没有。
        var errors = new BoundedTextTail();
        _ = Task.Run(() => DrainAsync(process.StandardError, errors));

        // ⚠️ 读法与预览、采集三处**同一份**（`PreviewProcess.ReadFramesAsync`）——
        // 定长切裸帧这件事多写一遍就多一处「尺寸对不上」的静默花屏。
        var readLoop = Task.Run(
            () => PreviewProcess.ReadFramesAsync(
                process.StandardOutput.BaseStream, sink, Width, Height, logger,
                sourceSize: sourceSize));

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new PrerecordProcess(process, readLoop, errors));
    }

    /// <summary>
    /// 纯取景那一档的命令（**不写文件**）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与录制那条路的差别：不编码、不写文件，直接出**彩色裸帧**。
    /// <b>几何必须钉住</b> —— 裸帧没有容器告诉读端宽高，
    /// 所以 <see cref="Width"/>×<see cref="Height"/> 是**读端**的硬前提。
    /// </para>
    /// <para>
    /// ⚠️ <b>网络摄像头那一档必须在输出侧处理</b>：RTSP 没法要求对端按 640×480 发流
    /// （<c>-video_size</c> 对 rtsp 解复用器来说是不存在的选项），
    /// 而对端多半是 1080P/4K —— 不处理的话读端按 640×480 去切一路 1920×1080 的裸帧，
    /// 切出来的是**错位的花屏**，识码永远认不出来（而且不会有任何报错）。
    /// 见 <see cref="FrameFilters"/>。
    /// </para>
    /// </remarks>
    /// <param name="rotation">方向（规格 §3.1.7）。必须与录制那一档一致。</param>
    public static IReadOnlyList<string> ScannerArguments(
        CameraSource source, CameraRotation rotation = CameraRotation.None)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
        };

        // 输入参数由源自己给：本机设备带 `-video_size 640x480 -framerate 30`，
        // 网络地址一个都不带（见 CameraSource.InputArguments）。
        arguments.AddRange(source.InputArguments("64M", $"{Width}x{Height}"));

        arguments.AddRange(
        [
            "-vf", string.Join(',', FrameFilters(rotation)),
            "-pix_fmt", "rgb24",
            "-f", "rawvideo",
            "pipe:1",
        ]);

        return arguments;
    }

    /// <summary>
    /// 取景那一档的**滤镜链**：转完之后正好是 <see cref="Width"/>×<see cref="Height"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>抽成公开的纯函数是因为它有第二个调用方</b>：预录进程的第二路输出
    /// （<c>FfmpegCameraCapture.BuildArguments</c> 的 <c>frameTap</c>）。
    /// 两处各写一份的话，同一档取景在「预录开着」与「预录关着」时会收到**两种尺寸**的帧
    /// —— 而读端按定长切裸帧，尺寸不对就是错位的花屏，且不报任何错。
    /// </para>
    /// <para>
    /// ⚠️ <b>几何那两环（缩、补黑边）由 <see cref="PreviewProcess.PreviewFilters"/>
    /// 一处产出</b>（§2026-10-09）：这里只是把它按**取景要的尺寸**（640×480）要一遍，
    /// 再缀上帧率与像素格式。从前的写法是「先 <c>scale</c> 到 640×480 再转方向」，
    /// 而那个 <c>scale</c> 对 16:9 的源是**拉伸**（画面被压扁）——从前它是灰的、
    /// 3 fps 的，看不出来；现在这一路就是主窗那个取景框，变形一眼就看得出来。
    /// 现在与预览同一套：**先转方向，再按比例缩进框里，四周补黑边**。
    /// </para>
    /// <para>
    /// ⚠️ 方向滤镜的产出**只有一处**（<see cref="CameraRotationFilters.For"/>）——
    /// 与录制那一档用的是同一个函数，所以两边朝向不可能不一致。
    /// <c>fps</c>/<c>format</c> 排在它之后：那两步不改变几何。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> FrameFilters(CameraRotation rotation)
    {
        var filters = new List<string>(PreviewProcess.PreviewFilters(rotation, Width, Height));

        filters.Add($"fps={Fps},format=rgb24");

        return filters;
    }

    /// <summary>
    /// 停下并**等到进程真的退出**。
    /// </summary>
    /// <remarks>
    /// 等它退干净是必须的：相机独占，没释放干净的话紧接着的录制进程
    /// 会拿到 <c>device already in use</c>。
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> = 没能优雅停下、**强杀**了。
    /// 这件事必须往上报：强杀的代价是 MKV 的尾部丢掉（§54.2 —— 收尾靠 muxer 写索引，
    /// 而 `Kill` 之后没有收尾），也就是说**刚滚过去的那一片可能缺一点尾巴**。
    /// 记在调用方那边，因为 logger 只有一个出口（见 <see cref="StartAsync"/> 的说明）。
    /// </returns>
    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        var forced = false;

        try
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync("q");
                await _process.StandardInput.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // stdin 已经不通了 —— 走下面的强杀。
            forced = true;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            forced = true;

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }

        // 读循环会在管道断开后自己结束。
        await Task.WhenAny(_readLoop, Task.Delay(TimeSpan.FromSeconds(2)));

        _process.Dispose();

        return forced;
    }

    /// <summary>把管道读干，尾部留下（照 <see cref="PreviewProcess"/> 的兄弟做法）。</summary>
    private static async Task DrainAsync(StreamReader reader, BoundedTextTail sink)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                sink.Append(buffer, read);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 进程退了就是结束，不是错误。
        }
    }
}
