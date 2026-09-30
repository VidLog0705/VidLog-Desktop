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
/// <para>
/// ⚠️ 它也会起回放服务（真的 <c>HttpListener</c>），所以与回放那些测试
/// **同一个集合** —— 串行跑，免得两边挑到同一个端口（见
/// <see cref="HttpListenerCollection"/>）。
/// </para>
/// </remarks>
[Collection(HttpListenerCollection.Name)]
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
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（2026-09-27 实测：会话还在
            // 收尾时删含 `.ass` / `.mkv` 的临时目录）。清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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

    /// <summary>
    /// 钉住「组合根真的把日志器递下去了」—— **每一个边界**都得拿到它。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>为什么不能靠行为测试</b>：<c>BoundaryLoggingTests</c> 是**直接构造**
    /// 那几个类的（传自己的 logger），所以它证明的是「这些类会记」，
    /// **不是「生产上有人把 logger 递给它们」**。这一点是试出来的：
    /// 把 <c>DesktopServices</c> 里那三处 <c>logger</c> 参数删掉，
    /// 那几条用例**照样全绿**。
    /// </para>
    /// <para>
    /// 与 §«装配的最后一跳» 同一类毛病：零件齐了、各自都测过，**接起来那一步没人看**。
    /// 现在把三处调用形态钉住，任何一处被摘掉都会红。
    /// </para>
    /// <para>
    /// ⚠️ 天花板与另几条绊线相同：只挡「删了 / 改了」，挡不住「改成一条不跑的路径」。
    /// </para>
    /// </remarks>
    [Fact]
    public void 组合根把日志器递给了每个边界()
    {
        var path = Path.Combine(
            RepoRoot(), "src", "VidLog.Desktop.Core", "Configuration", "DesktopServices.cs");

        var code = string.Join(
            '\n',
            File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//")));

        // ① FFmpeg 的每一次调用（一处覆盖 remux / 解码校验 / 编码器探测）
        Assert.Contains("new SystemProcessRunner(logger)", code, StringComparison.Ordinal);

        // ② 收尾（I9 的唯一落点）。
        // ⚠️ 2026-09-27 起它还接一个 relay —— 归档层那一份的**发布点就在这条路上**。
        // 只到 `logger);` 的话，把 relay 接掉（改回 `logger);`）这条绊线照样绿，
        // 而表现是「设了 NAS、文件却没发过去」（踩坑 #13 的又一张脸）。
        //
        // ⚠️ 2026-09-30：第一个参数从 `layout.ArchiveRoot`（一个字符串）变成了
        // `locations`（`StorageLocations`，设计图 `_43` 的多磁盘）——
        // 录像可能落在用户配的**任意一块盘**上，而收尾得知道往哪块写。
        // 绊线要挡的**还是同一件事**（relay 这根线不能从收尾那条路上掉），
        // 所以跟着改文本、不改口径。
        Assert.Contains("locations,\n            logger,\n            relay);", code.Replace("\r\n", "\n"),
            StringComparison.Ordinal);

        // ③ 入网决策（安全事件）+ **机位闸门**（`04-许可设计.md` §5.1）。
        //
        // ⚠️ 2026-09-27 起这处多了一根线：`seatLimit`。少了它的表现**完全看不见** ——
        // 手机端照样接得进来，只是**不限台数**了，而那正是许可唯一管的事。
        // 所以这里连着 `seatLimit:` 一起钉住（摘掉它、或者改回只传 logger，
        // 块对不上就红）。
        Assert.Contains(
            "new DeviceRegistry(\n            layout.DevicesPath,\n            logger: logger,\n"
            + "            seatLimit: () => license?.Status.Slots ?? 0)",
            code.Replace("\r\n", "\n"),
            StringComparison.Ordinal);

        // ④ 另一个发布点：接收远端上传之后也要发一份到归档层。
        // 少这一处的话，「电脑端自己录的」会发到 NAS，而**手机传上来的**不会 ——
        // 一半有、一半没有，比两条都不发更难发现。
        //
        // ⚠️ 判据**只到 `relay: relay,`**，不带结尾那个 `)` ——
        // 2026-09-29 给它后面补了 `logger:` 之后，原来那句（含 `)`）
        // 就不再匹配、这条绊线**红了**。绊线本身是对的（说明它真的在挡），
        // 但它该挡的是「relay 这根线被摘掉」，不是「末尾多了一个参数」。
        Assert.Contains("relay: relay,", code, StringComparison.Ordinal);

        // ⑤ 接收上传那一处也要拿 logger —— 它里面有一次**删目录**
        // （`CleanupIncomingAsync`），而删不掉时原来一个字都不留。
        Assert.Contains("logger: logger);", code, StringComparison.Ordinal);

        // ⑥ 工作区（`RecordingWorkspace`）。⚠️ 这一处最要紧：
        // 「读不出来的会话」是本仓**唯一会丢证据**的方向，而它是唯一的留痕出口。
        // 2026-09-29 审计查出来：那条路原来一声不吭。
        Assert.Contains("new RecordingWorkspace(layout.WorkspaceRoot, logger)", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 钉住「界面上那些**改用户数据**的动作也留痕」（`AGENTS.md` §6 + §6.1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 2026-09-29 审计查出来的缺口：检索窗里那个「锁定」写一个标签，
    /// 而那个标签**直接决定那条录像会不会被清理**（§3.5.3② 的硬豁免）——
    /// 而成功路径只改了一行界面文案，事后没人能回答「这条是谁、什么时候锁的」。
    /// </para>
    /// <para>
    /// 为什么只能看源码文本：App 层没有测试工程（与上面那几条同一条理由）。
    /// </para>
    /// <para>
    /// ⚠️ 天花板同另几条：只挡「删了 / 改了」，挡不住「改成一条不跑的路径」。
    /// </para>
    /// </remarks>
    [Fact]
    public void 界面上的锁定动作也留痕()
    {
        var app = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App");

        var host = File.ReadAllText(Path.Combine(app, "AppHost.cs"));
        var window = File.ReadAllText(Path.Combine(app, "SearchWindow.xaml.cs"));

        // ① 窗口拿得到那个出口（它是界面层唯一能记日志的通道）。
        Assert.Contains(
            "public void Log(LogLevel level, string category, string message)",
            host, StringComparison.Ordinal);

        // ② 锁定那一步真的记了，而且**成功与失败各一条** ——
        //    失败只留在界面上会被用户划走，而那次锁定其实没生效。
        Assert.Contains("_host.Log(", window, StringComparison.Ordinal);
        Assert.Contains("\"锁定\"", window, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(window, @"_host\.Log\(").Count);
    }

    /// <summary>
    /// 钉住「界面调 Core 那些 **logger 可选** 的入口时真的传了」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 那些构造点的 logger 是**可选**参数（默认 `NullLogger`）⇒
    /// **不传就静默不落盘**，而编译器一个字都不会说。
    /// 2026-09-29 需求方问「日志是否是按标准做的」时逐个核对调用点，
    /// 才发现**设置窗那两处漏了** —— 枚举设备失败时日志里什么都没有。
    /// </para>
    /// <para>
    /// 为什么只能看源码文本：App 层没有测试工程（与上面几条同一条理由）。
    /// </para>
    /// <para>
    /// ⚠️ 天花板同另几条：只挡「删了 / 改了」，挡不住「改成一条不跑的路径」。
    /// </para>
    /// </remarks>
    [Fact]
    public void 界面调设备枚举时真的把日志器传了()
    {
        var app = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App");

        var settings = File.ReadAllText(Path.Combine(app, "SettingsWindow.xaml.cs"));
        var host = File.ReadAllText(Path.Combine(app, "AppHost.cs"));

        // ① 出口真的存在（界面层唯一能拿到 logger 的地方）。
        Assert.Contains("public IAppLogger Logger => _logger;", host, StringComparison.Ordinal);

        // ② 设置窗那两处 —— **2026-09-29 实测漏掉的就是它们**。
        Assert.Contains(
            "DshowDevices.ListVideoAsync(_host.Services.FfmpegPath, _host.Logger)",
            settings, StringComparison.Ordinal);
        Assert.Contains(
            "DshowDevices.ListAudioAsync(_host.Services.FfmpegPath, _host.Logger)",
            settings, StringComparison.Ordinal);

        // ③ 组合根那两处。
        Assert.Contains(
            "DshowDevices.ListVideoAsync(services.FfmpegPath, logger, cancellationToken)",
            host, StringComparison.Ordinal);
        Assert.Contains(
            "DshowDevices.ListAudioAsync(services.FfmpegPath, logger, cancellationToken)",
            host, StringComparison.Ordinal);
    }

    /// <summary>
    /// 钉住「时长兜底的**询问**在界面上真的有出路」（规格 §3.3.4）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规格原话：「**交互**：语音提示 + 屏幕按钮，不用扫码」。
    /// 也就是说 Core 光会「问」不算做完 —— 屏幕上得**真有那两个按钮**、
    /// 而且**真的把答案转回协调器**，否则用户看得见问题却答不了。
    /// </para>
    /// <para>
    /// ⚠️ <b>为什么只能看源码文本</b>：App 层没有测试工程（与
    /// <c>入网二维码在界面上真的有出路</c>、<c>组合根把日志器递给了三处边界</c>
    /// 同一个理由）。这正是 §28 / §35.1 记的那类毛病 —— 零件齐了、没人接。
    /// </para>
    /// <para>
    /// ⚠️ 天花板同另几条：只挡「删了 / 改了」，挡不住「改成一条不跑的路径」。
    /// 还有一处文本挡不住的：④ 只能证明 <c>App</c> 里**有**那个 case 与那句
    /// <c>Speak</c>，证明不了「Speak 就在那个 case 里面」。
    /// </para>
    /// </remarks>
    [Fact]
    public void 时长兜底的询问在界面上真的有出路()
    {
        var root = RepoRoot();
        var app = Path.Combine(root, "src", "VidLog.Desktop.App");

        var xaml = File.ReadAllText(Path.Combine(app, "MainWindow.xaml"));
        var window = File.ReadAllText(Path.Combine(app, "MainWindow.xaml.cs"));
        var appCode = File.ReadAllText(Path.Combine(app, "App.xaml.cs"));

        // ① 屏幕上那两个按钮真的在，而且接着处理函数。
        Assert.Contains("x:Name=\"DurationStopButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DurationContinueButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnDurationStop\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnDurationContinue\"", xaml, StringComparison.Ordinal);

        // ② 两个处理函数真的把答案**转回协调器** —— 少了这一句，按钮就是摆设。
        Assert.Contains("AnswerDurationPrompt(continueRecording: false)", window, StringComparison.Ordinal);
        Assert.Contains("AnswerDurationPrompt(continueRecording: true)", window, StringComparison.Ordinal);

        // ③ 那条询问真的会点亮询问条（不发通知的话按钮永远藏着）。
        Assert.Contains("CoordinatorNoticeKind.DurationPrompt", window, StringComparison.Ordinal);

        // ④ 语音那一半 —— 规格要的是「语音提示」+ 按钮**两条**，
        //    而这个窗口多半收在托盘里：只听得到声音，看不到按钮。
        Assert.Contains("case CoordinatorNoticeKind.DurationPrompt:", appCode, StringComparison.Ordinal);
        Assert.Contains("Speech.Speak(notice.Message)", appCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// 钉住「锁定在界面上真的有出路」（规格 §3.6.5）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规格只有两行：「用户可给证据打标记、可锁定」+「**锁定后永不被自动清理**」。
    /// ⚠️ 而这条一直是**只有读的那一半**：清理判定读 <see cref="LabelKeys.Locked"/>
    /// （`CleanupTests` 覆盖得很全），而**没有任何地方写它** ——
    /// 那条硬豁免**结构性地走不到**：用户没有任何办法把一条纠纷录像保住，
    /// 保留期一到本机那份就被清了。
    /// </para>
    /// <para>
    /// ⚠️ 为什么只能看源码文本：App 层没有测试工程（与上面几条同一个理由）。
    /// 「清理会读这个标签」有测试守着，但**「有人能把它写下去」没有** ——
    /// 缺的正是这一跳。
    /// </para>
    /// </remarks>
    [Fact]
    public void 锁定在界面上真的有出路()
    {
        var root = RepoRoot();
        var app = Path.Combine(root, "src", "VidLog.Desktop.App");

        // ⚠️ 2026-09-28：检索与回放拆成了独立的 `SearchWindow`（主窗照设计图
        // 改成「录制台」）。这一整页连同锁定按钮搬到了那个窗里 ——
        // **断言原文一字未改，只是读的文件换了**。
        var xaml = File.ReadAllText(Path.Combine(app, "SearchWindow.xaml"));
        var window = File.ReadAllText(Path.Combine(app, "SearchWindow.xaml.cs"));

        // ① 那一列真的在，而且接着处理函数、把 evidenceId 带在按钮上。
        Assert.Contains("Click=\"OnToggleLock\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"{Binding EvidenceId}\"", xaml, StringComparison.Ordinal);

        // ② 处理函数真的**把标签写下去** —— 少了这一句，按钮就是个摆设。
        Assert.Contains("Labels.SetAsync(", window, StringComparison.Ordinal);
        Assert.Contains("LabelKeys.Locked", window, StringComparison.Ordinal);

        // ③ 判据用的是**与清理判定同一个函数**。
        // ⚠️ 在界面里另写一份（比如直接比 `== "true"`）会漏掉判据的第三条
        // 「认不出来的值**当锁着**」，于是出现「界面显示没锁、清理却把它保留了」
        // —— 那个状态用户没机会理解。
        Assert.Contains("EvidenceLock.IsLocked(", window, StringComparison.Ordinal);

        // ⚠️ **天花板（实测出来的，不是推测）**：③ 只能证明「界面里**有人**用那个
        // 共享判据」，**挡不住「只在其中一处用了它、另一处自己解析」**。
        // 做法是：把列表那一列与按钮动作**两处都**改成自己解析，③ 才红；
        // 只改按钮那一处时它**照样绿**（2026-09-27 实测）。
        //
        // 而「两处判据不一致」恰恰是这个坑最现实的形态 —— 界面上那一格写着
        // 「锁定」、点下去却是解锁。真要根治得让 App 层可测（见类注释里那条）；
        // 在那之前这条绊线的价值是「别把这一跳整体摘掉」。
    }

    /// <summary>
    /// 钉住「改名请求在界面上真的有出路」（规格 §3.4.5 ③）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 需求方 2026-09-24 原话：「……如需要再次更改，**需要电脑端同意才能更改**。」
    /// ⇒ 「同意」这个动作**只发生在界面上**：没有人弹那个窗，这条就等于没做
    /// —— 而手机在另一头一直等（它有等待上限，到点就放弃了）。
    /// </para>
    /// <para>
    /// ⚠️ App 层没有测试工程，所以这里只能看源码文本（与上面几条同一个理由）。
    /// </para>
    /// </remarks>
    [Fact]
    public void 改名请求在界面上真的有出路()
    {
        var root = RepoRoot();
        var app = Path.Combine(root, "src", "VidLog.Desktop.App");

        var window = File.ReadAllText(Path.Combine(app, "MainWindow.xaml.cs"));

        // ① 有人**取**待批准的改名请求，而且会**决定**它。
        Assert.Contains("PendingRenamesAsync()", window, StringComparison.Ordinal);
        Assert.Contains("DecideRenameAsync(", window, StringComparison.Ordinal);

        // ② 那个轮询**真的起了** —— 少了 `Start()`，弹窗逻辑写得再对也永远不跑
        //    （而它在测试里完全看不出来：窗关着就永远收不到请求）。
        Assert.Contains("_renameWatch.Start()", window, StringComparison.Ordinal);

        // ③ 路由真的注册了（Core 那边）。
        var server = File.ReadAllText(Path.Combine(
            root, "src", "VidLog.Desktop.Core", "Web", "PlaybackServer.cs"));

        Assert.Contains("\"/api/v1/enroll/rename\"", server, StringComparison.Ordinal);
    }

    /// <summary>
    /// 钉住「错误扫描真的接上了」—— <c>AppHost</c> 必须把 <c>ScanErrorLog</c>
    /// 交给协调器。
    /// </summary>
    /// <remarks>
    /// 只能看源码文本的理由与上面几条相同（App 层没有测试工程）。
    /// 这条挡的失效是**已经发生过**的同一类：零件写好了、测过了，**没人接**。
    /// 与那几条不同的是，它这次是**规格欠账**：§6.1 点名要这条记录，
    /// 而在此之前两端都没实现。
    /// </remarks>
    [Fact]
    public void AppHost_把错误扫描交给协调器()
    {
        var path = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App", "AppHost.cs");

        var code = string.Join(
            '\n',
            File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//")));

        Assert.Contains("new ScanErrorLog(layout.ScanErrorsPath)", code, StringComparison.Ordinal);
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
