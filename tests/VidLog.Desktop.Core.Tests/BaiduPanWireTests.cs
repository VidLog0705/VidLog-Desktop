using System.Net;
using System.Text;
using VidLog.Desktop.Core.Cloud;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// <see cref="BaiduPanClient"/> 那一段「照文档翻译」的钉法：请求怎么发、回包怎么读。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>这一层过去一条测试都没有，而它正是出错的那一层。</b>
/// 上面那些流程（队列、对比、重试）有 <c>CloudUploadTests</c> 用假件跑着，
/// 但「真实报文长什么样」全靠读文档猜 —— 猜错的地方在上面一层是看不出来的：
/// 假件按猜出来的约定回，两边一起错，测试全绿。
/// </para>
/// <para>
/// 这里换掉 <see cref="HttpMessageHandler"/>，于是**不用出网**也能把
/// 「网盘回了什么、我们读成什么」钉死。带 ★ 的那几条是 2026-09-30
/// 拿真凭据打过真服务器之后补的。
/// </para>
/// </remarks>
public class BaiduPanWireTests
{
    private static readonly BaiduPanCredentials Credentials = new("key-1", "secret-1", null);

    // ─────────────────────────────────────────────
    // 设备码登录
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 用户还没授权时_轮询回的是等一下而不是异常()
    {
        // ⚠️ ★ 这一条是**打真服务器测出来的**（2026-09-30）：
        // 还没授权时网盘回的是 HTTP 400 + {"error":"authorization_pending",...}。
        //
        // 那个 400 会先在 ReadAsync 里被当成「请求失败」抛出去 ——
        // 于是 `PollDeviceTokenAsync` 里「还没批准就 return null」那句
        // **永远执行不到**，设备码登录的第一次轮询必然炸。
        // 而报出来的话是「HTTP 400」，看的人只会去查网络，查不到「其实只是还没点」。
        var stub = new Stub(
            """{"error":"authorization_pending","error_description":"User has not yet completed the authorization"}""",
            HttpStatusCode.BadRequest);

        Assert.Null(await Client(stub).PollDeviceTokenAsync("dev-1"));
    }

