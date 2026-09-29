using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 失败提示的中文化（需求方 2026-09-29 写死的硬要求：**提示必须是中文**）。
/// </summary>
/// <remarks>
/// ⚠️ 这一组守的**不是措辞好不好听**，而是两条硬边界：
/// ① 认得出来的原因要给**具体**的中文（不能一律「失败了」）；
/// ② 认不出来的**不许把英文塞回去** —— 那等于这条规则没生效。
/// </remarks>
public class CameraErrorTextTests
{
    // ─────────────────────────────────────────────
    // 网络摄像头
    // ─────────────────────────────────────────────

    [Fact]
    public void 认证失败说清是账号密码()
    {
        // 2026-09-29 实测到的原话。
        Assert.Equal(
            "网络摄像头的用户名或密码不对。",
            CameraErrorText.Describe(
                "rtsp://…: Server returned 401 Unauthorized (authorization failed)"));
    }

    [Fact]
    public void 路径不对与连不上是两句不同的话()
    {
        // ⚠️ 分开是有用的：「端口对了、路径错了」与「根本没有这台机器」
        // 要改的地方完全不同。合成一句「连不上」的话用户得从头试一遍。
        var notFound = CameraErrorText.Describe("method DESCRIBE failed: 404 Not Found");
        var refused = CameraErrorText.Describe("Connection refused");

        Assert.Contains("路径", notFound);
        Assert.Contains("没能连上", refused);
        Assert.NotEqual(notFound, refused);
    }

    [Fact]
    public void 把http端口当rtsp用的那句话说清是端口不对()
    {
        // ⚠️ 2026-09-29 实测：往一个 HTTP 端口发 RTSP 请求，ffmpeg 报的是
        // `Invalid data found when processing input`。
        // 它与「连不上」是两回事 —— 端口是通的、服务也在，只是说不到一块去。
        var text = CameraErrorText.Describe(
            "rtsp://h:8081/live: Invalid data found when processing input");

        Assert.Contains("不是视频流", text);
    }

    // ─────────────────────────────────────────────
    // 本机摄像头（DirectShow）
    // ─────────────────────────────────────────────

    [Fact]
    public void 本机设备被占用与找不到也各有话说()
    {
        Assert.Contains("占用", CameraErrorText.Describe("[dshow @ 0] device already in use"));
        Assert.Contains(
            "没有找到",
            CameraErrorText.Describe("Could not find video device with name [Cam]"));
    }

    // ─────────────────────────────────────────────
    // ★ 认不出来时**不许把英文塞回去**
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Some brand new ffmpeg wording we have never seen")]
    [InlineData("[dshow @ 0] weird thing happened")]
    public void 认不出来时给通用句而且一个英文字母都不带(string stderr)
    {
        // ⚠️ **这条是这一组的核心**：规则是「提示必须是中文」，
        // 而认不出的那一类最容易在实现里被写成「把原文贴回去」——
        // 那样这条规则就只在**认得出来**的时候生效，等于没生效。
        var text = CameraErrorText.Describe(stderr);

        Assert.NotEmpty(text);

        // 允许出现的东西里没有英文字母：中文 + 中文标点。
        var letters = text.Where(char.IsAsciiLetter).ToArray();
        Assert.True(
            letters.Length == 0,
            $"认不出时的话里带了英文：`{text}`（英文字母：{new string(letters)}）");
    }
}
