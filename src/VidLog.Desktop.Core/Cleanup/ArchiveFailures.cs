using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Cleanup;

/// <summary>一条发布记录是「没成」还是「后面补上了」。</summary>
/// <remarks>
/// ⚠️ 落盘存的是**数字**，所以**顺序即格式，别重排**（与 <c>CloudUploadState</c> 同一条规矩）。
/// </remarks>
public enum ArchiveFailureState
{
    /// <summary>那次没发上去。</summary>
    Failed = 0,

    /// <summary>后来补上了 —— 这一行把上面那条撤掉。</summary>
    Recovered = 1,
}

/// <summary>
/// 「这一条发到归档层没成功」的一笔账。
/// </summary>
/// <param name="EvidenceId">哪一条录像。**主键**。</param>
/// <param name="Location">归档层里本该落在哪 —— 出事时用来回答「它该发到哪儿去」。</param>
/// <param name="Reason">为什么没成（给人看的一句话）。撤掉的那一行是空的。</param>
/// <param name="At">这一行是什么时候写的。</param>
/// <param name="State">没成，还是后面补上了。</param>
public sealed record ArchiveFailureRecord(
    string EvidenceId,
    string? Location,
    string? Reason,
    DateTimeOffset At,
    ArchiveFailureState State);

/// <summary>
/// 「哪几条没发到归档层」的落盘账（T23-A）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它。</b><see cref="ArchiveRelay.LastFailure"/> / <c>LastPublished</c>
/// **只在内存**里 ⇒ 重启之后那条提示就没了。而它要说的事很重：
/// **盘上那一份现在只有一份**，用户若以为已经双份了，就可能手动删掉唯一的那一份
/// （正是 I2 要防的事）。于是「必须对用户可见」（I3）在重启那一刻断掉 ——
/// 恰恰是最容易出事的时候：重启往往就是因为刚出过问题。
/// </para>
/// <para>
/// ⚠️ <b>它与 <see cref="PublishedStore"/> 记的不是同一件事，别合并。</b>
/// 那边记的是「**成了**的那些」—— 清理层拿它当时间锚，是**只增不减**的一条条事实；
/// 这边记的是「**没成的**」—— 一个**会被撤掉**的待办集合。
/// 一个是台账，一个是欠账。
/// </para>
/// <para>
/// <b>形态照 <c>UploadQueue</c></b>（JSON Lines、**最后一行胜出**）：同一条的状态会变
/// （没成 → 后来补上），而追加写永远不会重写既有记录，因此不存在「重写过程中断电
/// 导致整本账损坏」。读的时候同一条取**最后**一行。
/// </para>
/// <para>
/// ⚠️ <b>这个文件里的行数只在「真出事」时才涨。</b>发布成功**不会**追加一行来撤销 ——
/// 只有「内存里记着它还是欠的」才写那一行撤销记录（见 <see cref="NoteRecoveredAsync"/>）。
/// 否则每录一段就多一行，这本账会变成一个只涨不落的日志（那正是 T22 的形状）。
/// </para>
/// <para>
/// ⚠️ 它不是证据的一部分：读坏了、丢了，代价只是「不知道上次哪几条没发上去」。
/// 所以坏行一律丢掉，**绝不因此抛**（与队列、索引、回执、自记账同一条规矩）。
/// </para>
/// </remarks>
public sealed class ArchiveFailureLog
{
    /// <remarks>
    /// ⚠️ <c>camelCase</c> 与 <c>UploadQueue</c> 对齐（同为自己写的落盘文件），
    /// 也与隔壁的 <see cref="PublishedStore"/> 对齐 —— 这个文件会进诊断包，人是要打开看的。
    /// </remarks>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>现在还欠着的那些（<c>evidenceId</c> → 那一笔）。**内存里的那一份是权威。**</summary>
    private readonly Dictionary<string, ArchiveFailureRecord> _outstanding =
        new(StringComparer.Ordinal);

    /// <param name="path">落盘位置（<c>DataLayout.ArchiveFailurePath</c>）。</param>
    /// <param name="logger">留痕用。</param>
    public ArchiveFailureLog(string path, IAppLogger? logger = null)
    {
        _path = path;
        Logger = logger ?? NullLogger.Instance;

        // ⚠️ 这里**同步**读一次是刻意的：这个文件**只在真出事时才涨**（正常机器上是空的），
        // 而且只在装配时读这一次 —— 换一个懒加载的写法要多一层并发状态，
        // 换不来什么。之后每一次写都是异步的。
        LoadIntoMemory();
    }

