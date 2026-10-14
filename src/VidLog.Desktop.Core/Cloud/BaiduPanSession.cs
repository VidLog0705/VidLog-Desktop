using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Cloud;

/// <summary>一次登录的结果。</summary>
/// <param name="AccessToken">访问令牌。</param>
/// <param name="RefreshToken">刷新令牌（下次免登录靠它）。</param>
/// <param name="ExpiresAt">访问令牌何时过期（**绝对时间**，不是「还有几秒」）。</param>
/// <param name="DisplayName">网盘上的昵称 —— 给用户确认「登的是不是我那个号」。</param>
public sealed record BaiduLogin(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string DisplayName);

/// <summary>
/// 令牌落在哪。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>这个文件是一份 bearer 凭据</b>：拿到它的人可以读写这个账号
/// <c>/apps/&lt;应用名&gt;/</c> 下的东西。所以
/// </para>
/// <list type="bullet">
/// <item>它落在**应用自己的数据目录**（<c>%LOCALAPPDATA%</c> 那一支），不进仓库、不进设置文件；</item>
/// <item>设置文件（<c>settings.json</c>）会被用户复制、会被贴进工单，而它**绝不**写进去；</item>
/// <item>退出登录就是**删掉它**（不删网盘上的任何东西）。</item>
/// </list>
/// <para>
/// ⚠️ <b>它是加密的（2026-10-12 起，需求方拍板「token 别明文存」）。</b>
/// 走 Windows 的数据保护 API（<see cref="ProtectedData"/>，<c>CurrentUser</c> 作用域）：
/// 密钥由**当前 Windows 用户的登录凭据**派生，所以换一个 Windows 账号登进来解不开。
/// </para>
/// <para>
/// ⚠️ <b>为什么不用「同目录再放一份密钥文件」那种做法</b>（PackingProof 就是那样）：
/// 那把钥匙和锁在同一间屋子里，挡得住「配置文件被误发出去」，
/// 挡不住任何能读这台机器文件的人 —— 而后者才是这个文件真正的暴露面。
/// 本类原先那句「任何能读这个文件的人本来也能读这台机器上的全部录像」现在不成立了：
/// 加密之后，能读到密文的人**未必**能拿到令牌。
/// （这一点与 <c>docs/实现决策.md</c> §87 记的那条不同 —— 那条已被本条取代，
/// 那份文档本仓无权改动，留待需求方处置。）
/// </para>
/// <para>
/// ⚠️ <b>老格式（明文 JSON）继续读得动，而且读出来就**立刻改写成加密的**</b>：
/// 不迁移的话，那个明文文件会一直躺到 30 天后令牌自然过期为止。
/// </para>
/// </remarks>
public sealed class BaiduPanTokenStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// 加密时混进去的一串「附加熵」。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它**不是密钥**（密钥由 DPAPI 从 Windows 账号派生），只是一段谁都得知道的
    /// 上下文：别的程序即使在同一用户下解开同一份字节，也得知道这串东西。
    /// 换掉它 = 已有令牌全部作废、要重新登录，所以**别动**。
    /// </remarks>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VidLog/cloud/baidu-pan");

    /// <summary>明文 JSON 文件长什么样（用来认老格式）。</summary>
    private const char LegacyFirstChar = '{';

    private readonly IAppLogger _logger;

    public BaiduPanTokenStore(string path, IAppLogger? logger = null)
    {
        Path = path;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>令牌文件。</summary>
    public string Path { get; }

    public async Task<BaiduLogin?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            var text = (await File.ReadAllTextAsync(Path, cancellationToken)).Trim();

            // ⚠️ 旧的那份是明文 JSON（**以 `{` 开头**），新的是 DPAPI 密文的 Base64
            // （Base64 字母表里没有 `{`）。所以这一个字符就是格式判据 ——
            // 不必再往文件里塞一个版本号头：多一个字段就多一处「写的人忘了写、读的人不认识」。
            var legacy = text.StartsWith(LegacyFirstChar);

            var login = Parse(legacy ? text : Decrypt(text));

            if (login is not null && legacy)
            {
                // 读到明文就**顺手改成加密的**。不迁的话那份明文会一直躺到
                // 30 天后令牌自然过期为止 —— 而这正是这次改造要消灭的东西。
                _logger.Log(LogLevel.Info, "网盘", "登录信息还是老的明文格式，已改成加密存放");
                await SaveAsync(login, cancellationToken);
            }

            return login;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
            or CryptographicException or FormatException)
        {
            // ⚠️ 读坏了就当**没登录**，不抛：一个坏掉的令牌文件不该让整个程序起不来，
            // 也不该让用户看到「百度网盘」那一页变成一片红。重新登录一次就好。
            // 但要留痕 —— 否则「为什么每次开机都要重新登录」查不出来。
            //
            // ⚠️ 这里也接住了**换了 Windows 账号 / 改过密码之后解不开**那一类
            // （`CryptographicException`）：表现正是「又要重新登录一次」，而那是正确行为。
            _logger.Log(LogLevel.Warn, "网盘", $"登录信息读不出来，按未登录处理：{ex.Message}");

            return null;
        }
    }

    public async Task SaveAsync(BaiduLogin login, CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 先写临时名再改名：写一半断电时留下的是一个 `.part`，
        // 而不是一个看着像密文、解出来是半截令牌的文件。
        var staging = Path + ".part";
        await File.WriteAllTextAsync(staging, Encrypt(login), cancellationToken);
        File.Move(staging, Path, overwrite: true);
    }

    // ⚠️ CA1416：`ProtectedData` 只有 Windows 上有实现，而本工程的目标框架是
    // **不带平台后缀**的 `net9.0`，于是分析器认为它可能在非 Windows 上跑。
    // 这个担心是形式上的：本工程已经依赖 `System.Management`（同样只有 Windows 有），
    // 整个桌面端就是 Windows 应用。所以在这里挡掉这一条，而不是把它推给每个调用点。
