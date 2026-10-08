using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Export;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Search;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// I7：**交付副本不算归档层那份**（规格 §3.7）。
/// </summary>
/// <remarks>
/// <para>
/// 「交付副本」= 导出给别人的那个文件。它落在**归档根之外**的用户自选路径上，
/// 而导出器**一个字节都不往索引/标签里写** —— 于是检索、回放、清理这三条路
/// 全都看不见它：它们各自只认**索引里的条目**，而交付件不是一个条目。
/// </para>
/// <para>
/// ⚠️ 这是一条**回归绊线**，不是「验一遍本来就对的东西」：
/// 哪天有人把这三条路里的任何一条改成「扫目录」，交付件会立刻被当成一份录像
/// —— 被算进容量、被回放出来、被清理删掉。那时候下面这几条会红。
/// </para>
/// <para>
/// ⚠️ 本文件**只验这三条路**。「占用多少字节」那个数走的是另一条路
/// （<c>LibraryFootprintProbe</c>，它本来就扫目录），不在这里的范围里。
/// </para>
/// </remarks>
public class DeliveryCopyExclusionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(8));
    private const string Relative = "2026/09/27/SF1000000001/e1.mp4";

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public DataLayout Layout { get; }
        public string ArchiveRoot => Layout.ArchiveRoot;

        /// <summary>
        /// 用户自选的导出目录。**在归档根之外** —— 这是规格 §3.7 的硬要求，
        /// 也是「回放那条路够不着它」的结构性原因。
        /// </summary>
        public string DeliveryDir => System.IO.Path.Combine(Root, "交付");

        public JsonLinesRecordingIndex Index { get; }
        public JsonLinesLabelStore Labels { get; }

        /// <summary>归档层（NAS 那一档）的根。测试自己决定建不建。</summary>
        public string NasRoot => System.IO.Path.Combine(Root, "nas");

        public Fixture()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-i7-" + Guid.NewGuid().ToString("N"));
            Layout = new DataLayout(Root);
            Directory.CreateDirectory(ArchiveRoot);
            Directory.CreateDirectory(DeliveryDir);
            Index = new JsonLinesRecordingIndex(Layout.IndexPath);
            Labels = new JsonLinesLabelStore(Layout.LabelStorePath);
        }

        /// <summary>往归档目录里放一条「录像」，返回它的索引条目。</summary>
        public RecordingEntry Put(string evidenceId = "e1")
        {
            var relative = $"2026/09/27/SF1000000001/{evidenceId}.mp4";
            var full = System.IO.Path.Combine(
                ArchiveRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "evidence");

            // ⚠️ 录完时刻离 `Now` **四十天** —— 落在「24 小时内」那道豁免之外，
            // 否则清理计划里根本不会有候选（那条豁免是 §3.5.1 的三条之一）。
            return new RecordingEntry(
                evidenceId, "session-1", WaybillNumber.Parse("SF1000000001"),
                Now.AddDays(-40), Now.AddDays(-40).AddMinutes(5), TimeSpan.FromMinutes(5),
                RelativePath.Parse(relative),
                ContentHash.Parse(new string('a', 64)), "device-1");
        }

        public string ArchivedFile(string relative = Relative) => System.IO.Path.Combine(
            ArchiveRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public string DeliveryTarget(string name = "e1.mp4") =>
            System.IO.Path.Combine(DeliveryDir, name);

        public void Dispose()
        {
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（2026-09-27 实测）。清理失败
            // 不该让任何一条测试红。
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>导出一份交付副本。</summary>
    private static Task<ExportResult> DeliverAsync(Fixture f, RecordingEntry entry) =>
        new EvidenceExporter(f.ArchiveRoot).ExportAsync(entry, f.DeliveryTarget());

    // ─────────────────────────────────────────────
    // ★ 承重的第一条：交付件不是索引里的条目
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 导出交付副本_一个字节都不往索引和标签里写()
    {
        // 下面三条路之所以都看不见它，**全靠这一条成立** ——
        // 它是这个不变量的根，所以单独钉一条。
        using var f = new Fixture();
        var entry = f.Put();
        await f.Index.AddAsync(entry);

        var result = await DeliverAsync(f, entry);

        Assert.True(result.Exported);
        Assert.True(File.Exists(f.DeliveryTarget()), "导出件应当真的落在用户选的那个位置上");

        Assert.Single(await f.Index.LoadAllAsync());
        Assert.Empty(await f.Labels.LoadAllAsync());
    }

    // ─────────────────────────────────────────────
    // 检索
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 检索看不见交付副本()
    {
        using var f = new Fixture();
        var entry = f.Put();
        await f.Index.AddAsync(entry);
        await DeliverAsync(f, entry);

        var hits = await new RecordingSearch(f.Index, f.Labels).SearchAsync(new RecordingQuery
        {
            WaybillText = "SF1000000001",
            MatchMode = WaybillMatchMode.Exact,
        });

        // 命中的是**归档层那一份**；交付件不在索引里，所以它连一个候选都不是。
        var hit = Assert.Single(hits);
        Assert.Equal("e1", hit.Entry.EvidenceId);
        Assert.Equal(Relative, hit.Entry.Location.Value);
    }

    // ─────────────────────────────────────────────
    // 清理
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 清理判不到交付副本_而且删完了交付件还躺在原地()
    {
        using var f = new Fixture();
        var entry = f.Put();
        await f.Index.AddAsync(entry);
        await f.Labels.SetAsync("e1", LabelKeys.BusinessType, BusinessTypes.OutboundValue);
        await DeliverAsync(f, entry);

        // 归档层上另有一份（发布过去的）—— 回查要过得去，它才肯删本地那份（I8）。
        var nasCopy = System.IO.Path.Combine(
            f.NasRoot, Relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(nasCopy)!);
        File.WriteAllText(nasCopy, "second-copy");

        var receipts = new ReceiptStore(System.IO.Path.Combine(f.Root, "receipts.jsonl"));
        await receipts.AppendAsync(new ReceiptPayload(
            "e1", new string('a', 64), Now.AddDays(-30), Now.AddDays(-30),
            "receiver-1", "receiver-1", Relative));

        var service = new CleanupService(
            f.Index,
            f.Labels,
            receipts,
            new PublishedStore(System.IO.Path.Combine(f.Root, "published.jsonl")),
            new CleanupExecutor(
                new DirectoryArchiveBackend(f.NasRoot, ArchiveBackendKind.Nas),
                f.ArchiveRoot,
                new CleanupAuditLog(System.IO.Path.Combine(f.Root, "cleanup-audit.jsonl")),
                NullLogger.Instance));

        var plan = await service.PreviewAsync(
            RetentionSettings.KeepAll with
            {
                ArchivedOutbound = new RetentionSetting(7),
                ArchivedReturn = new RetentionSetting(7),
            },
            Now);

        // 计划里只有归档层那一条 —— 交付件从来没进过这个池子。
        Assert.Equal("e1", Assert.Single(plan.Candidates).Entry.EvidenceId);

        var report = await service.RunAsync(plan);

        Assert.Single(report.Deleted);
        Assert.False(File.Exists(f.ArchivedFile()), "本机那一份按计划该没了");
        Assert.True(File.Exists(f.DeliveryTarget()),
            "交付副本不是归档层那份 —— 清理不该把它一起带走");
    }
}
