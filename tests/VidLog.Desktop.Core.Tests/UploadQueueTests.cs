using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 上传队列的落盘形态（JSON Lines，**最后一行胜出**）。
/// </summary>
/// <remarks>
/// 队列本身丢了不可惜（重扫一遍索引就重建得出来），所以读取一律**宽着来**：
/// 坏行丢掉、读不出来按空队列算，绝不因此抛。但有两件事必须钉死：
/// <list type="number">
/// <item>同一条的**最后**一行胜出 —— 反了的话「传完了」会被它早先的「等着传」盖掉，
/// 于是同一条录像被反复重传。</item>
/// <item>上次崩在半路的「正在传」要翻回「等着传」—— 不翻的话那些条目
/// 会永远停在队列里，而界面上显示的是一个看不出要干嘛的中间态。</item>
/// </list>
/// </remarks>
public class UploadQueueTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(8));

    private static UploadQueueItem Item(string id, CloudUploadState state = CloudUploadState.Pending) => new(
        id,
        $"2026/09/30/SF1/{id}.mp4",
        $"/apps/VidLog/2026/09/30/发货/SF1_{id}.mp4",
        1024,
        Now.AddMinutes(-30),
        state,
        0,
        null,
        Now);

    [Fact]
    public async Task 同一条取最后一行()
    {
        using var dir = new TempDir();
        var queue = new UploadQueue(dir.QueuePath);

        await queue.AppendAsync(Item("e1"));
        await queue.AppendAsync(Item("e1", CloudUploadState.Uploading));
        await queue.AppendAsync(Item("e1", CloudUploadState.Done));

        var one = Assert.Single(await queue.LoadAsync());
        Assert.Equal(CloudUploadState.Done, one.State);

        // 三行都在盘上（追加写、不重写既有记录）—— 这是「断电也不会损坏」的来源。
        Assert.Equal(3, (await File.ReadAllLinesAsync(dir.QueuePath)).Length);
    }

    [Fact]
    public async Task 从没传过的_正在传_翻回等着传()
    {
        // 上次跑到一半被杀掉的那些就是卡在这个状态上，而**没人会再来推它**。
        using var dir = new TempDir();
        var queue = new UploadQueue(dir.QueuePath);

        await queue.AppendAsync(Item("e1", CloudUploadState.Uploading));
        await queue.AppendAsync(Item("e2", CloudUploadState.Done));
        await queue.AppendAsync(Item("e3", CloudUploadState.Failed));

        var items = (await queue.LoadAsync()).ToDictionary(i => i.EvidenceId, StringComparer.Ordinal);

        Assert.Equal(CloudUploadState.Pending, items["e1"].State);

        // 已经完成的、已经失败的**不动**：翻前者会让它被重传一遍。
        Assert.Equal(CloudUploadState.Done, items["e2"].State);
        Assert.Equal(CloudUploadState.Failed, items["e3"].State);
    }

    [Fact]
    public async Task 坏行跳过并留痕_绝不因此抛()
    {
        using var dir = new TempDir();
        var logger = new CapturingLogger();
        var queue = new UploadQueue(dir.QueuePath, logger);

        await queue.AppendAsync(Item("e1"));

        // 手工塞两行读不出来的（崩在半路写下的半截 JSON、以及一个空对象）。
        await File.AppendAllTextAsync(dir.QueuePath, "{这不是 JSON\n");
        await File.AppendAllTextAsync(dir.QueuePath, "{}\n");

        var items = await queue.LoadAsync();

        Assert.Single(items);
        Assert.Equal("e1", items[0].EvidenceId);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("跳过"));
    }

    [Fact]
    public async Task 队列文件不存在时是空队列而不是错()
    {
        using var dir = new TempDir();
        var queue = new UploadQueue(System.IO.Path.Combine(dir.Path, "还没建", "queue.jsonl"));

        Assert.Empty(await queue.LoadAsync());

        // 而且**第一次写的时候会把目录建出来** —— 否则第一次上传必然失败。
        await queue.AppendAsync(Item("e1"));
        Assert.Single(await queue.LoadAsync());
    }

    [Fact]
    public async Task 查一条没有的就是没有()
    {
        using var dir = new TempDir();
        var queue = new UploadQueue(dir.QueuePath);

        await queue.AppendAsync(Item("e1"));

        Assert.NotNull(await queue.FindAsync("e1"));
        Assert.Null(await queue.FindAsync("没这条"));
    }

    private sealed class CapturingLogger : IAppLogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void Log(LogLevel level, string category, string message) =>
            Entries.Add((level, message));

        public void Log(
            LogLevel level, string category, string message, IReadOnlyDictionary<string, object?> data) =>
            Entries.Add((level, message));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vidlog-queue-" + Guid.NewGuid().ToString("N"));

        public string QueuePath => System.IO.Path.Combine(Path, "upload-queue.jsonl");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
