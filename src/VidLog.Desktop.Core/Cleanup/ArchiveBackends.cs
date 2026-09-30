using System.Text.Json.Serialization;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Cleanup;

/// <summary>
/// 归档层在哪、怎么连（规格 §3.4.6）。
/// </summary>
/// <remarks>
/// <para>
/// 规格原话（2026-09-24 需求变更）：「NAS 和百度网盘同时做，加一项，
/// 用户可挂载网络驱动器存储盘。」四种后端：**电脑端本地 / NAS / 百度网盘 /
/// 挂载网络驱动器**，对上层表现一致。
/// </para>
/// <para>
/// ⚠️ <b>实现上是两档</b>（规格点名的）：「目录型」= NAS + 挂载盘，
/// <b>一份代码</b>；「网盘型」= 百度网盘，走它的接口。两者对上层都是
/// 「一个可读写的目录」，差别只在要不要输凭据 —— 为它们各写一套就是三份要维护的重复。
/// </para>
/// <para>
/// ⚠️ <b>挂载盘那个路径绝不能进索引。</b> 它（<c>Z:\</c> 或 <c>\\nas\vidlog</c>）
/// 只当**配置里的根**：<see cref="RelativePath"/> 拒绝绝对路径与 UNC，
/// 索引里存的仍是相对路径（规格 §6.2）。换一台机器挂到别的盘符时，
/// 索引照样读得出来。
/// </para>
/// </remarks>
public sealed record ArchiveTarget(ArchiveBackendKind Kind, string? DirectoryPath = null)
{
    /// <summary>出厂默认：归档层就是本机磁盘（规格 §3.5.1 的保守那一头）。</summary>
    public static ArchiveTarget Default { get; } = new(ArchiveBackendKind.LocalDisk);

    /// <summary>目录型（NAS + 挂载网络驱动器）—— 共用一份实现。</summary>
    [JsonIgnore]
    public bool IsDirectoryType =>
        Kind is ArchiveBackendKind.Nas or ArchiveBackendKind.MountedDrive;

    /// <summary>
    /// 归档层是不是**就在这台电脑上**（规格 §3.5.1 的判据）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 判据是「归档层是不是就在这台电脑上」，**不是**「这个路径看着像不像本地盘」——
    /// NAS 与挂载盘**都算「在别处」**（哪怕挂载成 <c>Z:\</c> 看起来就是个本地盘），
    /// 所以它们**允许开清理**；只有「电脑端本地」这一种不给。
    /// </remarks>
    [JsonIgnore]
    public bool IsOnThisMachine => Kind == ArchiveBackendKind.LocalDisk;

    /// <summary>允不允许开本地清理（规格 §3.5.1）。</summary>
    [JsonIgnore]
    public bool AllowsCleanup => !IsOnThisMachine;

    /// <summary>用户在界面上看到的名字。</summary>
    [JsonIgnore]
    public string Label => Kind switch
    {
        ArchiveBackendKind.Nas => "NAS",
        ArchiveBackendKind.MountedDrive => "挂载网络驱动器",
        ArchiveBackendKind.Cloud => "百度网盘",
        _ => "本机磁盘",
    };

    /// <summary>
    /// 这一档能不能跨网（规格 §2.3 要求**如实告知**）。
    /// </summary>
    /// <remarks>
    /// 规格原话：「百度网盘能跨网、NAS 看用户自己的网络配置、**本机与挂载盘只能内网**」。
    /// 产品**不得承诺做不到的事** —— 所以这句话要出现在界面上。
    /// </remarks>
    [JsonIgnore]
    public string Reachability => Kind switch
    {
        ArchiveBackendKind.Cloud => "能跨网：换了网络也能取回。",
        ArchiveBackendKind.Nas => "能不能跨网取决于你自己的网络怎么配的。",
        _ => "只能内网：不在同一个网络里就取不回。",
    };

