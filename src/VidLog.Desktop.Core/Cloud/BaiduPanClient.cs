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
/// ⚠️ 单次 <c>create</c> 最多 512 片（= 2GB），也就是说更大的文件要**分多次合并**。
/// 本仓今天的录像单段远小于 2GB，所以超出那一段走 <see cref="MaxSlicesPerCreate"/>
/// 直接说清楚，而不是硬凑一个半截上传。
/// </para>
/// </remarks>
public static class BaiduPanBlocks
{
    /// <summary>一片的大小（网盘那边写死的 4MB）。</summary>
    public const int SliceSize = 4 * 1024 * 1024;

    /// <summary>一次 <c>create</c> 能吃下的分片数上限。</summary>
    public const int MaxSlicesPerCreate = 512;

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
    private const string SliceBase = "https://d.pcs.baidu.com/rest/2.0/pcs/superfile2";

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

        return new BaiduDeviceCode(
            Required(json, "device_code"),
            Required(json, "user_code"),
            Optional(json, "verification_url") ?? "https://openapi.baidu.com/device",
            Optional(json, "qrcode_url") ?? string.Empty,
            json.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5,
            json.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 1800);
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

        return ReadToken(await GetJsonAsync(url, cancellationToken));
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
                ["autoinit"] = "1",
                // rtype=3 = 超大文件（superfile），分片走 superfile2。
                ["rtype"] = "3",
                ["block_list"] = JsonSerializer.Serialize(blockList),
            },
            cancellationToken);

        var already = new HashSet<int>();

        // ⚠️ 网盘回的是「**已经有**的分片下标」。有的响应里这个字段干脆没有
        // （全新文件），有的给一个空数组 —— 两种都是「一片都还没有」。
        if (json.TryGetProperty("block_list", out var present)
            && present.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in present.EnumerateArray())
            {
                if (item.TryGetInt32(out var index))
                {
                    already.Add(index);
                }
            }
        }

        return new BaiduPrecreate(Required(json, "uploadid"), already);
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
                ["rtype"] = "3",
                ["uploadid"] = uploadId,
                ["block_list"] = JsonSerializer.Serialize(blockList),
            },
            cancellationToken);
    }

    public async Task UploadSliceAsync(
        string accessToken,
        string remotePath,
        string uploadId,
        int partSeq,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var url = $"{SliceBase}?method=upload"
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
        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        // ⚠️ 分片这一条路**不回 JSON，回一个裸的 md5 字符串**（成功时）。
        // 拿 JSON 去解它会抛，而那个异常会说「报文读不出来」——
        // 真正的原因是「这里本来就不是 JSON」。
        if (!response.IsSuccessStatusCode)
        {
            throw new BaiduPanException(
                0, $"传分片 {partSeq} 失败：HTTP {(int)response.StatusCode} {Trim(text)}");
        }
    }

    public async Task<IReadOnlySet<string>> ListFilesAsync(
        string accessToken, string directory, CancellationToken cancellationToken = default)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var start = 0;

        while (true)
        {
            var url = $"{FileBase}?method=list"
                + $"&access_token={Uri.EscapeDataString(accessToken)}"
                + $"&dir={Uri.EscapeDataString(directory)}"
                + "&order=name"
                + "&limit=1000"
                + "&start=" + start.ToString(CultureInfo.InvariantCulture);

            var json = await GetJsonAsync(url, cancellationToken);

            // ⚠️ 目录还不存在时网盘回 **-9（目录不存在）**，那对一个刚登录、
            // 一条都还没传过的用户来说是**正常状态**，不是错误。
            if (json.TryGetProperty("errno", out var errno) && errno.GetInt32() == -9)
            {
                return names;
            }

            if (json.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    var path = Optional(entry, "path");
                    if (path is { Length: > 0 })
                    {
                        names.Add(path[(path.LastIndexOf('/') + 1)..]);
                    }
                }
            }

            var more = json.TryGetProperty("has_more", out var hasMore) && hasMore.GetInt32() != 0;
            if (!more)
            {
                return names;
            }

            start += 1000;
        }
    }

    // ───────────── HTTP 那点杂事 ─────────────

    private static BaiduToken ReadToken(JsonElement json) => new(
        Required(json, "access_token"),
        Required(json, "refresh_token"),
        json.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 0);

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken);
        return await ReadAsync(response, url, cancellationToken);
    }

    private async Task<JsonElement> PostFormAsync(
        string url, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(fields);
        using var response = await _http.PostAsync(url, form, cancellationToken);
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

        // ⚠️ 网盘的错误**不一定配 HTTP 状态码**：errno 非 0 的时候 HTTP 可能是 200。
        // 只看状态码的话，「令牌失效」会被当成成功，然后拿一个空 uploadid 去传分片。
        if (json.TryGetProperty("errno", out var errno) && errno.GetInt32() != 0)
        {
            var code = errno.GetInt32();

            _logger.Log(LogLevel.Warn, "网盘", $"百度网盘返回 errno={code}（{Path(url)}）");

            throw new BaiduPanException(
                code, $"百度网盘出错 errno={code}（{Optional(json, "errmsg") ?? "没给原因"}）");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new BaiduPanException(
                0, $"百度网盘请求失败：HTTP {(int)response.StatusCode} {Trim(text)}");
        }

        return json;
    }

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
