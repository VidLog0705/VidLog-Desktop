using System.Text.Json.Serialization;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Cleanup;

/// <summary>
/// 归档层在哪、怎么连（规格 §3.4.6）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>它现在是**推算出来的**，不是用户选出来的</b>（2026-10-13 需求方裁定）。
/// 在此之前界面上有一个「归档层」下拉把后端拆成四项让用户**声明**，而声明与路径
/// 之间一个字都不校验 —— 声明成 NAS、路径填本机的目录，程序照认，清理入口照开，
/// 于是「两份都在本机」被当成「别处还有一份」。
/// </para>
/// <para>
/// 现在的规则（需求方的原话）：**存到电脑本地就自动上传百度网盘；存到网络位置
/// （NAS / 映射盘）就不上传**。翻译成 <see cref="FromConfig"/> 就是三句话。
/// </para>
/// <para>
/// ⚠️ <b>实现上只有两档</b>：「目录型」= 备份位置表里的路径，<b>一份代码</b>；
/// 「网盘型」= 百度网盘，走它的接口。两者对上层都是「一个可读写的目录」，
/// 差别只在要不要输凭据 —— 为它们各写一套就是三份要维护的重复。
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

    /// <summary>目录型（备份位置表里的那些路径）—— 走「一个可读写的目录」那一份实现。</summary>
    [JsonIgnore]
    public bool IsDirectoryType => Kind == ArchiveBackendKind.Directory;

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
        ArchiveBackendKind.Directory => "网络位置",
        ArchiveBackendKind.Cloud => "百度网盘",
        _ => "本机磁盘",
    };

    /// <summary>
    /// 这一档能不能跨网（规格 §2.3 要求**如实告知**）。
    /// </summary>
    /// <remarks>
    /// 规格原话：「百度网盘能跨网、NAS 看用户自己的网络配置、**本机与挂载盘只能内网**」。
    /// 产品**不得承诺做不到的事** —— 所以这句话要出现在界面上。
    /// <para>
    /// ⚠️ NAS 与挂载盘合并成「网络位置」之后，那一句「取决于你自己怎么配」就**没法只按类
    /// 型说**了：同一档里可能是一个只在内网通的 NAS，也可能是一个配了 VPN 的映射盘。
    /// 所以它改说**两档都成立**的那半句。
    /// </para>
    /// </remarks>
    [JsonIgnore]
    public string Reachability => Kind switch
    {
        ArchiveBackendKind.Cloud => "能跨网：换了网络也能取回。",
        ArchiveBackendKind.Directory => "必须和你在同一个网络里才取得到 —— 能不能跨网取决于你自己的网络怎么配。",
        _ => "只有本机这一份：这台电脑坏了就没了。",
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
        ArchiveBackendKind.Directory when string.IsNullOrWhiteSpace(DirectoryPath) =>
            "还没填网络位置 —— 这一档要一个可读写的目录（如 \\\\nas\\vidlog 或 Z:\\）。",
        ArchiveBackendKind.Directory when !Path.IsPathFullyQualified(DirectoryPath!) =>
            $"网络位置要写完整路径（如 \\\\nas\\vidlog 或 Z:\\vidlog），现在这个是相对的：{DirectoryPath}",
        // ⚠️ 2026-10-15 删掉了原来「百度网盘」那一支（需求方当场问「为什么还显示这个？」）。
        // 它是一句**写死的话**，什么都没查：只要推算出来是网盘那一档就断言「没配好」——
        // 而网盘照样传得好好的（凭据在环境变量里、登录也在），于是主窗「本机状态」的
        // 归档层那一行和「需要注意」里同时挂一条**假的**「没配好」，把真的那一条淹掉。
        // 真话说在别处，而且是量出来的：凭据没有 ⇒ `BaiduPanCredentials.MissingMessage`；
        // 有凭据但没登录 ⇒ `DesktopServices.StartAsync` 里那句（登录状态那一刻才知道）。
        _ => null,
    };

    /// <summary>配好了没有。</summary>
    [JsonIgnore]
    public bool IsConfigured => ConfigurationProblem is null;

    /// <summary>
    /// 归档层在哪儿 —— **推算出来的**（2026-10-13 需求方裁定）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规则（需求方原话）：「电脑端录制视频或者手机端录制视频备份存储到电脑本地，
    /// 会在保存到电脑本地的同时自动上传到百度网盘；如果是网络硬盘或者NAS，
    /// 则不需要上传到百度网盘。」翻译成三句话：
    /// </para>
    /// <list type="number">
    /// <item><b>备份位置表里有路径</b> ⇒ 第二份就在那个网络位置，网盘那一档不参与。
    /// 这与界面上那张表本来就是「第二份落在哪儿」是同一件事。</item>
    /// <item><b>备份位置表空着</b> ⇒ 第二份是百度网盘 —— 但只有那一页的
    /// 【启用自动上传】开着才算数；关着的时候 `CloudUploadService` 一条都不会传
    /// （它把「开关没开」如实回报成**失败**，见那边的说明）。</item>
    /// <item><b>两样都没有</b> ⇒ 没有第二份，回落 <see cref="Default"/>（本机、唯一副本）
    /// ⇒ 不给清理入口。</item>
    /// </list>
    /// <para>
    /// ⚠️ <b>落不到任何一档时朝「少删」的那头落</b>：与
    /// <c>RetentionSetting.fromConfig</c> 解析失败回落「全部保留」是同一条规矩。
    /// </para>
    /// <para>
    /// ⚠️ <b>老配置里的 <paramref name="legacyKind"/> 仍然要认</b>：它记着
    /// 2026-10-13 之前用户手选的那一档，而那时**还没有**「自动上传」这个开关。
    /// 只认 <see cref="ArchiveBackendKind.Cloud"/> 一个值 —— 老的本机 / NAS / 挂载盘
    /// 一律由上面那张表和开关重新算出来，不需要翻译：
    /// 备份位置表非空就是目录型，表空且开关关着就是本机。
    /// </para>
    /// </remarks>
    /// <param name="legacyKind">设置文件里那个老字段（只剩兼容读懂这一个作用）。</param>
    /// <param name="directoryPath">备份位置表的第一条路径（没有就是空）。</param>
    /// <param name="cloudAutoUpload">百度网盘那一页的【启用自动上传】开着没有。</param>
    public static ArchiveTarget FromConfig(
        ArchiveBackendKind legacyKind, string? directoryPath, bool cloudAutoUpload)
    {
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            return new ArchiveTarget(ArchiveBackendKind.Directory, directoryPath.Trim());
        }

        if (cloudAutoUpload || legacyKind == ArchiveBackendKind.Cloud)
        {
            return new ArchiveTarget(ArchiveBackendKind.Cloud);
        }

        return Default;
    }
}

