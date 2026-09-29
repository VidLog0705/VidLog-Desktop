using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 外部进程那一层的日志。
/// </summary>
/// <remarks>
/// 只测**一件事**：失败时记的那条 argv 里不能带凭据。
/// 它值得单独一个文件，因为这是「用户的密码会不会进日志」这个问题的落点，
/// 而发现它的方式很偶然（翻 <c>SystemProcessRunner</c> 才看到它在记整条 argv）。
/// </remarks>
public class ProcessRunnerTests
{
    [Fact]
    public async Task 失败时记的argv里不许有摄像头密码()
    {
        // ⚠️ 背景：网络摄像头的地址是 `rtsp://账号:密码@主机/流`，
        // 而它**就在 argv 里**。落败的 RTSP 连接正是最常走到这条日志的情况
        // —— 用户改错一次密码，密码就进了 logs/，而诊断包会把整个 logs/ 打包外发。
        var logger = new CapturingLogger();
        var runner = new SystemProcessRunner(logger);

        // 借 cmd 造一次「非 0 退出」，并把地址当成一个它不认识的参数带进去。
        // （地址本身不参与执行，只是要让它出现在 argv 里。）
        await runner.RunAsync(
            "cmd.exe",
            ["/c", "vidlog-no-such-command", "rtsp://admin:hunter2@10.0.0.9:554/stream"]);

        var entry = Assert.Single(logger.Entries);
        var arguments = (string)entry.Data["参数"]!;

        Assert.DoesNotContain("hunter2", arguments);
        // ⚠️ 但**主机要留着**：那条日志的用处就是回答「它在连哪一台」。
        Assert.Contains("10.0.0.9", arguments);
    }

    [Fact]
    public async Task 不带凭据的参数原样记下()
    {
        // 别把普通参数也抹了 —— 那样日志就没用了。
        var logger = new CapturingLogger();
        var runner = new SystemProcessRunner(logger);

        await runner.RunAsync("cmd.exe", ["/c", "vidlog-no-such-command", @"C:\work\seg.mkv"]);

        var arguments = (string)Assert.Single(logger.Entries).Data["参数"]!;

        Assert.Contains(@"C:\work\seg.mkv", arguments);
    }

    private sealed class CapturingLogger : IAppLogger
    {
        public List<(LogLevel Level, IReadOnlyDictionary<string, object?> Data)> Entries { get; } = [];

        public void Log(LogLevel level, string category, string message) =>
            Entries.Add((level, new Dictionary<string, object?>()));

        public void Log(
            LogLevel level, string category, string message,
            IReadOnlyDictionary<string, object?> data) =>
            Entries.Add((level, data));
    }
}
