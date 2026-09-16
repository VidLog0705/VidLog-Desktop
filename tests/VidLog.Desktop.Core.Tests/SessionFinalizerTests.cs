using System.Reflection;
using System.Security.Cryptography;
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
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>成功路径的假 FFmpeg：会真的造出产物文件，让后续校验能过。</summary>
    private sealed class SucceedingRunner : IProcessRunner
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];
        public string Payload { get; set; } = "published-bytes";

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
        IRecordingIndex index)
    {
        return new SessionFinalizer(
            new RemuxPipeline("ffmpeg", runner),
            new DecodeVerifier("ffmpeg", runner),
            index,
            dir.Dir("archive"));
    }

    private static SegmentProduct CreateSegment(TempDir dir, int sequence = 0)
    {
        var path = dir.File($"segment-{sequence:000}.mkv");
        File.WriteAllText(path, "intermediate-mkv-bytes");

        var started = new DateTimeOffset(2026, 9, 16, 10, 30, 0, TimeSpan.FromHours(8));
        return new SegmentProduct(sequence, path, started, started.AddMinutes(1));
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
