using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 画面源（规格 §3.1.7 的设备维）：本机 DirectShow 设备，或网络摄像头地址。
/// </summary>
/// <remarks>
/// ⚠️ 本机**一台 dshow 设备都没有**（实测 2026-09-29），所以这里验的是
/// 「参数拼得对不对、身份里有没有把密码漏出去」，
/// **不是**「RTSP 真连得上」——后者只有真机知道，如实记在文档里。
/// </remarks>
public class CameraSourceTests
{
    // ─────────────────────────────────────────────
    // 两种源的输入参数
    // ─────────────────────────────────────────────

    [Fact]
    public void 本机设备的输入参数与改动前逐字一致()
    {
        // ⚠️ 这条是「没配网络摄像头的人感觉不到这次改动」的绊线。
        var arguments = CameraSource.Local("Lenovo EasyCamera").InputArguments("256M", "1920x1080");

        Assert.Equal(
            ["-f", "dshow", "-rtbufsize", "256M",
             "-video_size", "1920x1080", "-framerate", "30",
             "-i", "video=Lenovo EasyCamera"],
            arguments);
    }

    [Fact]
    public void 网络地址一个dshow选项都不带()
    {
        // ⚠️ `-video_size` / `-framerate` 对 rtsp 解复用器来说是**不存在的选项** ——
        // 写上它 ffmpeg 直接报 `Option not found`、起都起不来
        // （2026-09-29 实测过输入侧选项会被校验：见 实现决策 §65）。
        var arguments = CameraSource.Network("rtsp://192.168.1.64:554/stream")
            .InputArguments("256M", "1920x1080");

        Assert.Equal(["-rtsp_transport", "tcp", "-i", "rtsp://192.168.1.64:554/stream"], arguments);

        // 尺寸那一路改在输出侧做，所以这里必须一个都不带（连传进来的 1920x1080 也不要）。
        Assert.DoesNotContain("-video_size", arguments);
        Assert.DoesNotContain("-framerate", arguments);
        Assert.DoesNotContain("dshow", arguments);
    }

    [Fact]
    public void http源带墙钟打戳但不带rtsp选项()
    {
        // ⚠️ `-rtsp_transport` 是 **rtsp 解复用器的私有选项**，不是「网络源」的公共选项：
        // 给 http 源加上它，ffmpeg 直接报 `Option rtsp_transport not found` ——
        // **起都起不来**（2026-09-30 真机实测：一台手机 IP 摄像头是 `mpjpeg` over HTTP，
        // 录制退出码非 0、「测试连接」同样失败）。
        //
        // ⚠️ 这条尤其要守：`ConfigurationProblem` **本来就收 `http://`**
        // ⇒ 在修之前，界面说「地址没问题」、而一录就起不来。
        var arguments = CameraSource.Network("http://192.168.101.66:8081")
            .InputArguments("256M", "1920x1080")
            .ToList();

        // ★ 2026-10-05 起 http 那一路多一个**输入侧**选项：MJPEG 这类流不带时间戳，
        // 不按墙钟打戳的话，ffmpeg 会按 mpjpeg 解复用器写死的 25fps 发时间戳 ——
        // 而真源实测是 28~32fps ⇒ 录出来的东西慢动作、比事件长、水印时钟偏快。
        Assert.Equal(
            ["-use_wallclock_as_timestamps", "1", "-i", "http://192.168.101.66:8081"],
            arguments);

        // ⚠️ 顺序是承重的：它是**输入**选项，落在 `-i` 之后会被当成输出选项静默失效。
        Assert.True(arguments.IndexOf("-i") > arguments.IndexOf("-use_wallclock_as_timestamps"));

        // dshow 那几个选项对 http 同样不存在（与 RTSP 那条同一顶帽子）。
        Assert.DoesNotContain("-video_size", arguments);
        Assert.DoesNotContain("-framerate", arguments);

        // 而 rtsps（TLS 那一路）**仍然是 RTSP 系** —— 它认这个选项，别一起误伤。
        Assert.Contains(
            "-rtsp_transport",
            CameraSource.Network("rtsps://h/s").InputArguments("256M"));
    }