    /// <summary>
    /// 目录型这一档的路径有没有配好。
    /// </summary>
    /// <remarks>
    /// 返回 <see langword="null"/> 表示没问题。
    /// 不在这里 <c>Directory.Exists</c> —— 那是 I/O，而配置校验是纯函数
    /// （设置页每敲一个字符都会问一次）。目录在不在由<b>归档层自己</b>在回查时说，
    /// 那才是它该说的地方（查不了 ≠ 不存在）。
    /// </remarks>
    [JsonIgnore]
    public string? ConfigurationProblem => Kind switch
    {
        ArchiveBackendKind.Nas or ArchiveBackendKind.MountedDrive
            when string.IsNullOrWhiteSpace(DirectoryPath) =>
            "还没填归档目录 —— 这一档要一个可读写的目录（如 \\\\nas\\vidlog 或 Z:\\）。",
        ArchiveBackendKind.Nas or ArchiveBackendKind.MountedDrive
            when !Path.IsPathFullyQualified(DirectoryPath!) =>
            $"归档目录要写完整路径（如 \\\\nas\\vidlog 或 Z:\\vidlog），现在这个是相对的：{DirectoryPath}",
        ArchiveBackendKind.Cloud => "百度网盘这一档要授权（应用凭据走环境变量，不写进配置文件）。",
        _ => null,
    };

    /// <summary>配好了没有。</summary>
    [JsonIgnore]
    public bool IsConfigured => ConfigurationProblem is null;

    /// <summary>
    /// 认不出的档位一律回落到**本机磁盘**。
    /// </summary>
    /// <remarks>
    /// 回落到本机是刻意的：它是不给清理选项的那一档（规格 §3.5.1）——
    /// 解析失败时朝**少删**的那头落，与 <c>RetentionSetting.fromConfig</c>
    /// 解析失败回落「全部保留」是同一条规矩。
    /// </remarks>
    public static ArchiveTarget FromConfig(ArchiveBackendKind kind, string? directoryPath) =>
        Enum.IsDefined(kind)
            ? new ArchiveTarget(kind, string.IsNullOrWhiteSpace(directoryPath) ? null : directoryPath.Trim())
            : Default;
}

/// <summary>一次「把本机这一份发布到归档层」的结果。</summary>
/// <param name="Published">归档层上现在有没有这一份。</param>
/// <param name="FailureReason">没成的原因（给人看的）。</param>
public sealed record ArchivePublishResult(bool Published, string? FailureReason)
{
    public static ArchivePublishResult Ok { get; } = new(true, null);

    public static ArchivePublishResult Failed(string reason) => new(false, reason);
}

