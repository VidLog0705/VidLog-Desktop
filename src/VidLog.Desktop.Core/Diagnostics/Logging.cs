using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

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
/// <para>
/// 2026-09-26 补结构化、级别阈值、出入记录、脱敏与全局异常兜底时**复核过这一条**：
/// 那几件没有一件是日志库能替你做的（都是接线与格式），
/// 而它们要改的地方（<see cref="FileLogOptions.MinLevel"/>、JSON 行、
/// <c>Diagnostics.Sanitizer</c>、<c>App/Platform/CrashGuard.cs</c>）
/// 全是这个接口与这个实现自己的事。所以结论不变。
/// </para>
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

/// <param name="RetainDays">留几天。<see cref="LogRetention.SelectExpired"/> 用它挑该删的。</param>
/// <param name="MinLevel">
/// 低于这个级别的一条都不写。默认 <see cref="LogLevel.Debug"/> = 全写
/// （改动前的行为，测试与临时排查要它）；**组合根在正式运行时应传
/// <see cref="LogLevel.Info"/>** —— 生产上开着 DEBUG，日志会被淹掉，
/// 而淹掉的日志等于没有日志。
/// </param>
public sealed record FileLogOptions(
    string Directory,
    string Prefix = "vidlog",
    int RetainDays = 14,
    LogLevel MinLevel = LogLevel.Debug)
{
    public string FileNameFor(DateTimeOffset at) => $"{Prefix}-{at:yyyyMMdd-HHmmss}.log";
}

/// <summary>
/// 追加写的文件日志。<b>一行一个 JSON 对象。</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>任何 I/O 异常都吞掉并降级</b>：日志坏了绝不能拖垮录制（I4 的同一条精神）。
/// 磁盘满、目录只读、被别的进程占住、**文件被外部删掉** —— 这些情况下用户要的是
/// 「录像还在跑」，而不是一个因为写不进日志就崩掉的程序。
/// </para>
/// <para>
/// 写入走后台队列：录制路径上不该有人等磁盘。队列满了丢最旧的，
/// 并且**按批写盘**（排干当前排队的再一次 flush）——
/// 请求风暴时不会变成「一行一次 open/write/close」。
/// </para>
/// <para>
/// 格式选了 JSON 而不是人读的一行文本：诊断要能**按字段查**
/// （「这台机器昨天有没有 5xx」「哪几次收尾失败」），而 grep 一段自由文本做不到。
/// 用 <see cref="Utf8JsonWriter"/> 而不是字符串拼接 —— <c>message</c> 里一个引号
/// 或一个换行就能把整份 JSONL 弄坏。
/// </para>
/// </remarks>
public sealed class FileLogger : IAppLogger, IAsyncDisposable
{
    /// <summary>队列里的一项：要么是一行日志，要么是一个「写到这里为止」的屏障。</summary>
    private readonly record struct Entry(string? Line, TaskCompletionSource? Barrier);

    private readonly FileLogOptions _options;
    private readonly string _path;