    [Fact]
    public void rtsp源不带墙钟打戳()
    {
        // ⚠️ RTSP 流**自带真时间戳**，没有 MJPEG 那个病 ——
        // 给它加 `-use_wallclock_as_timestamps 1` 是多余的，而且它在 rtsp 解复用器上
        // 的行为**没验过**（局域网里那台 RTSP 真源已不在网里）。
        // 这条钉住「别顺手改成两只都加」。
        var arguments = CameraSource.Network("rtsp://h/s").InputArguments("256M");

        Assert.DoesNotContain("-use_wallclock_as_timestamps", arguments);
        Assert.Contains("-rtsp_transport", arguments);
    }

    [Fact]
    public void 网络地址走tcp不走udp()
    {
        // UDP 在很多现场网络里被防火墙丢掉，表现是「偶尔能连、多数连不上」——
        // 那种故障极难排查。工位上要的是稳。
        var arguments = CameraSource.Network("rtsp://h/s").InputArguments("256M").ToList();

        Assert.Equal("tcp", arguments[arguments.IndexOf("-rtsp_transport") + 1]);
    }

    // ─────────────────────────────────────────────
    // ★ 身份（进索引 / manifest / 日志的那一份）
    // ─────────────────────────────────────────────

    [Fact]
    public void 身份里不能有凭据()
    {
        // ⚠️ 这条是这一节最要紧的一条：Identity 会被写进 `session.json` 与检索索引
        // （`entries.jsonl`，用户要长期留着、还要交付出去的证据元数据）。
        // 漏出去一次，摄像头密码就刻进了每一份档案。
        var source = CameraSource.Network("rtsp://admin:hunter2@192.168.1.64:554/stream");

        Assert.Equal("rtsp://192.168.1.64:554/stream", source.Identity);

        // 而给 ffmpeg 用的那一份**必须**还带着凭据，否则根本连不上。
        Assert.Equal("rtsp://admin:hunter2@192.168.1.64:554/stream", source.Address);
    }

    [Fact]
    public void 路径里的at号不算凭据分隔符()
    {
        // 只处理**主机之前**那一段里的 `@`：把路径里的也当分隔符会把路径切掉、
        // 于是身份指向另一个地址。
        Assert.Equal(
            "rtsp://host/path@v2",
            CameraSource.Redact("rtsp://host/path@v2"));
    }

    [Fact]
    public void 没有凭据的地址原样返回()
    {
        Assert.Equal("rtsp://192.168.1.64:554/s", CameraSource.Redact("rtsp://192.168.1.64:554/s"));
        Assert.Equal(string.Empty, CameraSource.Redact(string.Empty));

        // 不是 URL 形状（手打了一半）时也**不许抛** —— 一个用来隐藏密码的函数
        // 因为「地址打错了」而抛出去，调用方多半连那半截地址一起写进日志。
        Assert.Equal("192.168.1.64", CameraSource.Redact("192.168.1.64"));
        Assert.Equal("rtsp://", CameraSource.Redact("rtsp://"));
    }

    [Fact]
    public void 本机设备的身份就是设备名()
    {
        Assert.Equal("Lenovo EasyCamera", CameraSource.Local("Lenovo EasyCamera").Identity);
        Assert.Equal("Lenovo EasyCamera", CameraSource.Local("Lenovo EasyCamera").Display);
    }

    [Fact]
    public void 界面上要能看出这是网络摄像头()
    {
        // 用户同时接着本机摄像头与 IP 摄像头时，光看一个 IP 认不出来它是哪一路。
        var display = CameraSource.Network("rtsp://admin:pw@10.0.0.9:554/s").Display;

        Assert.Contains("网络摄像头", display);
        Assert.Contains("10.0.0.9", display);
        Assert.DoesNotContain("pw", display);
    }

    // ─────────────────────────────────────────────
    // 从设置构造
    // ─────────────────────────────────────────────

    [Fact]
    public void 老设置里没有这个字段时仍然是本机设备()
    {
        // ⚠️ 升级之后所有人的摄像头都不能变成「网络摄像头、地址为空」。
        // `default(CameraSourceKind)` 就是 `Local`，而这一条把它钉住。
        Assert.Equal(CameraSourceKind.Local, default);

        var source = CameraSource.FromConfig(default, "Lenovo EasyCamera", null);

        Assert.False(source.IsNetwork);
        Assert.Equal("Lenovo EasyCamera", source.Address);
    }

