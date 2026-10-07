using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 配置向导的两条规矩：草稿怎么并进设置、哪一步取景（T27② 第 3 批块 4）。
/// </summary>
/// <remarks>
/// 这一族原先在 <c>WizardWindow.xaml.cs</c> 里，而那个工程没有测试工程 ——
/// 于是每一条都只是注释，而且**每一条的坏法都不吭声**：
/// 换种类把另一边的值忘掉（用户得重填一遍那个没有备份的地址）、
/// 网络源没测通也去取景（画面上只是没有画面）、
/// 预览的键带上了摄像头密码（只是密码一直躺在内存里）。
/// </remarks>
public class WizardPlanTests
{
    private static readonly AppSettings 存着的那份 = AppSettings.Default with
    {
        CameraDevice = "USB 摄像头",
        CameraNetworkUrl = "rtsp://admin:hunter2@192.168.101.55:8554/live",
        MicrophoneDevice = "麦克风(USB)",
        Rotation = CameraRotation.Right90,
    };

    // ─────────────────────────────────────────────
    // 草稿 → 设置
    // ─────────────────────────────────────────────

    [Fact]
    public void 草稿并出来的摄像头与界面上选的是同一路()
    {
        // ⚠️ 这条钉的是**壳里两处各用了一个**的那件事：`Pending().Camera` 与
        // `SelectedSource()` 必须是同一个值 —— 取景那两条规则读的是后者。
        // 哪天 `Build` 少搬一个字段（比如漏了 `CameraNetworkUrl`），
        // 预览会在**另一个地址**上起来，而界面上看不出来。
        foreach (var picked in new[]
                 {
                     CameraSource.None,
                     CameraSource.Local("USB 摄像头"),
                     CameraSource.Network("rtsp://admin:hunter2@192.168.101.55:8554/live"),
                 })
        {
            var next = WizardPlan.Build(存着的那份, new WizardDraft { Camera = picked });

            Assert.Equal(picked, next.Camera);
        }
    }

    [Fact]
    public void 换摄像头种类要保留另一边的值()
    {
        // ⚠️ 忘掉的话，用户在两种之间来回试一次就得**重填一遍地址** ——
        // 而那个地址（网络源的，或本机设备的名字）是他手上唯一一份。
        var 换成网络 = WizardPlan.Build(
            存着的那份, new WizardDraft { Camera = CameraSource.Network("rtsp://192.168.101.66:8554/live") });

        Assert.Equal("USB 摄像头", 换成网络.CameraDevice);          // 本机那一份还在
        Assert.Equal("rtsp://192.168.101.66:8554/live", 换成网络.CameraNetworkUrl);

        var 换回本机 = WizardPlan.Build(换成网络, new WizardDraft { Camera = CameraSource.Local("笔记本自带") });

        Assert.Equal("笔记本自带", 换回本机.CameraDevice);
        Assert.Equal("rtsp://192.168.101.66:8554/live", 换回本机.CameraNetworkUrl);  // 网络那一份也还在
    }

    [Fact]
    public void 麦克风没选时保持原值_不许把它清掉()
    {
        // ⚠️ 这一步的下拉在**枚举不出来**时是空的（ffmpeg 没找到之类）。
        // 写成 `MicrophoneDevice = draft.Microphone` 的话，向导走到最后一步会把
        // 用户原来配好的麦克风清掉 —— 之后录出来全是默片，而界面上一切正常。
        var 没选 = WizardPlan.Build(存着的那份, new WizardDraft { Microphone = null });
        Assert.Equal("麦克风(USB)", 没选.MicrophoneDevice);

        var 选了 = WizardPlan.Build(存着的那份, new WizardDraft { Microphone = "阵列麦克风" });
        Assert.Equal("阵列麦克风", 选了.MicrophoneDevice);
    }

    [Fact]
    public void 其余字段跟着草稿走_没动过的保持原值()
    {
        var next = WizardPlan.Build(存着的那份, new WizardDraft
        {
            Continuous = true,
            Recognition = true,
            Rotation = CameraRotation.Left90,
        });

        Assert.Equal(WorkMode.Continuous, next.Mode);
        Assert.True(next.CameraRecognition);
        Assert.Equal(CameraRotation.Left90, next.Rotation);

        // 草稿里没有的东西一律保持原值 —— 连同那些**不在这一向导里**的字段。
        Assert.Equal(存着的那份.SegmentMinutes, next.SegmentMinutes);
        Assert.Equal(存着的那份.PlaybackPort, next.PlaybackPort);

        var 同单停 = WizardPlan.Build(存着的那份, new WizardDraft { Continuous = false });
        Assert.Equal(WorkMode.StopOnSameWaybill, 同单停.Mode);
    }

    // ─────────────────────────────────────────────
    // 取景判定
    // ─────────────────────────────────────────────

