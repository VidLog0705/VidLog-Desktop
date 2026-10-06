using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Import;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Live;
using VidLog.Desktop.Core.Playback;
using VidLog.Desktop.Core.Search;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Web;

public sealed partial class PlaybackServer : IAsyncDisposable
{

    // ───────────── /api/v1 · 手机端上传（M5） ─────────────

    /// <summary>
    /// M5 的上传与入网接口。形状见母仓 <c>docs/05-上传接口形状.md</c>。
    /// </summary>
    /// <remarks>
    /// 老的三条 GET 路由（<c>/api/search</c> 等）**原样不动** —— 它们服务的网页回放已经验收过，
    /// 「不动它」比「统一它」便宜。版本前缀 <c>v1</c> 只加在新接口上。
    /// </remarks>
    private async Task HandleV1Async(HttpListenerContext context, string path)
    {
        var isPost = string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase);

        if (path == "/api/v1/health" && !isPost)
        {
            await WriteJsonAsync(
                context,
                new HealthPayload(HealthPayload.ServiceName, HealthPayload.Version, _deviceName));
            return;
        }

        if (!isPost)
        {
            await WriteErrorAsync(context, 404, UploadErrors.NotFound, "这个路径只接受 POST");
            return;
        }

        switch (path)
        {
            case "/api/v1/enroll/request":
                await GuardAsync(context, () => HandleEnrollRequestAsync(context));
                return;

            case "/api/v1/enroll/claim":
                await GuardAsync(context, () => HandleEnrollClaimAsync(context));
                return;
        }

        // 入网之外一律要凭据。
        var auth = await AuthenticateAsync(context);
        if (auth is null)
        {
            await WriteErrorAsync(context, 401, UploadErrors.BadCredential, "凭据无效，请在手机端重新配对电脑");
            return;
        }

        // 取成局部量再进 lambda：可空元组在里面不会被收窄，而设备身份**必须**来自凭据。
        var deviceId = auth.Value.Device.DeviceId;
        var credential = auth.Value.Credential;

        switch (path)
        {
            case "/api/v1/upload/probe":
                await GuardAsync(context, async () =>
                {
                    var request = await ReadJsonAsync<ProbeRequest>(context);
                    if (request is null)
                    {
                        await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "请求体为空");
                        return;
                    }

                    await WriteJsonAsync(context, await _upload.ProbeAsync(request));
                });
                return;