    [Fact]
    public void 认不出的档位回落到本机设备()
    {
        var source = CameraSource.FromConfig((CameraSourceKind)99, "Cam", "rtsp://h/s");

        Assert.False(source.IsNetwork);
        Assert.Equal("Cam", source.Address);
    }

    [Fact]
    public void 选了网络摄像头但地址空着时不回落到本机设备()
    {
        // ⚠️ 回落的后果是「用户明明选了网络摄像头，录出来的却是本机那台」，
        // 而画面上不会有任何迹象。空就是空，界面按「还没配好」处理。
        var source = CameraSource.FromConfig(CameraSourceKind.Network, "Lenovo EasyCamera", "   ");

        Assert.True(source.IsNetwork);
        Assert.True(source.IsEmpty);
        Assert.NotNull(source.ConfigurationProblem);
    }

    [Fact]
    public void 地址要能直接说清是哪里配错了()
    {
        Assert.Null(CameraSource.Network("rtsp://192.168.1.64:554/s").ConfigurationProblem);

        // 空地址
        Assert.NotNull(CameraSource.Network("").ConfigurationProblem);

        // 少了协议 —— ffmpeg 会报 `Protocol not found`，那句对用户毫无指向性，
        // 所以在这一层就说清楚。
        var problem = CameraSource.Network("192.168.1.64:554/s").ConfigurationProblem;
        Assert.NotNull(problem);
        Assert.Contains("rtsp://", problem);
    }

    // ─────────────────────────────────────────────
    // 网络那一档在录制 argv 里的落点
    // ─────────────────────────────────────────────

    [Fact]
    public void 网络摄像头的尺寸走输出侧缩放_帧率补在输出侧()
    {
        var spec = new RecordingSpec(VideoCodec.H264, VideoResolution.P1080);
        var args = FfmpegCameraCapture.BuildArguments(
            CameraSource.Network("rtsp://h/s"), @"C:\out\seg.mkv", "libx264", spec).ToList();

        // 输入侧一个尺寸都没有（RTSP 不认）。
        var input = args.IndexOf("-i");
        Assert.DoesNotContain("-video_size", args);

        // 输出侧缩放到用户选的那一档。
        var filters = args[args.IndexOf("-vf") + 1];
        Assert.Equal($"scale={spec.FfmpegSize}", filters);

        // ⚠️ 「帧率固定 30」在网络那一路只能在输出侧钉 —— 输入侧没有可用的选项。
        Assert.True(input < args.IndexOf("-r"), "-r 是输出侧选项，必须在 -i 之后");
        Assert.Equal("30", args[args.IndexOf("-r") + 1]);
    }

    [Fact]
    public void 网络摄像头的水印必须排在缩放之后()
    {
        // ⚠️ 顺序是承重的：水印是按**最终**分辨率排版烧上去的
        // （AssWatermark.Build 拿 width/height 算字号与位置）。
        // 先烧后缩会把字的位置和大小一起缩歪 —— 而画面上只是「水印看着怪怪的」。
        var args = FfmpegCameraCapture.BuildArguments(
            CameraSource.Network("rtsp://h/s"), @"C:\out\seg.mkv", "libx264",
            new RecordingSpec(VideoCodec.H264, VideoResolution.P1080),
            watermarkAssPath: @"C:\work\seg.ass").ToList();

        var filters = args[args.IndexOf("-vf") + 1];
        var scale = filters.IndexOf("scale=", StringComparison.Ordinal);
        var ass = filters.IndexOf("ass=", StringComparison.Ordinal);

        Assert.True(scale >= 0 && ass > scale, $"缩放必须排在 ass 之前，实际是：{filters}");
        // 只有一条 -vf：写两个的话后一个会顶掉前一个，而且不报错。
        Assert.Equal(1, args.Count(a => a == "-vf"));
    }

