using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Cloud;

/// <summary>
/// 分片怎么切、MD5 怎么算。
/// </summary>
/// <remarks>
/// <para>
/// 网盘那边的约定是**固定 4MB** 一片，而分片的 MD5 是「这一片在原文件里的名义内容」
/// 的摘要 —— 所以切法必须与算 MD5 的切法**一字不差**，两处分别实现迟早会差一个字节，
/// 而差一个字节的表现是「网盘说这个分片它有了，于是不传，最后合出来的文件是坏的」。
/// 所以切与算**只有这一份**。
/// </para>
/// <para>
/// ⚠️ <b>一个文件的分片数不得超过 1024</b>（017 能力说明「大小限制」段原文：
/// 「分片数量不得超过 1024 个」）。4MB × 1024 = 4GB，正好是普通用户单文件的上限，
/// 两个数在对得上，所以这里就照 1024 写。
/// </para>
/// <para>
/// 会员与超级会员的分片上限分别是 16MB / 32MB，单文件 10GB / 20GB ——
/// 那两个是**上限**而不是「固定」，所以对所有档位都用 4MB 是安全的
/// （普通用户那一档 4MB 是「固定」，用别的反而会被 <c>31299</c> 拒）。
/// </para>
/// </remarks>
public static class BaiduPanBlocks
{
    /// <summary>一片的大小（普通用户这一档网盘写死 4MB）。</summary>
    public const int SliceSize = 4 * 1024 * 1024;

    /// <summary>一个文件的分片数上限（017 写的 1024，= 4GB）。</summary>
    public const int MaxSlicesPerCreate = 1024;

    /// <summary>一个这么大的文件要切成几片。</summary>
    public static int Count(long size) => (int)((size + SliceSize - 1) / SliceSize);

    /// <summary>逐片算 MD5（小写十六进制，网盘要的就是这个形态）。</summary>
    public static async Task<IReadOnlyList<string>> ComputeAsync(
        string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

        var buffer = new byte[SliceSize];
        var hashes = new List<string>();

        while (true)
        {
            var filled = await FillAsync(stream, buffer, cancellationToken);
            if (filled == 0)
            {
                break;
            }

            // ⚠️ 最后一片按**实际读到的长度**算。拿整块 buffer（尾部还留着上一片的字节）
            // 去算，会得到一个与网盘手里那一份对不上的 MD5 ——
            // 于是网盘说「这片我没有」，我传上去，合并时报错，而错在哪完全看不出来。
            hashes.Add(Convert.ToHexString(MD5.HashData(buffer.AsSpan(0, filled))).ToLowerInvariant());

            if (filled < buffer.Length)
            {
                break;
            }
        }

        return hashes;
    }

    /// <summary>把 <paramref name="buffer"/> 填满（或读到文件尾）。</summary>
    private static async Task<int> FillAsync(
        Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}

/// <summary>
/// 真的走 HTTP 的那一份（百度网盘开放平台）。
/// </summary>
/// <remarks>
/// <para>
/// 按**公开的接口文档**写，参考的不是别的产品的实现 —— 洁净室边界见
/// <c>docs/实现决策.md</c> §87：界面外观的豁免不覆盖这一层。
/// </para>
/// <para>
/// ⚠️ <b>本类绝不把令牌写进异常或日志。</b>报错只报 <c>errno</c> 与网盘给的那句话；
/// 令牌在 URL 查询串里，一旦进了日志就等于进了工单。
/// </para>
/// </remarks>
public sealed class BaiduPanClient : IBaiduPanApi
{
    private const string OAuthBase = "https://openapi.baidu.com/oauth/2.0";
    private const string FileBase = "https://pan.baidu.com/rest/2.0/xpan/file";
    private const string NasBase = "https://pan.baidu.com/rest/2.0/xpan/nas";

    /// <summary>取上传域名的那一个服务（016）。**注意它只负责发域名，不收分片。**</summary>
    private const string UploadLocateBase = "https://d.pcs.baidu.com/rest/2.0/pcs/file";

