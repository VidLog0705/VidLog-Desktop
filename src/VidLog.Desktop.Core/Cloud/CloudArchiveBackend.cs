using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Cloud;

/// <summary>
/// 归档层 = 百度网盘（规格 §3.4.6 的第四档）。
/// </summary>
/// <remarks>
/// <para>
/// 对上层就是「一个可读写的目录」，与 <see cref="DirectoryArchiveBackend"/> 同一个位置 ——
/// 差别只在路径怎么拼、以及**问不到**的时候怎么说。
/// </para>
/// <para>
/// ⚠️ <b>「查不了」与「不存在」在这里的区分更要紧</b>（I8）。
/// 网盘断网、令牌过期、限流 —— 全都是「查不了」，而它们**一律不许被当成「不存在」**：
/// 那会让清理删掉本机上唯一的那一份。
/// </para>
/// <para>
/// ⚠️ <b>回查要问两个目录</b>（发货一个、退货一个），理由写在
/// <see cref="BaiduPanLayout.DirectoriesFor"/> 上：分类取自可改的标签。
/// </para>
/// </remarks>
public sealed class CloudArchiveBackend : IArchiveBackend, IArchivePublisher
{
    /// <summary>网盘回的「目录不存在」。</summary>
    /// <remarks>
    /// ⚠️ 它**不是「查不了」**：还没往那个日期目录里传过东西时，它当然不存在。
    /// 把它当成查不了的话，装好之后**第一次**回查会一律「查不了」⇒ 一律拒删，
    /// 而那句「查不了」会把用户引向「是不是网盘断线了」这个错误方向。
    /// </remarks>
    private const int DirectoryMissing = -9;

    /// <summary>网盘回的「文件不存在」（公共错误码 063 的 <c>31066</c>）。</summary>
    /// <remarks>
    /// ⚠️ 与 <see cref="DirectoryMissing"/> 同一个待遇：**这是「这一条不在这儿」，
    /// 不是故障**。取直链时路径不存在有的接口回 -9、有的回 31066，
    /// 两个都当「换下一个分类目录看」。
    /// </remarks>
    private const int FileMissing = 31066;

    private readonly BaiduPanLayout _layout;
    private readonly IBaiduPanApi _api;
    private readonly BaiduPanSession _session;
    private readonly CloudUploadService _uploads;
    private readonly IAppLogger _logger;

    public CloudArchiveBackend(
        BaiduPanLayout layout,
        IBaiduPanApi api,
        BaiduPanSession session,
        CloudUploadService uploads,
        IAppLogger? logger = null)
    {
        _layout = layout;
        _api = api;
        _session = session;
        _uploads = uploads;
        _logger = logger ?? NullLogger.Instance;
    }

    public ArchiveBackendKind Kind => ArchiveBackendKind.Cloud;

    /// <summary>网盘上的落点规则（设置页也要它，好把「远端路径」显示给用户看）。</summary>
    public BaiduPanLayout Layout => _layout;

    /// <remarks>
    /// ⚠️ 用**列目录**来判在不在，而不是「我们记着传过」：清理的前置闸（§3.5.4）
    /// 问的是**归档层上现在有没有**，而不是「我们以为传过没有」。
    /// 用户自己上网页把文件删了，记着的那一笔就成了假话。
    /// </remarks>
    public async Task<ArchiveVerifyResult> VerifyAsync(
        RelativePath location, CancellationToken cancellationToken = default)
    {
        string token;
        try
        {
            token = await _session.TokenAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is BaiduPanException or HttpRequestException)
        {
            // 没登录 / 续期失败 —— 这是「查不了」，**不是「不存在」**。
            return new ArchiveVerifyResult(false, $"问不到百度网盘：{ex.Message}");
        }

        var fileName = _layout.FileNameFor(location);

        foreach (var directory in _layout.DirectoriesFor(location))
        {
            try
            {
                var names = await _api.ListFilesAsync(token, directory, cancellationToken);

                if (names.Contains(fileName))
                {
                    return new ArchiveVerifyResult(true, null);
                }
            }
            catch (BaiduPanException ex) when (ex.Errno == DirectoryMissing)
            {
                // 这个目录下什么都没有。看下一个。
                continue;
            }
            catch (Exception ex)
                when (ex is BaiduPanException or HttpRequestException or TaskCanceledException)
            {
                _logger.Log(LogLevel.Warn, "网盘", $"回查网盘出错：{ex.Message}",
                    new Dictionary<string, object?> { ["location"] = location.Value });

                return new ArchiveVerifyResult(false, $"回查百度网盘出错：{ex.Message}");
            }
        }

        // ⚠️ 两个目录都没有，**给 null 原因**（=「不存在」，不是「查不了」）。
        // 两者的区别由调用方说 —— 它才知道自己在做的是一次删除判定（§3.5.6③）。
        return new ArchiveVerifyResult(false, null);
    }

