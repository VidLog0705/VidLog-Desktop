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
public sealed record UploadQueueItem(
    string EvidenceId,
    string Location,
    string RemotePath,
    long SizeBytes,
    DateTimeOffset StartedAt,
    CloudUploadState State,
    int Attempts,
    string? LastError,
    DateTimeOffset UpdatedAt);

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
