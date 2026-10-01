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
    int ExpiresInSeconds)
{
    /// <summary>
    /// 二维码里要放的那串内容 —— 用户拿手机扫的就是它。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 拼接规则照 009《设备码模式授权》的注意事项**逐字**：
    /// <c>https://openapi.baidu.com/device?display=mobile&amp;code=&lt;user_code&gt;</c>，
    /// 「其中 code 为接口返回内容的 user_code 字段」。
    /// </para>
    /// <para>
    /// ⚠️ <b>里面必须是 <see cref="UserCode"/>，不是 <see cref="DeviceCode"/>。</b>
    /// 放错了有两个后果，而且第二个更糟：码扫了没用（授权页认的是 user_code），
    /// 而**轮询用的那串密钥被画到了屏幕上** —— 站在旁边的人拍到就能冒领这次登录。
    /// 这一条由 <c>BaiduPanWireTests</c> 钉着。
    /// </para>
    /// <para>
    /// <b>为什么不用响应里那个 <see cref="QrCodeUrl"/></b>（百度渲染好的现成图片）：
    /// 用它就得在用户登录的这一刻再去网上取一张图。而文档把**内容**规则也给全了 ——
    /// 本地已经有二维码能力（<c>EnrollQr</c>，与非对称的一维码共用一套约定），
    /// 自己画就不用为「显示一个码」多接一条网络依赖，登录出问题时也少一个环节要排查。
    /// </para>
    /// <para>
    /// ⚠️ 这条规则**只在文档里**，真机没验过（扫码那条路要到有真手机时才知道通不通）。
    /// 判定办法写在 <c>docs/实现决策.md</c> §87：拿手机扫这张码，
    /// 手机上应当直接打开百度授权页**并已经把码填好**，只剩「登录 + 同意」两步。
    /// </para>
    /// </remarks>
    public string QrPayload =>
        $"https://openapi.baidu.com/device?display=mobile&code={Uri.EscapeDataString(UserCode)}";
}

/// <summary>一对令牌。</summary>
/// <param name="AccessToken">访问令牌。</param>
/// <param name="RefreshToken">刷新令牌（下一次免登录靠它）。</param>
/// <param name="ExpiresInSeconds">访问令牌还有多久过期。</param>
public sealed record BaiduToken(string AccessToken, string RefreshToken, int ExpiresInSeconds);