    [Fact]
    public void 网络档没测通就不取景()
    {
        // ⚠️ 这条是**静默**规则（照图 `_18`）：那时界面上只是没有画面，
        // 看不出「是因为还没测连接」。反过来（没测通也去取景）的后果是
        // ffmpeg 对着一个连不上的地址反复超时，用户看到的是「卡住了」。
        var 网络 = CameraSource.Network("rtsp://192.168.101.55:8554/live");

        Assert.False(WizardPlan.SourceReady(网络, networkVerified: false));
        Assert.True(WizardPlan.SourceReady(网络, networkVerified: true));

        // 测没测通**只对网络那一档**有意义：本机设备不看它。
        Assert.True(WizardPlan.SourceReady(CameraSource.Local("USB 摄像头"), networkVerified: false));

        // 一个都没选中 / 网络档地址还空着 —— 都不取景。
        Assert.False(WizardPlan.SourceReady(CameraSource.None, networkVerified: true));
        Assert.False(WizardPlan.SourceReady(CameraSource.Network("   "), networkVerified: true));
    }

    [Fact]
    public void 只有摄像头步与识码步取景_识码关了也不取景()
    {
        // ⚠️ 关着识码却在取景 = 白占相机，而 dshow 上相机是**独占**的
        // （占着会挡住别的程序，用户那边的表现是「摄像头被别的软件占用了」）。
        Assert.True(WizardPlan.NeedsPreview(WizardPlan.CameraStep, recognition: true, sourceReady: true));
        Assert.False(WizardPlan.NeedsPreview(WizardPlan.CameraStep, recognition: false, sourceReady: false));

        Assert.True(WizardPlan.NeedsPreview(WizardPlan.RecognitionStep, recognition: true, sourceReady: true));
        Assert.False(WizardPlan.NeedsPreview(WizardPlan.RecognitionStep, recognition: false, sourceReady: true));

        // 摄像头那一步不受「识码开不开」影响（两件事）。
        Assert.True(WizardPlan.NeedsPreview(WizardPlan.CameraStep, recognition: false, sourceReady: true));

        // 其余每一步都不取景，**而且**两档的步号要真的是 1 与 2
        // （壳里那两个常量取的是这里的值，改这里就等于改界面）。
        Assert.Equal(1, WizardPlan.CameraStep);
        Assert.Equal(2, WizardPlan.RecognitionStep);

        foreach (var step in new[] { 0, 3, 4, 5, 6 })
        {
            Assert.False(WizardPlan.NeedsPreview(step, recognition: true, sourceReady: true));
        }
    }

    // ─────────────────────────────────────────────
    // 预览键
    // ─────────────────────────────────────────────

    [Fact]
    public void 预览键不许带地址里的密码()
    {
        // ⚠️⚠️ 这个串会长期留在内存里当一个字典键（壳里的 `_previewKey`）。
        // 用 `Address` 而不是 `Identity` 的表现是**没有任何症状** ——
        // 只是密码一直躺在那里，而且没人会想到去查这个键。
        var key = WizardPlan.PreviewKey(
            CameraSource.Network("rtsp://admin:hunter2@192.168.101.55:8554/live"),
            CameraRotation.None);

        Assert.DoesNotContain("hunter2", key, StringComparison.Ordinal);
        Assert.Contains("192.168.101.55", key, StringComparison.Ordinal);
    }

    [Fact]
    public void 换了路或换了方向都要重开预览()
    {
        // ⚠️ 方向是**烘焙进 ffmpeg 参数**的（输入侧滤镜），改了必须重开 ——
        // 不重开的话用户看着的是旧朝向的画面，而他在这一步正是靠画面判断装正没有。
        // 键少拼一段的话，上面这条就只有靠别的路径兜（或者根本不重开）。
        var one = CameraSource.Network("rtsp://192.168.101.55:8554/live");
        var two = CameraSource.Network("rtsp://192.168.101.66:8554/live");

        Assert.NotEqual(
            WizardPlan.PreviewKey(one, CameraRotation.None),
            WizardPlan.PreviewKey(one, CameraRotation.Right90));

        Assert.NotEqual(
            WizardPlan.PreviewKey(one, CameraRotation.None),
            WizardPlan.PreviewKey(two, CameraRotation.None));

        Assert.NotEqual(
            WizardPlan.PreviewKey(one, CameraRotation.None),
            WizardPlan.PreviewKey(CameraSource.Local("USB 摄像头"), CameraRotation.None));

        // 同样的输入要拼出同样的串（不然每次对账都判「变了」，预览反复重开）。
        Assert.Equal(
            WizardPlan.PreviewKey(one, CameraRotation.Right90),
            WizardPlan.PreviewKey(CameraSource.Network("rtsp://192.168.101.55:8554/live"), CameraRotation.Right90));
    }
}