    /// <summary>
    /// 拿不到域名时的兜底。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只在下发列表为空时用（文档的示例响应里 <c>servers</c> 正常是有值的）。
    /// 特意**不**把它当成「默认上传域名」长期用：016 明说不要固定写死。
    /// </remarks>
    private const string UploadHostFallback = "https://d.pcs.baidu.com";

    /// <summary>
    /// 每个请求都带的 <c>User-Agent</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 文档里每一个 cURL / Python 示例都带它，019 下载的 header 表把它标成**必填**，
    /// 而 <c>31326</c>（命中防盗链）的排查方向写的就是「User-Agent 请求头是否正常」。
    /// 不带它时不一定报错，但报错时报的是「防盗链」—— 那种错很难联想到「少了个头」。
    /// </remarks>
    private const string UserAgent = "pan.baidu.com";

    /// <summary><c>-8</c>「文件或目录已存在」（063）。建目录时它是**成功**。</summary>
    private const int AlreadyExists = -8;

    private readonly BaiduPanCredentials _credentials;
    private readonly HttpClient _http;
    private readonly IAppLogger _logger;

    public BaiduPanClient(
        BaiduPanCredentials credentials, HttpClient http, IAppLogger? logger = null)
    {
        _credentials = credentials;
        _http = http;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<BaiduDeviceCode> StartDeviceLoginAsync(
        CancellationToken cancellationToken = default)
    {
        var url = $"{OAuthBase}/device/code"
            + $"?client_id={Uri.EscapeDataString(_credentials.AppKey)}"
            + "&response_type=device_code"
            + "&scope=basic,netdisk";

        var json = await GetJsonAsync(url, cancellationToken);

        // 兜底值照 2026-09-30 打真服务器实测的结果：那一刻网盘回的就是
        // interval=5、expires_in=300（5 分钟）。写 1800 会让界面说「这串码半小时内有效」，
        // 而它 5 分钟就作废了 —— 用户按界面上的话去泡杯茶，回来只看到「授权失败」。
        return new BaiduDeviceCode(
            Required(json, "device_code"),
            Required(json, "user_code"),
            Optional(json, "verification_url") ?? "https://openapi.baidu.com/device",
            Optional(json, "qrcode_url") ?? string.Empty,
            json.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5,
            json.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 300);
    }

    public async Task<BaiduToken?> PollDeviceTokenAsync(
        string deviceCode, CancellationToken cancellationToken = default)
    {
        var url = $"{OAuthBase}/token"
            + "?grant_type=device_token"
            + $"&code={Uri.EscapeDataString(deviceCode)}"
            + $"&client_id={Uri.EscapeDataString(_credentials.AppKey)}"
            + $"&client_secret={Uri.EscapeDataString(_credentials.AppSecret)}";

        var json = await GetJsonAsync(url, cancellationToken);

        // 「人还没点」与「点得太快」都是**流程里正常的一步**，不是错误。
        //
        // ⚠️ 2026-09-30 拿真凭据打过真服务器，还没授权时它回的是
        //     HTTP 400 + {"error":"authorization_pending","error_description":"..."}
        // 也就是说这个「正常的一步」是**带着一个 4xx 状态码**回来的。所以
        // ReadAsync 必须先把带 error 的响应体交回来（它也确实那么做了），
        // 否则这里根本轮不到 —— 那句话会先被当成 HTTP 失败抛出去，
        // 而设备码登录的第一次轮询就必然炸。
        if (json.TryGetProperty("error", out var error))
        {
            var code = error.GetString();

            if (code is "authorization_pending" or "slow_down")
            {
                return null;
            }

            throw new BaiduPanException(
                0, $"百度网盘授权失败：{code} {Optional(json, "error_description")}");
        }

        return ReadToken(json);
    }

    public async Task<BaiduToken> RefreshAsync(
        string refreshToken, CancellationToken cancellationToken = default)
    {
        var url = $"{OAuthBase}/token"
            + "?grant_type=refresh_token"
            + $"&refresh_token={Uri.EscapeDataString(refreshToken)}"
            + $"&client_id={Uri.EscapeDataString(_credentials.AppKey)}"
            + $"&client_secret={Uri.EscapeDataString(_credentials.AppSecret)}";

        var json = await GetJsonAsync(url, cancellationToken);

        // ⚠️ 续期被拒时**回的是 error 而不是 errno**（同一个 OAuth 端点）。
        // 不在这里挡一道的话，`ReadToken` 会因为读不到 access_token 而报
        // 「回包里没有 access_token」—— 那句话把「授权已经失效了，得重新登录」
        // 说成了一个像解析 bug 的东西。
        if (json.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            var code = error.GetString() ?? string.Empty;
            var description = Optional(json, "error_description");

            // ⚠️ 「凭据没了」与「我们配错了」要分开，因为后果差得很远：
            // 前者该把本机那串令牌丢掉、请用户重新授权；后者丢掉令牌**也解决不了**
            // （AppKey/Secret 配错了，重新授权多少次都一样），反而把用户
            // 每次都逼去走一遍授权流程。
            //
            // 判据取标准的授权类错误码。文档没有列这张表（接入授权那一页只说
            // refresh_token 是一次性的、刷新失败旧的那串也一起失效），
            // 所以这里**只认最保守的三个**，其余一律当「不明原因」照实报。
            var gone = code is "invalid_grant" or "invalid_token" or "expired_token";

            throw new BaiduPanException(
                // 20017 = access_token 无效（「可能因用户解绑或授权撤销等原因失效，
                // 请重新获取 token 或续期 token」）—— 正是眼下这件事。
                gone ? 20017 : 0,
                $"百度网盘续期失败：{code} {description}"
                + (gone ? "（这一串 refresh_token 已经作废了，得重新登录一次）" : string.Empty));
        }

        return ReadToken(json);
    }

    public async Task<string> GetDisplayNameAsync(
        string accessToken, CancellationToken cancellationToken = default)
    {
        var url = $"{NasBase}?method=uinfo&access_token={Uri.EscapeDataString(accessToken)}";

        var json = await GetJsonAsync(url, cancellationToken);

        // ⚠️ 两个名字都给不出来时回一句实话，**不编一个「百度用户」**：
        // 界面上那一行是给用户确认「登的是不是我那个号」用的。
        return Optional(json, "netdisk_name")
            ?? Optional(json, "baidu_name")
            ?? "（网盘没回昵称）";
    }

    public async Task<BaiduPrecreate> PrecreateAsync(
        string accessToken,
        string remotePath,
        long size,
        IReadOnlyList<string> blockList,
        CancellationToken cancellationToken = default)
    {
        var json = await PostFormAsync(
            $"{FileBase}?method=precreate&access_token={Uri.EscapeDataString(accessToken)}",
            new Dictionary<string, string>
            {
                ["path"] = remotePath,
                ["size"] = size.ToString(CultureInfo.InvariantCulture),
                ["isdir"] = "0",
                // ⚠️ 归档那条路径（`/apps/<应用名>/2026/09/30/发货/`）**靠这个参数**
                // 把中间那几层目录一起建出来 —— 代码里没有任何一处显式建目录。
                // 文档只说它「本接口固定为 1」，**没写它到底做什么**，
                // 所以这条是**真机必须验**的（`docs/实现决策.md` §87.15 第 3 条）：
                // 不成立的话，第一次往一个新日期目录里传会整个失败，
                // 得改成先 `method=create` + `isdir=1` 逐层建（文档 020）。
                ["autoinit"] = "1",
                // rtype=3 = 同名时**覆盖**（018/014 的「冲突处理策略」：
                // 0 冲突失败、1 冲突重命名、2 冲突且 block_list 不同才重命名、3 覆盖）。
                // 归档要的是幂等：同一条录像重传一次就该落回同一个文件，
                // 而不是在网盘上堆出 `SF1000000001_ab12_000(1).mp4`。
                // ⚠️ 文档要求它与 create 那一步**保持一致**（两处都是 3）。
                ["rtype"] = "3",
                ["block_list"] = JsonSerializer.Serialize(blockList),
            },
            cancellationToken);

        // ⚠️ 响应里这个**同名**的 block_list 与请求里那个不是一回事：
        // 请求里是「每片的 MD5」，响应里是「**还需要传**的分片序号」。
        var pending = new HashSet<int>();

        if (json.TryGetProperty("block_list", out var wanted)
            && wanted.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in wanted.EnumerateArray())
            {
                if (item.TryGetInt32(out var index))
                {
                    pending.Add(index);
                }
            }

            // 文档：「block_list 为空时等价于 [0]」——
            // 空数组不是「一片都不用传」，是那个单片的退化写法。
            if (pending.Count == 0)
            {
                pending.Add(0);
            }
        }
        else
        {
            // 字段整个缺席时按「解析不出来」处理 ⇒ 全传。
            // 这一头必须朝「多传」落：朝「少传」落就是在 create 那一步炸，
            // 而且炸出来的错（分片缺失）指不回这里。
            for (var index = 0; index < blockList.Count; index++)
            {
                pending.Add(index);
            }
        }

        return new BaiduPrecreate(Required(json, "uploadid"), pending);
    }

