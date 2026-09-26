using System.Net;
using System.Net.Sockets;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 启动流程。
/// </summary>
/// <remarks>
/// 规格 §3.1.1 的「重启后自动收尾孤儿」是发生在**启动时**的行为。
/// 装配逻辑要是写在 WPF 窗口的构造函数里，这段就永远测不到 ——
/// 所以它被抽在 <see cref="DesktopServices.StartAsync"/>。
/// </remarks>
public class DesktopServicesTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-boot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Dir(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task 启动时建好数据目录()
    {
        // 规格 §6.2：卸载不清用户数据 —— 数据根必须在安装目录之外。
        using var dir = new TempDir();
        var layout = new DataLayout(dir.Dir("data"));

        await using var services = DesktopServices.Create(layout, playbackPort: null);
        await services.StartAsync();

        Assert.True(Directory.Exists(layout.RootDirectory));
        Assert.True(Directory.Exists(layout.ArchiveRoot));
        Assert.True(Directory.Exists(layout.WorkspaceRoot));
    }

    [Fact]
    public async Task 不起回放服务时也能启动()
    {
        using var dir = new TempDir();
        var layout = new DataLayout(dir.Dir("data"));

        await using var services = DesktopServices.Create(layout, playbackPort: null);
        var report = await services.StartAsync();

        Assert.Null(report.PlaybackUrl);
        Assert.Empty(report.OrphanOutcomes);
    }

    [Fact]
    public async Task 回放服务起得来并给出地址()
    {
        using var dir = new TempDir();
        var layout = new DataLayout(dir.Dir("data"));
        var port = FreePort();

        await using var services = DesktopServices.Create(layout, playbackPort: port);
        var report = await services.StartAsync();

        Assert.NotNull(report.PlaybackUrl);
        Assert.Contains(port.ToString(), report.PlaybackUrl);
    }

    [Fact]
    public async Task 端口被占时不阻断启动_只降级成一条警告()
    {
        // 回放起不来不该让应用不可用 —— 收尾与录制远比回放重要（I4 的同一条精神）。
        using var dir = new TempDir();
        var layout = new DataLayout(dir.Dir("data"));

        var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var takenPort = ((IPEndPoint)squatter.LocalEndpoint).Port;

        try
        {
            await using var services = DesktopServices.Create(layout, playbackPort: takenPort);
            var report = await services.StartAsync();

            Assert.Null(report.PlaybackUrl);
            Assert.Contains(report.Warnings, w => w.Contains("回放服务"));
        }
        finally
        {
            squatter.Stop();
        }
    }

    [Fact]
    public async Task 找不到FFmpeg时给出可见警告而不是静默()
    {
        // 换个空目录 + 清掉环境变量，让定位器只能去 PATH 找；
        // PATH 上有没有 ffmpeg 不确定，所以这里只断言「要么找到、要么有警告」。
        var previous = Environment.GetEnvironmentVariable(FfmpegLocator.EnvironmentVariable);
        try
        {
            using var dir = new TempDir();
            var layout = new DataLayout(dir.Dir("data"));

            await using var services = DesktopServices.Create(layout, playbackPort: null);
            var report = await services.StartAsync();

            var hasFfmpeg = services.FfmpegPath is not null;
            var hasWarning = report.Warnings.Any(w => w.Contains("FFmpeg"));

            Assert.True(
                hasFfmpeg ^ hasWarning,
                $"找到 FFmpeg={hasFfmpeg}，有警告={hasWarning} —— 两者必居其一，不能既没找到又不吭声");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FfmpegLocator.EnvironmentVariable, previous);
        }
    }

    // ─────────────────────────────────────────────
    // 绊线：装配根必须真的调 StartAsync
    // ─────────────────────────────────────────────

    /// <summary>
    /// 钉住 <c>AppHost</c> 里那一跳：装配完必须调 <see cref="DesktopServices.StartAsync"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这条测试看的是源码文本，不是行为</b> —— 说清楚为什么只能这样：
    /// 那一跳落在 <c>VidLog.Desktop.App</c>（WPF、<c>net9.0-windows</c>），
    /// 而本测试工程是 <c>net9.0</c>，引用不了它；App 层也没有自己的测试工程。
    /// 于是「组合根少调了一步」这类 bug 对整套测试**完全不可见**。
    /// </para>
    /// <para>
    /// 这不是假想的风险，是**已经发生过一次**的：这一跳曾经漏掉，
    /// 后果是孤儿永不收尾、回放服务永不起、「局域网回放页」永久禁用，
    /// 三个功能静默失效而 337 条测试全绿。
    /// </para>
    /// <para>
    /// ⚠️ <b>天花板</b>：文本绊线只挡「有人把这一行删了/注释了」，
    /// 挡不住「改成一个不跑的路径」。把 App 层做成可测的（要么给它一个
    /// <c>net9.0-windows</c> 测试工程，要么把 <c>DesktopServices.Create</c>
    /// 与 <c>StartAsync</c> 合成一个忘不掉的方法）才是根治 —— 记在
    /// <c>docs/实现决策.md</c>「装配的最后一跳」。那时这条绊线应当被删掉。
    /// </para>
    /// </remarks>
    [Fact]
    public void AppHost_装配时会真的启动服务()
    {
        var path = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App", "AppHost.cs");

        // 断言的是「调用形态」而非精确某一行 —— 改写措辞、加空行都不该让它变红，
        // 只有**这一步不再发生**才该变红。
        //
        // 必须排除被注释掉的那一行：`// await services.StartAsync(...)` 里
        // **仍然含有**这串文本。第一版漏了这一点，于是「把调用注释掉」这种
        // 最常见的失活方式反而抓不住（2026-09-23 试出来的）。
        Assert.Contains(
            File.ReadAllLines(path),
            line => line.Contains("await services.StartAsync(")
                && !line.TrimStart().StartsWith("//"));
    }

    /// <summary>
    /// 钉住「那张二维码真的显示得出来、批准真的点得下去」—— App 层必须真的调到那三个方法。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只能看源码文本的理由与上一条相同（App 层是 <c>net9.0-windows</c>，没有测试工程）。
    /// 但这一条挡的是**另一种**失效，而且那种失效**真的发生过**：
    /// 2026-09-24 收工时 <see cref="DeviceRegistry.PendingAsync"/> 与
    /// <c>DevicesAsync</c> 是**零调用点** —— Core 逻辑、单测、CI 全绿，
    /// 而**屏幕上根本没有地方能显示出那张二维码**，于是手机永远换不到凭据、
    /// M5 端到端走不通。那不是「没联过」，是这一步根本没做。
    /// </para>
    /// <para>
    /// ⚠️ <b>天花板</b>：文本绊线只挡「有人把这一行删了/注释了」（注释先被剥掉，
    /// 所以注释掉也挡得住），挡不住「改成一个不跑的路径」。
    /// 根治办法与上一条同一句话：把 App 层做成可测的。
    /// </para>
    /// </remarks>
    [Fact]
    public void 入网二维码在界面上真的有出路()
    {
        var checks = new (string File, string Fragment, string Why)[]
        {
            ("EnrollWindow.xaml.cs", "OpenSessionAsync(", "屏幕上那张码就生成不出来"),
            ("EnrollWindow.xaml.cs", "PendingAsync(", "界面不知道谁在申请，也就没有人去批"),
            ("EnrollWindow.xaml.cs", "DecideAsync(", "同意 / 拒绝落不下去，手机会一直等"),
            ("MainWindow.xaml.cs", "new EnrollWindow(", "设置里那个按钮点不开二维码窗口"),
        };

        foreach (var (file, fragment, why) in checks)
        {
            var code = string.Join(
                '\n',
                File.ReadAllLines(Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App", file))
                    .Where(line => !line.TrimStart().StartsWith("//")));

            Assert.True(
                code.Contains(fragment, StringComparison.Ordinal),
                $"{file} 里没有 {fragment} —— {why}。见 docs/实现决策.md「装配的最后一跳」。");
        }
    }

    /// <summary>
    /// 钉住「保留期真的执行了」—— <c>AppHost</c> 必须调 <c>FileLogger.PurgeExpired</c>。
    /// </summary>
    /// <remarks>
    /// 只能看源码文本的理由与上一条相同（App 层是 <c>net9.0-windows</c>，没有测试工程）。
    /// 这一条挡的失效**已经发生过**：<c>LogRetention.SelectExpired</c> 写对了、
    /// 有 4 条测试盯着它，而在 <c>src</c> 里**零调用点** ⇒
    /// 「保留 14 天」是个死值，日志只增不减 —— 而整套测试全绿。
    /// 与「装配的最后一跳」是同一类：**方法写对了、没人调**。
    /// </remarks>
    [Fact]
    public void AppHost_启动时会真的清理过期日志()
    {
        var path = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App", "AppHost.cs");

        var code = string.Join(
            '\n',
            File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//")));

        Assert.Contains("FileLogger.PurgeExpired(", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 钉住「崩溃兜底装上了，而且装在任何业务代码**之前**」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只能看源码文本的理由与上两条相同（App 层是 <c>net9.0-windows</c>，没有测试工程）。
    /// 这一条挡的失效**已经发生过**：2026-09-26 之前三个钩子**一个都没有**，
    /// 而这个应用是 <c>WinExe</c>（无控制台）—— 未捕获异常的表现是
    /// **窗口直接消失、磁盘上一个字都没有**。
    /// </para>
    /// <para>
    /// ⚠️ <b>顺序是这条测试的一半</b>：钩子挂在 <c>AppHost.StartAsync</c> **之后**的话，
    /// 启动过程里崩掉就没有记录 —— 而「启动失败」恰恰是最需要现场的那一种。
    /// 所以断言的是「谁在前」，不是「两个都在」。
    /// </para>
    /// </remarks>
    [Fact]
    public void App_崩溃兜底装在任何业务代码之前()
    {
        var path = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App", "App.xaml.cs");

        var code = string.Join(
            '\n',
            File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//")));

        var install = code.IndexOf("CrashGuard.Install(", StringComparison.Ordinal);
        var start = code.IndexOf("AppHost.StartAsync(", StringComparison.Ordinal);

        Assert.True(install >= 0, "App.xaml.cs 里没有装崩溃兜底");
        Assert.True(start >= 0, "App.xaml.cs 里没有启动 AppHost？");
        Assert.True(install < start, "崩溃兜底必须在 AppHost.StartAsync **之前**装上");
    }

    /// <summary>
    /// 钉住三个钩子都挂了 —— 少一个就是一类异常永远没记录。
    /// </summary>
    /// <remarks>
    /// 三个各挡一类，缺一不可：
    /// <c>AppDomain.UnhandledException</c>（非 UI 线程，进程会死）、
    /// <c>Application.DispatcherUnhandledException</c>（UI 线程）、
    /// <c>TaskScheduler.UnobservedTaskException</c>（本仓有好几处
    /// <c>_ = Task.Run(...)</c>，.NET Core 默认把这类异常**静默丢掉**）。
    /// </remarks>
    [Fact]
    public void 崩溃兜底挂了三个钩子()
    {
        var path = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App", "Platform", "CrashGuard.cs");
        var code = string.Join(
            '\n',
            File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//")));

        Assert.Contains("AppDomain.CurrentDomain.UnhandledException +=", code, StringComparison.Ordinal);
        Assert.Contains("DispatcherUnhandledException +=", code, StringComparison.Ordinal);
        Assert.Contains("TaskScheduler.UnobservedTaskException +=", code, StringComparison.Ordinal);

        // 遗言要带堆栈，不是只有一句 Message（这个仓此前 22 处日志全是 Message）。
        Assert.Contains("ToString()", code, StringComparison.Ordinal);
    }

    /// <summary>从测试程序集往上找到仓库根（含 <c>src</c> 与 <c>tests</c> 的那一层）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 往上找不到仓库根（含 src 与 tests 的目录）。");
    }

    // ─────────────────────────────────────────────
    // 端到端：被杀会话在下次启动时被收尾（规格 §3.1.1 / §8）
    // ─────────────────────────────────────────────

    [RequiresFfmpegFact]
    public async Task 端到端_启动时收尾上次没走完的会话()
    {
        var ffmpeg = FfmpegLocator.TryFind()!;
        var runner = new SystemProcessRunner();

        using var dir = new TempDir();
        var layout = new DataLayout(dir.Dir("data"));

        // 造一个「录到一半被杀」的现场：工作区里有分段、有 manifest、没有收尾标记
        var workspace = new RecordingWorkspace(layout.WorkspaceRoot);
        var sessionDir = workspace.SessionDirectory("session-killed");
        Directory.CreateDirectory(sessionDir);

        var segmentPath = System.IO.Path.Combine(sessionDir, "segment-000.mkv");
        var encode = await runner.RunAsync(ffmpeg, [
            "-hide_banner", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=1",
            "-c:v", "libx264", "-y", segmentPath,
        ]);
        Assert.True(encode.Succeeded, encode.StandardError);

        await workspace.WriteManifestAsync(new SessionManifest(
            "session-killed",
            "SF1000000001",
            "device-1",
            "2026-09-16T10:30:00+08:00",
            [new SegmentManifest(0, "segment-000.mkv", "2026-09-16T10:30:00+08:00", "2026-09-16T10:31:00+08:00")]));

        // 重启
        await using var services = DesktopServices.Create(layout, ffmpeg, playbackPort: FreePort());
        var report = await services.StartAsync();

        // 1. 孤儿被收尾了
        Assert.Equal(1, report.RecoveredCount);
        Assert.Equal(0, report.FailedCount);

        // 2. 收尾走的是一条路径：停录原因是「进程被杀」
        Assert.Equal(StopReason.ProcessKilled, Assert.Single(report.OrphanOutcomes).Reason);

        // 3. 成品进了索引
        var entries = await services.Index.LoadAllAsync();
        var entry = Assert.Single(entries);
        Assert.Equal("SF1000000001", entry.Waybill.Value);
        Assert.Equal("session-killed", entry.SessionId);

        // 4. 成品文件真的在归档目录里，而且能检索到
        var hits = await services.Search.SearchAsync(new Core.Search.RecordingQuery
        {
            WaybillText = "SF1000000001",
        });
        Assert.Single(hits);
    }
}
