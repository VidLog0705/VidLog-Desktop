using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Search;

/// <summary>单号匹配方式（规格 §3.8：精确 / 前缀 / 模糊）。</summary>
public enum WaybillMatchMode
{
    /// <summary>整串相等。</summary>
    Exact,

    /// <summary>以查询串开头。</summary>
    Prefix,

    /// <summary>包含查询串。</summary>
    Contains,
}

/// <summary>一次检索的条件。</summary>
public sealed record RecordingQuery
{
    /// <summary>单号查询串。为空则不按单号过滤。</summary>
    public string? WaybillText { get; init; }

    /// <summary>
    /// 匹配方式。
    /// </summary>
    /// <remarks>
    /// 中文检索界面惯称的「精确/前缀/模糊」在这里对应
    /// <c>=</c> / <c>LIKE 'x%'</c> / <c>LIKE '%x%'</c> ——
    /// 也就是 <see cref="WaybillMatchMode.Exact"/> / <see cref="WaybillMatchMode.Prefix"/> /
    /// <see cref="WaybillMatchMode.Contains"/>。
    /// <para>
    /// **「模糊」不做到错字容忍。** 记错一位数字的查不出来 —— 那是另一个功能
    /// （编辑距离或拼音），不是「模糊查询」这四个字在中文界面里的通常含义。
    /// </para>
    /// </remarks>
    public WaybillMatchMode MatchMode { get; init; } = WaybillMatchMode.Exact;

    /// <summary>录制开始时间的下界（含）。</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>
    /// 录制开始时间的上界（**不含**）。
    /// </summary>
    /// <remarks>
    /// 半开区间是为了配合「按天筛选」：选 9 月 16 日就传
    /// [16 日 00:00, 17 日 00:00)，不必操心当天最后一秒的边界。
    /// </remarks>
    public DateTimeOffset? To { get; init; }

    /// <summary>发货 / 退货筛选。为 null 则不限。</summary>
    public BusinessType? BusinessType { get; init; }

    /// <summary>
    /// 按「录像来源设备」筛选（设计图 `_36` 那个下拉）。为 null 或空则不限。
    /// </summary>
    /// <remarks>
    /// ⚠️ 比的是 <see cref="RecordingEntry.SourceDeviceId"/> 的**原样值、区分大小写**。
    /// 那个字段的值只有两个来源：本机录的写机器名，外面导进来的写常量
    /// <c>imported</c>（<c>RecordingImporter.SourceDeviceId</c>）。
    /// 大小写不敏感地比会把 <c>DESKTOP-A</c> 与 <c>desktop-a</c> 这两台真机器混成一台，
    /// 而用户看到的是一份**少了半截**的列表。
    /// </remarks>
    public string? SourceDevice { get; init; }

    /// <summary>
    /// 把**已作废**的段排除掉（D1/D2）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 默认 <see langword="false"/> = **照常返回**（检索界面要看得见它们，
    /// 只是行上带个「已作废」标记）。置真的是**重复单号探针**
    /// （<c>AppHost</c> 那个委托）：作废就是因为要重录，重录时当然不能拿
    /// 自己刚作废的那一段来提醒「你录过了」。
    /// </para>
    /// <para>
    /// ⚠️ 做成查询条件而不是在探针那一头过滤：探针走的就是这个方法
    /// （`AppHost` 的注释写着「接检索那一层，再写一份就会与它走岔」），
    /// 在调用方过滤等于把那句话反着做。
    /// </para>
    /// </remarks>
    public bool ExcludeVoided { get; init; }

    /// <summary>最多返回多少条。</summary>
    public int Limit { get; init; } = 200;
}

/// <summary>一条检索结果。</summary>
public sealed record RecordingHit(
    RecordingEntry Entry,
    IReadOnlyDictionary<string, string> Labels)
{
    /// <summary>本次打包的业务类型；标签缺失或无法解析时为 <see langword="null"/>。</summary>
    public BusinessType? BusinessType =>
        Labels.TryGetValue(LabelKeys.BusinessType, out var raw) &&
        BusinessTypes.TryParse(raw, out var parsed)
            ? parsed
            : null;

    /// <summary>这一段作废了没有（D1）。判据只此一处，见 <see cref="EvidenceVoid"/>。</summary>
    public bool IsVoided => EvidenceVoid.IsVoided(Labels);
}

