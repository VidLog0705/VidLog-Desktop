using VidLog.Desktop.Core.Live;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 多画面那面墙的对账（`LiveWall`）—— 机位表变了，墙上跟着变。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 这一组钉的是 2026-10-03 报上来的那个缺陷：窗口开出来之后**再也不跟手机走**。
/// 手机每开一次实时共享都绑一个新端口，而墙上每一格钉死在开窗那一刻的地址上 ——
/// 表现是「手机明明在推流，电脑端那一格一直黑着」，
/// 用户唯一的出路是把多画面窗口关掉重开（日志里六分钟关了四次）。
/// </para>
/// <para>
/// 这里一格 ffmpeg 都不会真起：<see cref="Tile"/> 给的是一个**不存在的**可执行文件路径，
/// 所以它当场就返回一个「起不来」的 tile 对象。这一组要验的本来就不是画面，
/// 而是**谁挂在哪一格上、地址是哪个、该收的收了没有**。
/// </para>
/// </remarks>
public class LiveWallTests
{
    private static LiveEndpoint Endpoint(string deviceId, int port) =>
        new(deviceId, "192.168.1.9", port, DateTimeOffset.UnixEpoch);

    private static LiveTile Tile(LiveEndpoint endpoint) =>
        LiveTile.Start("no-such-ffmpeg", endpoint.BaseUrl, endpoint.DeviceId);

    /// <summary>一面墙 + 它摆过的阵容（每一次 `adopt` 都记一笔）。</summary>
    private sealed class Harness
    {
        public List<IReadOnlyList<LiveTile?>> Lineups { get; } = [];

        public List<string> Created { get; } = [];

        public LiveWall Wall { get; }

        public Harness()
        {
            Wall = new LiveWall(
                endpoint =>
                {
                    Created.Add(endpoint.BaseUrl);
                    return Task.FromResult(Tile(endpoint));
                },
                lineup => Lineups.Add(lineup));
        }

        public async Task Sync(params LiveEndpoint[] active) => await Wall.SyncAsync(active);

        public IReadOnlyList<LiveTile?> Last => Lineups[^1];
    }

    [Fact]
    public async Task 报了到的接上_换了端口的指过去_不重建那一格()
    {
        var harness = new Harness();

        await harness.Sync(Endpoint("phone-1", 65393));
        await harness.Sync(Endpoint("phone-1", 65395));

        // 只建过一次 —— 换地址是**把这一格指过去**，不是重开一格。
        Assert.Single(harness.Created);

        var tile = Assert.Single(harness.Last);
        Assert.NotNull(tile);
        Assert.Equal("http://192.168.1.9:65395", tile!.BaseUrl);
        Assert.Same(harness.Lineups[0][0], tile);
    }

    [Fact]
    public async Task 地址没变的那一拍_什么都不动()
    {
        var harness = new Harness();

        await harness.Sync(Endpoint("phone-1", 65393));
        await harness.Sync(Endpoint("phone-1", 65393));

        // ⚠️ 这一条比看上去要紧：对账两秒一次，而 `Repoint` 每叫一次都会**重起那一路
        // ffmpeg**。地址没变时它必须一声不响，否则画面会每两秒断一次 —— 永远不出画面。
        Assert.False(harness.Last[0]!.Repoint("http://192.168.1.9:65393"));
        Assert.Equal("http://192.168.1.9:65393", harness.Last[0]!.BaseUrl);
    }

    [Fact]
    public async Task 不再报到的不留_格子腾空_顺序保持不变()
    {
        var harness = new Harness();

        await harness.Sync(Endpoint("phone-1", 1), Endpoint("phone-2", 2));
        var first = harness.Last[0];

        // phone-1 过期被摘掉了（`LiveDirectory.Active` 那边摘的，这里只是少了一条）。
        await harness.Sync(Endpoint("phone-2", 2));

        var lineup = harness.Last;
        Assert.Single(lineup);
        Assert.Equal("phone-2", lineup[0]!.Name);
        Assert.DoesNotContain(lineup, tile => ReferenceEquals(tile, first));
        Assert.Equal(1, harness.Wall.Count);
    }

    [Fact]
    public async Task 一台机位都没有_摆的还是空阵容()
    {
        var harness = new Harness();

        await harness.Sync(Endpoint("phone-1", 1));
        await harness.Sync();

        Assert.Empty(harness.Last);
        Assert.Equal(0, harness.Wall.Count);
    }

    [Fact]
    public async Task 同一台报了两条_只接一格()
    {
        // 表是按 DeviceId 索引的，理论上出不来两条 —— 真出来了也别在同一次对账里
        // 建两格、然后把其中一格丢掉（那会留下一路没人收的 ffmpeg）。
        var harness = new Harness();

        await harness.Sync(Endpoint("phone-1", 1), Endpoint("phone-1", 2));

        Assert.Single(harness.Last);
        Assert.Single(harness.Created);
    }

    [Fact]
    public async Task 收掉之后_再对账一个字都不动()
    {
        var harness = new Harness();

        await harness.Sync(Endpoint("phone-1", 1));
        await harness.Wall.DisposeAsync();

        // 窗口关掉之后又响了一拍（计时器停之前那一拍）：不许再建、再摆。
        var before = harness.Lineups.Count;
        await harness.Sync(Endpoint("phone-1", 1), Endpoint("phone-2", 2));

        Assert.Equal(before, harness.Lineups.Count);
        Assert.Equal(0, harness.Wall.Count);
    }
}

/// <summary>重连的节拍（<see cref="LiveTile.RetryDelay"/>）。</summary>
public class LiveTileRetryTests
{
    [Fact]
    public void 退避是翻倍涨的_而且封顶三十秒()
    {
        // ⚠️ 从前这里是个**上限**（3 次就不再试了），而那正是缺陷的一半：
        // 手机那边每开一次共享都换端口，过了 3 次就永久停手 ⇒ 那面墙用着用着
        // 就再也不出画面。现在只是「说得少」，试是一直在试的 ——
        // 次数不再影响延迟的走势，只有失败次数影响。
        Assert.Equal(TimeSpan.FromSeconds(2), LiveTile.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), LiveTile.RetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(8), LiveTile.RetryDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(16), LiveTile.RetryDelay(4));

        // 封顶：再失败下去也是 30 秒一次，不会退到「一小时才试一次」。
        Assert.Equal(TimeSpan.FromSeconds(30), LiveTile.RetryDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(30), LiveTile.RetryDelay(50));
    }
}
