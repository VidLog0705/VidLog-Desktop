using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 入网申请**到达电脑端**时要留痕，而且只留一次（规格 §3.4.5 ①②、`AGENTS.md` §6.1）。
/// </summary>
/// <remarks>
/// <para>
/// 这条留痕的由来很具体（2026-10-03）：有人报「手机扫码后电脑端不提示同意」，
/// 而日志里**一个字都没有** —— 分不清是「手机的请求根本没到」（防火墙）
/// 还是「到了、但界面没弹窗」，两个方向的修法完全相反。申请到达本身就是关键操作。
/// </para>
/// <para>
/// ⚠️ 手机是**每两秒轮询一次**的（同一个 <c>deviceId</c> 会反复送进来），
/// 所以只记「第一次看到这台设备」那一次 —— 每轮都记会把日志刷满，
/// 而被刷满的日志等于没有日志。
/// </para>
/// </remarks>
public class EnrollApprovalTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-enroll-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            // 宽着接（与其它测试文件同一个口径：Windows 在文件被持有时可能报
            // UnauthorizedAccessException 而不是 IOException，清理失败不该让测试红）。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>留痕的**数据**也收下来 —— 这条测试要断言「令牌没进去」，只看消息看不见。</summary>
    private sealed class CapturingLogger : IAppLogger
    {
        public List<(LogLevel Level, string Message, string Data)> Entries { get; } = [];

        public void Log(LogLevel level, string category, string message) =>
            Entries.Add((level, message, string.Empty));

        public void Log(
            LogLevel level,
            string category,
            string message,
            IReadOnlyDictionary<string, object?> data) =>
            Entries.Add((level, message, string.Join(' ', data.Select(kv => $"{kv.Key}={kv.Value}"))));
    }

    private static DeviceRegistry NewRegistry(TempDir dir, IAppLogger log) =>
        new(System.IO.Path.Combine(dir.Path, "devices.jsonl"), logger: log);

    [Fact]
    public async Task 收到入网申请留一条痕_同一台设备的后续轮询不重复记()
    {
        using var dir = new TempDir();
        var log = new CapturingLogger();
        var registry = NewRegistry(dir, log);

        var session = await registry.OpenSessionAsync();

        // 手机：我到了 / 我还在等 / 我还在等 ……
        await registry.RequestAsync("device-1", "测试手机", session.Token);
        await registry.RequestAsync("device-1", "测试手机", session.Token);
        await registry.RequestAsync("device-1", "测试手机", session.Token);

        var arrivals = log.Entries.Where(e => e.Message == "收到入网申请").ToList();

        Assert.Single(arrivals);
        Assert.Equal(LogLevel.Info, arrivals[0].Level);
        Assert.Contains("device-1", arrivals[0].Data, StringComparison.Ordinal);

        // 另一台手机是**另一条**申请（不能因为"有痕了"就把第二台也吞掉）。
        await registry.RequestAsync("device-2", "第二台手机", session.Token);

        Assert.Equal(2, log.Entries.Count(e => e.Message == "收到入网申请"));
    }

    [Fact]
    public async Task 入网这几条痕里绝不出现令牌与凭据()
    {
        using var dir = new TempDir();
        var log = new CapturingLogger();
        var registry = NewRegistry(dir, log);

        var session = await registry.OpenSessionAsync();
        await registry.RequestAsync("device-1", "测试手机", session.Token);
        await registry.DecideAsync("device-1", approved: true);
        var credential = (await registry.ClaimAsync("device-1", session.Token)).Credential!;

        // 台账要真的有东西，否则下面那个循环是**空的也算过**。
        Assert.NotEmpty(log.Entries);

        foreach (var entry in log.Entries)
        {
            Assert.DoesNotContain(session.Token, entry.Message + entry.Data, StringComparison.Ordinal);
            Assert.DoesNotContain(credential, entry.Message + entry.Data, StringComparison.Ordinal);
        }
    }
}
