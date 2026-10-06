using System.Text;
using System.Text.Json;

namespace VidLog.Desktop.Core.Cleanup;

/// <summary>
/// 「这条证据**已经成功发布到归档层**」的一条记录。
/// </summary>
/// <param name="EvidenceId">索引里的那一条（<c>{sessionId}-{序号}</c>）。</param>
/// <param name="PublishedAt">放进归档层那一刻。</param>
/// <param name="Location">归档层里的相对路径 —— 出事时用来回答「发到哪儿去了」。</param>
public sealed record PublishedRecord(
    string EvidenceId,
    DateTimeOffset PublishedAt,
    string Location);

/// <summary>
/// 桌面**自己发布出去**的那一份「已归档」账（T18）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它。</b>清理的时间锚原来只有一个来源：<c>receipts.jsonl</c>
/// （<see cref="VidLog.Desktop.Core.Upload.ReceiptStore"/>）。而那份回执**只有手机上传
/// 那一路会写**（<c>UploadReceiver</c>）—— 桌面自己录、自己发到 NAS 的录像**没有任何
/// 锚点**，于是被 <see cref="CleanupPlanner"/> 判成「还没成功归档，这是唯一副本」
/// 而**永久豁免**：设置里那个「本机保留多久」对本机录制内容根本不成立，
/// 只能清手机传上来的。后果是**盘满只是时间问题**，而盘一满就开始静默丢（T22）。
/// </para>
/// <para>
/// <b>为什么不复用回执。</b>回执的类型 <c>ReceiptPayload</c> **是线上契约** ——
/// 它就是 <c>/commit</c> 的响应体，包在带 HMAC 的 <c>CommitResponse</c> 里发给手机的。
/// 拿它来记自录，等于**让电脑端给自己签一张「本来只发给手机」的收据**，
/// 于是那个文件从「我给谁发过收据」变成「我看过哪些东西」——
/// 将来谁按前一个意思读它，就会读到错的答案。两边记的其实是**同一个事实**
/// （「这条证据在 T 时刻进了归档层」），区别只在**谁产出的**，所以分开记。
/// </para>
/// </remarks>
public sealed class PublishedStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PublishedStore(string path)
    {
        _path = path;
    }

    /// <summary>
    /// 一次读全表：<c>evidenceId</c> → **归档成功时刻**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 形状与 <c>ReceiptStore.LoadAnchorMapAsync</c> 一致，好让清理层把两张表叠在一起。
    /// </para>
    /// <para>
    /// ⚠️ 与回执表相反，这里**先写的胜出**（回执那边是后写的胜出）。
    /// 回执那边敢用「后者胜出」，是因为重复 commit 走「原样再给一份」、
    /// 两行内容一样；而这里**同一条重复发布会给出两个不同的时刻** ——
    /// 取后者会让时间锚往后挪，那条录像就比它该被清的时刻更晚才能清。
    /// 起算点的意思是「**第一次**在别处有了第二份」，所以取最早的。
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> LoadAnchorMapAsync(
        CancellationToken cancellationToken = default)
    {
        var anchors = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        if (!File.Exists(_path))
        {
            return anchors;
        }

        foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<PublishedRecord>(line, SerializerOptions);
                if (record is not null && !string.IsNullOrEmpty(record.EvidenceId))
                {
                    // 先写的胜出 ⇒ 已经有了就别动它。
                    anchors.TryAdd(record.EvidenceId, record.PublishedAt);
                }
            }
            catch (JsonException)
            {
                // 坏行跳过 —— 与索引、打点、标签、回执同一条规矩。
            }
        }

        return anchors;
    }

    public async Task AppendAsync(PublishedRecord record, CancellationToken cancellationToken = default)
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
