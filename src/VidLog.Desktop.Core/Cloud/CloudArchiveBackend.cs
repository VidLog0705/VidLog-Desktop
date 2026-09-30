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
}
