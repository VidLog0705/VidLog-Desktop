using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 规格探测的闸（C2，2026-10-10）。
/// </summary>
/// <remarks>
/// ⚠️ 这一组测的就是**互斥**本身：并发进来是不是真的串住了。
/// 它原来长在装配层（那里没有测试工程），只能靠读代码确认 —— 而互斥写错了
/// 平时完全看不出来，只在两条探测真的撞上那一下把能用的组合误判成跑不通。
/// </remarks>
public class SpecProbeGateTests
{
    [Fact]
    public async Task 两个探测不会同时跑()
    {
        // 反证配方：去掉 `SpecProbeGate.RunAsync` 里的
        // `await _gate.WaitAsync(...)` 与 `finally { _gate.Release(); }`
        // ⇒ 两条 probe 重叠，这条红（`overlapped` 为真）。
        var gate = new SpecProbeGate();
        var inside = 0;
        var overlapped = false;

        async Task Probe()
        {
            if (Interlocked.Increment(ref inside) > 1)
            {
                overlapped = true;
            }

            await Task.Delay(150);

            Interlocked.Decrement(ref inside);
        }

        var first = gate.RunAsync(() => false, Probe);
        var second = gate.RunAsync(() => false, Probe);

        await Task.WhenAll(first, second);

        Assert.False(overlapped);
        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(0, gate.SkippedCount);
    }

    [Fact]
    public async Task 等门期间发现这一档已经探过就不再探()
    {
        // ⚠️ 这一条是**两次扫码挨太近**那个现场：第二条等门时，第一条已经探完，
        // 结论（`ProbedSpec` / 采集对象 / 编码器）都落定了 —— 再探一次只是
        // 白开一次相机、白等十秒。
        // 反证配方：把 `if (alreadyDone()) { … return false; }` 整段去掉
        // ⇒ 这条红（`probes` 会是 2）。
        var gate = new SpecProbeGate();
        var done = false;
        var probes = 0;

        async Task Probe()
        {
            Interlocked.Increment(ref probes);
            await Task.Delay(50);
            done = true;
        }

        var first = gate.RunAsync(() => done, Probe);
        var second = gate.RunAsync(() => done, Probe);

        Assert.True(await first);    // 真探了
        Assert.False(await second);  // 跳过 —— caller 拿这个去记那条日志
        Assert.Equal(1, probes);
        Assert.Equal(1, gate.SkippedCount);
    }

    [Fact]
    public async Task 该探的时候探完了会把门放掉_后面那条不会被永久卡住()
    {
        // ⚠️ 一条探完（哪怕是**抛异常**）之后门必须放掉，否则后续每一次开段
        // 都会卡在 `WaitAsync` 上 —— 那是比原来的并发 bug 更坏的一种：
        // 不再是「偶尔误判」，而是「从此录不了」。
        // 反证配方：把 `Release()` 从 `finally` 里挪到 `try` 末尾 ⇒ 这条红（第二次永远不返回）。
        var gate = new SpecProbeGate();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.RunAsync(() => false, () => throw new InvalidOperationException("探测炸了")));

        var ran = false;

        // ⚠️ 加超时：门漏了 Release 时这条**会挂住**而不是失败，
        // 挂住比失败更难看懂（测试进程一直不返回）。超时把它变成一条红的断言。
        var ok = await gate.RunAsync(() => false, () => { ran = true; return Task.CompletedTask; })
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(ok);
        Assert.True(ran);
    }
}
