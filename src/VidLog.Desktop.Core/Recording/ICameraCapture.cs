namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 摄像头采集 —— 起一个进程，把某个视频设备录进一个文件。
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
    /// <param name="device">视频设备名，来自 <see cref="CameraDevices.ListAsync"/>。</param>
    /// <param name="outputPath">目标文件。调用方保证父目录已存在。</param>
    /// <param name="encoder">H.264 编码器名，来自编码探测的结果。</param>
    /// <returns>可等待、可优雅停止的采集进程。</returns>
    Task<ICaptureProcess> StartAsync(
        string device,
        string outputPath,
        string encoder,
        CancellationToken cancellationToken = default);
}
