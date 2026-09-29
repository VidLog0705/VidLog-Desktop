using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 网络摄像头的分辨率判决与那段「后果说明」
/// （需求方 2026-09-29 裁决：先检测，够就选，不够就弹框说明后果让用户选）。
/// </summary>
public class CameraSizePolicyTests
{
    // ─────────────────────────────────────────────
    // 判决
    // ─────────────────────────────────────────────

    [Fact]
    public void 对端够就是够_不弹框()
    {
        // 裁决原话：「如果支持 1080P 就选 1080p」。
        Assert.Equal(
            CameraSizeVerdict.Enough,
            CameraSizePolicy.Judge(1920, 1080, VideoResolution.P1080));

        // 对端更大也一样够（缩下来就是）。
        Assert.Equal(
            CameraSizeVerdict.Enough,
            CameraSizePolicy.Judge(3840, 2160, VideoResolution.P720));
    }

    [Fact]
    public void 对端比选的小就要问用户()
    {
        // 裁决原话：「如果仅支持 720P 就弹选择」。
        Assert.Equal(
            CameraSizeVerdict.NeedsUpscale,
            CameraSizePolicy.Judge(1280, 720, VideoResolution.P1080));
        Assert.Equal(
            CameraSizeVerdict.NeedsUpscale,
            CameraSizePolicy.Judge(1280, 720, VideoResolution.Uhd4K));
    }

    [Fact]
    public void 读不出尺寸时不挡_按用户选的走()
    {
        // ⚠️ 「探测不出」是**正常的一种结果**，不是失败 ——
        // 混成失败会把一个能用的摄像头挡在门外。
        Assert.Equal(CameraSizeVerdict.Unknown, CameraSizePolicy.Judge(null, null, VideoResolution.P1080));
        Assert.Equal(CameraSizeVerdict.Unknown, CameraSizePolicy.Judge(0, 0, VideoResolution.P1080));
    }

    [Fact]
    public void 按高度判而不是按像素总数()
    {
        // ⚠️ 一条 2560×720 的宽幅流：像素数（184 万）比 1280×720（92 万）大一倍，
        // 但只有 720 行 —— 选 1080P 仍然是要**放大**。
        // 按像素数判的话这里会误判成「够」，于是用户拿到的是一片糊的放大画面，
        // 而界面上什么都没说。
        Assert.Equal(
            CameraSizeVerdict.NeedsUpscale,
            CameraSizePolicy.Judge(2560, 720, VideoResolution.P1080));
    }

    // ─────────────────────────────────────────────
    // 后果说明
    // ─────────────────────────────────────────────

    /// <summary>需求方给的那台：1280×720，而用户想要 1080P。</summary>
    private static string UpscaleCase() =>
        CameraSizePolicy.Describe(1280, 720, VideoResolution.P1080);

    [Fact]
    public void 后果说明要把1080P与4K各自都说到()
    {
        // ⚠️ 裁决原话要求「**说明选 1080P 和 4K 的后果**」——
        // 只讲用户当前选的那一档是不够的：他要做的选择是「仍然选大的」还是
        // 「按原尺寸」，不把各档摆出来等于没给他选择的依据。
        var text = UpscaleCase();

        Assert.Contains("1080P", text);
        Assert.Contains("4K", text);

        // 两档各自的代价都要有（像素倍数），不是只列个名字。
        Assert.Contains("倍", text);
    }

    [Fact]
    public void 后果说明要把不会更清晰这句话说白()
    {
        // ⚠️ 这是整段话的**唯一重点**：放大只让小图变大，画质不会变好。
        // 不说明白的话，用户会以为「选大的总是更好」—— 那就白弹这个框了。
        Assert.Contains("不会更清晰", UpscaleCase());
        Assert.Contains("变大", UpscaleCase());
    }

    [Fact]
    public void 后果说明要给一条能选的出路()
    {
        // 只列代价不给出路，用户只能硬着头皮选大的。
        Assert.Contains("720P", UpscaleCase());
        Assert.Contains("建议", UpscaleCase());
    }

    [Fact]
    public void 后果说明里不许有markdown标记()
    {
        // ⚠️ 这段字**原样出现在界面上**（WPF 的 TextBlock 不认 markdown），
        // `**加粗**` 只会显示成字面的星号。
        var text = UpscaleCase();

        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("`", text);
        Assert.DoesNotContain("](", text);
    }

    [Fact]
    public void 对端比选的大时不许说反话()
    {
        // ⚠️ 判决为 Enough 的情况**不该**弹框；真被叫到时也**不许**说出
        // 「放大的代价：画面不会更清晰」—— 那是**一句反话**
        // （对端 4K、用户选 720P 时劝他别放大）。
        // 这一条守的是「调用方判错时会露出什么」。
        var text = CameraSizePolicy.Describe(3840, 2160, VideoResolution.P720);

        Assert.DoesNotContain("不会更清晰", text);
        Assert.DoesNotContain("放大的代价", text);

        // 而且要说一句**对**的话。
        Assert.Contains("够", text);
    }
}
