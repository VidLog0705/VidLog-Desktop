using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 取景框上那两行字（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// ⚠️ 两行原先都长在 <c>MainWindow</c> 里，而那个工程没有测试工程。
/// 它们各自有一条**说错了很坏**的规矩：水印不许在未校准时显示一个时间，
/// 中央那句的**先后次序**决定了用户以为「机器缺东西」还是「还没开工」。
/// </remarks>
public class PreviewTextsTests
{
    // ─────────────────────────────────────────────
    // 右上角：可信时间的水印
    // ─────────────────────────────────────────────

    [Fact]
    public void 未校准时只说未校准_不许显示一个时间()
    {
        // ⚠️ 那时可信时钟的 Now 会静默回落到系统墙钟，而一个**看起来正常、
        // 其实不可信**的时间比空着坏得多 —— 用户拿它去核对快递单上的手写时间。
        // 所以这里连一个数字都不许有（年月日时分秒里全是数字）。
        var text = PreviewTexts.Watermark(
            isCalibrated: false, new DateTimeOffset(2026, 10, 7, 6, 0, 0, TimeSpan.Zero));

        Assert.Equal("时间未校准", text);
        Assert.DoesNotMatch(@"\d", text);
    }

    [Fact]
    public void 校准过要带着时区与秒()
    {
        // ⚠️ 断的是**形状**，不是某一个固定的钟点：这个水印印的是**本地**时间，
        // 而 CI 跑在 UTC 上、开发机在 +08:00 —— 写死一个钟点的话，
        // 它只会在其中一台机器上绿。
        var text = PreviewTexts.Watermark(
            isCalibrated: true, new DateTimeOffset(2026, 10, 7, 6, 0, 0, TimeSpan.Zero));

        Assert.Matches(@"^UTC[+-]\d\d: \d{4}/\d\d/\d\d \d\d:\d\d:\d\d$", text);
    }

    [Fact]
    public void 印出来的是本地时间_不是传进去那个时刻()
    {
        // ⚠️ 这一条钉的是「转成本地」那一步没有外包给调用处：漏一次就会
        // 安安静静地印出一个 UTC 时间 —— 它和本地时间长得一模一样，没人看得出来。
        // （⚠️ 机器本身就在 UTC 时这条分辨不出来，那是这个判据的天花板。）
        var utc = new DateTimeOffset(2026, 10, 7, 6, 30, 0, TimeSpan.Zero);
        var local = utc.ToLocalTime();

        var text = PreviewTexts.Watermark(isCalibrated: true, utc);

        Assert.EndsWith(local.ToString("yyyy/MM/dd HH:mm:ss"), text, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────
    // 中央：为什么没有画面
    // ─────────────────────────────────────────────

    [Fact]
    public void 没有ffmpeg先说ffmpeg()
    {
        // ⚠️ 它排在第一位：这是**这台机器上缺了东西**，用户得去装。
        Assert.Equal(
            "本机没有 FFmpeg，无法采集，也没有画面。",
            PreviewTexts.Hint(hasFfmpeg: false, hasCamera: false, isWorking: true));
    }

    [Fact]
    public void 没有摄像头就说去设置里看()
    {
        // ⚠️ 「一个空框」与「相机坏了」长得一模一样，所以这句话必须点出去哪找。
        var text = PreviewTexts.Hint(hasFfmpeg: true, hasCamera: false, isWorking: true);

        Assert.Contains("没有找到摄像头", text);
        Assert.Contains("设置", text);
    }

    [Fact]
    public void 齐全但没开工是正常的_不许说得像坏了()
    {
        // ⚠️ 配置齐全、只是还没开工的机器，是**最常见**的那一种状态。
        // 这一句与下面那一句（工作中却一直没画面）在界面上意思完全相反，
        // 次序排错了就会把一台好机器显示成像是坏了。
        var text = PreviewTexts.Hint(hasFfmpeg: true, hasCamera: true, isWorking: false);

        Assert.Contains("还没开始工作", text);
        Assert.Contains("【开始录制】", text);
        Assert.DoesNotContain("不受影响", text);
    }

    [Fact]
    public void 工作中没有画面要说清这与录制无关()
    {
        var text = PreviewTexts.Hint(hasFfmpeg: true, hasCamera: true, isWorking: true);

        Assert.Contains("录制本身不受影响", text);
        Assert.DoesNotContain("还没开始工作", text);
    }
}
