using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 设置页那些格子 → 一份 <c>AppSettings</c>（T27② 第 3 批从 <c>SettingsWindow</c> 搬进 Core）。
/// </summary>
/// <remarks>
/// ⚠️ 这一段原先有一百八十行，全长在没有测试工程的 App 里。它的后果不是「这一项不对」：
/// <see cref="AppSettings.IsPlausible"/> 判的是**整份设置**，一处放行了 Core 判不过的值，
/// 下次启动**整体回落到默认值**，而用户只碰过这一页、还看不到任何提示。
/// </remarks>
public class SettingsFormTests
{
    private static readonly AppSettings Current = new();

    private static SettingsFormInput Input(
        string? segment = "3",
        string? port = "8720",
        string? duplicateDays = "7",
        string? idleMinutes = null,
        string? prerecordTag = null,
        string? logRetainDays = null,
        string? cloudParallel = null)
    {
        var input = new SettingsFormInput
        {
            SegmentMinutes = segment,
            PlaybackPort = port,
            DuplicateCheckDays = duplicateDays,
            IdleMinutes = idleMinutes,
            PrerecordTag = prerecordTag,
            LogRetainDays = logRetainDays,
            CloudParallelText = cloudParallel,
        };

        return input;
    }

    private static (AppSettings? Next, string Problem) Build(
        SettingsFormInput input,
        AppSettings? current = null,
        (string Folder, string? ReservedText)[]? disks = null,
        (string Folder, string? ReservedText)[]? backups = null) =>
        SettingsForm.Build(input, current ?? Current, disks ?? [], backups ?? []);

