using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 日志与保留期（<c>AGENTS.md</c> §6）。
/// </summary>
public class LoggingTests
{
    private static readonly FileLogOptions Options = new(@"C:\logs", "vidlog", RetainDays: 14);

    // ─────────────────────────────────────────────
    // 保留期 —— AGENTS.md §6 点名的那个坑
    // ─────────────────────────────────────────────

    [Fact]
    public void 保留期按文件名里的时间戳判_不按名字字符串排()
    {
        // 这就是那个坑：按字符串排的话 "vidlog-2..." 会被当成比 "vidlog-10..." 更新，
        // 于是**删掉最新的、留下最旧的** —— 恰好反了。
        var names = new[]
        {
            "vidlog-20260102-100000.log",  // 最旧，该删
            "vidlog-20260102-090000.log",  // 字符串序里比上面大，但它更旧
            "vidlog-20260120-100000.log",  // 新，留
        };
        var now = new DateTimeOffset(2026, 1, 20, 12, 0, 0, TimeSpan.Zero);

        var expired = LogRetention.SelectExpired(names, Options, now);

        Assert.Equal(
            ["vidlog-20260102-090000.log", "vidlog-20260102-100000.log"],
            expired.Order());
        Assert.DoesNotContain("vidlog-20260120-100000.log", expired);
    }

    [Fact]
    public void 解析不出时间戳的文件不动()
    {
        // 不知道它是什么就删，那是赌。
        var names = new[] { "vidlog.log", "别的文件.log", "vidlog-坏时间戳.log" };
        var now = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var expired = LogRetention.SelectExpired(names, Options, now);

        Assert.Empty(expired);
    }

    [Fact]
    public void 保留期内的不删()
    {
        var names = new[] { "vidlog-20260120-100000.log" };
        var now = new DateTimeOffset(2026, 1, 25, 12, 0, 0, TimeSpan.Zero);

        Assert.Empty(LogRetention.SelectExpired(names, Options, now));
    }

    [Fact]
    public void 文件名格式可往返()
    {
        // 文件名里存的是**本地时钟读数**，没有时区信息 —— 日志本来就是本机的东西。
        // 所以只断言「时刻不丢」：写进去的瞬间与读回来的瞬间必须是同一刻。
        var at = new DateTimeOffset(new DateTime(2026, 9, 22, 17, 10, 5), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 22)));
        var name = Options.FileNameFor(at);

        Assert.Equal("vidlog-20260922-171005.log", name);
        Assert.True(LogRetention.TryParseTimestamp(name, "vidlog", out var parsed));
        Assert.Equal(at, parsed);
    }

    // ─────────────────────────────────────────────
    // 写入
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 写进去的行能被读回来()
    {
        using var dir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(dir.Path, "vidlog"));

        logger.Log(LogLevel.Info, "录制", "开录了");
        await logger.DisposeAsync();

        var text = await File.ReadAllTextAsync(logger.Path);
        Assert.Contains("开录了", text, StringComparison.Ordinal);
        Assert.Contains("[INFO ]", text, StringComparison.Ordinal);
        Assert.Contains("[录制]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 附带的数据会写进同一行()
    {
        using var dir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(dir.Path, "vidlog"));

        logger.Log(LogLevel.Warn, "采集", "设备打不开",
            new Dictionary<string, object?> { ["设备"] = "EasyCamera", ["码"] = 123 });
        await logger.DisposeAsync();

        var text = await File.ReadAllTextAsync(logger.Path);
        Assert.Contains("设备=EasyCamera", text, StringComparison.Ordinal);
        Assert.Contains("码=123", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 目录不可写时不抛_只丢日志()
    {
        // I4 的同一条精神：日志坏了不能拖垮录制。
        // 用一个不可能建成的目录（Windows 上路径里的非法字符）。
        var logger = new FileLogger(new FileLogOptions("Z:\\不存在的盘\\logs", "vidlog"));

        logger.Log(LogLevel.Error, "录制", "这条多半写不进去");

        // 关键断言：上面没抛。Dispose 也不该抛。
        await logger.DisposeAsync();
    }

    [Fact]
    public async Task 关闭后再写不抛()
    {
        using var dir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(dir.Path, "vidlog"));

        await logger.DisposeAsync();
        logger.Log(LogLevel.Info, "录制", "关完之后再写一条");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-log-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
