using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「本机没有 → 从归档层取回来」（甲，2026-10-16 需求方裁定）。
/// </summary>
/// <remarks>
/// <para>
/// 这一层自己不碰盘、不碰网：它把两件事凑起来 —— 「这一条该落在本机哪儿」
/// （<see cref="StorageLocations.ResolveOrActive"/>）与「归档层上有没有这一条、
/// 能不能搬回来」（<see cref="IArchiveBackend.FetchAsync"/>），外加一份**给界面读的进度**。
/// </para>
/// <para>
/// ⚠️ 所以这里验的是那几件**不会报错**的事：三态有没有判反
/// （「没有这一条」被说成「取不回来」会让用户以为录像没了）、
/// 失败有没有落到状态上（落不到的话界面停在「下载中」一辈子）、
/// 同一条会不会下两遍（点两下播放 = 两条一样的流量）。
/// </para>
/// </remarks>
public class ArchiveRetrievalTests
{
    private const string Where = "VidLog-Video/2026-10-16/发货/SF1000000001.mp4";

    [Fact]
    public async Task 取回来之后状态是已下载_进度到过一百()
    {
        using var dir = new TempDir();
        var archive = new FakeArchive
        {
            Behavior = (_, destination, progress) =>
            {
                progress?.Invoke(50, 100);
                File.WriteAllBytes(destination, [1, 2, 3]);

                return ArchiveFetchResult.Ok;
            },
        };

        var service = new ArchiveRetrievalService(archive, Storage(dir.Path));

        var destination = service.Start(Entry());

        // ⚠️ 落点必须是 `ResolveOrActive` 指的那个地方：**不是**另起一份副本。
        // 另起一份的话，检索页那边仍然判「本机没有」（它查的是同一个函数），
        // 于是每次点播放都再下一遍。
        Assert.Equal(Path.Combine(dir.Path, Where), destination);

        var status = await SettleAsync(service);

        Assert.Equal(RetrievalState.Done, status.State);
        Assert.Equal(100, status.Percent);
        Assert.True(File.Exists(destination), "取回来就得在本机那个该在的位置上出现");
    }

    [Fact]
    public async Task 归档层上没有这一条_状态是未找到_不是失败()
    {
        // ⚠️ 与「取不回来」分开：一个是「这条在哪儿都没有了」，
        // 一个是「现在问不到/搬不动」。混成一句的话，用户看到「未找到」
        // 会去查录像是不是被删了 —— 而它可能好端端在网盘上、只是刚才没连上。
        using var dir = new TempDir();
        var archive = new FakeArchive { Behavior = (_, _, _) => ArchiveFetchResult.NotFound };

        var service = new ArchiveRetrievalService(archive, Storage(dir.Path));
        service.Start(Entry());

        var status = await SettleAsync(service);

        Assert.Equal(RetrievalState.Missing, status.State);
        Assert.Null(status.Message);
    }

    [Fact]
    public async Task 取不回来时状态是失败_而且说得出原因()
    {
        using var dir = new TempDir();
        var archive = new FakeArchive
        {
            Behavior = (_, _, _) => ArchiveFetchResult.Failed("问不到百度网盘：没登录"),
        };

        var service = new ArchiveRetrievalService(archive, Storage(dir.Path));
        service.Start(Entry());

        var status = await SettleAsync(service);

        Assert.Equal(RetrievalState.Failed, status.State);
        Assert.Contains("没登录", status.Message);
    }

    [Fact]
    public async Task 归档层那头抛异常_也要落到失败状态_不能让界面干等()
    {
        // ⚠️ 这一趟跑在 `Task.Run` 里，**抛出去没人接**（会变成进程级的未观察异常）。
        // 不在这里收住的话，界面上那一行会永远停在「下载中」——
        // 用户既等不到片子，也等不到一句话。
        using var dir = new TempDir();
        var archive = new FakeArchive
        {
            Behavior = (_, _, _) => throw new IOException("盘掉了"),
        };

        var service = new ArchiveRetrievalService(archive, Storage(dir.Path));
        service.Start(Entry());

        var status = await SettleAsync(service);

        Assert.Equal(RetrievalState.Failed, status.State);
        Assert.Contains("盘掉了", status.Message);
    }

