using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 缩略图（规格 §3.4.3 的列表项之一）。**不得每次进页面都重新抽帧。**
/// </summary>
public class ThumbnailCacheTests
{
    /// <summary>假的 ffmpeg：把「产物」写出来（内容无所谓，验的是缓存与命名）。</summary>
    private sealed class FakeRunner(bool succeed = true) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(arguments.ToList());

            if (!succeed)
            {
                return Task.FromResult(new ProcessResult(1, string.Empty, "抽不出来"));
            }

            var args = arguments.ToList();
            var yIndex = args.IndexOf("-y");
            if (yIndex >= 0 && yIndex + 1 < args.Count)
            {
                var output = args[yIndex + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, "jpeg-bytes");
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vidlog-thumb-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string Video(string id = "e1")
        {
            var path = System.IO.Path.Combine(Path, id + ".mp4");
            File.WriteAllText(path, "video-bytes");
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task 第一次抽_第二次读缓存()
    {
        using var dir = new TempDir();
        var runner = new FakeRunner();
        var cache = new ThumbnailCache("ffmpeg", dir.Path, runner);

        var first = await cache.GetAsync("e1", dir.Video());
        var second = await cache.GetAsync("e1", dir.Video());

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Single(runner.Invocations);   // 只抽了一次
    }

    [Fact]
    public async Task 重启之后也认缓存_判据是文件在不在()
    {
        // ⚠️ 判据不能是「我们抽过没有」—— 那在重启之后全部落空，
        // 于是每次开程序都要重抽一整页（而规格明确不许）。
        using var dir = new TempDir();

        var first = new ThumbnailCache("ffmpeg", dir.Path, new FakeRunner());
        await first.GetAsync("e1", dir.Video());

        var runner = new FakeRunner();
        var reopened = new ThumbnailCache("ffmpeg", dir.Path, runner);

        Assert.NotNull(await reopened.GetAsync("e1", dir.Video()));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task 抽不出来返回_null_而且不留半截文件()
    {
        // 半截 JPG 会被界面当成一张真缩略图显示出来（一片灰，看不出是坏的）。
        using var dir = new TempDir();
        var cache = new ThumbnailCache("ffmpeg", dir.Path, new FakeRunner(succeed: false));

        await cache.GetAsync("e1", dir.Video());

        Assert.Null(await cache.GetAsync("e1", dir.Video()));
        Assert.False(File.Exists(cache.PathFor("e1")));
    }

    [Fact]
    public async Task 视频不在时不去调_ffmpeg()
    {
        using var dir = new TempDir();
        var runner = new FakeRunner();
        var cache = new ThumbnailCache("ffmpeg", dir.Path, runner);

        Assert.Null(await cache.GetAsync("e1", Path.Combine(dir.Path, "没有这个文件.mp4")));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void 缩略图落在_thumbnails_目录下_而且按_evidenceId_命名()
    {
        var cache = new ThumbnailCache("ffmpeg", @"C:\data\VidLog", new FakeRunner());

        Assert.Equal(
            Path.Combine(@"C:\data\VidLog", "thumbnails", "sess-1-000.jpg"),
            cache.PathFor("sess-1-000"));
    }

    /// <summary>
    /// 真 ffmpeg 抽一帧出来 —— 证明**这条命令真的能出图**。
    /// </summary>
    /// <remarks>
    /// 参数写错时（<c>-ss</c> 位置不对、<c>scale</c> 写错）假 runner 照样绿，
    /// 而真机上表现是「一张缩略图都没有」。
    /// </remarks>
    [Fact]
    public async Task 端到端_真_ffmpeg_抽得出图()
    {
        var ffmpeg = FfmpegLocator.TryFind();
        if (ffmpeg is null)
        {
            return;   // 本机没 ffmpeg 就跳过（与其它集成测试同一个口径）
        }

        using var dir = new TempDir();

        // 先造一段真视频（合成源，两秒）。
        var video = Path.Combine(dir.Path, "source.mp4");

        Assert.Equal(0, await RunAsync(ffmpeg, [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc=duration=2:size=640x360:rate=30",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-y", video,
        ]));

        var cache = new ThumbnailCache(
            ffmpeg, dir.Path, new SystemProcessRunner(NullLogger.Instance));

        var thumbnail = await cache.GetAsync("e1", video);

        Assert.NotNull(thumbnail);
        Assert.True(File.Exists(thumbnail));

        // ⚠️ 判据是「这是个能打开的 JPEG」而不只是「文件非空」：
        // 真出错时 ffmpeg 也会留下一个零碎文件（这正是要防的那种「看起来有图」）。
        var bytes = await File.ReadAllBytesAsync(thumbnail!);

        Assert.True(bytes.Length > 200, $"缩略图太小了（{bytes.Length} 字节），多半不是真图");
        Assert.Equal(0xFF, bytes[0]);   // JPEG 的 SOI
        Assert.Equal(0xD8, bytes[1]);
    }

    private static async Task<int> RunAsync(string ffmpeg, IReadOnlyList<string> arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
