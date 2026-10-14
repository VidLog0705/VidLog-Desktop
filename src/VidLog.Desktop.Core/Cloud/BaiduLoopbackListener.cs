using System.Net;
using System.Net.Sockets;
using System.Text;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Cloud;

/// <summary>回环回调接住的东西 —— 一个授权码，或者一个「用户拒绝了」。</summary>
/// <param name="Code">授权码。**它是凭据**，只在内存里过一手，不许落盘、不许进日志。</param>
/// <param name="Error">百度回的错误码（最常见的是 <c>access_denied</c> = 用户点了拒绝）。</param>
public sealed record BaiduAuthCallback(string? Code, string? Error);

/// <summary>
/// 登录期间在本机开一个回环端口，接百度授权页跳回来的那一次回调。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是 <see cref="TcpListener"/> 而不是 <c>HttpListener</c>：后者在非管理员账户下
/// 要先给这个地址登记一条 URL ACL（<c>netsh http add urlacl</c>），没登记就起不来 ——
/// 那是一条要走管理员权限才能铺平的安装步骤，而我们要的只是「接一个 GET」。
/// </para>
/// <para>
/// ⚠️ <b>放在 Core 而不是 App</b>：App 那个工程没有测试工程，放那边等于这一路没有闸。
/// </para>
/// </remarks>
public sealed class BaiduLoopbackListener : IAsyncDisposable
{
    /// <summary>
    /// 默认端口。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这个值是**与百度控制台登记的回调地址逐字绑死**的，不是随便挑的：
    /// 换端口 = 必须回控制台改，而且生效最长要等 1 小时（实测：19:55 改的、次日 09:54 才认）。
    /// ⚠️ 顺带一提，PackingProof 用的也是它（17895）—— 两个程序同时开着时后起来的那个绑不上，
    /// 所以绑不上必须报人话，见 <see cref="Start"/>。
    /// </remarks>
    public const int DefaultPort = 17895;

    private readonly string _expectedState;
    private readonly IAppLogger _logger;
    private readonly TcpListener _listener;
    private readonly int _port;

    private readonly TaskCompletionSource<BaiduAuthCallback> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? _cts;
    private Task? _accepting;

    public BaiduLoopbackListener(
        string expectedState, IAppLogger? logger = null, int port = DefaultPort)
    {
        _expectedState = expectedState;
        _logger = logger ?? NullLogger.Instance;
        _port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);

        // ⚠️ 上一次登录没退干净时端口可能还在 TIME_WAIT，这个选项让重试能起来。
        _listener.Server.SetSocketOption(
            SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        RedirectUri = $"http://127.0.0.1:{port}/callback";
    }

    /// <summary>
    /// 百度要跳回来的那一个地址。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>全仓唯一一处</b>：拼授权 URL（<see cref="BaiduPanClient.BuildAuthorizeUrl"/>）
    /// 与换令牌（<see cref="BaiduPanSession.LoginAsync"/>）**两处都读它**。
    /// 各拼一份的话，表现是换令牌时报 <c>redirect_uri mismatch</c> ——
    /// 一个看起来要回控制台查、实际一行代码就能修好的错。
    /// </remarks>
    public string RedirectUri { get; }

    /// <summary>真正绑上的端口。构造时传 0（测试用）时，实际端口只能从这里读。</summary>
    /// <remarks>⚠️ 必须在 <see cref="Start"/> 之后读 —— 之前它还没绑上。</remarks>
    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>开始听。</summary>
    /// <exception cref="InvalidOperationException">端口被别的程序占着（说人话的那种）。</exception>
    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.Log(LogLevel.Warn, "网盘", $"回环端口 {_port} 绑不上：{ex.Message}");

