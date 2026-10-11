using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 相机档位表（<c>ffmpeg -f dshow -list_options true</c>）的解析与挑选。
/// </summary>
/// <remarks>
/// ⚠️ 夹具是**本机那台相机真实吐出来的**（2026-10-11 实测，<c>PC CAMERA-</c>），
/// 不是编的 —— 这个解析器的全部风险都在「真实的输出长什么样」上，
/// 拿一份想象的输出测出来的绿**什么也证明不了**。
/// </remarks>
public sealed class DshowCapabilitiesTests
{
    /// <summary>本机 <c>PC CAMERA-</c> 的真实输出（原样，含重复行与色彩后缀）。</summary>
    private const string RealOutput =
        """
        [in#0 @ 000001ef17c70cc0] DirectShow video device options (from video devices)
        [in#0 @ 000001ef17c70cc0]  Pin "捕获" (alternative pin name "0")
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=1920x1080 fps=30 max s=1920x1080 fps=30
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=1920x1080 fps=30 max s=1920x1080 fps=30 (pc, bt470bg/bt709/unknown, center)
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=320x240 fps=30 max s=320x240 fps=30
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=320x240 fps=30 max s=320x240 fps=30 (pc, bt470bg/bt709/unknown, center)
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=640x480 fps=30 max s=640x480 fps=30
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=640x480 fps=30 max s=640x480 fps=30 (pc, bt470bg/bt709/unknown, center)
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=800x600 fps=30 max s=800x600 fps=30
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=800x600 fps=30 max s=800x600 fps=30 (pc, bt470bg/bt709/unknown, center)
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=1280x720 fps=30 max s=1280x720 fps=30
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=1280x720 fps=30 max s=1280x720 fps=30 (pc, bt470bg/bt709/unknown, center)
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=1600x1200 fps=30 max s=1600x1200 fps=30
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=1600x1200 fps=30 max s=1600x1200 fps=30 (pc, bt470bg/bt709/unknown, center)
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=2048x1536 fps=30 max s=2048x1536 fps=30
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=2048x1536 fps=30 max s=2048x1536 fps=30 (pc, bt470bg/bt709/unknown, center)
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=2560x1440 fps=30 max s=2560x1440 fps=30
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=2592x1944 fps=15 max s=2592x1944 fps=15
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=3840x2160 fps=30 max s=3840x2160 fps=30
        [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=4000x3000 fps=15 max s=4000x3000 fps=15
        [in#0 @ 000001ef17c70cc0]   pixel_format=yuyv422  min s=640x480 fps=30 max s=640x480 fps=30
        [in#0 @ 000001ef17c70cc0]   pixel_format=yuyv422  min s=640x480 fps=30 max s=640x480 fps=30 (tv, bt470bg/bt709/unknown, topleft)
        [in#0 @ 000001ef17c70cc0]   pixel_format=yuyv422  min s=320x240 fps=30 max s=320x240 fps=30
        [in#0 @ 000001ef17c70cc0]   pixel_format=yuyv422  min s=320x240 fps=30 max s=320x240 fps=30 (tv, bt470bg/bt709/unknown, topleft)
        Error opening input file video=PC CAMERA-.
        """;

    /// <summary>
    /// 真实的表解析出 **11 档**，而不是 24 行。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这个数是**算出来的**（写测试前逐行核过）：mjpeg 11 档 + yuyv422 2 档，
    /// 而 yuyv422 那两档的尺寸/帧率与 mjpeg 的**完全一样** ⇒ 去重后仍是 11。
    /// 不去重的话是 22（每一档都带一行 <c>(pc, …)</c> 后缀的孪生行）。
    /// </remarks>
    [Fact]
    public void 解析真实输出_是去重后的11档()
    {
        var modes = DshowCapabilities.Parse(RealOutput);

        Assert.Equal(11, modes.Count);
        Assert.All(modes, mode => Assert.True(mode.IsExact, $"{mode} 应当是离散档"));
    }

    [Fact]
    public void 解析真实输出_认得4K与720P()
    {
        var modes = DshowCapabilities.Parse(RealOutput);

        Assert.True(DshowCapabilities.Supports(modes, 3840, 2160, 30));
        Assert.True(DshowCapabilities.Supports(modes, 1280, 720, 30));

        // ⚠️ 480P 这一档**这台相机没有** —— 这正是要能判断出来的那件事：
        // 从前的做法是拿它去真开一次相机，失败了再试下一档。
        Assert.False(DshowCapabilities.Supports(modes, 854, 480, 30));
    }

    [Fact]
    public void 解析真实输出_帧率不够的档不算支持()
    {
        var modes = DshowCapabilities.Parse(RealOutput);

        // 2592×1944 与 4000×3000 只有 15 fps，而规格要求恒 30。
        Assert.False(DshowCapabilities.Supports(modes, 2592, 1944, 30));
        Assert.True(DshowCapabilities.Supports(modes, 2592, 1944, 15));
    }