    [Fact]
    public async Task 网盘没给总长度时_不给百分比()
    {
        // ⚠️ **不许编一个百分比出来**（`total` 是 null 就一路停在某个假数字上，
        // 比没有更糟：用户会以为卡住了）。界面上那一行只写「下载中」。
        using var dir = new TempDir();
        var gate = new TaskCompletionSource<bool>();
        var reported = new TaskCompletionSource<bool>();

        var archive = new FakeArchive
        {
            Behavior = (_, destination, progress) =>
            {
                progress?.Invoke(1024, null);
                reported.TrySetResult(true);
                gate.Task.Wait();
                File.WriteAllBytes(destination, [1]);

                return ArchiveFetchResult.Ok;
            },
        };

        var service = new ArchiveRetrievalService(archive, Storage(dir.Path));
        service.Start(Entry());

        // ⚠️ 等**假归档层真的报了那一次进度**，不是等状态变成「下载中」——
        // 后者是 job 一生下来就有的状态，一等等个空，这条测试就变成了
        // 「进度那条路整条删掉也照样绿」。
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var during = service.StatusOf("e1");

        Assert.Equal(RetrievalState.Downloading, during.State);
        Assert.Null(during.Percent);

        gate.SetResult(true);

        Assert.Equal(RetrievalState.Done, (await SettleAsync(service)).State);
    }

    [Fact]
    public async Task 本机已经有那一份_一步都不动_直接算已下载()
    {
        // 界面上那一份可能刚被人放回来（另开一个窗口、或者上一次取回其实成了）。
        // 再下一遍是白花的流量，而且**会盖掉本机那一份**。
        using var dir = new TempDir();
        var destination = Path.Combine(dir.Path, Where);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, [9]);

        var archive = new FakeArchive();
        var service = new ArchiveRetrievalService(archive, Storage(dir.Path));

        Assert.Equal(destination, service.Start(Entry()));

        var status = await SettleAsync(service);

