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
    /// 列出所有没走完收尾的会话。
    /// </summary>
    /// <remarks>
    /// 分段文件已经不在了的会话会被跳过 —— 那种情况没有东西可以收尾，
    /// 硬报一条只会是噪声。
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

            if (segments.Count == 0)
            {
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
    /// 写临时文件再改名。
    /// </summary>
    /// <remarks>
    /// <b>Windows 上「改名覆盖已存在的文件」会被拒绝</b>（实测 <c>errno = 5 拒绝访问</c>）——
    /// Defender 扫新写的文件时会短暂持有句柄。而 manifest 是**反复写同一个文件**的
    /// （开录写一次、每个分段封闭再写一次），所以正好每次都撞上。
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