/// <summary>一次「把本机这一份发布到归档层」的结果。</summary>
/// <param name="Published">归档层上现在有没有这一份。</param>
/// <param name="FailureReason">没成的原因（给人看的）。</param>
/// <param name="Kind">
/// 没成的**种类** —— 决定这笔欠账还要不要再试（<see cref="ArchiveFailureKind"/>）。
/// 不传就是「普通的没发上去」（照旧会重试）。
/// </param>
public sealed record ArchivePublishResult(
    bool Published,
    string? FailureReason,
    ArchiveFailureKind Kind = ArchiveFailureKind.Ordinary)
{
    public static ArchivePublishResult Ok { get; } = new(true, null);

    public static ArchivePublishResult Failed(string reason) => new(false, reason);

    /// <summary>
    /// 归档层上**已经有这个名字，但内容不是这一份**（§四）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这不是「已经传过了」，是「两份都要、而名字只有一个」</b>——
    /// 重试解决不了，得人来决定（改名 / 删掉多余的那一份）。所以它带上
    /// <see cref="ArchiveFailureKind.Conflict"/>，补发那一路会跳过它。
    /// </remarks>
    public static ArchivePublishResult Conflict(string reason) =>
        new(false, reason, ArchiveFailureKind.Conflict);
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
                    // ⚠️ **同名 ≠ 同一条录像**（§四）。
                    //
                    // 这里从前直接回 `Ok`，理由是「名字就是内容的身份」。那个前提
                    // 在 2026-10-12 换路径形状时**作废了**：新名字里没有会话号
                    // （`<单号>.mp4`），所以「同一个单号、同一天」的第二场**必然**同名。
                    // 那时回 `Ok` 的意思是「已经传上去了」—— 而盘上那一份是**第一场的**。
                    // 于是：账上记着「已发布」（用户以为双份了），回查（只问在不在）
                    // 也说「有」⇒ 清理把第二场本机那一份删掉 ⇒ **第二场只剩一个错的副本**。
                    //
                    // 所以判据只能是**内容**（需求方 2026-10-12 裁定：大小 + SHA-256）。
                    // 先比大小短路（份数不同必然不同），一样再比哈希 —— 撞名是罕见路径，
                    // 而这条路上做错的代价是丢证据，值得多读一遍盘。
                    if (await IsSameContentAsync(localPath, destination, cancellationToken))
                    {
                        // 真的是同一份（重发 / 补传）⇒ 复用归档层上那份，不重写。
                        return ArchivePublishResult.Ok;
                    }

                    // ⚠️ 这句话会**原样上屏**（设置页那行提示就是拿它拼的），所以不带
                    // 任何标记符号 —— `TextBlock` 不认 Markdown，`**` 会照字面显示出来。
                    return ArchivePublishResult.Conflict(
                        $"{Label}上已经有同名的一份，但内容和这一份不一样："
                        + $"归档层那份是 {destination}，本机这份是 {localPath}。"
                        + "两份都要、而这个名字只能占一个 —— 需要人工处理："
                        + "要么把它改名留档、要么删掉多余的那一份。本机这一份不会被自动删掉。");
                }

                // 先写临时名再改名：发到一半断网/掉盘时，归档层上留下的是一个
                // **半截文件**而不是一个看着完好的坏文件。半截文件的扩展名不是 .mp4，
                // 回查时那一份仍算「不存在」，下次会重发。
                var staging = destination + ".part";
                await CopyAsync(localPath, staging, null, cancellationToken);
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

    /// <remarks>
    /// ⚠️ 归档层是**本机**那一档（<see cref="ArchiveBackendKind.LocalDisk"/>）时
    /// **直接回「没有这一条」**，不往下走。那一档的语义是「本机这一份**就是**归档层
    /// 那一份」（见本类说明），所以盘上没有就真的没有第二个地方可取；
    /// 而那一档的根是 <c>&lt;root&gt;\archive</c>，那个目录**根本不会被建出来**
    /// —— 不早退的话会走成「归档目录访问不了：…」，一句与事实不符的话。
    /// <para>
    /// ⚠️ 多根时**第一个有的赢**（与 <see cref="VerifyAsync"/> 同一个试法）：
    /// 换过 NAS 的机器上，同一个相对路径可能两个根上都有。
    /// </para>
    /// </remarks>
    public async Task<ArchiveFetchResult> FetchAsync(
        RelativePath location,
        string destination,
        Action<long, long?>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsSelf)
        {
            return ArchiveFetchResult.NotFound;
        }

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

                var source = Path.Combine(root, location.Value);

                if (!File.Exists(source))
                {
                    continue;
                }

                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                // ⚠️ 与发布那一头**同一条**：先写 `.part` 再改名。
                // 半截的 `<单号>.mp4` 落在盘上会被 `File.Exists` 当成「本机有」——
                // 检索页那句警告消失，而播放器打开的是一个坏文件。
                var staging = destination + ".part";
                await CopyAsync(source, staging, progress, cancellationToken);
                File.Move(staging, destination, overwrite: false);

                return ArchiveFetchResult.Ok;
            }

            if (reachable == 0)
            {
                return ArchiveFetchResult.Failed($"{Label}访问不了：{string.Join("、", _roots)}");
            }

            return ArchiveFetchResult.NotFound;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            TryCleanStaging(destination);

            return ArchiveFetchResult.Failed($"从{Label}取回出错：{ex.Message}");
        }
    }

    /// <summary>
    /// 这两份是不是**同一个文件的内容**（§四 的判据：先比大小，再比 SHA-256）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>先比大小</b>：不同的两场录像时长不同，字节数几乎必然不同 —— 一次
    /// <c>FileInfo</c> 就把绝大多数撞名挡掉了，不必读两遍盘。一样大才落到哈希
    /// （两场录像碰巧字节数相同是可能的，短片段尤其；而**比错方向**的代价是
    /// 「把别人的录像当成自己的」⇒ 丢证据，所以这一层不能只看大小）。
    /// </para>
    /// <para>
    /// ⚠️ <b>读不动时**不**返回 false，而是让它抛</b>：抛出去会被
    /// <see cref="PublishAsync"/> 那个 <c>catch</c> 接住，落成「这一档写不进去 ⇒
    /// 试下一个根 ⇒ 都没成就是一次普通失败（下次照旧重试）」。若在这里吞掉并返回
    /// false，网络抖一下就会被判成**撞名**——那是一笔**永远等不到人**的账。
    /// </para>
    /// </remarks>
    private static async Task<bool> IsSameContentAsync(
        string left, string right, CancellationToken cancellationToken)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length)
        {
            return false;
        }

        var leftHash = await ContentHasher.ComputeFileHashAsync(left, cancellationToken);
        var rightHash = await ContentHasher.ComputeFileHashAsync(right, cancellationToken);

        return string.Equals(leftHash.Value, rightHash.Value, StringComparison.Ordinal);
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
        ArchiveBackendKind.Directory => "网络位置",
        _ => "本机磁盘",
    };

    private static async Task CopyAsync(
        string from, string to, Action<long, long?>? progress, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            from, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var target = new FileStream(
            to, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        if (progress is null)
        {
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);

            return;
        }

        // 要报进度就得自己搬 —— `Stream.CopyToAsync` 没有「搬到哪儿了」的出口。
        // 总数取 `Length`：本机盘与网络盘上的文件都报得出来（读不动的话
        // `Length` 自己会抛，那也是「取不回来」，由调用方落成一次失败）。
        var total = source.Length;
        var buffer = new byte[81920];
        long copied = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);

            if (read <= 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progress(copied, total);
        }

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
/// <para>
/// <b>T18：这里也是「已归档」这笔账唯一的记账点。</b>发布成功 ⇒ 往
/// <see cref="PublishedStore"/> 追加一条 —— 清理层的时间锚靠它。写在咽喉上而不是
/// 两个调用点各写一遍，理由是同一句：**两处各写一遍必然走岔**，
/// 而走岔的表现是「有一条没记上 ⇒ 它被永久豁免 ⇒ 盘满」，不报错。
/// </para>
/// <para>
/// <b>T23-A：成败两笔账都落盘。</b>上面那个 <see cref="LastFailure"/> 只在内存 ⇒
/// 重启后「NAS 那份没发上去」就看不见了，而它恰恰是最容易出事的时候
/// （重启往往就是因为刚出过问题）。所以失败时往 <see cref="ArchiveFailureLog"/>
/// 记一笔、成功时把那笔撤掉。**同一个咽喉，不另找地方写。**
/// </para>
/// <para>
/// <b>⚠️ 这本账不是每档都有（2026-10-15 需求方裁定）。</b>它存在的理由是
/// 「目录型那一档没有落盘重传队列」，而网盘那一档有 —— 失败由
/// <c>CloudUploadService.UploadQueue</c> 到点重试，「百度网盘上传」页上还摆着
/// **放弃 / 恢复**，那是用户真能操作的那一个。网盘也记一遍的后果是具体的：
/// 两个地方说同一件事，而**网盘那条路没有任何代码去销这笔账**
/// ⇒ 设置页上会永远挂着一条「N 条待上传」，修好了也不消失。
/// 所以网盘那一档传 <see langword="null"/>，见构造函数的参数说明。
/// </para>
/// </remarks>
public sealed class ArchiveRelay
{
    private readonly IArchivePublisher _publisher;
    private readonly PublishedStore _published;
    private readonly ArchiveFailureLog? _failures;
    private readonly IAppLogger _logger;

