namespace VidLog.Desktop.Core.Cloud;

/// <summary>一次上传的进度（给队列界面用）。</summary>
/// <param name="Slice">第几片（从 1 数起）。</param>
/// <param name="Slices">一共几片。</param>
/// <param name="Skipped">有几片是网盘已经有了、没传的。</param>
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
/// 流程照开放平台文档：<c>precreate</c>（报大小与分片摘要，拿 uploadid 与「已有分片」）
/// → 逐片 <c>superfile2</c> → <c>create</c>（合并，这一步之后网盘上才真的出现文件）。
/// </para>
/// <para>
/// ⚠️ <b>必须跳过网盘说它已经有了的那些分片。</b>那是「秒传」的来源，
/// 也是断点重传的省力点 —— 上一次传到一半断了，这一次从头再传一遍
/// 会让一条 4GB 的录像重传三次就是 12GB 上行，而用户看到的只是「怎么这么慢」。
/// </para>
/// <para>
/// ⚠️ <b>重试在上一层（队列），不在这一层。</b>这里失败就抛，抛出去的错
/// 由队列决定「换一片再来」还是「整条记为失败」—— 两层各有一份重试策略
/// 就会互相打架，而打架的表现是偶发的重复上传。
/// </para>
/// </remarks>
public sealed class BaiduPanUploader
{
    private readonly IBaiduPanApi _api;

    public BaiduPanUploader(IBaiduPanApi api) => _api = api;

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
            // 512 片 × 4MB = 2GB。本仓今天的单段远小于它，真撞上说明该做分段合并了，
            // 那时**把话说明白**比硬传一个半截文件强。
            throw new BaiduPanException(
                0,
                $"这个文件有 {Size(info.Length)}，超过单次上传的上限 {Size(BaiduPanBlocks.SliceSize * (long)BaiduPanBlocks.MaxSlicesPerCreate)}。");
        }

        var blockList = await BaiduPanBlocks.ComputeAsync(localPath, cancellationToken);
        var precreate = await _api.PrecreateAsync(
            accessToken, remotePath, info.Length, blockList, cancellationToken);

        var skipped = 0;

        await using (var stream = new FileStream(
            localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        {
            for (var index = 0; index < slices; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (precreate.AlreadyThere.Contains(index))
                {
                    skipped++;
                    progress?.Report(new UploadProgress(index + 1, slices, skipped));
                    continue;
                }

                var offset = (long)index * BaiduPanBlocks.SliceSize;
                var length = (int)Math.Min(BaiduPanBlocks.SliceSize, info.Length - offset);

                stream.Seek(offset, SeekOrigin.Begin);

                await _api.UploadSliceAsync(
                    accessToken, remotePath, precreate.UploadId, index,
                    new SliceStream(stream, length), cancellationToken);

                progress?.Report(new UploadProgress(index + 1, slices, skipped));
            }
        }

        await _api.CreateAsync(
            accessToken, remotePath, info.Length, blockList, precreate.UploadId, cancellationToken);
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
