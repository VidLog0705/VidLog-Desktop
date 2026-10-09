using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 顶栏那颗「开始 / 结束」按钮此刻是哪一态、能不能点
/// （T27② 第 4 批；B1 于 2026-10-09 加第三态）。
/// </summary>
/// <remarks>
/// ⚠️ 抽出来的理由只有一个：**次序**。三条判断谁先谁后都不会报错、编译照样过，
/// 而后果分别是「录制中把单号框清空 ⇒ 停不下来」「待扫态里手动录入被【结束工作】吃掉」
/// 「待扫态没有出口」。三条都钉在这里。
/// </remarks>
public class StartButtonTests
{
    [Theory]
    [InlineData(false, false)]   // 待扫，框里空 → 【结束工作】
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void 录制中一律是停(bool working, bool hasWaybill)
    {
        // ⚠️ 这一条是**全部意义所在**：那颗按钮在录制中就是【停止录制】，
        // 而「停不下来」是这一屏里最坏的一种坏法。框里有什么都不影响它。
        Assert.Equal(StartButtonKind.StopRecording, StartButton.Kind(recording: true, working, hasWaybill));
    }

    [Fact]
    public void 待扫态框里空才是结束工作()
    {
        // B1 补的那一态：扫 VLREC 进了待扫态之后总得有办法收工。
        Assert.Equal(StartButtonKind.EndWork, StartButton.Kind(recording: false, working: true, hasWaybill: false));
    }

    [Fact]
    public void 待扫态框里有单号仍旧是开始录制()
    {
        // ⚠️ 这一条防的是「第三态把今天的行为吃掉」：待扫态里那个框**往往还留着
        // 上一段的单号**（没有一处会在收段时清它），那时按下去该是「用这个单号再录一段」
        // —— 手动录入那条路也全靠它（手打的单号一进框就该回到【开始录制】）。
        Assert.Equal(StartButtonKind.Start, StartButton.Kind(recording: false, working: true, hasWaybill: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 没在工作就是开始(bool hasWaybill)
    {
        Assert.Equal(StartButtonKind.Start, StartButton.Kind(recording: false, working: false, hasWaybill));
    }

    [Theory]
    [InlineData(StartButtonKind.EndWork)]
    [InlineData(StartButtonKind.StopRecording)]
    public void 要停的两种没摄像头也能点(StartButtonKind kind)
    {
        Assert.True(StartButton.Enabled(kind, hasCamera: false));
    }

    [Fact]
    public void 要开始就得有摄像头()
    {
        Assert.True(StartButton.Enabled(StartButtonKind.Start, hasCamera: true));
        Assert.False(StartButton.Enabled(StartButtonKind.Start, hasCamera: false));
    }

    [Fact]
    public void 没单号也能开始工作()
    {
        // ⚠️ B1 的第一半：扫码枪能进的门（扫 VLREC 进待扫态），按钮也得能进。
        // 从前这里要求框里已经有单号 ⇒ 没单号时那一颗是灰的。
        Assert.Equal(StartButtonKind.Start, StartButton.Kind(recording: false, working: false, hasWaybill: false));
        Assert.True(StartButton.Enabled(StartButtonKind.Start, hasCamera: true));
    }
}
