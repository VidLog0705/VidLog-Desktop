using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Cloud;

/// <summary>
/// 上传队列当下的样子（给「百度网盘上传」那一页用，设计图 `_45` / `_46`）。
/// </summary>
/// <param name="Pending">等着传的。</param>
/// <param name="Uploading">正在传的。</param>
/// <param name="Done">网盘上已经有了的。</param>
/// <param name="Failed">传失败了的。</param>
/// <param name="Active">此刻真的在跑的并发数（「同时上传数」是它的上限）。</param>
/// <param name="LastError">最近一次出错的原因；没出过错就是 <see langword="null"/>。</param>
/// <param name="LastChecked">最近一次与网盘对比是什么时候。</param>
/// <param name="Enabled">
/// 「启用自动上传」开着没有。⚠️ 关闭时界面上那行原话是
/// 「已停用：勾选"启用自动上传"并保存后，本机新录制的录像才会上传」（设计图 `_45`）。
/// </param>
public sealed record UploadStatus(
    int Pending,
    int Uploading,
    int Done,
    int Failed,
    int Active,
    string? LastError,
    DateTimeOffset? LastChecked,
    bool Enabled)
{
    /// <summary>队列里一共几条。</summary>
    public int Total => Pending + Uploading + Done + Failed;
}

/// <summary>
/// 把录像送上百度网盘的那一套：队列、对比去重、并发、定时检查。
/// </summary>
/// <remarks>
/// <para>
/// 两个入口，**共用下面同一段上传逻辑**（<see cref="DrainAsync"/>）：
/// </para>
/// <list type="number">
/// <item><b>收尾那一条路</b>（<see cref="PublishAsync"/>）：一条录像刚收尾完就传 ——
/// 对应设计图 `_45`「启用自动上传：仅此开关开启后新开始录制的视频会上传」。
/// 开关没开就**不传**，并如实说为什么。</item>
/// <item><b>补传那一条路</b>（<see cref="SyncNowAsync"/>）：扫一遍库，把该有而还没有的
/// 补上去 —— 对应设计图 `_46`「自动对比补传」与「补传范围」。</item>
/// </list>
/// <para>
/// ⚠️ <b>两条路都写同一个队列、都走同一段上传。</b>各写一份的话，
/// 「重试算几次」「失败记在哪」这些会慢慢走岔，而走岔之后的表现是
/// **偶发的重复上传**与**对不上的失败原因** —— 两种都很难查。
/// </para>
/// <para>
/// ⚠️ <b>上传失败绝不影响任何本机的东西</b>（I2）：本机那一份、索引、
/// 收尾流程一条都不看这里的结果。它唯一的后果是「这条还不能被清理」。
/// </para>
/// </remarks>
public sealed class CloudUploadService : IAsyncDisposable
{
    /// <summary>多久检查一次队列。</summary>
    /// <remarks>设计图 `_46` 原话：「开启后约每分钟检查一次队列」。</remarks>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    private readonly BaiduPanSession _session;
    private readonly BaiduPanUploader _uploader;
    private readonly IBaiduPanApi _api;
    private readonly BaiduPanLayout _layout;
    private readonly UploadQueue _queue;
    private readonly IRecordingIndex _index;
    private readonly ILabelStore _labels;
    private readonly StorageLocations _locations;
    private readonly IAppLogger _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _interval;

    /// <summary>
    /// 当前这一组设置。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它是**活**的：用户在设置页点「应用」之后要立刻生效，而那个 tick 循环
    /// 正在另一个线程上读它。用 <see langword="volatile"/> 是因为这里只要求
    /// 「读到的是某一个完整的快照」—— 设置是一个不可变 record，换掉整个引用即可。
    /// </remarks>
    private volatile CloudUploadSettings _settings;

    /// <summary>同一时刻只跑一次 drain。两次一起跑会让同一条录像被传两遍。</summary>
    private readonly SemaphoreSlim _drainGate = new(1, 1);

    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// 「启用自动上传」那场开关是什么时候打开的。
    /// </summary>
    /// <remarks>
    /// ⚠️ 设置文件里没有（手改过设置）时的兜底：按**服务启动那一刻**算 ——
    /// 朝「少传」的那头落。按 <see cref="DateTimeOffset.MinValue"/> 算的话，
    /// 打开开关会把库里**所有**历史录像一次全传上去，而用户以为只是「从现在起」。
    /// </remarks>
    private readonly DateTimeOffset _since;