    /// <param name="failures">
    /// 「哪几条没发上去」那本账。**给 <see langword="null"/> 表示这一档的失败不归这儿管**
    /// —— 它自己有落盘重传队列，也自己有界面（见 <see cref="RetryOutstandingAsync"/> 的说明）。
    /// </param>
    public ArchiveRelay(
        IArchivePublisher publisher,
        string label,
        PublishedStore published,
        ArchiveFailureLog? failures,
        IAppLogger? logger = null)
    {
        _publisher = publisher;
        _published = published;
        _failures = failures;
        Label = label;
        _logger = logger ?? NullLogger.Instance;
    }

    public string Label { get; }

    /// <summary>最近一次发布失败的原因；没失败过就是 <see langword="null"/>。</summary>
    /// <remarks>
    /// 界面要显示它 —— 「归档层那一份没发上去」是**必须让用户知道**的事：
    /// 它意味着本地这一份还不能被清理（回查会拒），用户如果以为已经双份了，
    /// 就会手动删掉唯一的那一份。
    /// <para>
    /// ⚠️ <b>它只在内存</b>，所以它答的是「**这一趟运行**里最近那次」。
    /// 「重启之前发生过的那些」在 <see cref="ArchiveFailureLog"/> 里 ——
    /// 界面要**两个都看**，只看这个的话，重启后那句话就没了（T23-A）。
    /// </para>
    /// </remarks>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// 最近那次失败是**哪一种**（<see cref="LastFailure"/> 的伴侣）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 界面必须看它：撞名那一档「修好之后下次会自动再发」那句话**是假的**
    /// —— 它不是在等网络，是在等**一个人**做决定。说错方向就把用户支去查网络。
    /// </remarks>
    public ArchiveFailureKind LastFailureKind { get; private set; } = ArchiveFailureKind.Ordinary;

