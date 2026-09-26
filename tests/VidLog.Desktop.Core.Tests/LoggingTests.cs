using System.Text.Json;
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

    // ─────────────────────────────────────────────
    // 清理 —— SelectExpired 与文件系统之间的那一跳
    // ─────────────────────────────────────────────

    [Fact]
    public void 清理真的把过期文件删掉_近的留着()
    {
        // ⚠️ `SelectExpired` 一直是对的、也一直有测试；坏的是**没有人调它**。
        // 这条测的是「有人调了，而且删的是对的那几个」。
        using var dir = new TempDir();

        var old = Path.Combine(dir.Path, "vidlog-20200101-120000.log");
        var fresh = Path.Combine(dir.Path, "vidlog-20260120-120000.log");
        File.WriteAllText(old, "旧");
        File.WriteAllText(fresh, "新");

        var now = new DateTimeOffset(2026, 1, 20, 12, 0, 0, TimeSpan.Zero);
        var deleted = FileLogger.PurgeExpired(Options with { Directory = dir.Path }, now);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void 清理不动认不出名字的文件()
    {
        // 不知道它是什么就删，那是赌 —— 与 SelectExpired 同一条规矩。
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "别的什么.log"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "vidlog.log"), "y");

        var deleted = FileLogger.PurgeExpired(
            Options with { Directory = dir.Path },
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(Path.Combine(dir.Path, "别的什么.log")));
    }

    [Fact]
    public void 保留期小于一天时什么都不删()
    {
        // 那种配置下「过期」会把今天这个正在写的文件也算进去。
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "vidlog-20200101-120000.log"), "旧");

        var deleted = FileLogger.PurgeExpired(
            Options with { Directory = dir.Path, RetainDays = 0 },
            new DateTimeOffset(2026, 1, 20, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(0, deleted);
    }

    [Fact]
    public void 目录不存在时不抛()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Path, "还没有这个目录");

        Assert.Equal(0, FileLogger.PurgeExpired(Options with { Directory = missing }, DateTimeOffset.Now));
    }

    [Fact]
    public async Task 写进去的行能当_JSON_读回来()
    {
        using var dir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(dir.Path, "vidlog"));

        logger.Log(LogLevel.Info, "录制", "开录了");
        await logger.DisposeAsync();

        // ⚠️ 这一条以前断言的是 `[INFO ]` 与 `[录制]` —— 那是旧的**自由文本**格式。
        // 换成 JSON 是有意的（结构化是为了按字段查，而不是 grep 一段人读的文本），
        // 所以这里是**有意识地换断言**，不是"改到绿为止"：级别与分类各自成字段。
        var line = Assert.Single(await File.ReadAllLinesAsync(logger.Path));
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;

        Assert.Equal("INFO", root.GetProperty("lvl").GetString());
        Assert.Equal("录制", root.GetProperty("cat").GetString());
        Assert.Equal("开录了", root.GetProperty("msg").GetString());

        // 时间戳**必须带偏移量**：不带的话，同一个诊断包里两台机器的 17:10
        // 是歧义的，而排事件顺序正是要拿它。
        var ts = root.GetProperty("ts").GetString()!;
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+[+-]\d{2}:\d{2}$", ts);
    }

    [Fact]
    public async Task 附带的数据进同一个_JSON_对象的_data_里()
    {
        using var dir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(dir.Path, "vidlog"));

        logger.Log(LogLevel.Warn, "采集", "设备打不开",
            new Dictionary<string, object?> { ["设备"] = "EasyCamera", ["码"] = 123 });
        await logger.DisposeAsync();

        var line = Assert.Single(await File.ReadAllLinesAsync(logger.Path));
        using var json = JsonDocument.Parse(line);
        var data = json.RootElement.GetProperty("data");

        Assert.Equal("EasyCamera", data.GetProperty("设备").GetString());
        // 数字要还是数字 —— 全写成字符串的话，按耗时排序这种查询就得先转型。
        Assert.Equal(123, data.GetProperty("码").GetInt32());
    }

    [Fact]
    public async Task 消息里的引号与换行不会把一条日志劈成两行()
    {
        // 手拼 JSON 最典型的坏法：`message` 里一个 `"` 或一个 `\n` ——
        // 轻则不是 JSON，重则**把一条事件劈成两行、整份 JSONL 报废**。
        // 这是选 `Utf8JsonWriter` 而不是字符串拼接的全部理由。
        using var dir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(dir.Path, "vidlog"));

        logger.Log(LogLevel.Info, "扫码", "扫到 \"引号\" 与\n换行");
        await logger.DisposeAsync();

        var lines = await File.ReadAllLinesAsync(logger.Path);
        var line = Assert.Single(lines); // 一条日志就是一行
        using var json = JsonDocument.Parse(line); // 而且是合法 JSON
        Assert.Contains("换行", json.RootElement.GetProperty("msg").GetString()!);
    }

    [Fact]
    public async Task 低于阈值的日志不落盘()
    {
        using var dir = new TempDir();
        var logger = new FileLogger(
            new FileLogOptions(dir.Path, "vidlog", MinLevel: LogLevel.Warn));

        logger.Log(LogLevel.Debug, "录制", "开发细节");
        logger.Log(LogLevel.Info, "录制", "普通信息");
        logger.Log(LogLevel.Warn, "录制", "值得看一眼");
        await logger.DisposeAsync();

        // 生产上开 DEBUG 会把日志淹掉，而**淹掉的日志等于没有日志**。
        var line = Assert.Single(await File.ReadAllLinesAsync(logger.Path));
        Assert.Contains("值得看一眼", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 一次写很多条_一条都不少也不乱序()
    {
        // 走的是「排干再刷」那条批量路径。少了顺序保证的话，
        // 「先开录后停录」会被读成反过来 —— 那比丢一条更难发现。
        using var dir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(dir.Path, "vidlog"));

        for (var i = 0; i < 200; i++)
        {
            logger.Log(LogLevel.Info, "录制", $"第 {i} 条");
        }

        await logger.DisposeAsync();

        var lines = await File.ReadAllLinesAsync(logger.Path);
        Assert.Equal(200, lines.Length);

        for (var i = 0; i < 200; i++)
        {
            Assert.Contains($"第 {i} 条", lines[i], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 日志文件被删掉之后_下一条会把它重新建出来()
    {
        // ⚠️ **常开句柄带来的新失败模式**：文件被外部删掉（用户清理、备份工具）之后，
        // 句柄还指着那个已经不存在的对象继续写 —— 表现是**日志静默消失**，
        // 而且没有任何一处会报错。
        using var dir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(dir.Path, "vidlog"));

        logger.Log(LogLevel.Info, "录制", "第一条");
        await logger.FlushAsync();
        Assert.True(File.Exists(logger.Path));

        File.Delete(logger.Path);

        logger.Log(LogLevel.Info, "录制", "删掉之后这一条");
        await logger.FlushAsync();

        Assert.True(File.Exists(logger.Path), "被删掉之后必须自己长回来");
        var line = Assert.Single(await File.ReadAllLinesAsync(logger.Path));
        Assert.Contains("删掉之后这一条", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 凭据塞进_credential_键里_落盘那一行读不到它()
    {
        // 诊断包会把 logs/* **整个**打包外发，所以「写盘时就是干净的」这件事
        // 必须在**这一层**成立 —— 导出时再过滤就是第二个过滤器了。
        using var dir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(dir.Path, "vidlog"));

        logger.Log(LogLevel.Info, "入网", "签发了凭据", new Dictionary<string, object?>
        {
            ["credential"] = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",
            ["deviceId"] = "phone-1",
        });
        await logger.DisposeAsync();

        var line = Assert.Single(await File.ReadAllLinesAsync(logger.Path));
        Assert.DoesNotContain("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8", line, StringComparison.Ordinal);
        Assert.Contains(SensitiveName.Redacted, line, StringComparison.Ordinal);

        // 同一个对象里的其它字段照常写 —— 脱敏不该把整行变成一句空话。
        using var json = JsonDocument.Parse(line);
        Assert.Equal("phone-1", json.RootElement.GetProperty("data").GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task 非标量的值只留一个类型名_不反射出它的字段()
    {
        // ⚠️ **绊线**：这是结构层那一层，也是最硬的一层。
        // 反射式序列化会把 EnrolledDevice 的凭据原样写进日志 ——
        // 所以这里刻意不认识对象图，只写「<对象:Xxx>」。
        // 谁要是哪天图方便改成反射式序列化，这条会红。
        using var dir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(dir.Path, "vidlog"));

        logger.Log(LogLevel.Info, "入网", "看看这个对象",
            new Dictionary<string, object?> { ["设备"] = new FakeEnrolled() });
        await logger.DisposeAsync();

        var line = Assert.Single(await File.ReadAllLinesAsync(logger.Path));
        Assert.DoesNotContain("不该出现在日志里的凭据", line, StringComparison.Ordinal);
        Assert.Contains("<对象:FakeEnrolled>", line, StringComparison.Ordinal);
    }

    private sealed class FakeEnrolled
    {
        // 名字起得直白：它出现在落盘行里就是错。
        public string Credential { get; } = "不该出现在日志里的凭据";
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
