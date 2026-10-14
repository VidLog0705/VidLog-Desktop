using System.Net;
using System.Net.Sockets;
using System.Text;
using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 回环回调那一段：接住百度的授权码，别把别的东西也接住。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>这几条用的是真 socket，不是假件</b>（<c>TcpListener</c> 不需要 URL ACL，
/// 在测试进程里开得起来）。这一段的风险全在「真的连上了之后会发生什么」——
/// 用假件把那一段绕过去，等于什么都没测。
/// </para>
/// <para>
/// ⚠️ 每条都用 <c>port: 0</c>（让系统挑一个空闲端口）再从
/// <see cref="BaiduLoopbackListener.BoundPort"/> 读回来 —— **不许写死 17895**：
/// 那是产品用的端口，跑测试时本机多半正开着 VidLog（或者 PackingProof），
/// 写死就是「本机随机红、CI 上因为没装反而绿」。
/// </para>
/// </remarks>
public class BaiduLoopbackListenerTests
{
    /// <summary>等一小会儿 —— 这些都是本机回环，用不了几十毫秒。</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task 收到对的回调_拿到授权码_而且回的是200()
    {
        await using var listener = new BaiduLoopbackListener("state-ok", port: 0);
        listener.Start();

        var waiting = listener.WaitForCodeAsync(Patience);

        var response = await SendAsync(listener.BoundPort, "/callback?code=ABC123&state=state-ok");

        Assert.StartsWith("HTTP/1.1 200", response, StringComparison.Ordinal);

        var callback = await waiting;
        Assert.NotNull(callback);
        Assert.Equal("ABC123", callback.Code);
        Assert.Null(callback.Error);
    }

    [Fact]
    public async Task 用户点了拒绝_回的是error不是超时()
    {
        // ⚠️ 这两件事在界面上要说成两句**不同**的话：「你点了拒绝」要能马上再点一次，
        // 「等了五分钟没动静」是另一回事。混成一个的话，用户点完拒绝还要对着
        // 一个转圈的窗口等满五分钟。
        await using var listener = new BaiduLoopbackListener("state-ok", port: 0);
        listener.Start();

        var waiting = listener.WaitForCodeAsync(Patience);

        await SendAsync(listener.BoundPort, "/callback?error=access_denied&state=state-ok");

        var callback = await waiting;
        Assert.NotNull(callback);
        Assert.Null(callback.Code);
        Assert.Equal("access_denied", callback.Error);
    }

    [Fact]
    public async Task state对不上_拒收_而且接着等下一次()
    {
        // ⚠️ **这一条是安全项**（改造清单第 22 条）。没有它，本机随便哪个进程
        // 都能往那个端口发一次请求，把**它自己的**百度账号绑到用户的 VidLog 上
        // （登录 CSRF）—— 而且连码都不用猜对。
        //
        // ⚠️ 拒收之后**必须接着听**，不能就此收摊：直接停掉的话，上面那件事
        // 就变成「谁都能把这次登录顶掉」（一个连码都不用知道的拒绝服务）。
        await using var listener = new BaiduLoopbackListener("state-ok", port: 0);
        listener.Start();

        var waiting = listener.WaitForCodeAsync(Patience);

        var rejected = await SendAsync(listener.BoundPort, "/callback?code=ATTACKER&state=state-bad");

        Assert.StartsWith("HTTP/1.1 400", rejected, StringComparison.Ordinal);

        // 还在听：这一次才是真的要接的那个。
        var accepted = await SendAsync(listener.BoundPort, "/callback?code=REAL&state=state-ok");

        Assert.StartsWith("HTTP/1.1 200", accepted, StringComparison.Ordinal);

        var callback = await waiting;
        Assert.Equal("REAL", callback?.Code);
    }

    [Fact]
    public async Task 没有授权码的那一次请求_回400()
    {
        // 直接拿浏览器打开 `http://127.0.0.1:<端口>/callback` 就长这样。
        // 回 200 的话，用户会看到一个空白页而不知道发生了什么。
        await using var listener = new BaiduLoopbackListener("state-ok", port: 0);
        listener.Start();

        var waiting = listener.WaitForCodeAsync(Patience);

        var response = await SendAsync(listener.BoundPort, "/callback?state=state-ok");

        Assert.StartsWith("HTTP/1.1 400", response, StringComparison.Ordinal);

        // 而且**没有把它当成接住了**：等待那头仍然挂着。
        Assert.False(waiting.IsCompleted);
    }

