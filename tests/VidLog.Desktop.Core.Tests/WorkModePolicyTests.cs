using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 工作模式的判定（规格 §3.3.1 / §3.3.2 / §3.3.3）。
/// </summary>
/// <remarks>
/// <para>
/// 策略是**纯函数**，所以规格 §3.3.1 那张表的每一格都能直接断言。
/// </para>
/// <para>
/// ⚠️ <b>电脑端只有两种模式</b>：规格 2026-09-24 裁定删掉「扫码静止停录」
/// （§3.3.1）。原来那一组「静止停录 / 扫码静止那 2 秒」的用例
/// **随功能一起删掉了** —— 不是"改成永远通过"，是那几个行为在这端不存在了。
/// 手机端仍然有三种，那边的用例在 `VidLog-Mobile/test/stop_controller_test.dart`。
/// </para>
/// </remarks>
public class WorkModePolicyTests
{
    private static readonly WaybillNumber A = WaybillNumber.Parse("SF1234567890");
    private static readonly WaybillNumber B = WaybillNumber.Parse("SF9999999999");

    // ─────────────────────────────────────────────
    // 开录：两个模式都一样
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData(WorkMode.Continuous)]
    [InlineData(WorkMode.StopOnSameWaybill)]
    public void 空闲时扫到单号就开录(WorkMode mode)
    {
        var decision = Build(mode).OnScan(WorkModeState.Idle, A);

        var start = Assert.IsType<WorkDecision.StartSegment>(decision);
        Assert.Equal(A, start.Waybill);
    }

    [Fact]
    public void 电脑端只有两种模式_没有了扫码静止停录()
    {
        // 规格 §3.3.1 的原话：「电脑端不要静止停录，电脑端只保留连续扫码和同码停录模式。」
        // 这条钉的是**枚举本身** —— 有人照着手机端（三种）把它加回来时会红。
        Assert.Equal(
            [WorkMode.Continuous, WorkMode.StopOnSameWaybill],
            Enum.GetValues<WorkMode>());
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

    [Fact]
    public void 同码停复扫同码即停()
    {
        var decision = Build(WorkMode.StopOnSameWaybill).OnScan(Open(A), A);

        var stop = Assert.IsType<WorkDecision.StopSegment>(decision);
        Assert.Equal(StopReason.SameWaybillRescan, stop.Reason);
    }

    // ─────────────────────────────────────────────
    // 段中扫到**不同**单号 —— 两个模式在这里分道扬镳
    // ─────────────────────────────────────────────

    [Fact]
    public void 连续扫下扫到异码就是换件_不是错误()
    {
        // 规格 2026-09-22 的需求变更：连续扫改成换段式。
        var decision = Build(WorkMode.Continuous).OnScan(Open(A), B);

        var next = Assert.IsType<WorkDecision.SwitchTo>(decision);
        Assert.Equal(B, next.Next);
    }

    [Fact]
    public void 同码停扫到异码只提示不停录()
    {
        // 规格 §3.3.2 错码保护。⚠️ 2026-09-24 起它**在电脑端只挂在「同码停」上**
        // （另一处挂载点「扫码静止停录」已经删掉）。
        var decision = Build(WorkMode.StopOnSameWaybill).OnScan(Open(A), B);

        var announce = Assert.IsType<WorkDecision.Announce>(decision);
        Assert.Equal(AnnouncementKind.WrongWaybill, announce.Kind);
    }

    // ─────────────────────────────────────────────
    // 闲置提醒（规格 §3.3.3 电脑端那半）
    // ─────────────────────────────────────────────

    [Fact]
    public void 档位关闭时永远不提醒()
    {
        var decision = Build(WorkMode.StopOnSameWaybill, IdleReminderOption.Off)
            .OnIdle(Open(A) with { IdleFor = TimeSpan.FromHours(1) });

        Assert.IsType<WorkDecision.Nothing>(decision);
    }

    [Fact]
    public void 档位未到时不提醒()
    {
        var decision = Build(WorkMode.StopOnSameWaybill, IdleReminderOption.Three)
            .OnIdle(Open(A) with { IdleFor = TimeSpan.FromMinutes(2) });

        Assert.IsType<WorkDecision.Nothing>(decision);
    }

    [Fact]
    public void 档位到时提醒()
    {
        var decision = Build(WorkMode.StopOnSameWaybill, IdleReminderOption.Three)
            .OnIdle(Open(A) with { IdleFor = TimeSpan.FromMinutes(3) });

        var announce = Assert.IsType<WorkDecision.Announce>(decision);
        Assert.Equal(AnnouncementKind.Idle, announce.Kind);
    }

    [Fact]
    public void 闲置提醒绝不产出停录()
    {
        // 规格 §3.3.3 原话：「**它只提醒，绝不改录制状态** —— 用户不理就一直录。
        // 最终把它停掉的是 §3.3.4 的时长兜底」。
        //
        // 这条是这一组里最要紧的：把闲置接成「到点就停」是个很自然的写法
        // （两个都叫"防忘停录"），而那样操作员离开一会儿就会被停掉一段录像。
        foreach (var option in Enum.GetValues<IdleReminderOption>())
        {
            var decision = Build(WorkMode.StopOnSameWaybill, option, customMinutes: 1)
                .OnIdle(Open(A) with { IdleFor = TimeSpan.FromDays(1) });

            Assert.IsNotType<WorkDecision.StopSegment>(decision);
        }
    }

    [Fact]
    public void 空闲时闲置不提醒()
    {
        // 没在录就没什么可提醒的 —— 而且调用方已经按「本段已录时长」封了顶（I12）。
        var decision = Build(WorkMode.StopOnSameWaybill, IdleReminderOption.Two)
            .OnIdle(WorkModeState.Idle with { IdleFor = TimeSpan.FromHours(1) });

        Assert.IsType<WorkDecision.Nothing>(decision);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(120)]
    public void 自定义档按填的分钟数走(int minutes)
    {
        var policy = Build(WorkMode.StopOnSameWaybill, IdleReminderOption.Custom, minutes);

        Assert.IsType<WorkDecision.Nothing>(
            policy.OnIdle(Open(A) with { IdleFor = TimeSpan.FromMinutes(minutes - 1) }));

        var announce = Assert.IsType<WorkDecision.Announce>(
            policy.OnIdle(Open(A) with { IdleFor = TimeSpan.FromMinutes(minutes) }));

        Assert.Equal(AnnouncementKind.Idle, announce.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(24 * 60 + 1)]
    public void 自定义档越界时回落到默认三分钟_而不是夹到边界(int minutes)
    {
        // 与设置层一贯的「越界回落默认值」同一条规矩（I4 的精神）。
        // 夹到边界的话，「自定义 0 分钟」会变成「1 分钟」—— 一开录就提醒。
        var policy = Build(WorkMode.StopOnSameWaybill, IdleReminderOption.Custom, minutes);

        Assert.IsType<WorkDecision.Nothing>(
            policy.OnIdle(Open(A) with { IdleFor = TimeSpan.FromMinutes(2) }));

        Assert.IsType<WorkDecision.Announce>(
            policy.OnIdle(Open(A) with { IdleFor = TimeSpan.FromMinutes(3) }));
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
        WorkMode mode,
        IdleReminderOption idle = IdleReminderOption.Off,
        int customMinutes = 0) =>
        new(mode, idle, customMinutes);

    private static WorkModeState Open(WaybillNumber waybill) =>
        new(SegmentOpen: true, Current: waybill);
}