    /// <summary>最近一次成功发布是哪一条。</summary>
    public string? LastPublished { get; private set; }

    /// <param name="evidenceId">
    /// 索引里的那一条。**不是可选的** —— 记账要它，而「调用方忘了传」这件事
    /// 不该在运行期表现为「那条录像永远清不掉」。让编译器挡住。
    /// </param>
    public async Task<ArchivePublishResult> PublishAsync(
        string evidenceId, RelativePath location, string localPath,
        CancellationToken cancellationToken = default)
    {
        var result = await _publisher.PublishAsync(location, localPath, cancellationToken);

        if (result.Published)
        {
            LastFailure = null;
            LastPublished = location.Value;

            // ⚠️ 顺序：**先撤欠账，再记台账**。反过来的话，撤欠账那一步若失败
            // （磁盘满、没权限），界面会一直提示「这条没发上去」——
            // 而那是个**假警报**，用户会去查一个不存在的问题。
            if (_failures is not null)
            {
                await BookkeepAsync(
                    evidenceId,
                    () => _failures.NoteRecoveredAsync(evidenceId, DateTimeOffset.Now),
                    $"发布到{Label}成功，但「它上次没发上去」那笔欠账没撤掉 —— "
                    + "重启后设置页会继续提示这一条（那是假的，它已经发上去了）");
            }

            await RecordPublishedAsync(evidenceId, location);
            return result;
        }

        // 留痕：这个失败**不影响**本机那一份与索引，但要能回答
        // 「这条为什么还没上归档层」。⚠️ 日志**不看账本有没有** —— 没账本的那一档
        // （网盘）出事时，日志是唯一的留痕。
        _logger.Log(LogLevel.Warn, "归档",
            $"发布到{Label}失败：{result.FailureReason ?? "（没给出原因）"}",
            new Dictionary<string, object?> { ["location"] = location.Value });

        // 没账本 ⇒ 这一档的失败不归这儿管（见构造函数的说明）。**内存里那个也不记** ——
        // 账本与 `LastFailure` 是同一件事的两份（一份落盘、一份在内存），
        // 只关掉一半，设置页照样会挂出那条提示。
        if (_failures is null)
        {
            return result;
        }

        LastFailure = result.FailureReason ?? "归档层那一份没发上去（没给出原因）";
        LastFailureKind = result.Kind;

        // ⚠️ **不传调用方的 CancellationToken**（见 `BookkeepAsync` 的说明）：
        // 这条欠账要是跟着「收尾被取消」一起丢了，重启之后用户就再也看不到它 ——
        // 而那正是 T23-A 要修的那个洞。
        await BookkeepAsync(
            evidenceId,
            () => _failures.NoteFailedAsync(
                new ArchiveFailureRecord(
                    evidenceId, location.Value, LastFailure, DateTimeOffset.Now,
                    ArchiveFailureState.Failed, result.Kind)),
            $"发布到{Label}失败，而且这笔欠账没落盘 —— "
            + "重启之后设置页不会提示这一条（盘上它仍然只有一份）");

        return result;
    }

