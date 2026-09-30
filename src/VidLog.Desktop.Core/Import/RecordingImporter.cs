using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Import;

/// <summary>一次导入的请求。</summary>
/// <param name="SourcePath">用户挑的那个文件（绝对路径，**不进索引**）。</param>
/// <param name="Waybill">挂到哪个单号上。**必填** —— 索引里单号是唯一的事实标识。</param>
/// <param name="BusinessType">发货还是退货。</param>
/// <param name="StartedAt">
/// 这一段是什么时候录的。
/// <para>
/// ⚠️ <b>它必须由人来定，不能猜</b>：这个时间决定这条录像落在哪个日期目录、
/// 落在哪个保留期窗口里、以及在按日期检索时出不出现 —— 而它同时也是
/// <c>StartedAt</c> 这个**证据元数据**。界面上会从文件时间预填一个值让人确认。
/// </para>
/// </param>
public sealed record ImportRequest(
    string SourcePath,
    WaybillNumber Waybill,
    BusinessType BusinessType,
    DateTimeOffset StartedAt);

/// <summary>一次导入的结果。</summary>
/// <param name="Imported">这段录像有没有真的进库。</param>
/// <param name="EvidenceId">进库后的证据 id；失败时为 <see langword="null"/>。</param>
/// <param name="Location">归档层里的相对路径。</param>
/// <param name="Duration">
/// 量出来的时长；<b>没量出来时是 <see langword="null"/></b>
/// （那时索引里那条记的是 0 —— 界面要说出来，别让用户以为这段录像只有 0 秒）。
/// </param>
/// <param name="FailureReason">没成的原因（给人看的）。</param>
public sealed record ImportResult(
    bool Imported,
    string? EvidenceId,
    RelativePath? Location,
    TimeSpan? Duration,
    string? FailureReason)
{
    public static ImportResult Failed(string reason) => new(false, null, null, null, reason);
}

/// <summary>
/// 「导入录像」—— 把**外面录来的**一个视频文件收进本机的录像库（设计图 `_41`）。
/// </summary>
/// <remarks>
/// <para>
/// 它解决的是「这一段不是这台电脑录的」：换过机器、从旧备份里翻出来的、
/// 或者别人给的一个 mp4。做完之后它必须与**本机录的**在库里一模一样 ——
/// 检索得到、回放得了、导出得了、也参与清理判定。
/// </para>
/// <para>
/// ⚠️ <b>顺序是刻意的：先证明它是好的，再往盘上写一个字。</b>
/// 哈希 → 查重 → 量一遍 → 解一遍，四步全过了才复制。反过来（先复制再验）
/// 会让一个坏文件在归档目录里留下一个**看起来正常**的 mp4，
/// 而它已经占着一个像证据一样的路径了。
/// </para>
/// <para>
/// ⚠️ 它与 <c>UploadReceiver</c>（手机上传）**不是一条路**，也不该合成一条：
/// 那一条有分片、有续传、有凭据与签名回执，而这一条只有「用户挑的一个本地文件」。
/// 硬凑到一起会让上传那条路上多出一堆只有导入才用得着的分支。
/// 两条路共用的是下面这几件事：<see cref="ArchiveLayout"/>、
/// <see cref="ContentHasher"/>、<see cref="DecodeVerifier"/>、索引与标签。
/// </para>
/// </remarks>
public sealed class RecordingImporter
{
    /// <summary>
    /// 导入进来的录像在索引里记的「来源设备」。
    /// </summary>
    /// <remarks>
    /// ⚠️ 是 <c>"imported"</c> 而**不是**本机机器名：这个字段回答的是「哪台设备录的」，
    /// 而导入进来的这一段我们**不知道**是哪台录的。填本机名字等于往证据元数据里
    /// 写一句假话 —— 事后查的人会以为这台电脑录过它。
    /// </remarks>
    public const string SourceDeviceId = "imported";

