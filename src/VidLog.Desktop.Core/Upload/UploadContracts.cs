namespace VidLog.Desktop.Core.Upload;

/// <summary>
/// 上传接口的错误码。
/// </summary>
/// <remarks>
/// 母仓 <c>docs/05-上传接口形状.md</c> §3 的失败分类表就是这几个码。
/// <para>
/// ⚠️ 它们是**协议的一部分**，不是给人看的文案：发送方按码决定「重试还是进终态」，
/// 改字符串等于改协议。给人看的中文在 <c>detail</c> 里，那一栏随便改。
/// </para>
/// </remarks>
public static class UploadErrors
{
    /// <summary>请求体看不懂 / 缺字段 / 哈希形态不对。**不可重试** —— 这是两端实现不一致。</summary>
    public const string BadRequest = "bad_request";

    /// <summary>凭据无效。发送方要**重新走一遍入网**，不是重试。</summary>
    public const string BadCredential = "bad_credential";

    /// <summary>这个 evidenceId 已经发布过，**且内容不同**。不可重试。</summary>
    public const string AlreadyPublished = "already_published";

    /// <summary>拼起来的东西哈希对不上 —— 传输途中被改过，或发送方自己算错。</summary>
    public const string HashMismatch = "hash_mismatch";

    /// <summary>分片不齐。</summary>
    public const string ChunkMissing = "chunk_missing";

    /// <summary><c>evidenceId</c> 与 <c>sessionId</c> + <c>sequence</c> 对不上。</summary>
    public const string EvidenceMismatch = "evidence_mismatch";

    /// <summary>拼起来的东西 FFmpeg 解不开。**不可重试** —— 重传同样的字节出来还是解不开。</summary>
    public const string Unplayable = "unplayable";

    /// <summary>路径不认识。⚠️ 手机端把 404 读作「**电脑端版本太旧**」—— 这是一条给用户的提示，不是给日志的。</summary>
    public const string NotFound = "not_found";

    /// <summary>令牌不对 —— 不是屏幕上那张码里的、已过期、或已经被用掉（§2.3）。</summary>
    public const string BadToken = "bad_token";

    /// <summary>没有待批准的入网请求：没发起过、已过期、或凭据已经被领走过（§2.3）。</summary>
    public const string NoPendingRequest = "no_pending_request";

    /// <summary>
    /// 机位满了，这台手机接不进来（`docs/04-许可设计.md` §5.1）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>不可重试</b>，但也**不该让用户以为录不了了**：手机上那条提示要说的是
    /// 「去电脑端激活或升级」，而不是「连接失败」。已经在录的手机不受影响（L5）。
    /// </remarks>
    public const string SeatLimit = "seat_limit";
}

/// <summary>分片清单查询（`docs/05-上传接口形状.md` §2.4）。</summary>
/// <remarks>
/// 规格 §3.4.2 的落点：**「已有哪些分片」的权威在接收方**，
/// 发送方绝不拿自己记的进度去续传。所以这个请求是「先问」，
/// 而不是「我传到第 3 片了，你接着收」。
/// </remarks>
/// <param name="TotalBytes">
/// 整个文件的大小。⚠️ 不是冗余 —— 接收方靠它算出**每一片该有多大**，
/// 才能把「传了一半就断了的片」判成**没有**。少了它，半截的片会被报成「已有」，
/// 发送方于是不重传，一直卡到重试耗尽。
/// </param>
public sealed record ProbeRequest(string EvidenceId, int ChunkCount, string ContentHash, long TotalBytes);

/// <param name="ChunkSize">**接收方定**，发送方跟着重切。写死两端会各自漂移，而漂移的表现是「每次续传都要重传」。</param>
/// <param name="Have">已经收全的分片下标，升序。</param>
public sealed record ProbeResponse(long ChunkSize, int ChunkCount, IReadOnlyList<int> Have);

/// <param name="Sha256">接收方算出来的分片哈希，64 位小写十六进制。发送方拿它确认这一片没在途中坏掉。</param>
public sealed record ChunkAccepted(int Index, string Sha256);

/// <summary>
/// 回查归档层（规格 §3.5.4；手机端「手动删除」的前置闸，§3.5.6③）。
/// </summary>
/// <param name="Location">
/// **归档层里的相对路径**（就是索引里存的那个）。
/// ⚠️ 接收方会用 `RelativePath.Parse` 校验它 —— 这条接口是远端调的，
/// 绝对路径、UNC、`..` 越级都在那里被拒。
/// </param>
public sealed record VerifyRequest(string Location);

/// <summary>回查的结果。</summary>
/// <param name="Exists">归档层上还有这一份。</param>
/// <param name="CouldNotVerify">
/// **查不了**（网络断了、盘符掉了、路径不合规）。
/// ⚠️ 它与「不存在」分开报，但**两者都导致不删** —— 把「查不了」当成「不存在」，
/// 删掉的可能就是最后一份（I2）。
/// </param>
/// <param name="Reason">给人看的原因；「那一份不在了」时为空。</param>
public sealed record VerifyPayload(bool Exists, bool CouldNotVerify, string? Reason);

