using System.Globalization;
using System.Text.RegularExpressions;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 一次「测试连接」看到的东西。
/// </summary>
/// <param name="Connected">连上并解出了至少一帧。</param>
/// <param name="Width">对端视频的宽；<see langword="null"/> = 没读出来。</param>
/// <param name="Height">对端视频的高。</param>
/// <param name="VideoCodec">对端视频编码名（<c>h264</c> / <c>hevc</c> …）。</param>
/// <param name="HasAudio">对端自带音轨没有。</param>
/// <param name="FailureReason">没连上时**给用户看的一句中文**（I3：不许静默失败）。</param>
public sealed record NetworkStreamInfo(
    bool Connected,
    int? Width,
    int? Height,
    string? VideoCodec,
    bool HasAudio,
    string? FailureReason)
{
    public static NetworkStreamInfo Failed(string reason) => new(false, null, null, null, false, reason);

    /// <summary>对端的尺寸读出来没有。</summary>
    /// <remarks>
    /// ⚠️ 「连上了但读不出尺寸」是**正常的一种结果**，不是失败：
    /// 那意味着后面的分辨率判决**不能做**，而不是「这个摄像头不能用」。
    /// 混成一件的话，会把一个能用的摄像头挡在门外。
    /// </remarks>
    public bool SizeKnown => Width is > 0 && Height is > 0;
}

/// <summary>「测试连接」—— 打开一次这一路，看它对不对、以及它发多大。</summary>
public interface INetworkCameraProbe
{
    Task<NetworkStreamInfo> InspectAsync(CameraSource source, CancellationToken cancellationToken = default);
}

/// <summary>
/// 用 ffmpeg 打开一次这一路，从它的输出里读出对端的尺寸与编码。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么不用 ffprobe</b>：本仓的部署只保证有 `ffmpeg.exe` 一个二进制
/// （<see cref="FfmpegLocator"/> 找的就是它），而发布包里的 `tools\` 也只放它。
/// 为了读一个尺寸去要求「必须同时带 ffprobe.exe」，是把一个**可选功能**
/// 变成一道**部署门槛** —— 而那道门槛一漏，配置文件向导的这一步就整块用不了。
/// </para>
/// <para>
/// ⚠️ <b>所以这里要解析 ffmpeg 的 stderr</b>（见 <see cref="ParseStreams"/>）。
/// 那是本仓一贯避免的事（措辞会随版本变），代价用两件事兜住：
/// ① 只认那一行里的 `宽x高` 这个**形状**，不认任何措辞；
/// ② 读不出来就返回 <see cref="NetworkStreamInfo.SizeKnown"/> = false，
/// <b>不判失败</b> —— 探测的结论「不知道」与「不行」必须分开。
/// </para>
/// <para>
/// ⚠️ 它**只读不写**：`-f null -` 不落盘、不编码（只要解码），
/// 所以它不依赖任何编码器可用，也不占磁盘。
/// </para>
/// </remarks>
public sealed class FfmpegNetworkCameraProbe : INetworkCameraProbe
{
    /// <summary>最多看一帧就退出 —— 只是要它的流信息，不是要看画面。</summary>
    private const string FramesToRead = "1";

    private readonly string _ffmpegPath;
    private readonly IProcessRunner _runner;
    private readonly TimeSpan _timeout;

