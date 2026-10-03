using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Live;

/// <summary>
/// 多画面那面墙的**对账**：把「现在报到着的机位」对成「每一格该挂谁」。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么要有它。</b>手机的推流地址**每一段都不一样**（每次开共享都绑一个新端口），
/// 机位表也随时会变（新手机报到、老机位过期）。而窗口只取一次机位表的话，
/// 那一格就永远钉在开窗那一刻的地址上 —— 表现是「手机明明在推，电脑端一直黑着」，
/// 而用户唯一的出路是把窗口关掉重开（2026-10-03 的日志里六分钟关了四次）。
/// </para>
/// <para>
/// 三件事，一件都不能少：<b>来的接上</b>（新建一格）、<b>换地址的指过去</b>
/// （<see cref="LiveTile.Repoint"/>）、<b>不再报到的不留</b>（收掉并腾空格子）。
/// 第三种少掉的话，那台手机关掉之后格子里会**冻着一张旧图**，看起来还在看。
/// </para>
/// <para>
/// ⚠️ <b>只在界面线程上跑，所以没有锁。</b>调用点只有两个：窗口那个对账计时器
/// （每次一拍）和窗口关闭。它自己起的线程一个都没有 —— 这也是它敢直接改
/// <see cref="_tiles"/> 而不加锁的全部理由。将来要是有人想从别的线程叫它，
/// 得先把这里加锁（不是「顺手指一下」就行的改动）。
/// </para>
/// </remarks>
public sealed class LiveWall : IAsyncDisposable
{
    private readonly Func<LiveEndpoint, Task<LiveTile>> _create;
    private readonly Action<IReadOnlyList<LiveTile?>> _adopt;
    private readonly IAppLogger _logger;
    private readonly Dictionary<string, LiveTile> _tiles = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <param name="create">
    /// 建一格。**做成委托是为了让这一层能在没有 ffmpeg 的测试里验**
    ///（真机上它就是 <c>LiveTile.Start</c> 外加一次机位名查询）。
    /// </param>
    /// <param name="adopt">
    /// 把新阵容摆到界面上（长度 = 现在有几台机位；界面自己补到九格）。
    /// ⚠️ **收退役的那几个之前一定先叫它**（见 <see cref="SyncAsync"/>）。
    /// </param>
    public LiveWall(
        Func<LiveEndpoint, Task<LiveTile>> create,
        Action<IReadOnlyList<LiveTile?>> adopt,
        IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(adopt);

        _create = create;
        _adopt = adopt;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>现在挂着几格（诊断用）。</summary>
    public int Count => _tiles.Count;

    /// <summary>
    /// 对一次账。<paramref name="active"/> 就是 <see cref="LiveDirectory.Active"/>
    /// 那一列（按 DeviceId 排好，所以同一台机位的位置是稳定的）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>顺序是「先摆新的、再收旧的」。</b>反过来的话，收到一半出岔子就会留下
    /// 一格指向**已经收掉的那个 tile** —— 而界面照旧每 1/12 秒去问它要帧。
    /// </remarks>
    public async Task SyncAsync(IReadOnlyList<LiveEndpoint> active)
    {
        ArgumentNullException.ThrowIfNull(active);

        if (_disposed) return;

        var lineup = new List<LiveTile?>(active.Count);
        var kept = new HashSet<string>(StringComparer.Ordinal);

        foreach (var endpoint in active)
        {
            // 同一台报了两条是**不该发生**的（表按 DeviceId 索引）—— 真发生了也别翻车。
            if (!kept.Add(endpoint.DeviceId)) continue;

            if (_tiles.TryGetValue(endpoint.DeviceId, out var tile))
            {
                // 地址没变时它自己会认出来并立刻返回（那是最常见的一拍）。
                tile.Repoint(endpoint.BaseUrl);
            }
            else
            {
                tile = await _create(endpoint);
                _tiles[endpoint.DeviceId] = tile;

                _logger.Log(
                    LogLevel.Info, "多画面",
                    $"这一格接上了机位：{tile.Name} → {endpoint.BaseUrl}");
            }

            lineup.Add(tile);
        }

        _adopt(lineup);

        List<(string DeviceId, LiveTile Tile)>? gone = null;

        foreach (var (deviceId, tile) in _tiles)
        {
            if (kept.Contains(deviceId)) continue;

            (gone ??= []).Add((deviceId, tile));
        }

        if (gone is null) return;

        foreach (var (deviceId, tile) in gone)
        {
            _tiles.Remove(deviceId);

            _logger.Log(
                LogLevel.Info, "多画面",
                $"这一格收了：{tile.Name} —— {deviceId} 不再报到了（剩下的格子会往前挪）");

            // ⚠️ 这一下要等一下（`LiveTileProcess` 最多留 2 秒让它好好退）——
            // 所以它排在 `_adopt` 后面：界面**已经**不指着它了才收。
            await tile.DisposeAsync();
        }
    }

    /// <summary>把还挂着的都收掉（窗口关了）。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var tiles = _tiles.Values.ToList();
        _tiles.Clear();

        // 先把格子摘空再收（同 <see cref="SyncAsync"/> 那条顺序）。
        _adopt([]);

        foreach (var tile in tiles) await tile.DisposeAsync();
    }
}
