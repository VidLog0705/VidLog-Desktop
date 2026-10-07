using System.Globalization;

using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Cleanup;

/// <summary>清理流水上的一行（T24）。</summary>
/// <param name="AtText">什么时候。</param>
/// <param name="WaybillText">哪一条 —— 单号；查不到时见 <see cref="CleanupLogView.NoWaybillText"/>。</param>
/// <param name="ActionText">干了什么（人话，见 <see cref="CleanupLogView.DescribeAction"/>）。</param>
/// <param name="ReasonText">为什么。</param>
public sealed record CleanupLogRow(
    string AtText,
    string WaybillText,
    string ActionText,
    string ReasonText)
{
    /// <summary>审计里的原值（哪一份录像）。界面上挂在那一行的 tooltip 里。</summary>
    public string EvidenceId { get; init; } = string.Empty;
}

/// <summary>
/// 把清理审计那本账（<see cref="CleanupAuditRecord"/>）翻成界面能直接画的几行（T24）。
/// </summary>
/// <remarks>
/// <para>
/// 规格 §6.2 那句「禁止静默清理」是**两半**：清理前必须预告（那半早就做完了，
/// <see cref="CleanupService.PreviewAsync"/>），**且保留可查的清理记录**（这半）。
/// 审计从 T19 起就在写盘了，但**一直没人能看** —— 文件在
/// <c>%LOCALAPPDATA%\VidLog\cleanup-audit.jsonl</c> 里，而现场没有人会去翻它，
/// 于是 <see cref="CleanupAsk"/> 那句「明细见清理流水」指向的是一个不存在的地方。
/// </para>
/// <para>
/// ⚠️ **翻账的逻辑放 Core 而不是窗口**：App 层没有测试工程（与 <c>CleanupAsk</c>
/// 搬过来是同一条理由），而这些恰恰是**会错的那种代码** —— 时间格式、
/// 动作码翻人话、单号查不到时的兜底，每一样写错了界面上都只是「看着有点怪」，
/// 不会有任何东西喊。
/// </para>
/// </remarks>
public static class CleanupLogView
{
    /// <summary>单号查不到时那一格写什么。</summary>
    /// <remarks>
    /// ⚠️ 逐字与 <c>PlaybackServer.Page.cs</c> 里那个 JS 兜底一致 ——
    /// 两处写两个词的话，同一件事在局域网回放页和这个窗口上会是两个说法。
    /// </remarks>
    public const string NoWaybillText = "（无单号）";

    /// <summary>一条流水都没有时显示什么。</summary>
    public const string EmptyText =
        "还没有清理记录。清过哪一条、为什么清、哪几条按 I8 保留，都会记在这里。";

    /// <summary>审计文件里那些读不动的行。</summary>
    public static string DescribeUnreadable(int lines) =>
        $"另有 {lines} 行读不动（写了一半或内容坏了），它们不在上面的流水里。";

    /// <summary>
    /// 翻账。**最近的在上**（审计是追加写的，倒着走即可）。
    /// </summary>
    /// <param name="records">审计里的全部记录。</param>
    /// <param name="entries">录像索引 —— 只用来把证据 id 换成用户认得的单号。</param>
    public static IReadOnlyList<CleanupLogRow> Build(
        IReadOnlyList<CleanupAuditRecord> records,
        IReadOnlyList<RecordingEntry> entries)
    {
        var waybills = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            // ⚠️ 用索引器而不是 Add：这份对照表只是给界面看的，万一索引里
            // 同一个 EvidenceId 出现两行，Add 会当场抛 —— 那是「窗口打不开」，
            // 比「单号显示得不够准」严重得多。
            waybills[entry.EvidenceId] = entry.Waybill.Value;
        }

        var rows = new List<CleanupLogRow>(records.Count);

        for (var i = records.Count - 1; i >= 0; i--)
        {
            var record = records[i];

            rows.Add(new CleanupLogRow(
                DescribeAt(record.At),
                waybills.TryGetValue(record.EvidenceId, out var waybill)
                    && !string.IsNullOrWhiteSpace(waybill)
                        ? waybill
                        : NoWaybillText,
                DescribeAction(record.Action),
                DescribeReason(record))
            {
                EvidenceId = record.EvidenceId,
            });
        }

        return rows;
    }

    /// <summary>把审计里的 ISO 时间翻成本地时间。</summary>
    /// <remarks>
    /// ⚠️ 解析不了就**原样印出来**：这是审计，宁可摆一串看不懂的东西，
    /// 也不能把这一行吃掉、更不能编一个时间 —— 那正是这本账存在的意义反面。
    /// </remarks>
    public static string DescribeAt(string at) =>
        DateTimeOffset.TryParse(
            at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : at;

    /// <summary>动作码翻人话。</summary>
    /// <remarks>
    /// ⚠️ 一次成功的清理留**两条**（<c>deleting</c> + <c>deleted</c>，见
    /// <see cref="CleanupAuditRecord"/>），所以这里刻意把它们翻成
    /// 「准备清理」/「已清理」两个**看得出先后**的词，而不是都写成「清理」——
    /// 两行一模一样的话，用户会以为同一条删了两遍。
    /// </remarks>
    public static string DescribeAction(string action) => action switch
    {
        "deleting" => "准备清理",
        "deleted" => "已清理",
        "refused" => "保留（没清）",
        "failed" => "清理失败",
        // ⚠️ 认不出的动作**原样印出来**。吞掉或者编一个中文，
        // 等于在这本「不能静默」的账上又静默了一次。
        _ => action,
    };

    /// <summary>为什么。失败时把失败原因接在后面。</summary>
    public static string DescribeReason(CleanupAuditRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.FailureReason))
        {
            return record.Reason;
        }

        return string.IsNullOrWhiteSpace(record.Reason)
            ? record.FailureReason!
            : $"{record.Reason} —— {record.FailureReason}";
    }
}