    /// <param name="timeout">
    /// 一次「测试连接」最多等多久；<see langword="null"/> = <see cref="DefaultTimeout"/>。
    /// 可注入是为了让「上界真的生效」能被验到（否则验一次要等满那个数）。
    /// </param>
    public FfmpegNetworkCameraProbe(
        string ffmpegPath, IProcessRunner runner, TimeSpan? timeout = null)
    {
        _ffmpegPath = ffmpegPath;
        _runner = runner;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// 一次尝试的上限。
    /// </summary>
    /// <remarks>
    /// ⚠️ **必须自己设上界**：2026-09-29 实测，连不上的 RTSP 地址会让 ffmpeg
    /// **静默挂住好几分钟**（等满 180 秒时 stderr 一个字都没有），
    /// 连「连接被拒」都不立刻放弃 —— 它会重试。
    /// 而这是用户**按了一个按钮**之后在等的东西，卡住比报错糟得多。
    /// </remarks>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(10);

    public async Task<NetworkStreamInfo> InspectAsync(
        CameraSource source, CancellationToken cancellationToken = default)
    {
        // 地址本身就没配好时**不开进程**：让 ffmpeg 去报 `Protocol not found`
        // 对用户没有指向性，而且白等一次超时。
        if (source.ConfigurationProblem is { } problem)
        {
            return NetworkStreamInfo.Failed(problem);
        }

        if (source.IsEmpty)
        {
            return NetworkStreamInfo.Failed("还没填网络摄像头地址。");
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            // ⚠️ 这里**不能**用 `-v error`：流的编码与尺寸是 ffmpeg 在
            // **info** 级别打出来的，压掉它就没得解析了。
            "-v", "info",
        };

        arguments.AddRange(source.InputArguments("64M"));
        arguments.AddRange(["-frames:v", FramesToRead, "-f", "null", "-"]);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_timeout);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_ffmpegPath, arguments, attempt.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消的（关窗 / 收尾）—— 那是**取消**，不是「连不上」。
            // 吞掉它会把一次正常收尾变成一条假警告。
            throw;
        }
        catch (OperationCanceledException)
        {
            return NetworkStreamInfo.Failed(
                $"等了 {_timeout.TotalSeconds:0} 秒也没有回应（地址或端口不对，或者对端不通）。");
        }
        catch (Exception ex)
        {
            return NetworkStreamInfo.Failed($"调用 FFmpeg 失败：{ex.Message}");
        }

        var streams = ParseStreams(result.StandardError);

        if (!streams.HasVideo)
        {
            // 连不上、或者连上了但没有视频那一路 —— 两种都没得录。
            // ⚠️ 判据取「有没有视频那一路」，**不是退出码**：ffmpeg 读够帧之后
            // 主动退出时退出码未必是 0，而那不代表失败。
            return NetworkStreamInfo.Failed(CameraErrorText.Describe(result.StandardError));
        }

