using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Cleanup;

/// <summary>归档层的种类。</summary>
public enum ArchiveBackendKind
{
    /// <summary>归档层就是**本机磁盘**。</summary>
    /// <remarks>
    /// 规格 §3.5.1：此时该副本是**唯一副本**，不提供清理选项 ——
    /// 「显示了但禁用」和「根本不显示」是两回事，后者才符合「不提供」。
    /// </remarks>
    LocalDisk,

    Nas,
    Cloud,
}

/// <summary>回查归档层的结果。</summary>
/// <param name="Exists">归档层上还在不在。</param>
/// <param name="FailureReason">回查失败的原因（查不了 ≠ 不存在）。</param>
public sealed record ArchiveVerifyResult(bool Exists, string? FailureReason)
{
    /// <summary>回查本身出错（网络断了、凭据失效）。</summary>
    /// <remarks>
    /// 这个状态**不能当成「不存在」** —— 那会导致误删（I8）。
    /// 查不了的时候唯一安全的动作是**不删**。
    /// </remarks>
    public bool CouldNotVerify => FailureReason is not null;
}

/// <summary>
/// 归档层。
/// </summary>
/// <remarks>
/// 规格 §3.5.4 是 I8 的唯一落点：清理前**必须**回查归档层，回查不通过**绝不删除**。
/// </remarks>
public interface IArchiveBackend
{
    ArchiveBackendKind Kind { get; }

    /// <summary>回查某条录像的成品在归档层上还在不在。</summary>
    Task<ArchiveVerifyResult> VerifyAsync(RelativePath location, CancellationToken cancellationToken = default);
}

/// <summary>保留期策略（规格 §3.5.2）。</summary>
/// <param name="Mode">按什么清理。</param>
/// <param name="KeepDays">保留天数；<see cref="RetentionMode.KeepAll"/> 时无意义。</param>
/// <param name="MinFreeBytes">磁盘剩余低于它就清；<see cref="RetentionMode.ByDays"/> 时无意义。</param>
public sealed record RetentionPolicy(RetentionMode Mode, int? KeepDays = null, long? MinFreeBytes = null)
{
    /// <summary>
    /// 默认：**什么都不清**。
    /// </summary>
    /// <remarks>
    /// 这是刻意的默认值。规格 §6.2「数据删除必须极度克制」——
    /// 清理是一个会造成不可逆后果的动作，它必须是用户**主动开启**的，
    /// 不能因为「装完就有个默认 30 天」而在用户不知情时删掉东西。
    /// </remarks>
    public static RetentionPolicy KeepAll { get; } = new(RetentionMode.KeepAll);
}

public enum RetentionMode
{
    /// <summary>全部保留（默认）。</summary>
    KeepAll,

    /// <summary>按天数。</summary>
    ByDays,

    /// <summary>按磁盘剩余空间。</summary>
    BySpace,
}

/// <summary>一条可清理的候选。</summary>
public sealed record CleanupCandidate(RecordingEntry Entry, long SizeBytes, RelativePath Location, string Why);

/// <summary>一条被排除的录像，以及**为什么没清它**。</summary>
/// <remarks>
/// 规格 §3.5.5：用户要能问「这条为什么被删 / 为什么没删」。
/// 只给候选不给豁免，等于让人无从质疑。
/// </remarks>
public sealed record ExemptedEntry(RecordingEntry Entry, string Why);

/// <summary>一次清理的完整计划。</summary>
public sealed record CleanupPlan(
    IReadOnlyList<CleanupCandidate> Candidates,
    IReadOnlyList<ExemptedEntry> Exempted)
{
    public long TotalBytes => Candidates.Sum(c => c.SizeBytes);
}

/// <summary>
/// 清理计划的制定。
/// </summary>
/// <remarks>
/// 纯函数：给定「有哪些录像、多大、锁没锁、策略、现在几点」，算出该清谁。
/// 它**不碰文件系统** —— 真正删除在别处，而且要先回查归档层（I8）。
/// </remarks>
public sealed class CleanupPlanner
{
    /// <summary>
    /// 最近这段时间内的录像**一律不清**。
    /// </summary>
    /// <remarks>
    /// 规格 §3.5.3 的第三条豁免。刚录完的东西往往还在被检查、被导出 ——
    /// 而清理是不可逆的，所以给它一个冷静期。
    /// </remarks>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromHours(24);

