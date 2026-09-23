using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Playback;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Search;
using VidLog.Desktop.Core.Upload;
using VidLog.Desktop.Core.Web;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 规格 §3.8：电脑端提供局域网网页回放。
/// </summary>
/// <remarks>
/// 起真 <see cref="HttpListener"/> 打真 HTTP 请求 —— 路由、Range、JSON 这些
/// 只有真跑一遍才知道对不对，mock 掉就什么都没验。
/// 绑 localhost 不需要 urlacl，所以这些测试在 CI 上也能跑。
/// </remarks>
public class PlaybackServerTests
{
    private const string DeviceName = "测试主机";

    /// <summary>
    /// 解码校验必定通过的假 FFmpeg。
    /// </summary>
    /// <remarks>
    /// <see cref="UploadReceiver"/> 在发布前会让 FFmpeg 真解一遍
    /// （规格 §3.1.4，手机端没有这一步，所以这是唯一一次能发现「手机产出坏文件」的机会）。
    /// 这里上传的是随手造的字节，不是真 MP4，所以把那一关假掉 ——
    /// **上传链路**本身要验的东西不受影响。
    /// </remarks>
    private sealed class AlwaysOkRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-web-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);
        public string Dir(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required PlaybackServer Server { get; init; }
        public required HttpClient Client { get; init; }
        public required string BaseUrl { get; init; }
        public required byte[] MediaBytes { get; init; }
        public required string EvidenceId { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<Fixture> StartAsync(TempDir dir)
    {
        const string evidenceId = "e1";
        const string relative = "2026/09/16/SF1000000001/e1.mp4";

        var archiveRoot = dir.Dir("archive");
        var mediaPath = System.IO.Path.Combine(archiveRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(mediaPath)!);

        var mediaBytes = new byte[100];
        for (var i = 0; i < mediaBytes.Length; i++)
        {
            mediaBytes[i] = (byte)i;
        }

        await File.WriteAllBytesAsync(mediaPath, mediaBytes);

        var started = new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.FromHours(8));
        var index = new JsonLinesRecordingIndex(dir.File("index.jsonl"));
        await index.AddAsync(new RecordingEntry(
            evidenceId,
            "session-1",
            WaybillNumber.Parse("SF1000000001"),
            started,
            started.AddMinutes(1),
            TimeSpan.FromMinutes(1),
            RelativePath.Parse(relative),
            ContentHash.Parse(new string('a', 64)),
            "device-1"));

        await index.AddAsync(new RecordingEntry(
            "e2",
            "session-2",
            WaybillNumber.Parse("YT9999999999"),
            started.AddHours(1),
            started.AddHours(1).AddMinutes(2),
            TimeSpan.FromMinutes(2),
            RelativePath.Parse("2026/09/16/YT9999999999/e2.mp4"),
            ContentHash.Parse(new string('b', 64)),
            "device-1"));

        var labels = new JsonLinesLabelStore(dir.File("labels.jsonl"));
        await labels.SetAsync(evidenceId, LabelKeys.BusinessType, BusinessTypes.OutboundValue);
        await labels.SetAsync("e2", LabelKeys.BusinessType, BusinessTypes.ReturnValue);

        var punchLog = new JsonLinesPunchLog(dir.File("punches.jsonl"));
        await punchLog.AppendAsync(new Punch(
            "p1", "session-1", WaybillNumber.Parse("SF1000000001"),
            started, 30_000, PunchSource.KeyboardScanner));

        var port = FreePort();
        var baseUrl = $"http://localhost:{port}/";

        var layout = new DataLayout(dir.Path);

        var server = new PlaybackServer(
            new PlaybackServerOptions { Prefix = baseUrl, ArchiveRoot = archiveRoot },
            new RecordingSearch(index, labels),
            index,
            new PunchNavigation(index, punchLog),
            new UploadReceiver(
                layout,
                index,
                punchLog,
                labels,
                new DecodeVerifier("ffmpeg", new AlwaysOkRunner()),
                DeviceName),
            new DeviceRegistry(layout.DevicesPath),
            DeviceName);

        await server.StartAsync();

        return new Fixture
        {
            Server = server,
            Client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(20) },
            BaseUrl = baseUrl,
            MediaBytes = mediaBytes,
            EvidenceId = evidenceId,
        };
    }

    // ─────────────────────────────────────────────
    // 页面
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 根路径返回回放页面()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var response = await fixture.Client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("VidLog", html);
        Assert.Contains("<video", html);
    }

    [Fact]
    public async Task 页面不依赖任何外部资源_局域网可能完全无网()
    {
        // I10：完全无网环境下除归档与交付外全部功能可用。
        // 引用 CDN 的页面在工位断网时就是个白屏。
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var html = await (await fixture.Client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("http://cdn", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://cdn", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link rel=\"stylesheet\"", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 页面写明这不是证据分享链接()
    {
        // I7 的边界：本服务直接读本地副本，而本地副本可能已被生命周期清理。
        // 页面必须讲清楚，避免有人把它当分享链接发出去。
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var html = await (await fixture.Client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("不是证据分享链接", html);
    }

    // ─────────────────────────────────────────────
    // 检索接口
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 检索接口返回全部录像()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var response = await fixture.Client.GetAsync("/api/search");
        var items = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, items.GetArrayLength());
    }

    [Fact]
    public async Task 检索接口按单号过滤()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var json = await (await fixture.Client.GetAsync("/api/search?q=YT9999&mode=prefix"))
            .Content.ReadAsStringAsync();
        var items = JsonSerializer.Deserialize<JsonElement>(json);

        var only = Assert.Single(items.EnumerateArray().ToList());
        Assert.Equal("YT9999999999", only.GetProperty("waybill").GetString());
    }

    [Fact]
    public async Task 检索接口按发货退货过滤()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var json = await (await fixture.Client.GetAsync("/api/search?type=return"))
            .Content.ReadAsStringAsync();
        var items = JsonSerializer.Deserialize<JsonElement>(json);

        var only = Assert.Single(items.EnumerateArray().ToList());
        Assert.Equal("e2", only.GetProperty("evidenceId").GetString());
        Assert.Equal("Return", only.GetProperty("businessType").GetString());
    }

    [Fact]
    public async Task 检索无结果时返回空数组而不是错误()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var json = await (await fixture.Client.GetAsync("/api/search?q=NOPE")).Content.ReadAsStringAsync();

        Assert.Equal(0, JsonSerializer.Deserialize<JsonElement>(json).GetArrayLength());
    }

    // ─────────────────────────────────────────────
    // 打点导航（§3.8「跳转到打点位置」）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 打点接口返回播放器可直接seek的位置()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var json = await (await fixture.Client.GetAsync($"/api/punches?evidenceId={fixture.EvidenceId}"))
            .Content.ReadAsStringAsync();
        var items = JsonSerializer.Deserialize<JsonElement>(json);

        var only = Assert.Single(items.EnumerateArray().ToList());
        Assert.Equal("p1", only.GetProperty("punchId").GetString());
        Assert.Equal(fixture.EvidenceId, only.GetProperty("evidenceId").GetString());
        Assert.Equal(30, only.GetProperty("offsetSeconds").GetDouble());
        Assert.Equal("SF1000000001", only.GetProperty("waybillNumber").GetString());
    }

    [Fact]
    public async Task 打点接口对未知证据返回空数组()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var json = await (await fixture.Client.GetAsync("/api/punches?evidenceId=nope"))
            .Content.ReadAsStringAsync();

        Assert.Equal(0, JsonSerializer.Deserialize<JsonElement>(json).GetArrayLength());
    }

    // ─────────────────────────────────────────────
    // 视频与 Range
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 视频可以整段取回()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var response = await fixture.Client.GetAsync($"/media/{fixture.EvidenceId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(fixture.MediaBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task 声明支持Range()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var response = await fixture.Client.GetAsync($"/media/{fixture.EvidenceId}");

        Assert.Contains("bytes", response.Headers.AcceptRanges);
    }

    [Fact]
    public async Task Range请求返回206与正确的字节区间()
    {
        // 浏览器拖进度条就靠这个；不支持 Range 的话视频根本没法跳转播放。
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/media/{fixture.EvidenceId}");
        request.Headers.Range = new RangeHeaderValue(10, 19);

        var response = await fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 10-19/100", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(10, body.Length);
        Assert.Equal(fixture.MediaBytes.Skip(10).Take(10), body);
    }

    [Fact]
    public async Task 后缀Range取最后若干字节()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/media/{fixture.EvidenceId}");
        request.Headers.TryAddWithoutValidation("Range", "bytes=-10");

        var response = await fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(fixture.MediaBytes.Skip(90).Take(10), body);
    }

    // ─────────────────────────────────────────────
    // 安全
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 未知证据id返回404()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var response = await fixture.Client.GetAsync("/media/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/media/..%2F..%2Fwindows%2Fwin.ini")]
    [InlineData("/media/%2e%2e%2f%2e%2e%2fsecret")]
    [InlineData("/media/..%5C..%5Cwindows%5Cwin.ini")]
    [InlineData("/media/../../etc/passwd")]
    public async Task 路径穿越取不到任何文件(string path)
    {
        // 这里断言的是**安全性质**（取不到内容），不是某个具体状态码。
        // 实测：带编码的 .. 会在请求进到我们的代码**之前**被 http.sys 挡掉（403），
        // 而普通 ../../ 会被 HttpClient 归一化掉（于是走到 404）。
        // 换一个 HTTP 服务器实现，状态码可能不同，但性质必须一样。
        using var dir = new TempDir();
        var archiveRoot = dir.Dir("archive");
        Directory.CreateDirectory(archiveRoot);
        await File.WriteAllTextAsync(System.IO.Path.Combine(archiveRoot, "win.ini"), "不该被读到");
        await File.WriteAllTextAsync(dir.File("passwd"), "不该被读到");

        await using var fixture = await StartAsync(dir);

        var response = await fixture.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound
                or HttpStatusCode.Forbidden
                or HttpStatusCode.BadRequest,
            $"路径穿越应当被拒绝，实际 {response.StatusCode}");

        Assert.DoesNotContain("不该被读到", body);
    }

    [Fact]
    public async Task 未收录的路径不存在于归档根下的文件也取不到()
    {
        // 归档根里放一个索引里没有的文件 —— 直接按名字取应当取不到。
        using var dir = new TempDir();
        var archiveRoot = dir.Dir("archive");
        Directory.CreateDirectory(archiveRoot);
        await File.WriteAllTextAsync(System.IO.Path.Combine(archiveRoot, "secret.txt"), "不该被读到");

        await using var fixture = await StartAsync(dir);

        var response = await fixture.Client.GetAsync("/media/secret.txt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 未知路由返回404()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var response = await fixture.Client.GetAsync("/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 服务停掉后不再应答()
    {
        using var dir = new TempDir();
        var fixture = await StartAsync(dir);
        var url = fixture.BaseUrl;

        await fixture.Server.StopAsync();
        fixture.Client.Dispose();

        using var client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(5) };

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("/"));
    }
}
