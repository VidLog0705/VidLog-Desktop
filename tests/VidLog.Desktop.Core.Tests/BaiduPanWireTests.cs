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
    // 授权码登录（2026-10-14 起；设备码那一套已经删了）
    // ─────────────────────────────────────────────

    [Fact]
    public void 授权链接里那几个参数一个都不能少_而且回调地址要编码过()
    {
        var url = BaiduPanClient.BuildAuthorizeUrl(
            "key-1", "http://127.0.0.1:17895/callback", "abc123");

        Assert.StartsWith(
            "https://openapi.baidu.com/oauth/2.0/authorize?", url, StringComparison.Ordinal);

        Assert.Contains("response_type=code", url, StringComparison.Ordinal);
        Assert.Contains("client_id=key-1", url, StringComparison.Ordinal);
        Assert.Contains("scope=basic,netdisk", url, StringComparison.Ordinal);
        Assert.Contains("state=abc123", url, StringComparison.Ordinal);

        // ⚠️ 回调地址**必须编码过**：它自己带着 `:` 与 `/`。不编码的话
        // 对端收到的是一个被截断的 `http://127.0.0.1:17895/callback`——
        // 而报出来的错是「回调地址与控制台登记的对不上」，看的人会去后台改一个本来就对的地址。
        Assert.Contains(
            "redirect_uri=" + Uri.EscapeDataString("http://127.0.0.1:17895/callback"),
            url,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 登录页开的是passport_不是直接把授权页丢给用户()
    {
        // ⚠️ ★ 这一条是**探针实测出来的形状**（2026-10-14，53.2 秒拿到 32 位授权码）：
        // 直接开 `openapi…/authorize` 的话，用户在那一页**没有密码登录的入口**
        // （那一页只给已登录的人点「同意」）。走 passport 才把扫码 / 微信 / 微博 /
        // QQ / 密码这些登录方式都摆出来。
        var authorize = BaiduPanClient.BuildAuthorizeUrl(
            "key-1", "http://127.0.0.1:17895/callback", "abc123");

        var login = BaiduPanClient.BuildPassportLoginUrl(authorize);

        const string Prefix = "https://passport.baidu.com/v2/?login&u=";

        Assert.StartsWith(Prefix, login, StringComparison.Ordinal);

        // ⚠️ 整个授权 URL 是**当成一个参数值**编码进去的。不编码的话，它自己那些
        // `&` 会把 passport 页的查询串截断 —— 表现是登录完了跳回来少带几个参数，
        // 而那一页不会报错。
        Assert.Equal(authorize, Uri.UnescapeDataString(login[Prefix.Length..]));
    }

    [Fact]
    public async Task 拼授权URL用的回调地址_与换令牌送出去的必须是同一个()
    {
        // ⚠️ 改造清单第 23 条。
        //
        // 两处各拼一份的后果不是「慢一点」：换令牌时百度会直接拒，报的是
        // `redirect_uri` 不匹配 —— 一条**看起来要回开放平台后台查**、
        // 实际一行代码就能修好的错（而那后台改完最长要等 1 小时才生效）。
        //
        // ⚠️ 这条钉子钉住的是 **Core 这一半**：两个方法都逐字用它收到的那一个值，
        // 谁也没自己去拼一个。**界面那一半钉不住**（App 层没有测试工程）——
        // 它靠的是「`BaiduLoopbackListener.RedirectUri` 是全仓唯一一处拼这个地址的地方，
        // 两处调用都传它」，见 `SettingsWindow.OnCloudLogin` 里那段注释。
        var listener = new BaiduLoopbackListener("state-1", port: 0);
        var redirect = listener.RedirectUri;

        var authorize = BaiduPanClient.BuildAuthorizeUrl("key-1", redirect, "state-1");
        var encoded = "redirect_uri=" + Uri.EscapeDataString(redirect);

        Assert.Contains(encoded, authorize, StringComparison.Ordinal);

        var stub = new Stub("""{"access_token":"a1","refresh_token":"r1","expires_in":1}""");

        await Client(stub).ExchangeCodeAsync("the-code", redirect);

        Assert.Single(stub.Urls);
        Assert.Contains(encoded, stub.Urls[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 拿授权码换令牌_成功()
    {
        var stub = new Stub(
            """{"access_token":"a1","refresh_token":"r1","expires_in":2592000}""");

        var token = await Client(stub)
            .ExchangeCodeAsync("the-code", "http://127.0.0.1:17895/callback");

        Assert.Equal("a1", token.AccessToken);
        Assert.Equal("r1", token.RefreshToken);
        Assert.Equal(2592000, token.ExpiresInSeconds);
    }

    [Fact]
    public async Task 拿授权码换令牌_百度回错误_要把原因翻成人话()
    {
        // ⚠️ 授权码模式下的失败**几乎都是这几个**：码用过了、码过期了、控制台里
        // 那条回调地址对不上。而这几个错原文都是英文的 error 码 ——
        // 照着显示的话，用户看到的是「invalid_grant」，查不出下一步该干什么。
        var stub = new Stub(
            """{"error":"invalid_grant","error_description":"code has been used"}""",
            HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<BaiduPanException>(
            () => Client(stub).ExchangeCodeAsync("the-code", "http://127.0.0.1:17895/callback"));

        Assert.Contains("invalid_grant", error.Message);
        // 「十分钟」「一次性」这两个事实要说出来 —— 它决定了「能不能再点一次」。
        Assert.Contains("一次性", error.Message);
        Assert.False(error.IsCredentialProblem);
    }

    [Fact]
    public async Task 拿授权码换令牌_回调地址对不上时要说清是回后台改()
    {
        // ⚠️ 这个错的**真实原因**十有八九是「换令牌时送的那一个地址，与拼授权 URL
        // 时用的那一个不是同一个」—— 一行代码就能修。但不去说这一句的话，
        // 看的人会跑回百度开放平台后台去改一个本来就对的地址（而且改完要等最长 1 小时生效）。
        var stub = new Stub(
            """{"error":"redirect_uri_mismatch","error_description":"redirect uri not match"}""",
            HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<BaiduPanException>(
            () => Client(stub).ExchangeCodeAsync("the-code", "http://127.0.0.1:17895/callback"));

        Assert.Contains("回调地址", error.Message);
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
    // 建目录（020）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 建目录照020_isdir为一_不传大小与分片_且已存在算成功()
    {
        var stub = new Stub("""{"errno":0,"isdir":1,"path":"/apps/VidLog/2026"}""");

        await Client(stub).CreateDirectoryAsync("t", "/apps/VidLog/2026");

        var body = Assert.Single(stub.FormBodies);

        Assert.Contains("isdir=1", body);

        // ⚠️ 目录的冲突策略与文件**不是一套**（020 vs 018）：目录只有
        // 「0 冲突时失败 / 1 冲突时重命名」，没有覆盖这一档。
        // 写成 1 会在网盘上堆出 `发货(1)`、`发货(2)`，而回查是按路径找的（I8）——
        // 那些目录里的文件会被判成「云端没有」，于是本机那份永远不敢删。
        Assert.Contains("rtype=0", body);

        // ⚠️ 020 注意事项逐字：创建文件夹**不要传入** size、block_list 和 uploadid。
        Assert.DoesNotContain("size=", body);
        Assert.DoesNotContain("block_list", body);
        Assert.DoesNotContain("uploadid", body);
    }

    [Fact]
    public async Task 建目录时撞上已存在不算失败()
    {
        // -8「文件或目录已存在」（063/020）。建目录是**幂等**的，这就是要的结果；
        // 不吞掉的话，往一个用过的日期目录里传第二条录像会整条失败。
        var stub = new Stub("""{"errno":-8}""");

        await Client(stub).CreateDirectoryAsync("t", "/apps/VidLog/2026");
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

        /// <summary>表单请求的**原文**（只抄表单那一类，分片是多部分体、不碰它）。</summary>
        public List<string> FormBodies { get; } = [];

        /// <summary>
        /// 每个请求的地址（**含查询串**）。
        /// </summary>
        /// <remarks>
        /// ⚠️ 只在测试里比字符串用，**别往测试输出或日志里打**：授权那几条的查询串里
        /// 带着 `client_id` 与授权码。
        /// </remarks>
        public List<string> Urls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri?.ToString() ?? string.Empty);

            // 读原始值而不是 `Headers.UserAgent`：后者要能被解析成
            // ProductInfoHeaderValue 才有内容，解析不了时是空的 ——
            // 那会让「忘了带这个头」和「带了但格式怪」看起来一样。
            UserAgents.Add(request.Headers.TryGetValues("User-Agent", out var values)
                ? string.Join(" ", values)
                : null);

            if (request.Content is FormUrlEncodedContent)
            {
                FormBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return _respond(request);
        }
    }
}
