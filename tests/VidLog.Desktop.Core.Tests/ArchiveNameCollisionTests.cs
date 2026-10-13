// §四「撞名比内容」的绊线（2026-10-12）。
//
// 起因：换路径形状之后名字里**没有会话号**（`<单号>.mp4`），于是「同一个单号、同一天」
// 的第二场必然撞名。实测复现出两条**会静默丢证据**的走法（都在真 U 盘上跑过，
// 见桌面 `§四复现-结果.txt`）：
//
//   路径 a 换盘：第二场挑到另一块盘 ⇒ 那块盘上没有那个文件 ⇒ 名字照旧取 `<单号>.mp4`
//                （而索引里第一场已经指着这个名字）
//   路径 b 本机那份不在了：盘上没有 ⇒ 名字照旧 ⇒ 发到归档层时那边已经有第一场的一份
//
// 那时的表现是：**归档层上那份是第一场的**，而第二场「ArchiveComplete = True」，
// 回查也说「已备份」⇒ 清理把第二场唯一的好副本删掉。
//
// 真的部分：SessionFinalizer 全程、ArchiveLayout 相对路径、`_2` 去重、File.Move 原子占位、
//          DirectoryArchiveBackend 的 PublishAsync / VerifyAsync、两个**真的落盘根**。
// 假的部分：ffmpeg 是桩进程（中间片与成品的字节是写死的占位内容）。
//          bug 长在**名字那一层**，与片子内容无关，所以桩进程藏不住它。
//
// ⚠️ **别把落盘根钉回真 U 盘**（2026-10-12 的教训）。这三条一开始是拿 `K:\vidlog-repro`
//    写的 —— 那条路只在这台机器上、且只在 U 盘插着的时候存在。CI 机器没有 `K:` ⇒
//    `Directory.CreateDirectory` 当场抛 `DirectoryNotFoundException` ⇒ **整类三条一起红**
//    （run 38234011955 实测）。而这三条要的只是「**两个不同的落盘根**」，
//    跟那块盘是不是可移动盘**没有任何关系** ⇒ 一律走临时目录。
using System.Text;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

