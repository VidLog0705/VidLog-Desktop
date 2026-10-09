namespace VidLog.Desktop.Core.Camera;

/// <summary>一帧灰度画面（紧凑的 w×h 亮度，stride == width）。</summary>
/// <remarks>
/// ZXing 的 <c>Gray8</c> 正好吃这个形状，中间不需要任何图像库。
/// <para>
/// ⚠️ <b>它现在是从 C# 转出来的，不是 ffmpeg 直接给的</b>（2026-10-09 改的）：
/// 帧通路上只剩**一路彩色**输出（<see cref="PreviewFrame"/>，它同时是主窗那个取景框），
/// 灰度由 <see cref="PreviewFrame.ToGray"/> 在真要喂 ZXing 的那一帧上按需转 ——
/// 一个 ffmpeg 只有一条 stdout，多出一路灰度没有收益，代价却是整个取景框变灰。
/// </para>
/// <para>
/// 下面那个「单槽」与「按定长切裸帧」从前是本文件里的两个类
/// （<c>SingleSlotFrameSink</c> / <c>RawGrayFrameReader</c>），现在都由
/// <see cref="SingleSlotPreviewSink"/> 与 <see cref="PreviewProcess.ReadFramesAsync"/>
/// 一处承担 —— 同一件事（永不阻塞地及时读、切满一帧就换一帧）写两遍，
/// 就多一处「尺寸对不上」的静默花屏。
/// </para>
/// </remarks>
public sealed record CameraFrame(byte[] Gray, int Width, int Height, long CapturedAtMs);
