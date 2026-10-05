using System.Text;
using System.Text.Json;

namespace VidLog.Desktop.Core.Recording;

/// <summary>工作区里 <c>session.json</c> 的形状。</summary>
/// <param name="Spec">
/// 这一场是在哪一档规格上录的。
/// </param>
/// <remarks>
/// ⚠️ <b><paramref name="Spec"/> 只有一个用处：让**孤儿恢复**能把它读回来。</b>
/// 进程被杀之后，「这场录的是什么编码 / 分辨率 / 方向」在内存里没了，只剩这个文件；
/// 不写它，孤儿收尾出来的索引条目那三栏**永远是空的**。
/// <para>
/// ⚠️ 那不只是显示问题：<c>CleanupPolicy.EstimateBytes</c> 见空就按**默认档**
/// （H.264 1080P）估，而「按空间清理」是拿估算 <c>freed += size</c> 攒到够为止 ——
/// 估大了得删更多条才够，删的却是**不可逆的证据**。2026-10-02 实测：
/// 主窗「152.1 MB」（实测字节）vs 数据窗「约 355.9 MB」（估算），差 **2.34 倍**。
/// </para>
/// <para>
/// ⚠️ 可空 ⇒ 老 <c>session.json</c>（追加这个字段之前写的）照样读得出来，
/// 读出来是 null，行为与从前**逐字一样**。
/// </para>
/// </remarks>
public sealed record SessionManifest(
    string SessionId,
    string Waybill,
    string SourceDeviceId,
    string StartedAt,
    IReadOnlyList<SegmentManifest> Segments,
    SessionSpecManifest? Spec = null);

/// <summary>一个分段的落盘元数据。</summary>
public sealed record SegmentManifest(
    int Sequence,
    string FileName,
    string StartedAt,
    string EndedAt);

/// <summary>
/// <c>session.json</c> 里记的那一档录制规格。
/// </summary>
/// <remarks>
/// ⚠️ <b>存的是三个枚举名（字符串），不是 <see cref="Media.RecordingSpec"/> 本身。</b>
/// 直接把那个 record 序列化，会把它**算出来的**那些成员（`Label`、`EncoderCandidates`、
/// 观察到的尺寸）一起写进文件，而读回来时它们会被当成输入 —— 那些值本来是
/// 从这三个字段推出来的，写进去就多了一份**会对不上**的副本。
/// <para>
/// ⚠️ 这三个字符串与 <c>RecordingEntry</c> 的 <c>Codec</c> / <c>Resolution</c> /
/// <c>Orientation</c> 是**同一套写法**（都是枚举名）—— 因为它们最终就是往那三栏里写，
/// 用两套写法就多了一道会走岔的转换。
/// </para>
/// </remarks>
public sealed record SessionSpecManifest(string Codec, string Resolution, string Rotation)
{
    public static SessionSpecManifest From(Media.RecordingSpec spec) =>
        new(spec.Codec.ToString(), spec.Resolution.ToString(), spec.Rotation.ToString());

    /// <summary>
    /// 读回一档规格。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>三个值有一个认不出就返回 null —— 不猜，也不逐项回落。</b>
    /// 这组数要写进索引、还要拿去估容量：拿默认档补上一个认不出的字段，
    /// 会得到一条**看着正常、其实是编的**证据元数据 —— 那比空着坏。
    /// （空着至少估容量那一头知道自己在猜，见 <c>CleanupPolicy.EstimateBytes</c>。）
    /// </remarks>
    public Media.RecordingSpec? ToSpec() =>
        Enum.TryParse<Media.VideoCodec>(Codec, out var codec)
        && Enum.TryParse<Media.VideoResolution>(Resolution, out var resolution)
        && Enum.TryParse<Media.CameraRotation>(Rotation, out var rotation)
            ? new Media.RecordingSpec(codec, resolution, rotation)
            : null;
}

/// <summary>重启后发现的、没有收尾的会话。</summary>
/// <param name="Spec">
/// 这一场录的是什么规格；老 manifest 里没记时为 <see langword="null"/>。
/// </param>
public sealed record OrphanSession(
    string SessionId,
    WaybillNumber Waybill,
    string SourceDeviceId,
    IReadOnlyList<SegmentProduct> Segments,
    Media.RecordingSpec? Spec = null);

