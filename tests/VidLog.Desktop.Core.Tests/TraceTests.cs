using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 请求关联 id（<c>trace</c>）：把一次请求散在各层的日志串起来。
/// </summary>
public class TraceTests
{
    public TraceTests() => Trace.Clear();

    [Fact]
    public void 没设过的时候是空的()
    {
        Assert.Null(Trace.Current);
    }

    [Fact]
    public void 设了之后读得到_清掉之后就没有了()
    {
        var id = Trace.Start();

        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(id, Trace.Current);

        Trace.Clear();
        Assert.Null(Trace.Current);
    }

    [Fact]
    public void id_是八个十六进制字符()
    {
        // 长度是给人肉眼在诊断包里对日志用的，不是安全标识。
        Assert.Matches("^[0-9a-f]{8}$", Trace.Start());
    }

    [Fact]
    public async Task await_出去换了线程之后仍然是同一个()
    {
        // ⚠️ **这条是 AsyncLocal 的全部意义**：一次上传要过七八层，
        // 中间 await 到别的线程上（线程池）。ID 必须跟着走，
        // 否则「把这一次请求的行串起来」在第一个 await 之后就断了。
        var id = Trace.Start();

        await Task.Yield();
        await Task.Run(() => { });

        Assert.Equal(id, Trace.Current);
    }

    [Fact]
    public async Task 两个并发任务各有各的_id_互不串()
    {
        // ⚠️ 这一条是**不能用 `[ThreadStatic]` 的原因**：
        // HTTP 处理是线程池上的异步续体，同一个线程会先后跑不同请求的代码。
        static async Task<string?> RunOnce()
        {
            Trace.Start();
            await Task.Delay(20);
            return Trace.Current;
        }

        var ids = await Task.WhenAll(Task.Run(RunOnce), Task.Run(RunOnce));

        Assert.NotNull(ids[0]);
        Assert.NotNull(ids[1]);
        Assert.NotEqual(ids[0], ids[1]);
    }

    [Fact]
    public async Task 子任务里设的_id_不会回流到外面()
    {
        // 流动是**向下**的。反过来的话，一个后台任务会把外层请求的 id 顶掉，
        // 而那种串错比没有 id 更误导。
        Trace.Start();
        var outer = Trace.Current;

        await Task.Run(() => Trace.Start());

        Assert.Equal(outer, Trace.Current);
    }
}
