using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Scanning;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 设置的读写。
/// </summary>
/// <remarks>
/// 关键约束是 <b>I4</b>：坏设置**不得**让录制起不来。所以下面一半的用例
/// 都在喂各种坏输入，断言「回落默认值 + 说得出为什么」。
/// </remarks>
public class SettingsStoreTests
{
    // ─────────────────────────────────────────────
    // 读：坏输入一律回落，绝不抛
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 文件不存在时返回默认值且不报警告()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));

        var result = await store.LoadAsync();

        // 第一次运行就是这样，不该吓唬用户。
        Assert.Empty(result.Warnings);
        Assert.Equal(AppSettings.Default, result.Settings);
    }

    [Fact]
    public async Task 坏JSON回落默认值并给出可见的警告()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        await File.WriteAllTextAsync(path, "{ 这不是 JSON");

        var result = await new SettingsStore(path).LoadAsync();

        Assert.Equal(AppSettings.Default, result.Settings);
        // I3：不允许静默失效 —— 用户得知道设置没读进来。
        Assert.NotEmpty(result.Warnings);
        // 而且原文件要留着，别把他手写的内容冲掉。
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task 越界值整体回落而不是静默接受()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        // 端口 0 起不来、分段 0 分钟会疯狂滚段 —— 这类值不会报错，
        // 只会让行为变得莫名其妙，所以宁可整体回落。
        await File.WriteAllTextAsync(path,
            """{"SegmentMinutes":0,"PlaybackPort":0}""");

        var result = await new SettingsStore(path).LoadAsync();

        Assert.Equal(AppSettings.Default, result.Settings);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task 合法设置原样读回()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var want = AppSettings.Default with
        {
            Mode = WorkMode.Continuous,
            IdleReminder = IdleReminderOption.Off,
            SegmentMinutes = 3,
            PlaybackPort = 9090,
        };

        await new SettingsStore(path).SaveAsync(want);
        var result = await new SettingsStore(path).LoadAsync();

        Assert.Empty(result.Warnings);
        Assert.Equal(want, result.Settings);
    }

    [Fact]
    public async Task 写盘是原子的_不会留下半个JSON()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));

        await store.SaveAsync(AppSettings.Default with { SegmentMinutes = 2 });

        // 临时文件必须被改名掉，不能留下。
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        Assert.True(File.Exists(dir.File("settings.json")));
    }

    // ─────────────────────────────────────────────
    // 保留期与归档层（规格 §3.5.1 / §3.5.2.1）
    // ─────────────────────────────────────────────

    [Fact]
    public void 出厂默认是四个数全是全部保留_且归档层是本机磁盘()
    {
        // 「装完就有个默认 30 天」是不能接受的 —— 清理必须是用户主动开的（§6.2）。
        Assert.Equal(ArchiveBackendKind.LocalDisk, AppSettings.Default.ArchiveBackend);
        Assert.Equal(RetentionSettings.KeepAll, AppSettings.Default.Retention);
        Assert.False(AppSettings.Default.Retention.DeletesAnything);
    }

    [Fact]
    public async Task 归档目录存得住_而且不落盘成一个多余的字段组()
    {
        // `Archive` 是由 `ArchiveBackend` + `ArchiveDirectory` 算出来的（不落盘），
        // 所以「存了什么」要看那两个字段本身。
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var want = AppSettings.Default with
        {
            ArchiveBackend = ArchiveBackendKind.MountedDrive,
            ArchiveDirectory = @"Z:\vidlog",
        };

        await new SettingsStore(path).SaveAsync(want);
        var result = await new SettingsStore(path).LoadAsync();

        Assert.Empty(result.Warnings);
        Assert.Equal(ArchiveBackendKind.MountedDrive, result.Settings.ArchiveBackend);
        Assert.Equal(@"Z:\vidlog", result.Settings.ArchiveDirectory);

        var archive = result.Settings.Archive;
        Assert.True(archive.IsDirectoryType);
        Assert.True(archive.AllowsCleanup, "挂载盘算「在别处」，所以允许开清理（§3.5.1）");
        Assert.Null(archive.ConfigurationProblem);

        // 那个合成的属性本身**不写进文件** —— 写了就会出现两份真相。
        Assert.DoesNotContain("\"Archive\":", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 老设置文件里只有归档层没有目录_照样读得出来()
    {
        // 2026-09-27 之前的 settings.json 里没有 ArchiveDirectory 这个键。
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        await File.WriteAllTextAsync(path, """{"ArchiveBackend":1}""");

        var result = await new SettingsStore(path).LoadAsync();

        Assert.Equal(ArchiveBackendKind.Nas, result.Settings.ArchiveBackend);
        Assert.Null(result.Settings.ArchiveDirectory);

        // 老文件里的 NAS **没有路径** —— 那是「没配好」，不是「配好了没生效」。
        var problem = result.Settings.Archive.ConfigurationProblem;
        Assert.NotNull(problem);
        Assert.Contains("还没填归档目录", problem);
    }

    [Fact]
    public async Task 四个数分开存读_互不串()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var want = AppSettings.Default with
        {
            ArchiveBackend = ArchiveBackendKind.Nas,
            ArchiveDirectory = @"\\nas\vidlog",
            Retention = new RetentionSettings(
                new RetentionSetting(7), new RetentionSetting(30),
                new RetentionSetting(3), new RetentionSetting(45)),
        };

        await new SettingsStore(path).SaveAsync(want);
        var result = await new SettingsStore(path).LoadAsync();

        Assert.Empty(result.Warnings);

        var r = result.Settings.Retention;
        Assert.Equal(7, r.ArchivedOutbound.Days);
        Assert.Equal(30, r.ArchivedReturn.Days);
        Assert.Equal(3, r.UnarchivedOutbound.Days);
        Assert.Equal(45, r.UnarchivedReturn.Days);   // 自定义天数也存得住
        Assert.True(r.DeletesAnything);
    }

    [Fact]
    public async Task 老格式的两个数按已备份那一列读入_并说出来()
    {
        // 2026-09-27 之前存的是 `RetentionPolicies`：{"Outbound":{"Mode":1,"KeepDays":7},…}。
        // ⚠️ 不认它的话那两个数会被**静默丢掉**（反序列化跳过未知属性），
        // 用户看到的是「我明明设过 7 天，怎么变回全部保留了」。
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        await File.WriteAllTextAsync(path,
            """
            {"ArchiveBackend":1,"Retention":{"Outbound":{"Mode":1,"KeepDays":7},
             "Return":{"Mode":1,"KeepDays":30}}}
            """);

        var result = await new SettingsStore(path).LoadAsync();

        var r = result.Settings.Retention;
        Assert.Equal(7, r.ArchivedOutbound.Days);
        Assert.Equal(30, r.ArchivedReturn.Days);

        // 新增的两列默认「全部保留」——**未备份那一列永不自动删**。
        Assert.Equal(RetentionSetting.KeepAll, r.UnarchivedOutbound);
        Assert.Equal(RetentionSetting.KeepAll, r.UnarchivedReturn);

        Assert.Contains(result.Warnings, w => w.Contains("旧格式"));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(99999)]
    public async Task 手改出来的越界保留天数整体回落(int days)
    {
        // 负数天数不会报错，只会让 cutoff 落到未来 ⇒ 判什么都超期 ⇒ 全删。
        // 99999 天（≈273 年）是「手抖多打几个 9」——上限 3650 天是本仓标定的
        // （规格 §3.5.2.1 说这类阈值由实现标定）。
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        await File.WriteAllTextAsync(path,
            """
            {"Retention":{"ArchivedOutbound":DAYS,"ArchivedReturn":DAYS}}
            """.Replace("DAYS", days.ToString()));

        var result = await new SettingsStore(path).LoadAsync();

        // 一份坏掉的设置**整份**回落默认值 —— 这是既有的口径（`IsPlausible` 一票否决），
        // 所以这里断言的是「四个数都回到了全部保留」。
        Assert.Equal(RetentionSettings.KeepAll, result.Settings.Retention);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task 老配置里的扫码静止停录_回落到同码停并说出来()
    {
        // 规格 §3.3.1：电脑端删掉那个模式之后，「设置文件里存着它」要**回落到同码停**
        // （不是连续扫 —— 同码停会自己停，不会一路录到把盘写满）。
        //
        // ⚠️ 老配置里的形态是**数字**：本仓没挂 JsonStringEnumConverter，
        // 枚举一律按数字存（见 `SettingsStoreTests` 里那条保留期的字面量）。
        // 规格那句「设置文件里存的是模式名字」对**手机端**成立（Dart 存 name），
        // 对电脑端不成立 —— 所以这里钉的是数字 2。
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        await File.WriteAllTextAsync(path, """{"Mode":2}""");

        var result = await new SettingsStore(path).LoadAsync();

        Assert.Equal(WorkMode.StopOnSameWaybill, result.Settings.Mode);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("扫码静止停录", StringComparison.Ordinal));

        // 别的东西不该被这条回落带走 —— 只换模式，不是整份回落默认值。
        Assert.Equal(AppSettings.Default.IdleReminder, result.Settings.IdleReminder);
    }

    [Fact]
    public async Task 闲置提醒的档位存在老键名下_老配置读得回来()
    {
        // 属性叫 IdleReminder，而 JSON 键仍是 `StaticStop`（历史名）——
        // 键名不动是为了**老配置不丢**：存的是数字，换了键名整条就回落默认值。
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        await File.WriteAllTextAsync(path, """{"StaticStop":1}""");

        var result = await new SettingsStore(path).LoadAsync();

        Assert.Equal(IdleReminderOption.Two, result.Settings.IdleReminder);
    }

    [Fact]
    public void 四个数的变更各留一行痕()
    {
        // 合成一行的话，看日志的人分不清是哪一份动了；而这里**四个数各有一行**，
        // 名字还是「已备份 / 未备份」（两列语义相反正是 §3.5.2.1 最要紧的一句话）。
        var before = AppSettings.Default;
        var after = before with
        {
            Retention = RetentionSettings.KeepAll with
            {
                ArchivedOutbound = new RetentionSetting(7),
            },
        };

        var changes = SettingsStore.DescribeChanges(before, after);

        Assert.Single(changes);
        Assert.Contains("发货.已备份", changes[0]);
        Assert.DoesNotContain(changes, c => c.Contains("退货"));
        Assert.DoesNotContain(changes, c => c.Contains("未备份"));
    }

    // ─────────────────────────────────────────────
    // 变更留痕（AGENTS.md §6）
    // ─────────────────────────────────────────────

    [Fact]
    public void 变更留痕只列真正变了的字段()
    {
        var before = AppSettings.Default;
        var after = before with { Mode = WorkMode.Continuous, SegmentMinutes = 5 };

        var changes = SettingsStore.DescribeChanges(before, after);

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Contains("Mode"));
        Assert.Contains(changes, c => c.Contains("SegmentMinutes"));
        // 没变的不能出现。
        Assert.DoesNotContain(changes, c => c.Contains("PlaybackPort"));
    }

    [Fact]
    public void 没有变化时留痕为空()
    {
        Assert.Empty(SettingsStore.DescribeChanges(AppSettings.Default, AppSettings.Default));
    }

    [Fact]
    public void 密钥类字段只记已修改不记值()
    {
        // AGENTS.md §6：密钥类字段只记「已修改」，不记值。
        // 今天还没有密钥字段，但这条判据先立住 —— 等有了再补就晚了。
        //
        // ⚠️ 这条以前是**绿在一个巧合上**的：它拿 `Default` 与 `Default` 比，
        // 留痕本来就是空的，空集合当然不含 "token" —— 判据换成恒真也照样绿。
        // 2026-09-27 改成真的造一处变化再断言。
        var previous = AppSettings.Default;
        var next = previous with { SegmentMinutes = previous.SegmentMinutes + 1 };

        var changes = SettingsStore.DescribeChanges(previous, next);

        // 先证明这份留痕**确实有内容**（否则下面那句又是空集合上的恒真）。
        Assert.Contains(changes, c => c.StartsWith(nameof(AppSettings.SegmentMinutes), StringComparison.Ordinal));
        Assert.DoesNotContain(changes, c => c.Contains("token"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
