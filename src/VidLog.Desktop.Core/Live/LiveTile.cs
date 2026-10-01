using System.Net.Http;
using System.Text.Json;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Live;

/// <summary>手机报上来的当场计数（多画面那一格下面的 <c>F</c> / <c>T</c>）。</summary>
/// <param name="Outbound">发货（绿的那个 F）。</param>
/// <param name="Returned">退货（红的那个 T）。</param>
/// <param name="ReportedQuality">手机**自己说**它现在是哪一档。</param>
/// <remarks>
/// ⚠️ <paramref name="ReportedQuality"/> 是给诊断用的，不是给我们用的：
/// 用户抱怨「明明选了 1080P 怎么还是糊」时，把它和我们以为的那一档一比，
/// 就知道是**改档没生效**还是**那一格本来就该糊**。
/// </remarks>
public sealed record LiveCounts(int Outbound, int Returned, int? ReportedQuality);

/// <summary>
/// 多画面里的**一格**：一路画面 + 那台手机的计数 + 它现在的画质档。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>「无信号输入」不是一个错误状态</b>，它是这一格**正常的一种样子** ——
/// 机位不够（用户选了 3 格但只有 2 台手机）时就是这样。所以
/// <see cref="Latest"/> 为 <see langword="null"/> 时界面画那四个字，
/// 而不是画一个报错。
/// </para>
/// <para>
/// ⚠️ <b>本地解码尺寸跟着画质档走，不另开一个旋钮。</b>格子里那一格会被 WPF 缩小显示，
/// 但**解码**是按档来的：480P 解出来的是 854×480。多一个「本地尺寸」参数意味着
/// 两处各自可调、而它们其实永远该一致 —— 那是白送的出错机会。
/// </para>
/// </remarks>
public sealed class LiveTile : IAsyncDisposable
{
    private readonly string _ffmpeg;
    private readonly string _baseUrl;
    private readonly IAppLogger _logger;
    private readonly HttpClient _http;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private LiveTileProcess? _process;
    private LiveCounts? _counts;

    /// <summary>上一次问计数是不是失败着（用来把重复的失败**去重成两条**：变坏一条、变好一条）。</summary>
    private bool _statusFailing;
    private LiveQuality _quality;
    private string? _problem;
    private int _disposed;

    private LiveTile(
        string ffmpeg, string baseUrl, string name, LiveQuality quality, IAppLogger logger, HttpClient http)
    {
        _ffmpeg = ffmpeg;
        _baseUrl = baseUrl;
        _logger = logger;
        _http = http;

        Name = name;
        _quality = quality;
    }

    /// <summary>这一格上面显示的名字（机位名）。</summary>
    public string Name { get; }

    /// <summary>**这一格自己**的地址（`http://<ip>:<port>`），改档与计数都打给它。</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>现在这一档。</summary>
    public LiveQuality Quality => _quality;

    /// <summary>最新一帧；**还没有就是「无信号输入」**。</summary>
    public LiveFrame? Latest() => _process?.Latest();

    /// <summary>那台手机报上来的计数；还没问到就是 <see langword="null"/>。</summary>
    public LiveCounts? Counts => _counts;

    /// <summary>
    /// 这一格为什么没画面（起不来、断了）。**不是**「无信号输入」——
    /// 那是「手机没开实时共享」，这个是「这边出了问题」，两句话不一样。
    /// </summary>
    public string? Problem => _problem;