/// <summary>
/// 录像检索（规格 §3.8）。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类不得依赖许可状态。</b>许可设计 §5 与 L8：试用到期 / 未激活 / 校验失败，
/// 都不得锁住用户已有的录像 —— <b>历史录像必须仍可查看、检索、导出</b>。
/// 「用户的数据是他的，不是人质。」这是商业伦理约束，不是技术选择。
/// 所以这里没有任何许可入参、没有任何门禁分支（有测试断言这一点）。
/// </para>
/// <para>
/// 规模：当前每次查询都全量读索引文件。几千条时是毫秒级，满足「秒查到」。
/// 真到十万条级别该换的是存储（嵌入式库或倒排索引），不是在这里加缓存 ——
/// 提前优化会掩盖真正的瓶颈。
/// </para>
/// </remarks>
public sealed class RecordingSearch
{
    private readonly IRecordingIndex _index;
    private readonly ILabelStore _labels;

    public RecordingSearch(IRecordingIndex index, ILabelStore labels)
    {
        _index = index;
        _labels = labels;
    }

    public async Task<IReadOnlyList<RecordingHit>> SearchAsync(
        RecordingQuery query,
        CancellationToken cancellationToken = default)
    {
        var entries = await _index.LoadAllAsync(cancellationToken);
        var allLabels = await _labels.LoadAllAsync(cancellationToken);

        // 查询串也要归一化 —— 否则用户敲小写 or 带空格就查不到（单号在库里是大写形态）。
        var needle = string.IsNullOrWhiteSpace(query.WaybillText)
            ? null
            : WaybillNumber.Normalize(query.WaybillText);

        var hits = new List<RecordingHit>();

        foreach (var entry in entries)
        {
            if (needle is not null && !Matches(entry.Waybill.Value, needle, query.MatchMode))
            {
                continue;
            }

            if (query.From is not null && entry.StartedAt < query.From.Value)
            {
                continue;
            }

            if (query.To is not null && entry.StartedAt >= query.To.Value)
            {
                continue;
            }

            if (query.SourceDevice is { Length: > 0 } device
                && !string.Equals(entry.SourceDeviceId, device, StringComparison.Ordinal))
            {
                continue;
            }

            var labels = allLabels.TryGetValue(entry.EvidenceId, out var found)
                ? found
                : EmptyLabels;

            if (query.BusinessType is not null)
            {
                var actual = labels.TryGetValue(LabelKeys.BusinessType, out var raw) &&
                             BusinessTypes.TryParse(raw, out var parsed)
                    ? parsed
                    : (BusinessType?)null;

                if (actual != query.BusinessType)
                {
                    continue;
                }
            }

            // ⚠️ 作废的段在**检索界面照常显示**（行上带「已作废」标记，
            // 用户要能看见自己作废过什么），只有 `ExcludeVoided` 的调用方
            // —— 重复单号探针 —— 才看不见它们。见 `RecordingQuery.ExcludeVoided`。
            if (query.ExcludeVoided && EvidenceVoid.IsVoided(labels))
            {
                continue;
            }

            hits.Add(new RecordingHit(entry, labels));
        }

        // 最近录的排前面 —— 找纠纷录像时绝大多数是刚发生的那些。
        hits.Sort((a, b) => b.Entry.StartedAt.CompareTo(a.Entry.StartedAt));

        return hits.Count <= query.Limit ? hits : hits.GetRange(0, query.Limit);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyLabels =
        new Dictionary<string, string>();

    private static bool Matches(string waybill, string needle, WaybillMatchMode mode) => mode switch
    {
        WaybillMatchMode.Exact => string.Equals(waybill, needle, StringComparison.Ordinal),
        WaybillMatchMode.Prefix => waybill.StartsWith(needle, StringComparison.Ordinal),
        WaybillMatchMode.Contains => waybill.Contains(needle, StringComparison.Ordinal),
        _ => false,
    };
}
