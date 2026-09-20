using System.Text;
using System.Text.Json;

namespace VidLog.Desktop.Core.Index;

/// <summary>
/// 索引里的一条录像记录。
/// </summary>
/// <remarks>
/// 规格 §6.1「录像记录」要求的字段：单号、起止时间、时长、**相对路径**、内容哈希、来源设备。
/// <para>
/// 注意这里**没有**公司/分类/备注 —— 按 I5，那些只是可修正标签，
/// 存在别处，可随时改，不影响证据本身的同一性。
/// </para>
/// </remarks>
/// <param name="EvidenceId">这一条（= 一个分段成品）的标识。</param>
/// <param name="SessionId">
/// 所属录制会话。
/// </param>
/// <remarks>
/// <para>
/// <b>为什么必须有 SessionId</b>：录像是**区间**，一次打包可能横跨多个分段文件
/// （母仓 `docs/02-数据模型.md` §2），于是同一会话会落成多条记录。
/// 而打点记的是**会话内偏移** —— 要把它定位到「第几段的第几秒」，
/// 就得先把同一会话的分段归到一起。没有这个字段，回放的「跳到打点位置」根本做不了。
/// </para>
/// <para>
/// 不能靠拆 <paramref name="EvidenceId"/> 的字符串前缀来替代：
/// 那是把编码格式当契约用，格式一改就全线崩，而且没有任何编译期保护。
/// </para>
/// </remarks>
public sealed record RecordingEntry(
    string EvidenceId,
    string SessionId,
    WaybillNumber Waybill,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    TimeSpan Duration,
    RelativePath Location,
    ContentHash ContentHash,
    string SourceDeviceId);

/// <summary>录像索引。</summary>
public interface IRecordingIndex
{
    /// <summary>写入一条记录。</summary>
    Task AddAsync(RecordingEntry entry, CancellationToken cancellationToken = default);

    /// <summary>读出全部记录。用于验证落盘确实成功、以及后续的检索。</summary>
    Task<IReadOnlyList<RecordingEntry>> LoadAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 按行追加的 JSON 索引（JSON Lines）。
/// </summary>
/// <remarks>
/// 选这个形态的理由来自规格 §6.2「数据删除必须**极度克制**」：
/// 追加写不会重写既有记录，因此不存在「重写过程中崩溃导致整库损坏」这个失败模式。
/// 代价是更新/删除需要压实，但那本来就是少数操作。
/// <para>
/// 写入用信号量串行化：收尾可能并发发生在多个分段上，追加写必须原子。
/// </para>
/// </remarks>
public sealed class JsonLinesRecordingIndex : IRecordingIndex
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLinesRecordingIndex(string path)
    {
        _path = path;
    }

    public async Task AddAsync(RecordingEntry entry, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(RecordingEntryDto.From(entry), SerializerOptions);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureDirectory();

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

    public async Task<IReadOnlyList<RecordingEntry>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(_path, cancellationToken);
        var entries = new List<RecordingEntry>(lines.Length);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var dto = JsonSerializer.Deserialize<RecordingEntryDto>(line, SerializerOptions);
            if (dto is not null)
            {
                entries.Add(dto.ToEntry());
            }
        }

        return entries;
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}

/// <summary>
/// 索引的落盘形态。
/// </summary>
/// <remarks>
/// 单独立一个 DTO 而不是直接序列化 <see cref="RecordingEntry"/>：
/// 那几个值对象（单号 / 相对路径 / 内容哈希）用的是私有构造函数，
/// 直接反序列化会绕不过去。用 DTO 把「磁盘上的形状」和「内存里的类型」分开，
/// 顺带让格式变更有个明确的落点。
/// </remarks>
public sealed record RecordingEntryDto(
    string EvidenceId,
    string? SessionId,
    string Waybill,
    string StartedAt,
    string EndedAt,
    double DurationSeconds,
    string Location,
    string ContentHash,
    string SourceDeviceId)
{
    public static RecordingEntryDto From(RecordingEntry entry) => new(
        entry.EvidenceId,
        entry.SessionId,
        entry.Waybill.Value,
        entry.StartedAt.ToString("O"),
        entry.EndedAt.ToString("O"),
        entry.Duration.TotalSeconds,
        entry.Location.Value,
        entry.ContentHash.Value,
        entry.SourceDeviceId);

    public RecordingEntry ToEntry() => new(
        EvidenceId,
        // 早期写入的记录没有 SessionId 字段。退化成「自己是自己的会话」——
        // 单段录像本来就等价于此，而它至少不会把不同会话错并到一起。
        string.IsNullOrEmpty(SessionId) ? EvidenceId : SessionId,
        WaybillNumber.Parse(Waybill),
        DateTimeOffset.Parse(StartedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(EndedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
        TimeSpan.FromSeconds(DurationSeconds),
        RelativePath.Parse(Location),
        // 属性名与类型名同名，这里必须全限定才不会被解析成属性本身。
        VidLog.Desktop.Core.ContentHash.Parse(ContentHash),
        SourceDeviceId);
}
