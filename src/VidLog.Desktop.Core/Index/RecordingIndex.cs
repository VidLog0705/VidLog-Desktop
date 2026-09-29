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
/// <param name="Codec">
/// 编码名（<c>H264</c> / <c>H265</c>）。**追加字段**，老条目没有 ⇒ null。
/// </param>
/// <param name="Resolution">分辨率名（<c>Uhd4K</c> / <c>P1080</c> / <c>P720</c>）。追加字段。</param>
/// <param name="Orientation">
/// 方向名。**两端都写**（2026-09-29 起）—— 规格 §3.1.7 原本写着「方向只在手机端」，
/// 但需求方照设计图裁决「要完整三档方向」，所以电脑端从此也写它。
/// <para>
/// ⚠️ <b>两端的值不是同一套</b>：手机写 <c>landscapeLeft</c> / <c>portrait</c> /
/// <c>landscapeRight</c>（持机方向），电脑写 <c>None</c> / <c>Left90</c> /
/// <c>Right90</c> / <c>UpsideDown</c>（画面转多少度）—— 因为电脑端的摄像头
/// **是固定的**，「横左」在那里没有对应物（见 <c>CameraRotation</c> 的说明）。
/// 读端认不出来会**回落默认档**（两端都这么写），不会炸。
/// </para>
/// </param>
/// <remarks>
/// <para>
/// <b>为什么索引要记这三个</b>：规格 §3.1.7 的连带项原话 ——「否则回放端不知道该按什么播、
/// 容量也估不准」。容量那一半是真的承重：电脑端「按空间清理」正是靠
/// 「每个文件大概多大」决定删到够为止，而 4K 与 720P 差好几倍。
/// </para>
/// <para>
/// ⚠️ 存的是**枚举名**（不是数字，也不是界面上的「H.264」）：
/// 索引是要进诊断包、要被人打开看的，而 <c>H264</c> 一眼能认、也不会被翻译错。
/// 界面上的名字另有 <c>RecordingSpec.CodecLabel</c> 一处产出（规格要求**不得出现 HEVC**）。
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
    string SourceDeviceId,
    string? Codec = null,
    string? Resolution = null,
    string? Orientation = null);

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

    /// <summary>
    /// 读全部记录。<b>放宽着读</b> —— 字段名按下面的候选表逐个试，大小写不敏感。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>为什么不能直接用 <c>JsonSerializer.Deserialize</c> 绑 DTO</b>：
    /// 两端写的**字段名根本不一样**，而且是历史造成的（2026-09-27 核过）：
    /// </para>
    /// <list type="bullet">
    /// <item>本仓自己的：<c>Waybill</c> / <c>StartedAt</c> / <c>DurationSeconds</c>…（PascalCase）；</item>
    /// <item>手机端写的：<c>waybill</c> / <c>startedAt</c> / <c>durationSeconds</c>…（camelCase）；</item>
    /// <item>而母仓 <c>docs/02-数据模型.md</c> §1.1 那张表用的是
    /// <c>WaybillNumber</c> / <c>RecordingStartedAt</c> —— <b>两端的落盘名都不是它</b>。</item>
    /// </list>
    /// <para>
    /// 所以「加一个 <c>PropertyNameCaseInsensitive</c>」**不够**：`Waybill` 与 `WaybillNumber`
    /// 大小写相同也对不上。真要收口得改两端的写入端，而两端的文件**都已经在盘上了**
    /// （手机上装的是 19/21/22 号包）⇒ 读端必须宽容，写端维持原样。
    /// </para>
    /// <para>
    /// ⚠️ 宽容的是**字段名**，不是**内容**：认不出来的记录仍然整条丢掉（一条坏行
    /// 不该让整份索引读不出来），但丢掉的那条**不会被编造**出来。
    /// </para>
    /// </remarks>
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

            if (TryReadEntry(line, out var entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>宽容地读一行。认不出来（缺关键字段、类型不对、坏 JSON）返回 false。</summary>
    private static bool TryReadEntry(string line, out RecordingEntry entry)
    {
        entry = null!;

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(line).RootElement;
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // 关键字段少一个就整条不认 —— 与其编一条出来，不如当它不存在。
        if (Text(root, "EvidenceId") is not { } evidenceId
            || Text(root, "Waybill", "WaybillNumber") is not { } waybill
            || Text(root, "StartedAt", "RecordingStartedAt") is not { } startedAt
            || Text(root, "EndedAt", "RecordingEndedAt") is not { } endedAt
            || Text(root, "Location") is not { } location
            || Text(root, "ContentHash") is not { } contentHash
            || Text(root, "SourceDeviceId") is not { } sourceDeviceId)
        {
            return false;
        }

        try
        {
            entry = new RecordingEntry(
                evidenceId,
                // 早期写入的记录没有 SessionId。退化成「自己是自己的会话」——
                // 单段录像本来就等价于此，而它至少不会把不同会话错并到一起。
                Text(root, "SessionId") ?? evidenceId,
                WaybillNumber.Parse(waybill),
                DateTimeOffset.Parse(startedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(endedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
                TimeSpan.FromSeconds(Number(root, "DurationSeconds", "Duration") ?? 0),
                RelativePath.Parse(location),
                VidLog.Desktop.Core.ContentHash.Parse(contentHash),
                sourceDeviceId,
                // 追加字段：老条目没有它们 ⇒ null（不是空串 —— 「没记」与「记了个空」
                // 是两件事，容量估算那边靠 null 才敢回落到保守值）。
                Text(root, "Codec"),
                Text(root, "Resolution"),
                Text(root, "Orientation"));

            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // 字段在、但内容不合法（单号格式、时间格式、哈希长度…）——
            // 与「缺字段」同样处理：丢掉这一条，不编。
            return false;
        }
    }

    /// <summary>按候选名取一个字符串，**大小写不敏感**；取不到返回 null。</summary>
    private static string? Text(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>按候选名取一个数（数字或能当数字的字符串）；取不到返回 null。</summary>
    private static double? Number(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Number)
                {
                    return property.Value.GetDouble();
                }

                if (property.Value.ValueKind == JsonValueKind.String
                    && double.TryParse(
                        property.Value.GetString(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
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
/// 索引的**写入**形态。
/// </summary>
/// <remarks>
/// <para>
/// 单独立一个 DTO 而不是直接序列化 <see cref="RecordingEntry"/>：
/// 那几个值对象（单号 / 相对路径 / 内容哈希）用的是私有构造函数，
/// 直接序列化会把它们摊成对象。用 DTO 把「磁盘上的形状」和「内存里的类型」分开。
/// </para>
/// <para>
/// ⚠️ <b>它只管写，不管读</b>（2026-09-27）：读那一侧是
/// <c>TryReadEntry</c> 的手写查找 —— 因为两端的字段名不一样，
/// 而且两端的文件都已经在盘上了，读端必须宽容。**改这里的属性名 = 改落盘格式**，
/// 而读端认得新名字之前，老文件会读不出来；要改就两边一起。
/// </para>
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
    string SourceDeviceId,
    string? Codec,
    string? Resolution,
    string? Orientation)
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
        entry.SourceDeviceId,
        entry.Codec,
        entry.Resolution,
        entry.Orientation);
}
