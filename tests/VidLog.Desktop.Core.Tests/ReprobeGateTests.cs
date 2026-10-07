using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「相机恢复之后该不该重探一次」（T27② 第 2 批从 <c>AppHost.MaybeReprobeAsync</c> 搬进 Core）。
/// </summary>
/// <remarks>
/// ⚠️ 这一档原先长在 <c>VidLog.Desktop.App</c> 里，而那个工程没有测试工程 ——
/// 于是「什么情况下会白开一次相机 / 会漏探一次」只能靠读代码确认。
/// 这里的每一条都对着一个**真踩过**的坑（见各自的注释）。
/// </remarks>
public class ReprobeGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>回落过、没在录、相机空着、早就过了限流 —— **该探**的基准。</summary>
    private static (bool ShouldProbe, DateTimeOffset LastTriedAt) Decide(
        bool fellBack = true,
        bool recording = false,
        bool cameraHeld = false,
        DateTimeOffset? lastTriedAt = null,
        DateTimeOffset? now = null) =>
        ReprobeGate.Decide(fellBack, recording, cameraHeld, lastTriedAt ?? default, now ?? T0);

    [Fact]
    public void 没回落过就不探_哪怕相机空着而且早就过了限流()
    {
        // 上一次探测是**照着用户选的**成的，那就没有要修的东西 ——
        // 这条路径每次心跳都走，不挡住的话就是每秒白开一次相机。
        Assert.False(Decide(fellBack: false).ShouldProbe);
    }

    [Fact]
    public void 录着的时候不探_相机是独占的()
    {
        // 录制中另开一路只会失败（§25）。
        Assert.False(Decide(recording: true).ShouldProbe);
    }

    [Fact]
    public void 相机在预录进程手里时不探_哪怕没在录()
    {
        // ⚠️ 这一条是 2026-10-02 那个坑：待扫期间相机在**预录那个进程**手里，
        // 这时去探只会拿到 `device already in use` —— 于是把**能用的组合误判成
        // 跑不通**，而那正是「重探」这件事存在的理由。
        // 「没在录」不等于「相机是空的」，这两档必须分开。
        Assert.False(Decide(recording: false, cameraHeld: true).ShouldProbe);
    }

    [Fact]
    public void 一分钟内不重复探_相机一直不可达时别每次心跳都去开相机()
    {
        // 一次探测是十秒的超时 + 一次设备占用。
        Assert.False(Decide(lastTriedAt: T0 - TimeSpan.FromSeconds(59), now: T0).ShouldProbe);
        Assert.True(Decide(lastTriedAt: T0 - TimeSpan.FromMinutes(1), now: T0).ShouldProbe);
    }

    [Fact]
    public void 头一次要探_而且给出的时刻就是此刻()
    {
        // `AppHost` 那个字段初值是 `DateTimeOffset.MinValue`（从没试过）⇒ 必须能探。
        var (shouldProbe, stamp) = Decide();

        Assert.True(shouldProbe);
        Assert.Equal(T0, stamp);
    }

    [Fact]
    public void 不探的时候不许把时刻往前推()
    {
        // ⚠️ 这条钉的是「记时刻」的方向：限流算的是「上次**试过**的时刻」。
        // 心跳每秒来一次，倘若不探也把时刻记成 `now`，那么一次长录制结束之后
        // 限流会从**最后一次心跳**起算 —— 白白多等一分钟才重探。
        var last = T0 - TimeSpan.FromMinutes(30);
        var recent = T0 - TimeSpan.FromSeconds(30);

        Assert.Equal(last, Decide(recording: true, lastTriedAt: last).LastTriedAt);
        Assert.Equal(last, Decide(fellBack: false, lastTriedAt: last).LastTriedAt);
        Assert.Equal(recent, Decide(lastTriedAt: recent, now: T0).LastTriedAt);

        // 真探了才推。
        Assert.Equal(T0, Decide(lastTriedAt: last, now: T0).LastTriedAt);
    }
}
