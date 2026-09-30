using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Punches;

namespace VidLog.Desktop.Core.Upload;

/// <summary>
/// 一次上传被拒。<see cref="Code"/> 是 <see cref="UploadErrors"/> 里的协议码。
/// </summary>
/// <remarks>
/// 用异常而不是返回值：这些是**明确定义、可预期**的失败，调用方（路由层）要做的只有
/// 「把码映射成状态码」，而正常路径上不该为它套一层解包。真出问题的是别的异常 ——
/// 它们会照常往上抛，不会被这里的 catch 顺手吞掉。
/// </remarks>
public sealed class UploadRejectedException : Exception
{
    public UploadRejectedException(string code, string? detail)
        : base(detail ?? code)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// 回执的签名（`docs/05-上传接口形状.md` §2.7）。
/// </summary>
/// <remarks>
/// **不签 JSON，签一条两端各自拼出来的规范串。** 理由写在 §2.7：
/// .NET 与 Dart 对同一份数据的序列化结果不是逐字节相同的（数字、时区写法、小数位数），
/// 而那种差异**在两端各自的单元测试里都不会暴露** —— 只会现场表现成「回执验签失败」，
/// 而那看起来像被篡改了。对取证产品来说，把格式差异误报成篡改是最坏的一种假警报。
/// </remarks>
public static class ReceiptSignature
{
    /// <summary>规范串的第一行，用来把这份签名和将来的其他签名区分开。</summary>
    public const string Scheme = "vidlog-receipt/v1";

    /// <summary>
    /// 七行、<c>\n</c> 连接、UTF-8、**末尾不加换行**。
    /// </summary>
    public static string Canonicalize(ReceiptPayload receipt) => string.Join(
        '\n',
        Scheme,
        receipt.EvidenceId,
        receipt.ContentHash,
        Format(receipt.PublishedAt),
        Format(receipt.TimeAnchor),
        receipt.ReceiverDeviceId,
        receipt.ReceiverDeviceName,
        receipt.Location);

    /// <param name="credential">该设备入网时拿到的凭据（base64url），它的**原始字节**就是 HMAC 的密钥。</param>
    public static string Compute(string credential, ReceiptPayload receipt)
    {
        var key = Base64Url.DecodeFromChars(credential.AsSpan());
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonicalize(receipt)));

        return Base64Url.EncodeToString(mac);
    }

    /// <summary>
    /// UTC，<c>yyyy-MM-ddTHH:mm:ss.fffffff+00:00</c>（7 位小数）。
    /// </summary>
    /// <remarks>
    /// <c>K</c> 对 <see cref="DateTimeOffset"/> 输出的就是偏移量写法（零偏移即 <c>+00:00</c>）。
    /// ⚠️ **Dart 侧没有等价的一行**：<c>toIso8601String()</c> 给的是 3 位小数 + <c>Z</c>，
    /// 必须显式补零、显式接 <c>+00:00</c>。
    /// </remarks>
    internal static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffK", CultureInfo.InvariantCulture);
}

/// <summary>
/// 远端上传的接收方（规格 §3.4、§5.1；`docs/05-上传接口形状.md`）。
/// </summary>
/// <remarks>
/// <para>
/// <b>原子发布就落在这里</b>：先落盘、后入索引，而**可见性由索引决定、索引只有一个写入点**。
/// <c>incoming/</c> 里的东西不在索引里，因此不参与检索、不参与回放、不参与归档回查
/// （端间契约 §1.4）—— 这正是「要么完整可见、要么完全不可见」，且不需要另写一套过滤逻辑。
/// </para>
/// <para>
/// <b>身份来自凭据，不来自报文。</b> 报文里的 <c>sourceDeviceId</c> 只用来比对：
/// 信它的话，任何一台已入网的设备都能把别人的 id 写进索引。
/// </para>
/// </remarks>
public sealed class UploadReceiver
{
    /// <summary>分片大小。**接收方定，发送方跟随**（§0）。</summary>
    public const long ChunkSize = 4L * 1024 * 1024;

