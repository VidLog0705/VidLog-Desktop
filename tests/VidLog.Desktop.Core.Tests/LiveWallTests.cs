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

        public async Task Sync(Func<int, bool> isLive, params LiveEndpoint[] active) =>
            await Wall.SyncAsync(active, isLive);

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

    /// <summary>T9：不看的那一格**不起 ffmpeg，但位置照占**。</summary>
    /// <remarks>
    /// ⚠️ 这一条里「位置照占」比「省了一路」要紧。省一路是顺带的；位置要是塌了，
    /// 用户关掉第 2 格却看到第 3 格换了台手机 —— 而每一格画面都还在动，
    /// 看不出哪里错了（这一组别的几条也都盯这件事）。
    /// </remarks>
    [Fact]
    public async Task 不看的那一格_不起画面但位置照占()
    {
        var harness = new Harness();
        var all = new[] { Endpoint("phone-1", 1), Endpoint("phone-2", 2), Endpoint("phone-3", 3) };

        await harness.Sync(all);
        Assert.Equal(3, harness.Wall.Count);

        var built = harness.Created.Count;

        // 第 2 格不看了（下标 1）。
        await harness.Sync(index => index != 1, all);

        Assert.Equal(2, harness.Wall.Count);            // 那一路收了
        Assert.Equal(built, harness.Created.Count);     // 也没有为了补位再建一格

        var lineup = harness.Last;
        Assert.Equal(3, lineup.Count);

        Assert.NotNull(lineup[0]);
        Assert.Null(lineup[1]);                 // ⚠️ 空的是**第 2 格**，不是整列往前挪
        Assert.Equal("phone-3", lineup[2]!.Name);
    }

    /// <summary>T9：重新看上那一格 = **重新起一路**，不是把旧的捡回来。</summary>
    /// <remarks>
    /// ⚠️ 正因为它起的是新的一路，关掉才是真的省下了 ffmpeg —— 只把画面藏起来、
    /// 进程还留着的做法在这条测试里当场露馅（那样 <see cref="Harness.Created"/>
    /// 会停在 1，而 `Wall.Count` 一直是 2）。
    /// </remarks>
    [Fact]
    public async Task 重新看上那一格_是重新起一路()
    {
        var harness = new Harness();
        var all = new[] { Endpoint("phone-1", 1), Endpoint("phone-2", 2) };

        await harness.Sync(all);
        var first = harness.Last[0];

        await harness.Sync(index => index != 0, all);
        Assert.Null(harness.Last[0]);
        Assert.Equal(1, harness.Wall.Count);

        await harness.Sync(_ => true, all);

        Assert.NotNull(harness.Last[0]);
        Assert.NotSame(first, harness.Last[0]);
        Assert.Equal(3, harness.Created.Count);   // 两格 + 重新看上那一格起的这一路
        Assert.Equal(2, harness.Wall.Count);
    }

    /// <summary>T9：整面墙都不看了（最小化那一拍）—— 一路都不留，位置也不塌。</summary>
    [Fact]
    public async Task 全都不看了_一路都不留但位置还在()
    {
        var harness = new Harness();
        var all = new[] { Endpoint("phone-1", 1), Endpoint("phone-2", 2) };

        await harness.Sync(all);
        await harness.Sync(_ => false, all);

        Assert.Equal(0, harness.Wall.Count);

        var lineup = harness.Last;
        Assert.Equal(2, lineup.Count);
        Assert.All(lineup, tile => Assert.Null(tile));

        // 还原：两格都自己接回来。
        await harness.Sync(_ => true, all);

        Assert.Equal(2, harness.Wall.Count);
        Assert.All(harness.Last, tile => Assert.NotNull(tile));
    }

    /// <summary>不传 <c>isLive</c> 时与从前一模一样（老调用处不受影响）。</summary>
    [Fact]
    public async Task 不传那一位时_谁都不关()
    {
        var harness = new Harness();

        await harness.Sync(Endpoint("phone-1", 1), Endpoint("phone-2", 2));

        Assert.All(harness.Last, tile => Assert.NotNull(tile));
        Assert.Equal(2, harness.Wall.Count);
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

/// <summary>
/// 那面墙摆几列几行（<see cref="LiveWall.Layout"/>，T27② 第 4 批）。
/// </summary>
/// <remarks>
/// ⚠️ 行数算错不会报错，只会**有一格跑到墙外面看不见** —— 用户看到的是
/// 「我明明选了 9 格，只出来 6 个」。原先长在 <c>MultiViewWindow</c> 里，
/// 那个工程没有测试工程。
/// </remarks>
public class LiveWallLayoutTests
{
    private const int 第一档 = 2;
    private const int 九格 = 9;

    [Theory]
    [InlineData(2, 2, 1)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 3, 2)]
    [InlineData(6, 3, 2)]
    [InlineData(7, 3, 3)]
    [InlineData(8, 3, 3)]
    [InlineData(9, 3, 3)]
    public void 每一档摆成设计图那个形状(int count, int columns, int rows)
    {
        var wall = LiveWall.Layout(count, 第一档, 九格);

        Assert.Equal(count, wall.Count);
        Assert.Equal(columns, wall.Columns);
        Assert.Equal(rows, wall.Rows);
    }

    [Fact]
    public void 每一档都装得下_一个格子都不许漏到墙外面()
    {
        // ⚠️ 这一条是全部意义所在：行数要**向上取整**（`(n + cols - 1) / cols`）。
        // 写成 `n / cols` 的话 5 格 → 3 列 1 行 ⇒ 第 4、5 格没地方摆。
        for (var count = 第一档; count <= 九格; count++)
        {
            var wall = LiveWall.Layout(count, 第一档, 九格);

            Assert.True(
                wall.Columns * wall.Rows >= wall.Count,
                $"{count} 格摆成 {wall.Columns}×{wall.Rows}，装不下");
        }
    }

    /// <summary>T9：工具栏上那几档（1 / 4 / 9 / 16）各自摆成什么形状。</summary>
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(9, 3, 3)]
    [InlineData(16, 4, 4)]
    public void 工具栏那四档摆成方块(int count, int columns, int rows)
    {
        var wall = LiveWall.Layout(count, 1, 16);

        Assert.Equal(count, wall.Count);
        Assert.Equal(columns, wall.Columns);
        Assert.Equal(rows, wall.Rows);
    }

    /// <summary>
    /// T9：十格到十六格**一个都不许漏**，而且十六格那一下要摆四列。
    /// </summary>
    /// <remarks>
    /// ⚠️ 从前那条 <c>_ =&gt; 3</c> 会把十六格摆成 3×6 —— 装得下，所以「装得下」那条
    /// 绊线**拦不住它**（这正是要单独写一条的理由）：用户点的是「十六分割」，
    /// 出来的却是个三列六行的怪东西，不像同一个手势放大一档。
    /// </remarks>
    [Fact]
    public void 十格往上摆四列_而且一个格子都不许漏到墙外面()
    {
        for (var count = 10; count <= 16; count++)
        {
            var wall = LiveWall.Layout(count, 1, 16);

            Assert.Equal(count, wall.Count);
            Assert.Equal(4, wall.Columns);
            Assert.True(
                wall.Columns * wall.Rows >= wall.Count,
                $"{count} 格摆成 {wall.Columns}×{wall.Rows}，装不下");
        }
    }

    /// <summary>T9：一到十六格逐档扫一遍，没有一格被挤到墙外面去。</summary>
    [Fact]
    public void 一到十六格_每一档都装得下()
    {
        for (var count = 1; count <= 16; count++)
        {
            var wall = LiveWall.Layout(count, 1, 16);

            Assert.Equal(count, wall.Count);
            Assert.True(
                wall.Columns * wall.Rows >= wall.Count,
                $"{count} 格摆成 {wall.Columns}×{wall.Rows}，装不下");
        }
    }

    [Fact]
    public void 选到档位外面就夹回边界那一档()
    {
        // 格数来自菜单，将来档位会变；夹在这里，越界最坏也就是摆成边界那一档。
        Assert.Equal(第一档, LiveWall.Layout(0, 第一档, 九格).Count);
        Assert.Equal(第一档, LiveWall.Layout(-3, 第一档, 九格).Count);
        Assert.Equal(九格, LiveWall.Layout(99, 第一档, 九格).Count);
    }

    [Fact]
    public void 第一档给零时不许把行数那个除法炸掉()
    {
        // 走到这儿的话列数是 0，`(n + cols - 1) / cols` 当场 DivideByZero。
        // 眼下的调用处第一档就是 2，走不到 —— 这一条是防着下一个调用处的。
        var wall = LiveWall.Layout(0, min: 0, max: 九格);

        Assert.Equal(0, wall.Count);
        Assert.Equal(0, wall.Rows);
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
