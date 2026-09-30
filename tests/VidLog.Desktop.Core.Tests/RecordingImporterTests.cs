using System.Globalization;
using System.Security.Cryptography;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Import;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「导入录像」把外面录来的一个文件收进本机库（设计图 `_41` 左栏那颗按钮）。
/// </summary>
/// <remarks>
/// <para>
/// 这一批全部**不开真的 ffmpeg**：量元数据与解码校验都走同一个注入进来的
/// <see cref="IProcessRunner"/> 假实现（罐头输出）。所以这几条在任何机器上都跑得了 ——
/// 不挂 <c>RequiresFfmpegFact</c>，也就不需要 <c>SkipMarker.Allow</c>。
/// </para>
/// <para>
/// ⚠️ 钉得最死的是**顺序**：先哈希 → 查重 → 量 → 解，四步全过才往盘上写。
/// 反过来的话，一个坏文件会在归档目录里留下一个**看起来正常**的 mp4，
/// 而它已经占着一个像证据一样的路径了。
/// </para>
/// </remarks>
public class RecordingImporterTests
{
    /// <summary>一个正常的成品：容器头里有 Duration，视频路是 1080P H.264。</summary>
    private const string ProbeOutput = """
        Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'C:\Temp\x.mp4':
          Metadata:
            major_brand     : isom
          Duration: 00:01:23.45, start: 0.000000, bitrate: 1234 kb/s
          Stream #0:0: Video: h264 (High), yuv420p(progressive), 1920x1080, 30 fps, 30 tbr, 90k tbn
          Stream #0:1: Audio: aac (LC), 48000 Hz, mono, fltp
        """;

    /// <summary>实时流那种：`Duration: N/A` —— 时长读不出来。</summary>
    private const string LiveOutput = """
        Input #0, rtsp, from 'rtsp://host:554/live':
          Duration: N/A, start: 0.006000, bitrate: N/A
          Stream #0:0: Video: h264 (High), yuv420p(progressive), 1920x1080, 30 fps, 30 tbr
        """;

    /// <summary>只有声音的容器（例如一个 m4a）—— 收进来也回放不了。</summary>
    private const string AudioOnlyOutput = """
        Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'C:\Temp\x.m4a':
          Duration: 00:03:00.00, start: 0.000000, bitrate: 128 kb/s
          Stream #0:0: Audio: aac (LC), 44100 Hz, mono, fltp
        """;

    /// <summary>认不出来路的编码与尺寸：既不是 h264/hevc，也不对任何一档分辨率。</summary>
    private const string OddOutput = """
        Input #0, matroska,webm, from 'C:\Temp\x.mkv':
          Duration: 00:00:30.00, start: 0.000000, bitrate: 900 kb/s
          Stream #0:0: Video: vp9, yuv420p, 1600x900, 30 fps, 30 tbr
        """;

    // ─────────────────────────────────────────────
    // 成功路径
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 导入之后落进归档目录_索引与业务类型都写了()
    {
        using var dir = new TempDir();
        var source = dir.File("SF1000000001.mp4");
        await File.WriteAllTextAsync(source, "the-recording-bytes");

        var index = new IndexSpy();
        var labels = new LabelSpy();
        var importer = Build(dir.Dir("archive"), index, new FakeFfmpeg(), labels);

        var started = new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.FromHours(8));
        var result = await importer.ImportAsync(
            new ImportRequest(source, WaybillNumber.Parse("SF1000000001"), BusinessType.Return, started));

        Assert.True(result.Imported, result.FailureReason);

        var entry = Assert.Single(index.Entries);
        Assert.Equal(entry.EvidenceId, result.EvidenceId);
        Assert.Equal(entry.Location, result.Location);

        // 落点：<年>/<月>/<日>/<单号>/<会话>_000.mp4，会话号**由内容哈希推出来**。
        Assert.Equal(
            $"2026/09/30/SF1000000001/import-{entry.ContentHash.Value[..12]}_000.mp4",
            entry.Location.Value);

        // 归档目录里真的有一份，内容与源文件一比一
        var published = Path.Combine(dir.Dir("archive"), entry.Location.Value);
        Assert.True(File.Exists(published), $"归档目录里应当有 {entry.Location.Value}");
        Assert.Equal("the-recording-bytes", await File.ReadAllTextAsync(published));

        // 源文件**留在原地** —— 导的是副本，不是搬家
        Assert.True(File.Exists(source));

        // 时长、结束时间、编码、尺寸都是从文件里量出来的
        Assert.Equal(TimeSpan.Parse("00:01:23.45", CultureInfo.InvariantCulture), entry.Duration);
        Assert.Equal(started + entry.Duration, entry.EndedAt);
        Assert.Equal("H264", entry.Codec);
        Assert.Equal("P1080", entry.Resolution);
        Assert.Equal(TimeSpan.Parse("00:01:23.45", CultureInfo.InvariantCulture), result.Duration);