    /// <summary>起一格。⚠️ 立刻返回，画面是异步来的 —— 界面先画「无信号输入」。</summary>
    public static LiveTile Start(
        string ffmpeg,
        string baseUrl,
        string name,
        LiveQuality quality = LiveQualityExtensions.Tile,
        IAppLogger? logger = null,
        HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpeg);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var tile = new LiveTile(
            ffmpeg,
            baseUrl.TrimEnd('/'),
            name,
            quality,
            logger ?? NullLogger.Instance,
            http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) });

        tile.RestartVideo();

        return tile;
    }

    /// <summary>画质档要的那对尺寸（16:9，宽在前）。</summary>
    /// <remarks>
    /// ⚠️ 宽高都取**偶数**：ffmpeg 的 <c>scale</c> 配 yuv420p 时奇数尺寸会被它
    /// 自己抹成偶数，而抹的方向不必与另一半一致 —— 那是白白换来的一像素错位。
    /// </remarks>
    public static (int Width, int Height) SizeFor(LiveQuality quality) => quality switch
    {
        LiveQuality.P480 => (854, 480),
        LiveQuality.P720 => (1280, 720),
        _ => (1920, 1080),
    };

    /// <summary>问一次那台手机的计数（规格 §3.8 那两个字）。</summary>
    public async Task RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var body = await _http.GetStringAsync($"{_baseUrl}/status", cancellationToken);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            _counts = new LiveCounts(
                root.TryGetProperty("f", out var f) ? f.GetInt32() : 0,
                root.TryGetProperty("t", out var t) ? t.GetInt32() : 0,
                root.TryGetProperty("p", out var p) ? p.GetInt32() : null);

            _problem = null;

            if (_statusFailing)
            {
                _statusFailing = false;
                _logger.Log(LogLevel.Info, "多画面", $"又能问到计数了：{Name}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // ⚠️ 问不到计数**不影响画面**：那一格照样在放，只是那两个数先显示 `–`。
            // 把整格当成坏了的话，网络抖一下整面墙都会闪一下「无信号输入」。
            _counts = null;

            // ⚠️ **只在「从问得到变成问不到」那一刻记一条**（§6.1 的配套要求：
            // 重复的问题只在「变了」的时候记）。这个是每秒跑一次的，
            // 每次都记的话一分钟六十条，而被灌满的日志等于没有日志。
            if (!_statusFailing)
            {
                _statusFailing = true;
                _logger.Log(
                    LogLevel.Warn, "多画面",
                    $"问不到计数了：{Name} —— {ex.Message}（画面不受影响，那两个数先显示 -）");
            }
        }
    }

    /// <summary>
    /// 改档（进出全屏时用）。返回是否真的改成了。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>先告诉手机、成了才动本地。</b>反过来的话：手机没改成而本地按新档解，
    /// 那一格会一直等一个永远不来的分辨率 —— 看起来像卡死。
    /// </remarks>
    public async Task<bool> SetQualityAsync(LiveQuality quality, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (ObjectDisposedCheck()) return false;

            using var response = await _http.GetAsync(
                $"{_baseUrl}/quality?p={quality.Height()}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Log(
                    LogLevel.Warn, "多画面",
                    $"改档被拒（HTTP {(int)response.StatusCode}）：{Name} 要 {quality.Label()}");
                return false;
            }

            if (_quality != quality)
            {
                _quality = quality;
                RestartVideo();
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.Log(LogLevel.Warn, "多画面", $"改档没送到：{Name} —— {ex.Message}");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按当前档重起本地那一路。（改档、以及第一次起来时用。）</summary>
    private void RestartVideo()
    {
        var old = _process;
        _process = null;

        if (old is not null) _ = old.DisposeAsync();

        var (width, height) = SizeFor(_quality);

        // 「无信号输入」是常态（手机没开实时共享），所以起不来**不是错误** ——
        // 记一条就够，界面画那四个字。
        _process = LiveTileProcess.Start(_ffmpeg, $"{_baseUrl}/live", width, height, _logger);

        if (_process is null)
        {
            _problem = "这一格的画面起不来（本机 ffmpeg 没跑起来）。";
        }
    }

    private bool ObjectDisposedCheck() => Volatile.Read(ref _disposed) != 0;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        var process = _process;
        _process = null;

        if (process is not null) await process.DisposeAsync();

        _gate.Dispose();
    }
}
