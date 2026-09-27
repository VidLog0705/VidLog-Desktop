using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Media;

/// <summary>
/// 缩略图（规格 §3.4.3 的列表项之一）。
/// </summary>
/// <remarks>
/// <para>
/// 抽帧用**本机已有的 ffmpeg**（电脑端本来就带着它）—— 不引任何图片/视频库。
/// </para>
/// <para>
/// ⚠️ <b>必须缓存</b>：规格原话「**不得每次进页面都重新抽帧**」。
/// 抽一帧要解一段视频，一页十几条就是几秒 —— 而它每次翻页、每次刷新都会重来一遍。
/// 所以抽出来的图落在 <c>&lt;root&gt;/thumbnails/&lt;evidenceId&gt;.jpg</c>。
/// </para>
/// <para>
/// ⚠️ 抽不出来**不是错误**（文件坏了、编码器不支持）：返回 <see langword="null"/>，
/// 界面显示占位方块即可 —— 缩略图是锦上添花，它坏掉不该让整页失败。
/// </para>
/// </remarks>
public sealed class ThumbnailCache
{
    private readonly string _ffmpegPath;
    private readonly string _rootDirectory;
    private readonly IProcessRunner _runner;
    private readonly IAppLogger _logger;

    /// <summary>已经在抽的那些（同一张图被两处同时问到时别抽两次）。</summary>
    private readonly Dictionary<string, Task<string?>> _inFlight = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public ThumbnailCache(
        string ffmpegPath, string rootDirectory, IProcessRunner runner, IAppLogger? logger = null)
    {
        _ffmpegPath = ffmpegPath;
        _rootDirectory = rootDirectory;
        _runner = runner;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>缩略图落在哪儿。</summary>
    /// <remarks>
    /// <c>evidenceId</c> 是会话号 + 序号（<c>sess-…-000</c>），**只含字母数字和横线** ——
    /// 它本来就要当文件名用（分段文件就是这么命名的），所以不必再转义。
    /// </remarks>
    public string PathFor(string evidenceId) =>
        Path.Combine(_rootDirectory, "thumbnails", evidenceId + ".jpg");

    /// <summary>拿这一段的缩略图路径；抽不出来返回 <see langword="null"/>。</summary>
    public Task<string?> GetAsync(string evidenceId, string videoPath, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(evidenceId, out var running))
            {
                return running;
            }

            var task = GenerateAsync(evidenceId, videoPath, cancellationToken);
            _inFlight[evidenceId] = task;

            // 抽完就从「在抽」里拿掉 —— 留着的话，文件后来被删了就永远拿不到新的。
            _ = task.ContinueWith(
                _ =>
                {
                    lock (_gate)
                    {
                        _inFlight.Remove(evidenceId);
                    }
                },
                TaskScheduler.Default);

            return task;
        }
    }

    private async Task<string?> GenerateAsync(
        string evidenceId, string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            var cached = PathFor(evidenceId);

            // ⚠️ 判据是「文件在不在」而不是「我们抽过没有」—— 后者在**重启之后**
            // 全部落空，于是每次开程序都要重抽一整页。
            if (File.Exists(cached) && new FileInfo(cached).Length > 0)
            {
                return cached;
            }

            if (!File.Exists(videoPath))
            {
                return null;
            }

            var directory = Path.GetDirectoryName(cached);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写临时名再改名：抽到一半断电时留下的是一个**半截 JPG**，
            // 而界面会把它当成一张真缩略图显示出来（一片灰，看不出是坏的）。
            var temporary = cached + ".tmp";

            var result = await _runner.RunAsync(
                _ffmpegPath,
                [
                    "-hide_banner", "-v", "error",
                    // 取第 1 秒那一帧：第 0 秒常常还是黑的（相机刚起来）。
                    "-ss", "1",
                    "-i", videoPath,
                    "-frames:v", "1",
                    // 缩略图只要「一眼认出是哪一段」，320 宽足够。
                    "-vf", "scale=320:-2",
                    // ⚠️ **必须显式给 `-f image2`**：产物是 `.tmp` 结尾的临时名，
                    // 而 ffmpeg 靠扩展名猜格式 —— 猜不出来就直接失败。
                    // 实测：少了这一句，抽帧永远失败，而表现只是「一张缩略图都没有」。
                    "-f", "image2",
                    "-y", temporary,
                ],
                cancellationToken);

            if (result.ExitCode != 0 || !File.Exists(temporary))
            {
                _logger.Log(LogLevel.Warn, "缩略图", $"抽帧失败：{result.StandardError.Trim()}");

                TryDelete(temporary);
                return null;
            }

            File.Move(temporary, cached, overwrite: true);
            return cached;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 抽不出来不是错误 —— 见类注释。
            _logger.Log(LogLevel.Warn, "缩略图", $"抽帧出错：{ex.Message}");
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉只是留个 .tmp 垃圾。
        }
    }
}
