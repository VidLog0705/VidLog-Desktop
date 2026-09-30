namespace VidLog.Desktop.Core.Cloud;

/// <summary>
/// 一次设备码登录的起点（OAuth 2.0 device code）。
/// </summary>
/// <param name="DeviceCode">程序拿去轮询的那串码（**不给用户看**）。</param>
/// <param name="UserCode">用户要在授权页上确认的那串短码。</param>
/// <param name="VerificationUrl">用户在浏览器里打开的授权页。</param>
/// <param name="QrCodeUrl">授权页的二维码图片地址。</param>
/// <param name="IntervalSeconds">两次轮询之间至少要隔多少秒。</param>
/// <param name="ExpiresInSeconds">这串码多久作废。</param>
public sealed record BaiduDeviceCode(
    string DeviceCode,
    string UserCode,
    string VerificationUrl,
    string QrCodeUrl,
    int IntervalSeconds,
    int ExpiresInSeconds);

/// <summary>一对令牌。</summary>
/// <param name="AccessToken">访问令牌。</param>
/// <param name="RefreshToken">刷新令牌（下一次免登录靠它）。</param>
/// <param name="ExpiresInSeconds">访问令牌还有多久过期。</param>
public sealed record BaiduToken(string AccessToken, string RefreshToken, int ExpiresInSeconds);

/// <summary>预创建的结果。</summary>
/// <param name="UploadId">这次上传的会话号。</param>
/// <param name="AlreadyThere">
/// 网盘上**已经有**的分片下标。
/// </param>
/// <remarks>
/// ⚠️ 这个字段是网盘「秒传」的来源，也是**重传时的省力点**：
/// 上一次传到一半断了，这一次要跳过网盘已经收下的那些分片。
/// 忽略它会让每次重传都从头传一遍 —— 一条 4GB 的录像重传三次就是 12GB 上行。
/// </remarks>
public sealed record BaiduPrecreate(string UploadId, IReadOnlySet<int> AlreadyThere);

/// <summary>网盘那边报错。</summary>
/// <remarks>
/// ⚠️ 是普通类不是 <c>record</c>：<c>record</c> 只能继承对象或另一条 record，
/// 而异常得继承 <see cref="Exception"/>。
/// </remarks>
public sealed class BaiduPanException : Exception
{
    public BaiduPanException(int errno, string message) : base(message) => Errno = errno;

    public BaiduPanException(int errno, string message, Exception inner) : base(message, inner) =>
        Errno = errno;

    /// <summary>接口回的 <c>errno</c>（HTTP 那一层的错误没有这个码，为 0）。</summary>
    public int Errno { get; }

    /// <summary>
    /// 这个错是不是「凭据不管用了」。
    /// </summary>
    /// <remarks>
    /// ⚠️ 判据**只认这两个码**：网盘的 <c>errno</c> 是一个很大的整数空间，
    /// 把「文件不存在」（31066）也当成凭据问题，会让界面反复提示用户重新登录，
    /// 而他真正该做的是别的。
    /// <list type="bullet">
    /// <item><c>111</c> —— access token 失效。</item>
    /// <item><c>-6</c> —— 身份验证失败。</item>
    /// </list>
    /// </remarks>
    public bool IsCredentialProblem => Errno is 111 or -6;
}

/// <summary>
/// 百度网盘那一层的**窄缝隙**。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由不是「将来可能换一家网盘」，而是**上传这一整套流程要测得动**：
/// 预创建 → 传分片 → 合并 → 列目录对账 → 重试，这里面每一步的顺序、跳过、
/// 并发与失败处理都是真逻辑，而它们全都藏在 HTTP 后面。
/// 把 HTTP 收进这一个接口，上面那套就能用假件跑到（<c>CloudUploadTests</c>）。
/// </para>
/// <para>
/// ⚠️ <b>这一个接口只做「翻译」，不做决定。</b>不带重试、不带并发控制、
/// 不带「该不该传」的判断 —— 那些是上层的事，混进来就会有两份重试策略互相打架。
/// </para>
/// </remarks>
public interface IBaiduPanApi
{
    /// <summary>申请一串设备码（登录的第一步）。</summary>
    Task<BaiduDeviceCode> StartDeviceLoginAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 问一次「用户批准了没有」。
    /// </summary>
    /// <returns>批准了返回令牌；还没批准返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// ⚠️ 「还没批准」**不是错误** —— 它是这个流程里正常的一步。
    /// 抛异常的话调用方要拿异常当控制流，而那时「网络断了」与「人还没点」
    /// 会走同一条 catch。
    /// </remarks>
    Task<BaiduToken?> PollDeviceTokenAsync(
        string deviceCode, CancellationToken cancellationToken = default);

    /// <summary>用刷新令牌换一对新令牌（免登录）。</summary>
    Task<BaiduToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>网盘上的用户名（用来在界面上说「登录成了，你是某某」）。</summary>
    Task<string> GetDisplayNameAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>预创建：告诉网盘要传一个多大的文件，拿回 uploadid 与已有分片。</summary>
    Task<BaiduPrecreate> PrecreateAsync(
        string accessToken,
        string remotePath,
        long size,
        IReadOnlyList<string> blockList,
        CancellationToken cancellationToken = default);

    /// <summary>合并分片，这一步之后网盘上才真的出现那个文件。</summary>
    Task CreateAsync(
        string accessToken,
        string remotePath,
        long size,
        IReadOnlyList<string> blockList,
        string uploadId,
        CancellationToken cancellationToken = default);

    /// <summary>传一个分片。</summary>
    Task UploadSliceAsync(
        string accessToken,
        string remotePath,
        string uploadId,
        int partSeq,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列一个目录下的**文件名**（不含子目录）。
    /// </summary>
    /// <returns>该目录下每个条目的文件名。</returns>
    /// <remarks>
    /// ⚠️ 返回的是**名字集合**而不是「在不在」：对比补传一次要问上千条，
    /// 一条一次请求就是把网盘当数据库用（而且它有限流）。
    /// </remarks>
    Task<IReadOnlySet<string>> ListFilesAsync(
        string accessToken, string directory, CancellationToken cancellationToken = default);
}
