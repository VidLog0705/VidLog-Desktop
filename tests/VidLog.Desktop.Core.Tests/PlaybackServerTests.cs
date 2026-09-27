using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
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
/// <para>
/// ⚠️ 它属于 <see cref="HttpListenerCollection"/>：这一族**串行**跑，
/// 为的是把「两个测试挑到同一个端口」那个竞态从根上拿掉（见那个文件的说明）。
/// </para>
/// </remarks>
[Collection(HttpListenerCollection.Name)]
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
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（2026-09-27 实测：会话还在
            // 收尾时删含 `.ass` / `.mkv` 的临时目录）。清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required PlaybackServer Server { get; init; }
        public required HttpClient Client { get; init; }
        public required string BaseUrl { get; init; }
        public required byte[] MediaBytes { get; init; }
        public required string EvidenceId { get; init; }

        /// <summary>已入网那台设备的凭据（`/api/v1/*` 要它）。</summary>
        public required string Credential { get; init; }

        /// <summary>归档层里那条录像的相对路径。</summary>
        public required string Location { get; init; }

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

    /// <param name="logger">请求日志。不传就不记（默认）。</param>
    private static async Task<Fixture> StartAsync(TempDir dir, IAppLogger? logger = null)
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

        // ⚠️ 这一条**带录制规格**（H.265 / 4K），另一条（e1）不带 ——
        // 于是「回包里 codec 有没有真的从索引流过来」验得出来：
        // 两条都是 null 的话，「字段在」与「值是对的」分不开。
        await index.AddAsync(new RecordingEntry(
            "e2",
            "session-2",
            WaybillNumber.Parse("YT9999999999"),
            started.AddHours(1),
            started.AddHours(1).AddMinutes(2),
            TimeSpan.FromMinutes(2),
            RelativePath.Parse("2026/09/16/YT9999999999/e2.mp4"),
            ContentHash.Parse(new string('b', 64)),
            "device-1",
            Codec: "H265",
            Resolution: "Uhd4K"));

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
        var devices = new DeviceRegistry(layout.DevicesPath);

        // 走一遍完整入网，拿一张真凭据 —— `/api/v1/*` 一律要它，
        // 而「凭据从哪儿来」这件事本身就是那条接口的一半。
        var session = await devices.OpenSessionAsync();
        await devices.RequestAsync("device-1", "测试手机", session.Token);
        await devices.DecideAsync("device-1", approved: true);
        var credential = (await devices.ClaimAsync("device-1", session.Token)).Credential!;

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
            devices,
            DeviceName,
            logger);

        await server.StartAsync();

        return new Fixture
        {
            Server = server,
            Client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(20) },
            BaseUrl = baseUrl,
            MediaBytes = mediaBytes,
            EvidenceId = evidenceId,
            Credential = credential,
            Location = relative,
        };
    }

    // ─────────────────────────────────────────────
    // 回查归档层（规格 §3.5.4；手机端「手动删除」的前置闸 §3.5.6③）
    // ─────────────────────────────────────────────

    private static HttpRequestMessage VerifyRequest(string credential, string location)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/archive/verify")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { location }),
                System.Text.Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    [Fact]
    public async Task 回查归档层_那一份还在()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var response = await fixture.Client.SendAsync(
            VerifyRequest(fixture.Credential, fixture.Location));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        Assert.True(payload.GetProperty("exists").GetBoolean());
        Assert.False(payload.GetProperty("couldNotVerify").GetBoolean());
    }

    [Fact]
    public async Task 回查归档层_那一份不在了_而且与查不了分得开()
    {
        // 规格 §3.5.6③：**回查查不到（或查不了）⇒ 不许删**。
        // 这两者对用户是两句话，所以答复里必须是两个字段。
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var response = await fixture.Client.SendAsync(
            VerifyRequest(fixture.Credential, "2026/09/16/SF1000000001/根本没有这一条.mp4"));

        var payload = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        Assert.False(payload.GetProperty("exists").GetBoolean());
        Assert.False(payload.GetProperty("couldNotVerify").GetBoolean(),
            "目录摸得到、文件不在 —— 这才叫「不存在」");
    }

    [Fact]
    public async Task 回查归档层_没有凭据就拒()
    {
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/archive/verify")
        {
            Content = new StringContent("""{"location":"a.mp4"}"""),
        };

        var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 回查归档层_路径越级一律拒()
    {
        // ⚠️ 这条接口是**远端**调的，所以 `RelativePath` 那道校验在这里就是安全边界：
        // 放过去的话，手机端就能问「C:\Windows\...」在不在。
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        foreach (var bad in new[] { @"C:\Windows\notepad.exe", @"\\nas\share\x.mp4", "../../secret.mp4" })
        {
            var response = await fixture.Client.SendAsync(
                VerifyRequest(fixture.Credential, bad));

            var payload = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

            Assert.False(payload.GetProperty("exists").GetBoolean());
            Assert.True(payload.GetProperty("couldNotVerify").GetBoolean(),
                $"「{bad}」这种路径必须在**回查之前**就被拒掉");
        }
    }

    // ─────────────────────────────────────────────
    // 页面
    // ─────────────────────────────────────────────

    // ─────────────────────────────────────────────
    // 请求日志（2026-09-26）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 等到日志文件里出现至少 <paramref name="count"/> 行，且**每一行都能当 JSON 解析**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两个理由都要等，缺一个就是间歇性红：
    /// </para>
    /// <list type="number">
    /// <item>日志是**异步落盘**的（后台队列），而请求的 `finally` 也可能还没跑到 ——
    /// 直接读会读到一半。</item>
    /// <item>⚠️ 更阴的一个：**边写边读会读到半行**。写入是一个 syscall，
    /// 但读者可能在它落完之前就看到文件变长了 —— 于是最后一行是残缺的 JSON，
    /// `JsonDocument.Parse` 当场抛。所以判据不是「行数够了」，
    /// 而是「行数够了**而且都解析得动**」。</item>
    /// </list>
    /// </remarks>
    private static async Task<List<JsonElement>> WaitForLogEntriesAsync(string path, int count)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path))
            {
                var lines = await TryReadAllLinesAsync(path);

                if (lines is not null && lines.Length >= count && TryParseAll(lines, out var entries))
                {
                    return entries;
                }
            }

            await Task.Delay(20);
        }

        Assert.Fail($"{path} 里始终没有出现 {count} 行可解析的日志（等到超时）");
        return [];
    }

    /// <summary>
    /// 读日志文件 —— **允许写者继续写**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>不能图省事用 <c>File.ReadAllLinesAsync</c>：它的默认共享模式是
    /// <see cref="FileShare.Read"/>，意思是「我读的时候<b>不许别人写</b>」</b> ——
    /// 而写者（<c>FileLogger</c>）此刻正允许别人读（<c>FileShare.Read | FileShare.Delete</c>），
    /// 两边一撞就是 <c>IOException</c>「文件正被另一个进程使用」。
    /// </para>
    /// <para>
    /// 表现是这条用例**偶发红**，而红的原因与它要验的「每个请求留一条日志」
    /// **毫无关系**（2026-09-27 抓到了现场：堆栈就停在这一行）。
    /// 这一类「重跑就绿」最坏 —— 它让人开始无视红（§46 那句话）。
    /// </para>
    /// <para>
    /// ⚠️ 诊断包导出读日志用的是**同一个口径**（<c>FileShare.ReadWrite</c>）
    /// —— 照它抄，别另立一套。
    /// </para>
    /// </remarks>
    /// <returns>读不到时返回 <see langword="null"/>（下一轮 20 毫秒后再试）。</returns>
    private static async Task<string[]?> TryReadAllLinesAsync(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var lines = new List<string>();
            while (await reader.ReadLineAsync() is { } line)
            {
                lines.Add(line);
            }

            return [.. lines];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 正被写着、或刚被删掉 —— 轮询本来就会再来一次。
            return null;
        }
    }

    private static bool TryParseAll(string[] lines, out List<JsonElement> entries)
    {
        entries = [];

        foreach (var line in lines)
        {
            try
            {
                entries.Add(JsonDocument.Parse(line).RootElement.Clone());
            }
            catch (JsonException)
            {
                // 多半是最后那行还没写完 —— 下一轮再看。
                return false;
            }
        }

        return true;
    }

    [Fact]
    public async Task 每个请求都留下一条带_trace_与状态码的日志()
    {
        // 2026-09-26 之前这里**一行日志都没有**，而出错那个 catch 是**静默吞掉**的：
        // 手机那边只看到 500，电脑端连出了什么事都不知道。
        using var dir = new TempDir();
        using var logDir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(logDir.Path, "vidlog"));

        await using var fixture = await StartAsync(dir, logger);

        await fixture.Client.GetAsync("/");
        await fixture.Client.GetAsync("/api/nope");

        var entries = await WaitForLogEntriesAsync(logger.Path, 2);
        await logger.DisposeAsync();

        Assert.Equal(2, entries.Count);

        var ok = Assert.Single(entries, e => e.GetProperty("data").GetProperty("path").GetString() == "/");
        Assert.Equal(200, ok.GetProperty("data").GetProperty("status").GetInt32());
        Assert.Equal("INFO", ok.GetProperty("lvl").GetString());
        Assert.Equal("GET", ok.GetProperty("data").GetProperty("method").GetString());

        // ⚠️ `trace` 是**日志器自己从异步上下文里取的**，调用点一个字都没传 ——
        // 这一条证明那条路通了，否则「把一次上传的七八行串起来」就是空话。
        Assert.False(string.IsNullOrWhiteSpace(ok.GetProperty("trace").GetString()));

        // 4xx 以上是 Warn：一次错误的路径请求不该和正常的页面请求一个级别，
        // 否则「昨天有没有异常请求」要靠人肉翻完整份日志。
        // ⚠️ 路径用 ASCII 的：日志里记的是 `Url.AbsolutePath`，也就是**参与路由的那个
        // 原始路径**（中文会被客户端百分号编码）。这是有意的 ——
        // 日志要如实反映「路由当时看到的是什么」，而不是事后美化过的样子。
        var missing = Assert.Single(
            entries, e => e.GetProperty("data").GetProperty("path").GetString() == "/api/nope");
        Assert.Equal(404, missing.GetProperty("data").GetProperty("status").GetInt32());
        Assert.Equal("WARN", missing.GetProperty("lvl").GetString());
    }

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
    public async Task 页面写明它不是交付渠道_并指向导出那条路()
    {
        // 规格 §3.7 改版之后**不再有链接**（分享 = 交付原视频）。
        // 而这一页看起来很像一条「能发出去的地址」—— 所以必须讲清楚它**不是**，
        // 并且**告诉用户该走哪儿**（只说「不能这么用」而不给出路，等于把人留在原地）。
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        var html = await (await fixture.Client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("不是交付渠道", html);
        Assert.Contains("导出原视频", html);
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
    public async Task 检索回包里带着编码_页面据此如实告知_H265_可能播不了()
    {
        // 规格 §3.1.7 的连带项：「**网页回放的兼容性** —— H.265 已确定要做，
        // 而**浏览器对它的支持不一致** ⇒ 局域网网页回放**可能播不了 H.265 录的**。
        // …产品必须**如实告知**当前这条录像能不能在网页里播（**不得承诺做不到的事**），
        // **不为此砍掉 H.265 选项**」。
        using var dir = new TempDir();
        await using var fixture = await StartAsync(dir);

        // ① 回包里带着**这条录像自己的**编码 —— 夹具里 e2 是 H.265、e1 没记，
        //    正好证明它是从索引来的，不是个写死的值。
        var json = await (await fixture.Client.GetAsync("/api/search")).Content.ReadAsStringAsync();
        var items = JsonSerializer.Deserialize<JsonElement>(json);

        var h265 = items.EnumerateArray()
            .Single(i => i.GetProperty("evidenceId").GetString() == "e2");
        Assert.Equal("H265", h265.GetProperty("codec").GetString());

        var old = items.EnumerateArray()
            .Single(i => i.GetProperty("evidenceId").GetString() == "e1");
        Assert.Equal(JsonValueKind.Null, old.GetProperty("codec").ValueKind);

        // ② 页面里有那段告知（藏在一个点了 H.265 才显示的元素里）。
        var html = await fixture.Client.GetStringAsync("/");

        Assert.Contains("codecNote", html, StringComparison.Ordinal);
        Assert.Contains("H.265", html, StringComparison.Ordinal);
        // ⚠️ 不许承诺「一定能播」—— 那是做不到的事。
        Assert.DoesNotContain("都能播放", html, StringComparison.Ordinal);
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
