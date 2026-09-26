using System.Text.Json;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 边界留痕（2026-09-26）：外部进程、收尾、入网决策。
/// </summary>
/// <remarks>
/// <para>
/// 这三处挑出来的理由是同一个：**它们出的事，用户看到的只是「没反应」**——
/// 「录像传不上来」「这条播不了」「手机连不上」。而此前这三处
/// <b>一行日志都没有</b>，问「到底哪一步坏了」只能靠猜。
/// </para>
/// <para>
/// 都要求**真的产生一行 JSON**，而不是「调了 logger 就算」。
/// </para>
/// </remarks>
public class BoundaryLoggingTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-boundary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>读出落盘的每一行（都必须是合法 JSON）。</summary>
    private static async Task<List<JsonElement>> ReadLinesAsync(FileLogger logger)
    {
        var path = logger.Path;
        await logger.DisposeAsync();

        if (!File.Exists(path))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(path);
        return [.. lines.Select(line => JsonDocument.Parse(line).RootElement.Clone())];
    }

    private static string TextOf(JsonElement entry, string field) =>
        entry.GetProperty(field).GetString() ?? string.Empty;

    // ─────────────────────────────────────────────
    // 外部进程（FFmpeg 的每次调用）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 外部进程失败时记下退出码与它自己说的话()
    {
        // 失败时记的是 ffmpeg **自己说的话**（stderr 尾巴）——
        // 「编码器不存在」「文件头损坏」这些只有它说得清，从退出码上读不出来。
        //
        // ⚠️ 子进程的输出**刻意用 ASCII**：`cmd.exe` 的 stderr 走的是控制台代码页，
        // 而那个页在两台机器上不一样 —— 本机（936）中文能原样回来，
        // CI 的 windows-latest 上就是一串乱码。第一版断言的是中文，
        // **本机全绿、CI 红**，正是「一条只在这台机器上成立的测试」。
        // 这里要验的是「stderr 尾巴有没有被记下来」，不是「编码能不能过 cmd」。
        using var dir = new TempDir();
        using var logDir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(logDir.Path, "vidlog"));

        var result = await new SystemProcessRunner(logger)
            .RunAsync("cmd.exe", ["/c", "echo boom 1>&2 & exit 3"]);

        Assert.Equal(3, result.ExitCode);

        var entry = Assert.Single(await ReadLinesAsync(logger));
        Assert.Equal("WARN", TextOf(entry, "lvl"));
        Assert.Contains("以 3 退出", TextOf(entry, "msg"), StringComparison.Ordinal);

        var data = entry.GetProperty("data");
        Assert.Contains("boom", data.GetProperty("stderr").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 外部进程成功时不记_否则正常流量会把日志淹掉()
    {
        using var dir = new TempDir();
        using var logDir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(logDir.Path, "vidlog"));

        var result = await new SystemProcessRunner(logger).RunAsync("cmd.exe", ["/c", "echo ok"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(await ReadLinesAsync(logger));
    }

    // ─────────────────────────────────────────────
    // 收尾（I9 的唯一落点）
    // ─────────────────────────────────────────────

    private sealed class AlwaysOkRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }

    /// <summary>会真的造出产物文件的假 FFmpeg —— 成功路径要它（remux 要产出目标文件）。</summary>
    private sealed class PublishingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            var args = arguments.ToList();
            var y = args.IndexOf("-y");

            if (y >= 0 && y + 1 < args.Count)
            {
                var output = args[y + 1];
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
                File.WriteAllText(output, "published");
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    [Fact]
    public async Task 收尾失败时记下原因与会话()
    {
        // 收尾是这个应用**唯一会丢证据**的地方，而用户在界面上一律只看到「收尾失败」——
        // remux 失败 / 解码校验不过 / 写索引失败，要修的东西完全不同。
        using var dir = new TempDir();
        using var logDir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(logDir.Path, "vidlog"));

        var finalizer = new SessionFinalizer(
            new RemuxPipeline("ffmpeg", new AlwaysOkRunner()),
            new DecodeVerifier("ffmpeg", new AlwaysOkRunner()),
            new JsonLinesRecordingIndex(dir.File("index.jsonl")),
            dir.File("archive"),
            logger);

        var outcome = await finalizer.FinalizeAsync(
            "session-1",
            WaybillNumber.Parse("SF1000000001"),
            "device-1",
            [new SegmentProduct(0, dir.File("根本不存在.mkv"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
            StopReason.Manual);

        Assert.False(outcome.Succeeded);

        var entry = Assert.Single(await ReadLinesAsync(logger));
        Assert.Equal("ERROR", TextOf(entry, "lvl"));
        Assert.Contains("收尾失败", TextOf(entry, "msg"), StringComparison.Ordinal);

        var data = entry.GetProperty("data");
        Assert.Equal("session-1", data.GetProperty("会话").GetString());
        Assert.Equal(1, data.GetProperty("分段数").GetInt32());
        Assert.Equal(0, data.GetProperty("成功段数").GetInt32());
    }

    [Fact]
    public async Task 收尾成功也留痕_但只是_INFO()
    {
        using var dir = new TempDir();
        using var logDir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(logDir.Path, "vidlog"));

        var finalizer = new SessionFinalizer(
            // remux 要真的产出目标文件，后面那两步才过得去。
            new RemuxPipeline("ffmpeg", new PublishingRunner()),
            new DecodeVerifier("ffmpeg", new AlwaysOkRunner()),
            new JsonLinesRecordingIndex(dir.File("index.jsonl")),
            dir.File("archive"),
            logger);

        // 空的段列表会**提前返回**（那条路没有日志）—— 所以这里给一个真的存在的文件。
        var segment = dir.File("segment-000.mkv");
        await File.WriteAllTextAsync(segment, "x");

        var outcome = await finalizer.FinalizeAsync(
            "session-2",
            WaybillNumber.Parse("SF1000000001"),
            "device-1",
            [new SegmentProduct(0, segment, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
            StopReason.Manual);

        Assert.True(outcome.Succeeded, outcome.FailureReason);

        var entry = Assert.Single(await ReadLinesAsync(logger));
        Assert.Equal("INFO", TextOf(entry, "lvl"));
        Assert.Contains("收尾完成", TextOf(entry, "msg"), StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────
    // 入网决策（安全事件）
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 批准与拒绝都留痕_而且记不到凭据()
    {
        using var dir = new TempDir();
        using var logDir = new TempDir();
        var logger = new FileLogger(new FileLogOptions(logDir.Path, "vidlog"));

        var registry = new DeviceRegistry(dir.File("devices.jsonl"), logger: logger);

        var session = await registry.OpenSessionAsync();
        await registry.RequestAsync("phone-1", "打包手机-1", session.Token);
        await registry.DecideAsync("phone-1", approved: true);

        var claimed = await registry.ClaimAsync("phone-1", session.Token);
        Assert.Equal(EnrollStatus.Approved, claimed.Status);

        var entries = await ReadLinesAsync(logger);
        var blob = string.Join('\n', entries.Select(e => e.ToString()));

        // 决策留了痕
        Assert.Contains(entries, e => TextOf(e, "msg").Contains("同意了一台设备连接", StringComparison.Ordinal));
        Assert.Contains(entries, e => TextOf(e, "msg").Contains("签发了设备凭据", StringComparison.Ordinal));

        // ⚠️ 而**凭据与令牌一个都不能出现** —— 诊断包会把 logs/* 整个打包外发。
        Assert.DoesNotContain(claimed.Credential!, blob, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Token, blob, StringComparison.Ordinal);
    }
}
