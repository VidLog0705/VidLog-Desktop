using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 归档层四档（规格 §3.4.6）与它上面的一进一出：**发布**与**回查**。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 测试里的「NAS」就是**一个本地临时目录** —— 这不是偷懒，而是规格自己定的
/// 实现口径：「挂载网络驱动器……网络那一层由用户自己在系统里解决，我们只当成一个目录用」。
/// 所以「目录型」这一档能验到的东西（发布、覆盖、半截文件、回查），
/// 本地全都验得到；**验不了的只有「那个目录真的在网络那头」**。
/// </para>
/// <para>
/// ⚠️ 百度网盘那一档（<see cref="ArchiveBackendKind.Cloud"/>）的**发布与回查**
/// 从 2026-09-30 起有实现了（<c>CloudArchiveBackend</c>），但它的测试在
/// <c>CloudUploadTests</c> 里 —— 那边用假网盘把 HTTP 那一层换掉，因为
/// 真接口要真账号、真网络，本地一行都验不了。见 `docs/实现决策.md` §87。
/// </para>
/// </remarks>
public class ArchiveBackendTests
{
    private static RelativePath Where(string value) => RelativePath.Parse(value);

    // ─────────────────────────────────────────────
    // 档位本身
    // ─────────────────────────────────────────────

    [Fact]
    public void 只有网络位置那一档算目录型()
    {
        // ⚠️ 2026-10-13 之前这里是**两档**（NAS 与挂载盘各一个枚举值），
        // 而它们走的是同一份实现、同一张配置表 —— 合并成「网络位置」一档。
        Assert.True(new ArchiveTarget(ArchiveBackendKind.Directory).IsDirectoryType);

        Assert.False(new ArchiveTarget(ArchiveBackendKind.LocalDisk).IsDirectoryType);
        Assert.False(new ArchiveTarget(ArchiveBackendKind.Cloud).IsDirectoryType);
    }

    [Fact]
    public void 只有本机磁盘那一档不给清理()
    {
        // 规格 §3.5.1：判据是「归档层是不是**就在这台电脑上**」——
        // 网络位置（哪怕挂载成 Z:\ 看着就是个本地盘）算「在别处」。
        Assert.False(new ArchiveTarget(ArchiveBackendKind.LocalDisk).AllowsCleanup);

        Assert.True(new ArchiveTarget(ArchiveBackendKind.Directory, @"\\nas\vidlog").AllowsCleanup);
        Assert.True(new ArchiveTarget(ArchiveBackendKind.Directory, @"Z:\vidlog").AllowsCleanup);
        Assert.True(new ArchiveTarget(ArchiveBackendKind.Cloud).AllowsCleanup);
    }

    [Fact]
    public void 目录型没填路径或填了相对路径都算没配好()
    {
        Assert.NotNull(new ArchiveTarget(ArchiveBackendKind.Directory).ConfigurationProblem);
        Assert.NotNull(new ArchiveTarget(ArchiveBackendKind.Directory, "   ").ConfigurationProblem);
        Assert.NotNull(new ArchiveTarget(ArchiveBackendKind.Directory, "vidlog").ConfigurationProblem);

        Assert.Null(new ArchiveTarget(ArchiveBackendKind.Directory, @"\\nas\vidlog").ConfigurationProblem);
        Assert.Null(new ArchiveTarget(ArchiveBackendKind.Directory, @"Z:\vidlog").ConfigurationProblem);

        // 本机磁盘不需要配路径 —— 它就在本机。
        Assert.Null(new ArchiveTarget(ArchiveBackendKind.LocalDisk).ConfigurationProblem);

        // ⚠️ 网盘那一档**永远不算「没配好」**（2026-10-15 需求方：「百度网盘已经可以上传了，
        // 为什么还显示归档层没配好？」）。这里原来有一句写死的话，什么都没查就断言「要授权」——
        // 而凭据在不在、登没登录**这一格问不到**（凭据是环境变量、登录是启动时才恢复的）。
        // 真话由那两处量出来说：`BaiduPanCredentials.MissingMessage` 与
        // `DesktopServices.StartAsync` 里按 `Session.IsLoggedIn` 判的那句。
        // 这条钉住的是「别再往这一格塞一句写死的话」。
        Assert.Null(new ArchiveTarget(ArchiveBackendKind.Cloud).ConfigurationProblem);
    }

    // ─────────────────────────────────────────────
    // 归档层是**推算**出来的（2026-10-13 需求方裁定）
    // ─────────────────────────────────────────────

    [Fact]
    public void 备份位置表里有路径_就是那个网络位置()
    {
        // 需求方原话：「如果是网络硬盘或者NAS，则不需要上传到百度网盘」——
        // 表里那条路径**就是**第二份在哪儿。
        var target = ArchiveTarget.FromConfig(
            ArchiveBackendKind.LocalDisk, @"\\nas\vidlog", cloudAutoUpload: true);

        Assert.Equal(ArchiveBackendKind.Directory, target.Kind);
        Assert.Equal(@"\\nas\vidlog", target.DirectoryPath);

        // ⚠️ 开关开着也**不算网盘**：表里有位置，第二份就是它。
        // 算成「两个第二份都有」的话，清理会把「网盘那份其实没传上去」当成功。
        Assert.True(target.AllowsCleanup);
    }

    [Fact]
    public void 表空着而自动上传开着_第二份就是百度网盘()
    {
        var target = ArchiveTarget.FromConfig(
            ArchiveBackendKind.LocalDisk, null, cloudAutoUpload: true);

        Assert.Equal(ArchiveBackendKind.Cloud, target.Kind);
        Assert.True(target.AllowsCleanup);
    }

