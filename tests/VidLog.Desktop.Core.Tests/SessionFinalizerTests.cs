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

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(arguments.ToList());

            var args = arguments.ToList();
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
            new DirectoryArchiveBackend(nas, ArchiveBackendKind.Nas), "NAS");

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
            "NAS");

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
    }
}
