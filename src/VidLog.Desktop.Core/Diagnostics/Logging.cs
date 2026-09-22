using System.Text;

namespace VidLog.Desktop.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// 关键操作留痕（<c>AGENTS.md</c> §6）。
/// </summary>
/// <remarks>
/// 刻意做得很小：需求只有三条 —— 关键操作留痕、能导出诊断包、轮转按文件名里的
/// 时间戳排序。引 NLog / Serilog 换不来这三条之外的任何东西，却会带来
/// 一个新依赖与一次许可证核对。
/// </remarks>
public interface IAppLogger
{
    void Log(LogLevel level, string category, string message);

    /// <summary>附一段诊断数据（键值对）。</summary>
    void Log(LogLevel level, string category, string message, IReadOnlyDictionary<string, object?> data);
}

/// <summary>不记任何东西。给测试与「日志不可用」的降级路径用。</summary>
public sealed class NullLogger : IAppLogger
{
    public static IAppLogger Instance { get; } = new NullLogger();

    private NullLogger()
    {
    }

    public void Log(LogLevel level, string category, string message)
    {
    }

    public void Log(LogLevel level, string category, string message, IReadOnlyDictionary<string, object?> data)
    {
    }
}

public sealed record FileLogOptions(
    string Directory,
    string Prefix = "vidlog",
    int RetainDays = 14)
{
    public string FileNameFor(DateTimeOffset at) => $"{Prefix}-{at:yyyyMMdd-HHmmss}.log";
}

/// <summary>
/// 追加写的文件日志。
/// </summary>
/// <remarks>
/// <para>
/// <b>任何 I/O 异常都吞掉并降级</b>：日志坏了绝不能拖垮录制（I4 的同一条精神）。
/// 磁盘满、目录只读、被别的进程占住 —— 这些情况下用户要的是「录像还在跑」，
/// 而不是一个因为写不进日志就崩掉的程序。
/// </para>
/// <para>
/// 写入走后台队列：录制路径上不该有人等磁盘。
/// </para>
/// </remarks>
public sealed class FileLogger : IAppLogger, IAsyncDisposable
{
    private readonly FileLogOptions _options;
    private readonly string _path;
    private readonly System.Threading.Channels.Channel<string> _queue =
        System.Threading.Channels.Channel.CreateBounded<string>(
            new System.Threading.Channels.BoundedChannelOptions(4096)
            {
                // 满了就丢最旧的：日志可以丢，录制不能停。
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

    private readonly Task _writer;

    public FileLogger(FileLogOptions options)
    {
        _options = options;
        _path = System.IO.Path.Combine(options.Directory, options.FileNameFor(DateTimeOffset.Now));

        try
        {
            Directory.CreateDirectory(options.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _writer = Task.Run(WriteLoopAsync);
    }

    public string Path => _path;

    /// <summary>文件名里带时间戳 —— 轮转与保留期都靠它，不靠文件系统的修改时间。</summary>
    public static string FileNameFor(FileLogOptions options, DateTimeOffset at) => options.FileNameFor(at);

    public void Log(LogLevel level, string category, string message) =>
        Log(level, category, message, new Dictionary<string, object?>());

    public void Log(
        LogLevel level, string category, string message, IReadOnlyDictionary<string, object?> data)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(Level(level)).Append(']')
            .Append(" [").Append(category).Append("] ")
            .Append(message);

        if (data.Count > 0)
        {
            line.Append(" | ");
            line.Append(string.Join(", ", data.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        // TryWrite 不阻塞。写不进去（队列满/已关）就是丢一条日志，不抛。
        _queue.Writer.TryWrite(line.ToString());
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    await File.AppendAllTextAsync(_path, line + Environment.NewLine);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 写不进去就丢这一条 —— 录制比日志重要。
                }
            }
        }
        catch (Exception)
        {
            // 读循环本身出问题也不该把进程带下去。
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "?????",
    };

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try
        {
            await _writer.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // 关得慢不该卡住退出。
        }
    }
}

/// <summary>
/// 日志保留期的挑选。
/// </summary>
/// <remarks>
/// ⚠️ <b>必须按文件名里的时间戳排序，不能按文件名字符串排</b> ——
/// 这是 <c>AGENTS.md</c> §6 点名的坑：按名字倒序会把 <c>vidlog-2</c> 当成比
/// <c>vidlog-10</c> 更新，于是**删掉最新的、留下最旧的**。
/// </remarks>
public static class LogRetention
{
    /// <summary>挑出该删的日志文件（只返回文件名，不动文件系统）。</summary>
    public static IReadOnlyList<string> SelectExpired(
        IEnumerable<string> fileNames, FileLogOptions options, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-options.RetainDays);
        var expired = new List<string>();

        foreach (var name in fileNames)
        {
            if (!TryParseTimestamp(name, options.Prefix, out var at))
            {
                // 解析不出时间戳的文件**不动** —— 不知道它是什么，删它就是赌。
                continue;
            }

            if (at < cutoff)
            {
                expired.Add(name);
            }
        }

        return expired;
    }

    /// <summary>从 <c>&lt;prefix&gt;-&lt;yyyyMMdd-HHmmss&gt;.log</c> 里取出时间戳。</summary>
    public static bool TryParseTimestamp(string fileName, string prefix, out DateTimeOffset at)
    {
        at = default;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var expected = prefix + "-";
        if (!stem.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stamp = stem[expected.Length..];
        return DateTimeOffset.TryParseExact(
            stamp, "yyyyMMdd-HHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal,
            out at);
    }
}