    [Fact]
    public void 老配置里手选过网盘那一档的_自动上传关着也照认()
    {
        // ⚠️ 这条挡的是「升级之后网盘那份再也算不出来」：2026-10-13 之前
        // **没有**「自动上传」这个开关，用户是靠老下拉框声明网盘的。只认表与开关
        // 的话，那些人的归档层会当场掉回本机 ⇒ 清理入口消失、录像永远清不掉。
        var target = ArchiveTarget.FromConfig(
            ArchiveBackendKind.Cloud, null, cloudAutoUpload: false);

        Assert.Equal(ArchiveBackendKind.Cloud, target.Kind);
    }

    [Fact]
    public void 表空着开关也关着_就没有第二份_朝少删的那头落到本机()
    {
        // 与前一条刻意不同：**不认识的**老值不翻译，只认表与开关。
        // 与 `RetentionSetting.FromConfig` 解析失败回落「全部保留」是同一条规矩。
        var target = ArchiveTarget.FromConfig(
            (ArchiveBackendKind)99, null, cloudAutoUpload: false);

        Assert.Equal(ArchiveBackendKind.LocalDisk, target.Kind);
        Assert.False(target.AllowsCleanup);
    }

    [Fact]
    public void 跨网能力要如实说()
    {
        // 规格 §2.3：产品**不得承诺做不到的事**。
        Assert.Contains("跨网", new ArchiveTarget(ArchiveBackendKind.Cloud).Reachability);
        Assert.Contains("只有本机这一份", new ArchiveTarget(ArchiveBackendKind.LocalDisk).Reachability);
        Assert.Contains(
            "同一个网络", new ArchiveTarget(ArchiveBackendKind.Directory, @"Z:\v").Reachability);
    }

    // ─────────────────────────────────────────────
    // 发布
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 本机磁盘那一档发布是空操作()
    {
        using var dir = new TempDir();
        var backend = new DirectoryArchiveBackend(dir.ArchiveRoot, ArchiveBackendKind.LocalDisk);

        var result = await backend.PublishAsync(Where("a/b.mp4"), "根本不存在的路径.mp4");

        // 本机这一份**就是**归档层那一份 —— 没什么可发的，也不该因为
        // 「源文件路径不对」而报错（它本来就不需要那个参数）。
        Assert.True(result.Published);
    }

    [Fact]
    public async Task 目录型发布把文件搬过去而且内容一致()
    {
        using var dir = new TempDir();
        var nas = System.IO.Path.Combine(dir.Path, "nas");
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Directory);

        var local = System.IO.Path.Combine(dir.Path, "local.mp4");
        await File.WriteAllBytesAsync(local, [1, 2, 3, 4, 5]);

        var result = await backend.PublishAsync(Where("2026/09/27/SF1/e-000.mp4"), local);

        Assert.True(result.Published);
        Assert.Null(result.FailureReason);

        var copied = System.IO.Path.Combine(nas, "2026", "09", "27", "SF1", "e-000.mp4");
        Assert.True(File.Exists(copied), "归档层上应当出现同样的相对路径");
        Assert.Equal(await File.ReadAllBytesAsync(local), await File.ReadAllBytesAsync(copied));