    /// <summary>分片数的上限。挡住「chunkCount 填个整数溢出」那类报文。</summary>
    public const int MaxChunkCount = 1_000_000;

    private const string AssembledFileName = "assembled.tmp";

    private readonly DataLayout _layout;
    private readonly IRecordingIndex _index;
    private readonly IPunchLog _punches;
    private readonly ILabelStore _labels;
    private readonly ReceiptStore _receipts;
    private readonly DecodeVerifier _verifier;
    private readonly string _deviceName;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>录像成品的落盘位置（设计图 `_43` 的多磁盘）。不传 = 只有 <c>ArchiveRoot</c>。</summary>
    private readonly StorageLocations? _storage;

    /// <summary>异常留痕用（`AGENTS.md` §6）。</summary>
    private readonly IAppLogger _logger;

    /// <summary>提交串行闸。发布是「先落盘后入索引」两步，不许交叉。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UploadReceiver(
        DataLayout layout,
        IRecordingIndex index,
        IPunchLog punches,
        ILabelStore labels,
        DecodeVerifier verifier,
        string deviceName,
        Func<DateTimeOffset>? now = null,
        ArchiveRelay? relay = null,
        IAppLogger? logger = null,
        StorageLocations? storage = null)
    {
        _layout = layout;
        _index = index;
        _punches = punches;
        _labels = labels;
        _verifier = verifier;
        _deviceName = deviceName;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _receipts = new ReceiptStore(layout.ReceiptsPath);
        _relay = relay;
        _logger = logger ?? NullLogger.Instance;
        _storage = storage;
    }

    /// <summary>
    /// 把刚发布的那一份再发一份到归档层（规格 §3.4.6）。
    /// </summary>
    /// <remarks>
    /// ⚠️ **发布失败不回滚、不改回执。** 回执说的是「这台电脑端收下了」——
    /// 那是事实，而且手机端拿到它才会走下一步（§3.4.4）。归档层那份没上去的代价是
    /// 「手机端那条还不能被清理」（回查不通过 ⇒ 拒删），不是「这条录像没了」。
    /// </remarks>
    private readonly ArchiveRelay? _relay;

    /// <summary>
    /// 分片清单查询（§2.4）。**接收方算，发送方绝不拿自己记的进度续传**（规格 §3.4.2）。
    /// </summary>
    /// <remarks>
    /// 没有 <c>async</c>：这里读的全是本地目录元数据，没有真正需要等待的东西。
    /// </remarks>
    public Task<ProbeResponse> ProbeAsync(ProbeRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var evidenceId = RequireEvidenceId(request.EvidenceId);

        Require(
            request.ChunkCount is >= 1 and <= MaxChunkCount,
            UploadErrors.BadRequest,
            $"分片数 {request.ChunkCount} 不在 1..{MaxChunkCount} 之内");

        Require(ContentHash.TryParse(request.ContentHash, out _, out var hashError), UploadErrors.BadRequest, hashError);
        Require(request.TotalBytes >= 0, UploadErrors.BadRequest, "总字节数为负");

        var directory = ChunkDirectory(evidenceId);
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(new ProbeResponse(ChunkSize, request.ChunkCount, []));
        }

        var have = new List<int>();

        for (var i = 0; i < request.ChunkCount; i++)
        {
            var path = ChunkPath(directory, i);
            if (!File.Exists(path))
            {
                continue;
            }

            // ⚠️ 大小对不上 = 这一片只传了一半（连接断了），**不算已有**。
            // 少了这一步，半截的片会被报成「已有」，发送方于是不重传，
            // 一路卡到重试耗尽 —— 界面上是一条永远好不了的「上传失败」。
            var expected = ExpectedChunkLength(request.TotalBytes, request.ChunkCount, i);
            if (expected >= 0 && new FileInfo(path).Length == expected)
            {
                have.Add(i);
            }
        }

        return Task.FromResult(new ProbeResponse(ChunkSize, request.ChunkCount, have));
    }

    /// <summary>
    /// 收一个分片（§2.5）。**允许乱序到达**（§1.3 ④），重复上传同一片就是覆盖（幂等）。
    /// </summary>
    public async Task<ChunkAccepted> StoreChunkAsync(
        string evidenceId,
        int index,
        Stream body,
        CancellationToken cancellationToken = default)
    {
        RequireEvidenceId(evidenceId);

        Require(
            index >= 0 && index < MaxChunkCount,
            UploadErrors.BadRequest,
            $"分片下标 {index} 不合法");

        var directory = ChunkDirectory(evidenceId);
        Directory.CreateDirectory(directory);

        // 上限按「发送方跟随接收方的分片大小」定：除了最后一片，每片都恰好 ChunkSize。
        // 超过就说明两端对分片大小的理解不一致 —— 当场拒掉，别把几个 GB 读进内存。
        var bytes = await ReadCappedAsync(body, ChunkSize, cancellationToken);

        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));

        // 先写 .tmp 再改名：直接覆盖目标文件的话，写到一半断掉会留下一个
        // 「大小对、内容半截」的片，而它看起来和完整的一模一样。
        var destination = ChunkPath(directory, index);
        var temporary = destination + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
        File.Move(temporary, destination, overwrite: true);

        return new ChunkAccepted(index, digest);
    }

    /// <summary>
    /// 完成提交（§2.6）：校验 → 原子发布 → 签名回执。
    /// </summary>
    /// <param name="authenticatedDeviceId">
    /// **从凭据解出来的**设备 id，不是报文里那个。
    /// </param>
    /// <param name="credential">用来给回执签名的那把凭据。</param>
    public async Task<CommitResponse> CommitAsync(
        CommitRequest request,
        string authenticatedDeviceId,
        string credential,
        CancellationToken cancellationToken = default)
    {
        var evidenceId = RequireEvidenceId(request.EvidenceId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await CommitCoreAsync(request, evidenceId, authenticatedDeviceId, credential, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CommitResponse> CommitCoreAsync(
        CommitRequest request,
        string evidenceId,
        string authenticatedDeviceId,
        string credential,
        CancellationToken cancellationToken)
    {
        RequireEvidenceId(request.SessionId);
        Require(
            string.Equals(request.SourceDeviceId, authenticatedDeviceId, StringComparison.Ordinal),
            UploadErrors.BadRequest,
            "报文里的 sourceDeviceId 与凭据所属的设备对不上");

        // ⚠️ 归档文件名是「<会话>_<序号>.mp4」，而 sessionId 自己就带 `-`，
        // 从 evidenceId 反推是有歧义的。所以序号必须由报文给出，再**校验**它对得上。
        // 对不上就拒：拼错了不会报错，只会让文件名与证据 id 静默对不上。
        Require(
            string.Equals(evidenceId, $"{request.SessionId}-{request.Sequence:000}", StringComparison.Ordinal),
            UploadErrors.EvidenceMismatch,
            $"evidenceId 与 sessionId + sequence 对不上：{evidenceId}");

        Require(
            request.ChunkCount is >= 1 and <= MaxChunkCount,
            UploadErrors.BadRequest,
            $"分片数 {request.ChunkCount} 不在 1..{MaxChunkCount} 之内");

        Require(
            request.ChunkHashes is not null && request.ChunkHashes.Count == request.ChunkCount,
            UploadErrors.BadRequest,
            "chunkHashes 的条数与 chunkCount 对不上");

        Require(ContentHash.TryParse(request.ContentHash, out var contentHash, out var hashError),
            UploadErrors.BadRequest, hashError);

        var chunkHashes = new ContentHash[request.ChunkCount];
        for (var i = 0; i < request.ChunkCount; i++)
        {
            Require(
                ContentHash.TryParse(request.ChunkHashes[i], out var parsed, out var error),
                UploadErrors.BadRequest,
                $"第 {i} 片的哈希：{error}");
            chunkHashes[i] = parsed!;
        }

        Require(request.EndedAt >= request.StartedAt, UploadErrors.BadRequest, "结束时刻早于开始时刻");

        WaybillNumber waybill;
        try
        {
            waybill = WaybillNumber.Parse(request.Waybill);
        }
        catch (ArgumentException ex)
        {
            throw new UploadRejectedException(UploadErrors.BadRequest, $"单号不合法：{ex.Message}");
        }

        // ───────── 回执丢了、发送方重发 ─────────
        var existing = await _receipts.FindAsync(evidenceId, cancellationToken);
        if (existing is not null)
        {
            Require(
                string.Equals(existing.ContentHash, contentHash!.Value, StringComparison.Ordinal),
                UploadErrors.AlreadyPublished,
                $"这个证据已经发布过，且内容不同（已发布 {existing.ContentHash}，本次 {contentHash!.Value}）");

            // 幂等重放：**回原样那一份**，绝不重新签一个时间锚。
            await EnsureSidecarsAsync(evidenceId, request, authenticatedDeviceId, cancellationToken);

            return new CommitResponse(existing, ReceiptSignature.Compute(credential, existing));
        }

        // ───────── 校验分片 ─────────
        var directory = ChunkDirectory(evidenceId);
        for (var i = 0; i < request.ChunkCount; i++)
        {
            var path = ChunkPath(directory, i);
            if (!File.Exists(path))
            {
                throw new UploadRejectedException(UploadErrors.ChunkMissing, $"缺第 {i} 片");
            }

            var actual = await ContentHasher.ComputeFileHashAsync(path, cancellationToken);
            Require(
                string.Equals(actual.Value, chunkHashes[i].Value, StringComparison.Ordinal),
                UploadErrors.HashMismatch,
                $"第 {i} 片的哈希对不上");
        }

        // ───────── 拼装 ─────────
        var assembled = Path.Combine(directory, AssembledFileName);
        await using (var output = new FileStream(
            assembled, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            for (var i = 0; i < request.ChunkCount; i++)
            {
                await using var input = new FileStream(
                    ChunkPath(directory, i), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await input.CopyToAsync(output, cancellationToken);
            }
        }

        var assembledHash = await ContentHasher.ComputeFileHashAsync(assembled, cancellationToken);
        Require(
            string.Equals(assembledHash.Value, contentHash!.Value, StringComparison.Ordinal),
            UploadErrors.HashMismatch,
            "拼起来的整体哈希与 contentHash 对不上");

        // 规格 §3.1.4「校验失败不得入库为正常」。⚠️ 手机端**没有**这一步
        // （它没有 FFmpeg，见 `session_finalizer.dart` 的说明），所以这是**唯一**
        // 一次能发现「手机产出了一个坏文件」的机会 —— 而电脑端是归档层。
        var verification = await _verifier.VerifyAsync(assembled, cancellationToken);
        if (!verification.IsPlayable)
        {
            throw new UploadRejectedException(
                UploadErrors.Unplayable,
                $"这段录像解不开：{verification.FailureReason}");
        }

        // ───────── 原子发布 ─────────
        //
        // ⚠️ 多磁盘（设计图 `_43`）：**先看这一条在不在任何一个位置上**
        // （那是"上一次发布到一半断电了"要处理的那种），不在才按列表挑一块盘写。
        // 只按活动根拼路径的话，另一块盘上那份同名的会被当成"不存在"而重复写一遍，
        // 于是同一条录像在盘上有了两份 —— 而它们将来会各自被清理判定一次。
        var location = ArchiveLayout.BuildLocation(waybill, request.SessionId, request.Sequence, request.StartedAt);
        var storage = _storage ?? StorageLocations.Single(_layout.ArchiveRoot);
        var destination = storage.Resolve(location.Value)
            ?? Path.Combine(storage.ActiveRoot, location.Value);

        if (File.Exists(destination))
        {
            // 走到这里说明「文件在、回执不在」—— 上一次发布到一半断电了。
            // 内容一样就接着走（下面 EnsureSidecars 会把索引和回执补上）；
            // 内容不一样就是**归档层里另有一份同名的东西**，不能覆盖它。
            var onDisk = await ContentHasher.ComputeFileHashAsync(destination, cancellationToken);
            Require(
                string.Equals(onDisk.Value, contentHash!.Value, StringComparison.Ordinal),
                UploadErrors.AlreadyPublished,
                $"归档层已存在同名文件且内容不同：{location.Value}");
        }
        else
        {
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Move(assembled, destination, overwrite: false);
        }

        var now = _now();
        var receipt = new ReceiptPayload(
            EvidenceId: evidenceId,
            ContentHash: contentHash!.Value,
            PublishedAt: now,
            TimeAnchor: now,
            ReceiverDeviceId: _deviceName,
            ReceiverDeviceName: _deviceName,
            Location: location.Value);

        // ⚠️ 回执写在索引**之前**。两样都可能丢，但丢的代价不对称：
        // 索引没写 = 还不可见，重放能补上；回执没写 = 那个时间锚永久没了，补不回来。
        await _receipts.AppendAsync(receipt, cancellationToken);

        await EnsureSidecarsAsync(evidenceId, request, authenticatedDeviceId, cancellationToken);
        await IndexOnceAsync(evidenceId, request, waybill, location, contentHash!, authenticatedDeviceId, cancellationToken);

        await CleanupIncomingAsync(directory, cancellationToken);

        // 再发一份到归档层（规格 §3.4.6）。排在最后：回执与索引都已经落了盘，
        // 这一份没上去**不会**影响手机端拿到的那个答复。
        if (_relay is not null)
        {
            await _relay.PublishAsync(location, destination, cancellationToken);
        }

        return new CommitResponse(receipt, ReceiptSignature.Compute(credential, receipt));
    }

    /// <summary>
    /// 把打点与标签补进去。**幂等** —— 重放时走的就是这里。
    /// </summary>
    private async Task EnsureSidecarsAsync(
        string evidenceId,
        CommitRequest request,
        string authenticatedDeviceId,
        CancellationToken cancellationToken)
    {
        if (request.Punches is { Count: > 0 })
        {
            // 已经写过的打点不再追加 —— 打点日志是追加写且读取时不去重，
            // 多写一遍就会在回放里**同一个时刻出现两个打点**。
            var known = (await _punches.LoadAllAsync(cancellationToken))
                .Select(p => p.PunchId)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var payload in request.Punches)
            {
                if (string.IsNullOrEmpty(payload.PunchId) || known.Contains(payload.PunchId))
                {
                    continue;
                }

                known.Add(payload.PunchId);
                await _punches.AppendAsync(
                    new Punch(
                        payload.PunchId,
                        request.SessionId,
                        WaybillNumber.Parse(payload.WaybillNumber),
                        payload.PunchedAt,
                        payload.MonotonicOffsetMilliseconds,
                        ParseSource(payload.Source)),
                    cancellationToken);
            }
        }

        if (request.Labels is null)
        {
            return;
        }

        foreach (var payload in request.Labels)
        {
            if (string.IsNullOrEmpty(payload.Key))
            {
                continue;
            }

            // 标签是「后者胜出」的追加写，重复同一个值没有影响，不必去重。
            await _labels.SetAsync(evidenceId, payload.Key, payload.Value, cancellationToken);
        }
    }

    /// <summary>
    /// 写索引 —— **可见性就是从这一行开始的**。已经有了就不写第二遍。
    /// </summary>
    /// <remarks>
    /// 索引是追加写且不去重的（<c>JsonLinesRecordingIndex</c> 的既定形态），
    /// 同一份证据写两行会让它在检索里出现两次。
    /// <c>ponytail:</c> 为此扫一遍全表；重放是罕见路径，不值得为它加一份「已写过哪些」的缓存。
    /// </remarks>
    private async Task IndexOnceAsync(
        string evidenceId,
        CommitRequest request,
        WaybillNumber waybill,
        RelativePath location,
        ContentHash contentHash,
        string authenticatedDeviceId,
        CancellationToken cancellationToken)
    {
        var all = await _index.LoadAllAsync(cancellationToken);
        if (all.Any(e => string.Equals(e.EvidenceId, evidenceId, StringComparison.Ordinal)))
        {
            return;
        }

        await _index.AddAsync(
            new RecordingEntry(
                EvidenceId: evidenceId,
                SessionId: request.SessionId,
                Waybill: waybill,
                StartedAt: request.StartedAt,
                EndedAt: request.EndedAt,
                Duration: request.EndedAt - request.StartedAt,
                Location: location,
                ContentHash: contentHash,
                SourceDeviceId: authenticatedDeviceId),
            cancellationToken);
    }

    /// <summary>发布成功后把暂存的分片删掉。**尽力而为** —— 删不掉不影响这次已经成功的发布。</summary>
    /// <remarks>
    /// 这是**唯一**可以删 <c>incoming/</c> 的时机：此刻成品已在归档层、已在索引里、
    /// 回执也已落盘。提前删就等于把续传的能力毁掉（§3.4.2 要的正是能续传）。
    /// </remarks>
    private async Task CleanupIncomingAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => Directory.Delete(directory, recursive: true), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 留给人工/将来的清理。不在这里抛 —— 这次上传是成功的。
            //
            // ⚠️ 但**必须记一条**（`AGENTS.md` §6「异常」+「不可逆动作」）：
            // 删不掉就会**永远留在盘上**（这个目录只在这一处被清），而它的体积
            // 是整份录像的原始分片 —— 盘满时用户查不到是谁占的。
            // 2026-09-29 审计查出来的缺口：这里原来一个字都不留。
            _logger.Log(LogLevel.Warn, "上传",
                $"暂存分片删不掉，会一直占着盘（{directory}）：{ex.Message}");
        }
    }

    /// <summary>这一片该有多大；<paramref name="totalBytes"/> 不可信时返回 -1（那就跳过大小检查）。</summary>
    private static long ExpectedChunkLength(long totalBytes, int chunkCount, int index)
    {
        if (totalBytes < 0)
        {
            return -1;
        }

        var offset = (long)index * ChunkSize;
        if (offset >= totalBytes)
        {
            return -1;
        }

        return Math.Min(ChunkSize, totalBytes - offset);
    }

    /// <summary>
    /// 证据 id 既进文件名也进目录名，所以它在**信任边界**上必须收紧。
    /// </summary>
    /// <remarks>
    /// ⚠️ 比「转义一下」更严：**只接受字母数字与 <c>-</c> <c>_</c>，其余一律拒**。
    /// 转义会掩盖问题 —— 两个不同的 id 转义后可能撞成同一个目录，
    /// 而那是**两条证据混在一起**，不会报错。
    /// </remarks>
    private static string RequireEvidenceId(string? value)
    {
        Require(!string.IsNullOrEmpty(value), UploadErrors.BadRequest, "evidenceId 为空");

        foreach (var ch in value)
        {
            Require(
                char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_',
                UploadErrors.BadRequest,
                $"evidenceId 含不合法字符：{value}");
        }

        Require(value.Length <= 128, UploadErrors.BadRequest, "evidenceId 过长");
        return value;
    }

    private static PunchSource ParseSource(string? value) => value switch
    {
        nameof(PunchSource.KeyboardScanner) => PunchSource.KeyboardScanner,
        nameof(PunchSource.CameraDecoder) => PunchSource.CameraDecoder,
        _ => PunchSource.ManualEntry,
    };

    private string ChunkDirectory(string evidenceId) => Path.Combine(_layout.UploadIncomingRoot, evidenceId);

    private static string ChunkPath(string directory, int index) => Path.Combine(directory, $"{index}.part");

    private static async Task<byte[]> ReadCappedAsync(Stream body, long cap, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        using var sink = new MemoryStream();

        while (true)
        {
            var read = await body.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            Require(
                sink.Length + read <= cap,
                UploadErrors.BadRequest,
                $"分片超过上限 {cap} 字节 —— 两端对分片大小的理解不一致");

            sink.Write(buffer, 0, read);
        }

        return sink.ToArray();
    }

    /// <remarks>
    /// <c>[DoesNotReturnIf(false)]</c> 不只是注释：它让编译器在调用之后**认定条件成立**，
    /// 于是 <c>Require(x is not null, …)</c> 之后就不必再写 <c>!</c>。
    /// 少写一个 <c>!</c> 就少一处将来会骗人的断言。
    /// </remarks>
    private static void Require([DoesNotReturnIf(false)] bool condition, string error, string? detail)
    {
        if (!condition)
        {
            throw new UploadRejectedException(error, detail);
        }
    }
}

/// <summary>
/// 回执存储 —— 按 <c>evidenceId</c> 追加写、读取时后者胜出（与标签存储同构）。
/// </summary>
/// <remarks>
/// 存在的理由是 §2.6「重发一次 commit」那一节：**时间锚必须是稳定的**，
/// 同一条证据两次归档给出两个不同的 <c>timeAnchor</c>，看起来就是被篡改了。
/// </remarks>
public sealed class ReceiptStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ReceiptStore(string path)
    {
        _path = path;
    }

    public async Task<ReceiptPayload?> FindAsync(string evidenceId, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        ReceiptPayload? found = null;

        foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var receipt = JsonSerializer.Deserialize<ReceiptPayload>(line, SerializerOptions);
                if (receipt is not null && string.Equals(receipt.EvidenceId, evidenceId, StringComparison.Ordinal))
                {
                    found = receipt;
                }
            }
            catch (JsonException)
            {
                // 坏行跳过 —— 与索引、打点、标签同一条规矩。
            }
        }

        return found;
    }

    /// <summary>
    /// 一次读全表：<c>evidenceId</c> → **归档成功时刻**（回执里的 <c>timeAnchor</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 清理判定要的就是这张表（规格 §3.5.2.1 的起算点）。此前它**故意没有**：
    /// 「在本类有生产调用点之前，那会是一段没人调用、也没法验证的代码」——
    /// 那个理由写在 <c>CleanupPlanner</c> 的注释里，而现在执行层接通了，
    /// 调用点来了。
    /// </para>
    /// <para>
    /// ⚠️ 同一条证据有**多行回执**时**后者胜出** —— 与 <c>ArchiveStore.loadAll</c>、
    /// 标签表一致（都是追加写、都靠后者覆盖）。这里不会出现「两个不同的时间锚」：
    /// 重复 commit 走的是「原样再给一份」（§2.6），所以后写的那一行内容是一样的。
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> LoadAnchorMapAsync(
        CancellationToken cancellationToken = default)
    {
        var anchors = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        if (!File.Exists(_path))
        {
            return anchors;
        }

        foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var receipt = JsonSerializer.Deserialize<ReceiptPayload>(line, SerializerOptions);
                if (receipt is not null && !string.IsNullOrEmpty(receipt.EvidenceId))
                {
                    anchors[receipt.EvidenceId] = receipt.TimeAnchor;
                }
            }
            catch (JsonException)
            {
                // 坏行跳过 —— 与索引、打点、标签同一条规矩。
            }
        }

        return anchors;
    }

    public async Task AppendAsync(ReceiptPayload receipt, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(receipt, SerializerOptions);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(
                _path,
                line + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