            case "/api/v1/upload/commit":
                await GuardAsync(context, async () =>
                {
                    var request = await ReadJsonAsync<CommitRequest>(context);
                    if (request is null)
                    {
                        await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "请求体为空");
                        return;
                    }

                    await WriteJsonAsync(context, await _upload.CommitAsync(request, deviceId, credential));
                });
                return;

            case "/api/v1/enroll/rename":
                // 改名（规格 §3.4.5 ③）—— **要凭据**，所以它在上面那道闸之后。
                //
                // ⚠️ `deviceId` 用的是**凭据反查**出来的那个（上面那个局部量），
                // 不是报文里自称的：报文里根本没有 deviceId（见
                // `RenameRequestPayload` 的说明）。信自称的话，任何一台已入网
                // 设备都能改**别人**的名字。
                await GuardAsync(context, () => HandleRenameAsync(context, deviceId, credential));
                return;

            case "/api/v1/live/announce":
                // 手机报到它的实时推流地址（规格 §3.8 的机位发现）。
                //
                // ⚠️ `deviceId` 用的是**凭据反查**出来的那个（上面那个局部量），
                // 地址用的是**请求的来源地址** —— 两样都不许客户端自称，理由见
                // `LiveAnnouncePayload` 的说明。它在这道闸之后，是因为
                // 「哪台手机现在能看」本身就是一份机位名单。
                await GuardAsync(context, () => HandleLiveAnnounceAsync(context, deviceId));
                return;

            case "/api/v1/netdisk/token":
                // 把百度网盘的令牌**借**给手机。手机端不自己登录 —— 它拿不到 AppSecret
                // （见 `NetdiskGrant` 的类注释）。**要凭据**，所以它在这道闸之后。
                //
                // ⚠️ 这一条的份量比别的路由重：拿到令牌的手机能读写这个账号下
                // `/apps/<应用名>/` 里的全部东西。它够格拿，是因为入网那一步
                // **有人在电脑上点过同意**（规格 §3.4.5 ②）。
                // ⚠️ 别把这一条挪到凭据闸前面 —— 那等于对局域网里任何人开放。
                await GuardAsync(context, async () =>
                {
                    await WriteJsonAsync(
                        context,
                        _netdisk is null
                            ? NetdiskGrant.Off(
                                "电脑端这边网盘这一档没启用 —— 归档层不是「百度网盘」，"
                                + "或者还没配应用凭据。去电脑上「设置 → 存储与备份」看一眼。")
                            : await _netdisk.GrantAsync());
                });
                return;

            case "/api/v1/archive/verify":
                await GuardAsync(context, async () =>
                {
                    var request = await ReadJsonAsync<VerifyRequest>(context);
                    if (request is null || string.IsNullOrWhiteSpace(request.Location))
                    {
                        await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "缺少 location");
                        return;
                    }

                    await WriteJsonAsync(context, await VerifyAsync(request.Location));
                });
                return;
        }

        if (path.StartsWith("/api/v1/upload/chunk/", StringComparison.Ordinal))
        {
            await GuardAsync(context, () => HandleChunkAsync(context, path["/api/v1/upload/chunk/".Length..]));
            return;
        }

        await WriteErrorAsync(context, 404, UploadErrors.NotFound, $"电脑端不认识这个路径：{path}");
    }

    /// <summary>
    /// 回查归档层：这一份还在不在（规格 §3.5.4）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 手机端**手动删除**的前置闸就是它（§3.5.6③）：用户点删除 → 手机端问电脑端
    /// 「你那份还在吗」→ **查不到或查不了都绝不删**。
    /// </para>
    /// <para>
    /// ⚠️ <b>「查不了」必须与「不存在」分开报。</b>
    /// 对用户是两句话（「归档层上那份被删了」vs「现在问不到」），
    /// 而两者都导致不删 —— 把「查不了」当成「不存在」，删掉的可能就是最后一份（I2）。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 某一段的缩略图（规格 §3.4.3）。**没有就回 404** —— 页面显示占位方块。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这里**不现抽**：抽帧要跑 ffmpeg（几百毫秒），而这是浏览器在渲染列表时
    /// 挨个发的请求。抽帧发生在**用户上一次看过这一条**的时候（或者第一次点开时
    /// 由另一条路补上），这里只负责把已经抽好的那张端出去。
    /// </remarks>
    private async Task WriteThumbnailAsync(
        HttpListenerContext context, Media.ThumbnailCache thumbnails, string evidenceId)
    {
        var id = Uri.UnescapeDataString(evidenceId);
        var path = thumbnails.PathFor(id);

        if (!File.Exists(path))
        {
            context.Response.StatusCode = 404;
            return;
        }

        // 缩略图很小，用不上 Range（原来的整段写文件那条路是给视频的）。
        context.Response.ContentType = "image/jpeg";
        context.Response.ContentLength64 = new FileInfo(path).Length;

        await using var stream = File.OpenRead(path);
        await stream.CopyToAsync(context.Response.OutputStream);
    }

    private async Task<VerifyPayload> VerifyAsync(string location)
    {
        var backend = _options.ArchiveBackend
            ?? new DirectoryArchiveBackend(_options.ArchiveRoot, ArchiveBackendKind.LocalDisk);

        RelativePath relative;
        try
        {
            // ⚠️ `RelativePath.Parse` 会拒绝绝对路径、UNC 与 `..` 越级 ——
            // 这条接口是**远端**调的，那个校验在这里就是安全边界。
            relative = RelativePath.Parse(location);
        }
        catch (Exception ex)
        {
            return new VerifyPayload(false, true, $"这个路径不合规：{ex.Message}");
        }

        var result = await backend.VerifyAsync(relative);

        return new VerifyPayload(result.Exists, result.CouldNotVerify, result.FailureReason);
    }

    private async Task HandleEnrollRequestAsync(HttpListenerContext context)
    {
        var request = await ReadJsonAsync<EnrollRequestPayload>(context);
        if (request is null)
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "请求体为空");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.DeviceName))
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "deviceId / deviceName 不得为空");
            return;
        }

        // ⚠️ **不自动批准**（规格 §3.4.5：入网必须经主机端人工批准）。
        // 这里只记下"谁来了"，并**顺带告诉它现在批没批** ——
        // 手机轮询的就是这个接口，它**只报警不发货**（凭据在 claim 那一步才产出）。
        var result = await _devices.RequestAsync(request.DeviceId, request.DeviceName, request.Token);

        switch (result.Status)
        {
            case EnrollStatus.Pending:
                await WriteJsonAsync(context, new EnrollPendingPayload(EnrollPendingPayload.Pending));
                return;

            case EnrollStatus.Approved:
                await WriteJsonAsync(context, new EnrollPendingPayload(EnrollPendingPayload.Approved));
                return;

            case EnrollStatus.Rejected:
                await WriteJsonAsync(context, new EnrollPendingPayload(EnrollPendingPayload.Rejected));
                return;

            case EnrollStatus.BadToken:
                await WriteErrorAsync(context, 403, UploadErrors.BadToken, result.Detail);
                return;

            case EnrollStatus.SeatLimitExceeded:
                // 机位满了。**不是「请求坏了」**：这台手机本身没问题，
                // 是电脑端手上的激活码不够。403 + 自己的码，手机那边照着
                // `seat_limit` 说「去电脑端激活或升级」，而不是「再试一次」。
                await WriteErrorAsync(context, 403, UploadErrors.SeatLimit, result.Detail);
                return;

            default:
                // 屏幕上的码换了、或者已经超时 —— 手机该重新扫一次。
                await WriteErrorAsync(context, 410, UploadErrors.NoPendingRequest, result.Detail);
                return;
        }
    }

    /// <summary>
    /// 改名（规格 §3.4.5 ③）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="HandleEnrollRequestAsync"/> **同形**：手机轮询它，
    /// 一次调用既报上新名字、也问批没批（三个状态都可能回来）。
    /// </para>
    /// <para>
    /// ⚠️ 「还没批 / 被拒了」回 **200 + status**，不是 4xx —— 理由与入网那条一字不差：
    /// 那不是「请求坏了」，是「人还没做决定 / 人做了个决定」。
    /// </para>
    /// </remarks>
    private async Task HandleRenameAsync(
        HttpListenerContext context, string deviceId, string credential)
    {
        var request = await ReadJsonAsync<RenameRequestPayload>(context);

        if (request is null)
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "请求体为空");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.DeviceName))
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "deviceName 不得为空");
            return;
        }

        var result = await _devices.RequestRenameAsync(deviceId, request.DeviceName, credential);

        switch (result.Status)
        {
            case EnrollStatus.Pending:
                await WriteJsonAsync(context, new EnrollPendingPayload(EnrollPendingPayload.Pending));
                return;

            case EnrollStatus.Approved:
                await WriteJsonAsync(context, new EnrollPendingPayload(EnrollPendingPayload.Approved));
                return;

            case EnrollStatus.Rejected:
                await WriteJsonAsync(context, new EnrollPendingPayload(EnrollPendingPayload.Rejected));
                return;

            default:
                // 凭据无效 —— 手机端照 `bad_token` 那条提示去重新配对。
                await WriteErrorAsync(context, 403, UploadErrors.BadToken, result.Detail);
                return;
        }
    }

    private async Task HandleEnrollClaimAsync(HttpListenerContext context)
    {
        var request = await ReadJsonAsync<EnrollClaimPayload>(context);
        if (request is null)
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "请求体为空");
            return;
        }

        var result = await _devices.ClaimAsync(request.DeviceId, request.Token);

        switch (result.Status)
        {
            case EnrollStatus.Approved:
                await WriteJsonAsync(context, new EnrollCredentialPayload(result.Credential!));
                return;

            case EnrollStatus.Pending:
                // 还没批 —— **不是错误**，是流程里正常的一步。回 200 + pending，
                // 手机照着继续等。回 4xx 的话手机会把它当成"入网失败"。
                await WriteJsonAsync(context, new EnrollPendingPayload(EnrollPendingPayload.Pending));
                return;

            case EnrollStatus.Rejected:
                // 被拒了也要让手机看得见（规格 §3.4.5）。200 + rejected 而不是 4xx：
                // 这不是"请求坏了"，是"人做的决定"，手机那边要显示的是
                // 「电脑端拒绝了这次连接」，不是「网络错误」。
                await WriteJsonAsync(context, new EnrollPendingPayload(EnrollPendingPayload.Rejected));
                return;

            case EnrollStatus.BadToken:
                await WriteErrorAsync(context, 403, UploadErrors.BadToken, result.Detail);
                return;

            case EnrollStatus.SeatLimitExceeded:
                // 留着这条分支**不是**「以防万一」：`EnrollStatus` 是共享的枚举，
                // 掉了分支就会落到下面的 default，把它当 410「重新扫一次码」回给手机
                // —— 那会让用户对着同一个码反复扫，而问题根本不在这张码上。
                await WriteErrorAsync(context, 403, UploadErrors.SeatLimit, result.Detail);
                return;

            default:
                await WriteErrorAsync(context, 410, UploadErrors.NoPendingRequest, result.Detail);
                return;
        }
    }

    private async Task HandleChunkAsync(HttpListenerContext context, string tail)
    {
        var separator = tail.LastIndexOf('/');
        if (separator <= 0)
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "分片路径要是 <evidenceId>/<下标>");
            return;
        }

        var evidenceId = Uri.UnescapeDataString(tail[..separator]);

        if (!int.TryParse(tail[(separator + 1)..], out var index))
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "分片下标不是整数");
            return;
        }

        // ⚠️ 请求体是**裸字节**，不是 JSON —— 这里绝不能去反序列化它。
        var accepted = await _upload.StoreChunkAsync(evidenceId, index, context.Request.InputStream);
        await WriteJsonAsync(context, accepted);
    }

    /// <summary>
    /// 把 <see cref="UploadRejectedException"/> 翻成状态码。
    /// </summary>
    /// <remarks>
    /// 协议码 → 状态码的映射**只有这一处**。手机端按状态码分类重试与否（§3），
    /// 所以这张表改了就是改了协议。
    /// </remarks>
    private async Task GuardAsync(HttpListenerContext context, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (UploadRejectedException ex)
        {
            await WriteErrorAsync(
                context,
                ex.Code switch
                {
                    UploadErrors.BadRequest => 400,
                    UploadErrors.BadCredential => 401,
                    _ => 409,
                },
                ex.Code,
                ex.Message);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, $"报文读不出来：{ex.Message}");
        }
    }

    private async Task<(EnrolledDevice Device, string Credential)?> AuthenticateAsync(HttpListenerContext context)
    {
        const string scheme = "Bearer ";
        var header = context.Request.Headers["Authorization"];

        if (header is null || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var credential = header[scheme.Length..].Trim();
        if (credential.Length == 0)
        {
            return null;
        }

        var device = await _devices.FindByCredentialAsync(credential);

        return device is null ? null : (device, credential);
    }

    /// <summary>
    /// 手机报到它的实时推流地址（规格 §3.8 的机位发现）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>报到每隔 20 秒来一次，而这里**一条日志都不记**</b> ——
    /// 记的话是每小时一百八十条。留痕全在 <see cref="LiveDirectory"/>：
    /// 它只在「第一次来 / 换了地址 / 过期后又回来 / 过期了」这四种**状态变化**时记。
    /// </remarks>
    private async Task HandleLiveAnnounceAsync(HttpListenerContext context, string deviceId)
    {
        if (_live is not { } live)
        {
            await WriteErrorAsync(
                context, 400, UploadErrors.BadRequest, "电脑端这边实时推流这一档没启用");
            return;
        }

        // ⚠️ 这道闸门问的是「这台机器还许不许手机报到实时画面」（未激活 / 试用已结束
        //    ⇒ 不许，2026-10-03 用户裁定：「电脑端功能均不能使用，观看已存储的视频除外」）。
        //    判断与那句话都在 `DeviceRegistry` 里 —— **这个文件里不许出现「许可」**
        //    （`LicenseIndependenceTests` 那条绊线）：它同时托管着检索 / 回放 / 导出，
        //    而 L8 要求那几条路上一个门禁都没有。闸门只装在这一条路上，
        //    **不装进 `AuthenticateAsync`** —— 手机上已经录下、还没传过来的那一段必须照收。
        if (_devices.LiveAnnounceBlocked() is { } blocked)
        {
            await WriteErrorAsync(context, 403, UploadErrors.SeatLimit, blocked.Detail);
            return;
        }

        var request = await ReadJsonAsync<LiveAnnouncePayload>(context);

        if (request?.Port is not { } port || port is < 1 or > 65535)
        {
            // ⚠️ 端口越界**不当成小事**：`http://主机:0/live` 那种地址会让取流那一步
            // 以一个看不懂的错失败，而那时已经离这里很远了。
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "port 要在 1..65535 之间");
            return;
        }

        var remote = context.Request.RemoteEndPoint?.Address;
        if (remote is null)
        {
            await WriteErrorAsync(context, 400, UploadErrors.BadRequest, "读不到请求的来源地址");
            return;
        }

        live.Announce(deviceId, HostOf(remote), port);
        await WriteJsonAsync(context, new LiveAnnounceAck(true));
    }

    /// <summary>
    /// 把请求的来源地址写成能塞进 URL 的样子。
    /// </summary>
    /// <remarks>
    /// ⚠️ 双栈监听时，IPv4 的客户端会显示成**映射地址**（`::ffff:192.168.1.5`）。
    /// 不映射回去的话，那个地址塞进 `http://…/live` 是 ffmpeg 连不上的 ——
    /// 而表现只是「这一格黑着」，看不出是地址形状的问题。
    /// </remarks>
    private static string HostOf(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        // IPv6 在 URL 里要方括号。⚠️ 链路本地那串 `%scope`（如 `fe80::1%12`）
        // 是 `ToString()` 自带的，**要留着** —— 去掉它指向的是另一个接口上的同名地址。
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();

        return string.IsNullOrWhiteSpace(text)
            ? default
            : JsonSerializer.Deserialize<T>(text, JsonOptions);
    }


    private static async Task WriteErrorAsync(
        HttpListenerContext context,
        int status,
        string error,
        string? detail)
    {
        context.Response.StatusCode = status;
        await WriteJsonAsync(context, new ErrorPayload(error, detail));
    }

}
