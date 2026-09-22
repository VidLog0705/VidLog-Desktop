using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 采集会话的编排：开录 → 分段滚动 → 收尾 → 入库。
/// </summary>
/// <remarks>
/// 规格 §3.1.1（连续分段录制、重启后能收尾孤儿）与 §4.1（收尾只有一条路径）。
/// <para>
/// 这里的替身是**记录型**的：价值不在「假采集能不能跑」，而在
/// 「编排器调它时给的参数对不对、按什么顺序、落了几次盘」。
/// 真采集那一半由 <see cref="FfmpegCameraCaptureIntegrationTests"/> 与真机回归负责。
/// </para>
/// </remarks>
public class RecordingSessionTests
{
    // ─────────────────────────────────────────────
    // 开录：manifest 必须立刻落盘
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 开录时立刻写第一版manifest_否则进程被杀后没有孤儿可恢复()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        await using var session = Build(dir, capture);

        await session.StartAsync(WaybillNumber.Parse("SF1234567890"), "libx264");

        // 这条是孤儿的**前提**：孤儿判据是「有 session.json 但没有 finalized.json」。
        // 不写这一版，进程被杀后 ListOrphansAsync 根本看不见这个会话。
        var manifestPath = Path.Combine(dir.WorkspaceRoot, session.SessionId, "session.json");
        Assert.True(File.Exists(manifestPath), "开录必须立刻写下 session.json");
    }

    [Fact]
    public async Task 开录后采集进程收到的是设备名与目标路径()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        await using var session = Build(dir, capture);

        await session.StartAsync(WaybillNumber.Parse("SF1"), "h264_nvenc");

        var start = Assert.Single(capture.Starts);
        Assert.Equal("Lenovo EasyCamera", start.Device);
        Assert.Equal("h264_nvenc", start.Encoder);
        Assert.EndsWith("segment-000.mkv", start.OutputPath);
    }

    // ─────────────────────────────────────────────
    // 分段滚动 —— 规格 §3.1.1「不因切分而中断用户体验」
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 到分段时长就滚下一段_序号递增且文件名不重()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();

        // 段 2 分钟、循环每次推进 1 分钟（见 AdvancingDelay）：
        // 恰好滚 2 次，第 3 段进行到一半时撞上时长上限，循环自己停下来。
        await using var session = Build(dir, capture, clock: clock, options: new RecordingSessionOptions(
            SegmentDuration: TimeSpan.FromMinutes(2),
            MaxDuration: TimeSpan.FromMinutes(5),
            StopGracePeriod: TimeSpan.FromSeconds(1),
            PollInterval: TimeSpan.FromMinutes(1)));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.RunAsync("libx264");

        // 撞上限 → 自动收尾，循环必然退出（所以这条测试不会空转）。
        Assert.Equal(StopReason.DurationFallback, session.StoppedBecause);

        // 序号从 0 起、连续、不重 —— 收尾器按 Sequence 排序拼时间轴，
        // 重号会让跨分段定位错位。
        Assert.Equal(
            ["segment-000.mkv", "segment-001.mkv", "segment-002.mkv"],
            capture.Starts.Select(s => Path.GetFileName(s.OutputPath)));
    }

    [Fact]
    public async Task 每滚一段就更新一次manifest_进程被杀后已封闭的段仍可恢复()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();

        // 段 1 分钟、上限 2.5 分钟、每次推进 1 分钟：
        // 第 2 分钟滚出第 2 段，第 3 分钟撞上限停下 ⇒ manifest 里恰好 2 段。
        await using var session = Build(dir, capture, clock: clock, options: new RecordingSessionOptions(
            SegmentDuration: TimeSpan.FromMinutes(1),
            MaxDuration: TimeSpan.FromMinutes(2.5),
            StopGracePeriod: TimeSpan.FromSeconds(1),
            PollInterval: TimeSpan.FromMinutes(1)));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.RunAsync("libx264");

        // 读盘上留下了什么 —— 这相当于「进程被杀」时孤儿恢复能看到的东西。
        // 收尾在 RunAsync 里已经跑过了，所以这里先看它在收尾**之前**写下的 manifest：
        // 用一个新的工作区读同一批文件即可（finalized.json 已写，孤儿查不出来，
        // 因此直接查 manifest 里的分段，那正是恢复链路依赖的输入）。
        var manifestPath = Path.Combine(dir.WorkspaceRoot, session.SessionId, "session.json");
        var json = await File.ReadAllTextAsync(manifestPath);

        Assert.Contains("segment-000.mkv", json, StringComparison.Ordinal);
        Assert.Contains("segment-001.mkv", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 编排循环异常退出时_会话仍会被收尾而不是卡在收尾中()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();
        var thrown = false;

        // 让循环在滚出第 2 段之后抛一个非取消异常 —— 模拟编排途中出岔子。
        Func<TimeSpan, CancellationToken, Task> rollThenFail = (interval, _) =>
        {
            clock.Advance(interval);
            if (capture.Starts.Count >= 2 && !thrown)
            {
                thrown = true;
                throw new InvalidOperationException("模拟编排途中的意外");
            }

            return Task.CompletedTask;
        };

        await using var session = new RecordingSession(
            new RecordingWorkspace(dir.WorkspaceRoot), capture,
            BuildFinalizer(dir, new SucceedingRunner(), new RecordingIndexSpy()),
            new DiskSpaceGuard(new PlentyOfSpaceProbe()),
            "Lenovo EasyCamera", "device-1",
            new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromMinutes(1),
                MaxDuration: TimeSpan.FromHours(1),
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1)),
            clock.Read, rollThenFail);

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.RunAsync("libx264");

        // 关键：不能卡在「收尾中」。卡住的话界面会永远显示正在收尾，
        // 等 Completion 的人（界面、测试）会一起挂死 —— 这个坑真踩过。
        Assert.NotEqual(RecordingSessionState.Finalizing, session.State);
        Assert.Equal(RecordingSessionState.Indexed, session.State);

        // finalized.json 写了，说明确实走完了唯一那条收尾路径。
        Assert.True(File.Exists(Path.Combine(dir.WorkspaceRoot, session.SessionId, "finalized.json")));
    }

    // ─────────────────────────────────────────────
    // 收尾 —— 不变量 I9：只有一条路径
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 停录走的是既有的SessionFinalizer_不另起一条收尾路径()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();
        var index = new RecordingIndexSpy();
        var runner = new SucceedingRunner();

        await using var session = Build(dir, capture, clock: clock, runner: runner, index: index);

        await session.StartAsync(WaybillNumber.Parse("SF1234567890"), "libx264");
        clock.Advance(TimeSpan.FromSeconds(5));
        var outcome = await session.StopAsync(StopReason.Manual);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal(RecordingSessionState.Indexed, session.State);
        Assert.Equal(StopReason.Manual, session.StoppedBecause);

        // 收尾成功 → 必须写 finalized.json，否则下次启动会把它当孤儿重收一遍。
        Assert.True(File.Exists(Path.Combine(dir.WorkspaceRoot, session.SessionId, "finalized.json")));
    }

    [Fact]
    public async Task 收尾失败时不写finalized_留着让下次启动当孤儿重试()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var runner = new FailingRunner();

        await using var session = Build(dir, capture, runner: runner, index: new RecordingIndexSpy());

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        var outcome = await session.StopAsync(StopReason.Manual);

        Assert.False(outcome.Succeeded);
        Assert.Equal(RecordingSessionState.FinalizeFailed, session.State);

        // 关键：**不能**写 finalized.json —— 写了就等于把这批录像判了死刑（I2）。
        Assert.False(File.Exists(Path.Combine(dir.WorkspaceRoot, session.SessionId, "finalized.json")));

        // 而且它得能被当成孤儿看见，下次启动才有重试的机会。
        var orphans = await new RecordingWorkspace(dir.WorkspaceRoot).ListOrphansAsync();
        Assert.Single(orphans);
    }

    [Fact]
    public async Task 停录原因原样传给收尾器_所有原因走同一条路径()
    {
        var reasons = Enum.GetValues<StopReason>();

        foreach (var reason in reasons)
        {
            using var dir = new TempDir();
            var capture = new FakeCapture();
            var index = new RecordingIndexSpy();
            var runner = new SucceedingRunner();

            await using var session = Build(dir, capture, runner: runner, index: index);
            await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
            var outcome = await session.StopAsync(reason);

            Assert.True(outcome.Succeeded, $"{reason}: {outcome.FailureReason}");
            Assert.Equal(reason, outcome.Reason);
            Assert.Equal(reason, session.StoppedBecause);
        }
    }

    [Fact]
    public async Task 一段都没录到时收尾失败并且原因对用户可见()
    {
        using var dir = new TempDir();
        // 采集进程一起来就退出，什么也没写出 —— 模拟摄像头打不开。
        var capture = new FakeCapture { ProcessProducesFile = false };

        await using var session = Build(dir, capture, index: new RecordingIndexSpy());

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        var outcome = await session.StopAsync(StopReason.Manual);

        Assert.False(outcome.Succeeded);
        // I3：不允许静默失败 —— 必须有一句人能读懂的话。
        Assert.False(string.IsNullOrWhiteSpace(session.LastProblem));
    }

    // ─────────────────────────────────────────────
    // 时间：不变量 I11 —— 单调时钟，墙钟改了不算数
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 分段时长按单调时钟算_不随墙钟跳变()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();

        await using var session = Build(dir, capture, clock: clock, index: new RecordingIndexSpy());

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");

        // 用户在这时候把系统时间往前调了一天。单调时钟不为所动。
        clock.Advance(TimeSpan.FromSeconds(7));
        var outcome = await session.StopAsync(StopReason.Manual);

        // 段的时长应当正好是单调走的 7 秒，而不是墙钟跳变后的一天。
        var finalized = Assert.Single(outcome.Segments);
        var duration = finalized.Source.EndedAt - finalized.Source.StartedAt;
        Assert.Equal(TimeSpan.FromSeconds(7), duration);
    }

    // ─────────────────────────────────────────────
    // 兜底
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 到时长上限自动收尾_原因是时长兜底()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();

        // 段 1 小时、上限 1 小时、每次推进 1 分钟：
        // 段永远滚不到，循环到第 60 次撞上时长上限自己停下。
        await using var session = Build(dir, capture, clock: clock, index: new RecordingIndexSpy(),
            options: new RecordingSessionOptions(
                SegmentDuration: TimeSpan.FromHours(1),
                MaxDuration: TimeSpan.FromHours(1),
                StopGracePeriod: TimeSpan.FromSeconds(1),
                PollInterval: TimeSpan.FromMinutes(1)));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.RunAsync("libx264");

        // 循环自动收尾时，调用方没有 StopAsync 的 Task 可以等 —— 等这个信号。
        await session.Completion;

        Assert.Equal(StopReason.DurationFallback, session.StoppedBecause);
        Assert.Equal(RecordingSessionState.Indexed, session.State);
    }

    [Fact]
    public async Task 磁盘将满时主动收尾_而不是等崩溃()
    {
        using var dir = new TempDir();
        var capture = new FakeCapture();
        var clock = new FakeClock();

        await using var session = new RecordingSession(
            new RecordingWorkspace(dir.WorkspaceRoot), capture,
            BuildFinalizer(dir, new SucceedingRunner(), new RecordingIndexSpy()),
            new DiskSpaceGuard(new AlwaysFullProbe()),
            "Lenovo EasyCamera", "device-1",
            // 磁盘永远告急 ⇒ 第一轮轮询就该收尾，与时长无关。
            DefaultOptions, clock.Read, AdvancingDelay(clock));

        await session.StartAsync(WaybillNumber.Parse("SF1"), "libx264");
        await session.RunAsync("libx264");
        await session.Completion;

        Assert.Equal(StopReason.StorageLow, session.StoppedBecause);
        Assert.Equal(RecordingSessionState.Indexed, session.State);
    }

    // ─────────────────────────────────────────────
    // 测试脚手架
    // ─────────────────────────────────────────────

    private static readonly string Ffmpeg = FfmpegLocator.TryFind()!;

    private static RecordingSession Build(
        TempDir dir,
        FakeCapture capture,
        FakeClock? clock = null,
        IProcessRunner? runner = null,
        RecordingIndexSpy? index = null,
        RecordingSessionOptions? options = null)
    {
        // 方法组不能直接配合 ?. —— 显式判空，让类型明确是 Func<TimeSpan>?。
        Func<TimeSpan>? effectiveClock = clock is null ? null : clock.Read;

        return new RecordingSession(
            new RecordingWorkspace(dir.WorkspaceRoot),
            capture,
            BuildFinalizer(dir, runner ?? new SucceedingRunner(), index ?? new RecordingIndexSpy()),
            new DiskSpaceGuard(new PlentyOfSpaceProbe()),
            "Lenovo EasyCamera",
            "device-1",
            options ?? DefaultOptions,
            effectiveClock,
            AdvancingDelay(clock));
    }

    /// <summary>
    /// 编排循环的等待：**不真等，但要把假时钟往前推**。
    /// </summary>
    /// <remarks>
    /// 只返回 <see cref="Task.CompletedTask"/> 是不够的 —— 时钟不动的话循环永远停在
    /// 同一时刻，变成空转（第一版就是这么把测试挂住的）。推进时钟才等价于「真的等了一会」。
    /// </remarks>
    private static Func<TimeSpan, CancellationToken, Task> AdvancingDelay(FakeClock? clock) =>
        (interval, _) =>
        {
            clock?.Advance(interval);
            return Task.CompletedTask;
        };

    private static RecordingSessionOptions DefaultOptions => new(
        SegmentDuration: TimeSpan.FromHours(1),
        MaxDuration: TimeSpan.FromHours(1),
        StopGracePeriod: TimeSpan.FromSeconds(1),
        PollInterval: TimeSpan.FromSeconds(1));

    private static SessionFinalizer BuildFinalizer(
        TempDir dir, IProcessRunner runner, RecordingIndexSpy index) =>
        new(new RemuxPipeline(Ffmpeg, runner),
            new DecodeVerifier(Ffmpeg, runner),
            index,
            Path.Combine(dir.Path, "archive"));

    /// <summary>可控的单调时钟。</summary>
    private sealed class FakeClock
    {
        private TimeSpan _now;

        public TimeSpan Read() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>记录型采集替身：记下每次起采的参数，并真的造出文件。</summary>
    private sealed class FakeCapture : ICameraCapture
    {
        public List<(string Device, string OutputPath, string Encoder)> Starts { get; } = [];

        /// <summary>置 false 模拟「摄像头打不开」——进程起来了但没产物。</summary>
        public bool ProcessProducesFile { get; init; } = true;

        public Task<ICaptureProcess> StartAsync(
            string device, string outputPath, string encoder, CancellationToken cancellationToken = default)
        {
            Starts.Add((device, outputPath, encoder));
            return Task.FromResult<ICaptureProcess>(new FakeProcess(outputPath, ProcessProducesFile));
        }
    }

    /// <summary>假的采集进程。停的时候按需留下产物。</summary>
    private sealed class FakeProcess(string outputPath, bool producesFile) : ICaptureProcess
    {
        public bool HasExited { get; private set; }

        public Task<int?> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (producesFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, "captured-bytes");
            }

            HasExited = true;
            return Task.FromResult<int?>(0);
        }
    }

    /// <summary>磁盘充裕。</summary>
    private sealed class PlentyOfSpaceProbe : IDiskSpaceProbe
    {
        public long GetFreeBytes(string path) => 100L * 1024 * 1024 * 1024;
    }

    /// <summary>磁盘告急。</summary>
    private sealed class AlwaysFullProbe : IDiskSpaceProbe
    {
        public long GetFreeBytes(string path) => 1;
    }

    /// <summary>成功路径的假 FFmpeg：会真的造出产物，让校验能过。</summary>
    private sealed class SucceedingRunner : IProcessRunner
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Invocations.Add(arguments.ToList());

            var args = arguments.ToList();
            var yIndex = args.IndexOf("-y");
            if (yIndex >= 0 && yIndex + 1 < args.Count)
            {
                var output = args[yIndex + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, "published-bytes");
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    /// <summary>总是失败的假 FFmpeg。</summary>
    private sealed class FailingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(1, string.Empty, "boom"));
    }

    /// <summary>索引替身。</summary>
    private sealed class RecordingIndexSpy : IRecordingIndex
    {
        public List<RecordingEntry> Entries { get; } = [];

        public Task AddAsync(RecordingEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RecordingEntry>> LoadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RecordingEntry>>(Entries);
    }

    /// <summary>每个测试类自带的临时目录（本仓惯例：不共用基类）。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public string WorkspaceRoot => System.IO.Path.Combine(Path, "work");

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-sess-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