    public async Task<string> LocateUploadAsync(
        string accessToken,
        string remotePath,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{UploadLocateBase}?method=locateupload"
            + "&appid=250528"
            + $"&access_token={Uri.EscapeDataString(accessToken)}"
            + $"&path={Uri.EscapeDataString(remotePath)}"
            + $"&uploadid={Uri.EscapeDataString(uploadId)}"
            + "&upload_version=2.0";

        var json = await GetJsonAsync(url, cancellationToken);

        // ⚠️ 这一个接口的成功判据是 `error_code`，**不是 `errno`**（016）。
        // 和获取用户身份信息（058）一样是那两个例外之一。
        if (json.TryGetProperty("error_code", out var code) && code.GetInt32() != 0)
        {
            throw new BaiduPanException(
                code.GetInt32(),
                $"百度网盘没给上传域名（error_code={code.GetInt32()}）"
                + $"：{Optional(json, "error_msg") ?? "没给原因"}");
        }

        foreach (var name in new[] { "servers", "bak_servers" })
        {
            if (PickHost(json, name) is { Length: > 0 } host)
            {
                return host;
            }
        }

        // 一个都没下发。说清楚，然后退回文档里那个主机名 ——
        // 退回去有可能传不上去，但比在这里直接失败强（那会把「网盘抽风」变成「这条录像永远传不上去」）。
        _logger.Log(LogLevel.Warn, "网盘", "百度网盘没下发上传域名，退回默认的那一个");

        return UploadHostFallback;
    }