/// <summary>预创建的结果。</summary>
/// <param name="UploadId">这次上传的会话号。</param>
/// <param name="Pending">
/// **要传的分片序号**（索引从 0 起）。
/// </param>
/// <remarks>
/// <para>
/// ⚠️ <b>这个字段的语义照文档是「还需要传的」，不是「已经有了的」</b>
/// （018 预上传，响应参数表原文：「需要上传的分片序号列表，索引从 0 开始」）。
/// 反着读的后果**不是慢一点，是整个传不上去**：一个全新的 3 片文件网盘回的正是
/// <c>[0,1,2]</c>，把它当成「这三片它都有了」就会一片都不传，
/// 接着 <c>create</c> 去合并一份一个分片都没收到的文件 —— 报的是
/// <c>31190</c> / <c>31363</c>（文件不存在 / 分片缺失），
/// 而真正的原因（我们一片都没传）在报错里一个字都看不出来。
/// </para>
/// <para>
/// ⚠️ 文档另有一句「<c>block_list</c> 为空时等价于 <c>[0]</c>」——
/// 空数组不是「一片都不用传」，这一条由 <c>BaiduPanClient</c> 负责落。
/// </para>
/// <para>
/// 它仍然是断点续传的省力点：带着同一个 <c>uploadid</c> 重开一次预创建时，
/// 网盘把**还缺的那几片**列在这里，于是补传只传缺的，不必把 4GB 重来一遍。
/// </para>
/// </remarks>
public sealed record BaiduPrecreate(string UploadId, IReadOnlySet<int> Pending);

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
    /// <para>
    /// ⚠️ 判据**只认这四个码**，照平台简介 &gt; 错误码（063）的公共错误码表：
    /// </para>
    /// <list type="bullet">
    /// <item><c>-6</c> —— 身份验证失败（「access_token 是否有效 / 授权是否成功」）。</item>
    /// <item><c>20016</c> —— access_token 已过期。</item>
    /// <item><c>20017</c> —— access_token 无效，**可能因用户解绑或授权撤销**。</item>
    /// <item><c>31045</c> —— access_token 验证未通过。</item>
    /// </list>
    /// <para>
    /// ⚠️ <b><c>111</c> 不在里面。</b>它在这套文档里是「有其他异步任务正在执行」，
    /// 文档给的处置是「稍后，可重新请求」—— 把它当成掉线会让一个**好端端的**
    /// 登录被界面提示去重新授权，而用户照做之后问题还在（因为问题本来就不在那）。
    /// </para>
    /// <para>
    /// 同理，「文件不存在」（<c>-9</c>/<c>31066</c>）、「路径不对」（<c>31064</c>）、
    /// 限流（<c>31034</c>）都不是凭据问题，全部排除在外。
    /// </para>
    /// </remarks>
    public bool IsCredentialProblem => Errno is -6 or 20016 or 20017 or 31045;

    /// <summary>
    /// 这个错是不是「那个路径有问题」——也就是**可能是父目录还不存在**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 有它是因为一件文档**没写**的事：`precreate` 会不会顺手把中间的父目录
    /// 建出来？018 对 <c>autoinit</c> 只写了「本接口固定为 1」一句，**没说它做什么**；
    /// 全套文档里也没有任何一处写「上传会自动建目录」。而 020 专门有一个建文件夹
    /// 的接口 —— 这暗示**得自己建**，但同样没有正面写。
    /// </para>
    /// <para>
    /// 于是这里不猜：**平时一个目录都不建**（一次额外请求都不发），
    /// 只有真回了一个路径类错误时，才按 020 把目录补出来再试一次
    /// （<c>BaiduPanUploader</c>）。这样两种可能哪一边是真的都走得通，
    /// 而且不会在没验证过的情况下先把每个目录都建一遍（未过审的应用
    /// 每小时只有 10 次调用，浪费不起）。
    /// </para>
    /// <para>
    /// 认的这几个码按 063 表里的**字面描述**挑：<c>-7</c>「文件或目录名错误或
    /// 无权访问」、<c>-9</c>「文件或目录不存在」、<c>31064</c>「上传路径错误」、
    /// <c>31066</c>「文件名不存在」、<c>31190</c>「文件不存在」。
    /// ⚠️ <b>不带 20020 / 20022 / 20023</b>：那三个是「不在授权范围」「参数格式」
    /// 「漏传参数」，建目录**修不好**它们，带进来只会多几次白发的请求。
    /// </para>
    /// </remarks>
    public bool IsPathProblem => Errno is -7 or -9 or 31064 or 31066 or 31190;
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

    /// <summary>
    /// 问网盘要一个**这次能用的上传域名**（016「获取上传域名」）。
    /// </summary>
    /// <returns>上传服务地址，形如 <c>https://c3.pcs.baidu.com</c>（带协议）。</returns>
    /// <remarks>
    /// ⚠️ <b>这一步不能省，也不能固定写死一个域名。</b>016 原话：
    /// 「分片上传和单步上传请求必须使用接口返回的域名，不应在客户端固定写死上传服务器地址」，
    /// 「上传文件数据前必须调用本接口」。固定的那个域名是**取域名的那个服务**，
    /// 不是收分片的服务 —— 拿它去传分片正是那种「上线才发现」的错。
    /// </remarks>
    Task<string> LocateUploadAsync(
        string accessToken,
        string remotePath,
        string uploadId,
        CancellationToken cancellationToken = default);

    /// <summary>合并分片，这一步之后网盘上才真的出现那个文件。</summary>
    Task CreateAsync(
        string accessToken,
        string remotePath,
        long size,
        IReadOnlyList<string> blockList,
        string uploadId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 建一个文件夹（020）。已经在了**不算失败**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 建目录的冲突策略与建文件**不是一套**（020 的 <c>rtype</c>：目录只有
    /// 「0 冲突时失败 / 1 冲突时重命名」，**没有覆盖**），所以这里固定 <c>rtype=0</c>
    /// 并把「已存在」当成功 —— 换成 1 会在网盘上堆出 <c>发货(1)</c>、<c>发货(2)</c>，
    /// 而回查是按路径找的，那些目录里的文件**永远回查不到**（I8 会说「云端没有」）。
    /// </remarks>
    Task CreateDirectoryAsync(
        string accessToken,
        string directory,
        CancellationToken cancellationToken = default);

    /// <summary>传一个分片。</summary>
    /// <param name="uploadHost">
    /// <see cref="LocateUploadAsync"/> 给的那个域名。⚠️ 每个分片用**同一个**。
    /// </param>
    /// <returns>
    /// 网盘回显的该分片 MD5；它没给就是 <see langword="null"/>。
    /// </returns>
    /// <remarks>
    /// ⚠️ 回显的 MD5 是**唯一能证明「网盘收下的就是我发的这一片」的东西**。
    /// 不看它的话，一片传歪了的表现是「网盘上文件在那儿、大小也对，播出来是坏的」——
    /// 而那时本机那一份已经因为「云端有了」被允许清理（I8）。
    /// </remarks>
    Task<string?> UploadSliceAsync(
        string accessToken,
        string uploadHost,
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
