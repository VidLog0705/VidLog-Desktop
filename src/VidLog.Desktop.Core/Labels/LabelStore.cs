using System.Text;
using System.Text.Json;

namespace VidLog.Desktop.Core.Labels;

/// <summary>业务类型：发货还是退货。</summary>
/// <remarks>
/// 规格 §3.2.2 里由手机端用户在工作前选择，§3.8 把它列为检索维度之一。
/// </remarks>
public enum BusinessType
{
    Outbound,
    Return,
}

/// <summary>已知标签键。</summary>
/// <remarks>
/// 规格 §6.1 的「录像记录」**刻意不含**这些字段 —— 按不变量 I5，
/// 单号是唯一事实标识，其余属性都只是**可修正标签**。
/// 把它们放在独立存储里而不是塞进录像记录，正是为了让「改标签」不触碰证据本身：
/// 证据的同一性、哈希、时间锚都不受影响。
/// </remarks>
public static class LabelKeys
{
    public const string BusinessType = "business-type";
    public const string Company = "company";
    public const string Category = "category";
    public const string Note = "note";

    /// <summary>
    /// 锁定标记：被锁的录像**永不被自动清理**（规格 §3.5.3 的硬豁免）。
    /// </summary>
    /// <remarks>
    /// 复用标签存储是**有意的**：标签是追加写、后者胜出，
    /// 正好表达「解锁 = 再追加一条 false」，不必为此再造一套锁存储。
    /// <para>
    /// ⚠️ 但它与其他标签**性质不同**：别的标签是「可修正的描述」（I5），
    /// 这个是一条**硬豁免**。清理逻辑读它时不能把它当普通标签随手改掉 ——
    /// 那等于把用户锁上的东西解锁。
    /// </para>
    /// </remarks>
    public const string Locked = "locked";
}

/// <summary>
/// 某一条证据锁着没有（规格 §3.6.5）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>判据只有这一处</b>：<see cref="Cleanup.CleanupPlanner"/> 的清理判定
/// 与检索界面上的锁定图标**都调它**。分成两份的话会出现
/// 「界面显示没锁、清理却把它保留了」—— 那个状态用户没机会理解。
/// </para>
/// <para>
/// 三条判据（与手机端 <c>label_store.isEvidenceLocked</c> **同向**）：
/// </para>
/// <list type="number">
/// <item><b>没打过这个标签 = 没锁</b>（绝大多数证据的常态）。
/// ⚠️ 这一条必须单独判：少了它，「认不出来就当锁着」会把整个库永久锁死、
/// 永远清不掉任何东西。</item>
/// <item>打过了、值也认得出 ⇒ 按那个值。</item>
/// <item>打过了但<b>认不出来</b>（<c>'1'</c> / <c>''</c> / 被人手改坏的值）
/// ⇒ <b>当锁着</b>，朝<b>少删</b>的那头落。与
/// <c>RetentionSetting.FromConfig</c> 解析失败回落「全部保留」同一条规矩：
/// <b>把锁读丢了的代价是删掉用户锁上的证据</b>，而反过来只是少清一条、
/// 占点地方（而且它在豁免列表里看得见）。</item>
/// </list>
/// <para>
/// ⚠️ 所以<b>写入的值只能是 <c>"true"</c> / <c>"false"</c></b>：
/// 写 <c>'1'</c> 会变成「永远锁着」—— 用户解不开，而界面上看不出为什么。
/// </para>
/// </remarks>
public static class EvidenceLock
{
    public static bool IsLocked(IReadOnlyDictionary<string, string>? labelsForEvidence)
    {
        if (labelsForEvidence is null
            || !labelsForEvidence.TryGetValue(LabelKeys.Locked, out var raw))
        {
            return false;
        }

        return !bool.TryParse(raw, out var locked) || locked;
    }
}