        return new NetworkStreamInfo(
            Connected: true,
            streams.Width,
            streams.Height,
            streams.VideoCodec,
            streams.HasAudio,
            FailureReason: null);
    }

    /// <summary>从 ffmpeg 的输出里读出来的几件事。</summary>
    /// <param name="HasVideo">有没有视频那一路。</param>
    /// <param name="Width">宽；读不出来时是 <see langword="null"/>。</param>
    /// <param name="HasAudio">对端自带音轨没有。</param>
    /// <param name="FrameRate">
    /// 帧率（帧/秒）；读不出来时是 <see langword="null"/>。
    /// ⚠️ 与尺寸一样：**读不出来不判失败**，只是少印一个数字。
    /// </param>
    /// <param name="Duration">
    /// 整段的时长；读不出来时是 <see langword="null"/>。
    /// <para>
    /// ⚠️ 它是**容器级**的（`Duration:` 那一行不在 `Stream #` 里），
    /// 而且实时流（RTSP）上通常是 `Duration: N/A` —— 所以它**只对落盘的文件有意义**。
    /// 现在唯一的用处是「导入录像」量一遍外来文件的时长。
    /// </para>
    /// <para>
    /// ⚠️ 与尺寸、帧率同一条规矩：**读不出来就是 null，不猜**。
    /// 导入时按 0 记一条时长未知的录像，比编一个数字进证据元数据好得多。
    /// </para>
    /// </param>
    public sealed record ParsedStreams(
        bool HasVideo,
        string? VideoCodec,
        int? Width,
        int? Height,
        bool HasAudio,
        double? FrameRate = null,
        TimeSpan? Duration = null);

    /// <summary>
    /// 从 ffmpeg 的输出里挑出视频那一路的编码与尺寸，以及有没有音频。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 要认的就是这一行（2026-09-29 实测的那台网络摄像头）：
    /// <code>
    ///   Stream #0:0: Video: h264 (High), yuv420p(progressive), 1920x1080, 90k tbr, 90k tbn
    ///   Stream #0:1: Audio: aac (LC), 44100 Hz, mono, fltp, 64 kb/s
    /// </code>
    /// </para>
    /// <para>
    /// ⚠️ <b>只认「形如 `数字x数字`」这个形状，不认任何措辞</b> ——
    /// 措辞会随 ffmpeg 版本与编码器变，而 `1920x1080` 这个形状十几年来没变过。
    /// 所以即使以后 ffmpeg 改了说法，最坏也只是读不出尺寸（<c>SizeKnown=false</c>），
    /// 而不是认出一个**错的**尺寸。
    /// </para>
    /// <para>
    /// ⚠️ 取**第一路**视频：多路（主码流 + 子码流）时第一路是主码流，
    /// 也正是默认会录的那一路。
    /// </para>
    /// <para>
    /// `public` 是为了能被单独断言到 —— 本机只有一台真摄像头（还常常不在），
    /// 解析这一半拆不开就没法验（与 `FfmpegCameraCapture.BuildArguments` 同一理由）。
    /// </para>
    /// </remarks>
    public static ParsedStreams ParseStreams(string output)
    {
        var hasVideo = false;
        var hasAudio = false;
        string? codec = null;
        int? width = null;
        int? height = null;
        double? frameRate = null;
        TimeSpan? duration = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

            // 时长那一行**不在 `Stream #` 里**（它是容器级的），所以要在下面那个
            // continue 之前先看它：
            //   Duration: 00:01:23.45, start: 0.000000, bitrate: 1234 kb/s
            // ⚠️ 实时流印的是 `Duration: N/A`，那时正则不匹配 ⇒ 保持 null。
            if (duration is null
                && DurationPattern.Match(line) is { Success: true } found
                && TimeSpan.TryParse(found.Groups[1].Value, CultureInfo.InvariantCulture, out var span)
                && span > TimeSpan.Zero)
            {
                duration = span;
            }

            if (!line.StartsWith("Stream #", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("Audio:", StringComparison.Ordinal))
            {
                hasAudio = true;
                continue;
            }

            // 只要第一路视频 —— 后面的（子码流）忽略。
            if (hasVideo || !line.Contains("Video:", StringComparison.Ordinal))
            {
                continue;
            }

            hasVideo = true;

            var size = SizePattern.Match(line);
            if (size.Success)
            {
                width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
                height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
            }

            var match = CodecPattern.Match(line);
            if (match.Success)
            {
                codec = match.Groups[1].Value;
            }

            // 帧率：`640x480 …, 30 fps, 30 tbr` 里的那个 `30 fps`（2026-09-30 实测，
            // 读回的 MKV 与 dshow 输入两种行都是这个形状）。
            var rate = FrameRatePattern.Match(line);
            if (rate.Success
                && double.TryParse(
                    rate.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && parsed is > 0 and < MaxPlausibleFrameRate)
            {
                frameRate = parsed;
            }
        }

        return new ParsedStreams(hasVideo, codec, width, height, hasAudio, frameRate, duration);
    }

    /// <summary>帧率的合理上界 —— 超过它的数一定是认错了东西，不是真相机在拍的。</summary>
    private const double MaxPlausibleFrameRate = 1000;

    /// <summary>`1920x1080` 这种形状。两侧的前后视防的是把别的数字对认成尺寸。</summary>
    private static readonly Regex SizePattern = new(
        @"(?<![\d])(\d{2,5})x(\d{2,5})(?![\d])", RegexOptions.Compiled);

    /// <summary>`Video: h264` 里那个编码名。</summary>
    private static readonly Regex CodecPattern = new(
        @"Video:\s*([A-Za-z0-9_]+)", RegexOptions.Compiled);

    /// <summary>
    /// `30 fps` / `29.97 fps` 这种形状（帧率）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与尺寸同一条规矩：**只认这个形状，不认任何措辞**。
    /// 前视防的是把 `1k tbn` 之类的邻近字段认成帧率；
    /// 后视防的是把 `fps` 当别的词的尾巴（例如 `avgfps`）。
    /// 认不出来就是 null，**不猜** —— 印一个猜的帧率与印一个没测过的分辨率是同一件事。
    /// </remarks>
    private static readonly Regex FrameRatePattern = new(
        @"(?<![\d.])(\d+(?:\.\d+)?)\s*fps(?![\w])", RegexOptions.Compiled);

    /// <summary>
    /// `Duration: 00:01:23.45` 里那个时间（`Duration: N/A` 不匹配 ⇒ 读不出来）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与上面两条同一条规矩：**只认这个形状，不认措辞**。
    /// 小时位不钉位数（十几小时的录像会印成 `12:34:56.78` 一样的两段，
    /// 但理论上可以更长），秒位带可选小数。
    /// </remarks>
    private static readonly Regex DurationPattern = new(
        @"Duration:\s*(\d+:\d{2}:\d{2}(?:\.\d+)?)", RegexOptions.Compiled);
}
