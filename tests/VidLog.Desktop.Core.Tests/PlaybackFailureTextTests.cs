using VidLog.Desktop.Core.Playback;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「这段录像播不了」到底该说哪句话（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// ⚠️ 这里最坏的一种错是「**文件明明在盘上，却说它不在**」——
/// 那会把用户指去找一个不存在的问题。而它原先长在 <c>SearchWindow</c> 里，
/// 那个工程没有测试工程。
/// </remarks>
public class PlaybackFailureTextTests
{
    private const string 路径 = @"D:\VidLog\2026-10-07\abc.mkv";

    [Fact]
    public void 没选中任何一条时只说系统那句话()
    {
        var text = PlaybackFailureText.Describe(
            systemMessage: "找不到媒体文件。", path: null, fileOnDisk: false, storedCodec: null);

        Assert.Contains("找不到媒体文件。", text);
        Assert.DoesNotContain(路径, text);
    }

    [Fact]
    public void 系统没给原话时也要有话说()
    {
        var text = PlaybackFailureText.Describe(null, path: null, false, null);

        Assert.Contains("系统解码器不支持", text);
    }

    [Fact]
    public void 文件真不在盘上时照实说不在()
    {
        // 那时系统那句话反倒是**对的** —— 别改它。
        var text = PlaybackFailureText.Describe("找不到媒体文件。", 路径, false, "H265");

        Assert.Contains(路径, text);
        Assert.Contains("不在盘上", text);
    }

    [Fact]
    public void 文件在盘上时不许说不在()
    {
        // ⚠️ 这一条是全部意义所在。
        var text = PlaybackFailureText.Describe("找不到媒体文件。", 路径, fileOnDisk: true, "H265");

        Assert.Contains("文件在盘上", text);
        Assert.Contains("这台电脑的播放组件", text);
        Assert.DoesNotContain("不在盘上", text);

        // 系统那句原话照旧留着（排查要用），但摆在后面当参考。
        Assert.Contains("找不到媒体文件。", text);
    }

    [Fact]
    public void h265要指名那一个解码器并给出装法()
    {
        var text = PlaybackFailureText.Describe(null, 路径, true, "H265");

        Assert.Contains("H.265", text);
        Assert.Contains("视频扩展", text);
    }

    [Fact]
    public void 界面上不许出现那个英文缩写()
    {
        // ⚠️ 规格点名：两个名字混用会让用户以为是**两种编码**。
        // 所以连带指路时也只说「H.265 的解码器」。
        var text = PlaybackFailureText.Describe(null, 路径, true, "H265");

        Assert.DoesNotContain("HEVC", text);
    }

    [Fact]
    public void 别的编码不要说成h265那一套()
    {
        var text = PlaybackFailureText.Describe(null, 路径, true, "H264");

        Assert.Contains("H.264", text);
        Assert.DoesNotContain("视频扩展", text);
        Assert.DoesNotContain("H.265", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("不认识的东西")]
    public void 认不出编码时不许瞎猜(string? storedCodec)
    {
        var text = PlaybackFailureText.Describe(null, 路径, true, storedCodec);

        Assert.Contains("多半是缺这个编码的解码器", text);
        Assert.DoesNotContain("H.26", text);
    }
}