    /// <remarks>
    /// ⚠️ 失败**只影响「这条能不能被清理」**，绝不影响本机那一份（I2）。
    /// 见 <see cref="IArchivePublisher"/> 的说明。
    /// <para>
    /// ⚠️ 实际的「传」在 <see cref="CloudUploadService"/> 里 ——
    /// 收尾这条路与补传那条路共用同一段上传、同一个队列，
    /// 各写一份的话，「重试算几次」「失败记在哪」迟早走岔。
    /// </para>
    /// </remarks>
    public Task<ArchivePublishResult> PublishAsync(
        RelativePath location, string localPath, CancellationToken cancellationToken = default) =>
        _uploads.PublishAsync(location, localPath, cancellationToken);

    /// <remarks>
    /// <para>
    /// 两步：先要一条**下载直链**（<c>method=meta&amp;dlink=1</c>），再把直链的内容流回来。
    /// 两个分类目录**挨个试**（与 <see cref="VerifyAsync"/> 同一个理由：分类那一段
    /// 在老形状下取自可改的标签）—— 试到第一个取到直链的就是它。
    /// </para>
    /// <para>
    /// ⚠️ <c>errno=-9</c>（路径不存在）是**正常状态**：说明这一条不在这一个分类目录下，
    /// 接着看另一个。它**不是**故障，别记成 WARN（`BaiduPanClient.ReadAsync` 已经
    /// 按这一条把 -9 记成 Debug 了）。
    /// </para>
    /// <para>
    /// ⚠️ <b>下载本身走的是「一次 HTTP GET 把整条流搬回来」，中间没有断点续传。</b>
    /// 断了就是这次失败、`.part` 清掉；用户再点一次播放会从头再来。
    /// 已知的代价，先记着：172 MB 那种量级上，一条慢网断一次就要重头。
    /// </para>
    /// </remarks>
    public async Task<ArchiveFetchResult> FetchAsync(
        RelativePath location,
        string destination,
        Action<long, long?>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string token;
        try
        {
            token = await _session.TokenAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is BaiduPanException or HttpRequestException)
        {
            // 没登录 / 续期失败 —— 这是「问不到」，**不是「没有」**。
            return ArchiveFetchResult.Failed($"问不到百度网盘：{ex.Message}");
        }

        var fileName = _layout.FileNameFor(location);

        foreach (var directory in _layout.DirectoriesFor(location))
        {
            string dlink;

            try
            {
                dlink = await _api.GetDlinkAsync(token, directory + fileName, cancellationToken);
            }
            catch (BaiduPanException ex) when (ex.Errno is DirectoryMissing or FileMissing)
            {
                // 这个分类目录下没有这一条。看下一个。
                continue;
            }
            catch (Exception ex)
                when (ex is BaiduPanException or HttpRequestException or TaskCanceledException)
            {
                _logger.Log(LogLevel.Warn, "网盘", $"取回时问网盘出错：{ex.Message}",
                    new Dictionary<string, object?> { ["location"] = location.Value });

                return ArchiveFetchResult.Failed($"从百度网盘取回出错：{ex.Message}");
            }

            try
            {
                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                // ⚠️ 与另外两条路**同一条**：先写 `.part` 再改名。半截的
                // `<单号>.mp4` 落在盘上会被 `File.Exists` 当成「本机有」——
                // 检索页那句警告消失，而播放器打开的是一个坏文件。
                var staging = destination + ".part";

                await using (var file = new FileStream(
                    staging, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await _api.DownloadAsync(token, dlink, file, progress, cancellationToken);
                }

                File.Move(staging, destination, overwrite: false);

                return ArchiveFetchResult.Ok;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or BaiduPanException or HttpRequestException or TaskCanceledException)
            {
                TryCleanStaging(destination);

                return ArchiveFetchResult.Failed($"从百度网盘取回出错：{ex.Message}");
            }
        }

        return ArchiveFetchResult.NotFound;
    }

    /// <summary>把下了一半的 <c>.part</c> 清掉（清不掉也不影响别的：它不算一条录像）。</summary>
    private static void TryCleanStaging(string destination)
    {
        try
        {
            var staging = destination + ".part";

            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留着：它以 `.part` 结尾，检索那边判它「不存在」，不会冒充成品。
        }
    }
}
