using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 三种工作模式的判定（规格 §3.3.1 / §3.3.2 / §3.3.3）。
/// </summary>
/// <remarks>
/// 策略是**纯函数**，所以规格 §3.3.1 那张表的每一格都能直接断言。
/// </remarks>
public class WorkModePolicyTests
{
    private static readonly WaybillNumber A = WaybillNumber.Parse("SF1234567890");
    private static readonly WaybillNumber B = WaybillNumber.Parse("SF9999999999");

    // ─────────────────────────────────────────────
    // 开录：三个模式都一样
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData(WorkMode.Continuous)]
    [InlineData(WorkMode.StopOnSameWaybill)]
    [InlineData(WorkMode.StopOnStaticAfterRescan)]
    public void 空闲时扫到单号就开录(WorkMode mode)
    {
        var decision = Build(mode).OnScan(WorkModeState.Idle, A);

        var start = Assert.IsType<WorkDecision.StartSegment>(decision);
        Assert.Equal(A, start.Waybill);
    }

    // ─────────────────────────────────────────────
    // 段中扫到同一单号
    // ─────────────────────────────────────────────

    [Fact]
    public void 连续扫下复扫同码什么都不做()
    {
        // 同一件又扫一次是误触，不停也不提示。
        var decision = Build(WorkMode.Continuous).OnScan(Open(A), A);

        Assert.IsType<WorkDecision.Nothing>(decision);
    }

    [Theory]
    [InlineData(WorkMode.StopOnSameWaybill)]
    [InlineData(WorkMode.StopOnStaticAfterRescan)]
    public void 另两个模式复扫同码即停(WorkMode mode)
    {
        var decision = Build(mode).OnScan(Open(A), A);

        var stop = Assert.IsType<WorkDecision.StopSegment>(decision);
        Assert.Equal(StopReason.SameWaybillRescan, stop.Reason);
    }

    // ─────────────────────────────────────────────
    // 段中扫到**不同**单号 —— 三个模式在这里分道扬镳
    // ─────────────────────────────────────────────

    [Fact]
    public void 连续扫下扫到异码就是换件_不是错误()
    {
        // 规格 2026-09-22 的需求变更：连续扫改成换段式。
        var decision = Build(WorkMode.Continuous).OnScan(Open(A), B);

        var next = Assert.IsType<WorkDecision.SwitchTo>(decision);
        Assert.Equal(B, next.Next);
    }

    [Theory]
    [InlineData(WorkMode.StopOnSameWaybill)]
    [InlineData(WorkMode.StopOnStaticAfterRescan)]
    public void 另两个模式扫到异码只提示不停录(WorkMode mode)
    {
        // 规格 §3.3.2 错码保护。
        var decision = Build(mode).OnScan(Open(A), B);

        var announce = Assert.IsType<WorkDecision.Announce>(decision);
        Assert.Equal(AnnouncementKind.WrongWaybill, announce.Kind);
    }

    // ─────────────────────────────────────────────
    // 静止停录（规格 §3.3.3）
    // ─────────────────────────────────────────────

    [Fact]
    public void 档位关闭时静止不停录()
    {
        var decision = Build(WorkMode.StopOnSameWaybill, StaticStopOption.Off)
            .OnStatic(Open(A) with { StaticFor = TimeSpan.FromHours(1) }, isStatic: true);

        Assert.IsType<WorkDecision.Nothing>(decision);
    }

    [Fact]
    public void 档位未到时不停录()
    {
        var decision = Build(WorkMode.StopOnSameWaybill, StaticStopOption.Three)
            .OnStatic(Open(A) with { StaticFor = TimeSpan.FromMinutes(2) }, isStatic: true);

        Assert.IsType<WorkDecision.Nothing>(decision);
    }

    [Fact]
    public void 档位到时按静止停录()
    {
        var decision = Build(WorkMode.StopOnSameWaybill, StaticStopOption.Three)
            .OnStatic(Open(A) with { StaticFor = TimeSpan.FromMinutes(3) }, isStatic: true);

        var stop = Assert.IsType<WorkDecision.StopSegment>(decision);
        Assert.Equal(StopReason.StaticTimeout, stop.Reason);
    }

    [Fact]
    public void 画面没静止就不停()
    {
        var decision = Build(WorkMode.StopOnSameWaybill, StaticStopOption.Two)
            .OnStatic(Open(A) with { StaticFor = TimeSpan.FromHours(1) }, isStatic: false);

        Assert.IsType<WorkDecision.Nothing>(decision);
    }

    [Fact]
    public void 空闲时静止不停录()
    {
        // 没在录就无所谓静止 —— 否则架机久了会被判成「该停」。
        var decision = Build(WorkMode.StopOnSameWaybill, StaticStopOption.Two)
            .OnStatic(WorkModeState.Idle with { StaticFor = TimeSpan.FromHours(1) }, isStatic: true);

        Assert.IsType<WorkDecision.Nothing>(decision);
    }

    // ─────────────────────────────────────────────
    // 扫码静止停录**自己的** 2 秒（规格 2026-09-22 的语义澄清）
    // ─────────────────────────────────────────────

    [Fact]
    public void 扫码静止的两秒不看档位_档位关闭时照样生效()
    {
        // 规格原话：「档位设成『关闭』时它照样生效」。
        // 少了这条，本模式在「复扫同码就停」之后与同码停完全等价。
        var policy = Build(WorkMode.StopOnStaticAfterRescan, StaticStopOption.Off);
        var state = Open(A) with
        {
            TrackedWaybillLeftFrame = true,
            StaticFor = TimeSpan.FromSeconds(2),
        };

        var decision = policy.OnStatic(state, isStatic: true);

        var stop = Assert.IsType<WorkDecision.StopSegment>(decision);
        Assert.Equal(StopReason.StaticTimeout, stop.Reason);
    }

    [Fact]
    public void 面单没离场过时静止两秒不停()
    {
        // 门槛：必须「离场后再入场」。少了它，面单刚扫完就摆在框里，
        // 每段都会在开录 2 秒后自己结束。
        var state = Open(A) with
        {
            TrackedWaybillLeftFrame = false,
            StaticFor = TimeSpan.FromSeconds(10),
        };

        var decision = Build(WorkMode.StopOnStaticAfterRescan).OnStatic(state, isStatic: true);

        Assert.IsType<WorkDecision.Nothing>(decision);
    }

    [Fact]
    public void 扫码静止不满两秒不停()
    {
        var state = Open(A) with
        {
            TrackedWaybillLeftFrame = true,
            StaticFor = TimeSpan.FromSeconds(1.9),
        };

        var decision = Build(WorkMode.StopOnStaticAfterRescan).OnStatic(state, isStatic: true);

        Assert.IsType<WorkDecision.Nothing>(decision);
    }

    [Fact]
    public void 扫码静止模式下档位不会先触发()
    {
        // 2 秒必定早于任何档位（最小 2 分钟）—— 那是**结果**，不是把档位关掉了。
        var policy = Build(WorkMode.StopOnStaticAfterRescan, StaticStopOption.Two);
        var state = Open(A) with
        {
            TrackedWaybillLeftFrame = false,
            StaticFor = TimeSpan.FromMinutes(5),
        };

        // 面单没离场过 ⇒ 本模式自己的判据不成立 ⇒ 不停。
        Assert.IsType<WorkDecision.Nothing>(policy.OnStatic(state, isStatic: true));
    }

    // ─────────────────────────────────────────────
    // 手动停
    // ─────────────────────────────────────────────

    [Fact]
    public void 空闲时手动停不做任何事()
    {
        Assert.IsType<WorkDecision.Nothing>(WorkModePolicy.OnManualStop(WorkModeState.Idle));
    }

    [Fact]
    public void 段中手动停就是停()
    {
        var decision = WorkModePolicy.OnManualStop(Open(A));

        var stop = Assert.IsType<WorkDecision.StopSegment>(decision);
        Assert.Equal(StopReason.Manual, stop.Reason);
    }

    private static WorkModePolicy Build(
        WorkMode mode, StaticStopOption staticOption = StaticStopOption.Off) =>
        new(mode, staticOption);

    private static WorkModeState Open(WaybillNumber waybill) =>
        new(SegmentOpen: true, Current: waybill);
}