/// <summary><see cref="BusinessType"/> 与标签值之间的换算。</summary>
/// <remarks>
/// 单独一个类，不放进 <see cref="LabelKeys"/> —— 那里有个同名的
/// <c>const string BusinessType</c>，会把枚举类型遮蔽掉。
/// </remarks>
public static class BusinessTypes
{
    public const string OutboundValue = "outbound";
    public const string ReturnValue = "return";

    public static string ToValue(BusinessType type) =>
        type == BusinessType.Return ? ReturnValue : OutboundValue;

    public static bool TryParse(string? value, out BusinessType type)
    {
        switch (value)
        {
            case OutboundValue:
                type = BusinessType.Outbound;
                return true;
            case ReturnValue:
                type = BusinessType.Return;
                return true;
            default:
                type = BusinessType.Outbound;
                return false;
        }
    }
}

/// <summary>
/// 一条标签。挂在哪条证据上、键是什么、值是什么。
/// </summary>
/// <remarks>
/// 按**证据**挂而不是按单号挂：同一个单号可能既发过货又退过货，
/// 按单号挂会让两段录像共用一个「发货/退货」值，必然有一个是错的。
/// </remarks>
public sealed record RecordingLabel(
    string EvidenceId,
    string Key,
    string Value,
    DateTimeOffset UpdatedAt);

/// <summary>标签存储。</summary>
public interface ILabelStore
{
    /// <summary>设置（或修正）一个标签。**可反复修改** —— 这正是「可修正标签」的含义。</summary>
    Task SetAsync(string evidenceId, string key, string value, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetForEvidenceAsync(
        string evidenceId, CancellationToken cancellationToken = default);

    /// <summary>读出全部标签，按证据 id 分组。检索按标签筛选时用。</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadAllAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 按行追加的标签存储。
/// </summary>
/// <remarks>
/// **追加写、读取时同键后者胜出。**
/// <para>
/// 改标签是追加一条新记录，而不是原地覆盖旧的 —— 与规格 §6.2
/// 「数据删除必须极度克制」同源：不重写既有记录，就不存在
/// 「重写过程中崩溃导致整份标签损坏」这个失败模式。
/// 语义上后者胜出，与「改标签」的直觉一致。
/// </para>
/// </remarks>
public sealed class JsonLinesLabelStore : ILabelStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLinesLabelStore(string path)
    {
        _path = path;
    }

    public async Task SetAsync(
        string evidenceId,
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        var label = new RecordingLabel(evidenceId, key, value, DateTimeOffset.UtcNow);
        var line = JsonSerializer.Serialize(LabelDto.From(label), SerializerOptions);

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

    public async Task<IReadOnlyDictionary<string, string>> GetForEvidenceAsync(
        string evidenceId,
        CancellationToken cancellationToken = default)
    {
        var all = await LoadAllAsync(cancellationToken);

        return all.TryGetValue(evidenceId, out var labels)
            ? labels
            : new Dictionary<string, string>();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        if (!File.Exists(_path))
        {
            return result.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, string>)pair.Value,
                StringComparer.Ordinal);
        }

        var lines = await File.ReadAllLinesAsync(_path, cancellationToken);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var dto = JsonSerializer.Deserialize<LabelDto>(line, SerializerOptions);
            if (dto is null || string.IsNullOrEmpty(dto.EvidenceId) || string.IsNullOrEmpty(dto.Key))
            {
                continue;
            }

            if (!result.TryGetValue(dto.EvidenceId, out var labels))
            {
                labels = new Dictionary<string, string>(StringComparer.Ordinal);
                result[dto.EvidenceId] = labels;
            }

            // 后者胜出 —— 这就是「改标签」的落点。
            labels[dto.Key] = dto.Value;
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, string>)pair.Value,
            StringComparer.Ordinal);
    }
}

/// <summary>标签的落盘形态。</summary>
public sealed record LabelDto(string EvidenceId, string Key, string Value, string UpdatedAt)
{
    public static LabelDto From(RecordingLabel label) => new(
        label.EvidenceId, label.Key, label.Value, label.UpdatedAt.ToString("O"));
}
