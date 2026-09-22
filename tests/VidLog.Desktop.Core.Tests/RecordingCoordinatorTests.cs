using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 一次「工作」的编排 —— 三种工作模式都落在这里（规格 §3.3.1）。
/// </summary>
/// <remarks>
/// 与 <see cref="RecordingSessionTests"/> 的分工：那个测**一段**，这个测**一次工作**
/// （反复开关若干段、换件换单号、打点落盘）。
/// </remarks>
public class RecordingCoordinatorTests
{
    private static readonly WaybillNumber A = WaybillNumber.Parse("SF1234567890");
    private static readonly WaybillNumber B = WaybillNumber.Parse("SF9999999999");

    // ─────────────────────────────────────────────
    // 开录与打点
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 扫到单号就开录并打点()
    {
        using var dir = new TempDir();
        var punches = new FakePunchLog();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, punches);

        coordinator.StartWork();
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        Assert.Equal(A, coordinator.CurrentWaybill);

        // 规格 §3.2.4：识别到单号 → 记录该时刻与该单号的关联，且**立即**持久化。
        var punch = Assert.Single(punches.Written);
        Assert.Equal(A, punch.WaybillNumber);
        Assert.Equal(PunchSource.KeyboardScanner, punch.Source);

        // 偏移从**开录那一刻**起算，所以它应该贴近 0。
        // 不要求恰好 0：开录本身要写 manifest 落盘、起采集上下文，
        // 打点必然在它们之后。这里要验的是「基准对」，不是「零耗时」。
        Assert.InRange(punch.MonotonicOffsetMilliseconds, 0, 500);
    }

    [Fact]
    public async Task 没开始工作时扫到单号也开录()
    {
        // 扫码枪打进来的时候用户未必先点过【开始工作】——
        // 规格 §4.1 的状态机就是从「识别到单号」起算的。
        using var dir = new TempDir();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog());

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        Assert.Equal(A, coordinator.CurrentWaybill);
    }

    [Fact]
    public async Task 手动输入也算打点来源()
    {
        using var dir = new TempDir();
        var punches = new FakePunchLog();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, punches);

        await coordinator.SubmitAsync(A, PunchSource.ManualEntry);

        Assert.Equal(PunchSource.ManualEntry, Assert.Single(punches.Written).Source);
    }

    // ─────────────────────────────────────────────
    // 同码停
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 同码停下复扫同码就收尾()
    {
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), index);

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        Assert.Null(coordinator.CurrentWaybill);
        Assert.Single(index.Entries);
    }

    [Fact]
    public async Task 同码停下扫到异码只提示不停录()
    {
        using var dir = new TempDir();
        var notices = new List<CoordinatorNotice>();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog());
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(B, PunchSource.KeyboardScanner);

        // 规格 §3.3.2：不停止录制，仅声音提示。
        Assert.Equal(A, coordinator.CurrentWaybill);
        Assert.Contains(notices, n => n.Kind == CoordinatorNoticeKind.WrongWaybill);
    }

    // ─────────────────────────────────────────────
    // 连续扫：换件
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 连续扫下扫到异码就换段_旧段入库新段开录()
    {
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        var punches = new FakePunchLog();
        await using var coordinator = Build(dir, WorkMode.Continuous, punches, index);

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(B, PunchSource.KeyboardScanner);

        // 立刻以新单号开下一段。
        Assert.Equal(B, coordinator.CurrentWaybill);

        // 旧段在后台收尾 —— 等它。
        await coordinator.PendingFinalization;

        // 旧段入库了。停因（WaybillChanged）体现在收尾结果上，索引里不带停因字段。
        Assert.Single(index.Entries);
        Assert.Equal(2, punches.Written.Count);
    }

    [Fact]
    public async Task 连续扫下复扫同码不停也不提示()
    {
        using var dir = new TempDir();
        var notices = new List<CoordinatorNotice>();
        await using var coordinator = Build(dir, WorkMode.Continuous, new FakePunchLog());
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        Assert.Equal(A, coordinator.CurrentWaybill);
        // 连续扫下换件是正常路径，不该报「面单不同」。
        Assert.DoesNotContain(notices, n => n.Kind == CoordinatorNoticeKind.WrongWaybill);
    }

    // ─────────────────────────────────────────────
    // 开始 / 结束工作
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 结束工作会收尾在录的段()
    {
        using var dir = new TempDir();
        var index = new RecordingIndexSpy();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), index);

        coordinator.StartWork();
        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);

        var outcome = await coordinator.StopWorkAsync();

        Assert.NotNull(outcome);
        Assert.True(outcome!.Succeeded, outcome.FailureReason);
        Assert.Null(coordinator.CurrentWaybill);
        Assert.Single(index.Entries);
    }

    [Fact]
    public async Task 没在录时结束工作不抛()
    {
        using var dir = new TempDir();
        await using var coordinator = Build(dir, WorkMode.StopOnSameWaybill, new FakePunchLog());

        Assert.Null(await coordinator.StopWorkAsync());
    }

    [Fact]
    public async Task 收尾失败时给出可见的提示()
    {
        using var dir = new TempDir();
        var notices = new List<CoordinatorNotice>();

        // 让收尾器失败。
        await using var coordinator = Build(
            dir, WorkMode.StopOnSameWaybill, new FakePunchLog(), index: null, runner: new FailingRunner());
        coordinator.Notice += notices.Add;

        await coordinator.SubmitAsync(A, PunchSource.KeyboardScanner);
        await coordinator.StopWorkAsync();

        // I3：收尾失败必须让用户看见。
        var notice = Assert.Single(notices, n => n.Kind == CoordinatorNoticeKind.FinalizeFailed);
        // 而且要说明**东西还在**，否则用户会以为录像丢了。
        Assert.Contains("仍在工作区", notice.Message, StringComparison.Ordinal);
        // 也得给出真正的原因 —— 只说「失败了」等于没说（I3 的同一条精神）。
        Assert.Contains("boom", notice.Message, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────
    // 测试脚手架
    // ─────────────────────────────────────────────

    private static RecordingCoordinator Build(
        TempDir dir,
        WorkMode mode,
        IPunchLog punches,
        RecordingIndexSpy? index = null,
        IProcessRunner? runner = null)
    {
        var ffmpeg = FfmpegLocator.TryFind() ?? "ffmpeg";
        var effectiveRunner = runner ?? new SucceedingRunner();

        return new RecordingCoordinator(
            new RecordingWorkspace(dir.WorkspaceRoot),
            new FakeCapture(),
            new SessionFinalizer(
                new RemuxPipeline(ffmpeg, effectiveRunner),
                new DecodeVerifier(ffmpeg, effectiveRunner),
                index ?? new RecordingIndexSpy(),
                Path.Combine(dir.Path, "archive")),
            new DiskSpaceGuard(new PlentyOfSpaceProbe()),
            punches,
            NullLogger.Instance,
            new WorkModePolicy(mode, StaticStopOption.Off),
            new CoordinatorOptions("Lenovo EasyCamera", "device-1", "libx264"));
    }

    private sealed class FakeCapture : ICameraCapture
    {
        public Task<ICaptureProcess> StartAsync(
            string device, string outputPath, string encoder, CancellationToken cancellationToken = default) =>
            Task.FromResult<ICaptureProcess>(new FakeProcess(outputPath));
    }

    private sealed class FakeProcess(string outputPath) : ICaptureProcess
    {
        public Task<int?> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, "captured");
            return Task.FromResult<int?>(0);
        }
    }

    private sealed class FakePunchLog : IPunchLog
    {
        public List<Punch> Written { get; } = [];

        public Task AppendAsync(Punch punch, CancellationToken cancellationToken = default)
        {
            Written.Add(punch);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Punch>> LoadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Punch>>(Written);
    }

    private sealed class PlentyOfSpaceProbe : IDiskSpaceProbe
    {
        public long GetFreeBytes(string path) => 100L * 1024 * 1024 * 1024;
    }

    private sealed class SucceedingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            var args = arguments.ToList();
            var yIndex = args.IndexOf("-y");
            if (yIndex >= 0 && yIndex + 1 < args.Count)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(args[yIndex + 1])!);
                File.WriteAllText(args[yIndex + 1], "published");
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class FailingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(1, string.Empty, "boom"));
    }

    /// <summary>索引替身 —— 记下每次入库，供断言停因。</summary>
    /// <summary>索引替身 —— 记下每次入库。</summary>
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

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public string WorkspaceRoot => System.IO.Path.Combine(Path, "work");

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-coord-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