    private readonly System.Threading.Channels.Channel<Entry> _queue =
        System.Threading.Channels.Channel.CreateBounded<Entry>(
            new System.Threading.Channels.BoundedChannelOptions(4096)
            {
                // 满了就丢最旧的：日志可以丢，录制不能停。
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

    private readonly Task _writer;

    /// <summary>JSON 写法的设置。</summary>
    /// <remarks>
    /// ⚠️ <b>编码器必须换掉</b>：默认那个（<c>JavaScriptEncoder.Default</c>）会把
    /// 非 ASCII 全部转义 —— 一句中文消息会变成六个 <c>u</c> 加十六进制的转义序列，
    /// 时间戳里那个加号也会。那仍然是**合法**的 JSON，但**这份文件既没法读、
    /// 也没法 grep**（`grep 开录` 一条都搜不到），而它是要人打开来看的诊断文件。
    /// 这一条是写测试时撞出来的：断言读回来的原文里能找到「开录了」，红了。
    /// <para>
    /// 「Unsafe」指的是不转义 HTML 敏感字符（<c>&lt;</c>、<c>&gt;</c>、<c>&amp;</c>、<c>+</c>）——
    /// 这份文件不会被塞进 HTML 或 JS 里执行（WPF 的 TextBlock 也不执行标记），
    /// 所以那一层的风险在这里不存在，换来的是「看得懂」。
    /// </para>
    /// </remarks>
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public FileLogger(FileLogOptions options)
    {
        _options = options;
        _path = System.IO.Path.Combine(options.Directory, options.FileNameFor(DateTimeOffset.Now));

        // ⚠️ 这里**不建目录、不开文件**：开不了的时候要能继续跑（见类注释）。
        // 真正的创建放到写盘那一刻，失败了就丢那一批。
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
        // 低于阈值直接丢在调用线程上 —— 生产上开 DEBUG 会把日志淹掉，
        // 而**淹掉的日志等于没有日志**。
        if (level < _options.MinLevel)
        {
            return;
        }

        // TryWrite 不阻塞。写不进去（队列满/已关）就是丢一条日志，不抛。
        _queue.Writer.TryWrite(new Entry(Format(level, category, message, data), null));
    }

    /// <summary>
    /// **同步**写一条，绕过队列。给「进程马上就要死」那一处用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么不能走队列：未捕获异常之后进程随时会被终止，
    /// <see cref="DisposeAsync"/> 那两秒的等待**根本等不到**，
    /// 于是**最要紧的那一行（带堆栈的遗言）恰好落不下**。
    /// 这一条必须当场落盘，所以它是同步的。
    /// </para>
    /// <para>
    /// ⚠️ 它**不受 <see cref="FileLogOptions.MinLevel"/> 限制**：阈值是给日常噪声用的，
    /// 不是给遗言的。同样地，写不进去也**不抛** —— 崩溃路径上再抛一次更没意义。
    /// </para>
    /// </remarks>
    public void WriteSync(
        LogLevel level, string category, string message, IReadOnlyDictionary<string, object?>? data = null)
    {
        var line = Format(level, category, message, data ?? new Dictionary<string, object?>());

        try
        {
            Directory.CreateDirectory(_options.Directory);
            File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ObjectDisposedException)
        {
            // 遗言写不下去也没办法了。
        }
    }

    /// <summary>
    /// 把**调用这一刻已经在队列里的**行写完并落盘。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它不保证队列里没有正在飞的生产者，只保证「之前入队的都写出去了」。
    /// 给「崩溃前最后一条」与测试用。
    /// </remarks>
    public async Task FlushAsync()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_queue.Writer.TryWrite(new Entry(null, barrier)))
        {
            // 队列已经关了（Dispose 之后）—— 没有东西要等。
            return;
        }

        try
        {
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // 刷不动就算了 —— 日志不值得卡住调用方。
        }
    }

    /// <summary>一行 JSON。**非标量值不反射**（见 <see cref="WriteValue"/>）。</summary>
    private static string Format(
        LogLevel level, string category, string message, IReadOnlyDictionary<string, object?> data)
    {
        var buffer = new ArrayBufferWriter<byte>(256);

        try
        {
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                // 带偏移量的 "O"：不带的话，同一个诊断包里两台机器的 17:10 是歧义的。
                writer.WriteString("ts", DateTimeOffset.Now.ToString("O"));
                writer.WriteString("lvl", Level(level));
                writer.WriteString("cat", category);
                writer.WriteString("msg", Sanitizer.Sanitize(message));

                if (data.Count > 0)
                {
                    writer.WriteStartObject("data");
                    foreach (var (key, value) in data)
                    {
                        // 键名命中密钥类 ⇒ **连值都不看**，直接写占位。
                        if (Sanitizer.RedactionFor(key) is { } placeholder)
                        {
                            writer.WriteString(key, placeholder);
                            continue;
                        }

                        WriteValue(writer, key, value);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }
        }
        catch (Exception ex)
        {
            // 记日志本身出岔子**绝不能往外抛**（I4）。退化成一条最小可用的行，
            // 并把出错类型写进去 —— 静默丢一条比丢一条还说不清更糟。
            return Fallback(level, category, message, ex);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>写一个 value。**只认标量，其余一律写类型名占位。**</summary>
    /// <remarks>
    /// ⚠️ 这里**刻意不反射**：反射式序列化会把 <c>DeviceIdentity</c> / <c>EnrolledDevice</c>
    /// 的凭据原样写进日志，而诊断包会自动把 <c>logs/*</c> 打包外发。
    /// 写成一个「&lt;对象:DeviceIdentity&gt;」占位是**已知会漏**的一侧：
    /// 少一条诊断信息，好过凭据出差。
    /// </remarks>
    private static void WriteValue(Utf8JsonWriter writer, string key, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(key);
                break;
            case string s:
                // 字符串一律过一遍值层脱敏：一个叫「内容」的键里塞了凭据，
                // 键名判据看不出来，登记过的值能看出来。
                writer.WriteString(key, Sanitizer.Sanitize(s));
                break;
            case bool b:
                writer.WriteBoolean(key, b);
                break;
            case short or int or long or byte or sbyte or ushort or uint or ulong:
                writer.WriteNumber(key, Convert.ToInt64(value));
                break;
            case float or double or decimal:
                writer.WriteNumber(key, Convert.ToDouble(value));
                break;
            case TimeSpan t:
                writer.WriteString(key, t.ToString());
                break;
            case DateTimeOffset o:
                writer.WriteString(key, o.ToString("O"));
                break;
            case DateTime dt:
                writer.WriteString(key, dt.ToString("O"));
                break;
            default:
                writer.WriteString(key, $"<对象:{value.GetType().Name}>");
                break;
        }
    }

    private static string Fallback(
        LogLevel level, string category, string message, Exception error)
    {
        var buffer = new ArrayBufferWriter<byte>(128);

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", DateTimeOffset.Now.ToString("O"));
            writer.WriteString("lvl", Level(level));
            writer.WriteString("cat", category);
            writer.WriteString("msg", message);
            writer.WriteStartObject("data");
            writer.WriteString("记日志出错", error.GetType().Name);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync())
            {
                var batch = new StringBuilder();
                List<TaskCompletionSource>? barriers = null;

                while (_queue.Reader.TryRead(out var entry))
                {
                    if (entry.Line is not null)
                    {
                        batch.Append(entry.Line).Append('\n');
                    }

                    if (entry.Barrier is not null)
                    {
                        (barriers ??= []).Add(entry.Barrier);
                    }
                }

                if (batch.Length > 0)
                {
                    await AppendAsync(batch.ToString());
                }

                // 屏障在**它前面那些行写完之后**才放行 —— 顺序不能反。
                if (barriers is not null)
                {
                    foreach (var barrier in barriers)
                    {
                        barrier.TrySetResult();
                    }
                }
            }
        }
        catch (Exception)
        {
            // 读循环本身出问题也不该把进程带下去。
        }
    }