    private readonly StorageLocations _storage;
    private readonly IRecordingIndex _index;
    private readonly ILabelStore? _labels;
    private readonly DecodeVerifier _verifier;
    private readonly IProcessRunner _runner;
    private readonly string _ffmpegPath;
    private readonly ArchiveRelay? _relay;
    private readonly IAppLogger _logger;

    public RecordingImporter(
        StorageLocations storage,
        IRecordingIndex index,
        DecodeVerifier verifier,
        IProcessRunner runner,
        string ffmpegPath,
        ILabelStore? labels = null,
        ArchiveRelay? relay = null,
        IAppLogger? logger = null)
    {
        _storage = storage;
        _index = index;
        _verifier = verifier;
        _runner = runner;
        _ffmpegPath = ffmpegPath;
        _labels = labels;
        _relay = relay;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 收这一个文件进来。
    /// </summary>
    /// <remarks>
    /// 失败**不是异常**，是返回值：调用方要把它显示给用户（I3），
    /// 而正常路径上不该为它套一层解包。
    /// </remarks>
    public async Task<ImportResult> ImportAsync(
        ImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ImportResult Fail(string reason, LogLevel level = LogLevel.Warn)
        {
            // `AGENTS.md` §6：导入是「外部数据进库」的动作，成败都要留痕 ——
            // 事后要能回答「这条录像是什么时候、从哪个文件进来的」。
            _logger.Log(level, "导入", $"{request.Waybill.Value} 导入没成：{reason}",
                new Dictionary<string, object?>
                {
                    ["来源"] = request.SourcePath,
                    ["业务类型"] = BusinessTypes.ToValue(request.BusinessType),
                });

            return ImportResult.Failed(reason);
        }

        if (string.IsNullOrWhiteSpace(request.SourcePath) || !File.Exists(request.SourcePath))
        {
            return Fail($"找不到这个文件：{request.SourcePath}");
        }

        // ── 1. 先算它是什么 ────────────────────────────────────────────
        ContentHash contentHash;
        try
        {
            contentHash = await ContentHasher.ComputeFileHashAsync(request.SourcePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"读不了这个文件：{ex.Message}");
        }

        // ── 2. 已经在库里就不导第二遍 ──────────────────────────────────
        //
        // 判据是**内容哈希**，不是文件名：同一个文件换个名字再导一次，
        // 库里就有两条一模一样的证据 —— 它们哈希相同、会各自被清理判定一次，
        // 而界面上看起来是「同一天同一段录像录了两遍」。
        //
        // `ponytail:` 这里扫一遍全表。导入是用户手动按的、一个人一天按不了几次，
        // 而为它维护一份「哈希 → 证据」的索引不值得。
        var existing = (await _index.LoadAllAsync(cancellationToken))
            .FirstOrDefault(e => string.Equals(e.ContentHash.Value, contentHash.Value, StringComparison.Ordinal));

        if (existing is not null)
        {
            return Fail(
                $"这个文件已经在库里了（单号 {existing.Waybill.Value}，证据 {existing.EvidenceId}）—— 没有重复导入。",
                LogLevel.Info);
        }

        // ── 3. 量一遍、解一遍 ─────────────────────────────────────────
        var streams = await InspectAsync(request.SourcePath, cancellationToken);

        if (streams is null)
        {
            return Fail("读不出这个文件的内容（多半不是视频文件，或者本机的 FFmpeg 不可用）。");
        }

        if (!streams.HasVideo)
        {
            return Fail("这个文件里没有视频画面，收进来也回放不了。");
        }

        // 规格 §3.1.4：校验失败不得入库为「正常」。
        // ⚠️ 验的是**源文件**，不是复制之后那一份 —— 这样上面四步任何一步没过，
        // 归档目录里都还没有任何东西被写出来。
        var verification = await _verifier.VerifyAsync(request.SourcePath, cancellationToken);
        if (!verification.IsPlayable)
        {
            return Fail($"这段录像解不开，不收：{verification.FailureReason}");
        }

        // ── 4. 落点 ──────────────────────────────────────────────────
        //
        // 会话号**由内容哈希推出来**：同一个文件重复导入拿到同一个 id
        // （所以「导到一半断电了、再导一次」不会在旁边多出一份），
        // 而两条内容不同的文件永远不会撞。定长、纯 ASCII，进得了文件名。
        var sessionId = $"import-{contentHash.Value[..12]}";
        var duration = streams.Duration ?? TimeSpan.Zero;
        var location = ArchiveLayout.BuildLocation(request.Waybill, sessionId, 0, request.StartedAt);

        // ⚠️ 先用 `Resolve` 在**每一个**落盘位置上找一遍（设计图 `_43` 的多磁盘）：
        // 上一次导到一半（文件落了盘、索引没写成）会正好留下这一个路径。
        // 只看活动根的话，另一块盘上那份会被当成不存在而重写一遍。
        var destination = _storage.Resolve(location.Value)
            ?? Path.Combine(_storage.ActiveRoot, location.Value);

        if (!File.Exists(destination))
        {
            try
            {
                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                // 先写 `.tmp` 再改名（与 `UploadReceiver` 同一手法）：拷到一半断电时
                // 不会留下一个「文件名对、内容半截」的 mp4 —— 那个东西看起来
                // 与成品一模一样，而它会挡住下一次导入（`File.Exists` 为真）。
                var temporary = destination + ".tmp";
                await using (var input = new FileStream(
                    request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                await using (var output = new FileStream(
                    temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }

                File.Move(temporary, destination, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail($"复制不进录像目录：{ex.Message}");
            }
        }

        // ── 5. 写索引（可见性就是从这一行开始的）──────────────────────
        var entry = new RecordingEntry(
            EvidenceId: $"{sessionId}-000",
            SessionId: sessionId,
            Waybill: request.Waybill,
            // ⚠️ 时长量不出来时记 0 —— 索引里这个字段是 `TimeSpan`，
            // 没有「不知道」这个取值。界面上要说出来（拿 `ImportResult.Duration`
            // 是不是 null 判），否则用户以为这段录像只有 0 秒。
            StartedAt: request.StartedAt,
            EndedAt: request.StartedAt + duration,
            Duration: duration,
            Location: location,
            ContentHash: contentHash,
            SourceDeviceId: SourceDeviceId,
            Codec: CodecName(streams.VideoCodec),
            Resolution: ResolutionName(streams.Width, streams.Height),
            // ⚠️ 方向**不写**。方向回答的是「摄像头装成什么样」，而导入进来的
            // 这一段我们不知道它是怎么装的（它早就压进画面里了）。
            // 写 `None` 是编一个事实 —— 回放端会照它去摆画面。
            Orientation: null);

        try
        {
            await _index.AddAsync(entry, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件已经在盘上、但检索不到 —— 对「找得到自己的证据」这个承诺来说
            // 等于没入库。**算失败**，并且说清怎么补救。
            return Fail(
                $"文件已经复制进去了（{destination}），但没能写进索引：{ex.Message}。"
                + "再导一次同一个文件会把索引补上（内容一样，不会重复复制）。");
        }

        // ── 6. 业务类型标签 ──────────────────────────────────────────
        //
        // ⚠️ 与 `SessionFinalizer` 那一处同一条规矩：**写标签失败不算导入失败**。
        // 标签是可修正的描述（I5），而这段录像已经落盘、已经能检索了 ——
        // 为了一个描述性字段把一次成功的导入判成失败，是拿次要的东西毁掉主要的东西。
        if (_labels is not null)
        {
            try
            {
                await _labels.SetAsync(
                    entry.EvidenceId,
                    LabelKeys.BusinessType,
                    BusinessTypes.ToValue(request.BusinessType),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Log(LogLevel.Warn, "标签",
                    $"{request.Waybill.Value} 导入的业务类型标签没写上：{ex.Message}",
                    new Dictionary<string, object?>
                    {
                        ["证据"] = entry.EvidenceId,
                        ["业务类型"] = BusinessTypes.ToValue(request.BusinessType),
                    });
            }
        }

        // ── 7. 再发一份到归档层（规格 §3.4.6）────────────────────────
        //
        // ⚠️ 失败不算失败，也不清本机那一份：本机这份已经在索引里、能播能检索，
        // 它是这个系统的第一份。与收尾那一处是同一条判据。
        if (_relay is not null)
        {
            await _relay.PublishAsync(location, destination, cancellationToken);
        }

        _logger.Log(LogLevel.Info, "导入", $"{request.Waybill.Value} 导进来了一段录像",
            new Dictionary<string, object?>
            {
                ["来源"] = request.SourcePath,
                ["证据"] = entry.EvidenceId,
                ["时长"] = streams.Duration?.ToString() ?? "没量出来",
                ["录制时间"] = request.StartedAt.ToString("O"),
                ["落点"] = destination,
            });

        return new ImportResult(true, entry.EvidenceId, location, streams.Duration, null);
    }

    /// <summary>
    /// 从文件里问出时长、编码、尺寸。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 复用 <see cref="FfmpegSpecProbe.ReadBackArguments"/> 与
    /// <see cref="FfmpegNetworkCameraProbe.ParseStreams"/>：那是本仓**唯一一处**
    /// 「读回一个文件、问它是什么」的 argv，也是**唯一一处**解析 ffmpeg 输出的代码。
    /// 各写一份的话，ffmpeg 的输出格式一变就要改两处，而漏掉的那一处
    /// **静默读不到**（拿到的不是错误，是 null）。
    /// </para>
    /// <para>
    /// ⚠️ 只读一帧（argv 里的 <c>-frames:v 1</c>）：时长与编码都在容器头里。
    /// 完整解一遍是 <see cref="DecodeVerifier"/> 的事，不在这里重复。
    /// </para>
    /// </remarks>
    private async Task<FfmpegNetworkCameraProbe.ParsedStreams?> InspectAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(
                _ffmpegPath, FfmpegSpecProbe.ReadBackArguments(path), cancellationToken);

            return FfmpegNetworkCameraProbe.ParseStreams(result.StandardError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消的（关窗）—— 那是取消，不是「读不出来」。
            throw;
        }
        catch (Exception ex)
        {
            // 具体原因（多半是「ffmpeg 不在」）只留在日志里：给用户那一句
            // 在 ImportAsync 里，说的是与他的处境有关的那件事。
            _logger.Log(LogLevel.Warn, "导入", $"量不了 {path}：{ex.Message}");
            return null;
        }
    }

    /// <summary>ffmpeg 说的编码名 → 索引里那个名字；认不出来就是 <see langword="null"/>。</summary>
    /// <remarks>
    /// ⚠️ 用的是**枚举名**（<c>H264</c> / <c>H265</c>），与
    /// <c>SessionFinalizer</c> 写的是同一套 —— 索引里同名不同写法等于两条互不相认的记录。
    /// </remarks>
    private static string? CodecName(string? codec) => codec?.ToLowerInvariant() switch
    {
        "h264" => nameof(VideoCodec.H264),
        "hevc" or "h265" => nameof(VideoCodec.H265),
        _ => null,
    };

    /// <summary>
    /// 实测尺寸 → 分辨率档；**对不上任何一档就是 <see langword="null"/>**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 是**精确匹配**，不是「差不多就归到最近的一档」：录制规格那三档是我们
    /// 钉出去的尺寸，而导入进来的文件是别人给的 —— 把 1600×900 归到 1080P
    /// 等于往证据元数据里写一个假的档位。认不出来就空着，容量估算那边
    /// 靠 null 才会回落到保守值。
    /// </remarks>
    private static string? ResolutionName(int? width, int? height) => (width, height) switch
    {
        (3840, 2160) => nameof(VideoResolution.Uhd4K),
        (1920, 1080) => nameof(VideoResolution.P1080),
        (1280, 720) => nameof(VideoResolution.P720),
        _ => null,
    };
}
