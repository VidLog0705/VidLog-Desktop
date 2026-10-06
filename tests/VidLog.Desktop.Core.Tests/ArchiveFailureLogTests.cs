using VidLog.Desktop.Core.Cleanup;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「哪几条没发到归档层」那本落盘账（T23-A）。
/// </summary>
/// <remarks>
/// <para>
/// 缺陷原样：<c>ArchiveRelay.LastFailure</c> **只在内存** ⇒ 重启之后
/// 「NAS 那份没发上去」就看不见了。而它要说的事很重 —— 盘上那一份现在**只有一份**，
/// 用户若以为已经双份了，就可能手动删掉唯一的那一份（正是 I2 要防的事）。
/// 于是「必须对用户可见」（I3）断在**重启那一刻**，而那恰恰是最容易出事的时候。
/// </para>
/// <para>
/// ⚠️ 这一组的核心动词是**「重启」**：内存全丢、文件还在。
/// 所以每条都真的**另开一个 <see cref="ArchiveFailureLog"/> 指向同一个文件**，
/// 而不是断言「同一个对象里还记着」—— 后者与缺陷原样无关。
/// </para>
/// </remarks>
public class ArchiveFailureLogTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 6, 12, 0, 0, TimeSpan.FromHours(8));

    private sealed class TempLog : IDisposable
    {
        private readonly string _root;

        public TempLog()
        {
            _root = Path.Combine(Path.GetTempPath(), "vidlog-archfail-" + Guid.NewGuid().ToString("N"));
            Path_ = Path.Combine(_root, "archive-failures.jsonl");
        }

        public string Path_ { get; }

        public string PathOf(string name) => System.IO.Path.Combine(_root, name);

        public ArchiveFailureLog Open() => new(Path_);

        /// <summary>「重启」：另开一份，内存全丢、文件还在。</summary>
        public ArchiveFailureLog Reopen() => new(Path_);

        public int LineCount() =>
            File.Exists(Path_) ? File.ReadAllLines(Path_).Count(l => l.Trim().Length > 0) : 0;

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private static ArchiveFailureRecord Failed(string id, string why = "NAS 掉线了") =>
        new(id, $"2026/10/06/SF1/{id}.mp4", why, At, ArchiveFailureState.Failed);

    [Fact]
    public async Task 记一笔没发上去的账_重启之后还在()
    {
        // ⚠️ 这条就是 T23-A 的验收本身（清单原话：「让发布失败 → 重启 →
        // 断言设置页**仍显示**那条失败」）。这里断言到 Core 那一层为止 ——
        // 界面上那一步在 WPF 外壳，那个工程没有测试（T27②），只能手验。
        using var log = new TempLog();

        await log.Open().NoteFailedAsync(Failed("e-000"));

        var afterRestart = log.Reopen();

        var entry = Assert.Single(afterRestart.Outstanding);
        Assert.Equal("e-000", entry.EvidenceId);
        Assert.Contains("NAS 掉线", entry.Reason);

        // 界面要显示这三个字段，所以它们必须活着穿过重启。
        Assert.Equal("2026/10/06/SF1/e-000.mp4", entry.Location);
        Assert.Equal(At, entry.At);
    }

    [Fact]
    public async Task 补上之后那笔账被撤掉_重启之后也不再出现()
    {
        // 反证的另一半：只记不撤的话，重启后界面会一直提示一条**已经发上去**的录像 ——
        // 那是假警报，用户会去查一个不存在的问题（比不提示更坏）。
        using var log = new TempLog();

        var first = log.Open();
        await first.NoteFailedAsync(Failed("e-000"));
        Assert.Single(first.Outstanding);

        await first.NoteRecoveredAsync("e-000", At.AddHours(1));
        Assert.Empty(first.Outstanding);

        Assert.Empty(log.Reopen().Outstanding);
    }

    [Fact]
    public async Task 没欠着的时候补上_一行都不写_这本账不会只涨不落()
    {
        // ⚠️ 这是本组里**最容易被写成 bug** 的一条。`ArchiveRelay` 每次发布成功
        // 都会调 `NoteRecoveredAsync` —— 若那里无条件追加一行，正常机器的这个文件
        // **每录一段就长一行**（那是 T22「只涨不落」的形状）。
        using var log = new TempLog();

        var empty = log.Open();
        await empty.NoteRecoveredAsync("e-000", At);
        await empty.NoteRecoveredAsync("e-001", At);

        Assert.Equal(0, log.LineCount());
    }

    [Fact]
    public async Task 同一条反复失败_只算一条_原因取最后一次()
    {
        using var log = new TempLog();

        var first = log.Open();
        await first.NoteFailedAsync(Failed("e-000", "第一次：盘不在"));
        await first.NoteFailedAsync(Failed("e-000", "第二次：没权限"));

        var entry = Assert.Single(log.Reopen().Outstanding);

        // ⚠️ **最后一行胜出**（形态照 `UploadQueue`）：界面要说的是
        // 「现在为什么还没上去」，那句话是**最近一次**的原因。
        Assert.Equal("第二次：没权限", entry.Reason);
    }

    [Fact]
    public async Task 失败_补上_再失败_仍然算欠着()
    {
        // 撤掉是**一条记录**，不是「这个 id 以后都免检」。巡检式的反例：
        // 若把「撤掉」实现成「往一个免检名单里加一笔」，这条会红。
        using var log = new TempLog();

        var first = log.Open();
        await first.NoteFailedAsync(Failed("e-000"));
        await first.NoteRecoveredAsync("e-000", At.AddHours(1));
        await first.NoteFailedAsync(Failed("e-000", "又掉了"));

        var entry = Assert.Single(log.Reopen().Outstanding);
        Assert.Equal("又掉了", entry.Reason);
    }

    [Fact]
    public async Task 坏行跳过_读坏了不等于有欠账也不等于没有()
    {
        // ⚠️ 手写这几行的另一个用处：把**落盘格式**钉住（camelCase，与 `UploadQueue`
        // 和 `PublishedStore` 同一套）。格式漂了这条就红 —— 而它是会进诊断包、
        // 给人打开看的文件。字段名与 `ArchiveFailureRecord` 一一对应。
        using var log = new TempLog();

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log.Path_)!);
        await File.AppendAllTextAsync(
            log.Path_,
            "这不是 JSON\n"
            + "{\"evidenceId\":\"e-000\",\"location\":\"a.mp4\",\"reason\":\"掉了\","
            + "\"at\":\"2026-10-06T12:00:00+08:00\",\"state\":0}\n"
            + "{半截\n");

        var entry = Assert.Single(log.Reopen().Outstanding);
        Assert.Equal("e-000", entry.EvidenceId);
        Assert.Equal(At, entry.At);
    }

    [Fact]
    public void 读不出来只留痕不抛_否则应用起不来()
    {
        // 这本账**不是证据的一部分**：它丢了、读坏了，代价只是「这次启动不提示」，
        // 而下一段录制的成败照旧会被重新记上。所以读失败**绝不能让装配抛**。
        using var log = new TempLog();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log.Path_)!);

        var logger = new CapturingLogger();

        // ⚠️ 拿目录当文件路径**不行** —— `File.Exists(目录)` 是 false，
        // 代码会在「文件不存在」那一支直接返回，**根本走不到读**（这条我踩过）。
        // 要真让它抛，得**把文件锁住**：独占打开，读的那一头必然 IOException。
        using (new FileStream(log.Path_, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var subject = new ArchiveFailureLog(log.Path_, logger);

            Assert.Empty(subject.Outstanding);
            Assert.Single(logger.Entries);
            Assert.Contains("读不出来", logger.Entries[0].Message);
        }
    }

    private sealed class CapturingLogger : VidLog.Desktop.Core.Diagnostics.IAppLogger
    {
        public List<(VidLog.Desktop.Core.Diagnostics.LogLevel Level, string Message)> Entries { get; } = [];

        public void Log(VidLog.Desktop.Core.Diagnostics.LogLevel level, string category, string message) =>
            Entries.Add((level, message));

        public void Log(
            VidLog.Desktop.Core.Diagnostics.LogLevel level,
            string category,
            string message,
            IReadOnlyDictionary<string, object?> data) =>
            Entries.Add((level, message));
    }
}
