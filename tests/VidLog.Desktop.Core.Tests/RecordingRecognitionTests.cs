using VidLog.Desktop.Core.Camera;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 录制期识码：开录之后**仍然有人在读码**（不然同码复扫停录、连扫换件整个失灵）。
/// </summary>
/// <remarks>
/// 需求方 2026-10-10 的两条现场缺陷（「同码停模式第二遍扫它不停止录制」）
/// 落在这上面：相机是独占的，开段时待扫那个进程被整个交出去，**录制中没人读码**。
/// <para>
/// ⚠️ 这里钉的两件事各自都会单独出事故，而且都不报错：
/// <list type="bullet">
/// <item><b>开段那一刻不能报</b>：面单还摆在框里，报了就是「刚开录就停」。</item>
/// <item><b>离场再回来要报</b>：不然「再扫一次同一个单号」永远不被认，那段永远停不掉。</item>
/// </list>
/// 两件事方向相反 ⇒ 必须各自有用例。
/// </para>
/// </remarks>
public class RecordingRecognitionTests
{
    private static readonly WaybillNumber A = WaybillNumber.Parse("SF1234567890");
    private static readonly WaybillNumber B = WaybillNumber.Parse("SF9999999999");

    /// <summary>脚本化解码器：要它读什么就读什么，顺带记下被叫了几次。</summary>
    private sealed class ScriptedScanner : IFrameScanner
    {
        private string? _result;

        public bool Throw { get; set; }

        public int Decodes;

        /// <summary>最近一次被喂进来的那一帧的尺寸（验「只喂识别框那一片」）。</summary>
        public (int Width, int Height) LastSize;

        public string? Result
        {
            get => _result;
            set => _result = value;
        }

        public string? TryDecode(CameraFrame frame)
        {
            Interlocked.Increment(ref Decodes);
            LastSize = (frame.Width, frame.Height);

            if (Throw)
            {
                throw new InvalidOperationException("解码器炸了");
            }

            return _result;
        }
    }

    /// <summary>一张纯黑帧 —— 内容无所谓，解码器是脚本化的。</summary>
    private static PreviewFrame Frame() =>
        new(new byte[640 * 360 * 3], 640, 360, Environment.TickCount64);

    /// <summary>
    /// 一直投帧直到 <paramref name="until"/> 成立（或超时），返回收集到的单号。
    /// </summary>
    /// <remarks>
    /// 投帧循环必须**在测试这边**跑：真实现里帧是采集进程推过来的，
    /// 而录制的解码循环是「每 3 帧解一次」的节拍 —— 不持续投就没有节拍。
    /// </remarks>
    private static async Task<List<WaybillNumber>> PumpAsync(
        RecordingRecognition recognition,
        ScriptedScanner scanner,
        Func<List<WaybillNumber>, bool> until,
        int timeoutMs = 4000)
    {
        var seen = new List<WaybillNumber>();
        recognition.Scanned += w => { lock (seen) { seen.Add(w); } };

        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            recognition.OnFrame(Frame());
            await Task.Delay(10);

            lock (seen)
            {
                if (until(seen))
                {
                    return seen;
                }
            }
        }

        lock (seen)
        {
            return seen;
        }
    }

    /// <summary>持续投帧一段时间（不关心上报了什么）。</summary>
    private static async Task PumpForAsync(RecordingRecognition recognition, int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;

        while (Environment.TickCount64 < deadline)
        {
            recognition.OnFrame(Frame());
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task 认到单号就上报()
    {
        var scanner = new ScriptedScanner { Result = A.Value };
        await using var recognition = new Harness(scanner);

        var seen = await PumpAsync(recognition.Value, scanner, s => s.Count >= 1);

        Assert.Contains(A, seen);

        // 它认的是**框里那一片**，不是整幅（需求方：框外不识别）——
        // 640×360 上中央 60%×50% = 384×180。喂整幅的话框外的分拣码也会被认。
        Assert.Equal((384, 180), scanner.LastSize);
    }

    [Fact]
    public async Task 刚开录时同一个码不再报一次()
    {
        // ⚠️ 这条是「同码停模式刚开录就停」的绊线：开段那一刻面单还在框里。
        var scanner = new ScriptedScanner { Result = A.Value };
        await using var recognition = new Harness(scanner);

        recognition.Value.PrimeWith(A.Value);

        var seen = await PumpAsync(recognition.Value, scanner, s => s.Count > 0, timeoutMs: 800);

        Assert.Empty(seen);
    }

    [Fact]
    public async Task 换成别的码立刻上报()
    {
        // 连扫模式的换件就靠这条：框里换了件要马上认得出来。
        var scanner = new ScriptedScanner { Result = A.Value };
        await using var recognition = new Harness(scanner);

        recognition.Value.PrimeWith(A.Value);

        scanner.Result = B.Value;

        var seen = await PumpAsync(recognition.Value, scanner, s => s.Contains(B));

        Assert.Contains(B, seen);
        Assert.DoesNotContain(A, seen);
    }

    [Fact]
    public async Task 离场够久再回来才算又扫了一次()
    {
        // 同码停模式「再扫一次同一个单号就停录」—— 面单拿开、过一会儿再放回来。
        // ⚠️ 那个「够久」是 `DecodeGateOptions.Default` 的 2 秒，所以这里要真等。
        var scanner = new ScriptedScanner { Result = A.Value };
        await using var recognition = new Harness(scanner);

        recognition.Value.PrimeWith(A.Value);

        // ① 码离场（解码器读不到东西）—— 闸从这一刻起给「离场」计时。
        // ⚠️ 这 2.4 秒里**必须继续投帧**：闸是拿「读到的每一帧」判离场的，
        // 停投就等于把镜头盖上了 —— 那是「关掉」不是「拿开」。
        scanner.Result = null;
        await PumpForAsync(recognition.Value, 2400);

        // ② 同一个码回来了。
        scanner.Result = A.Value;

        var seen = await PumpAsync(recognition.Value, scanner, s => s.Count > 0);

        Assert.Equal(new[] { A }, seen);
    }

    [Fact]
    public async Task 解码器抛异常不会把循环带下去()
    {
        // 一帧失败是常态（歪的、糊的、没码的）—— 循环死了的话，
        // 现场的下一步是「这一整段录制再也扫不出任何东西」，而且日志里只有一条异常。
        var scanner = new ScriptedScanner { Result = A.Value, Throw = true };
        await using var recognition = new Harness(scanner);

        await PumpAsync(recognition.Value, scanner, s => s.Count > 0, timeoutMs: 600);
        Assert.True(scanner.Decodes > 0, "循环一次都没跑到解码器");

        // 解码器恢复正常之后仍然要认得出。
        scanner.Throw = false;

        var seen = await PumpAsync(recognition.Value, scanner, s => s.Count >= 1);

        Assert.Contains(A, seen);
    }

    /// <summary>
    /// 把「起循环 / 停循环」包起来，测试里 <c>await using</c> 一下就行。
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        public Harness(IFrameScanner scanner)
        {
            Value = new RecordingRecognition(scanner, NullLogger.Instance);
            Value.Start();
        }

        public RecordingRecognition Value { get; }

        public ValueTask DisposeAsync() => new(Value.StopAsync());
    }
}
