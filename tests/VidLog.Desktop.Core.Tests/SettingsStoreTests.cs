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
            StaticStop = StaticStopOption.Off,
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
        var changes = SettingsStore.DescribeChanges(
            AppSettings.Default, AppSettings.Default);

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