    private Task? _loop;
    private int _active;
    private string? _lastError;
    private DateTimeOffset? _lastChecked;

    public CloudUploadService(
        BaiduPanSession session,
        BaiduPanUploader uploader,
        IBaiduPanApi api,
        BaiduPanLayout layout,
        UploadQueue queue,
        IRecordingIndex index,
        ILabelStore labels,
        StorageLocations locations,
        CloudUploadSettings settings,
        IAppLogger? logger = null,
        TimeSpan? interval = null,
        Func<DateTimeOffset>? now = null)
    {
        _session = session;
        _uploader = uploader;
        _api = api;
        _layout = layout;
        _queue = queue;
        _index = index;
        _labels = labels;
        _locations = locations;
        _settings = settings;
        _logger = logger ?? NullLogger.Instance;
        _now = now ?? (() => DateTimeOffset.Now);
        _interval = interval ?? DefaultInterval;

        // ⚠️ 「启用自动上传」这场开关是什么时候打开的，判据就是它。
        // 设置文件里没有（手改过设置）时按**服务启动那一刻**算 ——
        // 朝「少传」的那头落：打开开关的本意是「从现在起」，
        // 而按 MinValue 算会把库里所有历史录像一次全传上去。
        _since = _now();
    }

    /// <summary>网盘上的落点规则（设置页要它，好把「远端路径」显示给用户看）。</summary>
    public BaiduPanLayout Layout => _layout;

    /// <summary>
    /// 登录那一套（设置页 `_45` 的「登录百度网盘 / 退出登录」两个按钮用它）。
    /// </summary>
    /// <remarks>
    /// 直接把它露出来而不是在这上面排六个转调方法：登录、轮询、续期、退出
    /// 是<b>同一个对象的一件事</b>，包一层只会让人以为这里还做了别的事。
    /// </remarks>
    public BaiduPanSession Session => _session;

    /// <summary>队列文件在哪（诊断包与设置页要「打开所在目录」）。</summary>
    public string QueuePath => _queue.Path;

    /// <summary>设置改了（用户点了「应用」）。下一次 tick、下一次上传就用新值。</summary>
    /// <remarks>
    /// ⚠️ <b>换的是整个快照，不是逐个字段赋值。</b>逐个赋值会让 tick 线程读到
    /// 「一半新一半旧」的一组设置，而其中最要紧的一对是
    /// 「启用自动上传 = 开」配着「开始上传时间 = 还没填」——
    /// 那正好是「把整库历史录像一次全传上去」这个最贵的情形。
    /// </remarks>
    public void UpdateSettings(CloudUploadSettings settings) => _settings = settings;

    /// <summary>
    /// 收尾那一条路：一条录像刚落地，问一句要不要顺手传上去。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>开关没开时回的是「失败」而不是「成功」。</b>这里很容易顺手写成
    /// 「没开就当传过了」—— 那会让回查归档层（I8）以为云端有一份，
    /// 于是本机**唯一**的那一份被清理删掉。
    /// </remarks>
    public async Task<ArchivePublishResult> PublishAsync(
        RelativePath location, string localPath, CancellationToken cancellationToken = default)
    {
        var settings = _settings;

        if (!settings.AutoUpload)
        {
            return ArchivePublishResult.Failed(
                "「启用自动上传」没开，这条没有传到百度网盘。");
        }

        var item = await EnqueueAsync(location, cancellationToken);

        if (item is null)
        {
            return ArchivePublishResult.Failed($"索引里没有这一条，不知道它是哪一条录像：{location.Value}");
        }

        if (item.State == CloudUploadState.Done)
        {
            return ArchivePublishResult.Ok;
        }

        var outcome = await UploadOneAsync(item, localPath, cancellationToken);

        return outcome.Published
            ? ArchivePublishResult.Ok
            : ArchivePublishResult.Failed(outcome.Reason ?? "上传百度网盘失败。");
    }

    /// <summary>队列现在的样子（设置页每一秒左右问一次）。</summary>
    public async Task<UploadStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        var items = await _queue.LoadAsync(cancellationToken);