#pragma warning disable CA1416
    /// <summary>令牌 → 要落盘的那段文本（DPAPI 加密后再 Base64）。</summary>
    private static string Encrypt(BaiduLogin login) => Convert.ToBase64String(
        ProtectedData.Protect(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(login, Options)),
            Entropy,
            DataProtectionScope.CurrentUser));

    /// <summary>落盘的那段文本 → 明文 JSON。解不开会抛，由调用方按「未登录」落。</summary>
    private static string Decrypt(string text) => Encoding.UTF8.GetString(
        ProtectedData.Unprotect(
            Convert.FromBase64String(text),
            Entropy,
            DataProtectionScope.CurrentUser));
#pragma warning restore CA1416

    /// <summary>明文 JSON → 令牌；解不出来、或者缺关键字段，一律 <see langword="null"/>。</summary>
    private static BaiduLogin? Parse(string json) =>
        JsonSerializer.Deserialize<BaiduLogin>(json, Options)
            is { AccessToken.Length: > 0, RefreshToken.Length: > 0 } login
                ? login
                : null;

    /// <summary>退出登录：把令牌文件删掉。⚠️ **不碰网盘上的任何文件。**</summary>
    public void Forget()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉要说出来：用户以为已经退出了，而令牌还躺在盘上。
            _logger.Log(LogLevel.Warn, "网盘", $"退出登录时删不掉令牌文件：{ex.Message}");
            throw;
        }
    }
}

/// <summary>
/// 令牌的生命周期：登录、续期、退出。
/// </summary>
/// <remarks>
/// <para>
/// 续期**只在这一处**，而且**串行**：后台的自动补传与用户手点的「立即对比同步」
/// 会同时发现令牌过期，两个刷新请求一起发出去时，后一个会把前一个刚拿到的
/// 令牌作废掉（网盘的刷新令牌是一次性的那种），表现是**随机地掉线**。
/// </para>
/// </remarks>
public sealed class BaiduPanSession
{
    /// <summary>提前多久就续期。</summary>
    /// <remarks>
    /// 5 分钟：一次上传可能要跑十几分钟，用「还剩 30 秒」当阈值的话
    /// 一批上传传到一半令牌就过期了，整条队列全失败。
    /// </remarks>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    private readonly IBaiduPanApi _api;
    private readonly BaiduPanTokenStore _store;
    private readonly IAppLogger _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private BaiduLogin? _login;