    [Fact]
    public async Task 轮询遇到真的错误_抛出来说的是原因()
    {
        // ⚠️ 与上一条**必须分开**：上一条是「等一等」，这一条是「别等了」。
        var stub = new Stub(
            """{"error":"invalid_client","error_description":"app not exist"}""",
            HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<BaiduPanException>(
            () => Client(stub).PollDeviceTokenAsync("dev-1"));

        Assert.Contains("invalid_client", error.Message);

        // 而且**不算**「凭据不管用了」：这是应用配错了，把用户丢去重新授权也修不好。
        Assert.False(error.IsCredentialProblem);
    }

    [Fact]
    public async Task 设备码那一步的兜底时间照实测的来()
    {
        // ★ 实测：网盘回 interval=5、expires_in=300。界面会照这个数告诉用户
        // 「这串码多久内有效」—— 兜底写成 1800 的话，用户按界面上的话去泡杯茶，
        // 回来只看到「授权失败」。
        var stub = new Stub(
            """{"device_code":"d1","user_code":"USER01","verification_url":"https://openapi.baidu.com/device"}""");

        var code = await Client(stub).StartDeviceLoginAsync();

        Assert.Equal(5, code.IntervalSeconds);
        Assert.Equal(300, code.ExpiresInSeconds);
    }

    // ─────────────────────────────────────────────
    // 续期
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 续期被拒且是授权没了_算凭据问题()
    {
        // 文档：refresh_token 是一次性的，且刷新失败时旧的那串**一起失效** ⇒ 只能重新授权。
        var stub = new Stub(
            """{"error":"invalid_grant","error_description":"refresh token expired"}""",
            HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<BaiduPanException>(
            () => Client(stub).RefreshAsync("r1"));

        Assert.True(error.IsCredentialProblem);
        Assert.Contains("重新登录", error.Message);
    }

    [Fact]
    public async Task 续期失败但其实是应用配错了_不算凭据问题()
    {
        // ⚠️ 这一头分开很要紧：AppKey/Secret 配错时，把用户丢去重新授权
        // 是**修不好**的（授权多少次都一样），而他只会以为是自己账号出了问题。
        var stub = new Stub(
            """{"error":"invalid_client","error_description":"app not exist"}""",
            HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<BaiduPanException>(
            () => Client(stub).RefreshAsync("r1"));

        Assert.False(error.IsCredentialProblem);
    }

    [Fact]
    public async Task 授权作废之后_本机登录信息就该没了()
    {
        // ⚠️ 不作废的后果是两件都在悄悄发生的事：
        // ① 界面一直写着「已登录」，用户以为自己登着，实际上什么都传不上去；
        // ② 定时检查每一分钟拿同一串死掉的 refresh_token 再问一次 ——
        //    而未过审的应用**每小时只有 10 次调用**（权限与配额），全喂给这个循环了。
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vidlog-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(8));
            var store = new BaiduPanTokenStore(System.IO.Path.Combine(dir, "token.json"));

            // 一个**已经过期**的令牌，逼下一次取值去续期。
            await store.SaveAsync(new BaiduLogin("old", "r1", now.AddMinutes(-1), "测试账号"));

            var stub = new Stub(
                """{"error":"invalid_grant","error_description":"refresh token expired"}""",
                HttpStatusCode.BadRequest);

            var session = new BaiduPanSession(Client(stub), store, now: () => now);
            await session.RestoreAsync();
            Assert.True(session.IsLoggedIn);

            await Assert.ThrowsAsync<BaiduPanException>(() => session.TokenAsync());

            Assert.False(session.IsLoggedIn);
            Assert.False(File.Exists(store.Path), "本机那串已经作废的令牌要删掉");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────
    // 上传三阶段
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 预创建回的序号是还要传的_不是已经有的()
    {
        // ⚠️ 文档 018 响应参数表原话：「需要上传的分片序号列表，索引从 0 开始」。
        // 反着读的后果不是慢一点，是整个传不上去。
        var stub = new Stub("""{"errno":0,"uploadid":"u1","block_list":[0,1,2]}""");

        var pre = await Client(stub)
            .PrecreateAsync("t", "/apps/VidLog/a.mp4", 12, ["a", "b", "c"]);

        Assert.Equal("u1", pre.UploadId);
        Assert.Equal(new[] { 0, 1, 2 }, pre.Pending.OrderBy(i => i));
    }

    [Fact]
    public async Task 预创建回空的序号_等价于要传第0片()
    {
        // 文档 018 那个小标题的原话就是「block_list 为空时等价于 [0]」——
        // 空数组不是「一片都不用传」。
        var stub = new Stub("""{"errno":0,"uploadid":"u1","block_list":[]}""");

        var pre = await Client(stub).PrecreateAsync("t", "/apps/VidLog/a.mp4", 10, ["a"]);

        Assert.Equal(new[] { 0 }, pre.Pending.OrderBy(i => i));
    }

    [Fact]
    public async Task 取上传域名优先挑https()
    {
        // 文档 016：「从 servers 中选择 HTTPS 地址，并保留协议与主机名」。
        // 挑到 http 的那一个就是明文传整段隐私画面。
        var stub = new Stub(
            """{"error_code":0,"servers":[{"server":"http://c2.pcs.baidu.com"},{"server":"https://c3.pcs.baidu.com"}]}""");

        Assert.Equal(
            "https://c3.pcs.baidu.com",
            await Client(stub).LocateUploadAsync("t", "/apps/VidLog/a.mp4", "u1"));
    }

    [Fact]
    public async Task 取上传域名失败时按error_code判而不是errno()
    {
        // ⚠️ 016 的成功判据是 `error_code`，不是 `errno`（这套文档里只有两处是这样，
        // 另一处是 058 获取用户身份信息）。只看 errno 的话，一个失败的取域名
        // 会被当成成功，然后拿一个空域名去传分片。
        var stub = new Stub("""{"error_code":31045,"error_msg":"access_token invalid"}""");

        var error = await Assert.ThrowsAsync<BaiduPanException>(
            () => Client(stub).LocateUploadAsync("t", "/apps/VidLog/a.mp4", "u1"));

        Assert.Equal(31045, error.Errno);
        Assert.True(error.IsCredentialProblem);
    }

    [Fact]
    public async Task 分片回的是JSON_里面errno非0要抛()
    {
        // ⚠️ 它**不是**一个裸的 md5 字符串（015 的响应参数表写的是 md5/request_id/errno）。
        // 只看 HTTP 状态码的话，一个 200 配着 errno=31363（分片缺失）会被当成传成功 ——
        // 一路走到 create 才炸，而那时报的错指不回「其实是这一片没上去」。
        var stub = new Stub("""{"errno":31363,"request_id":1}""");

        var error = await Assert.ThrowsAsync<BaiduPanException>(
            () => Client(stub).UploadSliceAsync(
                "t", "https://c3.pcs.baidu.com", "/apps/VidLog/a.mp4", "u1", 0,
                new MemoryStream(new byte[4])));

        Assert.Equal(31363, error.Errno);
        Assert.Contains("分片缺失", error.Message);
    }

    // ─────────────────────────────────────────────
    // 列目录
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 列目录不看has_more也能把第二页取回来()
    {
        // ⚠️ 053「获取文件列表」的响应参数表里**没有 has_more 这个字段**
        // （有它的是 048 搜索与 057 递归列目录）。只信它的话，一个上千条的目录
        // 会被**静默**截到第一页 —— 而回查归档层（I8）会因此说「云端没有这一条」，
        // 接着把本机那份重传一遍。
        var first = new StringBuilder("""{"errno":0,"list":[""");

        for (var index = 0; index < 1000; index++)
        {
            if (index > 0)
            {
                first.Append(',');
            }

            first.Append($$"""{"path":"/apps/VidLog/2026/09/30/发货/f{{index}}.mp4"}""");
        }

        first.Append("]}");

        const string Second =
            """{"errno":0,"list":[{"path":"/apps/VidLog/2026/09/30/发货/last.mp4"}]}""";

        // 第一页装满了 1000 条（没有 has_more），第二页只有 1 条。
        var stub = new Stub(request => Json(
            request.RequestUri!.Query.Contains("start=0", StringComparison.Ordinal)
                ? first.ToString()
                : Second));

        var names = await Client(stub)
            .ListFilesAsync("t", "/apps/VidLog/2026/09/30/发货/");

        Assert.Equal(1001, names.Count);
        Assert.Contains("last.mp4", names);
    }

    // ─────────────────────────────────────────────
    // 请求头
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 每个请求都带UserAgent()
    {
        // 文档里每个示例都带它；019 下载的 header 表把它标成**必填**，
        // 而 31326「命中防盗链」的排查方向写的就是「User-Agent 请求头是否正常」。
        var stub = new Stub("""{"errno":0,"uploadid":"u1","block_list":[0]}""");

        await Client(stub).PrecreateAsync("t", "/apps/VidLog/a.mp4", 10, ["a"]);
        await Client(stub).GetDisplayNameAsync("t");

        Assert.Equal(2, stub.UserAgents.Count);
        Assert.All(stub.UserAgents, agent => Assert.Equal("pan.baidu.com", agent));
    }

    // ─────────────────────────────────────────────
    // 夹具
    // ─────────────────────────────────────────────

    private static BaiduPanClient Client(HttpMessageHandler handler) =>
        new(Credentials, new HttpClient(handler));

    private static HttpResponseMessage Json(
        string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>一个照剧本回包的假 HTTP 层。<b>不出网。</b></summary>
    private sealed class Stub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public Stub(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            _respond = _ => Json(body, status);

        /// <summary>每个请求带的 User-Agent（请求对象会被释放，所以在这里就抄下来）。</summary>
        public List<string?> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 读原始值而不是 `Headers.UserAgent`：后者要能被解析成
            // ProductInfoHeaderValue 才有内容，解析不了时是空的 ——
            // 那会让「忘了带这个头」和「带了但格式怪」看起来一样。
            UserAgents.Add(request.Headers.TryGetValues("User-Agent", out var values)
                ? string.Join(" ", values)
                : null);

            return Task.FromResult(_respond(request));
        }
    }
}