    /// <summary>
    /// 把上次没发上去的那些**再发一次**（T23-B）。
    /// </summary>
    /// <param name="resolveLocalPath">
    /// 归档层相对路径 → **本机那一份**的绝对路径（生产上是
    /// <c>StorageLocations.ResolveOrActive</c>）。找不到给 <see langword="null"/>。
    /// </param>
    /// <returns>真的重发了几条（跳过的不算）。</returns>
    /// <remarks>
    /// <para>
    /// <b>为什么要它。</b>目录型那一档（NAS / 挂载盘）**没有落盘重传队列** ——
    /// 只有网盘那一档有（<c>CloudUploadService</c> 的 <c>UploadQueue</c> + 定时 tick）。
    /// 于是「NAS 那份没发上去」在那条录像的**余生**里都不会再试：发布只在收尾那一刻
    /// 发生一次（<c>SessionFinalizer</c>），而
    /// <see cref="DirectoryArchiveBackend.PublishAsync"/> 里「已经有一份就不重发」
    /// 那条判据在这儿够不着 —— 归档层上**根本没有**那一份，下次走的仍是「发一次」，
    /// 而下次永远不会来。
    /// </para>
    /// <para>
    /// 后果不是丢证据（回查会拒删，I2 / I8 都成立），而是**盘会满**：
    /// 那一条永远清不掉，而用户以为已经双份了。
    /// </para>
    /// <para>
    /// ⚠️ <b>只遍历这本失败账，绝不扫全库。</b>这本账只在「真出事」时才涨
    /// （见 <see cref="ArchiveFailureLog"/>），所以这一趟在正常机器上是**空转**。
    /// 扫全库在归档层是慢速 NAS 时会把启动拖死，而那条路上没有任何收益：
    /// 没进这本账的那些，收尾那一刻已经发过了。
    /// </para>
    /// <para>
    /// ⚠️ <b>本机那一份已经不在了的跳过</b>：没有东西可发。
    /// 那不是「重发失败」，只是这笔欠账已经没有意义了。
    /// </para>
    /// </remarks>
    public async Task<int> RetryOutstandingAsync(
        Func<string, string?> resolveLocalPath, CancellationToken cancellationToken = default)
    {
        // 没有失败账本的那一档（网盘）**没有要补发的东西** —— 它的重传在
        // `CloudUploadService.UploadQueue` 那边，不归这儿。生产上不会走到这一支
        // （`DesktopServices` 只对目录型起这个循环），挡住是为了「万一」不变成 NRE。
        if (_failures is null)
        {
            return 0;
        }

        // ⚠️ 先拷一份：下面一边发一边把这本账里的条目划掉（发成功就撤欠账），
        // 直接在 `Outstanding` 上遍历会「遍历时修改集合」。
        var outstanding = _failures.Outstanding
            .Where(record => record.Location is { Length: > 0 })
            .ToArray();

        if (outstanding.Length == 0)
        {
            // 正常机器上走的就是这一支 —— **一条日志都不记**（§6.1：不刷屏）。
            return 0;
        }

        _logger.Log(LogLevel.Info, "归档",
            $"上次有 {outstanding.Length} 条没发到{Label}，现在补发一次。");

        var attempted = 0;
        var skipped = 0;
        var conflicts = 0;

        foreach (var record in outstanding)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ⚠️ **撞名的不试**（§四）：那两份内容都要，而名字只有一个 ——
            // 再发一百次也是同一个结果。试下去只会每次启动多一条 Warn，而这件事
            // **永远得等一个人**（界面上那句话说清楚了该干什么）。它们**留在账上**
            // 不划掉：划掉就等于宣布「这条已经双份了」，而它并没有。
            if (record.Kind == ArchiveFailureKind.Conflict)
            {
                conflicts++;
                continue;
            }

            try
            {
                // ⚠️ **先解析账上那一行**。它才是**能被人手改坏**的那一头
                // （`RelativePath.Parse` 对 UNC / 根路径会抛），所以要在最前面挡住 ——
                // 排在它后面的那些该发还是得发。
                var location = RelativePath.Parse(record.Location!);
                var localPath = resolveLocalPath(record.Location!);

                if (localPath is null || !File.Exists(localPath))
                {
                    skipped++;
                    continue;
                }

                attempted++;

                // 成功 ⇒ `PublishAsync` 自己把这笔欠账撤掉、并记上「已归档」那本台账；
                // 失败 ⇒ 它已经记了一条 Warn，欠账照旧留着（下次启动再试）。
                await PublishAsync(record.EvidenceId, location, localPath, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ⚠️ **一条坏账不许把整趟补发拖垮** —— 与索引、队列那些「坏行跳过」
                // 同一条规矩。这里能抛的只有 `RelativePath.Parse`（账是手改过或写坏的）。
                _logger.Log(LogLevel.Warn, "归档",
                    $"补发这一条时出错（证据 {record.EvidenceId}）：{ex.Message}",
                    new Dictionary<string, object?> { ["证据"] = record.EvidenceId });
            }
        }

        var remaining = _failures.Outstanding.Count;

        _logger.Log(
            remaining == 0 ? LogLevel.Info : LogLevel.Warn,
            "归档",
            $"补发完了：试了 {attempted} 条，{Label}上还有 {remaining} 条没上去"
            + (skipped > 0 ? $"，另有 {skipped} 条本机那一份已经不在了（跳过）" : string.Empty)
            // ⚠️ 撞名的单独说一句 —— 它们**试都不会试**，只看上面那个数会以为
            // 「补发过了但还是没上去」，从而去查网络。
            + (conflicts > 0 ? $"，另有 {conflicts} 条与{Label}上同名但内容不同（要人工处理，跳过）" : string.Empty)
            + "。");

        return attempted;
    }

