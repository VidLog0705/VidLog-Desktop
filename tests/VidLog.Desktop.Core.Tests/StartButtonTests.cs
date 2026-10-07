using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 顶栏那颗「开始 / 停止录制」能不能点（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// ⚠️ 抽出来的理由只有一个：**运算次序**。原来那一行写成
/// <c>(recording || hasCamera) &amp;&amp; hasWaybill</c> 也不会报错、编译照样过，
/// 而后果是「录制中把单号框清空 ⇒ 停止按钮点不动 ⇒ 停不下来」。
/// </remarks>
public class StartButtonTests
{
    [Theory]
    [InlineData(true, false, false)]   // 录制中，摄像头没了（拔了 / 崩了）
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]    // 录制中，单号框被清空了
    [InlineData(true, true, true)]
    public void 录制中一律能点(bool recording, bool hasCamera, bool hasWaybill)
    {
        // ⚠️ 这一条是**全部意义所在**：那颗按钮在录制中就是【停止录制】，
        // 而「停不下来」是这一屏里最坏的一种坏法。
        Assert.True(StartButton.Enabled(recording, hasCamera, hasWaybill));
    }

    [Theory]
    [InlineData(false, false, true)]   // 没摄像头
    [InlineData(false, true, false)]   // 没单号
    [InlineData(false, false, false)]
    public void 没在录的时候两样都要齐(bool recording, bool hasCamera, bool hasWaybill)
    {
        Assert.False(StartButton.Enabled(recording, hasCamera, hasWaybill));
    }

    [Fact]
    public void 没在录但两样齐了就能点()
    {
        Assert.True(StartButton.Enabled(recording: false, hasCamera: true, hasWaybill: true));
    }
}
