using VidLog.Desktop.Core.Camera;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 帧通路：静止判定、识码闸。
/// </summary>
/// <remarks>
/// 两块都不需要真摄像头 —— 合成帧就够，所以它们能进 CI。
/// <para>
/// ⚠️ 「读帧」与「单槽缓冲」从前也在这里（<c>RawGrayFrameReader</c> /
/// <c>SingleSlotFrameSink</c>），2026-10-09 那两个类随灰度那一路一起删了 ——
/// 同一件事现在只有一处（<c>PreviewProcess.ReadFramesAsync</c> /
/// <c>SingleSlotPreviewSink</c>），那几条用例跟着搬到了 <c>PreviewProcessTests</c>。
/// </para>
/// </remarks>
public class CameraFramesTests
{
    private const int W = 64;
    private const int H = 48;

    private static CameraFrame Frame(byte fill, long at = 0) =>
        new([.. Enumerable.Repeat(fill, W * H)], W, H, at);

    // ─────────────────────────────────────────────
    // 静止判定
    // ─────────────────────────────────────────────

    [Fact]
    public void 第一帧算静止_因为没有可比的对象()
    {
        var detector = new FrameMotionDetector();

        Assert.True(detector.Observe(Frame(100)).IsStatic);
    }

    [Fact]
    public void 完全相同的两帧算静止()
    {
        var detector = new FrameMotionDetector();
        detector.Observe(Frame(100));

        Assert.True(detector.Observe(Frame(100)).IsStatic);
    }

    [Fact]
    public void 整体亮度漂移仍算静止()
    {
        // 这是**自动曝光**的形状：整幅画面一起变亮，但内容没动。
        // 直接比绝对差会把它当成「在动」，于是永远判不出静止 ——
        // 先减均值正是为了消掉这种全局漂移。
        var detector = new FrameMotionDetector();
        detector.Observe(Frame(100));

        var sample = detector.Observe(Frame(150));

        Assert.True(sample.IsStatic, $"整体变亮不该算动（变化占比 {sample.ChangedRatio:P1}）");
    }

    [Fact]
    public void 画面内容变了就不算静止()
    {
        var detector = new FrameMotionDetector();
        var still = Frame(100);
        detector.Observe(still);

        // 半边画面从暗变亮 —— 内容真的动了。
        var moved = new byte[W * H];
        for (var i = 0; i < moved.Length; i++)
        {
            moved[i] = (byte)(i < moved.Length / 2 ? 100 : 220);
        }

        var sample = detector.Observe(new CameraFrame(moved, W, H, 0));

        Assert.False(sample.IsStatic);
    }

    [Fact]
    public void 少量噪点不算动()
    {
        var detector = new FrameMotionDetector();
        detector.Observe(Frame(100));

        // 只改几个像素 —— 传感器噪声。
        var noisy = new byte[W * H];
        Array.Fill(noisy, (byte)100);
        noisy[0] = 200;
        noisy[1] = 10;

        Assert.True(detector.Observe(new CameraFrame(noisy, W, H, 0)).IsStatic);
    }

    [Fact]
    public void 尺寸变了要重新起算_不拿尺寸不同的帧比()
    {
        var detector = new FrameMotionDetector();
        detector.Observe(Frame(100));

        // 换分辨率之后不该拿旧帧比 —— 那是无意义的比较。
        var resized = new CameraFrame(new byte[32 * 32], 32, 32, 0);

        Assert.True(detector.Observe(resized).IsStatic);
    }

    // ─────────────────────────────────────────────
    // 识码闸
    // ─────────────────────────────────────────────

    [Fact]
    public void 第一次读到就上报()
    {
        var gate = new DecodeGate();

        Assert.Equal("SF1", gate.Observe("SF1", 0));
        Assert.True(gate.IsPresent);
    }

    [Fact]
    public void 同一个码连续读到不重复上报()
    {
        // 摄像头是**连续**读码的，而业务要的是「又扫了一次」。
        // 不设这道闸，包裹放上去就会被自己的持续识别反复触发。
        var gate = new DecodeGate();

        Assert.Equal("SF1", gate.Observe("SF1", 0));
        Assert.Null(gate.Observe("SF1", 100));
        Assert.Null(gate.Observe("SF1", 200));
    }

    [Fact]
    public void 码离开够久再回来才算新的一次识别()
    {
        var gate = new DecodeGate();

        Assert.Equal("SF1", gate.Observe("SF1", 0));

        // 离场。
        Assert.Null(gate.Observe(null, 100));
        Assert.True(gate.HasLeft);

        // 2 秒之后回来 —— 这是一次新的识别。
        Assert.Equal("SF1", gate.Observe("SF1", 100 + 2000));
    }

    [Fact]
    public void 离开得不够久不算新识别()
    {
        var gate = new DecodeGate();

        gate.Observe("SF1", 0);
        gate.Observe(null, 100);

        // 只离开了 500 毫秒 —— 是抖动，不是「又扫了一次」。
        Assert.Null(gate.Observe("SF1", 600));
    }

    [Fact]
    public void 换成另一个码就是新的一次()
    {
        var gate = new DecodeGate();

        Assert.Equal("SF1", gate.Observe("SF1", 0));
        Assert.Equal("SF2", gate.Observe("SF2", 100));
    }
}