    /// <summary>记一笔「这条已经发到归档层了」。**记不上不能把发布结果弄坏。**</summary>
    /// <remarks>
    /// ⚠️ <b>不传调用方的 <see cref="CancellationToken"/>，用 <see cref="CancellationToken.None"/>。</b>
    /// 走到这里**归档层上已经有那一份了** —— 这是既成事实。这时若收尾被取消（关机、退出）
    /// 而把这一行也丢掉，结果就是「归档层有、账上没有」⇒ 那条录像被判成唯一副本、
    /// **永远清不掉**，正是 T18 本身。账本比一次收尾的及时性值钱。
    /// </remarks>
    private Task RecordPublishedAsync(string evidenceId, RelativePath location) =>
        BookkeepAsync(
            evidenceId,
            () => _published.AppendAsync(
                new PublishedRecord(evidenceId, DateTimeOffset.Now, location.Value),
                CancellationToken.None),
            $"发布到{Label}成功，但「已归档」这笔账没记上 —— "
            + "这条将来会被当成唯一副本、永远清不掉（磁盘只涨不落）");

    /// <summary>
    /// 记一笔账：**记不上只留痕，绝不把发布结果弄坏**。
    /// </summary>
    /// <param name="evidenceId">哪一条的账。⚠️ 必须传：这三条 Warn 说的都是
    /// 「**某一条**的账没记上」，不写出是哪一条，事后翻日志的人无从下手。</param>
    /// <param name="write">真正落盘的那一步。</param>
    /// <param name="consequence">记不上的**后果**（不是堆栈）—— 出事了要能回答「下一步会怎样」。</param>
    /// <remarks>
    /// <para>
    /// 与 <c>CleanupExecutor.TryAppendAsync</c> 同一条规矩：账没记上是个
    /// **要让人看见的问题**（记 Warn，说的是**后果**而不是堆栈），
    /// 但它**不是发布失败** —— 返回 <c>Published: true</c> 才是实话，
    /// 否则调用方会去重发一份归档层上已经有的东西。
    /// </para>
    /// <para>
    /// ⚠️ <b>一律用 <see cref="CancellationToken.None"/></b>，不传调用方那个：
    /// 走到这几个调用点时事实已经发生了（归档层上有没有那一份已经定了），
    /// 这时因为「收尾被取消」把账丢掉，两次都对不上 —— 一次是「有却没记」，
    /// 一次是「没成却没留痕」。
    /// </para>
    /// </remarks>
    private async Task BookkeepAsync(string evidenceId, Func<Task> write, string consequence)
    {
        try
        {
            await write();
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warn, "归档", $"{consequence}（证据 {evidenceId}）：{ex.Message}",
                new Dictionary<string, object?> { ["证据"] = evidenceId });
        }
    }
}
