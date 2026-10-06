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
    public void 目录型只认_NAS_与挂载盘()
    {
        // 规格 §3.4.6：「NAS 与挂载盘的差别只在要不要输凭据，实现上是两档」。
        Assert.True(new ArchiveTarget(ArchiveBackendKind.Nas).IsDirectoryType);
        Assert.True(new ArchiveTarget(ArchiveBackendKind.MountedDrive).IsDirectoryType);

        Assert.False(new ArchiveTarget(ArchiveBackendKind.LocalDisk).IsDirectoryType);
        Assert.False(new ArchiveTarget(ArchiveBackendKind.Cloud).IsDirectoryType);
    }

    [Fact]
    public void 只有本机磁盘那一档不给清理()
    {
        // 规格 §3.5.1：判据是「归档层是不是**就在这台电脑上**」——
        // NAS 与挂载盘都算「在别处」（哪怕挂载成 Z:\ 看着就是个本地盘）。
        Assert.False(new ArchiveTarget(ArchiveBackendKind.LocalDisk).AllowsCleanup);

        Assert.True(new ArchiveTarget(ArchiveBackendKind.Nas, @"\\nas\vidlog").AllowsCleanup);
        Assert.True(new ArchiveTarget(ArchiveBackendKind.MountedDrive, @"Z:\vidlog").AllowsCleanup);
        Assert.True(new ArchiveTarget(ArchiveBackendKind.Cloud).AllowsCleanup);
    }

    [Fact]
    public void 目录型没填路径或填了相对路径都算没配好()
    {
        Assert.NotNull(new ArchiveTarget(ArchiveBackendKind.Nas).ConfigurationProblem);
        Assert.NotNull(new ArchiveTarget(ArchiveBackendKind.Nas, "   ").ConfigurationProblem);
        Assert.NotNull(new ArchiveTarget(ArchiveBackendKind.MountedDrive, "vidlog").ConfigurationProblem);

        Assert.Null(new ArchiveTarget(ArchiveBackendKind.Nas, @"\\nas\vidlog").ConfigurationProblem);
        Assert.Null(new ArchiveTarget(ArchiveBackendKind.MountedDrive, @"Z:\vidlog").ConfigurationProblem);

        // 本机磁盘不需要配路径 —— 它就在本机。
        Assert.Null(new ArchiveTarget(ArchiveBackendKind.LocalDisk).ConfigurationProblem);
    }

    [Fact]
    public void 认不出的档位回落到本机磁盘_朝少删的那头落()
    {
        var target = ArchiveTarget.FromConfig((ArchiveBackendKind)99, @"Z:\vidlog");

        Assert.Equal(ArchiveBackendKind.LocalDisk, target.Kind);
        Assert.False(target.AllowsCleanup);
    }

    [Fact]
    public void 跨网能力要如实说()
    {
        // 规格 §2.3：产品**不得承诺做不到的事**。
        Assert.Contains("跨网", new ArchiveTarget(ArchiveBackendKind.Cloud).Reachability);
        Assert.Contains("只能内网", new ArchiveTarget(ArchiveBackendKind.LocalDisk).Reachability);
        Assert.Contains("只能内网", new ArchiveTarget(ArchiveBackendKind.MountedDrive, @"Z:\v").Reachability);
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
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Nas);

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
    public async Task 归档层上已经有一份就不重发()
    {
        using var dir = new TempDir();
        var nas = System.IO.Path.Combine(dir.Path, "nas");
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Nas);

        var local = System.IO.Path.Combine(dir.Path, "local.mp4");
        await File.WriteAllBytesAsync(local, [1, 2, 3]);
        await backend.PublishAsync(Where("e.mp4"), local);

        // 把归档层那一份改掉，再发一次：**内容不该被覆盖**。
        // 归档路径是「单号 + 会话 + 序号 + 时间」推出来的，同名就是同一条录像；
        // 覆盖它等于抹掉归档层上可能更早、更可信的那一份。
        var copied = System.IO.Path.Combine(nas, "e.mp4");
        await File.WriteAllBytesAsync(copied, [9, 9, 9]);

        var result = await backend.PublishAsync(Where("e.mp4"), local);

        Assert.True(result.Published);
        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(copied));
    }

    [Fact]
    public async Task 归档目录不可达时发布失败_但本机那一份一个字都没动()
    {
        using var dir = new TempDir();
        // 拿一个**文件**当归档根：建目录必然失败，等价于「盘符掉了 / 没权限」。
        var blocker = System.IO.Path.Combine(dir.Path, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x");

        var backend = new DirectoryArchiveBackend(
            System.IO.Path.Combine(blocker, "nas"), ArchiveBackendKind.Nas);

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
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Nas);

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

        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Nas);

        var missing = await backend.VerifyAsync(Where("nope.mp4"));
        Assert.False(missing.Exists);
        Assert.False(missing.CouldNotVerify, "目录摸得到、文件不在 —— 这才是「不存在」");
        Assert.Null(missing.FailureReason);

        // 归档根整个是空的/不在了 → 「查不了」，不是「不存在」。
        var unreachable = await new DirectoryArchiveBackend(
            System.IO.Path.Combine(dir.Path, "不存在的盘", "nas"),
            ArchiveBackendKind.Nas).VerifyAsync(Where("e.mp4"));

        Assert.False(unreachable.Exists);
        Assert.True(unreachable.CouldNotVerify, "目录都摸不到，那时**不能**说「这份不存在」");
    }

    [Fact]
    public async Task 回查认得出刚发过去的那一份()
    {
        using var dir = new TempDir();
        var nas = System.IO.Path.Combine(dir.Path, "nas");
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Nas);

        var local = System.IO.Path.Combine(dir.Path, "local.mp4");
        await File.WriteAllBytesAsync(local, [1, 2, 3]);
        var location = Where("2026/09/27/SF1/e-000.mp4");
        await backend.PublishAsync(location, local);

        var verify = await backend.VerifyAsync(location);

        Assert.True(verify.Exists);
        Assert.False(verify.CouldNotVerify);
    }

    // ─────────────────────────────────────────────
    // relay：失败要看得见，但绝不拖垮本机那一份
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 发布失败会记住原因并留一条日志()
    {
        var logger = new CapturingLogger();
        using var published = new TempStore();
        var relay = new ArchiveRelay(new AlwaysFailingPublisher(), "NAS", published.Store, logger: logger);

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

        var backend = new DirectoryArchiveBackend([current, old], ArchiveBackendKind.Nas);

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
        var partial = await new DirectoryArchiveBackend([gone, reachable], ArchiveBackendKind.Nas)
            .VerifyAsync(location);

        Assert.False(partial.Exists);
        Assert.False(partial.CouldNotVerify, "目录摸得到、文件不在 —— 这才是「不存在」");

        // 一个根都摸不到 ⇒ 「查不了」。把这种情况当成「不存在」会删掉唯一一份。
        var none = await new DirectoryArchiveBackend(
            [gone, System.IO.Path.Combine(dir.Path, "也没有", "nas")],
            ArchiveBackendKind.Nas).VerifyAsync(location);

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
        var result = await new DirectoryArchiveBackend([blocked, ok], ArchiveBackendKind.Nas)
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

        var result = await new DirectoryArchiveBackend([a, b], ArchiveBackendKind.Nas)
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
        var result = await new DirectoryArchiveBackend(Array.Empty<string>(), ArchiveBackendKind.Nas)
            .PublishAsync(Where("2026/09/30/e-000.mp4"), local);

        Assert.False(result.Published);
        Assert.Contains("没有配置可写的目录", result.FailureReason);

        // ⚠️ 而且**不许崩**：这个对象在「归档层配了一半」的时候就会被建出来，
        // 抛的话界面还没画出来就先没了。
        Assert.Equal(
            string.Empty,
            new DirectoryArchiveBackend(Array.Empty<string>(), ArchiveBackendKind.Nas).Root);
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
        var relay = new ArchiveRelay(new AlwaysFailingPublisher(), "NAS", published.Store);

        await relay.PublishAsync("a-000", Where("a.mp4"), "x.mp4");
        Assert.NotNull(relay.LastFailure);

        var ok = new ArchiveRelay(new SucceedingPublisher(), "NAS", published.Store);
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
        var relay = new ArchiveRelay(new SucceedingPublisher(), "NAS", published.Store);

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
        var relay = new ArchiveRelay(new AlwaysFailingPublisher(), "NAS", published.Store);

        await relay.PublishAsync("e-000", Where("e.mp4"), "local.mp4");

        Assert.Empty(await published.Store.LoadAnchorMapAsync());
    }

    [Fact]
    public async Task 同一条发布两次_锚取最早的那次_不许往后挪()
    {
        // ⚠️ 与回执表相反（那边是「后写的胜出」）。回执敢那么做是因为重复 commit
        // 走「原样再给一份」、两行一样；而重复发布的**时刻不同**，
        // 取后者会把起算点往后推 ⇒ 那条录像比它该被清的时刻更晚才能清。
        // 起算点的意思是「**第一次**在别处有了第二份」。
        using var published = new TempStore();
        var relay = new ArchiveRelay(new SucceedingPublisher(), "NAS", published.Store);

        var first = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var later = first.AddDays(10);

        await published.Store.AppendAsync(new PublishedRecord("e-000", first, "e.mp4"));
        await published.Store.AppendAsync(new PublishedRecord("e-000", later, "e.mp4"));

        var anchors = await published.Store.LoadAnchorMapAsync();

        Assert.Equal(first, anchors["e-000"]);
    }

    /// <summary>一次性的 <c>published.jsonl</c>（临时目录，用完删）。</summary>
    private sealed class TempStore : IDisposable
    {
        private readonly string _root;

        public TempStore()
        {
            _root = Path.Combine(Path.GetTempPath(), "vidlog-published-" + Guid.NewGuid().ToString("N"));
            Store = new PublishedStore(Path.Combine(_root, "published.jsonl"));
        }

        public PublishedStore Store { get; }

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
