using System.Collections.Concurrent;
using System.Diagnostics;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Cleanup;

/// <summary>一条录像的取回状态。</summary>
public enum RetrievalState
{
    /// <summary>没取过（也没在取）。界面上什么都不显示。</summary>
    None,

    /// <summary>正在从归档层往本机搬。</summary>
    Downloading,

    /// <summary>取回来了，本机现在有这一条。</summary>
    Done,

    /// <summary>归档层上**没有**这一条（不是「取不回来」）。</summary>
    Missing,

    /// <summary>取的过程中出错了（断网、限流、写本地失败…）。</summary>
    Failed,
}

/// <summary>取回进度的一份快照。</summary>
/// <param name="State">到哪一步了。</param>
/// <param name="Percent">
/// 0–100。<b>可能是 <see langword="null"/></b> —— 网盘没给总长度时**不知道**还要多久，
/// 那时界面该只写「下载中」，不要编一个百分比出来（也别当成 0%）。
/// </param>
/// <param name="Message">出错时的一句人话（<see cref="RetrievalState.Failed"/> 才有）。</param>
public readonly record struct RetrievalStatus(RetrievalState State, double? Percent, string? Message)
{
    public static RetrievalStatus None { get; } = new(RetrievalState.None, null, null);
}

/// <summary>
/// 「本机没有 → 从归档层取回来」。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>它只做一件事：把一条录像从归档层搬回本机那份该在的地方</b>
/// （<c>StorageLocations.ResolveOrActive(Location)</c>）。搬完这条就与
/// 「本来就是在本机录的」**一模一样**：检索页那句「成品不在盘上」消失、
/// 能播、能导出、能回查、能清理 —— 下游一处都不用改。
/// </para>
/// <para>
/// ⚠️ <b>状态由界面**轮询**，不是事件推送。</b>与「百度网盘上传」那一页
/// （`CloudUploadService.ProgressOf`）同一套形状：进度存在服务侧、按
/// <c>EvidenceId</c> 索引，窗口用一个定时器去读。理由是结果列表**每一次检索都整个重建**
/// （`ItemsSource` 换掉），把事件接到行对象上的话，重建之后那些接线全成了野引用。
/// </para>
/// <para>
/// ⚠️ <b>同一条不会取两遍</b>：正在取的那一条再点一次只是不做事
/// （不排队、不重开一个 HTTP）。
/// </para>
/// <para>
/// ⚠️ <see cref="_jobs"/> **只增不减**（`ponytail:` 一次进程里取回的是少数几条，
/// 每条一个几十字节的小对象，不值得为它写一轮回收）。真要在这个进程里取回成千上万条
/// 时再按「做完的那条留够 N 分钟」收。
/// </para>
/// </remarks>
public sealed class ArchiveRetrievalService
{
    private readonly IArchiveBackend _archive;
    private readonly StorageLocations _storage;
    private readonly IAppLogger _logger;

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);

    public ArchiveRetrievalService(
        IArchiveBackend archive, StorageLocations storage, IAppLogger? logger = null)
    {
        _archive = archive;
        _storage = storage;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>这一条现在什么状态。没取过就是 <see cref="RetrievalState.None"/>。</summary>
    public RetrievalStatus StatusOf(string evidenceId) =>
        _jobs.TryGetValue(evidenceId, out var job) ? job.Snapshot() : RetrievalStatus.None;

    /// <summary>
    /// 开始取这一条（已经取回来了、或者正在取，就什么都不做）。
    /// </summary>
    /// <returns>本机那个成品的路径 —— 取回来之后播放器要指向它。</returns>
    public string Start(RecordingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var destination = _storage.ResolveOrActive(entry.Location.Value);

        // ⚠️ 先看盘上有没有：本机那一份可能刚刚被人放回来（另开一个窗口、
        // 或者上一次取回其实成了只是界面没刷）。已经有了就别再下一遍。
        if (File.Exists(destination))
        {
            _jobs[entry.EvidenceId] = Job.Finished(RetrievalState.Done);

            return destination;
        }

        var job = new Job();

        // ⚠️ 用 `GetOrAdd` 之后**只在自己是新来的那一个**上开工：两条同时点的
        // 请求里后到的那个拿到的是别人已经放进去的 job，它不该再起一趟下载。
        var existing = _jobs.GetOrAdd(entry.EvidenceId, job);

        if (!ReferenceEquals(existing, job))
        {
            return destination;
        }

        _ = Task.Run(() => RunAsync(entry, destination, job));

        return destination;
    }

    private async Task RunAsync(RecordingEntry entry, string destination, Job job)
    {
        var started = Stopwatch.GetTimestamp();

        _logger.Log(
            LogLevel.Info, "归档", $"开始从归档层取回：{entry.Location.Value}",
            new Dictionary<string, object?>
            {
                ["evidenceId"] = entry.EvidenceId,
                ["归档层"] = _archive.Kind.ToString(),
            });

        try
        {
            var result = await _archive.FetchAsync(
                entry.Location,
                destination,
                (received, total) => job.Report(
                    total is { } size and > 0
                        ? Math.Min(100, received * 100.0 / size)
                        : null));

            if (result.Fetched)
            {
                job.Finish(RetrievalState.Done, null);
            }
            else if (result.Missing)
            {
                job.Finish(RetrievalState.Missing, null);
            }
            else
            {
                job.Finish(RetrievalState.Failed, result.FailureReason ?? "取不回来");
            }

            LogFinish(entry, result, started);
        }
        catch (Exception ex)
        {
            // ⚠️ 这一趟是 `Task.Run` 里跑的火苗，**抛出去没人接**（会变成
            // 进程级的未观察异常）。取回失败必须落到状态上，让界面说得出话。
            job.Finish(RetrievalState.Failed, ex.Message);

            _logger.Log(
                LogLevel.Warn, "归档", $"从归档层取回失败：{ex.Message}",
                new Dictionary<string, object?> { ["evidenceId"] = entry.EvidenceId });
        }
    }

    private void LogFinish(RecordingEntry entry, ArchiveFetchResult result, long started)
    {
        // 三种结局都留痕、都带耗时与字节：用户报「取不回来」时，诊断包里要能看出
        // 是「网盘上没有」还是「下到一半断了」（§6.1：外部依赖调用记耗时）。
        var level = result.Fetched ? LogLevel.Info : LogLevel.Warn;

        _logger.Log(
            level, "归档", result.Fetched
                ? "从归档层取回完成"
                : result.Missing ? "归档层上没有这一条" : "从归档层取回失败",
            new Dictionary<string, object?>
            {
                ["evidenceId"] = entry.EvidenceId,
                ["耗时ms"] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ["原因"] = result.FailureReason,
            });
    }

    /// <summary>
    /// 一条取回任务。
    /// </summary>
    /// <remarks>
    /// ⚠️ 下载线程写、界面线程读，所以字段全在锁里改。
    /// 挣锁的次数就是「每搬 80 KB 一次 + 每 250 ms 每行读一次」，可以忽略。
    /// </remarks>
    private sealed class Job
    {
        private readonly Lock _gate = new();

        private RetrievalState _state = RetrievalState.Downloading;
        private double? _percent;
        private string? _message;

        public static Job Finished(RetrievalState state)
        {
            var job = new Job();

            job.Finish(state, null);

            return job;
        }

        /// <param name="percent"><see langword="null"/> = 还不知道总共多大。</param>
        public void Report(double? percent)
        {
            lock (_gate)
            {
                _percent = percent;
            }
        }

        public void Finish(RetrievalState state, string? message)
        {
            lock (_gate)
            {
                _state = state;
                _message = message;
                _percent = state == RetrievalState.Done ? 100 : _percent;
            }
        }

        public RetrievalStatus Snapshot()
        {
            lock (_gate)
            {
                return new RetrievalStatus(_state, _percent, _message);
            }
        }
    }
}
