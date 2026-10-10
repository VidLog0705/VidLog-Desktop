using VidLog.Desktop.Core.Camera;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 识别框（ROI）：喂 ZXing 的那一片是**裁出来**的，框外进不去解码器。
/// </summary>
/// <remarks>
/// 需求方 2026-10-10：框内才识别、框外不识别（防误扫）。这里钉的是「裁对了没有」——
/// 裁错的表现是「眼看着在框里却认不出来」或者「框外的分拣码反而被认了」，两种都很难查。
/// </remarks>
public class RecognitionRoiTests
{
    [Fact]
    public void 默认框是中央60乘50()
    {
        // 640×480 上：x=128 y=120 w=384 h=240
        Assert.Equal((128, 120, 384, 240), RecognitionRoi.Default.ToPixels(640, 480));
    }

    [Fact]
    public void 只裁框里那一片_框外的像素一个都不进()
    {
        // 4×4 的帧，每个像素给一个可区分的亮度。取中央 50%×50%（正中的 2×2）。
        var rgb = new byte[4 * 4 * 3];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var v = (byte)(y * 4 + x);   // 0..15
                var i = (y * 4 + x) * 3;
                rgb[i] = rgb[i + 1] = rgb[i + 2] = v;
            }
        }

        var frame = new PreviewFrame(rgb, 4, 4, 7);
        var roi = new RecognitionRoi(0.25, 0.25, 0.50, 0.50);

        var cropped = frame.ToGray(roi);

        Assert.Equal(2, cropped.Width);
        Assert.Equal(2, cropped.Height);
        Assert.Equal(7, cropped.CapturedAtMs);

        // 正中的四格 = 原来的 (1,1) (2,1) (1,2) (2,2) = 5 6 9 10
        Assert.Equal(new byte[] { 5, 6, 9, 10 }, cropped.Gray);
    }

    [Fact]
    public void 框越界时夹回画面内_不会读出画面外()
    {
        // 起点在画面外、宽高也超出去 —— 越界读会拿到**别的行**的像素（静默花屏）。
        var roi = new RecognitionRoi(-0.5, -0.5, 2.0, 2.0);
        var (x, y, w, h) = roi.ToPixels(10, 8);

        Assert.Equal((0, 0, 10, 8), (x, y, w, h));
    }

    // ─────────────────────────────────────────────
    // 画在界面上的那四段直角（识别框的可视那一半）
    // ─────────────────────────────────────────────

    [Fact]
    public void 容器比例与画面一致时_框就是容器上的同一比例()
    {
        // 640×360 的画面放进 640×360 的容器 —— 没有黑边，框就是中央 60%×50%。
        var box = RecognitionBox.Fit(640, 360, 640, 360);

        Assert.Equal(new RecognitionBox(128, 90, 384, 180), box);
    }

    [Fact]
    public void 画面比容器窄时_框要落在画面那一块里而不是容器上()
    {
        // ⚠️ 这条是「框画歪了」的绊线：640×480（4:3）放进 640×360（16:9）的容器，
        // 等比缩放之后画面只占中间 480×360，左右各留 80 的黑边。
        // 拿容器尺寸直接乘比例的话，框会整体偏左、还比应得的大。
        var box = RecognitionBox.Fit(640, 360, 640, 480);

        // 画面那一块：x 从 80 到 560，高 360。
        Assert.Equal(80 + 0.20 * 480, box.X, 6);
        Assert.Equal(0 + 0.25 * 360, box.Y, 6);
        Assert.Equal(0.60 * 480, box.Width, 6);
        Assert.Equal(0.50 * 360, box.Height, 6);
    }

    [Fact]
    public void 容器为0时不抛也不画出东西()
    {
        // 窗口刚建出来还没布局时就是这个状态（ActualWidth/Height = 0）。
        Assert.Equal(default, RecognitionBox.Fit(0, 0, 640, 360));
    }

    [Fact]
    public void 四个角是四段互不相连的直角()
    {
        var box = new RecognitionBox(100, 50, 200, 100);

        var corners = box.Corners(20);

        Assert.Equal(4, corners.Count);

        // 每段首尾都在框上、中间那点是**角**（直角的那一折）。
        Assert.Equal((100, 70, 100, 50, 120, 50), corners[0]);      // 左上
        Assert.Equal((280, 50, 300, 50, 300, 70), corners[1]);      // 右上
        Assert.Equal((300, 130, 300, 150, 280, 150), corners[2]);   // 右下
        Assert.Equal((120, 150, 100, 150, 100, 130), corners[3]);   // 左下

        // ⚠️ 「互不相连」= 任何两段都没有共用的端点（连起来就成了普通矩形框）。
        var ends = corners
            .SelectMany(c => new[] { (c.X1, c.Y1), (c.X3, c.Y3) })
            .ToList();

        Assert.Equal(ends.Count, ends.Distinct().Count());
    }

    [Fact]
    public void 臂长按短边夹住_小框上四条臂不会穿插()
    {
        var box = new RecognitionBox(0, 0, 40, 20);

        // 要 100 的臂，实际最多 10（短边 20 的一半）。
        var corners = box.Corners(100);

        // 左上的两条臂都只走了 10（要 100 也没用，再长两条臂就在小框里绕成一团）。
        Assert.Equal(10, corners[0].Y1);
        Assert.Equal(10, corners[0].X3);
    }

    // ─────────────────────────────────────────────
    // 真实画面那一块（需求方 2026-10-10：两路必须圈到同一片现实区域）
    // ─────────────────────────────────────────────

    [Fact]
    public void 按源尺寸算出画面那一块_补黑边的那一边才对得上()
    {
        // 1920×1080 缩进 640×480 ⇒ 640×360，上下各 60 黑边。
        Assert.Equal((0, 60, 640, 360), PreviewFrame.FitPicture(1920, 1080, 640, 480));

        // 1920×1080 缩进 640×360 ⇒ 正好铺满，没有黑边。
        Assert.Equal((0, 0, 640, 360), PreviewFrame.FitPicture(1920, 1080, 640, 360));

        // 4:3 的源缩进 16:9 的框 ⇒ 左右补黑边。
        Assert.Equal((80, 0, 480, 360), PreviewFrame.FitPicture(640, 480, 640, 360));

        // 源尺寸不知道时不猜（退回整幅）。
        Assert.Equal((0, 0, 640, 480), PreviewFrame.FitPicture(0, 0, 640, 480));
    }

    [Fact]
    public void 待扫那一路的黑边不算进画面_裁出来的那一片与录制那一路逐像素相同()
    {
        // 同一幅 640×360 的现场：录制帧铺满；待扫帧上下各垫 60 的黑边装进 640×480。
        var shot = new byte[640 * 360 * 3];
        for (var i = 0; i < shot.Length; i++)
        {
            shot[i] = (byte)(i % 251);
        }

        var tapRgb = new byte[640 * 480 * 3];
        Array.Copy(shot, 0, tapRgb, 60 * 640 * 3, shot.Length);   // 黑边那两段留 0

        var tap = new PreviewFrame(tapRgb, 640, 480, 1)
        {
            Picture = PreviewFrame.FitPicture(1920, 1080, 640, 480),   // = (0,60,640,360)
        };
        var preview = new PreviewFrame((byte[])shot.Clone(), 640, 360, 1);

        // ⚠️ 这条就是「框外不识别」那条规矩的地基：两路喂进解码器的必须是**同一片**。
        // 从前拿整幅乘比例，待扫那一路会连黑边一起算 ⇒ 圈到 16.7%..83.3%、录制那一路
        // 25%..75%，于是「框里明明有码」在一种状态下认得出、另一种状态下认不出。
        Assert.Equal(
            preview.ToGray(RecognitionRoi.Default).Gray,
            tap.ToGray(RecognitionRoi.Default).Gray);
    }

    [Fact]
    public void 框要按真实画面的尺寸算_不是按带补边的整幅帧()
    {
        // ⚠️ 界面上那幅画面是**先按 `PreviewFrame.Picture` 裁掉补边才上屏的**
        // （待扫那一路的帧是 640×480，画面只占 y 60..420 ⇒ 上屏的是 640×360），
        // 所以 `Fit` 拿到的是画面那一块的尺寸。
        var onPicture = RecognitionBox.Fit(640, 480, 640, 360);

        // 640×360 放进 640×480 的容器 ⇒ 上下各留 60 的黑边，框落在 (128,150,384,180)。
        Assert.Equal(new RecognitionBox(128, 150, 384, 180), onPicture);

        // ⚠️ 传整幅帧的尺寸 = 旧行为（框连补边一起算）：整体偏上、还高 60。
        // 那正是要修的错位 —— 这条断言就是「调用方别忘了传画面尺寸」的绊线。
        Assert.Equal(new RecognitionBox(128, 120, 384, 240), RecognitionBox.Fit(640, 480, 640, 480));
    }
}