/// <summary>
/// 归档层上的**写入**那一半。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="IArchiveBackend"/>（回查那一半）分开：回查是**清理的前置 gates**，
/// 它在没有归档层写入能力的年代就有用了；写入是「本机这一份还要在别处有一份」。
/// </para>
/// <para>
/// ⚠️ <b>发布失败不得让本机那一份消失，也不得让收尾失败。</b>
/// 本机这一份已经在索引里、能播能检索 —— 它是这个系统的第一份。
/// 归档层那一份没成，代价是「这条还不能被清理」（回查会不通过 ⇒ 拒删），
/// 而不是「这条录像没了」。这正是 I2 的方向。
/// </para>
/// </remarks>
public interface IArchivePublisher
{
    Task<ArchivePublishResult> PublishAsync(
        RelativePath location, string localPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// 目录型归档层：**NAS 与挂载网络驱动器共用这一份实现**（规格 §3.4.6）。
/// </summary>
/// <remarks>
/// <para>
/// 它对上层就是「一个可读写的目录」。NAS 与挂载盘的区别只在**要不要输凭据** ——
/// 而凭据那一层由用户自己在操作系统里解决（规格原话：「网络那一层由用户自己在系统里解决，
/// 我们只当成一个目录用」），所以这里一行凭据代码都没有。
/// </para>
/// <para>
/// ⚠️ **归档层就是本机磁盘时也走这一份**（<see cref="ArchiveBackendKind.LocalDisk"/>）：
/// 那时「发布」是空操作（本机这一份**就是**归档层那一份），回查就是看文件在不在。
/// 一条 <c>if (Kind == LocalDisk) return;</c> 换掉一整个类，值。
/// </para>
/// </remarks>
public sealed class DirectoryArchiveBackend : IArchiveBackend, IArchivePublisher
{
    private readonly string[] _roots;

    /// <param name="root">归档层的根目录（本机时是 <c>&lt;root&gt;\archive</c>）。</param>
    /// <param name="kind">它算哪一种后端 —— 决定「能不能开清理」。</param>
    public DirectoryArchiveBackend(string root, ArchiveBackendKind kind)
        : this([root], kind)
    {
    }

    /// <param name="roots">
    /// 归档层的根，**有序**（设计图 `_43` 的「录像备份位置」表：一个 NAS 满了
    /// 就换下一个）。第一个是首选。
    /// </param>
    /// <param name="kind">它算哪一种后端 —— 决定「能不能开清理」。</param>
    public DirectoryArchiveBackend(IReadOnlyList<string> roots, ArchiveBackendKind kind)
    {
        ArgumentNullException.ThrowIfNull(roots);

        // ⚠️ 空列表落回一个占位路径而不是抛：这个对象在「归档层配了一半」
        // 的时候也会被建出来（设置页每敲一个字符都会重算一次），
        // 而那时**报错的话界面还没画出来就先崩了**。
        // 真的没配好由 `ArchiveTarget.ConfigurationProblem` 说，那是它该说的地方。
        _roots = roots.Count > 0 ? [.. roots] : [string.Empty];
        Kind = kind;
    }

    public ArchiveBackendKind Kind { get; }

    /// <summary>归档层的根。**不进索引**，只在拼绝对路径时用。</summary>
    public string Root => _roots[0];

    /// <summary>归档层所有的根，有序。<b>回查要挨个试</b>。</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>归档层就是本机那一份（发布是空操作）。</summary>
    private bool IsSelf => Kind == ArchiveBackendKind.LocalDisk;

    /// <remarks>
    /// ⚠️ <b>在**任一**根上找到就算有</b>，这是这个类支持多根之后最要紧的一句话。
    /// 只看第一个的话，用户换过一次 NAS 之后，老录像的回查会一路说「找不到」——
    /// 而裁判断它「找不到 ⇒ 不删」（I8），于是清理**永远清不掉任何东西**，
    /// 磁盘一直满着，用户完全不知道为什么。
    /// </remarks>
    public async Task<ArchiveVerifyResult> VerifyAsync(
        RelativePath location, CancellationToken cancellationToken = default)
    {
        try
        {
            var reachable = 0;

            foreach (var root in _roots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                reachable++;

                if (File.Exists(Path.Combine(root, location.Value)))
                {
                    return await Task.FromResult(new ArchiveVerifyResult(true, null));
                }
            }

            if (reachable == 0)
            {
                // 一个根都访问不了：这**不是**「这一条不存在」，是「查不了」——
                // 两者都导致不删，但说给用户的话不一样（I8）。
                return new ArchiveVerifyResult(false, $"归档目录访问不了：{string.Join("、", _roots)}");
            }

            // ⚠️ 「找不到」要给 **null** 原因，不能给一句话。
            // `ArchiveVerifyResult.CouldNotVerify` 的判据就是「原因非空」——
            // 那里面的「查不了」与「不存在」是两件事（前者可能是网络断了），
            // 而**两者都导致不删**，但说给用户的话不一样。
            // 所以「找不到这一份了，它现在是唯一副本」那句话由**调用方**说
            // （它才知道自己在做的是一次删除判定，见 §3.5.6③）。
            return await Task.FromResult(new ArchiveVerifyResult(false, null));
        }
        catch (Exception ex)
        {
            // 网络断了、盘符掉了、没有权限 —— 一律「查不了」，**绝不当成不存在**。
            return new ArchiveVerifyResult(false, $"回查归档层出错：{ex.Message}");
        }
    }

    /// <remarks>
    /// ⚠️ <b>按列表顺序挨个试，直到有一个写成功</b>（图上的「NAS 满时自动切换到下一个」）。
    /// 只试第一个的话，一个满了的 NAS 会让**所有**后续录像都发不出去，
    /// 而那时本机那一份就再也不能被清理 —— 磁盘只会越来越满。
    /// </remarks>
    public async Task<ArchivePublishResult> PublishAsync(
        RelativePath location, string localPath, CancellationToken cancellationToken = default)
    {
        // 归档层就是本机这一份：没什么可发的（本机那一份已经在 <root>\archive 里）。
        if (IsSelf)
        {
            return ArchivePublishResult.Ok;
        }

        if (!File.Exists(localPath))
        {
            return ArchivePublishResult.Failed($"本机这一份不在了：{localPath}");
        }

        var failures = new List<string>();

        foreach (var root in _roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var destination = Path.Combine(root, location.Value);

            try
            {
                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                if (File.Exists(destination))
                {
                    // 已经有一份了。**名字就是内容的身份**（归档路径由单号 + 会话 +
                    // 序号 + 时间推出来），所以同名 = 同一条录像，不必重发。
                    return ArchivePublishResult.Ok;
                }

                // 先写临时名再改名：发到一半断网/掉盘时，归档层上留下的是一个
                // **半截文件**而不是一个看着完好的坏文件。半截文件的扩展名不是 .mp4，
                // 回查时那一份仍算「不存在」，下次会重发。
                var staging = destination + ".part";
                await CopyAsync(localPath, staging, cancellationToken);
                File.Move(staging, destination, overwrite: false);

                return ArchivePublishResult.Ok;
            }
            catch (Exception ex)
            {
                // 这一档写不进去（满了 / 掉线了 / 没权限）⇒ **试下一个**。
                failures.Add($"{root}：{ex.Message}");

                // 半截文件留在那儿的后果是「下次回查以为有」—— 得清掉。
                TryCleanStaging(destination);
            }
        }

        return ArchivePublishResult.Failed(
            failures.Count == 0
                ? $"{Label}没有配置可写的目录。"
                : $"{Label}上都没写成功 —— {string.Join("；", failures)}");
    }

    /// <summary>把写了一半的 <c>.part</c> 清掉。清不掉也不影响别的（它不算一条录像）。</summary>
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
            // 删不掉就留着：它以 `.part` 结尾，回查判它「不存在」，不会冒充一条录像。
        }
    }

