using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Live;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 机位发现那张表（规格 §3.8）：谁报了到、谁过期了、什么时候该留一条日志。
/// </summary>
/// <remarks>
/// 纯逻辑 + 可注入的钟 —— 过期那几条只有把钟拨快才测得动，
/// 所以这里一条都不用真等（等 60 秒的测试没有谁会去跑）。
/// </remarks>
public class LiveDirectoryTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>一个能拨的钟。</summary>
    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = Start;

        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>把日志收起来，好在测试里断言（本仓既有写法）。</summary>
    private sealed class CapturingLogger : IAppLogger
    {
        public List<string> Messages { get; } = [];

        public void Log(LogLevel level, string category, string message) => Messages.Add(message);

        public void Log(LogLevel level, string category, string message, IReadOnlyDictionary<string, object?> data) =>
            Messages.Add(message);
    }

    private static (LiveDirectory Directory, Clock Clock, CapturingLogger Logger) Build(
        TimeSpan? ttl = null)
    {
        var clock = new Clock();
        var logger = new CapturingLogger();
        var directory = new LiveDirectory(now: () => clock.Now, logger: logger, ttl: ttl);
        return (directory, clock, logger);
    }

    [Fact]
    public void 报了到就排在里面_并且报出可以拉的地址()
    {
        var (directory, _, _) = Build();

        directory.Announce("device-1", "192.168.1.5", 8888);

        var active = Assert.Single(directory.Active());
        Assert.Equal("device-1", active.DeviceId);
        Assert.Equal("http://192.168.1.5:8888", active.BaseUrl);
        Assert.True(directory.IsActive("device-1"));
    }

    [Fact]
    public void 同一台反复报同址_只更新时间_不重复记日志()
    {
        var (directory, clock, logger) = Build();

        directory.Announce("device-1", "192.168.1.5", 8888);
        var afterFirst = logger.Messages.Count;

        // 手机每 20 秒报一次 —— 拨到 TTL 之内再报三次。
        clock.Advance(TimeSpan.FromSeconds(20));
        directory.Announce("device-1", "192.168.1.5", 8888);
        clock.Advance(TimeSpan.FromSeconds(20));
        directory.Announce("device-1", "192.168.1.5", 8888);

        Assert.Equal(afterFirst, logger.Messages.Count);
        Assert.Equal(clock.Now, Assert.Single(directory.Active()).LastSeenUtc);
    }

    [Fact]
    public void 超过时限不报_就不在里面了_并且记一条()
    {
        var (directory, clock, logger) = Build();

        directory.Announce("device-1", "192.168.1.5", 8888);
        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Empty(directory.Active());
        Assert.False(directory.IsActive("device-1"));
        Assert.Contains("机位不再报到了", string.Join('\n', logger.Messages));
    }

    [Fact]
    public void 过期只记一条_反复读不会再记()
    {
        var (directory, clock, logger) = Build();

        directory.Announce("device-1", "192.168.1.5", 8888);
        clock.Advance(TimeSpan.FromSeconds(61));

        // 界面每两秒问一次 —— 若「过期」是按读的那一刻判的，这里会记三次。
        directory.Active();
        directory.Active();
        directory.Active();

        Assert.Single(logger.Messages, m => m.Contains("机位不再报到了"));
    }

    /// <remarks>
    /// ⚠️ 两条分开写是因为**同一个现实事件有两条代码路径**：手机过期之后又报回来，
    /// 而这时界面有没有恰好刷新过决定了那条老记录还在不在表里。
    /// 两条都**必须**留下一条日志 —— 悄悄复活才是最坏的那种
    ///（「这一格什么时候回来的」没人答得上来）。
    /// </remarks>
    [Fact]
    public void 过期且已被摘掉之后又报回来_记一条()
    {
        var (directory, clock, logger) = Build();

        directory.Announce("device-1", "192.168.1.5", 8888);
        clock.Advance(TimeSpan.FromSeconds(61));
        directory.Active();                       // 界面刷了一次，那条被摘掉

        var before = logger.Messages.Count;
        directory.Announce("device-1", "192.168.1.5", 8888);

        Assert.True(directory.IsActive("device-1"));
        Assert.Equal(before + 1, logger.Messages.Count);
        Assert.Contains("机位开始报到", logger.Messages[^1]);
    }

    [Fact]
    public void 过期但界面还没刷过_又报回来也要记一条_不许悄悄复活()
    {
        var (directory, clock, logger) = Build();

        directory.Announce("device-1", "192.168.1.5", 8888);
        clock.Advance(TimeSpan.FromSeconds(61));   // 没人调 Active()：老记录还在表里

        var before = logger.Messages.Count;
        directory.Announce("device-1", "192.168.1.5", 8888);

        Assert.True(directory.IsActive("device-1"));
        Assert.Equal(before + 1, logger.Messages.Count);
    }

    [Fact]
    public void 换了端口或地址_记一条()
    {
        var (directory, _, logger) = Build();

        directory.Announce("device-1", "192.168.1.5", 8888);
        directory.Announce("device-1", "192.168.1.5", 9001);
        directory.Announce("device-1", "192.168.1.6", 9001);

        Assert.Equal(2, logger.Messages.Count(m => m.Contains("机位换了地址")));
    }

    [Fact]
    public void 顺序按设备号_不跟着字典走()
    {
        var (directory, _, _) = Build();

        // 故意乱序报进来：字典的枚举顺序不保证稳定，而界面上的格子**必须**稳定
        // —— 否则每次刷新那几台会换位置。
        directory.Announce("device-3", "192.168.1.7", 1);
        directory.Announce("device-1", "192.168.1.5", 2);
        directory.Announce("device-2", "192.168.1.6", 3);

        Assert.Equal(["device-1", "device-2", "device-3"], directory.Active().Select(e => e.DeviceId));
    }

    [Fact]
    public void 各台的时限各算各的()
    {
        var (directory, clock, _) = Build();

        directory.Announce("device-1", "192.168.1.5", 8888);
        clock.Advance(TimeSpan.FromSeconds(50));
        directory.Announce("device-2", "192.168.1.6", 8888);
        clock.Advance(TimeSpan.FromSeconds(20));

        // 第 70 秒：device-1 已经 70 秒没报（过期），device-2 才 20 秒（还在）。
        Assert.Equal(["device-2"], directory.Active().Select(e => e.DeviceId));
    }
}
