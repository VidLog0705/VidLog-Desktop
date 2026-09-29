using System.Diagnostics;
using System.Globalization;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Camera;

/// <summary>
/// 麦克风电平监视：读出一路音频的**实时电平**，供一条音量条用。
/// </summary>
/// <remarks>
/// <para>
/// 用在配置向导第 5 步「选择麦克风」——设计图上那行小字写着
/// 「说话时音量条有跳动，就说明麦克风基础配置正常」，所以它不是装饰：
/// **它是那一步唯一的判据**。
/// </para>
/// <para>
/// ⚠️ <b>它只开音频，不开画面</b>（与预览进程分开）：这样麦克风那一步不必
/// 去抢相机（dshow 独占），而且起得快。
/// </para>
/// <para>
/// ⚠️ <b>电平取 <c>astats</c> 的 RMS</b>（dBFS，0 = 满刻度、负得越多越静），
/// 而不是峰值：峰值在说话间隙会瞬间掉到极小，条会闪得没法看；
/// RMS 是短时能量，跳动的样子与设计图上那条一致。
/// </para>
/// <para>
/// ⚠️ <b>本机没有麦克风</b>（2026-09-29 实测：`Could not enumerate audio only devices`）
/// ⇒ 「真麦克风上跳不跳」这一档**没验过**。验过的是**解析与映射**
/// （用 <c>sine</c> 合成源喂它，见 <c>MicrophoneLevelMonitorTests</c>）。
/// </para>
/// </remarks>
public sealed class MicrophoneLevelMonitor : IAsyncDisposable
{
    /// <summary>
    /// 静音门槛（dBFS）。
    /// </summary>
    /// <remarks>
    /// 低于它的电平一律当 0 —— 不设的话，底噪（通常 -60 dB 上下）会把条
    /// 顶在「有一点动静」的位置，而那**看起来像麦克风在工作**。
    /// 这一档只有真机能标定，先取一个常见的下限。
    /// </remarks>
    public const double FloorDb = -60;

    /// <summary>每读一次电平的间隔（毫秒）。</summary>
    /// <remarks>
    /// ⚠️ `astats` 是**按音频帧**统计的，`reset=1` 让它每帧重算 ——
    /// 间隔由 `-af` 的帧长决定，不是由这里决定。这个值只用于**读端**的节流。
    /// </remarks>
    private const int Scope = 1;

    private readonly Process _process;
    private readonly BoundedTextTail _errors;
    private readonly Task _readLoop;

    /// <summary>与读循环共享的那一格（见 <see cref="MonitorState"/>）。</summary>
    private readonly MonitorState _state;

    private readonly IAppLogger? _logger;

    private int _stopped;

    private MicrophoneLevelMonitor(
        Process process, BoundedTextTail errors, Task readLoop, MonitorState state,
        IAppLogger? logger)
    {
        _process = process;
        _errors = errors;
        _readLoop = readLoop;
        _state = state;
        _logger = logger;
    }

    /// <summary>最近的电平，<c>0.0</c–<c>1.0</c>。</summary>
    public double Level => Volatile.Read(ref _state.Latest) / 1000.0;

    /// <summary>ffmpeg 说的最后一句话（起不来时用它给用户一个原因，I3）。</summary>
    public string ErrorTail => _errors.ToString();

    /// <summary>起一个电平监视进程。</summary>
    public static Task<MicrophoneLevelMonitor> StartAsync(
        string ffmpegPath, string microphone,
        IAppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 承重：不重定向 stdin 就没法用 q 优雅停止。
            RedirectStandardInput = true,
        };

        foreach (var argument in BuildArguments(microphone))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();

        var errors = new BoundedTextTail();
        _ = Task.Run(() => DrainAsync(process.StandardError, errors));

        // ⚠️ 电平走 **stdout**（`ametadata=print` 打在那儿），
        // 所以 stderr 留给「起不来时的原因」—— 两条路不打架。
        //
        // ⚠️ 共享的那一格要**先在本地造出来、再交给读循环**：读循环比这个实例先存在，
        // 让它捕获 `_state` 的话会捕获到一个还没赋值的字段。
        // （我第一版就是这么写的，而且顺手多写了一个**从没人写**的字段 ——
        // 结果是 `Level` 恒为 0、音量条永远不动。测试里那条「电平真的会变」抓住了它。）
        var state = new MonitorState();
        var readLoop = Task.Run(() => ReadAsync(process.StandardOutput, state));