/// <summary>
/// 录制工作区 —— 会话落盘与孤儿发现。
/// </summary>
/// <remarks>
/// <para>
/// 目录形态：<c>&lt;root&gt;/&lt;sessionId&gt;/session.json</c> + 各分段文件，
/// 收尾成功后另写一个 <c>finalized.json</c>。
/// </para>
/// <para>
/// **孤儿 = 有 session.json 但没有 finalized.json。** 这个判据不用猜
/// 「文件能不能播」「进程还在不在」—— 进程被杀时来不及写任何东西，
/// 所以「缺 finalized.json」是唯一可靠且无需推断的信号。
/// </para>
/// <para>
/// 元数据文件很小，用「写临时文件再改名」保证原子性：
/// 进程在改名之前被杀，留下的是完整的旧版本或完整的新版本，不会有半个 JSON。
/// </para>
/// </remarks>
public sealed class RecordingWorkspace
{
    private const string ManifestFileName = "session.json";
    private const string FinalizedFileName = "finalized.json";

    /// <summary>分段文件的命名（`segment-000.mkv`，见 <c>RecordingSession.StartSegmentAsync</c>）。</summary>
    private const string SegmentFilePrefix = "segment-";
    private const string SegmentFilePattern = SegmentFilePrefix + "*.mkv";

    /// <summary>
    /// 空会话目录的冷静期（T21）：<c>session.json</c> 静了这么久、又一段都没留下，
    /// 就认定它不会再长出分段来了。
    /// </summary>
    private static readonly TimeSpan EmptySessionCoolDown = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string _root;
    private readonly Diagnostics.IAppLogger _logger;

    /// <param name="logger">
    /// 记「读不出来的会话」用。⚠️ 这一条**必须有**：它是本仓**唯一会丢证据的方向**
    /// （I2/I9），而在 2026-09-29 之前它一声不吭（见 <see cref="ReadManifestAsync"/>）。
    /// </param>
    public RecordingWorkspace(string root, Diagnostics.IAppLogger? logger = null)
    {
        _root = root;
        _logger = logger ?? Diagnostics.NullLogger.Instance;
    }

    public string SessionDirectory(string sessionId) => Path.Combine(_root, sessionId);

    /// <summary>
    /// 预录缓冲的临时目录（规格 §3.1.3）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>它不是一个会话目录</b>（没有 <c>session.json</c>）—— 所以
    /// <see cref="ListOrphansAsync"/> 本来就跳过它（它只认有 manifest 的目录），
    /// 不会被当成「录到一半被杀掉的会话」。这个名字以 <c>_</c> 开头，也让它
    /// 与 sessionId（GUID）一眼可分。
    /// </para>
    /// <para>
    /// ⚠️ 里面的东西**全部是可弃的**：滚动分片会被自己滚掉、采纳完那份也会被搬走。
    /// 它不进归档、不进索引、不算「盘上占了多少」。
    /// </para>
    /// </remarks>
    public string PrerecordDirectory => Path.Combine(_root, "_prerecord");

    public async Task WriteManifestAsync(SessionManifest manifest, CancellationToken cancellationToken = default)
    {
        var directory = SessionDirectory(manifest.SessionId);
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(manifest, SerializerOptions);
        await WriteAtomicallyAsync(
            Path.Combine(directory, ManifestFileName), json, cancellationToken);
    }

    public async Task MarkFinalizedAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var directory = SessionDirectory(sessionId);
        Directory.CreateDirectory(directory);

