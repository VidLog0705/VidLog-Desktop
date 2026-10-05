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
    /// <param name="segmentSeconds">
    /// 非 <see langword="null"/> 时**一个进程跑到底、由它自己按时长滚段**（T17）：
    /// <paramref name="outputPath"/> 因此是一个**模式**（如 <c>segment-%03d.mkv</c>），
    /// 单片秒数就是它。
    /// <para>
    /// ⚠️ 这个参数存在是要消灭**段边界那 1~2 秒的画面空洞**：按老的走法，到点要
    /// 「停 ffmpeg（释放相机）→ 重开 ffmpeg（重开相机）」，而开一次相机实测
    /// 1~1.5 秒（见 <c>FfmpegCameraCapture</c>），默认 1 分钟一段 ⇒ 一小时少 60~120 秒。
    /// </para>
    /// <para>
    /// ⚠️ 为 <see langword="null"/>（默认）时**行为与本次改动之前逐字一致**：
    /// 一个输出路径一个文件，段边界照旧靠停进程、重开进程。
    /// </para>
    /// </param>
    /// <param name="segmentStartNumber">
    /// 第一片从几号起（<c>-segment_start_number</c>）。**滚段时它不是可选项**：
    /// 采纳了预录缓冲时会话目录里**已经躺着** <c>segment-000.mkv</c>，而 ffmpeg
    /// 默认从 0 起、又带 <c>-y</c> —— 2026-10-05 本机实测，那会把开场那几秒
    /// **直接覆盖掉**，且不报任何错。
    /// </param>
    /// <returns>可等待、可优雅停止的采集进程。</returns>
    /// <remarks>
    /// ⚠️ <b>方向不在这里</b>：它在实现方构造时拿到的
    /// <see cref="Media.RecordingSpec"/> 里（<c>Rotation</c>）——
    /// 规格探测与录制共用那一份，所以两者连方向都必然一致。
    /// </remarks>
    Task<ICaptureProcess> StartAsync(
        CameraSource source,
        string outputPath,
        string encoder,
        string? microphone = null,
        CancellationToken cancellationToken = default,
        int? segmentSeconds = null,
        int segmentStartNumber = 0);
}