    /// <summary>从 <c>servers</c>/<c>bak_servers</c> 里挑一个 **HTTPS** 的地址。</summary>
    /// <remarks>
    /// ⚠️ 文档：「从 servers 中选择 HTTPS 地址，并保留协议与主机名」。
    /// 挑到 http 的会明文传整段录像，而那是隐私画面。
    /// </remarks>
    private static string? PickHost(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? plain = null;

        foreach (var entry in list.EnumerateArray())
        {
            var server = Optional(entry, "server");
            if (server is not { Length: > 0 })
            {
                continue;
            }

            if (server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return server.TrimEnd('/');
            }

            plain ??= server.TrimEnd('/');
        }

        return plain;
    }

    public async Task CreateAsync(
        string accessToken,
        string remotePath,
        long size,
        IReadOnlyList<string> blockList,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        await PostFormAsync(
            $"{FileBase}?method=create&access_token={Uri.EscapeDataString(accessToken)}",
            new Dictionary<string, string>
            {
                ["path"] = remotePath,
                ["size"] = size.ToString(CultureInfo.InvariantCulture),
                ["isdir"] = "0",
                // ⚠️ 必须与 precreate 那一步一致（014 注意事项逐字要求）。
                ["rtype"] = "3",
                ["uploadid"] = uploadId,
                ["block_list"] = JsonSerializer.Serialize(blockList),
            },
            cancellationToken);
    }

