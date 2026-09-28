using System.IO;
using VidLog.Desktop.Core.Clock;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 校准状态的落盘与读回（规格 §3.6.4）。
/// </summary>
/// <remarks>
/// ⚠️ 这个类里最重要的一条是 <see cref="读校准文件时_带着永不执行的同步上下文也不会挂住"/> ——
/// 它盯的是一个**曾经把整个电脑端打死**的毛病：校准过之后再启动，
/// 进程永久挂住、窗口永远不出现（2026-09-28 实测，见 `docs/实现决策.md`）。
/// </remarks>
public class CalibrationStoreTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-calib-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清不掉不该让测试变红。
            }
        }
    }

    /// <summary>
    /// 一个「Post 进去的回调永远不会被执行」的同步上下文 —— 也就是**卡死的 UI 线程**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不能直接用 <see cref="SynchronizationContext"/> 基类：它的 <c>Post</c>
    /// 是投到线程池的，那样续体照样会跑，复现不出死锁。
    /// </remarks>
    private sealed class NeverPumpingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // 故意什么都不做。
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            // 故意什么都不做。
        }
    }

    [Fact]
    public async Task 存了再读回来还是那一份()
    {
        using var temp = new TempDir();
        var store = new CalibrationStore(temp.File("calibration.json"));

        var saved = CalibrationState.Empty with { AnchorUtc = DateTimeOffset.UtcNow };
        await store.SaveAsync(saved);

        var loaded = await store.LoadAsync();

        Assert.Equal(saved.AnchorUtc, loaded.AnchorUtc);
    }

    [Fact]
    public async Task 没有文件时返回没校准过而不是抛()
    {
        using var temp = new TempDir();

        var loaded = await new CalibrationStore(temp.File("什么都没有.json")).LoadAsync();

        Assert.False(loaded.NeedsRecalibration);
        Assert.Null(loaded.AnchorUtc);
    }

    [Fact]
    public async Task 文件被改坏了当作没校准过而不是抛()
    {
        using var temp = new TempDir();
        var path = temp.File("calibration.json");
        await System.IO.File.WriteAllTextAsync(path, "{ 这不是 json");

        var loaded = await new CalibrationStore(path).LoadAsync();

        Assert.Null(loaded.AnchorUtc);
    }

    /// <summary>
    /// **电脑端「只能启动一次」那个 bug 的绊线。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// 复现的是：<c>DesktopServices.Create</c> 在 **UI 线程**上
    /// <c>calibration.LoadAsync().GetAwaiter().GetResult()</c> 阻塞等它。
    /// 只要 <c>LoadAsync</c> 里那个 await 少了 <c>ConfigureAwait(false)</c>，
    /// 续体就被投回这个永不执行的上下文 ⇒ **永久挂住**。
    /// </para>
    /// <para>
    /// ⚠️ 为什么以前抓不到：普通测试线程上 <c>SynchronizationContext.Current</c> 是
    /// <see langword="null"/>，await 直接在线程池上续，怎么都不会死锁；
    /// 而 App 层（真正有 UI 线程的那层）**没有测试工程**。
    /// 所以这里必须自己造一个上下文，**并且另起一条线程**去挂 ——
    /// 挂在 xunit 的测试线程上就不是「失败」而是「整个测试进程卡死」了。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 读校准文件时_带着永不执行的同步上下文也不会挂住()
    {
        using var temp = new TempDir();
        var path = temp.File("calibration.json");

        // 先真的写出一份 —— 没有文件就走不到那个 await，也就复现不出死锁。
        await new CalibrationStore(path).SaveAsync(
            CalibrationState.Empty with { AnchorUtc = DateTimeOffset.UtcNow });

        using var finished = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            // 模拟 UI 线程：Post 进来的续体永远不会被执行。
            SynchronizationContext.SetSynchronizationContext(new NeverPumpingContext());
            new CalibrationStore(path).LoadAsync().GetAwaiter().GetResult();
            finished.Set();
        })
        {
            // ⚠️ 后台线程：真死锁时让它跟着测试进程一起走，
            // 而不是留一条前台线程把 dotnet test 吊在那里。
            IsBackground = true,
        };

        thread.Start();

        Assert.True(
            finished.Wait(TimeSpan.FromSeconds(10)),
            "LoadAsync 在带同步上下文的线程上没返回 —— 又死锁了。"
            + "检查 CalibrationStore 里那几个 await 是不是少了 ConfigureAwait(false)。");
    }
}