public class ArchiveNameCollisionTests : IDisposable
{
    /// <summary>三条用例共用的临时根。⚠️ 见文件头那条「别钉回真 U 盘」。</summary>
    private static readonly string TempRoot = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "vidlog-namecollision");

    /// <summary>这条用例造出来的那些根，收尾时删掉。</summary>
    private readonly List<string> _created = [];

    private static readonly DateTimeOffset Day1 =
        new(2026, 10, 12, 10, 0, 0, TimeSpan.FromHours(8));

    private const long Gb = 1024L * 1024 * 1024;
    private static readonly WaybillNumber Waybill = WaybillNumber.Parse("SF9999999999");

    // ─────────────────────────────────────────────
    // 路径 b：本机那份不在了（换机 / 重装 / 人工清了保存目录）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 本机那份不在了_第二场也要落到_2_而不是复用第一场的名字()
    {
        var root = NewRoot("path-b");
        var save = System.IO.Path.Combine(root, "save");
        var nas = System.IO.Path.Combine(root, "nas");
        Directory.CreateDirectory(save);
        Directory.CreateDirectory(nas);

        var index = new IndexSpy();
        var runner = new StubRunner();
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Directory);
        var failures = NewFailureLog(root);
        var relay = NewRelay(backend, failures, root);
        var finalizer = NewFinalizer(StorageLocations.Single(save), runner, index, relay);

        // ── 第一场 ──
        runner.Payload = "第一场的字节";
        var r1 = await finalizer.FinalizeAsync(
            "sess-1", Waybill, "dev-1", [Segment(root, 0)], StopReason.Manual,
            businessType: BusinessType.Outbound);

        Assert.True(r1.Succeeded, r1.FailureReason);
        var e1 = Assert.Single(index.Entries);
        var local1 = System.IO.Path.Combine(save, e1.Location.Value);

        Assert.True(File.Exists(local1), "第一场本机那份应当落盘了");
        Assert.Equal(
            "第一场的字节",
            await File.ReadAllTextAsync(System.IO.Path.Combine(nas, e1.Location.Value)));

        // ── 本机那份被弄没了 ──
        // ⚠️ 手工删：清理有「最近 24 小时内录的一律不动」的地板（`CleanupPolicy.cs:164-170`），
        //    所以这个状态**走不到**清理那条路上，只能靠换机 / 重装 / 人工清目录。
        File.Delete(local1);

        // ── 第二场：同一天、同一个单号、内容不同 ──
        runner.Payload = "第二场的字节";
        var r2 = await finalizer.FinalizeAsync(
            "sess-2", Waybill, "dev-1", [Segment(root, 1)], StopReason.Manual,
            businessType: BusinessType.Outbound);

        Assert.True(r2.Succeeded, r2.FailureReason);
        var e2 = index.Entries[^1];

        // ★ 本轮的修法：索引里那个名字有主 ⇒ 换 `_2`（从前是「盘上没有 ⇒ 名字照旧」）
        Assert.NotEqual(e1.Location.Value, e2.Location.Value);
        Assert.EndsWith("_2.mp4", e2.Location.Value, StringComparison.Ordinal);

        // ★★ 归档层上**两份都在**，各是各的字节 —— 这才是「双份」的意思
        Assert.Equal(
            "第一场的字节", await File.ReadAllTextAsync(System.IO.Path.Combine(nas, e1.Location.Value)));
        Assert.Equal(
            "第二场的字节", await File.ReadAllTextAsync(System.IO.Path.Combine(nas, e2.Location.Value)));

        Assert.True(r2.ArchiveComplete, "第二场真的发上去了");

        // ★★ 回查（清理那一步的同一条路）指着的是**第二场自己那一份**
        var check = await backend.VerifyAsync(e2.Location);
        Assert.True(check.Exists);
        Assert.False(check.CouldNotVerify);

        Assert.Empty(failures.Outstanding);
    }

    // ─────────────────────────────────────────────
    // 路径 a：换盘（同一个 StorageLocations 实例，第二场挑到了另一块盘）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 换盘之后_第二场也要落到_2_而不是复用第一场的名字()
    {
        var root = NewRoot("path-a");
        var saveA = System.IO.Path.Combine(root, "saveA");
        var saveB = System.IO.Path.Combine(root, "saveB");
        var nas = System.IO.Path.Combine(root, "nas");
        Directory.CreateDirectory(saveA);
        Directory.CreateDirectory(saveB);
        Directory.CreateDirectory(nas);

        // A 先看着有空间、B 也有空间 ⇒ 按列表顺序挑第一个（A）。
        var probe = new FlipProbe();
        probe.Volumes[saveA] = new VolumeSpace(290 * Gb, 296 * Gb);
        probe.Volumes[saveB] = new VolumeSpace(290 * Gb, 296 * Gb);

        var storage = new StorageLocations([new DiskSlot(saveA), new DiskSlot(saveB)], root, probe);

        var index = new IndexSpy();
        var runner = new StubRunner();
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Directory);
        var failures = NewFailureLog(root);
        var relay = NewRelay(backend, failures, root);

        // ⚠️ **一个收尾器**，两场都走它 —— 与生产里同一个实例一致。
        var finalizer = NewFinalizer(storage, runner, index, relay);

        // ── 第一场：A 还有空间 ⇒ 落在 A ──
        runner.Payload = "第一场的字节";
        var r1 = await finalizer.FinalizeAsync(
            "sess-1", Waybill, "dev-1", [Segment(root, 0)], StopReason.Manual,
            businessType: BusinessType.Outbound);

        Assert.True(r1.Succeeded, r1.FailureReason);
        var e1 = index.Entries[^1];
        Assert.True(File.Exists(System.IO.Path.Combine(saveA, e1.Location.Value)), "第一场应当落在 A");

        // ── A 满了（真机上是「A 盘满了自动换盘」）──
        probe.Volumes[saveA] = new VolumeSpace(1 * Gb, 296 * Gb);

        // ── 第二场：A 被跳过 ⇒ 落在 B ──
        runner.Payload = "第二场的字节";
        var r2 = await finalizer.FinalizeAsync(
            "sess-2", Waybill, "dev-1", [Segment(root, 1)], StopReason.Manual,
            businessType: BusinessType.Outbound);

        Assert.True(r2.Succeeded, r2.FailureReason);
        var e2 = index.Entries[^1];

        Assert.True(
            File.Exists(System.IO.Path.Combine(saveB, e2.Location.Value)),
            "第二场应当落在 B —— 也就是「换盘」真的发生了");

        // ★ 名字必须**换一格**：B 上没有那个文件，但索引里第一场已经占了它。
        //   从前只看活动根 ⇒ 名字照旧 ⇒ 索引里两条记录共用一个 `Location`。
        Assert.NotEqual(e1.Location.Value, e2.Location.Value);
        Assert.EndsWith("_2.mp4", e2.Location.Value, StringComparison.Ordinal);

        // ★★ 归档层上两份都在、各是各的字节
        Assert.Equal(
            "第一场的字节", await File.ReadAllTextAsync(System.IO.Path.Combine(nas, e1.Location.Value)));
        Assert.Equal(
            "第二场的字节", await File.ReadAllTextAsync(System.IO.Path.Combine(nas, e2.Location.Value)));

        Assert.True(r2.ArchiveComplete);
        Assert.Empty(failures.Outstanding);
    }

    // ─────────────────────────────────────────────
    // 归档层上已经有同名的一份，而内容不是这一份（换机器 / 手上有一份老文件）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 归档层上已有同名但内容不同_判成撞名而不是已经传过了()
    {
        var root = NewRoot("conflict");
        var save = System.IO.Path.Combine(root, "save");
        var nas = System.IO.Path.Combine(root, "nas");
        Directory.CreateDirectory(save);
        Directory.CreateDirectory(nas);

        // 归档层上先躺着一份**别人的**录像，占着这个名字（内容不同）。
        var foreign = System.IO.Path.Combine(
            nas, "VidLog-Video", "2026-10-12", "发货", "SF9999999999.mp4");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(foreign)!);
        await File.WriteAllTextAsync(foreign, "别人的字节");

        var index = new IndexSpy();
        var runner = new StubRunner { Payload = "本场的字节" };
        var backend = new DirectoryArchiveBackend(nas, ArchiveBackendKind.Directory);
        var failures = NewFailureLog(root);
        var relay = NewRelay(backend, failures, root);
        var finalizer = NewFinalizer(StorageLocations.Single(save), runner, index, relay);

        var outcome = await finalizer.FinalizeAsync(
            "sess-1", Waybill, "dev-1", [Segment(root, 0)], StopReason.Manual,
            businessType: BusinessType.Outbound);

        // 收尾本身是成功的：本机这一份落盘、进索引、能播能检索。
        Assert.True(outcome.Succeeded, outcome.FailureReason);

        // ★ **但归档层那一份没上去** —— 从前这里会说「已经传过了」（Ok），
        //   而盘上那份是**别人的**。
        Assert.False(outcome.ArchiveComplete, "撞名 = 这一场没发上归档层");

        // ★★ 别人的那份**一个字节都没被覆盖**
        Assert.Equal("别人的字节", await File.ReadAllTextAsync(foreign));

        // ★★ 本机这一份还在（I2：宁可多占地方，不可少一份证据）
        var entry = Assert.Single(index.Entries);
        var local = System.IO.Path.Combine(save, entry.Location.Value);
        Assert.Equal("本场的字节", await File.ReadAllTextAsync(local));

        // ★★ 账上是**撞名**这一档（要人处理），不是「普通的没发上去」（等网络好）
        var record = Assert.Single(failures.Outstanding);
        Assert.Equal(ArchiveFailureKind.Conflict, record.Kind);

        // ★★ 补发那一路**跳过**它：试一百次也是同一个结果 ——
        //    试下去只会每次启动多一条 Warn，而这件事永远得等一个人。
        var attempted = await relay.RetryOutstandingAsync(
            path => path == entry.Location.Value ? local : null);

        Assert.Equal(0, attempted);
        Assert.Single(failures.Outstanding);   // 账没被划掉（划掉就等于说「已经双份了」）
    }

    // ─────────────────────────────────────────────

    private string NewRoot(string tag)
    {
        var dir = System.IO.Path.Combine(TempRoot, $"{tag}-{Guid.NewGuid():N}"[..16]);
        Directory.CreateDirectory(dir);
        _created.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _created)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉就留着 —— 与仓里其他临时目录同一条规矩。
            }
        }
    }

    private static ArchiveFailureLog NewFailureLog(string root) =>
        new(System.IO.Path.Combine(root, "archive-failures.jsonl"));

    private static ArchiveRelay NewRelay(
        DirectoryArchiveBackend backend, ArchiveFailureLog failures, string root) => new(
        backend,
        "NAS（测试用目录）",
        new PublishedStore(System.IO.Path.Combine(root, "published.jsonl")),
        failures);

    // ⚠️ 重载解析：`SessionFinalizer` 有一个收 `string archiveRoot` 的重载，也有一个收
    //    `StorageLocations`。两个都写出来，免得编译器挑错那个。
    private static SessionFinalizer NewFinalizer(
        StorageLocations storage, IProcessRunner runner, IRecordingIndex index, ArchiveRelay relay) =>
        new(new RemuxPipeline("ffmpeg", runner), new DecodeVerifier("ffmpeg", runner), index,
            storage, logger: null, relay: relay);

    /// <summary>造一段**真的在盘上**的源片（收尾第一步就是 `File.Exists`，不写就过不去）。</summary>
    private static SegmentProduct Segment(string root, int ordinal)
    {
        var path = System.IO.Path.Combine(root, $"segment-{ordinal:000}.mkv");
        File.WriteAllText(path, $"intermediate-mkv-{ordinal}");

        return new SegmentProduct(
            ordinal, path, Day1.AddMinutes(ordinal), Day1.AddMinutes(ordinal + 1));
    }

    // ─────────────────────────────────────────────

    private sealed class IndexSpy : IRecordingIndex
    {
        public List<RecordingEntry> Entries { get; } = [];

        public Task AddAsync(RecordingEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RecordingEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RecordingEntry>>(Entries);
    }

    /// <summary>照抄 `SessionFinalizerTests.SucceedingRunner`：只把 `-y` 后面那个路径写出来。</summary>
    private sealed class StubRunner : IProcessRunner
    {
        public string Payload { get; set; } = "bytes";

        public Task<ProcessResult> RunAsync(
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            var args = arguments.ToList();

            var yIndex = args.IndexOf("-y");
            if (yIndex >= 0 && yIndex + 1 < args.Count)
            {
                var destination = args[yIndex + 1];
                var directory = System.IO.Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(destination, Payload);
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class FlipProbe : IVolumeSpaceProbe
    {
        public Dictionary<string, VolumeSpace> Volumes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public VolumeSpace? Measure(string path) => Volumes.GetValueOrDefault(path);
    }
}