    public CleanupPlan Plan(
        IReadOnlyList<RecordingEntry> entries,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> labels,
        RetentionPolicy policy,
        DateTimeOffset now,
        long? freeBytes = null)
    {
        var candidates = new List<CleanupCandidate>();
        var exempted = new List<ExemptedEntry>();

        // 策略是「全部保留」时，谁都别动。
        if (policy.Mode == RetentionMode.KeepAll)
        {
            return new CleanupPlan(
                [], [.. entries.Select(e => new ExemptedEntry(e, "保留策略是「全部保留」"))]);
        }

        var eligible = new List<(RecordingEntry Entry, DateTimeOffset EndedAt)>();

        foreach (var entry in entries)
        {
            // 规格 §3.5.3 的三条豁免，一条都不能少。
            if (IsLocked(entry, labels))
            {
                exempted.Add(new ExemptedEntry(entry, "已被用户锁定"));
                continue;
            }

            if (now - entry.EndedAt < FreshWindow)
            {
                exempted.Add(new ExemptedEntry(entry, $"录完还不到 {FreshWindow.TotalHours:0} 小时"));
                continue;
            }

            eligible.Add((entry, entry.EndedAt));
        }

        switch (policy.Mode)
        {
            case RetentionMode.ByDays when policy.KeepDays is { } days:
                {
                    var cutoff = now.AddDays(-days);

                    foreach (var (entry, endedAt) in eligible)
                    {
                        if (endedAt < cutoff)
                        {
                            candidates.Add(new CleanupCandidate(
                                entry, EstimateBytes(entry), entry.Location, $"录于 {endedAt:yyyy-MM-dd}，超过 {days} 天"));
                        }
                        else
                        {
                            exempted.Add(new ExemptedEntry(entry, $"还在 {days} 天保留期内"));
                        }
                    }

                    break;
                }

            case RetentionMode.BySpace when policy.MinFreeBytes is { } floor && freeBytes is { } free:
                {
                    if (free >= floor)
                    {
                        foreach (var (entry, _) in eligible)
                        {
                            exempted.Add(new ExemptedEntry(entry, "磁盘剩余充足，不需要清"));
                        }

                        break;
                    }

                    // 空间不够 —— **从最旧的开始**清，清到够了就停。
                    var need = floor - free;
                    long freed = 0;

                    foreach (var (entry, _) in eligible.OrderBy(e => e.EndedAt))
                    {
                        if (freed >= need)
                        {
                            exempted.Add(new ExemptedEntry(entry, "已经清够了"));
                            continue;
                        }

                        var size = EstimateBytes(entry);
                        candidates.Add(new CleanupCandidate(
                            entry, size, entry.Location, "磁盘剩余不足，按最旧的先清"));
                        freed += size;
                    }

                    break;
                }

            default:
                foreach (var (entry, _) in eligible)
                {
                    exempted.Add(new ExemptedEntry(entry, "策略参数不完整，什么都不清"));
                }

                break;
        }

        return new CleanupPlan(candidates, exempted);
    }

    /// <summary>
    /// 这条录像的成品占多大。
    /// </summary>
    /// <remarks>
    /// 用**时长推算**而不是去问文件系统：计划制定这一步刻意不碰磁盘
    /// （那样会有「算的过程中文件被别的东西删了」这类竞态）。
    /// 真实大小在执行阶段复核 —— 那里本来就要 stat 一次。
    /// <para>
    /// 码率按 640x480@30 的 H.264 实测约 160 KB/s 估。
    /// </para>
    /// </remarks>
    public static long EstimateBytes(RecordingEntry entry) =>
        (long)Math.Max(0, entry.Duration.TotalSeconds * 160 * 1024);

    private static bool IsLocked(
        RecordingEntry entry, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> labels) =>
        labels.TryGetValue(entry.EvidenceId, out var entryLabels)
        && entryLabels.TryGetValue(LabelKeys.Locked, out var raw)
        && bool.TryParse(raw, out var locked)
        && locked;
}
