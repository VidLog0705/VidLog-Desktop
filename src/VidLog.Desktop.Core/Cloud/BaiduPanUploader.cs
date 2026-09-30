using System.Collections.Concurrent;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Cloud;

/// <summary>一次上传的进度（给队列界面用）。</summary>
/// <param name="Slice">第几片（从 1 数起）。</param>
/// <param name="Slices">一共几片。</param>
/// <param name="Skipped">有几片是网盘说它已经收下了、不用再传的。</param>
public sealed record UploadProgress(int Slice, int Slices, int Skipped)
{
    /// <summary>完成比例（0–1）。一片都没有时为 1。</summary>
    public double Fraction => Slices <= 0 ? 1 : (double)Slice / Slices;
}

/// <summary>
/// 把一个本地文件传上百度网盘。
/// </summary>
/// <remarks>
/// <para>
/// 流程照开放平台文档，四步一步不能少：
/// <c>precreate</c>（报大小与分片摘要，拿 <c>uploadid</c> 与**还要传哪几片**）
/// → <c>locateupload</c>（拿这次能用的上传域名，016 明说「上传文件数据前必须调用本接口」）
/// → 逐片 <c>superfile2</c> → <c>create</c>（合并，这一步之后网盘上才真的出现文件）。
/// </para>
/// <para>
/// ⚠️ <b>预创建回来的那串序号是「还要传的」，不是「已经有了的」。</b>
/// 文档 018 的响应参数表写的原话是「需要上传的分片序号列表，索引从 0 开始」。
/// 反着读的后果不是慢一点，是**整个传不上去**：全新的 3 片文件网盘回的正是
/// <c>[0,1,2]</c>，当成「它都有了」就会一片都不传，接着 <c>create</c>
/// 去合并一份空的分片集，报的是 <c>31190</c>/<c>31363</c>。
/// </para>
/// <para>
/// ⚠️ <b>重试在上一层（队列），不在这一层。</b>这里失败就抛，抛出去的错
/// 由队列决定「换一片再来」还是「整条记为失败」—— 两层各有一份重试策略
/// 就会互相打架，而打架的表现是偶发的重复上传。
/// </para>
/// <para>
/// <b>唯一的例外</b>是「路径不对 ⇒ 先把目录建出来再试一次」：那不是重试策略，
/// 是**修好条件**（文档没写上传会不会自动建父目录，见
/// <see cref="BaiduPanException.IsPathProblem"/>）。它**只发生一次**，
/// 再失败就照实抛给队列，不会在这里变成一层隐形的重试循环。
/// </para>
/// </remarks>
public sealed class BaiduPanUploader
{
    private readonly IBaiduPanApi _api;
    private readonly IAppLogger _logger;

    /// <summary>本次运行里已经建过的远端目录。</summary>
    /// <remarks>
    /// ⚠️ 用 <see cref="ConcurrentDictionary{TKey,TValue}"/> 而不是 <c>HashSet</c>：
    /// 上传是并发跑的（同时 8 条），而 <c>HashSet</c> 并发写会**静默丢项**——
    /// 丢项的表现是多发几次建目录请求，那倒还好；真正糟的是它可能把内部状态写坏，
    /// 之后 <c>Contains</c> 随机返回错的东西。这里要的正是「同一个键只有一个赢家」，
    /// <c>TryAdd</c> 给的恰好就是这个。
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte> _ensured = new(StringComparer.Ordinal);

