using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Live;

/// <summary>一台手机**报上来的**实时推流地址。</summary>
/// <param name="DeviceId">机位身份，**来自凭据**（不是报文里自称的那个）。</param>
/// <param name="Address">手机在局域网里的地址 —— 取自**请求的来源地址**，不是手机自报的。</param>
/// <param name="Port">手机那一路推流服务实际绑上的端口（它每次开 App 都可能变）。</param>
/// <param name="LastSeenUtc">最后一次报到的时间。</param>
public sealed record LiveEndpoint(
    string DeviceId,
    string Address,
    int Port,
    DateTimeOffset LastSeenUtc)
{
    /// <summary>多画面那一格要拉的地址（`LiveTile.Start` 的 `baseUrl`）。</summary>
    public string BaseUrl => $"http://{Address}:{Port}";
}

/// <summary>
/// 「哪台手机现在能看」—— 机位发现（规格 §3.8）。
/// </summary>
/// <remarks>
/// <para>
/// 手机端打开「实时共享」之后会起一路 HTTP 服务，并**每隔一会儿向电脑端报一次**
/// （`POST /api/v1/live/announce`）。这个目录就是那些报到的去处。
/// </para>
/// <para>
/// ⚠️ <b>只在内存里，绝不落盘。</b>手机每次开 App 的端口都可能不一样，
/// 存下来的那份只会在下次开机时指向一个**死地址** —— 而「连不上的机位」
/// 在界面上与「手机没开共享」长得一模一样，那正是本仓反复吃过的亏
/// （显示一个看起来正常的假状态）。
/// </para>
/// <para>
/// ⚠️ <b>过期是靠「读到的时候顺手清」实现的，没有定时器。</b>
/// 手机被关掉、App 被杀、网络断了 —— 这些都不会有人来通知电脑端，
/// 唯一的判据就是「它多久没报了」。所以 <see cref="Active"/> 既是读、
/// 也是清理点：过期的那一条在这里被摘掉，且**只记一条**（见
/// <see cref="Announce"/> 与 <see cref="Active"/> 的注释）。
/// </para>
/// </remarks>
public sealed class LiveDirectory
{
    /// <summary>多久没报到就算它不在了。</summary>
    /// <remarks>
    /// ⚠️ 比手机那边的报到间隔（20 秒）宽三倍：**网络抖一下不该让那一格消失**。
    /// 太紧会让画面在「有信号」与「无信号输入」之间来回跳，那比不显示更让人不安。
    /// </remarks>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, LiveEndpoint> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly IAppLogger _logger;

    /// <param name="now">时间源。**可注入**：过期那一条只有把钟拨快才测得动。</param>
    /// <param name="logger">可选，与全仓其它可选日志参数同一条理由（不传就不记）。</param>
    /// <param name="ttl">多久没报到算过期；不传用 <see cref="DefaultTtl"/>。</param>
    public LiveDirectory(
        Func<DateTimeOffset>? now = null,
        IAppLogger? logger = null,
        TimeSpan? ttl = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _logger = logger ?? NullLogger.Instance;
        Ttl = ttl ?? DefaultTtl;
    }

    /// <summary>多久没报到算它不在了。</summary>
    public TimeSpan Ttl { get; }

    /// <summary>
    /// 一台手机报到了（或又报了一次）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>同一个地址反复报，一条都不记</b> —— 它是每 20 秒一次的事，
    /// 每次都记的话一分钟三条、一小时一百八十条，而被灌满的日志等于没有日志。
    /// 只在**状态真的变了**的时候记：来了（含过期之后又来）、换了地址/端口。
    /// </remarks>
    public void Announce(string deviceId, string address, int port)
    {
        var at = _now();

        lock (_gate)
        {
            _entries.TryGetValue(deviceId, out var previous);

            var stale = previous is not null && IsExpired(previous, at);

            if (previous is not null && !stale
                && previous.Address == address && previous.Port == port)
            {
                // 同地址同端口又报了一次：只把时间戳往前推（**不记日志**）。
                _entries[deviceId] = previous with { LastSeenUtc = at };
                return;
            }

            _entries[deviceId] = new LiveEndpoint(deviceId, address, port, at);

            // ⚠️ `stale` 与 `previous is null` 走**同一句话**，不是偷懒：
            // 「过期了又被摘掉」与「过期了还留着」是同一个现实事件（手机断了一阵又回来），
            // 而它俩走哪条取决于界面有没有恰好刷新过 —— 让同一件事有两种文案，
            // 就是把「日志读起来像什么」交给了刷新时机。三种文案里那两种已删。
            if (previous is null || stale)
            {
                _logger.Log(LogLevel.Info, "多画面", $"机位开始报到：{deviceId} → {address}:{port}");
                return;
            }

            _logger.Log(
                LogLevel.Info, "多画面",
                $"机位换了地址：{deviceId} 从 {previous.Address}:{previous.Port} 变成 {address}:{port}");
        }
    }

    /// <summary>
    /// 现在**还算数**的机位，按 DeviceId 排（顺序稳定，界面不会自己跳）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这是个**会改状态的读**（摘掉过期的），理由见类注释。
    /// 摘掉的每一条都记一行 —— 「机位忽然不见了，为什么」应当能在日志里问出来。
    /// </remarks>
    public IReadOnlyList<LiveEndpoint> Active()
    {
        var at = _now();

        lock (_gate)
        {
            List<string>? gone = null;

            foreach (var (deviceId, entry) in _entries)
            {
                if (!IsExpired(entry, at)) continue;

                (gone ??= []).Add(deviceId);
            }

            if (gone is not null)
            {
                foreach (var deviceId in gone)
                {
                    var entry = _entries[deviceId];
                    _entries.Remove(deviceId);

                    _logger.Log(
                        LogLevel.Warn, "多画面",
                        $"机位不再报到了：{entry.DeviceId}（{entry.Address}:{entry.Port}）"
                        + $"—— 最后一次是 {Ttl.TotalSeconds:0} 秒之前。它那一格会显示「无信号输入」。");
                }
            }

            // ⚠️ 排一下序：这个列表直接喂给多画面窗口，而字典的顺序**不保证稳定**
            // —— 不排的话格子里那几台会在每次刷新时换位置。
            return _entries.Values.OrderBy(e => e.DeviceId, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>某台现在还算数吗。界面按机位逐个问的时候用。</summary>
    public bool IsActive(string deviceId)
    {
        var at = _now();

        lock (_gate)
        {
            return _entries.TryGetValue(deviceId, out var entry) && !IsExpired(entry, at);
        }
    }

    private bool IsExpired(LiveEndpoint entry, DateTimeOffset at) => at - entry.LastSeenUtc > Ttl;
}