    private string Label => Kind switch
    {
        ArchiveBackendKind.Nas => "NAS",
        ArchiveBackendKind.MountedDrive => "挂载盘",
        _ => "归档层",
    };

    private static async Task CopyAsync(string from, string to, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            from, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var target = new FileStream(
            to, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        await source.CopyToAsync(target, cancellationToken);
        await target.FlushAsync(cancellationToken);
    }
}

/// <summary>
/// 把「本机这一份」发布到归档层，并记住最近一次失败。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：发布点是**两处**（收尾那一条路、以及接收远端上传那一条路），
/// 而两处都要「失败了要看得见、但绝不因此让本机那一份消失」。
/// 这段逻辑写两遍就会走岔。
/// </para>
/// <para>
/// ⚠️ 归档层就是本机时**根本不建这个对象**（见 <c>DesktopServices</c>）——
/// 那时发布是空操作，建它只会让「有没有发布过」这个问题多一个没意义的答案。
/// </para>
/// </remarks>
public sealed class ArchiveRelay
{
    private readonly IArchivePublisher _publisher;
    private readonly IAppLogger _logger;

    public ArchiveRelay(IArchivePublisher publisher, string label, IAppLogger? logger = null)
    {
        _publisher = publisher;
        Label = label;
        _logger = logger ?? NullLogger.Instance;
    }

    public string Label { get; }

    /// <summary>最近一次发布失败的原因；没失败过就是 <see langword="null"/>。</summary>
    /// <remarks>
    /// 界面要显示它 —— 「归档层那一份没发上去」是**必须让用户知道**的事：
    /// 它意味着本地这一份还不能被清理（回查会拒），用户如果以为已经双份了，
    /// 就会手动删掉唯一的那一份。
    /// </remarks>
    public string? LastFailure { get; private set; }

    /// <summary>最近一次成功发布是哪一条。</summary>
    public string? LastPublished { get; private set; }

    public async Task<ArchivePublishResult> PublishAsync(
        RelativePath location, string localPath, CancellationToken cancellationToken = default)
    {
        var result = await _publisher.PublishAsync(location, localPath, cancellationToken);

        if (result.Published)
        {
            LastFailure = null;
            LastPublished = location.Value;
            return result;
        }

        LastFailure = result.FailureReason ?? "归档层那一份没发上去（没给出原因）";

        // 留痕：这个失败**不影响**本机那一份与索引，但要能回答
        // 「这条为什么还没上归档层」。
        _logger.Log(LogLevel.Warn, "归档", $"发布到{Label}失败：{LastFailure}",
            new Dictionary<string, object?> { ["location"] = location.Value });

        return result;
    }
}