    // ─────────────────────────────────────────────
    // 三条数值校验
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("三分钟")]
    [InlineData("3.5")]
    public void 分段时长越界就不保存_并说是哪一项(string text)
    {
        var (next, problem) = Build(Input(segment: text));

        Assert.Null(next);
        Assert.Contains("分段时长", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10")]
    [InlineData("1")]
    public void 分段时长的两端是收的(string text)
    {
        // ⚠️ 边界两边都要试：`is < 1 or > 10` 写成 `<= 1` 之类只有端点会红。
        Assert.Equal(int.Parse(text), Build(Input(segment: text)).Next!.SegmentMinutes);
    }

    [Theory]
    [InlineData("1023")]
    [InlineData("65536")]
    [InlineData("")]
    [InlineData("八千")]
    public void 端口越界就不保存(string text)
    {
        var (next, problem) = Build(Input(port: text));

        Assert.Null(next);
        Assert.Contains("端口", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1024")]
    [InlineData("65535")]
    public void 端口的两端是收的(string text)
    {
        Assert.Equal(int.Parse(text), Build(Input(port: text)).Next!.PlaybackPort);
    }

    [Fact]
    public void 重复单号检测收零_因为界面上写着零就是关闭()
    {
        // ⚠️ 界面上明写「0 = 关闭」，这里就**必须**收 0 —— 否则那句话是空话。
        Assert.Equal(0, Build(Input(duplicateDays: "0")).Next!.DuplicateCheckDays);
        Assert.Equal(365, Build(Input(duplicateDays: "365")).Next!.DuplicateCheckDays);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("366")]
    [InlineData("")]
    public void 重复单号检测越界就不保存(string text)
    {
        var (next, problem) = Build(Input(duplicateDays: text));

        Assert.Null(next);
        Assert.Contains("重复单号", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 校验的先后顺序照旧_一次只说第一句()
    {
        // ⚠️ 搬的时候**连顺序一起搬**了：用户一次只看到第一句。
        // 这条钉住「先分段、再端口、再重复天数、最后才是磁盘」——
        // 顺序变了用户会先看到一条跟他刚改的那一格无关的话。
        //
        // ⚠️ 两个接缝都要钉：只钉第一个的话，把端口整块挪到分段前面照样绿。
        var allThree = Build(Input(segment: "99", port: "1", duplicateDays: "-1"));

        Assert.Contains("分段时长", allThree.Problem, StringComparison.Ordinal);

        var lastTwo = Build(Input(port: "1", duplicateDays: "-1"));

        Assert.Contains("端口", lastTwo.Problem, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────
    // 磁盘表（T29 那两个上限）
    // ─────────────────────────────────────────────

    [Fact]
    public void 预留空间留空是合法的_算作没填()
    {
        var (next, problem) = Build(Input(), disks: [(@"D:\rec", null), (@"E:\rec", "  ")]);

        Assert.Equal(string.Empty, problem);
        Assert.Equal([new DiskSlot(@"D:\rec"), new DiskSlot(@"E:\rec")], next!.SaveDisks);
    }

    [Fact]
    public void 预留空间填了数就记下来()
    {
        var next = Build(Input(), disks: [(@"D:\rec", " 25 ")]).Next!;

        Assert.Equal([new DiskSlot(@"D:\rec", 25)], next.SaveDisks);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1000001")]
    [InlineData("二十")]
    [InlineData("1.5")]
    public void 预留空间读不懂就不保存_不许静默换成默认值(string text)
    {
        // ⚠️ 静默换默认值的话用户会以为他填的生效了 —— 而预留空间直接决定
        // 「什么时候换下一块盘」，猜错一个数就是白写满一块盘或者白换一次盘。
        var (next, problem) = Build(Input(), disks: [(@"D:\rec", text)]);

        Assert.Null(next);
        Assert.Contains(@"D:\rec", problem, StringComparison.Ordinal);
        Assert.Contains("1000000", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 预留空间的上限读的是Core的常量_不是界面上写的那个数()
    {
        // ⚠️ 这条替掉了 T29 那条**读源码文本**的绊线 —— 它自己写着天花板
        // 「挡不住常量引对了但比较方向写反」，而根治法是搬进 Core 用真单测盖。
        Assert.NotNull(Build(Input(), disks: [(@"D:\rec", AppSettings.MaxReservedGb.ToString())]).Next);
        Assert.Null(Build(Input(), disks: [(@"D:\rec", (AppSettings.MaxReservedGb + 1).ToString())]).Next);
    }

    [Fact]
    public void 磁盘最多几个也是Core的那个数()
    {
        var atLimit = new (string, string?)[AppSettings.MaxDiskSlots];
        for (var i = 0; i < atLimit.Length; i++)
        {
            atLimit[i] = ($@"D:\rec{i}", null);
        }

        Assert.NotNull(Build(Input(), disks: atLimit).Next);

        var overLimit = new (string, string?)[AppSettings.MaxDiskSlots + 1];
        for (var i = 0; i < overLimit.Length; i++)
        {
            overLimit[i] = ($@"D:\rec{i}", null);
        }

        var (next, problem) = Build(Input(), disks: overLimit);

        Assert.Null(next);
        Assert.Contains("录像保存位置", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 两张表各说自己那一句()
    {
        var (_, problem) = Build(Input(), backups: [(@"E:\bak", "99999999")]);

        Assert.Contains("录像备份位置", problem, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────
    // 「认不出来就保持原值」
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Nuclear")]
    public void 认不出来的下拉保持原值_不许静默换成默认(string? tag)
    {
        // ⚠️ 这条是这一块里最贵的。拿 `Mode` 举例：默认值是 `StopOnSameWaybill`，
        // 而一台设成「不间断录」的机器认不出来就往回落 = **悄悄少录很多段**。
        var current = Current with { Mode = WorkMode.Continuous };

        var (next, _) = Build(new SettingsFormInput
        {
            SegmentMinutes = "3",
            PlaybackPort = "8720",
            DuplicateCheckDays = "7",
            ModeTag = tag,
        }, current);

        Assert.Equal(WorkMode.Continuous, next!.Mode);

        // 边界另一头：认得出的 tag 要真的换过去。
        Assert.Equal(
            WorkMode.StopOnSameWaybill,
            Build(new SettingsFormInput
            {
                SegmentMinutes = "3",
                PlaybackPort = "8720",
                DuplicateCheckDays = "7",
                ModeTag = nameof(WorkMode.StopOnSameWaybill),
            }, current).Next!.Mode);
    }

    [Fact]
    public void 日志级别认不出来退的是Info_不是原值()
    {
        // ⚠️ 与上面那条**方向相反**，是刻意的：这一格四项写死，
        // 读不出 tag 就是界面坏了，而 Info 是那个「什么都记」的安全档。
        // 退回原值的话，一台设成 Warn 的机器读不出 tag 就继续只记警告，
        // 下次出事翻日志什么都没有。
        var current = Current with { LogMinLevel = LogLevel.Warn };

        Assert.Equal(LogLevel.Info, Build(Input(), current).Next!.LogMinLevel);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("366")]
    [InlineData("-5")]
    public void 日志保留天数越界退回原值(string text)
    {
        // ⚠️ 写个 0 进去 = 所有日志当场被清掉，而用户只是想改一个数。
        var current = Current with { LogRetainDays = 14 };

        Assert.Equal(14, Build(Input(logRetainDays: text), current).Next!.LogRetainDays);
    }

    [Fact]
    public void 日志保留天数认得出就换过去()
    {
        Assert.Equal(30, Build(Input(logRetainDays: "30")).Next!.LogRetainDays);
    }

    [Fact]
    public void 自定义分钟数越界是夹住_不是保持原值()
    {
        // ⚠️ 这一格与别处相反，是刻意的：用户**正在**这一格里敲数，
        // 夹到边界是他看得见的（框里就写着那个数）。
        var current = Current with { IdleReminderMinutes = 3 };

        Assert.Equal(24 * 60, Build(Input(idleMinutes: "99999"), current).Next!.IdleReminderMinutes);
        Assert.Equal(1, Build(Input(idleMinutes: "0"), current).Next!.IdleReminderMinutes);

        // 空着/认不出才保持原值（用户先填 7 又切走，那 7 不该丢）。
        Assert.Equal(3, Build(Input(idleMinutes: "  "), current).Next!.IdleReminderMinutes);
    }

    [Fact]
    public void 预录缓冲与同时上传数越界都保持原值_不夹()
    {
        // ⚠️ 与上面那一格相反：这两个都是**下拉**，用户没在敲数，
        // 静默改一档的后果不是慢一点 —— 预录猜错要么白丢几秒画面、
        // 要么白占一份磁盘；上传数猜错是整库被网盘风控限流。
        var current = Current with
        {
            PrerecordSeconds = 5,
            Cloud = Current.Cloud with { ParallelUploads = 2 },
        };

        var next = Build(Input(prerecordTag: "999", cloudParallel: "999"), current).Next!;

        Assert.Equal(5, next.PrerecordSeconds);
        Assert.Equal(2, next.Cloud.ParallelUploads);
    }

    // ─────────────────────────────────────────────
    // 「null 不许把记着的东西抹掉」
    // ─────────────────────────────────────────────

    [Fact]
    public void 下拉是空的时候不许把记着的设备名抹成null()
    {
        // ⚠️ 用着网络摄像头时摄像头下拉是禁用且空的、麦克风关着时那一栏根本没被填过 ——
        // 直接写 null 的话，用户哪天切回来就得重选一遍。
        var current = Current with { CameraDevice = "Logitech C920", MicrophoneDevice = "麦克风 (USB)" };

        var next = Build(Input(), current).Next!;

        Assert.Equal("Logitech C920", next.CameraDevice);
        Assert.Equal("麦克风 (USB)", next.MicrophoneDevice);
    }

    [Fact]
    public void 网盘应用名空白保持原值_有值才去空格()
    {
        var current = Current with { Cloud = Current.Cloud with { AppName = "老应用名" } };

        Assert.Equal("老应用名", Build(Input(), current).Next!.Cloud.AppName);

        // 只有空白也算没填 —— 否则会存进一个全是空格的「应用名」。
        Assert.Equal(
            "老应用名",
            Build(new SettingsFormInput
            {
                SegmentMinutes = "3",
                PlaybackPort = "8720",
                DuplicateCheckDays = "7",
                CloudAppName = "   ",
            }, current).Next!.Cloud.AppName);

        Assert.Equal(
            "新应用名",
            Build(new SettingsFormInput
            {
                SegmentMinutes = "3",
                PlaybackPort = "8720",
                DuplicateCheckDays = "7",
                CloudAppName = "  新应用名  ",
            }, current).Next!.Cloud.AppName);
    }

    [Fact]
    public void 补传起始日期没选就保持原值()
    {
        var since = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var current = Current with { Cloud = Current.Cloud with { BackfillFrom = since } };

        Assert.Equal(since, Build(Input(), current).Next!.Cloud.BackfillFrom);
    }

    [Fact]
    public void 老的那个归档目录要清掉_否则删掉的最后一行会原地复活()
    {
        // ⚠️ 留着它的话，`AppSettings.ArchiveDirectories` 会在备份表被清空时回落到它。
        var current = Current with { ArchiveDirectory = @"Z:\old" };

        Assert.Null(Build(Input(), current).Next!.ArchiveDirectory);
    }

    // ─────────────────────────────────────────────
    // 那一条不变式
    // ─────────────────────────────────────────────

    /// <summary>
    /// <b><see cref="SettingsForm.Build"/> 说「成」的时候，产物必须过
    /// <see cref="AppSettings.IsPlausible"/>。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 这是这一块**唯一真正承重**的一条，也是 T29 那条读源码文本的绊线
    /// 想表达而表达不了的东西。两边一旦对不上，后果不是「这一项不生效」：
    /// 整份设置下次启动**整体回落默认值**，保留期、云端、关窗行为一起被打回原样，
    /// 而用户只碰过设置页里的某一格。
    /// </para>
    /// <para>
    /// ⚠️ 说的是**单向**含蕴（成了 ⇒ 必须能过），不是「必须过」：校验不过时
    /// <c>Build</c> 本来就该返回 <see langword="null"/>。
    /// </para>
    /// </remarks>
    [Fact]
    public void 说成的时候_产物一定过IsPlausible()
    {
        var cases = new (string Name, SettingsFormInput Input, (string, string?)[] Disks)[]
        {
            ("每一格都顶到上限",
             new SettingsFormInput
             {
                 SegmentMinutes = "10", PlaybackPort = "65535", DuplicateCheckDays = "365",
                 IdleMinutes = "1440", PrerecordTag = "30", LogRetainDays = "365",
                 CloudParallelText = "8",
                 StationRoleTag = nameof(StationRole.RecordAndUpload),
                 ModeTag = nameof(WorkMode.Continuous),
                 CodecTag = nameof(VideoCodec.H265),
                 ResolutionTag = nameof(VideoResolution.P1080),
                 IdleReminderTag = nameof(IdleReminderOption.Custom),
                 DurationFallbackTag = nameof(DurationFallbackOption.Six),
                 CloseActionTag = nameof(CloseWindowAction.MinimizeToTray),
                 ArchiveBackendTag = nameof(ArchiveBackendKind.Nas),
                 CloudBackfillScopeTag = nameof(BackfillScope.All),
                 LogLevelTag = nameof(LogLevel.Warn),
                 RunAtStartup = true, CheckForUpdates = true, RecordAudio = true,
                 CloudAutoUpload = true, CloudCompareAndBackfill = true,
                 CameraDevice = "cam", MicrophoneDevice = "mic",
                 CloudBackfillFrom = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
                 CloudAppName = " 网盘 ",
                 ArchivedOutbound = new RetentionInput(-1, "3650天"),
                 ArchivedReturn = new RetentionInput(-1, "不保留"),
                 UnarchivedOutbound = new RetentionInput(-1, "30 天"),
                 UnarchivedReturn = new RetentionInput(-1, "全部保留"),
             },
             [(@"D:\rec", AppSettings.MaxReservedGb.ToString())]),

            ("每一格都顶到下限",
             new SettingsFormInput
             {
                 SegmentMinutes = "1", PlaybackPort = "1024", DuplicateCheckDays = "0",
                 IdleMinutes = "1", PrerecordTag = "0", LogRetainDays = "1",
                 CloudParallelText = "1",
                 ArchivedOutbound = new RetentionInput(-1, "不保留"),
                 ArchivedReturn = new RetentionInput(-1, "0天"),
                 UnarchivedOutbound = new RetentionInput(-1, "1天"),
                 UnarchivedReturn = new RetentionInput(-1, "0 天"),
             },
             [(@"D:\rec", "0")]),

            // ⚠️ 越界的输入也照样走一遍：它们该被拒（返回 null），
            // 但**万一**哪一项被放行了，产物也得是自洽的。
            //
            // ⚠️ 保留期那四格**刻意只喂「认不出来的写法」**（`-1` / 长到解析不出来 / `??`），
            // 不喂「解析得出来但过大」的数（`9999天`）—— 那是一个**已实证的缺陷**，
            // 单独钉在下面那条 `已知缺陷` 里，不混在这一条的用例表里假装它不存在。
            ("全是不合法的值",
             new SettingsFormInput
             {
                 SegmentMinutes = "999", PlaybackPort = "-1", DuplicateCheckDays = "99999",
                 IdleMinutes = "999999", PrerecordTag = "999", LogRetainDays = "99999",
                 CloudParallelText = "99", LogLevelTag = "没有这个",
                 ArchivedOutbound = new RetentionInput(-1, "999999999999999999999"),
                 ArchivedReturn = new RetentionInput(-1, "??"),
                 UnarchivedOutbound = new RetentionInput(-1, ""),
                 UnarchivedReturn = new RetentionInput(-1, ""),
             },
             [(@"D:\rec", "99999999"), (@"E:\rec2", "-5")]),

            ("全是空的",
             new SettingsFormInput(),
             []),
        };

        foreach (var (name, input, disks) in cases)
        {
            var (next, problem) = Build(input, disks: disks);

            if (next is null)
            {
                Assert.NotEqual(string.Empty, problem);
                continue;
            }

            Assert.True(
                AppSettings.IsPlausible(next),
                $"「{name}」这一组：Build 说能存，产物却过不了 IsPlausible。"
                + "两边一对不上，用户下次启动会发现**别的**设置一起被打回默认值。");
        }

        // ⚠️ 上面那张表里「全是不合法的值」那一组**每个字段都坏** —— 它钉不住
        // 「拆掉某一处守卫」：守卫拆一处，别的守卫照样拦下来。所以下面再走一组
        // **每次只坏一格**的：只有这样才能证明每一条上限都真的在承重。
        var good = new SettingsFormInput
        {
            SegmentMinutes = "3", PlaybackPort = "8720", DuplicateCheckDays = "7",
            IdleMinutes = "30", PrerecordTag = "5", LogRetainDays = "30",
            CloudParallelText = "3",
            ArchivedOutbound = new RetentionInput(-1, "7 天"),
            ArchivedReturn = new RetentionInput(-1, "不保留"),
            UnarchivedOutbound = new RetentionInput(-1, "30 天"),
            UnarchivedReturn = new RetentionInput(-1, "全部保留"),
        };

        var goodDisks = new (string, string?)[] { (@"D:\rec", "10") };
        var tooMany = Enumerable.Range(0, AppSettings.MaxDiskSlots + 1)
            .Select(i => ($@"D:\d{i}", (string?)"0")).ToArray();

        // ⚠️ 这里**必须分成两组**，它们的语义是反的（第一版混成一组，当场红了一条）：
        //   ① 越界 ⇒ **拒绝**（说清楚、不保存）：三格数字 + 两张磁盘表；
        //   ② 越界 ⇒ **保持原值**（认不出来就照旧）：三个下拉。
        // 混成一组的话，②那一组会被要求「必须被拒」，而它们的设计恰恰是不拒绝。
        var rejects = new (string Name, SettingsFormInput Input,
            (string, string?)[] Disks, (string, string?)[] Backups)[]
        {
            ("只有分段时长越界", good with { SegmentMinutes = "11" }, goodDisks, []),
            ("只有端口越界", good with { PlaybackPort = "65536" }, goodDisks, []),
            ("只有重复天数越界", good with { DuplicateCheckDays = "366" }, goodDisks, []),
            ("只有一处磁盘预留空间越界", good,
                [(@"D:\rec", (AppSettings.MaxReservedGb + 1).ToString())], []),
            ("只有一处磁盘预留空间是负数", good, [(@"D:\rec", "-1")], []),
            ("只有保存表太长", good, tooMany, []),
            ("只有备份表太长", good, goodDisks, tooMany),
        };

        foreach (var (name, input, disks, backups) in rejects)
        {
            var (next, problem) = Build(input, disks: disks, backups: backups);

            // ⚠️ 必须**被拒**：放行的话这个越界值就真的落进设置文件了。
            Assert.Null(next);
            Assert.NotEqual(string.Empty, problem);
        }

        // ② 三个「认不出来就保持原值」的下拉：不拒绝，但产物**照旧**过 IsPlausible。
        // 这一组钉的是「静默改成默认值」那条路：真改成默认值也不一定过不了
        // IsPlausible（默认值当然合法），所以还要断言**值没动**。
        var current = Current with
        {
            LogRetainDays = 30,
            PrerecordSeconds = 5,
            Cloud = Current.Cloud with { ParallelUploads = 3 },
        };

        var keeps = new (string Name, SettingsFormInput Input)[]
        {
            ("只有日志保留天数越界", good with { LogRetainDays = "366" }),
            ("只有预录缓冲越界", good with { PrerecordTag = "31" }),
            ("只有同时上传数越界", good with { CloudParallelText = "9" }),
        };

        foreach (var (name, input) in keeps)
        {
            var (next, problem) = Build(input, current, goodDisks, []);

            Assert.Equal(string.Empty, problem);
            Assert.Equal(30, next!.LogRetainDays);
            Assert.Equal(5, next.PrerecordSeconds);
            Assert.Equal(3, next.Cloud.ParallelUploads);
            Assert.True(AppSettings.IsPlausible(next), $"「{name}」：产物过不了 IsPlausible。");
        }
    }

    /// <summary>
    /// ⚠️⚠️ <b>已知缺陷（不是测试问题，是代码问题）</b> —— 保留期那四格里
    /// 手输一个**解析得出来但过大**的数（比如 <c>9999天</c>），
    /// <see cref="SettingsForm.Build"/> 会**放行**，而产物过不了
    /// <see cref="AppSettings.IsPlausible"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>实证过的完整后果链</b>（2026-10-07）：
    /// </para>
    /// <list type="number">
    /// <item>四个框的 XAML 都是 <c>IsEditable="True"</c>（`SettingsWindow.xaml:751,754,761,764`）
    /// ⇒ 界面上**打得出来**，不是只有手改 JSON 才够得着。</item>
    /// <item><see cref="SettingsForm.RetentionOf"/> 对解析得出的数一律
    /// <c>RetentionSetting.FromConfig(days)</c>，**没有 3650 那个上限**；
    /// 而 <c>AppSettings.PlausibleDays</c> 的上限是 3650。</item>
    /// <item><c>AppHost.SaveSettingsAsync</c> **不校验**（`AppHost.cs:856` 直接写盘）
    /// ⇒ 这个值真的落进设置文件。</item>
    /// <item>下次启动 <c>AppSettings</c> 读到 <c>!IsPlausible</c> ⇒ **整份设置回落默认值**
    /// （`AppSettings.cs:598`）—— 保留期、云端、磁盘表、关窗行为**一起**被打回原样，
    /// 而用户只碰过保留期那一格，且当场看不到任何提示。</item>
    /// </list>
    /// <para>
    /// ⚠️ <b>这是搬过来之前就有的</b>（`SettingsWindow` 里那份一模一样），
    /// 不是这次搬运引入的 —— 所以按「不混在搬家这一项里」的规矩**没有动手修**，
    /// 报给需求方定序。
    /// </para>
    /// <para>
    /// ⚠️ <b>这条断言在缺陷修好之后会变红</b>，那是**对的**：意思是
    /// 「洞补上了，回来把这条连同上面用例表里的那条注释一起删掉」。
    /// 写成「钉住当前错行为」是为了不让这个洞**静默地绿着**。
    /// </para>
    /// </remarks>
    [Fact]
    public void 已知缺陷_保留期手输超大数会被放行_而且产物过不了IsPlausible()
    {
        var (next, problem) = Build(new SettingsFormInput
        {
            SegmentMinutes = "3", PlaybackPort = "8720", DuplicateCheckDays = "7",
            ArchivedOutbound = new RetentionInput(-1, "9999天"),
        });

        Assert.Equal(string.Empty, problem);
        Assert.Equal(9999, next!.Retention.ArchivedOutbound.Days);
        Assert.False(AppSettings.IsPlausible(next));
    }

    // ─────────────────────────────────────────────
    // 保留期那四个可编辑下拉
    // ─────────────────────────────────────────────

    [Fact]
    public void 保留期下标和文本打架时_以文本为准()
    {
        // ⚠️ **以文本为准**才是对的那一头：WPF 的可编辑下拉里用户敲完数字，
        // `SelectedIndex` 可能还留在上一项上（这里是「7 天」），而框里已经是「30 天」——
        // 信下标的话用户会看到自己输的值被悄悄换掉。
        //
        // ⚠️ 第一版这条写的是「认选中项优先」，断言的是 7 —— **说过头了**，
        // 代码里那个下标分支还带着 `Text == 该项的 Label` 这个前置条件。
        // 真正承重的就是那个相等判断：拿掉它这条才会红（已验）。
        var index = RetentionSetting.Standard.ToList().IndexOf(new RetentionSetting(7));

        Assert.Equal(30, SettingsForm.RetentionOf(new RetentionInput(index, "30 天")).Days);

        // 两边一致时自然还是那一项。
        Assert.Equal(7, SettingsForm.RetentionOf(new RetentionInput(index, "7 天")).Days);

        // 一个都没选中（用户手输）时走文本。
        Assert.Equal(45, SettingsForm.RetentionOf(new RetentionInput(-1, "45天")).Days);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("全部保留")]
    [InlineData("看不懂")]
    [InlineData("-3")]
    public void 保留期认不出一律回落全部保留_朝少删的那头(string? text)
    {
        // ⚠️ 朝「少删」落：落错这一头是多占点磁盘，落错另一头是**证据没了**。
        Assert.Equal(RetentionSetting.KeepAll, SettingsForm.RetentionOf(new RetentionInput(-1, text)));
    }

    [Fact]
    public void 保留期那四个各进各的槽()
    {
        var next = Build(new SettingsFormInput
        {
            SegmentMinutes = "3",
            PlaybackPort = "8720",
            DuplicateCheckDays = "7",
            ArchivedOutbound = new RetentionInput(-1, "7 天"),
            ArchivedReturn = new RetentionInput(-1, "不保留"),
            UnarchivedOutbound = new RetentionInput(-1, "30天"),
            UnarchivedReturn = new RetentionInput(-1, "全部保留"),
        }).Next!;

        // ⚠️ 四个槽位弄混了界面上完全看不出来：一列是「真删」、一列是「只催不删」。
        Assert.Equal(7, next.Retention.ArchivedOutbound.Days);
        Assert.Equal(0, next.Retention.ArchivedReturn.Days);
        Assert.Equal(30, next.Retention.UnarchivedOutbound.Days);
        Assert.True(next.Retention.UnarchivedReturn.KeepsEverything);
    }

}
