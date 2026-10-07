using VidLog.Desktop.Core.Configuration;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 存设置时要做的两处纯判断（T27② 第 3 批块 3）。
/// </summary>
/// <remarks>
/// <para>
/// 这一族原先在 <c>AppHost.SaveSettingsAsync</c> 里，而那个工程没有测试工程 ——
/// 于是「拨开要盖章」只是条注释，实际那一行**从来没盖过章**：
/// 它拿自己的值喂给自己（见 <see cref="SettingsSavePlan.WithAutoUploadStamp"/>）。
/// 所以下面第一条用例特意**照着表单的写法**造输入
/// （<c>current.Cloud with { AutoUpload = true }</c>），不是随手 new 一个。
/// </para>
/// <para>
/// ⚠️ 其余几条钉的是**另外两种坏法**，它们比「不盖章」更不显眼：
/// 每次存都重盖（用户改一格并发数，章就往后跳，中间录的再也不会被自动传）、
/// 以及把那枚章丢掉（跨重启再也不自动传旧账）。
/// </para>
/// </remarks>
public class SettingsSavePlanTests
{
    private static readonly DateTimeOffset 拨开这一刻 =
        new(2026, 10, 7, 14, 0, 0, TimeSpan.FromHours(8));

    private static readonly DateTimeOffset 上一次那枚 =
        new(2026, 9, 1, 9, 30, 0, TimeSpan.FromHours(8));

    /// <summary>表单那条路造出来的设置：从 `current.Cloud` 起改（不含 AutoUploadSince）。</summary>
    private static AppSettings 表单存成这样(AppSettings current, bool autoUpload, int parallel = 2) =>
        current with
        {
            Cloud = current.Cloud with { AutoUpload = autoUpload, ParallelUploads = parallel },
        };

    [Fact]
    public void 拨开自动上传要盖章()
    {
        var current = AppSettings.Default;

        var next = SettingsSavePlan.WithAutoUploadStamp(
            current, 表单存成这样(current, autoUpload: true), 拨开这一刻);

        // ⚠️ 这就是原先坏掉的那一条：从前是 null，服务只能按「库里全都要传」算。
        Assert.True(next.Cloud.AutoUpload);
        Assert.Equal(拨开这一刻, next.Cloud.AutoUploadSince);
    }

    [Fact]
    public void 拨开之外的那一次存不能把章往后挪()
    {
        // ⚠️ 开头就是这么坏法的：每存一次都重盖 ⇒ 用户改一下并发数，
        // 章就跳到那一刻，而**这中间录的再也不会被自动传** ——
        // 界面上一点提示都没有，事后只会被当成「传漏了几段」。
        var current = AppSettings.Default with
        {
            Cloud = CloudUploadSettings.Default with { AutoUpload = true, AutoUploadSince = 上一次那枚 },
        };

        var next = SettingsSavePlan.WithAutoUploadStamp(
            current, 表单存成这样(current, autoUpload: true, parallel: 5), 拨开这一刻);

        Assert.Equal(上一次那枚, next.Cloud.AutoUploadSince);

        // 用户改的那一格**得留下**（别为了保时间戳把整块 Cloud 换回旧的）。
        Assert.Equal(5, next.Cloud.ParallelUploads);
    }

    [Fact]
    public void 关掉时不动那枚章()
    {
        var current = AppSettings.Default with
        {
            Cloud = CloudUploadSettings.Default with { AutoUpload = true, AutoUploadSince = 上一次那枚 },
        };

        var next = SettingsSavePlan.WithAutoUploadStamp(
            current, 表单存成这样(current, autoUpload: false), 拨开这一刻);

        Assert.False(next.Cloud.AutoUpload);
        Assert.Equal(上一次那枚, next.Cloud.AutoUploadSince);
    }

    [Fact]
    public void 关了再开_盖的是重开那一刻()
    {
        var open = AppSettings.Default with
        {
            Cloud = CloudUploadSettings.Default with { AutoUpload = true, AutoUploadSince = 上一次那枚 },
        };

        // 先关
        var closed = SettingsSavePlan.WithAutoUploadStamp(
            open, 表单存成这样(open, autoUpload: false), 拨开这一刻);

        // 再开（同一个开关来回拨，是用户真会做的事）
        var reopened = SettingsSavePlan.WithAutoUploadStamp(
            closed, 表单存成这样(closed, autoUpload: true), 拨开这一刻);

        // ⚠️ 重开时留着上次那枚的话，等于「只传上一次开后录的」——
        // 这一头更保守，但会让用户觉得「我明明刚打开，怎么没传」。
        // 规矩是**重开那一刻**（设计图 `_45`：仅此开关开启后新开始录制的会上传）。
        Assert.Equal(拨开这一刻, reopened.Cloud.AutoUploadSince);
    }

    [Fact]
    public void 从头new一份Cloud也丢不掉那枚章()
    {
        // ⚠️ 表单今天是 `current.Cloud with`，所以章是**这样**带下来的；
        // 万一哪天有人改成从头 new 一份 Cloud，那一支就得靠 Core 兜住 ——
        // 丢掉的表现是「跨重启不再自动传旧账」，而界面上看不出来。
        var current = AppSettings.Default with
        {
            Cloud = CloudUploadSettings.Default with { AutoUpload = true, AutoUploadSince = 上一次那枚 },
        };

        var 从头new的 = current with
        {
            Cloud = CloudUploadSettings.Default with { AutoUpload = true },
        };

        var next = SettingsSavePlan.WithAutoUploadStamp(current, 从头new的, 拨开这一刻);

        Assert.Equal(上一次那枚, next.Cloud.AutoUploadSince);
    }

    [Fact]
    public void 音轨两项任一变了都要重新枚举设备()
    {
        // ⚠️ 枚举一次要起一个 ffmpeg 进程、约 0.3 秒，而存设置是个高频动作；
        // 反过来漏一次 = 用户换了麦克风却还拿老设备录（或者关掉声音再重开时挑不到设备）。
        var current = AppSettings.Default with { RecordAudio = true, MicrophoneDevice = "mic-a" };

        Assert.False(SettingsSavePlan.AudioChanged(current, current));

        Assert.True(SettingsSavePlan.AudioChanged(current, current with { RecordAudio = false }));
        Assert.True(SettingsSavePlan.AudioChanged(current, current with { MicrophoneDevice = "mic-b" }));

        // 关掉声音时设备名多半是 null ⇒ 这两格要一起算，不能只看「开不开」。
        Assert.True(SettingsSavePlan.AudioChanged(
            current with { RecordAudio = false, MicrophoneDevice = null },
            current with { RecordAudio = true, MicrophoneDevice = "mic-a" }));
    }
}
