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
/// ⚠️ 百度网盘那一档（<see cref="ArchiveBackendKind.Cloud"/>）**没有实现** ——
/// 它要走真接口、要真账号，本地一行都验不了。见 `docs/实现决策.md`。
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
        var relay = new ArchiveRelay(new AlwaysFailingPublisher(), "NAS", logger);

        var result = await relay.PublishAsync(Where("e.mp4"), "local.mp4");

        Assert.False(result.Published);
        Assert.NotNull(relay.LastFailure);
        Assert.Contains("炸了", relay.LastFailure);

        // 留痕（AGENTS.md §6）：这条失败**不影响**本机那一份与索引，
        // 但要能回答「这条为什么还没上归档层」。
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Contains("NAS", entry.Message);
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
        var relay = new ArchiveRelay(new AlwaysFailingPublisher(), "NAS");

        await relay.PublishAsync(Where("a.mp4"), "x.mp4");
        Assert.NotNull(relay.LastFailure);

        var ok = new ArchiveRelay(new SucceedingPublisher(), "NAS");
        await ok.PublishAsync(Where("a.mp4"), "x.mp4");

        Assert.Null(ok.LastFailure);
        Assert.Equal("a.mp4", ok.LastPublished);
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