    [Fact]
    public async Task 授权码原文一个字都不许进日志()
    {
        // ⚠️ 探针期踩过一次：那层抹敏挡住了 `client_id` 却漏了 `code=`。
        // 授权码是**凭据** —— 十分钟内谁拿到都能换出一对令牌。
        var logger = new CapturingLogger();

        await using var listener = new BaiduLoopbackListener("state-ok", logger, port: 0);
        listener.Start();

        var waiting = listener.WaitForCodeAsync(Patience);
        await SendAsync(listener.BoundPort, "/callback?code=THE-SECRET-CODE&state=state-ok");
        await waiting;

        var joined = string.Join("\n", logger.Entries.Select(e => e.Message));

        Assert.DoesNotContain("THE-SECRET-CODE", joined, StringComparison.Ordinal);

        // ⚠️ 但也不能什么都不记：那一次「同一个 AppKey、一个回调都没接到」的失败
        // 至今没查明根因，判据只能是「到底有没有连接进来」。所以**长度**要记。
        Assert.Contains("长度 15", joined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task state也不许进日志_它也是一次性的凭据()
    {
        var logger = new CapturingLogger();

        await using var listener = new BaiduLoopbackListener("THE-STATE", logger, port: 0);
        listener.Start();

        var waiting = listener.WaitForCodeAsync(Patience);
        await SendAsync(listener.BoundPort, "/callback?code=ABC&state=THE-STATE");
        await waiting;

        Assert.DoesNotContain(
            "THE-STATE",
            string.Join("\n", logger.Entries.Select(e => e.Message)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 等不到就是等不到_超时返回空()
    {
        await using var listener = new BaiduLoopbackListener("state-ok", port: 0);
        listener.Start();

        Assert.Null(await listener.WaitForCodeAsync(TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public async Task error也要过state那一关_不然谁都能把这次登录顶掉()
    {
        // ⚠️ **上面那条的一个孪生洞**：`error` 那一支会**结束这次等待**，
        // 界面据此马上跟用户说「没授权成功」。所以只要它能绕过 state，
        // 本机随便哪个进程发一次 `?error=access_denied`（**连码都不用猜**）
        // 就能把用户正在进行的登录顶掉。
        await using var listener = new BaiduLoopbackListener("state-ok", port: 0);
        listener.Start();

        var waiting = listener.WaitForCodeAsync(Patience);

        var rejected = await SendAsync(
            listener.BoundPort, "/callback?error=access_denied&state=state-bad");

        Assert.StartsWith("HTTP/1.1 400", rejected, StringComparison.Ordinal);

        // 还在听 —— 上面那一次不算数。
        Assert.False(waiting.IsCompleted);
    }

    [Fact]
    public void 端口被占着时_抛的是人话不是SocketException()
    {
        // ⚠️ 甩一个 `SocketException` 出来，看的人只会去查网络 —— 而这里的原因是
        // 「本机另一个程序占着这个口号」，跟网络一点关系都没有。
        // 而真撞上这件事的概率一点也不低：**PackingProof 用的就是同一个端口**。
        var squatter = new TcpListener(IPAddress.Loopback, 0);

        squatter.Start();

        try
        {
            var port = ((IPEndPoint)squatter.LocalEndpoint).Port;
            var listener = new BaiduLoopbackListener("state-ok", port: port);

            var error = Assert.Throws<InvalidOperationException>(listener.Start);

            Assert.Contains("被别的程序占着", error.Message, StringComparison.Ordinal);
            Assert.Contains("PackingProof", error.Message, StringComparison.Ordinal);
            Assert.IsType<SocketException>(error.InnerException);
        }
        finally
        {
            squatter.Stop();
        }
    }

    [Fact]
    public async Task 收工之后端口要放掉()
    {
        // 不放掉的后果是**下一次登录绑不上**（而报的是「被别的程序占着」，
        // 用户会去找那个根本不存在的程序）。
        int port;

        await using (var listener = new BaiduLoopbackListener("state-ok", port: 0))
        {
            listener.Start();
            port = listener.BoundPort;
        }

        var again = new TcpListener(IPAddress.Loopback, port);
        again.Start();
        again.Stop();
    }

    /// <summary>
    /// 往那个回环端口发一次请求，把响应**原文**拿回来。
    /// </summary>
    /// <remarks>
    /// 手写 HTTP 而不是用 <c>HttpClient</c>：要发的是**一个坏的请求**
    /// （code 对但 state 不对、压根没有 code），而 <c>HttpClient</c> 会把
    /// 响应的状态码抛成异常，反而不好断言。
    /// </remarks>
    private static async Task<string> SendAsync(int port, string target)
    {
        using var client = new TcpClient();

        await client.ConnectAsync(IPAddress.Loopback, port);

        using var stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET {target} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n"));

        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.UTF8);

        return await reader.ReadToEndAsync();
    }

    private sealed class CapturingLogger : IAppLogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void Log(LogLevel level, string category, string message) =>
            Entries.Add((level, message));

        public void Log(
            LogLevel level, string category, string message, IReadOnlyDictionary<string, object?> data) =>
            Entries.Add((level, message));
    }
}
