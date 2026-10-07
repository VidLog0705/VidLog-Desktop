using System.Net.Http;
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
/// ⚠️ <b>地址写死，而且只有仓库列表这一个接口</b>：不用
/// <c>/releases/latest</c>（一个版本都没发过时它返回 404，而那**不是**失败，
/// 是「还没有发布过」）。列表接口在那种情况下返回 <c>[]</c>，两者分得清。
/// ⚠️ 未认证的 GitHub 接口每小时只给 60 次 —— 一次启动问一次，够用；
/// 被限流时按「没问到」处理（<c>UpdateChecker</c> 会吞掉）。
/// </para>
/// <para>
/// ⚠️ <b>超时给 8 秒</b>：这是个可选功能，用户开着代理或者断着网的时候，
/// 让它在后台挂两分钟没有意义。
/// </para>
/// </remarks>
internal static class ReleaseFeed
{
    private const string Feed =
        "https://api.github.com/repos/VidLog0705/VidLog-Desktop/releases?per_page=1";

    /// <summary>最新一个发布的 tag；一个都没发过时给 <see langword="null"/>。</summary>
    public static async Task<string?> FetchLatestTagAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        // GitHub 的接口**要求**带 User-Agent，不带直接 403。
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VidLog-Desktop");

        using var response = await http.GetAsync(Feed, cancellationToken);
        response.EnsureSuccessStatusCode();

        // ⚠️ 怎么读这一段在 Core 的 `ReleaseFeedJson.ReadLatestTag` 里
        // （T27② 第 4 批）—— 那边还管着「空数组」与「被限流的那个对象」
        // 怎么分开，这一层只管把文本取回来。
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        return ReleaseFeedJson.ReadLatestTag(json);
    }
}