        // 本机这一份**不动** —— 发布是复制，不是搬走（搬走的话本机就空了）。
        Assert.True(File.Exists(local));
    }

    [Fact]
    public async Task 归档层上已经有一份而且内容一样就不重发()
    {
        using var dir = new TempDir();
        var nas = System.IO.Path.Combine(dir.Path, "nas");
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Directory);

        var local = System.IO.Path.Combine(dir.Path, "local.mp4");
        await File.WriteAllBytesAsync(local, [1, 2, 3]);
        await backend.PublishAsync(Where("e.mp4"), local);

        // 再发一次同一个文件：**认下归档层那一份**，不用重写一遍。
        // 这是「补传 / 重发不会白读一遍盘」那一档。
        var result = await backend.PublishAsync(Where("e.mp4"), local);

        Assert.True(result.Published);
        Assert.Null(result.FailureReason);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(System.IO.Path.Combine(nas, "e.mp4")));
    }

    [Fact]
    public async Task 归档层上同名但内容不同_判成撞名且两边都不被覆盖()
    {
        using var dir = new TempDir();
        var nas = System.IO.Path.Combine(dir.Path, "nas");
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Directory);

        var local = System.IO.Path.Combine(dir.Path, "local.mp4");
        await File.WriteAllBytesAsync(local, [1, 2, 3]);
        await backend.PublishAsync(Where("e.mp4"), local);

        // 把归档层那一份改成**别的内容**，再发一次。
        //
        // ⚠️ 从前这里判「同名 = 同一条录像」⇒ 回 `Ok`（= 声称已经传上去了），
        // 而盘上躺着的是**别人的**那一份 —— 那正是 §四 要修的那个谎
        // （2026-10-12 换路径形状之后，名字里没有会话号，同名单号必然撞名）。
        // 现在的判据是**内容**：一样才 `Ok`，不一样就是撞名，要人来处理。
        var copied = System.IO.Path.Combine(nas, "e.mp4");
        await File.WriteAllBytesAsync(copied, [9, 9, 9]);

        var result = await backend.PublishAsync(Where("e.mp4"), local);

        Assert.False(result.Published);
        Assert.Equal(ArchiveFailureKind.Conflict, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));

        // 两边都**不许被覆盖** —— 这两份内容不同，可能都是别人的证据。
        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(copied));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(local));
    }

    [Fact]
    public async Task 归档目录不可达时发布失败_但本机那一份一个字都没动()
    {
        using var dir = new TempDir();
        // 拿一个**文件**当归档根：建目录必然失败，等价于「盘符掉了 / 没权限」。
        var blocker = System.IO.Path.Combine(dir.Path, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x");

        var backend = new DirectoryArchiveBackend(
            System.IO.Path.Combine(blocker, "nas"), ArchiveBackendKind.Directory);

        var local = System.IO.Path.Combine(dir.Path, "local.mp4");
        await File.WriteAllBytesAsync(local, [1, 2, 3]);

        var result = await backend.PublishAsync(Where("e.mp4"), local);

        Assert.False(result.Published);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
        Assert.True(File.Exists(local), "发布失败**绝不能**动本机那一份（I2）");
    }

    [Fact]
    public async Task 发布到一半的临时文件不叫_mp4()
    {
        // 半截文件如果叫 .mp4，回查会把它当成「那一份在」—— 而它根本播不了。
        // 那条判据就在这里：staging 用 .part 后缀。
        using var dir = new TempDir();
        var nas = System.IO.Path.Combine(dir.Path, "nas");
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Directory);

        var local = System.IO.Path.Combine(dir.Path, "local.mp4");
        await File.WriteAllBytesAsync(local, [1, 2, 3]);
        await backend.PublishAsync(Where("e.mp4"), local);

        var leftovers = Directory.GetFiles(nas, "*.part", SearchOption.AllDirectories);
        Assert.Empty(leftovers);
    }

    // ─────────────────────────────────────────────
    // 回查（I8 的前置）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 回查分得清_不存在_与_查不了()
    {
        // 这两者对用户不是一回事：一个是「归档层上那份被删了」，
        // 一个是「现在问不到」。但**两者都导致不删**（I8）——
        // 把「查不了」当成「不存在」会删掉唯一一份。
        using var dir = new TempDir();
        var nas = System.IO.Path.Combine(dir.Path, "nas");
        Directory.CreateDirectory(nas); // 归档层建好了、只是这一条没发上去

        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Directory);

        var missing = await backend.VerifyAsync(Where("nope.mp4"));
        Assert.False(missing.Exists);
        Assert.False(missing.CouldNotVerify, "目录摸得到、文件不在 —— 这才是「不存在」");
        Assert.Null(missing.FailureReason);

        // 归档根整个是空的/不在了 → 「查不了」，不是「不存在」。
        var unreachable = await new DirectoryArchiveBackend(
            System.IO.Path.Combine(dir.Path, "不存在的盘", "nas"),
            ArchiveBackendKind.Directory).VerifyAsync(Where("e.mp4"));

        Assert.False(unreachable.Exists);
        Assert.True(unreachable.CouldNotVerify, "目录都摸不到，那时**不能**说「这份不存在」");
    }

    [Fact]
    public async Task 回查认得出刚发过去的那一份()
    {
        using var dir = new TempDir();
        var nas = System.IO.Path.Combine(dir.Path, "nas");
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Directory);

        var local = System.IO.Path.Combine(dir.Path, "local.mp4");
        await File.WriteAllBytesAsync(local, [1, 2, 3]);
        var location = Where("2026/09/27/SF1/e-000.mp4");
        await backend.PublishAsync(location, local);

        var verify = await backend.VerifyAsync(location);

        Assert.True(verify.Exists);
        Assert.False(verify.CouldNotVerify);
    }

    // ─────────────────────────────────────────────
    // 取回（甲，2026-10-16）：本机没有就从归档层搬回来
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 取回把归档层那一份搬到本机()
    {
        using var dir = new TempDir();
        var nas = System.IO.Path.Combine(dir.Path, "nas");
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Directory);

        var location = Where("VidLog-Video/2026-10-16/发货/SF1.mp4");
        var onNas = System.IO.Path.Combine(nas, location.Value);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(onNas)!);
        await File.WriteAllBytesAsync(onNas, [1, 2, 3, 4]);

        var local = System.IO.Path.Combine(dir.Path, "本机", "SF1.mp4");

        var seen = new List<(long Received, long? Total)>();
        var result = await backend.FetchAsync(
            location, local, (received, total) => seen.Add((received, total)));

        Assert.True(result.Fetched, result.FailureReason);

        // ⚠️ 取回来的要**一模一样**：搬完这条就与「本来就是在本机录的」不可区分
        //（能播、能导出、能回查、能清理）—— 下游一处都不用改。
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(local));

        // ⚠️ 半截文件不留：留着的话它就是一份看着像成品的坏文件。
        Assert.False(File.Exists(local + ".part"));

        // 归档层那一份**不动** —— 取回是复制不是搬家。搬走的话别处就没第二份了，
        // 而清理层放行本机那一份的判据**正是**「别处还有一份」。
        Assert.True(File.Exists(onNas));

        // 进度要一路报到尾（界面那根条靠它）：最后一次是「收满 / 总共」。
        Assert.Equal(4, seen[^1].Received);
        Assert.Equal(4, seen[^1].Total);
    }

    [Fact]
    public async Task 归档层上没有这一条_说的是没找到_不是取不回来()
    {
        // 这两句话对用户**不是一回事**：一个说「这条在哪儿都没有了」，
        // 一个说「现在问不到」。混成一句的话，用户看到「没找到」会以为录像没了，
        // 而它可能好端端在盘上、只是那台机器现在没开机（I8 的同一条道理）。
        using var dir = new TempDir();
        var nas = System.IO.Path.Combine(dir.Path, "nas");
        Directory.CreateDirectory(nas); // 归档层建好了、只是这一条不在上面

        var local = System.IO.Path.Combine(dir.Path, "本机", "SF1.mp4");
        var result = await new DirectoryArchiveBackend(nas, ArchiveBackendKind.Directory)
            .FetchAsync(Where("VidLog-Video/2026-10-16/发货/SF1.mp4"), local);

        Assert.False(result.Fetched);
        Assert.True(result.Missing, "目录摸得到、文件不在 —— 这才是「没有这一条」");
        Assert.Null(result.FailureReason);
        Assert.False(File.Exists(local), "没取到就不许在本机留下空壳");
    }

    [Fact]
    public async Task 一个根摸不到_另一个根上有_就从有的那个取()
    {
        // ⚠️ 与回查同一个理由（用户换过 NAS）：只看第一个根的话，老录像永远取不回来，
        // 而界面上只会说「未找到」—— 用户会以为录像被删了。
        using var dir = new TempDir();
        var gone = System.IO.Path.Combine(dir.Path, "没插的盘", "nas");
        var old = System.IO.Path.Combine(dir.Path, "老NAS");
        Directory.CreateDirectory(old);

        var location = Where("VidLog-Video/2026-10-16/发货/SF1.mp4");
        var onOld = System.IO.Path.Combine(old, location.Value);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(onOld)!);
        await File.WriteAllBytesAsync(onOld, [7, 7]);

        var local = System.IO.Path.Combine(dir.Path, "本机", "SF1.mp4");
        var result = await new DirectoryArchiveBackend([gone, old], ArchiveBackendKind.Directory)
            .FetchAsync(location, local);

        Assert.True(result.Fetched, result.FailureReason);
        Assert.Equal([7, 7], await File.ReadAllBytesAsync(local));
    }

    [Fact]
    public async Task 一个根都摸不到_说的是取不回来_不是没有()
    {
        using var dir = new TempDir();

        var local = System.IO.Path.Combine(dir.Path, "本机", "SF1.mp4");
        var result = await new DirectoryArchiveBackend(
            [System.IO.Path.Combine(dir.Path, "没插的盘", "nas")],
            ArchiveBackendKind.Directory).FetchAsync(Where("VidLog-Video/2026-10-16/发货/SF1.mp4"), local);

        Assert.False(result.Fetched);
        Assert.False(result.Missing, "摸都摸不到，那时**不能**说「没有这一条」");
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
        Assert.False(File.Exists(local));
    }

    [Fact]
    public async Task 本机磁盘那一档_取回直接说没有_不去归档根下瞎找()
    {
        // 这一档下「本机那一份**就是**归档层那一份」，所以「本机没有」= 哪儿都没有。
        // ⚠️ 不特判的话它会去 `<根>\archive\…` 里翻（那个目录根本不存在），
        // 然后回一句「归档层访问不了」—— 把用户引向「是不是盘掉了」这个错方向。
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.ArchiveRoot);

        var local = System.IO.Path.Combine(dir.Path, "本机", "SF1.mp4");
        var result = await new DirectoryArchiveBackend(dir.Path, ArchiveBackendKind.LocalDisk)
            .FetchAsync(Where("VidLog-Video/2026-10-16/发货/SF1.mp4"), local);

        Assert.True(result.Missing);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task 本机已经有那一份了_取回不许把它盖掉()
    {
        // ⚠️ 这条挡的是「拿归档层上那一份（可能是老的、可能是别人的）覆盖掉本机这一份」。
        // 两份内容不同就意味着至少有一份要人来判 —— 静默盖掉 = 丢证据。
        using var dir = new TempDir();
        var nas = System.IO.Path.Combine(dir.Path, "nas");
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Directory);

        var location = Where("VidLog-Video/2026-10-16/发货/SF1.mp4");
        var onNas = System.IO.Path.Combine(nas, location.Value);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(onNas)!);
        await File.WriteAllBytesAsync(onNas, [9, 9, 9]);

        var local = System.IO.Path.Combine(dir.Path, "本机", "SF1.mp4");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(local)!);
        await File.WriteAllBytesAsync(local, [1, 1, 1]);

        var result = await backend.FetchAsync(location, local);

        Assert.False(result.Fetched);
        Assert.Equal([1, 1, 1], await File.ReadAllBytesAsync(local));
        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(onNas));
    }

    // ─────────────────────────────────────────────
    // relay：失败要看得见，但绝不拖垮本机那一份
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 发布失败会记住原因并留一条日志()
    {
        var logger = new CapturingLogger();
        using var published = new TempStore();
        var relay = new ArchiveRelay(new AlwaysFailingPublisher(), "NAS", published.Store, published.Failures, logger: logger);

        var result = await relay.PublishAsync("e-000", Where("e.mp4"), "local.mp4");

        Assert.False(result.Published);
        Assert.NotNull(relay.LastFailure);
        Assert.Contains("炸了", relay.LastFailure);

        // 留痕（AGENTS.md §6）：这条失败**不影响**本机那一份与索引，
        // 但要能回答「这条为什么还没上归档层」。
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Contains("NAS", entry.Message);
    }

    // ─────────────────────────────────────────────
    // 多根：一个满了就换下一个（设计图 `_43` 的「录像备份位置」表）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 回查认得出第二档上的那一份()
    {
        // ⚠️ 这条有**真后果**：只看第一个根的话，用户换过一次 NAS 之后
        // 老录像的回查会一路说「找不到」—— 而裁判断它「找不到 ⇒ 不删」（I8），
        // 于是清理**永远清不掉任何东西**，盘一直满着，用户完全不知道为什么。
        using var dir = new TempDir();
        var old = System.IO.Path.Combine(dir.Path, "老NAS");
        var current = System.IO.Path.Combine(dir.Path, "新NAS");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(current);

        var location = Where("2026/09/30/SF1/e-000.mp4");
        var onOld = System.IO.Path.Combine(old, location.Value);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(onOld)!);
        await File.WriteAllBytesAsync(onOld, [1, 2, 3]);

        var backend = new DirectoryArchiveBackend([current, old], ArchiveBackendKind.Directory);

        var verify = await backend.VerifyAsync(location);

        Assert.True(verify.Exists, "第二个根上有就算有");
        Assert.False(verify.CouldNotVerify);

        // 首选仍是第一个（新录像该发到那儿去），只是**回查**要挨个试。
        Assert.Equal(current, backend.Root);
        Assert.Equal<string>([current, old], backend.Roots);
    }

    [Fact]
    public async Task 摸得到目录但那一份不在才算不存在_一个根都摸不到是查不了()
    {
        using var dir = new TempDir();
        var reachable = System.IO.Path.Combine(dir.Path, "nas");
        Directory.CreateDirectory(reachable);
        var gone = System.IO.Path.Combine(dir.Path, "没插的盘", "nas");
        var location = Where("2026/09/30/e-000.mp4");

        // 有一个根摸得到、只是那一份不在 ⇒ 「不存在」（可以走「唯一副本」那段判定）。
        var partial = await new DirectoryArchiveBackend([gone, reachable], ArchiveBackendKind.Directory)
            .VerifyAsync(location);

        Assert.False(partial.Exists);
        Assert.False(partial.CouldNotVerify, "目录摸得到、文件不在 —— 这才是「不存在」");

        // 一个根都摸不到 ⇒ 「查不了」。把这种情况当成「不存在」会删掉唯一一份。
        var none = await new DirectoryArchiveBackend(
            [gone, System.IO.Path.Combine(dir.Path, "也没有", "nas")],
            ArchiveBackendKind.Directory).VerifyAsync(location);

        Assert.False(none.Exists);
        Assert.True(none.CouldNotVerify);
    }

    [Fact]
    public async Task 发布按顺序试_第一档写不进去就用下一档()
    {
        // 图上原话「NAS 满时自动切换到下一个」。只试第一个的话，一个满了的
        // NAS 会让**所有**后续录像都发不出去，而那时本机那一份就再也不能被清理 ——
        // 盘只会越来越满。
        using var dir = new TempDir();
        var local = System.IO.Path.Combine(dir.Path, "local.mp4");
        await File.WriteAllBytesAsync(local, [1, 2, 3, 4]);

        // 「写不进去」用**同名文件**造：目标根那儿躺着个文件，
        // `Directory.CreateDirectory` 就过不去（真盘满了是同样的失败路径）。
        var blocked = System.IO.Path.Combine(dir.Path, "满NAS");
        var ok = System.IO.Path.Combine(dir.Path, "空NAS");
        await File.WriteAllTextAsync(blocked, "这里不是目录");

        var location = Where("2026/09/30/e-000.mp4");
        var result = await new DirectoryArchiveBackend([blocked, ok], ArchiveBackendKind.Directory)
            .PublishAsync(location, local);

        Assert.True(result.Published, result.FailureReason);
        Assert.True(File.Exists(System.IO.Path.Combine(ok, location.Value)), "落到下一档上");
    }

    [Fact]
    public async Task 每一档都写不进去时_发布失败_但绝不动本机那一份()
    {
        using var dir = new TempDir();
        var local = System.IO.Path.Combine(dir.Path, "local.mp4");
        await File.WriteAllBytesAsync(local, [1, 2, 3, 4]);

        var a = System.IO.Path.Combine(dir.Path, "满A");
        var b = System.IO.Path.Combine(dir.Path, "满B");
        await File.WriteAllTextAsync(a, "x");
        await File.WriteAllTextAsync(b, "x");

        var result = await new DirectoryArchiveBackend([a, b], ArchiveBackendKind.Directory)
            .PublishAsync(Where("2026/09/30/e-000.mp4"), local);

        Assert.False(result.Published);

        // 两档**分别**说了原因 —— 合成一句「发布失败」的话，
        // 用户不知道是 NAS 满了、还是两台都掉线了。
        Assert.Contains("满A", result.FailureReason);
        Assert.Contains("满B", result.FailureReason);

        // ⚠️ 发不出去**绝不能**动本机那一份：它现在是唯一副本（I8 的前提）。
        Assert.True(File.Exists(local));
        Assert.Equal(4, (await File.ReadAllBytesAsync(local)).Length);
    }

    [Fact]
    public async Task 归档层是本机磁盘时_发布是空操作()
    {
        // 本机那一份**就是**归档层那一份（它在 `<root>\archive` 里），没什么可发的。
        using var dir = new TempDir();
        var local = System.IO.Path.Combine(dir.Path, "local.mp4");
        await File.WriteAllBytesAsync(local, [1]);

        var result = await new DirectoryArchiveBackend(dir.Path, ArchiveBackendKind.LocalDisk)
            .PublishAsync(Where("2026/09/30/e-000.mp4"), local);

        Assert.True(result.Published);
        Assert.False(
            File.Exists(System.IO.Path.Combine(dir.Path, "2026", "09", "30", "e-000.mp4")),
            "本机这一档不该真的去拷一份到归档根下");
    }

    [Fact]
    public async Task 一个根都没配时发布说的是没有可写的目录()
    {
        using var dir = new TempDir();
        var local = System.IO.Path.Combine(dir.Path, "local.mp4");
        await File.WriteAllBytesAsync(local, [1]);

        // 设置页每敲一个字符都会重算一次后端对象 —— 那时可能一个路径都还没有。
        var result = await new DirectoryArchiveBackend(Array.Empty<string>(), ArchiveBackendKind.Directory)
            .PublishAsync(Where("2026/09/30/e-000.mp4"), local);

        Assert.False(result.Published);
        Assert.Contains("没有配置可写的目录", result.FailureReason);

        // ⚠️ 而且**不许崩**：这个对象在「归档层配了一半」的时候就会被建出来，
        // 抛的话界面还没画出来就先没了。
        Assert.Equal(
            string.Empty,
            new DirectoryArchiveBackend(Array.Empty<string>(), ArchiveBackendKind.Directory).Root);
    }

    /// <summary>把日志收进内存，供断言。</summary>
    private sealed class CapturingLogger : IAppLogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void Log(LogLevel level, string category, string message) =>
            Entries.Add((level, message));

        public void Log(
            LogLevel level, string category, string message,
            IReadOnlyDictionary<string, object?> data) =>
            Entries.Add((level, message));
    }

    [Fact]
    public async Task 发布成功会清掉上一次的失败()
    {
        using var published = new TempStore();
        var relay = new ArchiveRelay(new AlwaysFailingPublisher(), "NAS", published.Store, published.Failures);

        await relay.PublishAsync("a-000", Where("a.mp4"), "x.mp4");
        Assert.NotNull(relay.LastFailure);

        var ok = new ArchiveRelay(new SucceedingPublisher(), "NAS", published.Store, published.Failures);
        await ok.PublishAsync("a-000", Where("a.mp4"), "x.mp4");

        Assert.Null(ok.LastFailure);
        Assert.Equal("a.mp4", ok.LastPublished);
    }

    // ─────────────────────────────────────────────
    // T18：记账点在**咽喉**上，不在两个调用点各写一遍
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 发布成功会往已归档账上追加一条()
    {
        using var published = new TempStore();
        var relay = new ArchiveRelay(new SucceedingPublisher(), "NAS", published.Store, published.Failures);

        await relay.PublishAsync("e-000", Where("e.mp4"), "local.mp4");

        var anchors = await published.Store.LoadAnchorMapAsync();

        // 记账的**唯一**理由：清理层要拿它当时间锚。所以断言直接落在锚表上，
        // 而不是「文件里有没有行」—— 后者是「有没有东西」而不是「是不是我要的那个」。
        Assert.True(anchors.ContainsKey("e-000"));
    }

    [Fact]
    public async Task 发布失败一条都不记_否则等于把唯一副本放行()
    {
        // ⚠️ 这是本组里最要紧的那条反证。记账写在「成功」那一支上是**承重的**：
        // 写错成「无论成败都记」的话，归档层上根本没有那一份，
        // 而清理层会以为它有第二份 ⇒ **把唯一副本删掉**。
        using var published = new TempStore();
        var relay = new ArchiveRelay(new AlwaysFailingPublisher(), "NAS", published.Store, published.Failures);

        await relay.PublishAsync("e-000", Where("e.mp4"), "local.mp4");

        Assert.Empty(await published.Store.LoadAnchorMapAsync());
    }

    [Fact]
    public async Task 没有失败账本的那一档_失败照样如实返回_只是不记账()
    {
        // ⚠️ 2026-10-15 需求方裁定「甲」：网盘那一档传 `failures: null`。
        // 那一档的失败由 `CloudUploadService.UploadQueue` 负责 —— 到点重试，
        // 「百度网盘上传」页上还有放弃 / 恢复，是用户**真能操作**的那一个。
        // 而网盘那条路上**没有任何代码去销这本账** ⇒ 记了就永远挂在设置页上
        //（本机实测：2026-10-13 的两条测试残留挂到今天，造出它的 bug 10-14 就修了）。
        using var published = new TempStore();
        var logger = new CapturingLogger();
        var relay = new ArchiveRelay(
            new AlwaysFailingPublisher(), "百度网盘", published.Store, failures: null, logger);

        var result = await relay.PublishAsync("e-000", Where("e.mp4"), "local.mp4");

        // ① 失败**不许被吞掉**。甲去掉的是「记这本账」，不是「不说」——
        //    结果、原因、日志三样一个字都不能少（否则网盘失败会变成静默的）。
        Assert.False(result.Published);
        Assert.Contains("归档层炸了", result.FailureReason);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warn);

        // ② 账本那**两处都空着**：落盘的那本、以及内存里的 `LastFailure`。
        //    ⚠️ 只关掉一半就是白改 —— 设置页的黄条读的正是 `LastFailure`。
        Assert.Null(relay.LastFailure);
        Assert.Empty(published.Failures.Outstanding);

        // ③ 补发那一路也一样空转（没有账本就没有要补的东西）。
        Assert.Equal(0, await relay.RetryOutstandingAsync(_ => "local.mp4"));
    }

    [Fact]
    public async Task 同一条发布两次_锚取最早的那次_不许往后挪()
    {
        // ⚠️ 与回执表相反（那边是「后写的胜出」）。回执敢那么做是因为重复 commit
        // 走「原样再给一份」、两行一样；而重复发布的**时刻不同**，
        // 取后者会把起算点往后推 ⇒ 那条录像比它该被清的时刻更晚才能清。
        // 起算点的意思是「**第一次**在别处有了第二份」。
        using var published = new TempStore();
        var relay = new ArchiveRelay(new SucceedingPublisher(), "NAS", published.Store, published.Failures);

        var first = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var later = first.AddDays(10);

        await published.Store.AppendAsync(new PublishedRecord("e-000", first, "e.mp4"));
        await published.Store.AppendAsync(new PublishedRecord("e-000", later, "e.mp4"));

        var anchors = await published.Store.LoadAnchorMapAsync();

        Assert.Equal(first, anchors["e-000"]);
    }

    // ─────────────────────────────────────────────
    // T23-B：上次没发上去的那些，启动时补发一次
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 上次没发上去的_重启之后重发了一次_发成了那笔账就清了()
    {
        // ⚠️ 这条就是 T23-B 的验收本身（清单原话：「让发布失败 → 重启 →
        // 断言**重发了一次**且成功后失败表清空」）。
        //
        // 「重启」在这里是**字面意思**：另开一个 ArchiveFailureLog 指向同一个文件，
        // 内存全丢、文件还在。少了这一步，这条测的只是「同一个对象里还能重发一遍」——
        // 而缺陷原样恰恰是「重启之后再也不会试第二次」。
        using var published = new TempStore();
        using var dir = new TempDir();
        var location = Where("2026/10/06/SF1/e-000.mp4");
        var local = System.IO.Path.Combine(dir.Path, "e-000.mp4");
        await File.WriteAllTextAsync(local, "evidence");

        // 第一次：收尾那一刻发失败，欠账落盘。
        var first = new ArchiveRelay(
            new AlwaysFailingPublisher(), "NAS", published.Store, published.Failures);
        await first.PublishAsync("e-000", location, local);

        // —— 重启 ——
        var reopened = published.ReopenFailures();

        // 前提：确实欠着。少了这句，下面「清了」可能本来就是空的。
        Assert.Single(reopened.Outstanding);

        var publisher = new CountingPublisher();
        var second = new ArchiveRelay(publisher, "NAS", published.Store, reopened);

        var attempted = await second.RetryOutstandingAsync(
            rel => rel == location.Value ? local : null);

        Assert.Equal(1, attempted);
        Assert.Equal(1, publisher.Count);
        Assert.Equal(location.Value, publisher.LastLocation);
        Assert.Empty(reopened.Outstanding);

        // ⚠️ 发成功**不只是撤欠账**，还要记上「已归档」那个锚 —— 清理层只认锚，
        // 少了它那条录像会被**永久豁免**（盘满，且不报错）。
        Assert.True((await published.Store.LoadAnchorMapAsync()).ContainsKey("e-000"));

        // 再重启一次：账上已经没有它了（这条挡的是「只撤内存、没落盘」）。
        Assert.Empty(published.ReopenFailures().Outstanding);
    }

    [Fact]
    public async Task 本机那一份已经不在了_跳过_但账不许划掉()
    {
        // 没有东西可发。⚠️ 跳过**不等于**把账划掉：划掉的话界面会以为「已经没事了」，
        // 而那条录像**从头到尾都没到过归档层** —— 用户可能因此手动删掉唯一那一份。
        using var published = new TempStore();
        using var dir = new TempDir();

        var first = new ArchiveRelay(
            new AlwaysFailingPublisher(), "NAS", published.Store, published.Failures);
        await first.PublishAsync(
            "e-000", Where("e-000.mp4"), System.IO.Path.Combine(dir.Path, "早就被删了.mp4"));

        var reopened = published.ReopenFailures();
        var publisher = new CountingPublisher();
        var logger = new CapturingLogger();
        var second = new ArchiveRelay(publisher, "NAS", published.Store, reopened, logger);

        // 本机那份不在了 ⇒ 生产上这就是 `ResolveOrActive` 指到一个不存在的文件。
        var attempted = await second.RetryOutstandingAsync(
            _ => System.IO.Path.Combine(dir.Path, "早就被删了.mp4"));

        Assert.Equal(0, attempted);
        Assert.Equal(0, publisher.Count);
        Assert.Single(reopened.Outstanding);

        // 而且这件事要留痕：剩下几条、跳过了几条 —— 否则「补发过了但什么都没发生」
        // 在日志上跟「根本没跑」长得一样。
        Assert.Contains(logger.Entries, e => e.Message.Contains("本机那一份已经不在了"));
    }

    [Fact]
    public async Task 没欠账的时候_一步都不动_连本机路径都不解析()
    {
        // ⚠️ 这是 T23-B 里**最要紧的克制**（清单原话：「只针对有一张落盘失败表的那些，
        // **不要全库扫** —— 全库扫在归档层是慢速 NAS 时会拖死启动」）。
        //
        // 正常机器上这本账就是空的，所以这一趟必须是**纯空转**：一次路径解析、
        // 一次发布都不许有。把这两件事钉住的办法是让它们各自计数、断言恰好为 0。
        //
        // ⚠️ **「绝不扫全库」这一半这条测不到，它是结构保证的**：`ArchiveRelay` 的
        // 构造函数里根本没有索引、也没有 `StorageLocations`，它手上只有这本失败账
        // 和一个 `IArchivePublisher` —— 想扫全库也没得扫。所以那条约束不是靠「记得别扫」，
        // 是靠这个类**拿不到那个东西**。
        using var published = new TempStore();
        var publisher = new CountingPublisher();
        var logger = new CapturingLogger();
        var relay = new ArchiveRelay(publisher, "NAS", published.Store, published.Failures, logger);

        var resolved = 0;
        var attempted = await relay.RetryOutstandingAsync(_ => { resolved++; return "x.mp4"; });

        Assert.Equal(0, attempted);
        Assert.Equal(0, resolved);
        Assert.Equal(0, publisher.Count);

        // 而且**一条日志都不记**：正常机器上每次启动都刷一行的话，
        // 真出事那天那一行会被自己的噪音淹掉（§6.1）。
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task 补发还是失败_欠账照旧留着_并且说得出还剩几条()
    {
        // 补发不是「再试一次就一定能成」—— NAS 可能还掉着。这时**不许把账划掉**，
        // 否则用户重启一次就再也看不到这条了（那正是 T23-A 修的洞反过来）。
        using var published = new TempStore();
        using var dir = new TempDir();
        var local = System.IO.Path.Combine(dir.Path, "e-000.mp4");
        await File.WriteAllTextAsync(local, "evidence");

        var first = new ArchiveRelay(
            new AlwaysFailingPublisher(), "NAS", published.Store, published.Failures);
        await first.PublishAsync("e-000", Where("e-000.mp4"), local);

        var reopened = published.ReopenFailures();
        var logger = new CapturingLogger();
        var second = new ArchiveRelay(new AlwaysFailingPublisher(), "NAS", published.Store, reopened, logger);

        var attempted = await second.RetryOutstandingAsync(_ => local);

        Assert.Equal(1, attempted);
        var entry = Assert.Single(reopened.Outstanding);
        Assert.Equal("e-000", entry.EvidenceId);

        // 收尾那行要说清「还剩几条」，且是 Warn —— 这是用户唯一的线索。
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warn && e.Message.Contains("还有 1 条没上去"));
    }

    [Fact]
    public async Task 一条坏账不许把整趟补发拖垮()
    {
        // 账是**落盘**文件，会进诊断包、可能被人手改。一行 `location` 写成 UNC
        // （`\\nas\x.mp4`，`RelativePath.Parse` 会抛）就能让整趟补发在它身上死掉 ——
        // 而排在它后面的那些本来是发得上去的。所以每条各自 try/catch，坏的**只跳它自己**。
        using var published = new TempStore();
        using var dir = new TempDir();
        var good = System.IO.Path.Combine(dir.Path, "good.mp4");
        await File.WriteAllTextAsync(good, "evidence");

        var first = new ArchiveRelay(
            new AlwaysFailingPublisher(), "NAS", published.Store, published.Failures);

        // 一条真欠账，加一条手改坏的（顺序刻意让坏的排在后面）。
        await first.PublishAsync("好", Where("good.mp4"), good);
        await published.Failures.NoteFailedAsync(new ArchiveFailureRecord(
            "坏", @"\\nas\手改坏的.mp4", "手改文件写坏了", DateTimeOffset.Now,
            ArchiveFailureState.Failed));

        var reopened = published.ReopenFailures();

        // 前提：两条都读进来了。少了这句，下面「好那条发出去了」可能只是因为它压根不在账上。
        Assert.Equal(2, reopened.Outstanding.Count);

        var publisher = new CountingPublisher();
        var logger = new CapturingLogger();
        var second = new ArchiveRelay(publisher, "NAS", published.Store, reopened, logger);

        var attempted = await second.RetryOutstandingAsync(
            rel => System.IO.Path.Combine(dir.Path, rel));

        // ⚠️ 断言的是「好那条**照样发出去了**」—— 这才是这条的存在理由。
        Assert.Equal(1, attempted);
        Assert.Equal("good.mp4", publisher.LastLocation);

        // 坏的那条留痕、且仍在账上（它不是「已经发过了」，是「这条只有本机一份」）。
        Assert.Contains(logger.Entries, e => e.Message.Contains("补发这一条时出错"));
        Assert.Equal("坏", Assert.Single(reopened.Outstanding).EvidenceId);
    }

    /// <summary>一次性的 <c>published.jsonl</c>（临时目录，用完删）。</summary>
    private sealed class TempStore : IDisposable
    {
        private readonly string _root;

        public TempStore()
        {
            _root = Path.Combine(Path.GetTempPath(), "vidlog-published-" + Guid.NewGuid().ToString("N"));
            Store = new PublishedStore(Path.Combine(_root, "published.jsonl"));
            Failures = new ArchiveFailureLog(Path.Combine(_root, "archive-failures.jsonl"));
        }

        public PublishedStore Store { get; }

        /// <summary>T23-A 的那本「没发上去」的账 —— 与 <see cref="Store"/> **方向相反**。</summary>
        public ArchiveFailureLog Failures { get; }

        /// <summary>再开一份**指向同一个目录**的账，用来演「重启」（内存全丢、文件还在）。</summary>
        public ArchiveFailureLog ReopenFailures() =>
            new(Path.Combine(_root, "archive-failures.jsonl"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class AlwaysFailingPublisher : IArchivePublisher
    {
        public Task<ArchivePublishResult> PublishAsync(
            RelativePath location, string localPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(ArchivePublishResult.Failed("归档层炸了"));
    }

    private sealed class SucceedingPublisher : IArchivePublisher
    {
        public Task<ArchivePublishResult> PublishAsync(
            RelativePath location, string localPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(ArchivePublishResult.Ok);
    }

    /// <summary>发得成，但**数着发了几个**（T23-B 要断言的正是这个数）。</summary>
    private sealed class CountingPublisher : IArchivePublisher
    {
        public int Count { get; private set; }

        public string? LastLocation { get; private set; }

        public Task<ArchivePublishResult> PublishAsync(
            RelativePath location, string localPath, CancellationToken cancellationToken = default)
        {
            Count++;
            LastLocation = location.Value;
            return Task.FromResult(ArchivePublishResult.Ok);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vidlog-archive-" + Guid.NewGuid().ToString("N"));

        public string ArchiveRoot => System.IO.Path.Combine(Path, "archive");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            // ⚠️ **宽着接**（与其它测试文件里的 TempDir 同一个口径）：
            // Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`。
            // 清理临时目录失败不该让测试红 —— 那不是被测行为。
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