    public async Task CreateDirectoryAsync(
        string accessToken,
        string directory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await PostFormAsync(
                $"{FileBase}?method=create&access_token={Uri.EscapeDataString(accessToken)}",
                new Dictionary<string, string>
                {
                    ["path"] = directory,
                    ["isdir"] = "1",

                    // ⚠️ 目录的冲突策略与文件**不是一套**（020 的 rtype：目录只有
                    // 「0 冲突时失败 / 1 冲突时重命名」，没有覆盖这一档）。
                    // 这里必须是 0：「已经在了」正是我们要的结果，而 1 会在网盘上
                    // 堆出 `发货(1)`、`发货(2)` —— 回查是按路径找的（I8），
                    // 那些目录里的文件会被判成「云端没有」，于是本机那份不敢删。
                    ["rtype"] = "0",

                    // ⚠️ 020 注意事项逐字：创建文件夹**不要传入** size、block_list 和 uploadid。
                    // 传了不一定会被拒，但那是「照文档写」与「照自己猜的写」的分界。
                },
                cancellationToken);
        }
        catch (BaiduPanException ex) when (ex.Errno == AlreadyExists)
        {
            // -8「文件或目录已存在」—— 建目录是**幂等**的，这不是失败。
            // 不吞掉的话，往一个已经用过的日期目录里传第二条录像就会整条失败。
        }
    }

    public async Task<string?> UploadSliceAsync(
        string accessToken,
        string uploadHost,
        string remotePath,
        string uploadId,
        int partSeq,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        // ⚠️ 域名用 locateupload 给的那一个（016），不写死。
        var url = $"{uploadHost.TrimEnd('/')}/rest/2.0/pcs/superfile2?method=upload"
            + $"&access_token={Uri.EscapeDataString(accessToken)}"
            + "&type=tmpfile"
            + $"&path={Uri.EscapeDataString(remotePath)}"
            + $"&uploadid={Uri.EscapeDataString(uploadId)}"
            + $"&partseq={partSeq.ToString(CultureInfo.InvariantCulture)}";

        using var form = new MultipartFormDataContent();
        using var part = new StreamContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", "slice");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        WithHeaders(request);

        using var response = await SendSliceAsync(request, partSeq, cancellationToken);

        // ⚠️ 这里回的是 **JSON**（015 响应参数表：md5 / request_id / errno），
        // 而且**失败时也可能带 200 状态码** —— 光看状态码会把「这片没传上去」
        // 当成成功，一路走到 create 才炸，报的却是「分片缺失」。
        var json = await ReadAsync(response, url, cancellationToken);

        return Optional(json, "md5");
    }