    public BaiduPanUploader(IBaiduPanApi api, IAppLogger? logger = null)
    {
        _api = api;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>上传一个文件。</summary>
    /// <exception cref="BaiduPanException">网盘拒绝、或者文件根本传不了。</exception>
    public async Task UploadAsync(
        string accessToken,
        string remotePath,
        string localPath,
        IProgress<UploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(localPath);

        if (!info.Exists)
        {
            throw new BaiduPanException(0, $"本机这一份不在了：{localPath}");
        }

        if (info.Length == 0)
        {
            // ⚠️ 空文件传上去在网盘上是一个 0 字节的「录像」，而它在网页回放里
            // 看起来与一条正常的录像一模一样 —— 点开才发现什么都没有。
            // 本仓的产物都要过解码校验，所以走到这里说明上游出了问题，如实报出来。
            throw new BaiduPanException(0, $"这个文件是空的，不传：{Path.GetFileName(localPath)}");
        }

        var slices = BaiduPanBlocks.Count(info.Length);

        if (slices > BaiduPanBlocks.MaxSlicesPerCreate)
        {
            // 1024 片 × 4MB = 4GB = 普通用户单文件的上限（017）。
            // 本仓今天单段远小于它；真撞上说明要么该做分段合并、
            // 要么用户的账号档位不够 —— 两种都**把话说明白**，不硬传一个半截文件。
            throw new BaiduPanException(
                0,
                $"这个文件有 {Size(info.Length)}，超过单次上传的上限 {Size(BaiduPanBlocks.SliceSize * (long)BaiduPanBlocks.MaxSlicesPerCreate)}。");
        }

        var blockList = await BaiduPanBlocks.ComputeAsync(localPath, cancellationToken);

        try
        {
            await UploadOnceAsync(
                accessToken, remotePath, localPath, info.Length, blockList, progress, cancellationToken);
        }
        catch (BaiduPanException ex) when (ex.IsPathProblem)
        {
            // ⚠️ 这一段在补一件**文档没写**的事：`precreate` 会不会顺手把中间的父目录
            // 建出来？018 对 `autoinit` 只有一句「本接口固定为 1」，没说它做什么；
            // 全套文档里也没有任何一处写「上传会自动建目录」。而 020 专门有建文件夹的
            // 接口 —— 暗示得自己建，但同样没有正面写。
            //
            // 所以这里**不猜**：平时一个目录都不建（正常路径上一次额外请求都不发），
            // 只有真回了路径类错误时才按 020 把目录补出来，然后**只重试一次**。
            // 两种可能哪一边是真的，这条路都走得通。
            // ⚠️ 不这样做的话，风险是**整条归档路径传不上去**：远端落点是
            // `/apps/<应用名>/2026/09/30/发货/`，而代码里没有任何一处显式建目录。
            _logger.Log(
                LogLevel.Warn,
                "网盘",
                $"网盘说这个路径有问题（errno {ex.Errno}）。文档没写 precreate 会不会自动建父目录，"
                + $"先按 020 把目录补出来再试一次：{ex.Message}");

            await EnsureParentDirectoriesAsync(accessToken, remotePath, cancellationToken);

            // 再失败就照实抛 —— 那就不是目录的事了，别把它盖成一个看不懂的错。
            await UploadOnceAsync(
                accessToken, remotePath, localPath, info.Length, blockList, progress, cancellationToken);
        }
    }

    /// <summary>
    /// 四步走一遍（预创建 → 问域名 → 逐片 → 合并）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它**会**被重跑一次（见 <see cref="UploadAsync"/> 里那段），所以不准在这里
    /// 攒任何「只做一次」的状态。分片摘要 <paramref name="blockList"/> 由调用方算好传进来 ——
    /// 重跑时再算一遍要把整个文件重读一次（4GB 那个量级），白白多花几分钟。
    /// </remarks>
    private async Task UploadOnceAsync(
        string accessToken,
        string remotePath,
        string localPath,
        long size,
        IReadOnlyList<string> blockList,
        IProgress<UploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var precreate = await _api.PrecreateAsync(
            accessToken, remotePath, size, blockList, cancellationToken);

        // ⚠️ 域名要先问（016：「上传文件数据前必须调用本接口」），而且**一个文件问一次**、
        // 所有分片共用同一个 —— 每个分片问一次的话，一次「立即对比同步」能把账号问进限流。
        var host = await _api.LocateUploadAsync(
            accessToken, remotePath, precreate.UploadId, cancellationToken);

        var slices = blockList.Count;
        var skipped = 0;

        await using (var stream = new FileStream(
            localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        {
            for (var index = 0; index < slices; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 网盘说要传的才传（`Pending`）。不在里面的那些它已经收下了 ——
                // 那是带着同一个 uploadid 重开预创建时的情形，也就是断点续传。
                if (!precreate.Pending.Contains(index))
                {
                    skipped++;
                    progress?.Report(new UploadProgress(index + 1, slices, skipped));
                    continue;
                }

                var offset = (long)index * BaiduPanBlocks.SliceSize;
                var length = (int)Math.Min(BaiduPanBlocks.SliceSize, size - offset);

                stream.Seek(offset, SeekOrigin.Begin);

                var echoed = await _api.UploadSliceAsync(
                    accessToken, host, remotePath, precreate.UploadId, index,
                    new SliceStream(stream, length), cancellationToken);

                // ⚠️ 网盘回显的 MD5 与本机算的那一片对不上 ⇒ **这一片传歪了**。
                // 必须在这里就断，不能等 create：create 只看分片齐不齐、不看内容，
                // 它会成功，于是网盘上出现一个大小对、播出来是坏的文件 ——
                // 而那时本机那一份已经因为「云端有了」被允许清理（I8）。
                //
                // 它没回显 MD5 时（字段缺席）不判定 —— 拿「没给」当「不匹配」会误杀。
                if (echoed is { Length: > 0 }
                    && !string.Equals(
                        echoed, blockList[index], StringComparison.OrdinalIgnoreCase))
                {
                    throw new BaiduPanException(
                        0,
                        $"第 {index + 1} 片传上去之后网盘回的摘要对不上（它回的 {echoed}，本机算的是 {blockList[index]}）。"
                        + "这一片的内容在传输中变了，不能就这么合上去。");
                }

                progress?.Report(new UploadProgress(index + 1, slices, skipped));
            }
        }

        await _api.CreateAsync(
            accessToken, remotePath, size, blockList, precreate.UploadId, cancellationToken);
    }

    /// <summary>
    /// 按 020 把一条远端路径的父目录链补出来（从 <c>/apps/&lt;应用名&gt;/</c> **下面**那一层起）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 逐层建，而不是只建最里面那一层：020 说的是「创建**一个**文件夹」，
    /// 没有任何一句保证它会连带建出中间的层。
    /// ⚠️ 也**不去 create** <c>/apps/&lt;应用名&gt;/</c> 本身：那是授权之后就在的应用目录，
    /// 去建它会被按「无权访问」拒掉（<c>-7</c>），于是兜底还没开始，
    /// 就先把原来那个错换成了一个更看不懂的错。
    /// </remarks>
    private async Task EnsureParentDirectoriesAsync(
        string accessToken, string remotePath, CancellationToken cancellationToken)
    {
        var slash = remotePath.LastIndexOf('/');

        if (slash <= 0)
        {
            return;
        }

        var parent = remotePath[..slash];
        var segments = parent.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var start = segments.Length >= 2 && segments[0] == "apps" ? 2 : 0;
        var path = start == 0 ? string.Empty : "/" + segments[0] + "/" + segments[1];
        var created = 0;

        for (var index = start; index < segments.Length; index++)
        {
            path += "/" + segments[index];

            // 本次运行里这一层已经建过就不再问：一条录像 4 层、一个日期目录下几十条录像，
            // 不去重的话同一条链会被反复建（而未过审的应用每小时只有 10 次调用）。
            if (!_ensured.TryAdd(path, 0))
            {
                continue;
            }

            await _api.CreateDirectoryAsync(accessToken, path, cancellationToken);
            created++;
        }

        if (created > 0)
        {
            _logger.Log(LogLevel.Info, "网盘", $"网盘上没有这个目录，已按 020 建出来 {created} 层",
                new Dictionary<string, object?> { ["dir"] = parent });
        }
    }

    /// <summary>给人看的大小（「12.3MB」）。</summary>
    internal static string Size(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024 * 1024):0.#}GB"
            : $"{bytes / (1024.0 * 1024):0.#}MB";

    /// <summary>
    /// 只让底层看见**这一片**的只读窗口。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不能直接把整个文件流交给 HTTP：那会让它把**整个文件**当成这一片发出去，
    /// 而网盘按 partseq 只收它要的那一段长度 —— 轻则被拒，重则合并出一个
    /// 长度对、内容错的文件（那种坏法在播放器里看不出来）。
    /// </remarks>
    private sealed class SliceStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public SliceStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
            Length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get; }
        public override long Position { get => Length - _remaining; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var wanted = (int)Math.Min(count, _remaining);
            if (wanted <= 0)
            {
                return 0;
            }

            var read = _inner.Read(buffer, offset, wanted);
            _remaining -= read;

            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var wanted = (int)Math.Min(buffer.Length, _remaining);
            if (wanted <= 0)
            {
                return 0;
            }

            var read = await _inner.ReadAsync(buffer[..wanted], cancellationToken);
            _remaining -= read;

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