        // ⚠️ 来源设备是 "imported"：这一段是哪台机器录的，我们**不知道**。
        // 写本机名字等于往证据元数据里写一句假话。
        Assert.Equal("imported", entry.SourceDeviceId);

        // ⚠️ 方向不写：摄像头怎么装的，导入的这段早就压进画面里了，问不出来。
        Assert.Null(entry.Orientation);

        // 业务类型标签
        var label = Assert.Single(labels.Written);
        Assert.Equal(entry.EvidenceId, label.EvidenceId);
        Assert.Equal(LabelKeys.BusinessType, label.Key);
        Assert.Equal(BusinessTypes.ReturnValue, label.Value);
    }

    [Fact]
    public async Task 量不出时长时按0记_但结果里明说没量出来()
    {
        using var dir = new TempDir();
        var source = dir.File("SF1000000001.mp4");
        await File.WriteAllTextAsync(source, "bytes");

        var index = new IndexSpy();
        var log = new LogSpy();
        var importer = Build(dir.Dir("archive"), index, new FakeFfmpeg { ProbeOutput = LiveOutput }, logger: log);

        var started = new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.FromHours(8));
        var result = await importer.ImportAsync(
            new ImportRequest(source, WaybillNumber.Parse("SF1000000001"), BusinessType.Outbound, started));

        Assert.True(result.Imported, result.FailureReason);

        // ⚠️ 索引里那个字段是 `TimeSpan`，没有「不知道」这个取值 ⇒ 记 0。
        // 但 **结果里必须是 null** —— 界面靠它决定要不要说「没能量出时长」。
        var entry = Assert.Single(index.Entries);
        Assert.Equal(TimeSpan.Zero, entry.Duration);
        Assert.Equal(started, entry.EndedAt);
        Assert.Null(result.Duration);

        // 日志里也要看得出这一条是「没量出来」而不是「真的 0 秒」。
        Assert.Contains(
            log.Lines,
            l => l.Data is { } data
                && data.TryGetValue("时长", out var duration)
                && (string?)duration == "没量出来");
    }

    [Fact]
    public async Task 认不出的编码与尺寸留空_不编一个档位()
    {
        // ⚠️ 分辨率是**精确匹配**，不是「差不多就归到最近的一档」：
        // 把 1600×900 归到 1080P 等于往证据元数据里写一个假的档位。
        using var dir = new TempDir();
        var source = dir.File("SF1.mp4");
        await File.WriteAllTextAsync(source, "bytes");

        var index = new IndexSpy();
        var importer = Build(dir.Dir("archive"), index, new FakeFfmpeg { ProbeOutput = OddOutput });

        await importer.ImportAsync(new ImportRequest(
            source, WaybillNumber.Parse("SF1"), BusinessType.Outbound, DateTimeOffset.UtcNow));

        var entry = Assert.Single(index.Entries);
        Assert.Null(entry.Codec);
        Assert.Null(entry.Resolution);
    }

    [Fact]
    public async Task 盘上已经有一份时不覆盖它_只把索引补上()
    {
        // 上一次导到一半：文件落了盘、索引没写成（断电、进程被杀）。
        // 再导一次同一个文件应当**只补索引**，而不是把已有的那份重写一遍。
        using var dir = new TempDir();
        var source = dir.File("SF1000000001.mp4");
        await File.WriteAllTextAsync(source, "the-recording-bytes");

        var archive = dir.Dir("archive");
        var sessionId = $"import-{(await HashOf(source))[..12]}";
        var destination = Path.Combine(archive, "2026", "09", "30", "SF1000000001", $"{sessionId}_000.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "the-recording-bytes");

        var index = new IndexSpy();
        var importer = Build(archive, index, new FakeFfmpeg());

        var result = await importer.ImportAsync(new ImportRequest(
            source, WaybillNumber.Parse("SF1000000001"), BusinessType.Outbound,
            new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.FromHours(8))));

        Assert.True(result.Imported, result.FailureReason);
        Assert.Single(index.Entries);
        Assert.Equal("the-recording-bytes", await File.ReadAllTextAsync(destination));
    }

    // ─────────────────────────────────────────────
    // 不收的那几种 —— 规格 §3.1.4：校验失败不得入库为「正常」
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 同一个文件换个名字再导一次也不收()
    {
        // 判据是**内容哈希**，不是文件名：同一个文件改个名再导，库里就会多出
        // 两条一模一样的证据 —— 它们哈希相同、各自被清理判定一次，
        // 而界面上看起来是「同一天同一段录像录了两遍」。
        using var dir = new TempDir();
        var first = dir.File("SF1.mp4");
        await File.WriteAllTextAsync(first, "same-bytes");

        var index = new IndexSpy();
        var importer = Build(dir.Dir("archive"), index, new FakeFfmpeg());

        var started = new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.FromHours(8));

        var ok = await importer.ImportAsync(
            new ImportRequest(first, WaybillNumber.Parse("SF1"), BusinessType.Outbound, started));
        Assert.True(ok.Imported, ok.FailureReason);

        var second = dir.File("SF2.mp4");
        await File.WriteAllTextAsync(second, "same-bytes");

        var again = await importer.ImportAsync(
            new ImportRequest(second, WaybillNumber.Parse("SF2"), BusinessType.Outbound, started));

        Assert.False(again.Imported);
        Assert.Contains("已经在库里", again.FailureReason);
        Assert.Single(index.Entries);
    }

    [Fact]
    public async Task 文件不存在时直接失败_连哈希都不算()
    {
        using var dir = new TempDir();
        var index = new IndexSpy();
        var runner = new FakeFfmpeg();
        var importer = Build(dir.Dir("archive"), index, runner);

        var result = await importer.ImportAsync(new ImportRequest(
            dir.File("nope.mp4"), WaybillNumber.Parse("SF1"), BusinessType.Outbound, DateTimeOffset.UtcNow));

        Assert.False(result.Imported);
        Assert.Contains("找不到这个文件", result.FailureReason);
        Assert.Empty(index.Entries);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task 没有视频画面的文件不收()
    {
        using var dir = new TempDir();
        var source = dir.File("SF1.m4a");
        await File.WriteAllTextAsync(source, "audio-bytes");

        var index = new IndexSpy();
        var runner = new FakeFfmpeg { ProbeOutput = AudioOnlyOutput };
        var importer = Build(dir.Dir("archive"), index, runner);

        var result = await importer.ImportAsync(new ImportRequest(
            source, WaybillNumber.Parse("SF1"), BusinessType.Outbound, DateTimeOffset.UtcNow));

        Assert.False(result.Imported);
        Assert.Contains("没有视频画面", result.FailureReason);
        Assert.Empty(index.Entries);

        // 量出「没有视频路」就停手 —— 不必再完整解一遍。
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task 解不开的不收_而且归档目录里一个字都没写()
    {
        // ⚠️ 验的是**源文件**，不是复制之后那一份。这条测试的全部意义就在这里：
        // 四步没过完之前，归档目录里不该出现任何东西。
        using var dir = new TempDir();
        var source = dir.File("SF1.mp4");
        await File.WriteAllTextAsync(source, "broken-bytes");

        var index = new IndexSpy();
        var importer = Build(
            dir.Dir("archive"), index, new FakeFfmpeg { VerificationFails = true });

        var result = await importer.ImportAsync(new ImportRequest(
            source, WaybillNumber.Parse("SF1"), BusinessType.Outbound, DateTimeOffset.UtcNow));

        Assert.False(result.Imported);
        Assert.Contains("解不开", result.FailureReason);
        Assert.Empty(index.Entries);
        Assert.Empty(FilesIn(dir.Dir("archive")));

        // 源文件当然还在 —— 用户拿它去别处试试，或者换一个文件。
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task 写索引失败算失败_但说清怎么补救()
    {
        // 文件已经在盘上、可检索却是零 —— 对「找得到自己的证据」这个承诺来说
        // 等于没入库。**算失败**，并且要告诉用户再按一次就好。
        using var dir = new TempDir();
        var source = dir.File("SF1.mp4");
        await File.WriteAllTextAsync(source, "bytes");

        var index = new IndexSpy { ThrowOnAdd = new IOException("磁盘满了") };
        var labels = new LabelSpy();
        var importer = Build(dir.Dir("archive"), index, new FakeFfmpeg(), labels);

        var result = await importer.ImportAsync(new ImportRequest(
            source, WaybillNumber.Parse("SF1"), BusinessType.Outbound, DateTimeOffset.UtcNow));

        Assert.False(result.Imported);
        Assert.Contains("再导一次", result.FailureReason);

        // 文件确实已经复制进去了（所以那一句才叫「再导一次会补上索引」）
        Assert.Single(FilesIn(dir.Dir("archive")));

        // 索引都没写成，标签就不该去写 —— 挂在一个库里不存在的证据上没有意义。
        Assert.Empty(labels.Written);
    }

    [Fact]
    public async Task 标签写不上不算导入失败()
    {
        // ⚠️ 与 `SessionFinalizer` 同一条规矩：标签是可修正的描述（I5），
        // 而这段录像已经落盘、已经能检索了 —— 为了一个描述性字段
        // 把一次成功的导入判成失败，是拿次要的东西毁掉主要的东西。
        using var dir = new TempDir();
        var source = dir.File("SF1.mp4");
        await File.WriteAllTextAsync(source, "bytes");

        var index = new IndexSpy();
        var labels = new LabelSpy { ThrowOnSet = new IOException("标签文件被占着") };
        var log = new LogSpy();
        var importer = Build(dir.Dir("archive"), index, new FakeFfmpeg(), labels, log);

        var result = await importer.ImportAsync(new ImportRequest(
            source, WaybillNumber.Parse("SF1"), BusinessType.Outbound, DateTimeOffset.UtcNow));

        Assert.True(result.Imported, result.FailureReason);
        Assert.Single(index.Entries);

        // 但**要留痕**（AGENTS.md §6）：不然「这条为什么没有业务类型」事后没人答得上来。
        Assert.Contains(log.Lines, l => l.Category == "标签" && l.Level == LogLevel.Warn);
    }

    [Fact]
    public async Task 每一次失败都留了痕()
    {
        using var dir = new TempDir();
        var index = new IndexSpy();
        var log = new LogSpy();
        var importer = Build(dir.Dir("archive"), index, new FakeFfmpeg(), logger: log);

        await importer.ImportAsync(new ImportRequest(
            dir.File("nope.mp4"), WaybillNumber.Parse("SF1"), BusinessType.Outbound, DateTimeOffset.UtcNow));

        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warn, line.Level);
        Assert.Equal("导入", line.Category);
        Assert.Contains("SF1", line.Message);
    }

    [Fact]
    public async Task 导成功了也留痕_带上来源与落点()
    {
        using var dir = new TempDir();
        var source = dir.File("SF1.mp4");
        await File.WriteAllTextAsync(source, "bytes");

        var index = new IndexSpy();
        var log = new LogSpy();
        var importer = Build(dir.Dir("archive"), index, new FakeFfmpeg(), logger: log);

        await importer.ImportAsync(new ImportRequest(
            source, WaybillNumber.Parse("SF1"), BusinessType.Outbound, DateTimeOffset.UtcNow));

        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Info, line.Level);
        Assert.NotNull(line.Data);

        var data = line.Data!;

        // 「这条录像是什么时候、从哪个文件进来的」事后要答得上来。
        Assert.Equal(source, data["来源"]);
        Assert.Equal(Assert.Single(index.Entries).EvidenceId, data["证据"]);
    }

    // ─────────────────────────────────────────────
    // 测试脚手架
    // ─────────────────────────────────────────────

    private static RecordingImporter Build(
        string archiveRoot,
        IRecordingIndex index,
        IProcessRunner runner,
        ILabelStore? labels = null,
        IAppLogger? logger = null) =>
        new(
            StorageLocations.Single(archiveRoot),
            index,
            new DecodeVerifier("ffmpeg", runner),
            runner,
            "ffmpeg",
            labels,
            relay: null,
            logger);

    private static async Task<string> HashOf(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();

    private static IReadOnlyList<string> FilesIn(string root) =>
        Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)]
            : [];

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);
        public string Dir(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（本仓实测过）。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 假的 ffmpeg。**两条路靠 argv 分**：量元数据那一步带 <c>-frames:v 1</c>
    /// （只解一帧），解码校验那一步不带。
    /// </summary>
    private sealed class FakeFfmpeg : IProcessRunner
    {
        public string ProbeOutput { get; set; } = RecordingImporterTests.ProbeOutput;

        /// <summary>解码校验是否报错（模拟成品损坏）。</summary>
        public bool VerificationFails { get; set; }

        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add([.. arguments]);

            if (arguments.Contains("-frames:v"))
            {
                return Task.FromResult(new ProcessResult(0, string.Empty, ProbeOutput));
            }

            return Task.FromResult(VerificationFails
                ? new ProcessResult(0, string.Empty, "Invalid data found when processing input")
                : new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class IndexSpy : IRecordingIndex
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

        public Task<IReadOnlyList<RecordingEntry>> LoadAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RecordingEntry>>(Entries);
    }

    private sealed class LabelSpy : ILabelStore
    {
        public List<RecordingLabel> Written { get; } = [];
        public Exception? ThrowOnSet { get; set; }

        public Task SetAsync(
            string evidenceId, string key, string value, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSet is not null)
            {
                throw ThrowOnSet;
            }

            Written.Add(new RecordingLabel(evidenceId, key, value, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetForEvidenceAsync(
            string evidenceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(
                new Dictionary<string, IReadOnlyDictionary<string, string>>());
    }

    private sealed class LogSpy : IAppLogger
    {
        public List<(LogLevel Level, string Category, string Message, IReadOnlyDictionary<string, object?>? Data)>
            Lines { get; } = [];

        public void Log(LogLevel level, string category, string message) =>
            Lines.Add((level, category, message, null));

        public void Log(
            LogLevel level, string category, string message, IReadOnlyDictionary<string, object?> data) =>
            Lines.Add((level, category, message, data));
    }
}
