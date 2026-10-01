using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Cloud;

/// <summary>
/// 电脑端把百度网盘的令牌**借**给已入网的手机时，回的那一份。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么是「借」而不是「手机自己登录」</b>：换令牌那一步非要
/// <c>client_secret</c> 不可（百度文档 009 的第三步），而 <c>AGENTS.md</c> §2 写死
/// 「客户端**绝不内置** secret」。手机是发给工人随身带的，正是那一条要防的东西。
/// 所以手机端不登录 —— 它向**已经登着的电脑端**要一个现成的 <c>access_token</c>。
/// </para>
/// <para>
/// ⚠️ <b>四档状态分开报</b>，是为了让手机端能说人话。用户看到一句笼统的
/// 「登录失败」只会去查自己的网络，而真正的原因在**另一台机器**上
/// （踩坑 #13 的同一条精神：宁可说清楚，也不要让人去猜）。
/// </para>
/// </remarks>
/// <param name="Status">见下面四个常量之一。</param>
/// <param name="AccessToken">只有 <see cref="Ok"/> 时才非空。</param>
/// <param name="AppName">
/// 开放平台后台填的那个「产品名称」—— 手机端要用它拼远端根 <c>/apps/&lt;应用名&gt;/</c>。
/// ⚠️ 由电脑端告诉手机端，而不是手机端自己写死：换了应用只改一处，
/// 而拼错这个名字报出来的是「目录不存在」，**查起来完全看不出是名字错了**
/// （`BaiduPanLayout` 的类注释里写着这件事）。
/// </param>
/// <param name="Message">给人看的一句话；成功时为空。</param>
public sealed record NetdiskGrant(string Status, string? AccessToken, string? AppName, string? Message)
{
    /// <summary>令牌到手了。</summary>
    public const string Ok = "ok";

    /// <summary>电脑端有凭据，但**没登着**（或者授权已经失效）。</summary>
    public const string NotLoggedIn = "not_logged_in";

    /// <summary>电脑端这边网盘这一档压根没启用（归档层不是网盘，或者没配凭据）。</summary>
    public const string Unavailable = "unavailable";

    /// <summary>登着，但这次问不到（网断了、被限流了）。**下次再问可能就好了。**</summary>
    public const string Failed = "failed";

    public static NetdiskGrant Granted(string accessToken, string appName) =>
        new(Ok, accessToken, appName, null);

    public static NetdiskGrant LoggedOut(string message) => new(NotLoggedIn, null, null, message);

    public static NetdiskGrant Off(string message) => new(Unavailable, null, null, message);

    public static NetdiskGrant Error(string message) => new(Failed, null, null, message);
}

/// <summary>
/// 「借一个网盘令牌出去」这一件事本身：要登着、要能续期、给不出来时怎么说。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>令牌绝不进日志。</b>这个类只记「借出去了一次」和失败原因 ——
/// 令牌本身、以及它的长度，一个字都不记（理由同 <see cref="BaiduPanCredentials.Describe"/>：
/// 长度也是关于值的信息，而它换不来用户能做的任何事）。
/// </para>
/// <para>
/// ⚠️ <b>借出去的是一把钥匙，不是一张便条。</b>拿到它的手机能读写这个账号下
/// <c>/apps/&lt;应用名&gt;/</c> 里的全部东西。手机端之所以够格拿，是因为它**已经过了
/// 入网那道人工批准**（规格 §3.4.5 ②）—— 见 <c>PlaybackServer</c> 里那条路由挂在
/// 凭据闸之后。这道端点**不再多加一层**，但这句话要留着：
/// 哪天有人想把它挪到闸前面，先读这一段。
/// </para>
/// <para>
/// ⚠️ <b>续期失败时绝不把旧令牌借出去。</b>手上那串可能已经失效了 ——
/// 借出去的表现是手机端拿到一堆看不懂的 <c>31045</c>，而真正该做的事
/// （去电脑端重新登录）没人告诉用户。
/// </para>
/// </remarks>
public sealed class NetdiskTokenSource
{
    private readonly BaiduPanSession _session;
    private readonly BaiduPanLayout _layout;
    private readonly IAppLogger _logger;

    public NetdiskTokenSource(BaiduPanSession session, BaiduPanLayout layout, IAppLogger? logger = null)
    {
        _session = session;
        _layout = layout;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>要一份令牌；给不出来时说清楚是哪一种给不出来。</summary>
    public async Task<NetdiskGrant> GrantAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.IsLoggedIn)
        {
            return NetdiskGrant.LoggedOut(
                "电脑端还没登录百度网盘。去电脑上打开「设置 → 百度网盘上传」，点【登录百度网盘】。");
        }

        string token;

        try
        {
            // 会按需续期（refresh_token 是一次性的，刷新失败时旧的那串一起失效）。
            token = await _session.TokenAsync(cancellationToken);
        }
        catch (BaiduPanException ex) when (ex.IsCredentialProblem)
        {
            // 「授权真没了」≠「这次问不到」。前者要用户去重新登录，后者等等就好 ——
            // 折成同一句话的话，用户会对着一句「网络错误」反复重试一件永远不成的事。
            _logger.Log(LogLevel.Warn, "网盘", $"借令牌失败：授权已失效（{ex.Message}）");
            return NetdiskGrant.LoggedOut(
                "电脑端的网盘授权已经失效了。去电脑上打开「设置 → 百度网盘上传」重新登录一次。");
        }
        catch (Exception ex) when (ex is BaiduPanException or HttpRequestException)
        {
            _logger.Log(LogLevel.Warn, "网盘", $"借令牌失败：{ex.Message}");
            return NetdiskGrant.Error($"问不到百度网盘：{ex.Message}");
        }

        _logger.Log(LogLevel.Info, "网盘", "把网盘令牌借给了已入网的手机");

        return NetdiskGrant.Granted(token, _layout.AppName);
    }
}
