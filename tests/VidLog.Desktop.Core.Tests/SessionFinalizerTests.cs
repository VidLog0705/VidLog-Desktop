using System.Reflection;
using System.Security.Cryptography;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 不变量 I9：录制收尾只有一条路径，不存在旁路。
/// </summary>
public class SessionFinalizerTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-fin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);
        public string Dir(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（2026-09-27 实测：会话还在
            // 收尾时删含 `.ass` / `.mkv` 的临时目录）。清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>成功路径的假 FFmpeg：会真的造出产物文件，让后续校验能过。</summary>
    private sealed class SucceedingRunner : IProcessRunner
    {
        /// <summary>产物内容。归档层那一份要跟它一比一。</summary>
        public const string DefaultPayload = "published-bytes";

        public List<IReadOnlyList<string>> Invocations { get; } = [];
        public string Payload { get; set; } = DefaultPayload;

        /// <summary>
        /// 最后一次 concat 清单里的那些行。
        /// </summary>
        /// <remarks>
        /// ⚠️ **必须在调用当场读**：清单是临时文件，收尾结束前就被删了。
        /// 而这是「到底拿哪几片去合的」唯一能看得见的地方 —— 只断言「调用了 concat」
        /// 的话，传一片进去也一样绿（产物内容是本假进程写死的）。
        /// </remarks>
        public List<string>? LastConcatList { get; private set; }

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(arguments.ToList());

            var args = arguments.ToList();

            if (args.Contains("concat"))
            {
                var i = args.IndexOf("-i");
                if (i >= 0)
                {
                    LastConcatList = [.. File.ReadAllLines(args[i + 1])];
                }
            }

            var yIndex = args.IndexOf("-y");
            if (yIndex >= 0 && yIndex + 1 < args.Count)
            {
                var output = args[yIndex + 1];
                var directory = System.IO.Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(output, Payload);
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    /// <summary>解码校验必定报错的假 FFmpeg（模拟成品损坏）。</summary>
    private sealed class FailingVerificationRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            var args = arguments.ToList();
            var yIndex = args.IndexOf("-y");
            if (yIndex >= 0 && yIndex + 1 < args.Count)
            {
                var output = args[yIndex + 1];
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
                File.WriteAllText(output, "broken");
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            // 解码那一步
            return Task.FromResult(new ProcessResult(0, string.Empty, "Invalid data found when processing input"));
        }
    }

    private sealed class RecordingIndexSpy : IRecordingIndex
    {
        public List<RecordingEntry> Entries { get; } = [];
        public Exception? ThrowOnAdd { get; set; }

        public Task AddAsync(RecordingEntry entry, CancellationToken cancellationToken = default)
        {
            if (ThrowOnAdd is not null)
            {
                throw ThrowOnAdd;
            }

            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RecordingEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RecordingEntry>>(Entries);
    }

    private static SessionFinalizer BuildFinalizer(
        TempDir dir,
        IProcessRunner runner,
        IRecordingIndex index,
        ArchiveRelay? relay = null)
    {
        return new SessionFinalizer(
            new RemuxPipeline("ffmpeg", runner),
            new DecodeVerifier("ffmpeg", runner),
            index,
            dir.Dir("archive"),
            logger: null,
            relay: relay);
    }

    private static SegmentProduct CreateSegment(TempDir dir, int sequence = 0)
    {
        var path = dir.File($"segment-{sequence:000}.mkv");
        File.WriteAllText(path, "intermediate-mkv-bytes");

        var started = new DateTimeOffset(2026, 9, 16, 10, 30, 0, TimeSpan.FromHours(8));
        return new SegmentProduct(sequence, path, started, started.AddMinutes(1));
    }

    // ─────────────────────────────────────────────
    // 归档层那一份（规格 §3.4.6）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 收尾之后归档层上也要有一份()
    {
        using var dir = new TempDir();
        var runner = new SucceedingRunner();
        var index = new RecordingIndexSpy();

        var nas = dir.Dir("nas");
        var relay = new ArchiveRelay(
            new DirectoryArchiveBackend(nas, ArchiveBackendKind.Nas), "NAS",
            new PublishedStore(dir.File("published.jsonl")), new ArchiveFailureLog(dir.File("archive-failures.jsonl")));

        var finalizer = BuildFinalizer(dir, runner, index, relay);

        var outcome = await finalizer.FinalizeAsync(
            "sess-1", WaybillNumber.Parse("SF1000000001"), "device-1",
            [CreateSegment(dir)], StopReason.Manual);

        Assert.True(outcome.Succeeded);

        // 本机那一份
        var entry = Assert.Single(index.Entries);
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Dir("archive"), entry.Location.Value)));

        // 归档层那一份：**同样的相对路径**，内容一样。
        var onNas = System.IO.Path.Combine(nas, entry.Location.Value);
        Assert.True(File.Exists(onNas), $"归档层上应当有 {entry.Location.Value}");
        Assert.Equal(SucceedingRunner.DefaultPayload, await File.ReadAllTextAsync(onNas));

        Assert.Null(relay.LastFailure);
    }

    [Fact]
    public async Task 归档层发不上去时本机这一份照样入库()
    {
        // ⚠️ 这条是这一段的**方向性**判据：归档层那份没上去，代价是
        // 「这条还不能被清理」（回查会拒），**不是**「这条录像没了」。
        // 反过来做（发布失败 ⇒ 整个收尾失败）会让本机这一份变成孤儿，
        // 每次启动重收一遍 —— 那是拿 I2 去换一个「发上去了没有」的仪式。
        using var dir = new TempDir();
        var runner = new SucceedingRunner();
        var index = new RecordingIndexSpy();

        // 拿一个文件当归档根：发布必然失败。
        var blocker = dir.File("not-a-directory");
        await File.WriteAllTextAsync(blocker, "x");

        var relay = new ArchiveRelay(
            new DirectoryArchiveBackend(
                System.IO.Path.Combine(blocker, "nas"), ArchiveBackendKind.Nas),
            "NAS",
            new PublishedStore(dir.File("published.jsonl")), new ArchiveFailureLog(dir.File("archive-failures.jsonl")));

        var finalizer = BuildFinalizer(dir, runner, index, relay);

        var outcome = await finalizer.FinalizeAsync(
            "sess-1", WaybillNumber.Parse("SF1000000001"), "device-1",
            [CreateSegment(dir)], StopReason.Manual);

        Assert.True(outcome.Succeeded, "归档层发不上去**不该**让收尾失败");
        Assert.Single(index.Entries);
        Assert.NotNull(relay.LastFailure);

        var entry = index.Entries[0];
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Dir("archive"), entry.Location.Value)),
            "本机那一份必须还在");
    }

    // ─────────────────────────────────────────────
    // I9 的结构保证
    // ─────────────────────────────────────────────

    [Fact]
    public void 收尾器只有一个公开入口()
    {
        // I9 说「不存在旁路」。这条测试把「没有旁路」变成可执行的断言：
        // 想绕开收尾逻辑，就必须先在这里加一个公开方法 —— 加不出来，就是没有旁路。
        // 这是有意为之的结构约束，不是形式主义。
        var publicMethods = typeof(SessionFinalizer)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Distinct()
            .ToList();

        Assert.Equal(["FinalizeAsync"], publicMethods);
    }

    [Fact]
    public async Task 所有停录原因走完全相同的收尾序列()
    {
        var reasons = Enum.GetValues<StopReason>();
        var sequences = new List<string>();

        foreach (var reason in reasons)
        {
            using var dir = new TempDir();
            var runner = new SucceedingRunner();
            var index = new RecordingIndexSpy();
            var finalizer = BuildFinalizer(dir, runner, index);
            var segment = CreateSegment(dir);

            await finalizer.FinalizeAsync(
                "session-1", WaybillNumber.Parse("SF1234567890"), "device-1", [segment], reason);

            // 去掉临时目录这段路径，只比「调了什么、按什么顺序、什么形状」
            var shape = string.Join(
                "\n",
                runner.Invocations.Select(a => string.Join(" ", a).Replace(dir.Path, "<DIR>", StringComparison.OrdinalIgnoreCase)));

            sequences.Add(shape);
        }

        Assert.Equal(reasons.Length, sequences.Count);
        Assert.All(sequences, s => Assert.Equal(sequences[0], s));
        Assert.NotEmpty(sequences[0]);
    }

    // ─────────────────────────────────────────────
    // 成功路径
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 成功收尾进入已入库并写入索引()
    {
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        var finalizer = BuildFinalizer(dir, new SucceedingRunner(), index);
        var segment = CreateSegment(dir);

        var outcome = await finalizer.FinalizeAsync(
            "session-1", WaybillNumber.Parse("SF1234567890"), "device-1",
            [segment], StopReason.Manual);

        Assert.Equal(RecordingSessionState.Indexed, outcome.State);
        Assert.True(outcome.Succeeded);

        var entry = Assert.Single(index.Entries);
        Assert.Equal("SF1234567890", entry.Waybill.Value);
        Assert.Equal("device-1", entry.SourceDeviceId);
        Assert.Equal(TimeSpan.FromMinutes(1), entry.Duration);
    }

    [Fact]
    public async Task 索引里只有相对路径()
    {
        // 规格 §6.2：绝对路径在应用重装、容器变更后必然失效。
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        var finalizer = BuildFinalizer(dir, new SucceedingRunner(), index);

        await finalizer.FinalizeAsync(
            "session-1", WaybillNumber.Parse("SF1234567890"), "device-1",
            [CreateSegment(dir)], StopReason.Manual);

        var location = Assert.Single(index.Entries).Location.Value;
        Assert.DoesNotContain(dir.Path, location, StringComparison.OrdinalIgnoreCase);
        Assert.False(System.IO.Path.IsPathRooted(location));
        Assert.Contains("SF1234567890", location);
    }

    [Fact]
    public async Task 哈希与落盘文件内容一致()
    {
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        var runner = new SucceedingRunner { Payload = "known-payload-for-hash" };
        var finalizer = BuildFinalizer(dir, runner, index);

        await finalizer.FinalizeAsync(
            "session-1", WaybillNumber.Parse("SF1234567890"), "device-1",
            [CreateSegment(dir)], StopReason.Manual);

        var entry = Assert.Single(index.Entries);
        var published = System.IO.Path.Combine(dir.Dir("archive"), entry.Location.Value);

        // 独立算一遍（直接读全部字节 vs. 被测代码的流式哈希）
        var expected = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(published))).ToLowerInvariant();

        Assert.Equal(expected, entry.ContentHash.Value);
    }

    // ─────────────────────────────────────────────
    // 失败路径 —— 规格 §4.1：校验失败不得当作正常入库
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 成品校验失败不得标记为已入库()
    {
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        var finalizer = BuildFinalizer(dir, new FailingVerificationRunner(), index);

        var outcome = await finalizer.FinalizeAsync(
            "session-1", WaybillNumber.Parse("SF1234567890"), "device-1",
            [CreateSegment(dir)], StopReason.Manual);

        Assert.Equal(RecordingSessionState.FinalizeFailed, outcome.State);
        Assert.False(outcome.Succeeded);
        Assert.Empty(index.Entries);
        Assert.NotNull(outcome.FailureReason);
    }

    [Fact]
    public async Task 收尾失败时源MKV必须保留()
    {
        // I2：任何录像出厂后任何时刻至少存在一份完整副本。
        // 收尾失败时那个 MKV 就是唯一一份 —— 上层不删它，收尾器也不删。
        using var dir = new TempDir();
        var finalizer = BuildFinalizer(dir, new FailingVerificationRunner(), new RecordingIndexSpy());
        var segment = CreateSegment(dir);

        var outcome = await finalizer.FinalizeAsync(
            "session-1", WaybillNumber.Parse("SF1234567890"), "device-1",
            [segment], StopReason.Manual);

        Assert.False(outcome.Succeeded);
        Assert.True(File.Exists(segment.SourcePath), "收尾失败后源 MKV 必须还在");
    }

    [Fact]
    public async Task 写索引失败不算收尾成功()
    {
        // 成品可播但检索不到 —— 那不是「成功」，是用户找不到自己的证据。
        using var dir = new TempDir();
        var index = new RecordingIndexSpy { ThrowOnAdd = new IOException("磁盘满了") };
        var finalizer = BuildFinalizer(dir, new SucceedingRunner(), index);

        var outcome = await finalizer.FinalizeAsync(
            "session-1", WaybillNumber.Parse("SF1234567890"), "device-1",
            [CreateSegment(dir)], StopReason.Manual);

        Assert.Equal(RecordingSessionState.FinalizeFailed, outcome.State);
        Assert.Contains("写索引失败", outcome.FailureReason);
    }

    [Fact]
    public async Task 分段文件不存在时收尾失败()
    {
        using var dir = new TempDir();
        var finalizer = BuildFinalizer(dir, new SucceedingRunner(), new RecordingIndexSpy());
        var missing = new SegmentProduct(
            0, dir.File("nope.mkv"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30));

        var outcome = await finalizer.FinalizeAsync(
            "session-1", WaybillNumber.Parse("SF1234567890"), "device-1",
            [missing], StopReason.ProcessKilled);

        Assert.Equal(RecordingSessionState.FinalizeFailed, outcome.State);
    }

    [Fact]
    public async Task 没有分段时收尾失败()
    {
        using var dir = new TempDir();
        var finalizer = BuildFinalizer(dir, new SucceedingRunner(), new RecordingIndexSpy());

        var outcome = await finalizer.FinalizeAsync(
            "session-1", WaybillNumber.Parse("SF1234567890"), "device-1", [], StopReason.Manual);

        Assert.Equal(RecordingSessionState.FinalizeFailed, outcome.State);
    }

    [Fact]
    public async Task 多分段里有一个坏的就整体不算成功()
    {
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        var runner = new SucceedingRunner();
        var finalizer = BuildFinalizer(dir, runner, index);

        var good = CreateSegment(dir, 0);
        var missing = new SegmentProduct(
            1, dir.File("gone.mkv"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30));

        var outcome = await finalizer.FinalizeAsync(
            "session-1", WaybillNumber.Parse("SF1234567890"), "device-1",
            [good, missing], StopReason.Manual);

        Assert.Equal(RecordingSessionState.FinalizeFailed, outcome.State);

        // 「整体」不只是状态位：坏了一片就**一个成品都不该落下**。
        // 落下的那个会是「少了中间一截」的片子，而它看起来完全正常。
        // ⚠️ 目录可能压根没建（缺片那一步在落盘之前）—— 那也是「没落下」。
        var archive = dir.Dir("archive");
        Assert.True(
            !Directory.Exists(archive) || Directory.GetFiles(archive, "*", SearchOption.AllDirectories).Length == 0,
            "坏了一片就不该在归档目录里留下任何成品");
    }

    // ─────────────────────────────────────────────
    // 一场一个成品（需求方 2026-10-12）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 多段合成一条_索引里只有一条成品()
    {
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        var runner = new SucceedingRunner();
        var finalizer = BuildFinalizer(dir, runner, index);

        var outcome = await finalizer.FinalizeAsync(
            "session-1", WaybillNumber.Parse("SF1234567890"), "device-1",
            [CreateSegment(dir, 0), CreateSegment(dir, 1), CreateSegment(dir, 2)], StopReason.Manual);

        Assert.True(outcome.Succeeded, outcome.FailureReason);

        // 索引里只有一条 —— 一个单号一个文件。
        var entry = Assert.Single(index.Entries);
        Assert.Equal("VidLog-Video/2026-09-16/发货/SF1234567890.mp4", entry.Location.Value);
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Dir("archive"), entry.Location.Value)));

        // 三片都指向**同一个**成品：这是「哪几片拼成了它」的可观测性，
        // 不是重复记账（少一片就是成品里少一截画面，而这件事不会报错）。
        Assert.Equal(3, outcome.Segments.Count);
        Assert.All(outcome.Segments, s => Assert.Equal(entry.Location, s.Location));
        Assert.Equal([0, 1, 2], outcome.Segments.Select(s => s.Source.Sequence));

        // 走的是 concat 那一路，不是「把第一片当整场 remux」；
        // 而且**三片都进了清单**（传一片进去也会是「调用了 concat」）。
        // ⚠️ 不能拿 `Assert.Single` 数调用次数 —— 校验那一步也调 ffmpeg。
        Assert.Contains(runner.Invocations, args => args.Contains("concat"));
        Assert.Equal(3, runner.LastConcatList?.Count);
        Assert.Contains(runner.LastConcatList!, line => line.Contains("segment-002.mkv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 同一单号的第二场落到_2_而不是盖掉第一场()
    {
        // ⚠️ 新文件名里**没有会话号** ⇒ 同一个单号的第二场必然撞名。撞上就加 `_2`，
        // **绝不覆盖**（I2：少一份证据）。第一场那一份必须原样还在。
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        var finalizer = BuildFinalizer(dir, new SucceedingRunner { Payload = "first" }, index);

        await finalizer.FinalizeAsync(
            "session-1", WaybillNumber.Parse("SF1234567890"), "device-1",
            [CreateSegment(dir)], StopReason.Manual);

        var second = BuildFinalizer(dir, new SucceedingRunner { Payload = "second" }, index);
        var outcome = await second.FinalizeAsync(
            "session-2", WaybillNumber.Parse("SF1234567890"), "device-1",
            [CreateSegment(dir, 1)], StopReason.Manual);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal(2, index.Entries.Count);

        var archive = dir.Dir("archive");
        Assert.Equal(
            ["VidLog-Video/2026-09-16/发货/SF1234567890.mp4", "VidLog-Video/2026-09-16/发货/SF1234567890_2.mp4"],
            index.Entries.Select(e => e.Location.Value));

        // 两份内容各是各的 —— 第二场没有把第一场写成自己那一份。
        Assert.Equal("first", await File.ReadAllTextAsync(System.IO.Path.Combine(archive, index.Entries[0].Location.Value)));
        Assert.Equal("second", await File.ReadAllTextAsync(System.IO.Path.Combine(archive, index.Entries[1].Location.Value)));
    }

    [Fact]
    public async Task 落盘撞上非撞名的IO故障时给出失败原因而不是抛出去()
    {
        // ⚠️ 这条钉的是「定名落盘」那一步的**出口形状**：`File.Move` 抛 IOException 时
        // 只有「目标已存在」才是撞名（换 `_2` 重试），其余（盘满 / 目标目录被删 /
        // 路径过长 / 文件被占）都必须**变成一个失败原因返回**。
        // 一旦漏成异常逃出去，后果有两层：`RecordingSession.StopAsync` 里
        // `State`/`Outcome` 不再落定，而 `OrphanRecovery.RecoverAsync` 的循环
        // **当场中断后面所有孤儿**（那里没有 catch）。
        //
        // 造法：在成品该落的位置预先摆一个**目录**（同名）。
        // `File.Move` 占不动它 ⇒ IOException；而 `File.Exists` 对目录恒 false
        // ⇒ 判断「是不是撞名」的那条 `when` 不成立，正好走到兜底那一支。
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        var finalizer = BuildFinalizer(dir, new SucceedingRunner(), index);

        var occupied = System.IO.Path.Combine(
            dir.Dir("archive"), "VidLog-Video", "2026-09-16", "发货", "SF1234567890.mp4");
        var parent = System.IO.Path.GetDirectoryName(occupied)!;
        System.IO.Directory.CreateDirectory(parent);
        System.IO.Directory.CreateDirectory(occupied);

        var outcome = await finalizer.FinalizeAsync(
            "session-1", WaybillNumber.Parse("SF1234567890"), "device-1",
            [CreateSegment(dir)], StopReason.Manual);

        Assert.False(outcome.Succeeded);
        Assert.Contains("落盘失败", outcome.FailureReason, StringComparison.Ordinal);

        // 失败就是失败：**不许**留下一条指向那个占位的索引条目。
        Assert.Empty(index.Entries);

        // I2：源 MKV 还在（下次启动按孤儿重试）。
        Assert.True(File.Exists(dir.File("segment-000.mkv")));
    }
}