        await WriteAtomicallyAsync(
            Path.Combine(directory, FinalizedFileName),
            DateTimeOffset.UtcNow.ToString("O"),
            cancellationToken);
    }

    /// <summary>
    /// 丢掉一个会话的工作目录。
    /// </summary>
    /// <remarks>
    /// ★ T21：收尾成功之后，<c>work/</c> 里那些源 MKV 只剩一个身份 —— **占地方**。
    /// 清理层只清归档层的成品 MP4，从来不碰 <c>work/</c>，留着就是无界增长。
    /// <para>
    /// ⚠️ <b>什么时候能丢是调用方的判据</b>（收尾成功 **且** 已发到归档层，
    /// 见 <see cref="FinalizeOutcome.ArchiveComplete"/>），这个方法只管丢干净。
    /// </para>
    /// <para>
    /// ⚠️ 删不掉**不是事故**：目录留着，占点地方而已。真抛出去的话会把一次
    /// **成功的**收尾报成失败 —— 那比多占几兆坏得多。
    /// </para>
    /// </remarks>
    public void DiscardSessionDirectory(string sessionId)
    {
        var directory = SessionDirectory(sessionId);

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log(Diagnostics.LogLevel.Warn, "录制",
                $"会话工作目录没删掉，源分段还占着地方（{directory}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 列出所有没走完收尾的会话。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>列出来的分段有两处来源</b>：<c>session.json</c> 里登记的（文件真在盘上的那些），
    /// 加上目录里**没登记的** <c>segment-*.mkv</c>（T20：进程被杀时正在写的那一段，
    /// 见 <see cref="RescueUnregisteredSegments"/>）。
    /// </para>
    /// <para>
    /// 分段文件已经不在了的会话不会被列出来 —— 那种情况没有东西可以收尾，
    /// 硬报一条只会是噪声。**但不会再静默地什么都不做**（T21）：
    /// 没有任何分段的会话目录收不了尾、也就永远写不上 <c>finalized.json</c>，
    /// 于是原来每次启动都跳过它、谁都不会碰它一下。
    /// 现在过冷静期就删掉，没到就记一条。
    /// </para>
    /// <para>
    /// ⚠️ 所以这个方法**有副作用**（会删陈年的空会话目录）。
    /// 它只在启动扫瞄时被调（<see cref="OrphanRecovery.RecoverAsync"/>），
    /// 别拿去当纯查询用。
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<OrphanSession>> ListOrphansAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var orphans = new List<OrphanSession>();

        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sessionId = Path.GetFileName(directory);
            if (File.Exists(Path.Combine(directory, FinalizedFileName)))
            {
                continue;
            }

            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
            if (manifest is null)
            {
                continue;
            }


            var segments = manifest.Segments
                .Select(s => new SegmentProduct(
                    s.Sequence,
                    Path.Combine(directory, s.FileName),
                    ParseTimestamp(s.StartedAt),
                    ParseTimestamp(s.EndedAt)))
                .Where(s => File.Exists(s.SourcePath))
                .OrderBy(s => s.Sequence)
                .ToList();

            // ★ T20：**崩溃 / 强杀 / 断电时正在写的那一段**不在 manifest 里
            // （manifest 只在段**封闭时**才更新）—— 不捞它，它就永远留在 `work/` 里：
            // 不进索引、也没人知道它存在过。捞回来之后走的是**同一条收尾路径**（I9）。
            var rescued = RescueUnregisteredSegments(directory, manifest, segments);
            if (rescued.Count > 0)
            {
                _logger.Log(Diagnostics.LogLevel.Info, "录制",
                    $"{manifest.Waybill} 捞回 {rescued.Count} 段没登记的分段（{sessionId}）："
                    + "进程被杀时正在写的那一段不在 session.json 里，本来会被永远遗弃");

                segments.AddRange(rescued);
                segments.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            }

            if (segments.Count == 0)
            {
                SweepSegmentsGoneSession(sessionId, manifestPath, manifest);
                continue;
            }

            if (!WaybillNumber.TryParse(manifest.Waybill, out var waybill, out _))
            {
                continue;
            }

            var spec = manifest.Spec?.ToSpec();

            // ⚠️ 「有规格但认不出」与「老 manifest 根本没记规格」**必须分得开**：
            // 两者收尾出来的索引条目**逐字一样**（编码 / 分辨率 / 方向三栏全空，
            // 容量估算跟着回落到默认档），但修法完全不同 —— 一个是历史文件照旧，
            // 一个是 `session.json` 里那几个枚举名认不出了（手改过文件，或哪天枚举改了名）。
            // 不留这一条，下一个人只能像 2026-10-02 那样对着检索页的样本重查一遍。
            if (manifest.Spec is { } recorded && spec is null)
            {
                _logger.Log(Diagnostics.LogLevel.Warn, "录制",
                    $"会话元数据里的录制规格认不出来，收尾后索引里不会有编码/分辨率/方向"
                    + $"（{manifestPath}）：{recorded.Codec}/{recorded.Resolution}/{recorded.Rotation}");
            }

            orphans.Add(new OrphanSession(
                sessionId, waybill!, manifest.SourceDeviceId, segments, spec));
        }

        return orphans;
    }

    /// <summary>
    /// 会话目录里 <c>segment-*.mkv</c> 的段号（升序）。
    /// </summary>
    /// <remarks>
    /// ★ T17：按时长滚段改成「一个 ffmpeg 进程跑到底、由它自己滚」之后，会话**不再有
    /// 「段封闭」那一刻可以挂钩** —— 分段什么时候滚出来只有盘知道。所以收尾时的
    /// 段清单从目录里读，而不是从内存里攒。
    /// <para>
    /// ⚠️ <b>0 字节的一律不算</b>，理由与 <see cref="RescueUnregisteredSegments"/>
    /// 逐字相同：封装器攒够一块才落盘，空壳收进收尾会让**整场**判失败（规格 §4.1）。
    /// </para>
    /// </remarks>
    public IReadOnlyList<int> ListSegmentSequences(string sessionId) =>
        ListSegmentFiles(SessionDirectory(sessionId)).ConvertAll(s => s.Sequence);

    /// <summary>目录里的分片：段号 + 路径（按段号升序）。0 字节的不算。</summary>
    private static List<(int Sequence, string Path)> ListSegmentFiles(string directory)
    {
        var files = new List<(int Sequence, string Path)>();

        if (!Directory.Exists(directory))
        {
            return files;
        }

        foreach (var path in Directory.EnumerateFiles(directory, SegmentFilePattern))
        {
            if (!TryParseSequence(Path.GetFileName(path), out var sequence))
            {
                continue;
            }

            if (new FileInfo(path).Length == 0)
            {
                continue;
            }

            files.Add((sequence, path));
        }

        files.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));

        return files;
    }

    /// <summary>没有任何分段留在盘上的会话目录：记一条，过了冷静期就删掉（T21）。</summary>
    /// <remarks>
    /// <para>
    /// 走到这里的是两种情形，**必须分得开**：
    /// ① <c>session.json</c> 里本来就没记过分段（起录之后、第一段封闭之前被杀）—— 什么都没丢；
    /// ② 记过，但那些文件现在一个都不在了 —— 那是**丢证据**（I2），要喊一声。
    /// </para>
    /// <para>
    /// ⚠️ 冷静期是给「正在录的那一场」留的：刚起录时会话目录也是空的，
    /// 立刻删就等于把正在录的连根拔了。启动扫瞄时不会有正在录的，
    /// 但这条判据让这个方法**在别处被调也安全**。
    /// </para>
    /// </remarks>
    private void SweepSegmentsGoneSession(string sessionId, string manifestPath, SessionManifest manifest)
    {
        var counted = manifest.Segments.Count;
        var cooled = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(manifestPath)
            >= EmptySessionCoolDown;

        if (counted > 0)
        {
            // ⚠️ 文件没了是**事实**，而它是「这条录像再也收不了尾」唯一的一句话。
            // 删不删都要喊这一声。
            _logger.Log(Diagnostics.LogLevel.Warn, "录制",
                $"{manifest.Waybill} 的源分段一个都不在了，这一场收不了尾（{sessionId}）："
                + $"session.json 里记着 {counted} 段");
        }

        if (!cooled)
        {
            return;
        }

        DiscardSessionDirectory(sessionId);

        if (counted == 0)
        {
            // 空的壳：收不了尾（没有分段可收）⇒ 也永远写不上 finalized.json
            // ⇒ 孤儿扫瞄每次都跳过它。T21 之前它会一直待在那儿。
            _logger.Log(Diagnostics.LogLevel.Info, "录制",
                $"清掉一个空会话目录（{sessionId}）：里面一段都没录到");
        }
    }

    private async Task<SessionManifest?> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<SessionManifest>(json, SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 半个 JSON（正是原子写要防的情况，但历史遗留文件可能长这样）。
            //
            // ⚠️ **这里必须记一条**：读不出来的会话**永远收不了尾** —— 它的分段
            // 既不会 remux、也不会进索引，而调用方只会静默 `continue`。
            // 2026-09-29 之前那句注释写的是「留给上层记录」，而**上层根本没记**：
            // 这是本仓唯一会**丢证据**的方向（I2/I9），却唯一没有留痕的地方。
            _logger.Log(Diagnostics.LogLevel.Error, "录制",
                $"会话元数据读不出来，这一场的分段收不了尾（{path}）：{ex.Message}");

            return null;
        }
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(
            value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    /// <summary>
    /// 目录里**没登记进 <c>session.json</c>** 的分段（T20：进程被杀时正在写的那一段）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么它一定存在</b>：manifest 只在段**封闭时**才更新，所以崩溃 / 强杀 / 断电时
    /// 正在写的那一段**必然不在里面**。原来 <see cref="ListOrphansAsync"/> 只认 manifest
    /// 列出的分段 ⇒ 那一段被永久遗弃在 <c>work/</c> 里：不在索引里、也没人知道它存在过。
    /// </para>
    /// <para>
    /// ⚠️ <b>电脑端写得是 MKV 中间容器，所以它救得回来</b>（分段结构、写到哪算哪，
    /// 与 MP4 的 moov 在文件末尾完全不同）。这里只负责**把它捞出来**，能不能用
    /// 交给**同一条收尾路径**判（remux + 实际解码校验，I9 不许有旁路）。
    /// </para>
    /// <para>
    /// ⚠️ <b>0 字节的一律不要。</b>ffmpeg 的 matroska 封装器是**攒够一块才落盘**的
    /// （2026-10-05 实测：一段 0 → 256KiB → 512KiB …），所以「起进程之后立刻被杀」
    /// 留下的是一个 0 字节的壳。收进来只会让整场收尾**失败**（一段不通过 ⇒ 全会话
    /// 不作数，规格 §4.1）—— 那会把本来救得回来的几段一起拖下水。
    /// 反过来，**非 0 就一定带着真画面**（最小的一档就是一块）。
    /// </para>
    /// <para>
    /// ⚠️ 时间戳：结束时刻取**文件最后一次写入的时刻** —— 进程就是在那一刻没的；
    /// 起录时刻取**前一段的结束时刻**（分段是紧接着滚的），前面没有就用会话起点。
    /// 于是 <c>Duration</c> 是它真录进去的那一段，而不是 0。
    /// </para>
    /// </remarks>
    private static List<SegmentProduct> RescueUnregisteredSegments(
        string directory, SessionManifest manifest, IReadOnlyList<SegmentProduct> registered)
    {
        var rescued = new List<SegmentProduct>();

        var known = manifest.Segments
            .Select(s => s.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sessionStart = ParseTimestamp(manifest.StartedAt);

        foreach (var (sequence, path) in ListSegmentFiles(directory))
        {
            if (known.Contains(Path.GetFileName(path)))
            {
                continue;
            }

            var endedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

            // 前一段就是它的起点。取不到（它是第一段、或前一段的文件已经不在盘上）
            // 就用会话起点；会话起点也认不出来（manifest 里那个时刻是坏的）就退回结束时刻
            // —— 宁可时长记成 0，也不能记成一个 `MinValue` 换来的上万年。
            var startedAt = registered
                .Where(s => s.Sequence < sequence)
                .Select(s => (DateTimeOffset?)s.EndedAt)
                .Max() ?? sessionStart;

            if (startedAt == DateTimeOffset.MinValue || startedAt > endedAt)
            {
                startedAt = endedAt;
            }

            rescued.Add(new SegmentProduct(sequence, path, startedAt, endedAt));
        }

        return rescued;
    }

    /// <summary>`segment-007.mkv` → 7。名字认不出来就返回 <see langword="false"/>。</summary>
    private static bool TryParseSequence(string fileName, out int sequence)
    {
        sequence = 0;

        var stem = Path.GetFileNameWithoutExtension(fileName);

        return stem.StartsWith(SegmentFilePrefix, StringComparison.Ordinal)
            && int.TryParse(stem.AsSpan(SegmentFilePrefix.Length), out sequence);
    }

    /// <summary>
    /// 写临时文件再改名。
    /// </summary>
    /// <remarks>
    /// <b>Windows 上「改名覆盖已存在的文件」会被拒绝</b>（实测 <c>errno = 5 拒绝访问</c>）——
    /// Defender 扫新写的文件时会短暂持有句柄。而 manifest 是**反复写同一个文件**的
    /// （开录写一次、收尾登记分段时再写一次），所以正好每次都撞上。
    /// <para>
    /// ⚠️ T17 之前这里写的是「每个分段封闭再写一次」—— 那条路随「一个进程跑到底」没了
    /// （见 <c>RecordingSession.StartCaptureAsync</c>），重写的次数少了很多。
    /// </para>
    /// <para>
    /// 手机端先踩到这个坑。后果是 manifest 写不进去、那段录像重启后收不了尾。
    /// 这里同样重试；仍不行就**退化成直接写** —— 宁可失去「原子替换」这层保护，
    /// 也不能把 manifest 整个丢掉。
    /// </para>
    /// </remarks>
    private static async Task WriteAtomicallyAsync(
        string destination,
        string content,
        CancellationToken cancellationToken)
    {
        var temporary = destination + ".tmp";

        await File.WriteAllTextAsync(
            temporary,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            }
        }

        await File.WriteAllTextAsync(
            destination,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        try
        {
            File.Delete(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉只是留个 .tmp 垃圾，不影响正确性。
        }
    }
}