        Assert.Equal(RetrievalState.Done, status.State);
        Assert.Equal(0, archive.Calls);
        Assert.Equal([9], await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task 同一条点两下_只下一次()
    {
        // 用户点播放、看着没反应（网盘慢）又点一下 —— 那两下不该变成两趟一样的下载。
        using var dir = new TempDir();
        var gate = new TaskCompletionSource<bool>();
        var archive = new FakeArchive
        {
            Behavior = (_, destination, _) =>
            {
                gate.Task.Wait();
                File.WriteAllBytes(destination, [1]);

                return ArchiveFetchResult.Ok;
            },
        };

        var service = new ArchiveRetrievalService(archive, Storage(dir.Path));
        var entry = Entry();

        service.Start(entry);
        service.Start(entry);

        // ⚠️ 这里**不能**等「状态变成下载中」：那正是 job 一生下来的状态，
        // 一等等个空 —— 断言会跑在 `Task.Run` 还没被排上之前，`Calls` 还是 0
        //（真跑起来就是这样：单独跑红、和别的测试一起跑反而绿）。
        for (var i = 0; i < 300 && archive.Calls == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, archive.Calls);

        gate.SetResult(true);

        Assert.Equal(RetrievalState.Done, (await SettleAsync(service)).State);
        Assert.Equal(1, archive.Calls);
    }

    [Fact]
    public async Task 取回来了之后_检索页那个判据认它是本机有的()
    {
        // ⚠️ 这条是「取回这件事对下游透明」的判据：搬完这条必须与
        // 「本来就是在本机录的」**一模一样**——检索页判「成品在不在」用的是
        // `ResolveOrActive` + `File.Exists`，取回之后那两样都得为真。
        using var dir = new TempDir();
        var archive = new FakeArchive
        {
            Behavior = (_, destination, _) =>
            {
                File.WriteAllBytes(destination, [1, 2]);
                return ArchiveFetchResult.Ok;
            },
        };

        var storage = Storage(dir.Path);
        var service = new ArchiveRetrievalService(archive, storage);

        service.Start(Entry());

        await SettleAsync(service);

        Assert.True(File.Exists(storage.ResolveOrActive(Entry().Location.Value)));
    }

    [Fact]
    public async Task 取回成功与失败都留一条日志()
    {
        // 用户报「取不回来」时，诊断包里要能看出是「那边没有」还是「搬到一半断了」。
        using var dir = new TempDir();
        var logger = new CapturingLogger();
        var archive = new FakeArchive { Behavior = (_, _, _) => ArchiveFetchResult.NotFound };

        var service = new ArchiveRetrievalService(archive, Storage(dir.Path), logger);
        service.Start(Entry());

        await SettleAsync(service);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Info && e.Message.Contains("开始从归档层取回"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("归档层上没有这一条"));
    }

    // ─────────────────────────────────────────────
    // 脚手架
    // ─────────────────────────────────────────────

    /// <summary>等到这一条不再是「下载中」，把最后那个状态交回来。</summary>
    private static async Task<RetrievalStatus> SettleAsync(ArchiveRetrievalService service)
    {
        for (var i = 0; i < 300; i++)
        {
            var status = service.StatusOf("e1");

            if (status.State != RetrievalState.Downloading)
            {
                return status;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException("取回一直没结束（界面会一直停在「下载中」）");
    }

    private static StorageLocations Storage(string root) =>
        new([new DiskSlot(root)], root, new FreeProbe());

    private static RecordingEntry Entry() => new(
        "e1",
        "sess-1",
        WaybillNumber.Parse("SF1000000001"),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(1),
        RelativePath.Parse(Where),
        ContentHash.Parse(new string('a', 64)),
        "device-1");

    /// <summary>「还有的是地方」的那块盘 —— 取回这条路上挑盘根本不参与，够用就行。</summary>
    private sealed class FreeProbe : IVolumeSpaceProbe
    {
        public VolumeSpace? Measure(string path) =>
            new(500L * 1024 * 1024 * 1024, 1000L * 1024 * 1024 * 1024);
    }

    /// <summary>可编程的归档层：想回什么就回什么，顺便记下被叫过几次。</summary>
    private sealed class FakeArchive : IArchiveBackend
    {
        private int _calls;

        public ArchiveBackendKind Kind => ArchiveBackendKind.Directory;

        public int Calls => Volatile.Read(ref _calls);

        public Func<RelativePath, string, Action<long, long?>?, ArchiveFetchResult> Behavior { get; set; } =
            (_, _, _) => ArchiveFetchResult.NotFound;

        public Task<ArchiveVerifyResult> VerifyAsync(
            RelativePath location, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArchiveVerifyResult(false, null));

        public Task<ArchiveFetchResult> FetchAsync(
            RelativePath location,
            string destination,
            Action<long, long?>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);

            // 落点的父目录由**实现这一头**建（`IArchiveBackend.FetchAsync` 上写着这条约定，
            // 两个真实现都照做）。假实现漏了这一步的话，`Behavior` 往一个不存在的目录里
            // 写文件会抛，而表现是「取回失败」—— 测出来的就不是被测的那件事了。
            var parent = Path.GetDirectoryName(destination);

            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            return Task.FromResult(Behavior(location, destination, progress));
        }
    }

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

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vidlog-retrieval-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            // ⚠️ **宽着接**（与其它测试文件里的 TempDir 同一个口径）：Windows 在
            // 「文件正被另一个进程持有」时可能报 `UnauthorizedAccessException`。
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
