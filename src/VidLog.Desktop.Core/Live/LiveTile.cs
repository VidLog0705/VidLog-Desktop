using System.Net.Http;
using System.Text.Json;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Live;

/// <summary>手机报上来的当场计数（多画面那一格下面的 <c>F</c> / <c>T</c>）。</summary>
/// <param name="Outbound">发货（绿的那个 F）。</param>
/// <param name="Returned">退货（红的那个 T）。</param>
/// <param name="ReportedQuality">手机**自己说**它现在是哪一档。</param>
/// <param name="PhoneDropped">
/// 手机**编码那一侧**丢了多少帧（T11）。⚠️ 与 <paramref name="Outbound"/> 那一对
/// **不是一回事**：那两个是**业务计数**，这个是**推流健康度**。
/// </param>
/// <remarks>
/// ⚠️ <paramref name="ReportedQuality"/> 是给诊断用的，不是给我们用的：
/// 用户抱怨「明明选了 1080P 怎么还是糊」时，把它和我们以为的那一档一比，
/// 就知道是**改档没生效**还是**那一格本来就该糊**。
/// </remarks>
public sealed record LiveCounts(
    int Outbound, int Returned, int? ReportedQuality, int PhoneDropped = 0);

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
    /// <summary>
    /// 连着几次都没起来之后就**不再逐次播报**（但**照样接着试**）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这只是「说多少」，不是「试几次」。手机没开实时共享时重连永远不会成功，
    /// 每次都记的话九格一起刷、一分钟几十行，而被灌满的日志等于没有日志
    ///（§6.1 的配套要求）—— 所以过了这个数就闭嘴，只留 <see cref="Problem"/> 在屏幕上说话。
    /// </remarks>
    private const int QuietAfterRetries = 3;

    /// <summary>两次重连之间最多等多久。</summary>
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>多久没拿到新帧就算「这一格现在没有画面」。</summary>
    /// <remarks>
    /// ⚠️ 取 3 秒：格子那一路是 12 fps，这是三十多帧没来；而 ffmpeg 自己的读超时是
    /// 5 秒（<c>LiveTileProcess.BuildArguments</c>）—— 也就是**在它放弃之前**就先说实话。
    /// </remarks>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    private readonly string _ffmpeg;
    private readonly IAppLogger _logger;
    private readonly HttpClient _http;

    /// <summary>这一格现在指的地址。⚠️ **不是 readonly**：手机每开一次共享都是一个新端口（见 <see cref="Repoint"/>）。</summary>
    private string _baseUrl;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 管 <see cref="_process"/> 与 <see cref="_retry"/> 这一对。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它与 <see cref="_gate"/> 是**两把不同的锁**，管的东西不重叠：
    /// <c>_gate</c> 串的是 HTTP 那两件事（改档、关），<c>_life</c> 串的是
    /// 「本地这一路是谁」—— 断线重连是**从读循环那条线程**上来的，
    /// 与界面上点改档是两条线程，只靠 <c>_gate</c> 挡不住（它只在 await 时才排队）。
    /// 取用顺序永远只有 <c>_gate → _life</c> 一个方向。
    /// </remarks>
    private readonly Lock _life = new();

    private LiveTileProcess? _process;
    private LiveCounts? _counts;

    /// <summary>定时的重连（一次性；<see cref="Timer"/> 不占线程，比挂个 Task 少一样要收的东西）。</summary>
    private Timer? _retry;

    /// <summary>连着断了几次。出过画面的从 1 数起（下一次出画面又会归 1），一帧都没出过的接着累加。</summary>
    private int _failStreak;

    /// <summary>上一次问计数是不是失败着（用来把重复的失败**去重成两条**：变坏一条、变好一条）。</summary>
    private bool _statusFailing;

    /// <summary>上一次量到的接收帧率（T11）。<see langword="null"/> = 还没量出来。</summary>
    private double? _fps;

    /// <summary>上一次采样的那两个底数（帧数、时刻）。⚠️ 只在 <see cref="_life"/> 里动。</summary>
    private long _rateFrames;
    private long _rateAtMs;
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
    /// <remarks>
    /// ⚠️ 这是**看一眼**（问「有没有画面」用），帧不会少，也不会让
    /// <see cref="ScreenDropped"/> 变准。要「画一次」请用 <see cref="Take"/>。
    /// </remarks>
    public LiveFrame? Latest() => Accept(_process?.Latest());

    /// <summary>
    /// 最新一帧，**取走**：画的那一处用它（<c>MultiViewWindow</c> 的 <c>Pump</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>为什么必须分成两条路</b>：<see cref="ScreenDropped"/> 数的是
    /// 「新帧来的时候上一帧还没被取走」。画的那一处要是走 <see cref="Latest"/>
    /// （只看不拿），从第一帧起**每一帧都会被记成我们丢了** ——
    /// 真机上量到的是 1069 帧收进来、1068 帧「丢了」。而这个数是给
    /// 「手机没编出来」和「这台电脑画不过来」分家的，恒非零等于没有。
    /// 分开之后：真看一眼的地方（比如「这一格现在有没有画面」）仍旧不会
    /// 把帧吃掉 —— 两条路各归各的，不要合并。
    /// </para>
    /// <para>
    /// ⚠️ <b>返回空只代表「没有画面」</b>（从没出过、或者已经过期 —— 上面那句判的）。
    /// 「这一手还没轮到新帧」**不是**空：那时候拿回来的还是上一次那一帧。
    /// 这条对界面是承重的 —— 画帧那一路见空就抹画面写「无信号输入」，
    /// 而取帧的钟与出帧的钟各走各的（2026-10-08 量到 12 fps 的节拍有 **32.9%**
    /// 的手落在两帧中间），当成空就会一闪一闪地黑。
    /// </para>
    /// </remarks>
    public LiveFrame? Take() => Accept(_process?.Take());

    /// <summary>取/看一眼之后共同的那两步。</summary>
    /// <remarks>
    /// ⚠️ <b>老画面不算画面。</b>一路断了之后 <see cref="LiveTileProcess"/> 里还留着
    /// 最后那一帧，不判的话格子里会**冻着一张静止的旧图** —— 而它看起来与「正在看」
    /// 一模一样，用户会拿几分钟前的画面当作现在（2026-10-03 那一格连着几次起不来时
    /// 就是这个形状：屏幕上一直有图，其实早就断了）。
    /// </remarks>
    private LiveFrame? Accept(LiveFrame? frame)
    {
        if (frame is null
            || Environment.TickCount64 - frame.CapturedAtMs > StaleAfter.TotalMilliseconds)
        {
            return null;
        }

        // ⚠️ 有画面了 = 之前那句「为什么没画面」不再成立，撤掉。
        // 撤在这儿是因为**只有这里知道画面真的来了** —— 问得到计数、
        // TCP 连得上，都不代表有画面（那两件事在「手机没开共享」时照样成立）。
        _problem = null;

        return frame;
    }

    /// <summary>那台手机报上来的计数；还没问到就是 <see langword="null"/>。</summary>
    public LiveCounts? Counts => _counts;

    /// <summary>
    /// 这一格**实测**的接收帧率（T11 的「网络侧」那个数）。还没量出来是 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>它不是「ffmpeg 说它收了多少」，是我们真从管子上一帧一帧数出来的。</b>
    /// 两者本来是一回事（ffmpeg 的 <c>fps=</c> 那行说的也是它自己读出来的速率），
    /// 但数自己的**少一层解析、也不会因为 ffmpeg 换了输出格式就失准** ——
    /// 而 ffmpeg 的 stderr 在这个类里已经另作他用（错误尾巴）。
    /// </remarks>
    public double? ReceiveFps
    {
        get { lock (_life) { return _fps; } }
    }

    /// <summary>因为**我们这一头**没跟上而丢掉的帧数（T11 的「显示侧」）。</summary>
    public long ScreenDropped
    {
        get { lock (_life) { return _process?.DroppedCount ?? 0; } }
    }

    /// <summary>
    /// 量一次接收帧率。<b>每秒调一次</b>（`MultiViewWindow` 的计数那一拍顺手带上）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>做成「显式采样」而不是一个会自己算的属性</b>：帧率是**一段时间上的差**，
    /// 而属性会在任何一次读取时把底数推走 —— 同一秒里被读两次的话，
    /// 第二次量到的是一段几十毫秒的窗口，那个数会乱跳。
    /// </remarks>
    public void SampleReceiveRate()
    {
        var now = Environment.TickCount64;

        lock (_life)
        {
            var count = _process?.ReceivedCount ?? 0;

            if (_rateAtMs == 0)
            {
                // 第一次只记底数 —— 没有上一次数，算不出差值。
                _rateFrames = count;
                _rateAtMs = now;
                return;
            }

            var ms = now - _rateAtMs;
            if (ms < 250) return;

            var frames = count - _rateFrames;

            // ⚠️ 负数是**重连**（新进程从 0 数起）。当成「重新起算」，
            // 不显示一个负的帧率 —— 那比不显示更让人困惑。
            _fps = frames < 0 ? 0 : frames * 1000.0 / ms;
            _rateFrames = count;
            _rateAtMs = now;
        }
    }

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
                root.TryGetProperty("p", out var p) ? p.GetInt32() : null,
                // ⚠️ 读不到就是 0，不是「未知」：老的手机端（还没报 `d` 的那些）
                // 与「一台都没丢」在界面上长得一样 —— 而这里是**诊断**，
                // 一个多出来的「未知」态只会让人去查一个不存在的问题。
                root.TryGetProperty("d", out var d) ? d.GetInt32() : 0);

            // ⚠️ **这里不撤 `_problem`。** 计数问得到和有没有画面是两回事：
            // 手机没开实时共享时 `/status` 照样回 200，而那一格一帧都没有 ——
            // 在这里撤的话，屏幕上那句「为什么没画面」会被每秒清掉一次，
            // 用户只会看到一格黑着、没有任何解释。撤销改在 `Latest()`。

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

    /// <summary>
    /// 那台手机**换了地址**（它每开一次实时共享都绑一个新端口）—— 把这一格指过去。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>没有这个方法，多画面里每一格都会永远钉在开窗那一刻的地址上。</b>
    /// 而手机那边每一段推流都是新端口（`HttpServer.bind(anyIPv4, 0)`），
    /// 于是「手机明明在推、电脑端那一格却一直黑着」，唯一的出路是把窗口关掉重开 ——
    /// 2026-10-03 的日志里用户六分钟关了四次，那不是他手欠，那是**当时的唯一出路**。
    /// </remarks>
    /// <returns>真的换了返回 <see langword="true"/>；地址没变（或已经收了）返回 <see langword="false"/>。</returns>
    public bool Repoint(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        baseUrl = baseUrl.TrimEnd('/');

        lock (_life)
        {
            if (ObjectDisposedCheck()) return false;
            if (string.Equals(_baseUrl, baseUrl, StringComparison.Ordinal)) return false;

            _baseUrl = baseUrl;

            // ⚠️ 换了地址就是从零开始：上一处地址的失败次数、那句「为什么没画面」、
            // 那两个计数，说的都是**另一台机器/另一段推流**，留着就是撒谎。
            _failStreak = 0;
            _problem = null;
            _counts = null;
            _statusFailing = false;

            RestartVideo();
        }

        _logger.Log(LogLevel.Info, "多画面", $"这一格换了地址：{Name} → {baseUrl}");
        return true;
    }

    /// <summary>连着断了 <paramref name="failStreak"/> 次之后，隔多久再试。</summary>
    /// <remarks>
    /// ⚠️ <b>退避到 30 秒就封顶，而且**没有次数上限**。</b>从前的上限是 3 次，
    /// 过了就永久停手并让用户「重开多画面」—— 而手机那边每次【开始工作】都换端口，
    /// 于是那面墙用着用着就再也不出画面了（2026-10-03 六分钟四次就是这个）。
    /// 30 秒封顶是给日志与 CPU 留的余地：一直试、但别一直吵。
    /// </remarks>
    public static TimeSpan RetryDelay(int failStreak)
    {
        var seconds = Math.Pow(2, Math.Min(Math.Max(failStreak, 1), 5));

        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
    }

    /// <summary>按当前档重起本地那一路。（改档、断线重连、以及第一次起来时用。）</summary>
    private void RestartVideo()
    {
        lock (_life)
        {
            // 关窗那一头也要拿这把锁 —— 不判的话「刚好在关的那一瞬重连」
            // 会起出一路没人收的 ffmpeg（它就这么一直跑着）。
            if (ObjectDisposedCheck()) return;

            var old = _process;
            _process = null;

            // ⚠️ 旧的那一路自己退掉时**也会**走到 `OnProcessEnded`，
            // 而那时 `_process` 已经是新的（或 null）—— 那一头靠 `ReferenceEquals` 挡住。
            if (old is not null) _ = old.DisposeAsync();

            var (width, height) = SizeFor(_quality);

            // 「无信号输入」是常态（手机没开实时共享），所以起不来**不是错误** ——
            // 记一条就够，界面画那四个字。
            _process = LiveTileProcess.Start(
                _ffmpeg, $"{_baseUrl}/live", width, height, _logger, OnProcessEnded);

            if (_process is null)
            {
                _problem = "这一格的画面起不来（本机 ffmpeg 没跑起来）。";
            }
        }
    }

    /// <summary>本地这一路**自己**断了 —— 接回来（见 <see cref="RetryDelay"/>）。</summary>
    /// <remarks>
    /// ⚠️ 它是在**读循环那条线程**上调的，所以这里只做两件不阻塞的事：
    /// 记个数、排一个一次性计时器。真正重起的那一下在 <see cref="Retry"/> 里。
    /// </remarks>
    private void OnProcessEnded(LiveTileProcess process)
    {
        if (ObjectDisposedCheck()) return;

        string? reason = null;

        lock (_life)
        {
            // 换档/关窗把我们换掉的那种「结束」不算断线。
            // ⚠️ 这一判必须在 `_life` 里：`RestartVideo` **整段**都握着这把锁
            //（起进程 + 赋 `_process`），所以新的一路不可能「还没赋上就被判成断了」。
            if (!ReferenceEquals(_process, process)) return;

            var hadFrame = process.HadFrame;

            // 出过画面说明这条路是通的（网抖、手机那侧重启）—— 从 1 数起，
            // 而且下次出画面时又会归 1：掉线接回来这件事本身不该被次数挡住。
            _failStreak = hadFrame ? 1 : _failStreak + 1;

            // ⚠️ **一直会重试，只是不再逐次播报。** 屏幕上那句话必须说清「还在试」，
            // 否则用户能做的下一个动作就是去关窗口重开（而那不是必须的了）。
            _problem = "这一格现在没有画面 —— 手机那边的实时共享可能关着，或者网络不通。（还在自动重试）";

            if (_failStreak <= QuietAfterRetries)
            {
                reason = hadFrame
                    ? $"这一格断了，接回来（第 {_failStreak} 次）"
                    : $"这一格还没出画面，再试（第 {_failStreak} 次）";
            }
            else if (_failStreak == QuietAfterRetries + 1)
            {
                // 只在这一个点上多说一句，往后每 30 秒一次的重试不再留痕
                //（§6.1 的配套要求：重复的问题只在「变了」的时候记）。
                reason = $"这一格连着 {_failStreak} 次都没起来，接着试（每 30 秒一次），不再逐次说了";
            }

            _retry?.Dispose();
            _retry = new Timer(
                _ => Retry(),
                null,
                RetryDelay(_failStreak),
                Timeout.InfiniteTimeSpan);
        }

        if (reason is not null) _logger.Log(LogLevel.Info, "多画面", $"{reason}：{Name}");
    }

    /// <summary>计时器到点：把本地那一路重起。</summary>
    private void Retry()
    {
        try
        {
            // ⚠️ 再判一次「关没关」：排计时器到它响之间，窗口可能已经关了。
            // （`_stopped` 那一侧也一样 —— 关掉之后重起就是一路没人收的 ffmpeg。）
            lock (_life)
            {
                _retry?.Dispose();
                _retry = null;

                if (ObjectDisposedCheck()) return;
                RestartVideo();
            }
        }
        catch (Exception ex)
        {
            // 计时器回调里的异常没人接得住（`.NET` 上就是静默消失）—— 必须自己说。
            _logger.Log(LogLevel.Warn, "多画面", $"重连那一步没成：{ex.Message}");
        }
    }

    private bool ObjectDisposedCheck() => Volatile.Read(ref _disposed) != 0;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        LiveTileProcess? process;

        lock (_life)
        {
            _retry?.Dispose();
            _retry = null;

            // ⚠️ 在这一把锁里换 —— 见 `RestartVideo` 里那条注释：
            // 不然「刚好在关的那一瞬重连」会漏出一路。
            process = _process;
            _process = null;
        }

        if (process is not null) await process.DisposeAsync();

        _gate.Dispose();
    }
}