/// <summary>完成提交（`docs/05-上传接口形状.md` §2.6）。</summary>
/// <param name="Sequence">
/// 分段序号。⚠️ **必须在报文里**：归档文件名是 <c>……/&lt;会话&gt;_&lt;序号&gt;.mp4</c>，
/// 而 <c>sessionId</c> 自己就带 <c>-</c>，从 <c>evidenceId</c> 反推是有歧义的。
/// </param>
/// <param name="SourceDeviceId">
/// 发送方自称的身份。⚠️ 接收方**不拿它当身份用** —— 身份从凭据来，
/// 这里只用来比对，对不上就拒（否则任何设备都能把别人的 id 写进索引）。
/// </param>
public sealed record CommitRequest(
    string EvidenceId,
    string SessionId,
    int Sequence,
    string Waybill,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string ContentHash,
    string SourceDeviceId,
    int ChunkCount,
    IReadOnlyList<string> ChunkHashes,
    IReadOnlyList<PunchPayload> Punches,
    IReadOnlyList<LabelPayload> Labels);

/// <summary>
/// 报文里的打点。
/// </summary>
/// <remarks>
/// ⚠️ 字段名是 **camelCase**，而同一个东西落在磁盘上是 PascalCase
/// （<c>PunchDto</c>，两端一致）。**这不是笔误** —— 报文跟着 §0 的 camelCase 走，
/// 磁盘格式是另一回事。两端在这一处都要转一次名。
/// </remarks>
public sealed record PunchPayload(
    string PunchId,
    string SessionId,
    string WaybillNumber,
    DateTimeOffset PunchedAt,
    long MonotonicOffsetMilliseconds,
    string Source);

/// <summary>报文里的标签。字段名同上，camelCase。</summary>
public sealed record LabelPayload(
    string EvidenceId,
    string Key,
    string Value,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 回执 —— **发送方就是靠它才敢清本地**（不变量 I1）。
/// </summary>
/// <param name="TimeAnchor">
/// 接收方时间，构成外部时间锚（规格 §3.6.4）。手机端保留期的**起算点**就是它。
/// </param>
/// <param name="Location">归档层内的相对路径。回给发送方，好让它知道那一份落在哪儿。</param>
public sealed record ReceiptPayload(
    string EvidenceId,
    string ContentHash,
    DateTimeOffset PublishedAt,
    DateTimeOffset TimeAnchor,
    string ReceiverDeviceId,
    string ReceiverDeviceName,
    string Location);

/// <param name="Signature">base64url 的 HMAC-SHA256，算法见 <c>05-上传接口形状.md</c> §2.7。</param>
public sealed record CommitResponse(ReceiptPayload Receipt, string Signature);

/// <summary>错误响应。<paramref name="Detail"/> 是给人看的中文，可以是 null。</summary>
public sealed record ErrorPayload(string Error, string? Detail);

/// <summary>`GET /api/v1/health` 的响应。</summary>
/// <remarks>
/// 手机端 <c>lan_probe.dart</c> 现在的判据是「这个端口上有没有 HTTP 响应」——
/// 同端口任何一个别的 HTTP 服务都会被当成电脑端在线（那个文件自认了这一点）。
/// 这条接口就是那笔债的答复。⚠️ 旧判据不删：没有 <c>/health</c> 的老版本电脑端还得能探到。
/// </remarks>
public sealed record HealthPayload(string Service, int ApiVersion, string DeviceName)
{
    public const string ServiceName = "vidlog-desktop";
    public const int Version = 1;
}

/// <summary>
/// 入网请求（`docs/05-上传接口形状.md` §2.2）。
/// </summary>
/// <remarks>
/// 2026-09-24 起带**令牌** —— 它来自电脑端屏幕上那张二维码（规格 §3.4.5）。
/// 手机是**轮询**这个接口的：既报"我来了"，也顺便问**批没批**。
/// </remarks>
public sealed record EnrollRequestPayload(string DeviceId, string DeviceName, string? Token);

/// <summary>
/// 入网请求的响应 —— 手机轮询到的**处置**。
/// </summary>
/// <remarks>
/// ⚠️ **它不回令牌、也不回凭据。** 令牌只出现在**电脑端屏幕上那张二维码**里，
/// 回给手机就等于把「只有站在主机屏幕前的人才知道」这个前提拆了 ——
/// 那样「人工批准」就又变成一个没挡东西的按钮。
/// <para>
/// 三个状态都要能被手机看见：<see cref="Pending"/> 继续等，
/// <see cref="Approved"/> 去领凭据，<see cref="Rejected"/> **明确告诉用户被拒了**（不能一直转圈）。
/// </para>
/// </remarks>
public sealed record EnrollPendingPayload(string Status)
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>凭令牌换凭据（§2.3）。</summary>
public sealed record EnrollClaimPayload(string DeviceId, string? Token);

/// <param name="Credential">base64url 的 32 字节。**只在这一次返回**，丢了要重新走一遍入网（不得降级为免凭据）。</param>
public sealed record EnrollCredentialPayload(string Credential);