    [Fact]
    public void 本机设备不会多出缩放那一层()
    {
        // 本机设备的尺寸已经在输入侧钉好了，再缩一次是白花 CPU（还可能再糊一层）。
        var args = FfmpegCameraCapture.BuildArguments(
            CameraSource.Local("Cam"), @"C:\out\seg.mkv", "libx264",
            new RecordingSpec(VideoCodec.H264, VideoResolution.P1080)).ToList();

        Assert.DoesNotContain("-vf", args);
        Assert.DoesNotContain("-r", args);
    }

    [Fact]
    public async Task 探测跑的就是录制那一份argv_网络那一档也一样()
    {
        // 与 RecordingSpecTests 里那条同源，但源换成网络地址 ——
        // 共享构造器这个不变量必须对两只都成立。
        var runner = new ProbingRunner();
        var probe = new FfmpegSpecProbe("ffmpeg.exe", runner);
        var spec = new RecordingSpec(VideoCodec.H264, VideoResolution.P1080);
        var source = CameraSource.Network("rtsp://h/s");

        await probe.ProbeAsync(spec, source);

        Assert.NotEmpty(runner.Invocations);

        var arguments = runner.Invocations[0];
        var probeFile = arguments[arguments.IndexOf("-y") + 1];

        var recording = FfmpegCameraCapture.BuildArguments(
            source, probeFile, spec.EncoderCandidates[0], spec, durationSeconds: 1);

        Assert.Equal(recording.ToArray(), arguments.ToArray());
    }

    [Fact]
    public async Task 地址没配好时不去开进程_直接说清楚()
    {
        // 让 ffmpeg 去报 `Protocol not found` 对用户没有指向性，
        // 而且白起一次进程（RTSP 的失败还可能等很久）。
        var runner = new ProbingRunner();
        var probe = new FfmpegSpecProbe("ffmpeg.exe", runner);

        var result = await probe.ProbeAsync(
            RecordingSpec.Default, CameraSource.Network("192.168.1.64:554/s"));

        Assert.False(result.Usable);
        Assert.Contains("rtsp://", result.FailureReason);
        Assert.Empty(runner.Invocations);
    }

    /// <summary>
    /// 「连着但不说话」的摄像头必须**在上界内**被判掉。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 这条不是形式主义。2026-09-29 实测：一个连不上的 RTSP 地址会让 ffmpeg
    /// **静默挂住好几分钟**（>180 秒时 stderr 一个字都没有、进程还活着），
    /// 而这条探测在**启动路径**上 —— 不设上界就是「开机卡住」，
    /// 而用户看到的只是「点了没反应」。
    /// </para>
    /// <para>
    /// ⚠️ 用**自己开的一个只收不答的监听**，不用 `192.0.2.1` 那种不可路由地址：
    /// 后者的行为取决于本机路由表（有的环境里立刻 `Network unreachable`，
    /// 那条路走的是「退出码非 0」而不是超时，判据就飘了）。
    /// 监听这一版是确定的：TCP 握手成功、ffmpeg 等 RTSP 响应、**必然挂住**。
    /// </para>
    /// <para>上界注入成 2 秒，所以这条用例只花两秒左右。</para>
    /// </remarks>
    [RequiresFfmpegFact]
    public async Task 连着但不说话的摄像头在上界内被判掉()
    {
        using var deaf = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        deaf.Start();
        var port = ((System.Net.IPEndPoint)deaf.LocalEndpoint).Port;

        var probe = new FfmpegSpecProbe(
            FfmpegLocator.TryFind()!, new SystemProcessRunner(),
            networkTimeout: TimeSpan.FromSeconds(2));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await probe.ProbeAsync(
            RecordingSpec.Default, CameraSource.Network($"rtsp://127.0.0.1:{port}/stream"));
        clock.Stop();

        Assert.False(result.Usable);
        Assert.Contains("没能连上", result.FailureReason);

        // ★ 2026-10-02：超时必须被归成「源打不开」——那是**真的 ffmpeg**
        // 在这一条路上报出来的东西，所以它同时是那条分类的端到端证据
        // （假 runner 那几条只能证明代码怎么走，证明不了 ffmpeg 真会说这句）。
        Assert.True(result.SourceUnavailable);

        // 上界生效的**证据**：明显早于「挂满好几分钟」。
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(30),
            $"上界没生效，等了 {clock.Elapsed.TotalSeconds:0} 秒");
    }
}