    /// <summary>写一批，失败就丢掉这一批。</summary>
    /// <remarks>
    /// ⚠️ <b>按批开关文件，不留常开句柄。</b> 常开句柄会带来两个坏法，都不报错：
    /// <list type="number">
    /// <item><b>把外部读取者挡在外面</b>：<c>File.ReadAllLines</c>（以及记事本、
    /// 支持脚本）会拿到「文件正被另一个进程使用」—— 一份**打不开的日志**等于没有日志。
    /// 改动前是每行开一次关一次，所以读得到；按批开关与那时的代价相当，
    /// 而一个突发里的 N 行合并成一次开关，只会更好。</item>
    /// <item>句柄可能指着**已经被删掉的文件**继续写，日志静默消失。</item>
    /// </list>
    /// </remarks>
    private async Task AppendAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        // 两次：第一次失败多半是文件正被别的程序占着（杀毒、编辑器），
        // 隔一拍再来一次；再失败就丢 —— 日志可以丢，录制不能停。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.CreateDirectory(_options.Directory);

                await using var stream = new FileStream(
                    _path,
                    FileMode.Append,
                    FileAccess.Write,
                    // 别人可以读（诊断包、记事本），也可以删（用户清理）。
                    // 删了没关系：下一次写盘 FileMode.Append 会把它重新建出来。
                    FileShare.Read | FileShare.Delete);

                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ObjectDisposedException)
            {
                // 落到下一次尝试。
            }
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "?",
    };

    /// <summary>删掉过期的日志文件，返回删掉了几个。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 这个方法是 <see cref="LogRetention.SelectExpired"/>（纯函数、有测试）
    /// 与文件系统之间的**那一跳**。2026-09-26 之前**那一跳根本不存在** ——
    /// <c>SelectExpired</c> 在 <c>src</c> 里零调用点，于是「保留 N 天」是个死值：
    /// 日志只增不减。这与「装配的最后一跳」是同一类毛病：
    /// **方法写对了、测过了，没人调它。**
    /// </para>
    /// <para>
    /// 删不动（正被别的进程占着、没权限）就跳过那一个 —— 一次清理失败绝不是错误。
    /// </para>
    /// </remarks>
    public static int PurgeExpired(FileLogOptions options, DateTimeOffset now)
    {
        // 保留期小于一天时**整个不删**：那种配置下「过期」会把今天这个正在写的文件
        // 也算进去，而删掉正在写的日志是纯损失。`AppSettings.IsPlausible` 已经把
        // 下限定在 1 天，这里再挡一道 —— 这个参数是能被直接构造的。
        if (options.RetainDays < 1)
        {
            return 0;
        }

        try
        {
            if (!Directory.Exists(options.Directory))
            {
                return 0;
            }

            var names = Directory
                .EnumerateFiles(options.Directory)
                .Select(System.IO.Path.GetFileName)
                .OfType<string>()
                .ToList();

            var deleted = 0;
            foreach (var name in LogRetention.SelectExpired(names, options, now))
            {
                try
                {
                    File.Delete(System.IO.Path.Combine(options.Directory, name));
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 这一个删不掉就留着 —— 下次启动再试。
                }
            }

            return deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

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