    public BaiduPanSession(
        IBaiduPanApi api,
        BaiduPanTokenStore store,
        IAppLogger? logger = null,
        Func<DateTimeOffset>? now = null)
    {
        _api = api;
        _store = store;
        _logger = logger ?? NullLogger.Instance;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>登录信息（没登录时为 <see langword="null"/>）。**不含令牌原文以外的秘密。**</summary>
    public BaiduLogin? Login => _login;

    /// <summary>登着没有。</summary>
    public bool IsLoggedIn => _login is not null;

    /// <summary>页面打开时先恢复上次的登录。返回恢复出来的那一份（没有就是 <see langword="null"/>）。</summary>
    public async Task<BaiduLogin?> RestoreAsync(CancellationToken cancellationToken = default)
    {
        _login = await _store.LoadAsync(cancellationToken);
        return _login;
    }

    /// <summary>
    /// 拿回环回调接住的授权码换令牌，换到了就落盘。
    /// </summary>
    /// <param name="code">授权码。**它是凭据，不许进日志。**</param>
    /// <param name="redirectUri">
    /// 必须与拼授权 URL 时用的那一个逐字相同（<c>BaiduLoopbackListener.RedirectUri</c>）。
    /// </param>
    /// <remarks>
    /// ⚠️ 授权码**十分钟过期且一次性** ⇒ 换令牌失败要能就地重来（界面上的登录按钮一直在），
    /// 所以这里**不吞异常**：失败原样抛给界面去说人话。
    /// </remarks>
    public async Task<BaiduLogin> LoginAsync(
        string code, string redirectUri, CancellationToken cancellationToken = default)
    {
        var token = await _api.ExchangeCodeAsync(code, redirectUri, cancellationToken);

        // ⚠️ 昵称拿不到**不该让登录失败**：令牌已经到手了，那才是登录成功与否的判据。
        // 为一句昵称把用户挡在门外，他只会反复点「登录百度网盘」。
        string name;
        try
        {
            name = await _api.GetDisplayNameAsync(token.AccessToken, cancellationToken);
        }
        catch (BaiduPanException ex)
        {
            _logger.Log(LogLevel.Warn, "网盘", $"登录成功但没读到昵称：{ex.Message}");
            name = "（没读到昵称）";
        }

        var login = new BaiduLogin(
            token.AccessToken,
            token.RefreshToken,
            _now() + TimeSpan.FromSeconds(token.ExpiresInSeconds),
            name);

        await _store.SaveAsync(login, cancellationToken);
        _login = login;

        _logger.Log(LogLevel.Info, "网盘", "百度网盘已登录",
            new Dictionary<string, object?> { ["账号"] = name });

        return login;
    }

    /// <summary>
    /// 拿一个**现在能用**的访问令牌（快过期就先续）。
    /// </summary>
    /// <exception cref="BaiduPanException">没登录，或者续期失败。</exception>
    public async Task<string> TokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_login is null)
            {
                throw new BaiduPanException(0, "还没登录百度网盘。");
            }

            if (_login.ExpiresAt - _now() > RenewBefore)
            {
                return _login.AccessToken;
            }

            BaiduToken token;

            try
            {
                token = await _api.RefreshAsync(_login.RefreshToken, cancellationToken);
            }
            catch (BaiduPanException ex) when (ex.IsCredentialProblem)
            {
                // ⚠️ 授权**真的**没了（不是网断了、不是限流）：文档说 refresh_token
                // 是一次性的，且刷新失败时旧的那串**一起失效**，所以重试是没用的。
                //
                // 不在这里丢掉本机那串令牌的话，后果是两件都在悄悄发生的事：
                // ① 界面上一直写着「已登录」，用户以为自己登着，而实际上什么都传不上去；
                // ② 定时检查每一分钟拿同一串死掉的 refresh_token 再问一次 ——
                //    未过审的应用每小时只有 10 次调用（权限与配额），全喂给这个循环了。
                _logger.Log(
                    LogLevel.Warn, "网盘", $"续期被拒，本机登录信息作废、需要重新授权：{ex.Message}");

                await LogoutAsync();

                throw;
            }

            // ⚠️ 网盘**有时不回新的 refresh_token**（沿用旧的那个）。
            // 拿空串去覆盖会让下一次续期彻底失败 —— 而那时用户看到的是
            // 「莫名其妙要重新登录」。
            var refreshed = _login with
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken.Length > 0 ? token.RefreshToken : _login.RefreshToken,
                ExpiresAt = _now() + TimeSpan.FromSeconds(token.ExpiresInSeconds),
            };

            await _store.SaveAsync(refreshed, cancellationToken);
            _login = refreshed;

            return refreshed.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>退出登录。⚠️ **只删本机的令牌，不碰网盘上的任何文件。**</summary>
    public Task LogoutAsync()
    {
        _store.Forget();
        _login = null;

        _logger.Log(LogLevel.Info, "网盘", "已退出百度网盘（网盘上的文件一个都没动）");

        return Task.CompletedTask;
    }
}
