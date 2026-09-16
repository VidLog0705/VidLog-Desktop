using System.Text;
using System.Text.Json;

namespace VidLog.Desktop.Core.Recording;

/// <summary>工作区里 <c>session.json</c> 的形状。</summary>
public sealed record SessionManifest(
    string SessionId,
    string Waybill,
    string SourceDeviceId,
    string StartedAt,
    IReadOnlyList<SegmentManifest> Segments);

/// <summary>一个分段的落盘元数据。</summary>
public sealed record SegmentManifest(
    int Sequence,
    string FileName,
    string StartedAt,
    string EndedAt);

/// <summary>重启后发现的、没有收尾的会话。</summary>
public sealed record OrphanSession(
    string SessionId,
    WaybillNumber Waybill,
    string SourceDeviceId,
    IReadOnlyList<SegmentProduct> Segments);

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

    public RecordingWorkspace(string root)
    {
        _root = root;
    }

    public string SessionDirectory(string sessionId) => Path.Combine(_root, sessionId);

    public string FinalizedMarkerPath(string sessionId) =>
        Path.Combine(SessionDirectory(sessionId), FinalizedFileName);

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

            orphans.Add(new OrphanSession(sessionId, waybill!, manifest.SourceDeviceId, segments));
        }

        return orphans;
    }

    private static async Task<SessionManifest?> ReadManifestAsync(
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
            // 读不出来的会话没法收尾，跳过并留给上层记录。
            return null;
        }
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(
            value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

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

        File.Move(temporary, destination, overwrite: true);
    }
}