    public async Task<IReadOnlySet<string>> ListFilesAsync(
        string accessToken, string directory, CancellationToken cancellationToken = default)
    {
        // ⚠️ 目录还不存在时网盘回 **-9（目录不存在）**，那对一个刚登录、
        // 一条都还没传过的用户来说是**正常状态**，不是错误。
        // 它由调用方按 errno 判（`CloudArchiveBackend` / `CloudUploadService` 各有一处），
        // 因为「不存在的目录」在那两处的含义不一样（一个是「还没归档」，另一个是「这个目录下一份都没有」）。
        const int Page = 1000;

        var names = new HashSet<string>(StringComparer.Ordinal);
        var start = 0;

        while (true)
        {
            var url = $"{FileBase}?method=list"
                + $"&access_token={Uri.EscapeDataString(accessToken)}"
                + $"&dir={Uri.EscapeDataString(directory)}"
                + "&order=name"
                + "&limit=" + Page.ToString(CultureInfo.InvariantCulture)
                + "&start=" + start.ToString(CultureInfo.InvariantCulture);

            var json = await GetJsonAsync(url, cancellationToken);

            var got = 0;

            if (json.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    got++;

                    var path = Optional(entry, "path");
                    if (path is { Length: > 0 })
                    {
                        names.Add(path[(path.LastIndexOf('/') + 1)..]);
                    }
                }
            }

            // ⚠️ **不能只看 has_more 决定翻不翻页。** 053「获取文件列表」的响应参数表里
            // 根本没有 has_more 这个字段（有它的是 048 搜索和 057 递归列目录）。
            // 只信它的话，一个上千条的目录会被**静默**截到第一页 ——
            // 而回查归档层（I8）会因此说「云端没有这一条」，接着把本机那份重传一遍。
            //
            // 判据用「这一页有没有装满」：没装满就是最后一页（053 原文的分页口径
            // 就是 start + limit）。has_more 只在它出现且说「还有」时额外投一票。
            var more = got >= Page
                || (json.TryGetProperty("has_more", out var hasMore) && hasMore.GetInt32() != 0);

            if (!more)
            {
                return names;
            }

            start += Page;
        }
    }

    // ───────────── HTTP 那点杂事 ─────────────

    private static BaiduToken ReadToken(JsonElement json) => new(
        Required(json, "access_token"),
        // ⚠️ 用 Optional 而不是 Required：文档说每次续期都会回一串新的 refresh_token，
        // 但**将来万一哪次没回**，`Required` 会在这里抛，于是
        // `BaiduPanSession` 里那句「没回就沿用旧的」永远轮不到 ——
        // 一个本来能救回来的续期会变成一个「莫名要重新登录」。
        Optional(json, "refresh_token") ?? string.Empty,
        json.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 0);

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        WithHeaders(request);

        using var response = await SendTimedAsync(request, url, cancellationToken);
        return await ReadAsync(response, url, cancellationToken);
    }

    /// <summary>每个请求都要带的头。</summary>
    private static void WithHeaders(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

    private async Task<JsonElement> PostFormAsync(
        string url, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(fields);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        WithHeaders(request);

        using var response = await SendTimedAsync(request, url, cancellationToken);
        return await ReadAsync(response, url, cancellationToken);
    }

    private async Task<JsonElement> ReadAsync(
        HttpResponseMessage response, string url, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch (JsonException ex)
        {
            throw new BaiduPanException(
                0, $"百度网盘回了读不懂的内容（HTTP {(int)response.StatusCode}）：{ex.Message} {Trim(text)}");
        }

        // ⚠️ 带 `error` 的响应体**先交回给调用方**，不在这里按 HTTP 状态码判死。
        //
        // 理由是一条实测事实（2026-09-30，真凭据、真服务器）：设备码轮询在
        // 「用户还没点授权」时回的就是 `HTTP 400 + {"error":"authorization_pending"}`。
        // 那是**流程里正常的一步**。先按状态码抛的话，调用方永远看不到那个 error，
        // 于是设备码登录的第一次轮询必然失败 —— 而且报出来的是「HTTP 400」，
        // 看的人只会去查网络，查不到「其实只是还没点」。
        if (json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("error", out var oauth) && oauth.ValueKind == JsonValueKind.String)
        {
            return json;
        }

        // ⚠️ 网盘的错误**不一定配 HTTP 状态码**：errno 非 0 的时候 HTTP 可能是 200。
        // 只看状态码的话，「令牌失效」会被当成成功，然后拿一个空 uploadid 去传分片。
        if (json.TryGetProperty("errno", out var errno) && errno.GetInt32() != 0)
        {
            var code = errno.GetInt32();

            _logger.Log(LogLevel.Warn, "网盘", $"百度网盘返回 errno={code}（{Path(url)}）");

            throw new BaiduPanException(
                code,
                $"百度网盘出错 errno={code}（{Optional(json, "errmsg") ?? "没给原因"}）{Hint(code)}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new BaiduPanException(
                0, $"百度网盘请求失败：HTTP {(int)response.StatusCode} {Trim(text)}");
        }

        return json;
    }

    /// <summary>
    /// 几个「用户自己动手才能解决」的错误码，补一句人话。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这几个码的共同点是：**重试一万次也没用**，得去开放平台后台做点什么。
    /// 光把 <c>errno=20013</c> 抛到界面上，用户（和半年后的我们）都得回来翻文档。
    /// 文案照 063 公共错误码表的「排查方向」列写。
    /// </remarks>
    private static string Hint(int errno) => errno switch
    {
        20011 => "（应用还在审核中，只有前 10 个完成授权的用户能用 —— 要放开得先过审）",
        20012 => "（调用次数已达上限被限流了：应用过审之前只能用于测试开发，也可能就是调得太密）",
        20013 => "（这个应用没有该接口的权限 —— 要过审，且在审核时申请过这个接口）",
        20015 => "（这个应用已经失效了，检查一下后台是不是被删了）",
        20020 => "（路径不在应用允许访问的范围内，只能动 /apps/<应用名>/ 底下）",
        31023 or 2 => "（参数不对：检查必填项、以及每个参数该放 URL 还是放 body）",
        31034 => "（请求太频繁，命中频控了 —— 缓一缓再来）",
        31064 => "（上传路径不对，必须是 /apps/<申请接入时填的那个产品名称>/…）",
        31066 => "（文件不存在，检查路径）",
        31190 or 31363 => "（分片缺失：某个分片没传上去，或者 size 与实际文件对不上）",
        31299 => "（第一个分片小于 4MB —— 非最后一片都必须满 4MB）",
        31364 => "（分片超过大小上限）",
        31365 => "（文件太大：普通用户单文件 4GB、会员 10GB、超级会员 20GB）",
        -6 or 20016 or 20017 or 31045 => "（授权不管用了，重新登录一次百度网盘）",
        _ => string.Empty,
    };

    /// <summary>
    /// 出网那一下 —— 耗时与成败都在这里留痕（§6.1：外部依赖调用要记耗时）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 记的是 <see cref="Path"/>（查询串已抹）而**不是整条 URL**：
    /// <c>access_token</c> 就在查询串里，而 <c>logs/</c> 会整个进诊断包 ——
    /// 记一条完整 URL 等于把凭据写进要发出去的文件。
    /// </para>
    /// <para>
    /// ⚠️ <b>分片不走这里</b>（见 <c>UploadSliceAsync</c>）：它每 4MB 一次，
    /// 逐片记会把日志淹掉，而**淹掉的日志等于没有日志** —— 那条路只在失败时记。
    /// </para>
    /// </remarks>
    private async Task<HttpResponseMessage> SendTimedAsync(
        HttpRequestMessage request, string url, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await _http.SendAsync(request, cancellationToken);

            _logger.Log(
                LogLevel.Debug, "网盘", $"网盘请求 {Path(url)}",
                new Dictionary<string, object?>
                {
                    ["耗时ms"] = ElapsedMs(started),
                    ["状态"] = (int)response.StatusCode,
                });

            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // ⚠️ 失败**带耗时**：否则「被拒了」和「慢到超时」分不出来。
            _logger.Log(
                LogLevel.Warn, "网盘", $"网盘请求 {Path(url)} 失败",
                new Dictionary<string, object?>
                {
                    ["耗时ms"] = ElapsedMs(started),
                    ["错误"] = ex.Message,
                });

            throw;
        }
    }

    /// <summary>
    /// 送一片 —— **只在失败时留痕**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这一路每 4MB 跑一次（一个 4GB 的文件就是 1024 次），成功也记的话
    /// 一次上传就能把日志灌满，而**灌满的日志等于没有日志** ——
    /// 那时真正要看的那几条（失败、重试、收尾）全被埋了。
    /// 与「起外部进程只在失败时记」同一条规矩。
    /// </remarks>
    private async Task<HttpResponseMessage> SendSliceAsync(
        HttpRequestMessage request, int partSeq, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.Log(
                LogLevel.Warn, "网盘", $"第 {partSeq} 片没送出去",
                new Dictionary<string, object?>
                {
                    ["耗时ms"] = ElapsedMs(started),
                    ["错误"] = ex.Message,
                });

            throw;
        }
    }

    private static long ElapsedMs(long startedTimestamp) =>
        (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

    /// <summary>日志里只留路径，**查询串一律丢掉**（令牌在里面）。</summary>
    private static string Path(string url)
    {
        var question = url.IndexOf('?');

        return question < 0 ? url : url[..question];
    }

    private static string Trim(string text) =>
        text.Length <= 200 ? text : text[..200] + "…";

    private static string Required(JsonElement json, string name) =>
        Optional(json, name) ?? throw new BaiduPanException(0, $"百度网盘的回包里没有 {name}");

    private static string? Optional(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
}