        return new UploadStatus(
            items.Count(i => i.State == CloudUploadState.Pending),
            items.Count(i => i.State == CloudUploadState.Uploading),
            items.Count(i => i.State == CloudUploadState.Done),
            items.Count(i => i.State == CloudUploadState.Failed),
            Volatile.Read(ref _active),
            _lastError,
            _lastChecked,
            _settings.AutoUpload);
    }

    /// <summary>
    /// 「立即对比同步」：把该传的收进队列、与网盘实际内容对一遍、然后开传。
    /// </summary>
    /// <returns>这一次真的传上去几条。</returns>
    /// <remarks>
    /// ⚠️ <b>它是手点的，所以两个开关都没开也照样干活</b>（设计图 `_45`：
    /// 那两个开关关着的时候，这四个按钮在图上仍然是可点的）。
    /// 但它**仍然守「补传范围」**—— 那是用户对「哪些录像算数」的定义，
    /// 不是自动化的一部分。
    /// </remarks>
    public async Task<int> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings;

        await EnqueueEligibleAsync(settings.Floor, cancellationToken);

        // 对比去重**手点的时候每次都做**：它就是「只补传网盘上没有的」这句承诺的落法。
        // 省掉它能换来的只是「少一次列目录」，代价却是把已经传过的再传一遍。
        await CompareAsync(cancellationToken);

        return await DrainAsync(cancellationToken);
    }

    /// <summary>「立即重试失败上传」：把失败的那些翻回「等着传」，然后开传。</summary>
    public async Task<int> RetryFailedAsync(CancellationToken cancellationToken = default)
    {
        var items = await _queue.LoadAsync(cancellationToken);
        var now = _now();

        foreach (var item in items.Where(i => i.State == CloudUploadState.Failed))
        {
            await _queue.AppendAsync(
                item with { State = CloudUploadState.Pending, LastError = null, UpdatedAt = now },
                cancellationToken);
        }

        return await DrainAsync(cancellationToken);
    }

    /// <summary>起定时检查。重复调用是安全的（第二次什么也不做）。</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_interval);

            try
            {
                while (await timer.WaitForNextTickAsync(_stopping.Token))
                {
                    await TickAsync(_stopping.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止。
            }
        });
    }

    /// <summary>一次定时检查。</summary>
    /// <remarks>
    /// ⚠️ <b>这里抛出去的异常必须被吃掉</b>：这个循环是后台任务，
    /// 它挂掉的表现是「网盘再也没传过东西，而界面上一切正常」——
    /// 没有任何人会知道。网络断了是常态，不是异常。
    /// </remarks>
    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_session.IsLoggedIn)
            {
                // ⚠️ 没登录时**什么都不做，而且一个字都不记**：
                // 刚装好、用户还没点「登录百度网盘」是常态，
                // 而每分钟一条「还没登录」会把真正的错误淹掉。
                // 界面上那一行「已停用 / 未登录」才是说这件事的地方。
                return;
            }

            var settings = _settings;

            if (settings.AutoUpload)
            {
                // 开关开着：**新开始录制的**那些自动进队列。
                await EnqueueEligibleAsync(
                    settings.AutoUploadSince ?? _since, cancellationToken);
            }

            if (settings.CompareAndBackfill)
            {
                await EnqueueEligibleAsync(settings.Floor, cancellationToken);
                await CompareAsync(cancellationToken);
            }

            // 队列里有东西就推一把 —— 哪怕两个开关都关了：
            // 手点「立即对比同步」放进来的那些不该因为开关被关掉就永远卡住。
            await DrainAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.Log(LogLevel.Warn, "网盘", $"定时检查上传队列出错：{ex.Message}");
        }
    }

    /// <summary>把库里的录像按时间下界收进队列（已经有的不动）。</summary>
    /// <returns>这一次新收进去几条。</returns>
    private async Task<int> EnqueueEligibleAsync(
        DateTimeOffset floor, CancellationToken cancellationToken)
    {
        var existing = (await _queue.LoadAsync(cancellationToken))
            .ToDictionary(i => i.EvidenceId, StringComparer.Ordinal);

        var added = 0;

        foreach (var entry in await _index.LoadAllAsync(cancellationToken))
        {
            if (existing.ContainsKey(entry.EvidenceId) || entry.StartedAt < floor)
            {
                continue;
            }

            if (await AddAsync(entry, cancellationToken) is not null)
            {
                added++;
            }
        }

        if (added > 0)
        {
            _logger.Log(LogLevel.Info, "网盘", $"有 {added} 条录像进了上传队列",
                new Dictionary<string, object?> { ["起始不早于"] = floor.ToString("O") });
        }

        return added;
    }

    /// <summary>一条录像进队列（已经在里面就不动）。</summary>
    private async Task<UploadQueueItem?> EnqueueAsync(
        RelativePath location, CancellationToken cancellationToken)
    {
        foreach (var entry in await _index.LoadAllAsync(cancellationToken))
        {
            if (!string.Equals(entry.Location.Value, location.Value, StringComparison.Ordinal))
            {
                continue;
            }

            return await _queue.FindAsync(entry.EvidenceId, cancellationToken)
                ?? await AddAsync(entry, cancellationToken);
        }

        return null;
    }

    /// <summary>真的往队列里加一条。<b>加不了就返回 <see langword="null"/></b>。</summary>
    /// <remarks>
    /// ⚠️ 本机这一份不在了的那些**根本不记进队列**：记进去只会让队列里躺着
    /// 一堆永远传不上去的东西，把真正失败、真正需要用户看的那些淹掉。
    /// </remarks>
    private async Task<UploadQueueItem?> AddAsync(
        RecordingEntry entry, CancellationToken cancellationToken)
    {
        var local = _locations.Resolve(entry.Location.Value);

        if (local is null)
        {
            return null;
        }

        var type = await BusinessTypeOfAsync(entry.EvidenceId, cancellationToken);

        var item = new UploadQueueItem(
            entry.EvidenceId,
            entry.Location.Value,
            _layout.RemotePath(entry.Location, type),
            new FileInfo(local).Length,
            entry.StartedAt,
            CloudUploadState.Pending,
            0,
            null,
            _now());

        await _queue.AppendAsync(item, cancellationToken);

        return item;
    }

    /// <summary>
    /// 与网盘实际内容对一遍：网盘已有的标成「完成」，只补传网盘上没有的。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>按目录分组，一个目录问一次。</b>一条一次请求就是把网盘当数据库用，
    /// 而它有限流 —— 一次「立即对比同步」能把账号问进风控。
    /// </para>
    /// <para>
    /// ⚠️ <b>只有 Pending 的会被标成 Done。</b>正在传的、已经失败的不动：
    /// 对比没有上传能力，它只回答「网盘上有没有」。
    /// </para>
    /// </remarks>
    private async Task CompareAsync(CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await _session.TokenAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is BaiduPanException or HttpRequestException)
        {
            _lastError = $"问不到百度网盘：{ex.Message}";
            _logger.Log(LogLevel.Warn, "网盘", $"对比补传跳过：{ex.Message}");
            return;
        }

        var pending = (await _queue.LoadAsync(cancellationToken))
            .Where(i => i.State == CloudUploadState.Pending)
            .ToList();

        var now = _now();

        foreach (var group in pending.GroupBy(
            i => DirectoryOf(i.RemotePath), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlySet<string> names;
            try
            {
                names = await _api.ListFilesAsync(token, group.Key, cancellationToken);
            }
            catch (BaiduPanException ex) when (ex.Errno == DirectoryMissing)
            {
                // ⚠️ 目录还不存在 = 这个目录下**一个都没有**，不是「查不了」。
                // 把它当成查不了的话，第一次同步（目录还没建）会一条都传不出去。
                names = new HashSet<string>(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is BaiduPanException or HttpRequestException)
            {
                _lastError = $"对比不了网盘上的 {group.Key}：{ex.Message}";
                _logger.Log(LogLevel.Warn, "网盘", $"对比补传跳过 {group.Key}：{ex.Message}");
                continue;
            }

            _lastChecked = now;

            foreach (var item in group)
            {
                if (!names.Contains(BaiduPanLayout.FileNameOf(item.RemotePath)))
                {
                    continue;
                }

                await _queue.AppendAsync(
                    item with
                    {
                        State = CloudUploadState.Done,
                        LastError = null,
                        UpdatedAt = now,
                    },
                    cancellationToken);
            }
        }
    }

    /// <summary>把队列里等着传的那些传上去。返回成功几条。</summary>
    private async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        if (!await _drainGate.WaitAsync(0, cancellationToken))
        {
            // 已经有一轮在跑了。**不排队**：那一轮跑完自然会再看一遍队列，
            // 而排队只会让同一条录像被两轮同时盯上。
            return 0;
        }

        try
        {
            var pending = (await _queue.LoadAsync(cancellationToken))
                .Where(i => i.State == CloudUploadState.Pending)
                .ToList();

            if (pending.Count == 0)
            {
                return 0;
            }

            // 一个格子的数组而不是 `int done`：下面的并发体里要 `Interlocked`，
            // 而捕获进闭包的局部变量取不了 `ref`（CS1628）。
            var done = new int[1];
            var parallelism = Math.Clamp(_settings.ParallelUploads, 1, 8);

            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellationToken,
                },
                async (item, ct) =>
                {
                    var local = _locations.Resolve(item.Location);

                    if (local is null)
                    {
                        await FailAsync(item, "本机这一份不在了，传不了。", countAttempt: false, ct);
                        return;
                    }

                    var outcome = await UploadOneAsync(item, local, ct);

                    if (outcome.Published)
                    {
                        Interlocked.Increment(ref done[0]);
                    }
                });

            return done[0];
        }
        finally
        {
            _drainGate.Release();
        }
    }

    /// <summary>传一条，并把结果写回队列。</summary>
    private async Task<(bool Published, string? Reason)> UploadOneAsync(
        UploadQueueItem item, string localPath, CancellationToken cancellationToken)
    {
        string token;

        try
        {
            token = await _session.TokenAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is BaiduPanException or HttpRequestException)
        {
            var reason = $"还没登上百度网盘：{ex.Message}";
            await FailAsync(item, reason, countAttempt: true, cancellationToken);

            return (false, reason);
        }

        var now = _now();

        await _queue.AppendAsync(
            item with { State = CloudUploadState.Uploading, UpdatedAt = now }, cancellationToken);

        Interlocked.Increment(ref _active);
        try
        {
            await _uploader.UploadAsync(
                token, item.RemotePath, localPath, progress: null, cancellationToken);

            await _queue.AppendAsync(
                item with
                {
                    State = CloudUploadState.Done,
                    Attempts = item.Attempts + 1,
                    LastError = null,
                    UpdatedAt = _now(),
                },
                cancellationToken);

            _lastError = null;

            _logger.Log(LogLevel.Info, "网盘", "已上传到百度网盘",
                new Dictionary<string, object?> { ["remote"] = item.RemotePath });

            return (true, null);
        }
        catch (Exception ex) when (ex is BaiduPanException or HttpRequestException or IOException)
        {
            await FailAsync(item, ex.Message, countAttempt: true, cancellationToken);

            return (false, ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private async Task FailAsync(
        UploadQueueItem item, string reason, bool countAttempt, CancellationToken cancellationToken)
    {
        _lastError = reason;

        await _queue.AppendAsync(
            item with
            {
                State = CloudUploadState.Failed,
                Attempts = countAttempt ? item.Attempts + 1 : item.Attempts,
                LastError = reason,
                UpdatedAt = _now(),
            },
            cancellationToken);

        _logger.Log(LogLevel.Warn, "网盘", $"上传百度网盘失败：{reason}",
            new Dictionary<string, object?> { ["remote"] = item.RemotePath });
    }

    /// <summary>这一条算发货还是退货（取自标签；没打过标签按发货，与手机端同向）。</summary>
    private async Task<BusinessType> BusinessTypeOfAsync(
        string evidenceId, CancellationToken cancellationToken)
    {
        var labels = await _labels.GetForEvidenceAsync(evidenceId, cancellationToken);

        return labels.TryGetValue(LabelKeys.BusinessType, out var raw)
            && BusinessTypes.TryParse(raw, out var type)
                ? type
                : BusinessType.Outbound;
    }

    /// <summary>远端一个目录的路径（从文件名倒推，列表接口要的是目录）。</summary>
    private static string DirectoryOf(string remotePath)
    {
        var slash = remotePath.LastIndexOf('/');

        return slash < 0 ? "/" : remotePath[..(slash + 1)];
    }

    /// <summary>网盘回的「目录不存在」。</summary>
    /// <remarks>
    /// ⚠️ 它**不是错误**：第一次往某个日期目录里传东西时那个目录当然还不存在。
    /// 把它当成「查不了」的话，第一次同步会一条都传不出去（对比那一步全部跳过）。
    /// </remarks>
    private const int DirectoryMissing = -9;

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        _stopping.Dispose();
        _drainGate.Dispose();
    }
}