            // ⚠️ 甩一个 SocketException 出来，看的人只会去查网络 —— 而这里的原因是
            // 「本机另一个程序占着这个口号」，跟网络一点关系都没有。
            throw new InvalidOperationException(
                $"登录要用的本机端口 {_port} 被别的程序占着了"
                + "（PackingProof 用的是同一个端口）。把那个程序关掉再试一次。",
                ex);
        }

        _cts = new CancellationTokenSource();
        _accepting = Task.Run(() => AcceptAsync(_cts.Token));

        _logger.Log(LogLevel.Info, "网盘", $"回环监听已启动：{RedirectUri}");
    }

    /// <summary>
    /// 等那一次回调。
    /// </summary>
    /// <returns>等到了就返回接住的东西；超时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// ⚠️ 用户在内嵌窗里点了「拒绝」时**不是超时** —— 百度也会跳回来，只是带的是
    /// <c>error=access_denied</c>。两者在界面上要说成两句不同的话。
    /// </remarks>
    public async Task<BaiduAuthCallback?> WaitForCodeAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var finished = await Task.WhenAny(
            _result.Task, Task.Delay(timeout, cancellationToken));

        if (finished == _result.Task)
        {
            return await _result.Task;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ⚠️ 超时要留痕：探针期有过一次「同一个 AppKey、监听器一个回调都没接到」，
        // 根因至今没查明 —— 那次长得就跟这里一样。不记的话无从下手。
        _logger.Log(LogLevel.Warn, "网盘", $"等了 {timeout.TotalMinutes:F0} 分钟还没收到授权回调");

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener.Stop();

        if (_accepting is { } accepting)
        {
            try
            {
                await accepting;
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Warn, "网盘", $"回环监听收尾时出错：{ex.Message}");
            }
        }

        _cts?.Dispose();
        _cts = null;

        _logger.Log(LogLevel.Info, "网盘", "回环监听已停止");
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // ⚠️ 不静默，而且**不再继续转**：真的出错时继续 accept 有可能变成一个
                // 空转的死循环（socket 已经关了的话每次都是立刻失败）。
                _logger.Log(LogLevel.Warn, "网盘", $"回环监听接受连接时出错：{ex.Message}");
                break;
            }

            _ = HandleAsync(client, cancellationToken);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var target = RequestTarget(await ReadHeadAsync(stream, cancellationToken));

                // ⚠️ 每来一次连接都留痕（探针期那次「一个回调都没接到」的失败，
                // 判据只能是「到底有没有连接进来」）。记的是**抹掉查询串**的地址。
                _logger.Log(LogLevel.Debug, "网盘", $"回环收到一次连接：{Bare(target)}");

                // ⚠️ **state 要第一个校验，排在 `error` 前面**。这两位不是随便排的：
                // `error` 那一支会**结束这次等待**（界面据此马上告诉用户「没授权成功」），
                // 所以只要有一支能绕过 state，本机随便哪个进程发一次
                // `?error=access_denied`（**连码都不用猜**）就能把用户正在进行的登录顶掉。
                // 那正是这一条校验要挡的那件事 —— 排在后面等于没挡。
                //
                // 百度在「用户点了拒绝」时也会原样回传 state（RFC 6749 §4.1.2.1 要求如此），
                // 所以真的拒绝照样走得通。
                if (!string.Equals(Query(target, "state"), _expectedState, StringComparison.Ordinal))
                {
                    // ⚠️ 不匹配就**拒收，然后接着听**。直接停掉的话，本机随便哪个进程
                    // 发一次请求就能把这次登录顶掉（拒绝服务）—— 而它连码都不用猜对。
                    _logger.Log(LogLevel.Warn, "网盘", "回环收到一个 state 对不上的回调，已拒收（继续等下次）");

                    await RespondAsync(
                        stream, 400,
                        Page("这个请求对不上", "这次登录不是从 VidLog 发起的，已忽略。"),
                        cancellationToken);

                    return;
                }

                var error = Query(target, "error");
                if (error is { Length: > 0 })
                {
                    // 用户点了「拒绝」—— 这不是故障，是一条要如实说出来的路。
                    await RespondAsync(
                        stream, 200,
                        Page("这次没有授权成功", "你在百度那一页点了「拒绝」。回到 VidLog 可以再点一次【登录百度网盘】。"),
                        cancellationToken);

                    _result.TrySetResult(new BaiduAuthCallback(null, error));
                    return;
                }

                var code = Query(target, "code");
                if (code is not { Length: > 0 })
                {
                    await RespondAsync(
                        stream, 400,
                        Page("这个地址不认识", "它是给百度授权回调用的，直接打开没有意义。"),
                        cancellationToken);

                    return;
                }

                // ⚠️ 只记**长度**，一个字都不记：code 是凭据。
                // 探针期踩过一次 —— 那个 Redact 挡住了 client_id 却漏了 code=。
                _logger.Log(LogLevel.Info, "网盘", $"回环收到授权码（长度 {code.Length}）");

                await RespondAsync(
                    stream, 200,
                    Page("可以关掉这个页面了", "授权码已经收到，回到 VidLog 就能看到登录结果。"),
                    cancellationToken);

                _result.TrySetResult(new BaiduAuthCallback(code, null));
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Warn, "网盘", $"回环处理回调时出错：{ex.Message}");
            }
        }
    }

    /// <summary>请求行里的那个地址（<c>GET /callback?code=… HTTP/1.1</c> 中间那一截）。</summary>
    private static string RequestTarget(string head)
    {
        var parts = head.Split("\r\n")[0].Split(' ');

        return parts.Length > 1 ? parts[1] : string.Empty;
    }

    /// <summary>抹掉查询串 —— 授权码就在里面。</summary>
    private static string Bare(string target)
    {
        var q = target.IndexOf('?');

        return q < 0 ? target : target[..q] + "?…";
    }

    /// <summary>查询串里的一个参数。没有就是 <see langword="null"/>。</summary>
    private static string? Query(string target, string name)
    {
        var q = target.IndexOf('?');
        if (q < 0)
        {
            return null;
        }

        foreach (var pair in target[(q + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');

            // eq == 0 是「= 前面没名字」，不是我们要的那个参数。
            if (eq > 0 && pair[..eq] == name)
            {
                // ⚠️ `+` 在查询串里是空格（form-encoding），所以先还原再解码。
                return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }

        return null;
    }

    /// <summary>读到请求头结束（<c>\r\n\r\n</c>）为止。</summary>
    /// <remarks>
    /// ⚠️ 带上限：这个端口对**本机任何进程**开放，不设上限的话一个不怀好意的连接
    /// 就能把内存喂满。
    /// </remarks>
    private static async Task<string> ReadHeadAsync(
        NetworkStream stream, CancellationToken cancellationToken)
    {
        const int MaxBytes = 16 * 1024;

        var buffer = new List<byte>();
        var one = new byte[1];

        while (buffer.Count < MaxBytes)
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
            {
                break;
            }

            buffer.Add(one[0]);

            var n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == 13 && buffer[n - 3] == 10
                && buffer[n - 2] == 13 && buffer[n - 1] == 10)
            {
                break;
            }
        }

        return Encoding.ASCII.GetString([.. buffer]);
    }

    private static async Task RespondAsync(
        NetworkStream stream, int status, string html, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(html);
        var reason = status == 200 ? "OK" : "Bad Request";

        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string Page(string title, string body) =>
        "<!doctype html><meta charset=\"utf-8\">"
        + "<title>" + title + "</title>"
        + "<body style=\"font-family:system-ui,-apple-system,'Microsoft YaHei',sans-serif;"
        + "padding:48px;line-height:1.7;color:#1f2937\">"
        + "<h2 style=\"margin:0 0 12px\">" + title + "</h2>"
        + "<p style=\"margin:0;color:#4b5563\">" + body + "</p></body>";
}
