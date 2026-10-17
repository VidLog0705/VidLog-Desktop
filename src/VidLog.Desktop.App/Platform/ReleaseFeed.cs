using System.Net.Http;
using System.Text;
using VidLog.Desktop.Core.Update;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 去发布页问一句「最新版本是多少」（设计图 `_49`「自动检查更新」）。
/// </summary>
/// <remarks>
/// <para>
/// 这是本仓**唯一**一处主动出网的地方（另一处是授时，那是规格要求的）。
/// 所以它必须短、必须能一眼看完、而且**只读一个字段**。
/// </para>
/// <para>
/// ⚠️ <b>只做检查 + 提示</b>（需求方 2026-09-30 裁决）：这里**不下载任何文件**、
/// 不写盘、不替换自己。返回值只有版本号那一行字。
/// </para>
/// <para>
/// ⚠️ <b>2026-10-17 换了源：GitHub → AtomGit 公开仓</b>（需求方：「主通道用
/// AtomGit」）。GitHub 那边照旧出包，只是客户端问的是 AtomGit —— 国内直连
/// GitHub 接口经常超时，而 AtomGit 匿名实测 0.35 秒。
/// </para>
/// <para>
/// ⚠️ <b>必须用 <c>/releases/latest</c>，不能用列表接口取第 0 条</b>：
/// AtomGit 的列表是**旧到新**排的（2026-10-17 实测 mindspore 三个 release
/// 依次是 v2.8.0 / v2.9.0 / v2.10.0），也没有 sort 参数 ⇒ `per_page=1` 拿到的是
/// **最旧**那一个，于是「有新版本」这条提示**永远不会出现**。
/// ⚠️ 而「一个版本都没发过」时它回的是 <b>400</b>（不是 404、不是空数组），
/// 由 Core 的 <c>ReleaseFeedJson.IsNoRelease</c> 认 —— 那不是失败。
/// </para>
/// <para>
/// ⚠️ <b>超时给 8 秒</b>：这是个可选功能，用户开着代理或者断着网的时候，
/// 让它在后台挂两分钟没有意义。
/// </para>
/// </remarks>
internal static class ReleaseFeed
{
    private const string Feed =
        "https://api.atomgit.com/api/v5/repos/Vidlog/vidlog-releases/releases/latest";

    /// <summary>最新一个发布的 tag；一个都没发过时给 <see langword="null"/>。</summary>
    public static async Task<string?> FetchLatestTagAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        // GitHub 的接口**要求**带 User-Agent，不带直接 403。
        // AtomGit 匿名 GET 不挑这个，但带上没坏处（也便于对端分辨是谁在问）。
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VidLog-Desktop");

        using var response = await http.GetAsync(Feed, cancellationToken);

        // ⚠️ 与 `BaiduPanClient.ReadAsync` 同一个理由（2026-10-14）：`ReadAsStringAsync`
        // 信服务器声明的 charset，撞上一个非法名字就**在读到正文之前抛**
        // （`InvalidOperationException`），连 4xx 的正文都拿不到。
        // 这里要读 400 的正文来分辨「还没发过」，所以必须先拿字节自己解。
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var json = Encoding.UTF8.GetString(bytes);

        // 「这个仓一个 release 都还没有」——AtomGit 回 400 + 一句「未找到 release」。
        // 它不是失败（与 GitHub 时代的空数组同一个意思）。
        if (ReleaseFeedJson.IsNoRelease((int)response.StatusCode, json))
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        // ⚠️ 怎么读这一段在 Core 的 `ReleaseFeedJson.ReadLatestTag` 里
        // （T27② 第 4 批）—— 那边管着「结构认不出来」要抛，这一层只管把正文取回来。
        return ReleaseFeedJson.ReadLatestTag(json);
    }
}