    [Fact]
    public void 区间型的模式_中间尺寸也算支持()
    {
        // dshow 上多数设备给的是离散档，但区间是合法形状（一行 min≠max）。
        var modes = DshowCapabilities.Parse(
            "[in#0 @ 0]   vcodec=mjpeg  min s=320x240 fps=30 max s=1920x1080 fps=30");

        var mode = Assert.Single(modes);
        Assert.False(mode.IsExact);
        Assert.True(DshowCapabilities.Supports(modes, 640, 480, 30));
        Assert.True(DshowCapabilities.Supports(modes, 1920, 1080, 30));
        Assert.False(DshowCapabilities.Supports(modes, 2560, 1440, 30));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error opening input file video=PC CAMERA-.")]
    [InlineData("[in#0 @ 0] DirectShow video device options (from video devices)")]
    public void 解析不出东西就是空表(string output) =>
        Assert.Empty(DshowCapabilities.Parse(output));

    [Fact]
    public void 挑最近的档_目标在表里就取它自己()
    {
        var modes = DshowCapabilities.Parse(RealOutput);

        var picked = DshowCapabilities.Nearest(modes, 1920, 1080, 30);

        Assert.NotNull(picked);
        Assert.Equal((1920, 1080), (picked!.Value.MaxWidth, picked.Value.MaxHeight));
    }

    [Fact]
    public void 挑最近的档_目标不在表里时取离得最近的那个()
    {
        var modes = DshowCapabilities.Parse(RealOutput);

        // 目标 1600×1200 在表里；目标 1440×1080 不在，最近的是 1600×1200。
        var picked = DshowCapabilities.Nearest(modes, 1440, 1080, 30);

        Assert.Equal((1600, 1200), (picked!.Value.MaxWidth, picked.Value.MaxHeight));
    }

    [Fact]
    public void 挑最近的档_帧率不够时退到按尺寸挑而不是返回空()
    {
        // 表里只有 1080P@15。规格要 1080P@30 —— 帧率那一支挑不出东西，
        // 但**不能因此返回 null**：那一档尺寸本身是对的，
        // 「帧率其实不够」该留给真开一次相机去证伪，而不是在这里替它下结论。
        var modes = DshowCapabilities.Parse(
            "[in#0 @ 0]   vcodec=mjpeg  min s=1920x1080 fps=15 max s=1920x1080 fps=30");

        var picked = DshowCapabilities.Nearest(modes, 1920, 1080, 30);

        Assert.Equal((1920, 1080), (picked!.Value.MaxWidth, picked.Value.MaxHeight));
    }

    [Fact]
    public void 挑最近的档_宁可尺寸远一点也要帧率够()
    {
        // 目标 1080P@30：1080P@15 在尺寸上是**零距离**，但它跑不了 30
        // （我们从命令行钉的就是 30）—— 挑中它等于挑了一个打开就失败的档。
        // 所以要退到 720P@30：尺寸远了一档，但**录得出来**。
        var modes = new[]
        {
            new CameraMode(1920, 1080, 15, 1920, 1080, 15),
            new CameraMode(1280, 720, 30, 1280, 720, 30),
        };

        var picked = DshowCapabilities.Nearest(modes, 1920, 1080, 30);

        Assert.Equal((1280, 720), (picked!.Value.MaxWidth, picked.Value.MaxHeight));
    }

    [Fact]
    public void 挑最近的档_一样近的时候取大的()
    {
        var modes = new[]
        {
            new CameraMode(800, 1000, 30, 800, 1000, 30),
            new CameraMode(1200, 1000, 30, 1200, 1000, 30),
        };

        // 目标 1000×1000：两个的 |Δ宽| 都是 200 ⇒ 打平。取大的那个。
        var picked = DshowCapabilities.Nearest(modes, 1000, 1000, 30);

        Assert.Equal(1200, picked!.Value.MaxWidth);
    }

    [Fact]
    public void 挑最近的档_空表给null() =>
        Assert.Null(DshowCapabilities.Nearest([], 1920, 1080, 30));

    [Fact]
    public void 枚举命令_是list_options那一套()
    {
        var arguments = DshowCapabilities.BuildArguments("PC CAMERA-");

        Assert.Equal(
            ["-hide_banner", "-f", "dshow", "-list_options", "true", "-i", "video=PC CAMERA-"],
            arguments);
    }

    [Fact]
    public async Task 网络源不问档位表()
    {
        // 网络源没有「dshow 档位表」这回事（尺寸在输出侧才定），
        // 而这里**不该起 ffmpeg** —— 路径写成不存在也不会报错就是证据。
        var capabilities = new DshowCameraCapabilities(@"Z:\没有这个 ffmpeg.exe");

        var modes = await capabilities.ListAsync(
            CameraSource.Network("rtsp://192.168.101.66:8554/live"));

        Assert.Empty(modes);
    }

    [Fact]
    public async Task 设备没配也不问档位表()
    {
        var capabilities = new DshowCameraCapabilities(@"Z:\没有这个 ffmpeg.exe");

        Assert.Empty(await capabilities.ListAsync(CameraSource.None));
    }
}