    private IAppLogger Logger { get; }

    /// <summary>
    /// 现在还欠着的那些 —— 给界面读。<b>不碰磁盘</b>（内存里那份已经是权威）。
    /// </summary>
    public IReadOnlyCollection<ArchiveFailureRecord> Outstanding => _outstanding.Values;

    /// <summary>一次读全表（按 <c>evidenceId</c> 最后一行胜出，只留还欠着的）。</summary>
    /// <remarks>
    /// 给测试与自检用。生产代码走 <see cref="Outstanding"/> —— 那边读的是内存，
    /// 不会在一个「给界面看一眼」的路径上做文件 I/O。
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, ArchiveFailureRecord>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var outstanding = new Dictionary<string, ArchiveFailureRecord>(StringComparer.Ordinal);

        foreach (var record in await ReadAllAsync(cancellationToken))
        {
            if (record.State == ArchiveFailureState.Failed)
            {
                outstanding[record.EvidenceId] = record;
            }
            else
            {
                outstanding.Remove(record.EvidenceId);
            }
        }

        return outstanding;
    }

    /// <summary>记一笔「这一条没发上去」。同一条重复失败只留**最后一次**的原因。</summary>
    public async Task NoteFailedAsync(ArchiveFailureRecord record, CancellationToken cancellationToken = default)
    {
        _outstanding[record.EvidenceId] = record;
        await AppendAsync(record, cancellationToken);
    }

    /// <summary>
    /// 「这一条后来补上了」—— 把这笔欠账撤掉。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>没欠着的时候什么都不写</b>：这是这本账不至于只涨不落的唯一机制。
    /// 调用方（<see cref="ArchiveRelay"/>）每次发布成功都会调它，
    /// 若这里无条件追加一行，正常机器的这个文件会每录一段长一行。
    /// </remarks>
    public async Task NoteRecoveredAsync(
        string evidenceId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        if (!_outstanding.Remove(evidenceId))
        {
            return;
        }

        await AppendAsync(
            new ArchiveFailureRecord(evidenceId, null, null, at, ArchiveFailureState.Recovered),
            cancellationToken);
    }

    private void LoadIntoMemory()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            using var reader = new StreamReader(
                _path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            while (reader.ReadLine() is { } line)
            {
                Apply(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ⚠️ 读不出来**不代表没有欠账**，但也没法知道欠了哪些。
            // 这里只留痕、不抛：抛出去会让整个应用起不来，而代价只是
            // 「这次启动不显示那条提示」—— 用户下一段录制的成败照旧会重新记上。
            Logger.Log(LogLevel.Warn, "归档",
                $"上次「没发到归档层」的那本账读不出来（{ex.Message}）—— "
                + "这次启动不会提示它，但盘上那几份仍然只有一份。");
        }
    }

    private void Apply(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            var record = JsonSerializer.Deserialize<ArchiveFailureRecord>(line, SerializerOptions);
            if (record is null || string.IsNullOrEmpty(record.EvidenceId))
            {
                return;
            }

            if (record.State == ArchiveFailureState.Failed)
            {
                // 最后一行胜出 ⇒ 直接盖掉（重复失败只留最后一次的原因）。
                _outstanding[record.EvidenceId] = record;
            }
            else
            {
                _outstanding.Remove(record.EvidenceId);
            }
        }
        catch (JsonException)
        {
            // 坏行跳过 —— 与索引、打点、标签、回执、自记账同一条规矩。
        }
    }

    private async Task<IReadOnlyList<ArchiveFailureRecord>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var records = new List<ArchiveFailureRecord>();

        if (!File.Exists(_path))
        {
            return records;
        }

        foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<ArchiveFailureRecord>(line, SerializerOptions);
                if (record is not null && !string.IsNullOrEmpty(record.EvidenceId))
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // 坏行跳过。
            }
        }

        return records;
    }

    private async Task AppendAsync(ArchiveFailureRecord record, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(record, SerializerOptions);

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
}
