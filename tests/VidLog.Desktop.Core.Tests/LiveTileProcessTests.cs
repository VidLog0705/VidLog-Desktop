using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VidLog.Desktop.Core.Live;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 多画面那一格：从**手机推上来的 HTTP 裸流**里取画面、问计数、改档。
/// </summary>
/// <remarks>
/// <para>
/// 这一组**真的把整条路径跑通**：本机 ffmpeg 先造一段 H.264，再起一台**假手机**
/// 按手机那侧的契约（`video/h264`、循环喂、每个循环开头带 SPS/PPS + IDR，
/// 外加 `/status` 与 `/quality`）把它喂出去。
/// </para>
/// <para>
/// ⚠️ <b>它挡的是「两端契约对不上」这一类错</b>：电脑端少给 <c>-f h264</c>、
/// 或者手机那侧不是 Annex-B、或者 SPS/PPS 没跟着关键帧走、或者改档的参数名两边
/// 写得不一样 —— 这些在两边各自的单元测试里**都看不出来**，
/// 表现清一色是「那一格一直黑着」或者「选了 1080P 还是糊」。
/// </para>
/// <para>
/// ⚠️ <b>手写 `TcpListener` 而不是 `HttpListener`</b>：后者在 Windows 上要 urlacl
/// （要么管理员、要么事先注册前缀），不该为一条测试去动那个。
/// </para>
/// </remarks>
public class LiveTileProcessTests
{
    [RequiresFfmpegFact]
    public async Task 拉一路HTTP裸流_出得来帧_而且尺寸是我们要的那个()
    {
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));

        Assert.True(h264.Length > 0, "ffmpeg 没造出 H.264 来，后面的断言都不作数");

        using var phone = new FakePhone(h264);

        await using var tile = LiveTileProcess.Start(ffmpeg!, phone.LiveUrl, 320, 180);

        Assert.NotNull(tile);

        var frame = await WaitForFrameAsync(tile!, TimeSpan.FromSeconds(30));

        Assert.True(frame is not null, $"三十秒没等到一帧。ffmpeg 说：{tile!.ErrorTail}");

        Assert.Equal(320, frame!.Width);
        Assert.Equal(180, frame.Height);
        Assert.Equal(320 * 180 * 3, frame.Rgb.Length);
    }

    [RequiresFfmpegFact]
    public async Task 一格_画面与计数都拿得到()
    {
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264) { Outbound = 12, Returned = 3 };

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位");

        Assert.True(
            await WaitForFrameAsync(tile, TimeSpan.FromSeconds(30)) is not null,
            "没等到画面");

        await tile.RefreshStatusAsync();

        Assert.NotNull(tile.Counts);
        Assert.Equal(12, tile.Counts!.Outbound);
        Assert.Equal(3, tile.Counts.Returned);
        Assert.Equal(480, tile.Counts.ReportedQuality);
        Assert.Null(tile.Problem);
    }

    [RequiresFfmpegFact]
    public async Task 改档要先告诉手机_成了本地才按新尺寸重来()
    {
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264);

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位");
        Assert.Equal(LiveQuality.P480, tile.Quality);

        var changed = await tile.SetQualityAsync(LiveQuality.P1080);

        Assert.True(changed);
        Assert.Equal([1080], phone.QualityRequests);
        Assert.Equal(LiveQuality.P1080, tile.Quality);

        // 本地那一路按新尺寸重来了 —— 1080 档是 1920×1080。
        var frame = await WaitForFrameAsync(tile, TimeSpan.FromSeconds(30));
        Assert.NotNull(frame);
        Assert.Equal(1920, frame!.Width);
        Assert.Equal(1080, frame.Height);
    }

    [RequiresFfmpegFact]
    public async Task 手机拒了改档_本地就不许按新档来()
    {
        // 反过来的话：手机没改成而本地按新档解，那一格会一直等一个永远不来的分辨率
        // —— 看起来像卡死。
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264) { RejectQuality = true };

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位");

        var changed = await tile.SetQualityAsync(LiveQuality.P1080);

        Assert.False(changed);
        Assert.Equal(LiveQuality.P480, tile.Quality);
    }

    [RequiresFfmpegFact]
    public async Task 问不到计数不影响画面()
    {
        // 网络抖一下不该让整面墙闪一下「无信号输入」。
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264);

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位");

        Assert.True(await WaitForFrameAsync(tile, TimeSpan.FromSeconds(30)) is not null);

        // 把手机「关掉」再问计数。
        phone.Dispose();
        await tile.RefreshStatusAsync();

        Assert.Null(tile.Counts);
        // 画面还在（读循环里的最后一帧），只是下面那两个字先不显示。
        Assert.NotNull(tile.Latest());
    }

    [RequiresFfmpegFact]
    public async Task 手机没开实时共享时_起得来但不报帧也不抛()
    {
        // ⚠️ 这就是「无信号输入」那一态：机位不够、或者那台手机没开共享。
        // 抛异常的话整个多画面窗口都开不了 —— 一格拉不起来不该拖垮别的八格。
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        var dead = new TcpListener(IPAddress.Loopback, 0);
        dead.Start();
        var port = ((IPEndPoint)dead.LocalEndpoint).Port;
        dead.Stop();

        await using var tile = LiveTile.Start(ffmpeg!, $"http://127.0.0.1:{port}", "空机位");

        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Null(tile.Latest());

        // 计数问不到也不该抛。
        await tile.RefreshStatusAsync();
        Assert.Null(tile.Counts);
    }

    [RequiresFfmpegFact]
    public async Task 计数问不到时_只在变坏和变好各记一条()
    {
        // ⚠️ 这是 §6.1 那条配套要求：**重复的问题只在「变了」的时候记**。
        // 计数是**每秒问一次**的；每次都记的话一分钟六十条，
        // 而被灌满的日志等于没有日志。
        var ffmpeg = FfmpegLocator.TryFind();
        Assert.NotNull(ffmpeg);

        using var dir = new TempDir();
        var h264 = await EncodeAsync(ffmpeg!, dir.File("stream.h264"));
        using var phone = new FakePhone(h264) { BrokenStatus = true };
        var logger = new CapturingLogger();

        await using var tile = LiveTile.Start(ffmpeg!, phone.BaseUrl, "1 号机位", logger: logger);

        // 只数跟计数有关的那几条（同一路上还有「起了一路」之类的正常留痕）。
        List<string> About() => [.. logger.Messages.Where(m => m.Contains("计数") || m.Contains("问到"))];

        for (var i = 0; i < 4; i++) await tile.RefreshStatusAsync();

        Assert.Single(About());
        Assert.Contains("问不到计数", About()[0]);
        Assert.Null(tile.Counts);

        // 坏 → 好：这一下也该说一条 —— 不然「什么时候恢复的」没人知道。
        phone.BrokenStatus = false;
        await tile.RefreshStatusAsync();

        Assert.Equal(2, About().Count);
        Assert.Contains("又能问到计数", About()[1]);
        Assert.NotNull(tile.Counts);
    }

    // ─────────────────────────────────────────────
    // 夹具
    // ─────────────────────────────────────────────

    /// <summary>把日志收起来，好在测试里断言（本仓既有写法）。</summary>
    private sealed class CapturingLogger : VidLog.Desktop.Core.Diagnostics.IAppLogger
    {
        public List<string> Messages { get; } = [];

        public void Log(VidLog.Desktop.Core.Diagnostics.LogLevel level, string category, string message) =>
            Messages.Add(message);

        public void Log(
            VidLog.Desktop.Core.Diagnostics.LogLevel level,
            string category,
            string message,
            IReadOnlyDictionary<string, object?> data) => Messages.Add(message);
    }

    /// <summary>临时目录（本仓每个测试类各自带一个，是既有惯例）。</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 进程还没放干净 —— 留给系统清，不为它把测试弄红。
            }
        }
    }

    /// <summary>用 lavfi 造一段真的 H.264（Annex-B）。</summary>
    private static async Task<byte[]> EncodeAsync(string ffmpeg, string path)
    {
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error",
                     "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10",
                     "-t", "1",
                     "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
                     "-pix_fmt", "yuv420p",
                     "-f", "h264", path,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();

        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : [];
    }

    private static async Task<LiveFrame?> WaitForFrameAsync(LiveTileProcess tile, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (tile.Latest() is { } frame) return frame;
            await Task.Delay(50);
        }

        return null;
    }

    private static async Task<LiveFrame?> WaitForFrameAsync(LiveTile tile, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (tile.Latest() is { } frame) return frame;
            await Task.Delay(50);
        }

        return null;
    }

    /// <summary>
    /// 一台**假手机**：按手机那侧的契约提供 <c>/live</c>、<c>/status</c>、<c>/quality</c>。
    /// </summary>
    private sealed class FakePhone : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[] _payload;
        private readonly CancellationTokenSource _cts = new();

        public FakePhone(byte[] payload)
        {
            _payload = payload;
            _listener.Start();

            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }

        public string LiveUrl => $"{BaseUrl}/live";

        public int Outbound { get; set; } = 7;

        public int Returned { get; set; } = 2;

        /// <summary>手机现在这一档。</summary>
        public int Quality { get; private set; } = 480;

        /// <summary>装成「这个应用不接受改档」（未过审、或那一档不支持）。</summary>
        public bool RejectQuality { get; set; }

        /// <summary>装成「计数坏了」（手机在、画面也在，就是 /status 回不了）。</summary>
        public bool BrokenStatus { get; set; }

        /// <summary>收到过的改档请求（按顺序）。</summary>
        public List<int> QualityRequests { get; } = [];

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;

            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, _cts.Token);
                if (request is null) return;

                var (path, query) = SplitTarget(request);

                if (path == "/live")
                {
                    await ServeVideoAsync(stream);
                    return;
                }

                if (path == "/status")
                {
                    if (BrokenStatus)
                    {
                        await WriteAsync(stream, 500, """{"error":"broken"}""");
                        return;
                    }

                    await WriteAsync(
                        stream,
                        200,
                        $$"""{"f":{{Outbound}},"t":{{Returned}},"p":{{Quality}}}""");
                    return;
                }

                if (path == "/quality")
                {
                    await ServeQualityAsync(stream, query);
                    return;
                }

                await WriteAsync(stream, 404, """{"error":"not found"}""");
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // 客户端走了 —— 正常路径。
            }
        }

        private async Task ServeQualityAsync(NetworkStream stream, string query)
        {
            var raw = query.Split('&')
                .Select(pair => pair.Split('=', 2))
                .FirstOrDefault(pair => pair.Length == 2 && pair[0] == "p")?[1];

            if (RejectQuality || !int.TryParse(raw, out var wanted) || wanted is not (480 or 720 or 1080))
            {
                await WriteAsync(stream, 400, """{"error":"bad quality"}""");
                return;
            }

            QualityRequests.Add(wanted);
            Quality = wanted;

            await WriteAsync(stream, 200, $$"""{"p":{{wanted}}}""");
        }

        private async Task ServeVideoAsync(NetworkStream stream)
        {
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: video/h264\r\n"
                    + "Cache-Control: no-store\r\n"
                    + "Connection: close\r\n\r\n"),
                _cts.Token);

            await stream.FlushAsync(_cts.Token);

            // ⚠️ 一直循环喂：既是「实时流不结束」的形状，
            // 也让每个循环开头都带上一组 SPS/PPS + IDR —— 正是手机那侧的契约。
            while (!_cts.IsCancellationRequested)
            {
                await stream.WriteAsync(_payload, _cts.Token);
                await stream.FlushAsync(_cts.Token);
                await Task.Delay(50, _cts.Token);
            }
        }

        private static async Task WriteAsync(NetworkStream stream, int status, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {StatusText(status)}\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Cache-Control: no-store\r\n"
                + "Connection: close\r\n"
                + $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");

            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }

        private static string StatusText(int status) => status switch
        {
            200 => "OK",
            400 => "Bad Request",
            _ => "Not Found",
        };

        private static (string Path, string Query) SplitTarget(string requestLine)
        {
            // "GET /live?x=1 HTTP/1.1"
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return ("/", string.Empty);

            var target = parts[1];
            var cut = target.IndexOf('?');

            return cut < 0 ? (target, string.Empty) : (target[..cut], target[(cut + 1)..]);
        }

        private static async Task<string?> ReadRequestAsync(
            NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024];
            var seen = new StringBuilder();

            while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read <= 0) return null;

                seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (seen.Length > 8192) break; // 防呆：正常请求头不会这么大
            }

            var lines = seen.ToString().Split("\r\n");
            return lines.Length > 0 ? lines[0] : null;
        }

        public void Dispose()
        {
            // ⚠️ 幂等：有的用例会**中途**把手机「关掉」（模拟掉线），
            // 然后 `using` 再放一次 —— 直接 Cancel 一个已释放的 CTS 会抛。
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            try { _cts.Cancel(); } catch (ObjectDisposedException) { }

            _listener.Stop();
            _cts.Dispose();
        }

        private int _disposed;
    }
}
