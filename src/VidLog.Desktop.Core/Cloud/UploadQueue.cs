using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Cloud;

/// <summary>
/// 一条录像在网盘上传队列里的状态。
/// </summary>
/// <remarks>
/// ⚠️ 落盘存的是**数字**，所以**顺序即格式，别重排**。
/// </remarks>
public enum CloudUploadState
{
    /// <summary>等着传。</summary>
    Pending = 0,

    /// <summary>正在传。</summary>
    Uploading = 1,

    /// <summary>网盘上已经有了。</summary>
    Done = 2,

    /// <summary>传失败了，等重试。</summary>
    Failed = 3,

    /// <summary>
    /// 用户明确说了「这条我不传了」。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与 <see cref="Failed"/> 是**两件事**：失败是「没传成，还要试」，
    /// 放弃是「别再试了」。混成一个的话，那句「立即重试失败上传」会把用户
    /// 明确放弃掉的又翻出来重传一遍 —— 而他点的是「重试失败的那些」。
    /// </remarks>
    Abandoned = 4,
}

/// <summary>
/// 一个状态还能做什么。
/// </summary>
/// <remarks>
/// ⚠️ 放在 Core 而不是界面那边：这两个判断**不只是「显示哪个按钮」**——
/// <see cref="CloudUploadService"/> 自己也要按同一把尺子拒掉
/// 「放弃一条已经传完的」。界面上写一份、服务里再写一份的话，
/// 迟早出现「按钮亮着但按下去说了不算」。
/// </remarks>
public static class CloudUploadStates
{
    /// <summary>这一条还能不能【放弃】。</summary>
    /// <remarks>已经传完的、已经放弃的都不该再放弃一次。</remarks>
    public static bool CanAbandon(CloudUploadState state) => state is not (CloudUploadState.Done
        or CloudUploadState.Abandoned);

    /// <summary>这一条还能不能【恢复】。</summary>
    public static bool CanResume(CloudUploadState state) => state == CloudUploadState.Abandoned;

    /// <summary>
    /// 这一条还能不能**立刻**再试一次（队列每一行那颗【重试】）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只有 <see cref="CloudUploadState.Failed"/>，<b>已放弃的不算</b> ——
    /// 那是用户明确说过「这条我不传了」，把它跟失败揉成同一个动作的话，
    /// 界面上就成了「一颗【重试】按在用户已经放弃的那条上」；
    /// 想让它再传，对的入口是【恢复】。
    /// </remarks>
    public static bool CanRetry(CloudUploadState state) => state == CloudUploadState.Failed;

    /// <summary>这一条会不会被自动重试（界面上要把它说清楚）。</summary>
    public static bool WillRetry(CloudUploadState state, DateTimeOffset? nextAttemptAt) =>
        state == CloudUploadState.Failed && nextAttemptAt is not null;
}

/// <summary>
/// 队列里的一条。
/// </summary>
/// <param name="EvidenceId">哪一条录像。**主键**。</param>
/// <param name="Location">归档相对路径（索引里的那一份）。</param>
/// <param name="RemotePath">
/// 打算传到网盘的哪个位置。
/// </param>
/// <remarks>
/// ⚠️ 记下来不是为了「靠它判在不在」（那是列目录说了算的），
/// 而是为了**能回答「它到底传哪儿去了」** —— 用户上网页版找不到文件时，
/// 唯一能把话说清楚的凭据就是它。而它取决于上传那一刻的标签，
/// 后来标签改了它也**不跟着改**：它记的是事实，不是当前配置。
/// </remarks>
/// <param name="SizeBytes">多大（进度与「还要传多少」用）。</param>
/// <param name="StartedAt">开录时间 —— **补传范围按它筛**（图上原话「以开录自然日为准」）。</param>
/// <param name="State">现在到哪一步了。</param>
/// <param name="Attempts">试过几次。</param>
/// <param name="LastError">上一次为什么没成（给人看的一句话）。</param>
/// <param name="UpdatedAt">这一行是什么时候写的。</param>
/// <param name="NextAttemptAt">
/// 下一次自动重试的时刻。
/// </param>
/// <remarks>
/// ⚠️ <b><see langword="null"/> 就是「不再自动重试」</b>，而且只有一个含义：
/// 要么是这次失败被判成终态（<c>BaiduPanException.IsHopeless</c>，或者次数用完），
/// 要么是这条本来就是老版本写下的（那时还没有自动重试这回事）。
/// 两种都是「要人动手」——界面照这句话写，不必再分。
/// </remarks>
public sealed record UploadQueueItem(
    string EvidenceId,
    string Location,
    string RemotePath,
    long SizeBytes,
    DateTimeOffset StartedAt,
    CloudUploadState State,
    int Attempts,
    string? LastError,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextAttemptAt = null);

