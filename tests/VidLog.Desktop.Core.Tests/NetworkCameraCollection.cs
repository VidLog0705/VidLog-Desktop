namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 打**同一台真网络摄像头**（<c>VIDLOG_TEST_RTSP_URL</c>）的那些测试**串行跑**。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有这个集合</b>：这几条同时压两个共享资源 —— ①**那台手机自己**
/// （MJPEG 1080p 由它一个人推，几个连接一起拉就是它先撑不住）；
/// ②**这台机器的 CPU**（录制那两条在跑 1080p 的 x264 实时编码）。
/// 并行时它们互相抢，而失败的那一条往往**代码一行没错**。
/// </para>
/// <para>
/// ⚠️ 2026-10-02 实测踩到：`PreviewThroughputTests` 单独跑绿、混在全量里
/// 只读到 **28 帧 / 4.0 秒 = 7.0 fps**（目标 12，判据要 ≥ 9）——
/// 同一轮里 `NetworkCameraIntegrationTests` 的两条正在做 x264 编码。
/// 拿掉 .NET 读端、只让 ffmpeg 解码同样的 8 秒，它**一帧不差**（96 帧 / 7.61 秒），
/// 所以瓶颈不是读端、也不是解码，就是并行的 CPU 争用。
/// </para>
/// <para>
/// ⚠️ <b>这不是「把 flake 藏起来」，而是把那个争用从根上拿掉</b>：
/// 同一个时刻只有一个测试在压那台手机。与 <see cref="DshowDeviceCollection"/>
/// 是同一条理由、同一个手法（那边争的是独占的 DirectShow 设备，这边争的是
/// 网络源与 CPU）。
/// </para>
/// <para>
/// ⚠️ <c>PreviewProcessTests</c> **不在这个集合里**，尽管它也有两条真源用例：
/// 它同时还要开**本机 dshow 相机**，而那必须与 <c>CameraCaptureTests</c> 串行 ⇒
/// 它只能归属于 <see cref="DshowDeviceCollection"/>（一个类只能进一个集合）。
/// 它那两条真源用例是轻的（640×360、约 1 秒），不足以把别人拱红。
/// </para>
/// <para>
/// ⚠️ <b>这个集合治的是「抢资源」，治不了 <c>PreviewThroughputTests</c> 曾经那个红。</b>
/// 2026-10-02 试过 <c>DisableParallelization = true</c>（让这一族独占跑）：**没用** ——
/// 那条用例单独跑三次都是 **5.2 / 5.2 / 5.3 fps**，红得一模一样。
/// </para>
/// <para>
/// ⚠️ <b>那条红的真正原因后来查清了：不是源、不是 ffmpeg、不是 1080p MJPEG 那条路，
/// 是「读循环跑在哪儿」。</b> 用例原来把读循环直接写在测试方法里，于是它跑在
/// xunit 测试方法的那条线程上；而生产 <c>PreviewProcess</c> 是
/// <c>Task.Run(() =&gt; ReadAsync(...))</c> 起的读循环。同一台机、同一条 argv、
/// 同一份读码、同一个源，**独立 .NET 9 进程 12.0–13.5 fps，测试宿主 5.5–6.0 fps**。
/// 把读循环整段挪进 <c>Task.Run</c> 之后实测 11.2–12.2 fps，与独立进程一致。
/// 细节见 <c>PreviewThroughputTests</c> 里那段注释。
/// </para>
/// <para>
/// ⚠️ 顺带记一笔：当时据此写下的「瓶颈在 1080p MJPEG 那条路本身（原生管道
/// ffmpeg→ffmpeg 同样只有 ~6.7 fps）」**是错的**，已被上面那组对照证伪，别再引用。
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class NetworkCameraCollection
{
    public const string Name = "network-camera";
}