        cancellationToken.ThrowIfCancellationRequested();

        logger?.Log(LogLevel.Info, "音量", $"开始监听 {microphone}");

        return Task.FromResult(new MicrophoneLevelMonitor(process, errors, readLoop, state, logger));
    }

    /// <summary>监视的命令。单独抽出来是为了让参数能被断言。</summary>
    public static IReadOnlyList<string> BuildArguments(string microphone)
    {
        if (string.IsNullOrWhiteSpace(microphone))
        {
            throw new ArgumentException("麦克风名字不能是空的", nameof(microphone));
        }

        var arguments = new List<string> { "-hide_banner", "-loglevel", "error" };

        // ⚠️ 与录制那一档**用同一个函数**拼音频输入参数（见它的说明）。
        arguments.AddRange(FfmpegCameraCapture.AudioInputArguments(microphone, "64M"));

        arguments.AddRange(
        [
            // ⚠️ `reset=1` 是承重的：不给它的话 astats 只在整个流结束时汇总一次，
            // 于是音量条**从头到尾一动不动**（而它看起来像「麦克风坏了」）。
            "-af", $"astats=metadata=1:reset={Scope},"
                + $"ametadata=print:key=lavfi.astats.Overall.RMS_level:file=-",
            "-f", "null",
            "-",
        ]);

        return arguments;
    }

    /// <summary>停下并等到进程真的退出。**幂等**（理由同 <see cref="PreviewProcess"/>）。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync("q");
                await _process.StandardInput.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // stdin 已经不通了 —— 走下面的等待。
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 已经退了，竞态。
            }
        }

        await Task.WhenAny(_readLoop, Task.Delay(TimeSpan.FromSeconds(2)));

        // ⚠️ ffmpeg 说过话就记一条：麦克风打不开的原因（被占用、名字不对）
        // **只在它那儿**。空的时候不记（正常停下来没有话说）。
        if (ErrorTail.Trim() is { Length: > 0 } said)
        {
            _logger?.Log(LogLevel.Warn, "音量", $"电平监视说过：{said}");
        }

        _process.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    /// <summary>
    /// 把 <c>lavfi.astats.Overall.RMS_level=-23.5</c> 这样的行解析成 0–1 的电平。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>静音时 astats 报的是 <c>-inf</c></b>（不是一个大负数）——
    /// 直接 <c>double.Parse</c> 会抛，或者在某些区域设置下解析成 <c>NaN</c>。
    /// 必须当成「静」而不是「异常」：麦克风那一步**没人说话时本来就该是静的**。
    /// </para>
    /// <para>
    /// 映射：<see cref="FloorDb"/>（-60dB）以下算 0，0dB 算 1，中间线性。
    /// </para>
    /// </remarks>
    public static double ParseLevel(string line)
    {
        var marker = line.IndexOf("RMS_level=", StringComparison.Ordinal);
        if (marker < 0)
        {
            return 0;
        }

        var text = line[(marker + "RMS_level=".Length)..].Trim();

        // `-inf` / `nan` / 空 ⇒ 静。
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var db)
            || double.IsNaN(db)
            || double.IsInfinity(db))
        {
            return 0;
        }

        if (db <= FloorDb)
        {
            return 0;
        }

        return Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }

    private static async Task ReadAsync(StreamReader reader, MonitorState state)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var level = ParseLevel(line);

                // 这里只更新状态；**电平的归属**由 StartAsync 那边的实例读。
                state.Latest = (int)Math.Round(level * 1000);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 进程退了就是结束，不是错误。
        }
    }

    private static async Task DrainAsync(StreamReader reader, BoundedTextTail sink)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                sink.Append(buffer, read);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 同上。
        }
    }

    /// <summary>
    /// 读端与实例之间的那格共享状态。
    /// </summary>
    /// <remarks>
    /// ⚠️ 用一个引用类型而不是直接写实例字段：<c>StartAsync</c> 里要**先起读循环、
    /// 再构造实例**（实例需要那个循环），而 lambda 捕获实例变量的话会捕获到
    /// 还没赋值的它。这是**写的时候就会踩到**的一类顺序问题。
    /// </remarks>
    private sealed class MonitorState
    {
        /// <summary>最近一次的电平（0–1000）。**字段**而不是属性 —— `Volatile.Read` 要它。</summary>
        public int Latest;
    }
}
