namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 摄像头采集 —— 起一个进程，把某个视频设备（可选地连同一路麦克风）录进一个文件。
/// </summary>
/// <remarks>
/// 抽象成接口是为了让 <see cref="RecordingSession"/> 的编排逻辑
/// （分段滚动、manifest 写入、收尾衔接）能在**没有摄像头**的机器上测到。
/// 真机那一半由 <see cref="FfmpegCameraCapture"/> 承担，
/// 另有 <c>RequiresCameraFact</c> 守着的集成测试兜底。
/// </remarks>
public interface ICameraCapture
{
    /// <summary>
    /// 开始往 <paramref name="outputPath"/> 录。
    /// </summary>
    /// <param name="source">
    /// 画面从哪来（本机设备或网络地址）。本机设备名来自
    /// <see cref="DshowDevices.ListVideoAsync"/>。
    /// </param>
    /// <param name="outputPath">目标文件。调用方保证父目录已存在。</param>
    /// <param name="encoder">H.264 编码器名，来自编码探测的结果。</param>
    /// <param name="microphone">
    /// 麦克风设备名（规格 §3.1.8），来自 <see cref="DshowDevices.ListAudioAsync"/>。
    /// <see langword="null"/> / 空 = 这一段不录声音。
    /// <para>
    /// ⚠️ <b>麦克风接不上绝不能把这一段录像弄失败</b>（I4）：实现方必须
    /// 降级成「没有音轨的那一段」，并把这件事写进
    /// <see cref="ICaptureProcess.StartupWarning"/>，**不得**让它变成异常或空段。
    /// </para>
    /// </param>
    /// <returns>可等待、可优雅停止的采集进程。</returns>
    Task<ICaptureProcess> StartAsync(
        CameraSource source,
        string outputPath,
        string encoder,
        string? microphone = null,
        CancellationToken cancellationToken = default);
}