/// <summary>
/// 上传队列的落盘形态（JSON Lines，**最后一行胜出**）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>JsonLinesRecordingIndex</c> 同一路数，理由也一样（规格 §6.2「数据删除必须极度克制」）：
/// 一条录像的状态会变好几次（等传 → 传中 → 完成，或者 → 失败 → 重试），
/// 而**追加写永远不会重写既有记录**，因此不存在「重写过程中断电导致整个队列损坏」。
/// 读的时候同一条取**最后**一行。
/// </para>
/// <para>
/// ⚠️ 这个文件是**我们自己**写的（不像索引那样两端都写），所以序列化就用
/// 普通的 camelCase 反序列化，不需要 <c>JsonLinesRecordingIndex</c> 那套
/// 按候选字段名逐个试的宽容读法。代价是**改属性名 = 改落盘格式**。
/// </para>
/// <para>
/// ⚠️ 队列**不是**证据的一部分：它丢了、读坏了，代价只是「不知道传到哪了」，
/// 重扫一遍索引就能重建。所以读坏的行一律丢掉并留痕，**绝不因此抛**。
/// </para>
/// </remarks>
public sealed class UploadQueue
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UploadQueue(string path, IAppLogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>队列文件。</summary>
    public string Path => _path;

    /// <summary>读出整个队列。<b>同一条取最后一行</b>。</summary>
    public async Task<IReadOnlyList<UploadQueueItem>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(_path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log(LogLevel.Warn, "网盘", $"上传队列读不出来，按空队列处理：{ex.Message}");
            return [];
        }

        var byId = new Dictionary<string, UploadQueueItem>(StringComparer.Ordinal);
        var broken = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                // ⚠️ 判空要用 `is { Length: > 0 }` 而**不是** `item.EvidenceId.Length > 0`：
                // 一行合法的 `{}` 反序列化出来是**所有字段都是默认值**的一条
                // （`EvidenceId` 是 null），直接读 `.Length` 会在这儿空引用 ——
                // 而这一段的承诺是「坏行丢掉、绝不因此抛」。
                if (JsonSerializer.Deserialize<UploadQueueItem>(line, SerializerOptions)
                    is { EvidenceId: { Length: > 0 } } item)
                {
                    byId[item.EvidenceId] = item;
                }
                else
                {
                    broken++;
                }
            }
            catch (JsonException)
            {
                broken++;
            }
        }

        if (broken > 0)
        {
            _logger.Log(LogLevel.Warn, "网盘", $"上传队列里有 {broken} 行读不出来，已跳过");
        }

        // ⚠️ 上次跑到一半被杀掉的那些条目卡在「正在传」上，而那个状态**没人会再来推它** ——
        // 不在这里翻回「等着传」，那些录像就会永远停在队列里从头到尾没动过，
        // 而界面上显示的不是「失败」也不是「等待」，是一个看不出要干嘛的中间态。
        //
        // ⚠️ **这一段每读一遍都会把一个正在飞的上传也翻回「等着传」**，而它曾经
        // 正是 A2「同一份录像并发传两遍」的帮凶（2026-10-16）。修的地方**不在这里**：
        // 挪成「每个进程只修一次」会把另一件事弄坏 —— 上传中途被取消（关机）的那条
        // 会一直卡在「正在传」上，直到下次开机才有人管。
        // 真正的根因是**收尾那条路绕开了 `_drainGate`**，见 `CloudUploadService.PublishAsync`。
        return [.. byId.Values.Select(i => i.State == CloudUploadState.Uploading
            ? i with { State = CloudUploadState.Pending, LastError = null }
            : i)];
    }

    /// <summary>某一条现在什么状态；队列里没有它就是 <see langword="null"/>。</summary>
    public async Task<UploadQueueItem?> FindAsync(
        string evidenceId, CancellationToken cancellationToken = default)
    {
        foreach (var item in await LoadAsync(cancellationToken))
        {
            if (string.Equals(item.EvidenceId, evidenceId, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>追加一行。这是**唯一**的写入方式（最后一行胜出）。</summary>
    public async Task AppendAsync(UploadQueueItem item, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(item, SerializerOptions);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
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
}
