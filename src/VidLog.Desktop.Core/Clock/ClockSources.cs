using System.Globalization;

namespace VidLog.Desktop.Core.Clock;

/// <summary>
/// 一个外部时间源 —— 问它「现在几点」。
/// </summary>
/// <remarks>
/// 规格 §3.6.4 给了两个来源：**公网时间服务**（NTP 或 HTTP Date）与
/// **归档回执里的外部时间锚**。两者在这里长得一样：都是「拿一个时刻回来」。
/// </remarks>
public interface IClockSource
{
    /// <summary>取一个外部时刻。取不到就抛（由调用方决定怎么告诉用户）。</summary>
    Task<DateTimeOffset> QueryAsync(CancellationToken cancellationToken = default);

    string Description { get; }
}

/// <summary>
/// 公网时间：读 HTTP 响应的 `Date` 头。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ **用 HTTP Date 而不是 NTP**：NTP 要走 UDP 123，在没有管理权限的机器上
/// 常常被防火墙拦掉；而 HTTP 走 80/443，**与这台机器上网用的是同一条路** ——
/// 一台能上网的机器就一定能取到。规格允许两者取一（原文「NTP 或 HTTP Date」）。
/// </para>
/// <para>
/// ⚠️ <b>它的精度是「秒」级，而且信任的是中间那段网络</b>（能被中间人改）。
/// 对这份用途是够的：要挡的是「用户手动改系统时间」这种伪造，
/// 而不是「国家级对手」。规格 §3.6.3 也明令**不得宣传「不可篡改的时间」**。
/// </para>
/// </remarks>
public sealed class HttpDateClockSource : IClockSource
{
    /// <summary>默认探的地址。</summary>
    /// <remarks>
    /// 挑一个**国内可达、且几乎不会挂**的：规格 §3.6.4 的后果那一句
    /// （「一个只有内网、工位电脑从没联过公网的打包间，这套系统开不了工」）
    /// 说明这次请求是**开工的硬前提**，所以地址要尽量稳。
    /// </remarks>
    public static readonly IReadOnlyList<string> DefaultUrls =
    [
        "https://www.baidu.com/",
        "https://www.aliyun.com/",
        "https://www.qq.com/",
    ];

    private readonly IReadOnlyList<string> _urls;
    private readonly TimeSpan _timeout;
    private readonly HttpClient _http;

    public HttpDateClockSource(
        IReadOnlyList<string>? urls = null,
        TimeSpan? timeout = null,
        HttpClient? http = null)
    {
        _urls = urls ?? DefaultUrls;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);

        // ⚠️ **不复用 HttpClient 实例就得自己控制超时** —— 默认 100 秒会把
        // 「开工前校准」这一步变成用户眼里的一次卡死。
        _http = http ?? new HttpClient { Timeout = _timeout };
    }

    public string Description => "公网时间服务（HTTP Date）";

    public async Task<DateTimeOffset> QueryAsync(CancellationToken cancellationToken = default)
    {
        Exception? last = null;

        foreach (var url in _urls)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);

            try
            {
                using var response = await _http.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                // ⚠️ **用框架解析好的 `Headers.Date`，不自己解析字符串**
                // （2026-09-27 核过）：`HttpResponseHeaders.Date` 内部就是按
                // RFC 1123 用 invariant 文化解析的，而且**不会**把本机区域格式
                // 卷进来 —— 正是这条路径要的性质（要的是**外部**时刻，
                // 不该受本机设置影响）。所以不必也不该再维护一个平行的解析器。
                //
                // ⚠️ 取不到就是**取不到**，不猜：这台机器上「开工前校准」是硬闸门
                // （未校准不得开始录制），猜一个时刻比没有更糟 —— 它看起来是校准过的。
                if (response.Headers.Date is { } date)
                {
                    return date;
                }

                last = new InvalidOperationException($"{url} 的响应里没有 Date 头");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            $"取不到公网时间（试过 {_urls.Count} 个地址）。最后一次失败：{last?.Message}");
    }
}
