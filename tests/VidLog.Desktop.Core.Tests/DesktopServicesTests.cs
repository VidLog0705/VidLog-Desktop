using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Upload;
using VidLog.Desktop.Core.Web;

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
    public async Task 发布端写的那本已归档账_清理端真的读得到()
    {
        // ⚠️ **T18 的收口检查**：缺陷原来断的就是这一截 ——
        // 桌面自己发布成功之后**一个可持久化的锚都没留下**，于是自录的内容
        // 永远被判成「唯一副本」，设置里那个保留期对它根本不成立、**盘满只是时间问题**。
        //
        // ⚠️ 这条刻意**走装配出来那一套对象图**（`DesktopServices.Create` 出来的
        // `ArchiveRelay` 与 `Cleanup`），而不是自己 new 两个部件 ——
        // 记账端读哪个文件、清理端读哪个文件是**两处**写的，只有把它们接起来跑一遍
        // 才能证明是**同一个文件**。各测各的部件时，这个问题看不见（两边都会过）。
        using var dir = new TempDir();
        var layout = new DataLayout(dir.Dir("data"));
        var nas = dir.Dir("nas");
        Directory.CreateDirectory(nas);

        await using var services = DesktopServices.Create(
            layout,
            playbackPort: null,
            archive: new ArchiveTarget(ArchiveBackendKind.Nas, nas));

        Assert.NotNull(services.ArchiveRelay);

        // 本机那一份得真在盘上 —— 发布要有东西可发。
        var relative = RelativePath.Parse("2026/09/27/SF1000000001/e-000.mp4");
        var local = System.IO.Path.Combine(layout.ArchiveRoot, relative.Value);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(local)!);
        await File.WriteAllTextAsync(local, "x");

        var endedAt = DateTimeOffset.Now.AddDays(-40);
        await services.Index.AddAsync(new RecordingEntry(
            "e-000",
            "sess-1",
            WaybillNumber.Parse("SF1000000001"),
            endedAt.AddMinutes(-5),
            endedAt,
            TimeSpan.FromMinutes(5),
            relative,
            ContentHash.Parse(new string('a', 64)),
            "device-1"));

        var published = await services.ArchiveRelay!.PublishAsync("e-000", relative, local);

        Assert.True(published.Published, published.FailureReason);

        // 盘快满了 ⇒ 该清的就该清得上。**自录的这条必须出现在候选里。**
        var plan = await services.Cleanup.PreviewBySpaceAsync(
            minFreeBytes: 1L << 40, freeBytes: 0, DateTimeOffset.Now);

        Assert.Equal("e-000", Assert.Single(plan!.Candidates).Entry.EvidenceId);
    }

    [Fact]
    public async Task 发布失败的欠账跨重启仍看得见_补上之后才消失()
    {
        // ⚠️ **T23-A 的验收**（清单原话：「让发布失败 → 重启 → 断言设置页**仍显示**那条失败」）。
        // ⚠️ 这里断言到 `DesktopServices.ArchiveFailures` 为止 —— 设置页那一步在 WPF 外壳，
        // 那个工程没有测试（T27②），只能手验。这一条管的是**它读的那个东西**跨不跨得过重启。
        using var dir = new TempDir();
        var layout = new DataLayout(dir.Dir("data"));

        // 拿一个**文件**当归档根：发布必然失败（`Directory.CreateDirectory` 就会抛）
        var blocker = System.IO.Path.Combine(dir.Path, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x");

        var relative = RelativePath.Parse("2026/10/06/SF1000000001/e-000.mp4");
        var local = System.IO.Path.Combine(layout.ArchiveRoot, relative.Value);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(local)!);
        await File.WriteAllTextAsync(local, "x");

        // ── 第一趟：发布失败 ───────────────────────────────────────
        await using (var first = DesktopServices.Create(
            layout, playbackPort: null,
            archive: new ArchiveTarget(ArchiveBackendKind.Nas, blocker)))
        {
            Assert.NotNull(first.ArchiveRelay);

            var published = await first.ArchiveRelay!.PublishAsync("e-000", relative, local);
            Assert.False(published.Published);

            Assert.Single(first.ArchiveFailures.Outstanding);
        }

        // ── 重启：另装配一套（内存全丢、文件还在），换成能写进去的归档层 ──
        var nas = dir.Dir("nas");
        Directory.CreateDirectory(nas);

        await using var second = DesktopServices.Create(
            layout, playbackPort: null,
            archive: new ArchiveTarget(ArchiveBackendKind.Nas, nas));

        // ⚠️ 这一句就是「重启后设置页仍显示」在 Core 那一层的等价物。
        var carried = Assert.Single(second.ArchiveFailures.Outstanding);
        Assert.Equal("e-000", carried.EvidenceId);

        // 这一趟里 `ArchiveRelay.LastFailure` 是 null（内存是空的）——
        // 所以只看它就什么都看不见，这正是缺陷的形状。
        Assert.Null(second.ArchiveRelay!.LastFailure);

        // ── 补上：欠账撤掉，而且**撤掉这件事也落盘** ────────────────
        var retry = await second.ArchiveRelay.PublishAsync("e-000", relative, local);
        Assert.True(retry.Published, retry.FailureReason);
        Assert.Empty(second.ArchiveFailures.Outstanding);

        // ── 再重启一次：不能再冒出来 ────────────────────────────────
        await using var third = DesktopServices.Create(
            layout, playbackPort: null,
            archive: new ArchiveTarget(ArchiveBackendKind.Nas, nas));

        Assert.Empty(third.ArchiveFailures.Outstanding);
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
            // ⚠️ 走 ReadSplit：`MainWindow` 已拆成 partial，`MainWindow.xaml.cs` 只是它的
            // 主文件（`EnrollWindow` 还没拆，扫出来仍然只有它自己那一个文件）。
            var code = ReadSplit(
                Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App"),
                file.Replace(".xaml.cs", string.Empty));

            Assert.True(
                code.Contains(fragment, StringComparison.Ordinal),
                $"{file} 里没有 {fragment} —— {why}。见 docs/实现决策.md「装配的最后一跳」。");
        }
    }

    /// <summary>
    /// 钉住「许可那句话只有一份」（T30）—— 侧栏与设置页都必须走
    /// <see cref="LicenseStatus.SummaryText"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 修之前这句话在 <c>StatusSummaries</c> 与设置页的许可页里**各写了一份**，
    /// 而设置页那份把机位数写死成「4 机位」（不读 <c>Slots</c>）。
    /// 那句话本身对不对由 <c>LicenseStatusTextTests</c> 管；这条管的只是
    /// **两个渲染点有没有用它** —— 那是单测够不着的那一跳。
    /// </para>
    /// <para>
    /// ⚠️ <b>天花板</b>（与上面几条同）：只挡「那句话又被抄了一遍」，
    /// 挡不住「引对了 `SummaryText`、但把它接在一句错话后面」。
    /// </para>
    /// <para>
    /// ⚠️ 那条 `Assert.True`（要求**引用到了**）不是凑数：只留「不许出现字面量」
    /// 那半边的话，**扫到零个文件**也会绿 —— 而 `ReadSplit` 不递归，
    /// 目录写浅一层就正好是这个形状（2026-10-07 写这条时当场撞到过）。
    /// </para>
    /// </remarks>
    [Fact]
    public void 许可那句话只有一份_两个渲染点都走Core()
    {
        var app = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App");

        // 两个渲染点：侧栏（`StatusSummaries`，主窗三个显示位都走它）
        // 与设置页的许可页（`SettingsWindow.*` 那一组 partial）。
        //
        // ⚠️ 目录**必须写全**：`ReadSplit` 只扫**顶层**（不递归），
        // 而 `StatusSummaries.cs` 在 `Platform/` 下 —— 少一层它扫到零个文件，
        // 于是断言拿到空串、报的却是「没有走 SummaryText」，指错地方。
        var sites = new (string Folder, string Stem, string What)[]
        {
            (Path.Combine(app, "Platform"), "StatusSummaries", "侧栏那句"),
            (app, "SettingsWindow", "设置页那句"),
        };

        foreach (var (folder, stem, what) in sites)
        {
            Assert.True(
                ReadSplit(folder, stem).Contains("SummaryText", StringComparison.Ordinal),
                $"{what}没有走 SummaryText —— 许可那句话又被抄了一份。见 T30。");
        }

        // ⚠️ 整个 App 工程此刻**一处字面量都没有**（2026-10-07 核过），
        // 所以这两条不会因为不相干的代码假红。
        foreach (var literal in new[] { "试用中：还剩", "已激活：允许接入" })
        {
            foreach (var (folder, stem, _) in sites)
            {
                Assert.False(
                    ReadSplit(folder, stem).Contains(literal, StringComparison.Ordinal),
                    $"{stem} 里又出现了写死的「{literal}」—— 那句话只该在 Core 的 SummaryText 上。见 T30。");
            }
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
    /// 钉住「取景识码那个对象真的挂到协调器上了」（规格 §3.2.1 / §3.1.3）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两处失效都**编译得过**，而且都一声不响：
    /// ① 少了 <c>coordinator.Prerecord = prerecord;</c> —— 对象建出来、两个事件也接好了，
    /// 而协调器从来不碰它。表现是「【开始工作】之后相机没反应、放到画面里也不开录、
    /// 预录缓冲也没有」，界面上没有原因可查（今天的 `_prerecord` 目录还会是空的）。
    /// ② 少了 <c>prerecord.Failed +=</c> —— 起来之后死掉的原因（`device in use`、
    /// 地址打不开）就没人往界面上说了，而那正是 I3 要挡的那件事。
    /// </para>
    /// <para>
    /// ⚠️ 这一处与「组合根真的把日志器递下去了」不同：那边的洞是**可选参数**
    /// （不传就静默），这里的洞是**赋值**（不赋也静默）。两者编译器都不管。
    /// 文本绊线只挡「有人把这一行删了/注释了」—— 见 §«装配的最后一跳»。
    /// </para>
    /// </remarks>
    [Fact]
    public void 取景识码真的挂到协调器上()
    {
        var code = string.Join(
            '\n',
            File.ReadAllLines(Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App", "AppHost.cs"))
                .Where(line => !line.TrimStart().StartsWith("//")));

        Assert.Contains("coordinator.Prerecord = prerecord;", code, StringComparison.Ordinal);
        Assert.Contains("prerecord.Failed += ", code, StringComparison.Ordinal);
        Assert.Contains("prerecord.Scanned += ", code, StringComparison.Ordinal);
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
    /// ⚠️ 2026-10-02 在这个窗口里补了**第三条出口**：回放打不开。它不改数据，
    /// 但它是 §6 表里的「异常」，而且原因（这台机器缺哪个解码器）
    /// **换台机器就复现不出来** —— 只能靠当时记下来的那一句。
    /// </para>
    /// <para>
    /// ⚠️ 2026-10-05 在这个窗口里补了**第四条出口**（T14）：日历读不出「哪几天有录像」。
    /// 它不改数据，但那是检索窗里唯一一处**会静默失败**的地方 —— 日子表读不出来
    /// 就只是日历全白，而检索本身照常。用户会说「你们这个日历怎么全白的」，
    /// 那时只有这条日志能回答是读索引读失败了、还是那几天真的没录。
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

        // ③ 界面上的**别的事故**也留痕（2026-10-02 补）：回放打不开时那句原因
        //    来自**这台机器**缺哪个解码器 —— 换一台机器就复现不出来，
        //    所以当时不记下来，事后无从查起。
        Assert.Contains("\"回放\"", window, StringComparison.Ordinal);

        // ④ 日历那张日子表读不出来时也要留痕（2026-10-05 补，T14）：它是这个窗口里
        //    唯一一处**会静默失败**的地方（全白的日历，而检索照常）。
        Assert.Contains("\"日历\"", window, StringComparison.Ordinal);

        // 数一数：锁定成功、锁定失败、回放失败、日历读不出日子 —— **恰好四条**。
        // ⚠️ 这是个精确计数（原为 2）：少一条说明「成功/失败各一条」被删了，
        // 多一条说明有人新加了一个出口却没在这里登记。
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(window, @"_host\.Log\(").Count);
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

        // ⚠️ 设置窗按页签拆成了若干 partial ⇒ 走 `ReadSplit`（2026-10-06，T26①）。
        var settings = ReadSplit(app, "SettingsWindow");
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
        // ⚠️ 2026-10-01 又加了一条：**多画面那一族**（规格 §3.8）也是
        // 「logger 可选」的构造点。这里钉三件事：组合根真的传了、
        // 界面真的把 `_host.Logger` 递下去了、别的地方没有偷偷 new 一个。
        Assert.Contains(
            "new LiveDirectory(logger: logger)",
            File.ReadAllText(Path.Combine(RepoRoot(), "src", "VidLog.Desktop.Core", "Configuration", "DesktopServices.cs")),
            StringComparison.Ordinal);

        // ④ 「哪几条没发上去」那本账（T23-A）也是「logger 可选」的构造点 ——
        //    它的读不出/写不上都要靠这条通道才看得见，不传就是静默的。
        Assert.Contains(
            "new ArchiveFailureLog(layout.ArchiveFailurePath, logger)",
            File.ReadAllText(Path.Combine(RepoRoot(), "src", "VidLog.Desktop.Core", "Configuration", "DesktopServices.cs")),
            StringComparison.Ordinal);
        var mainWindow = ReadSplit(app, "MainWindow");
        Assert.Contains("new MultiViewWindow(", mainWindow, StringComparison.Ordinal);
        // ⚠️ 数**几处**，而不是钉整段调用文本：2026-10-03 多画面那一族的构造点变了
        //（机位改成「取机位的函数 + 建一格的函数」，不再是开窗那一刻的快照），
        // 整段钉死的话，一次排版改动就会让这条测试红，而它真正要守的是
        // 「窗口与每一格都把 `_host.Logger` 递下去了」—— 正好两处。
        Assert.True(
            CountOf(mainWindow, "logger: _host.Logger)") >= 2,
            "多画面窗口与格子里那一路都要把 _host.Logger 递下去（不传就是静默不落盘）");

        Assert.Contains(
            "DshowDevices.ListVideoAsync(services.FfmpegPath, logger, cancellationToken)",
            host, StringComparison.Ordinal);
        Assert.Contains(
            "DshowDevices.ListAudioAsync(services.FfmpegPath, logger, cancellationToken)",
            host, StringComparison.Ordinal);

        // ⑤ 清理失败也要留痕（2026-10-06 补）。核 T26① 的 diff 时发现：三处
        //    「清理没能进行」的 catch 只写界面、**不落日志** —— 已对 HEAD 原文
        //    核过，是既有欠账、不是那次引入的。
        //    ⚠️ 为什么必须落：清理是**不可逆动作**，而界面上那句话会被用户划走、
        //    窗口一关就没了 —— 事后只有日志说得清那一次为什么没清成。
        //    三处 = 设置窗【按时间清理…】+【按空间释放…】+ 启动时那次清理。
        //    ⚠️ 与上面几条同理，数**几处**而不是钉整段文本：它真正要守的是
        //    「每一处失败都留痕」，而不是日志那句话的措辞。
        Assert.True(
            CountOf(settings + mainWindow, "Log(LogLevel.Warn, \"清理\"") >= 3,
            "清理失败那三处（设置窗两颗按钮 + 启动时那次）都要往日志里留一条 —— "
                + "只写界面的话，用户划走就再也查不到那一次为什么没清成");
    }

    /// <summary>
    /// 钉住「取景框那颗时间水印，可不可信两个状态**都给颜色**」（2026-10-07 需求方拍板）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 原先只给「未校准」那一支（另一支靠 XAML 里写死的 <c>Foreground="White"</c>），
    /// 于是**校准成功之后它会一直挂着警示色** —— 那次会话里水印已经写着可信时间了，
    /// 颜色还在喊「不可信」；用户学会忽略这个颜色之后，下一次真未校准就没人看了。
    /// 而且警示色是琥珀（<c>#FFF59E0B</c>），压在明亮画面上**比白色更难读** ——
    /// 语义与可读性两头都错。
    /// </para>
    /// <para>
    /// ⚠️ 颜色改成**只在 <c>UpdatePreviewClock</c> 里给**（XAML 那颗字上不再写
    /// <c>Foreground</c>）：两处各写一份的话，「哪种状态是什么色」就有两个来源。
    /// </para>
    /// <para>
    /// ⚠️ 为什么只能看源码文本：App 层没有测试工程（与上面几条同一条理由）。
    /// 天花板也一样：只挡「又变回只管一支」，挡不住「颜色令牌选错了」；
    /// XAML 那边去掉了 <c>Foreground</c> 是**人工约定**，这条绊线管不到它（只扫 .cs）。
    /// </para>
    /// </remarks>
    [Fact]
    public void 水印那颗字_可不可信两种状态都要给颜色()
    {
        var app = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App");
        var main = ReadSplit(app, "MainWindow");

        // 哨兵：扫到的是真文件（ReadSplit 扫到零个文件时这两条会红）。
        Assert.Contains("PreviewClockText.Text =", main, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(main, "PreviewClockText.Foreground ="));

        // 两支都要在同一个三元里：校准了恢复白色，没校准是警示色。
        Assert.Contains("Brushes.White", main, StringComparison.Ordinal);
        Assert.Contains("FindResource(\"Warning\")", main, StringComparison.Ordinal);
    }

    /// <summary>
    /// 钉住「导入那句的时长走**人话总量**，不走等宽的计时那一套」（2026-10-07 需求方拍板）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <c>Display.Timer</c>（等宽 <c>HH:MM:SS</c>）的存在理由是**每一秒都在变**的地方
    /// —— 位数不变，数字才不左右跳。导入结果那句是**一次性写上去的总量**，用它就是把
    /// 两种东西混着用（<c>Display.Duration</c> 的文档里写着「界面上用它的地方全是给人看的总量」）。
    /// </para>
    /// <para>
    /// ⚠️ 补一句为什么不是「无所谓」：那句话里紧挨着**证据号与日期**，
    /// <c>00:01:23</c> 读起来像**时刻**，<c>1 分 23 秒</c> 不会；而且人话更短（6 字 vs 8 字）。
    /// </para>
    /// <para>
    /// ⚠️ 检索列表那一行（<c>SearchWindow</c>）仍按设计图写 <c>1:23</c> —— 那是**另一处**，
    /// 这条绊线管不到、也不该管。
    /// </para>
    /// </remarks>
    [Fact]
    public void 导入那句的时长走人话而不是等宽的计时()
    {
        var app = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App");
        var import = ReadSplit(app, "ImportWindow");

        Assert.Contains("已导入（证据", import, StringComparison.Ordinal);
        Assert.Contains("Display.Duration(result.Duration.Value)", import, StringComparison.Ordinal);
        Assert.DoesNotContain("Display.Timer", import, StringComparison.Ordinal);
    }

    /// <summary>
    /// 钉住「取不到公网时间那一次**也要留痕**」（2026-10-07 需求方拍板补的）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 与「清理没能进行」那三处同一个口径：那条 <c>catch</c> 原先**只写界面**
    /// （只在设置页里，用户一关窗就没了），而「校不上时间」正是用户会来报的那种事 ——
    /// 未校准就录不了（规格 §3.6.4），那时日志里必须找得到那一次的原话。
    /// </para>
    /// <para>
    /// ⚠️ 两条断言**成对**：界面那句是「用户看得见」，兼作「扫到的是真文件」的哨兵
    /// （<c>ReadSplit</c> 扫到零个文件时它会红）。
    /// </para>
    /// </remarks>
    [Fact]
    public void 取不到公网时间时也要留痕()
    {
        var app = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App");
        var settings = ReadSplit(app, "SettingsWindow");

        Assert.Contains("取不到公网时间：", settings, StringComparison.Ordinal);
        Assert.Contains("Log(LogLevel.Warn, \"校时\"", settings, StringComparison.Ordinal);
    }

    /// <summary>
    /// 钉住「主按钮的白字压在强调色上**要达标**」（2026-10-07 合并两套蓝时定的）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 这一条是**算出来的**，不是抄设计图：<c>PrimaryButton</c> 是白字、字号 13
    /// （正文档），而 WCAG AA 对正文档要求 **4.5:1**。合并之前桌面端**一条对比度
    /// 绊线都没有** —— 换算才发现原先主窗口那 10 处按钮（blue-500 `#3B82F6` + 白字）
    /// 只有 **3.68:1**，长期不达标而没有任何东西挡得住。
    /// </para>
    /// <para>
    /// ⚠️ 为什么值得一条绊线：这次的裁定（合并到 blue-600）**只要有人把 <c>Accent</c>
    /// 改回浅色就静默失效**，而界面上完全看不出来 —— 正是「改成一条不跑的路」那一类，
    /// 与 <c>DesktopServicesTests</c> 里那批装配绊线同一个理由。
    /// </para>
    /// <para>
    /// ⚠️ 天花板：只量 <c>Accent</c> 与白这一对。别处新加一组「浅底 + 白字」它看不到。
    /// </para>
    /// </remarks>
    [Fact]
    public void 主按钮的白字压在强调色上要达标()
    {
        var theme = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App", "Theme.xaml"));

        // 哨兵：扫到的是真文件、拿到的是真模板。搬走或改名时这里先红，
        // 而不是让下面那条断言对着一个空字符串「通过」。
        var styleAt = theme.IndexOf("x:Key=\"PrimaryButton\"", StringComparison.Ordinal);
        Assert.True(styleAt >= 0, "Theme.xaml 里找不到 PrimaryButton 模板。");

        var button = theme[styleAt..theme.IndexOf("<Style", styleAt + 1, StringComparison.Ordinal)];

        // 4.5 这个门槛的依据就是这两行：白字、13px（正文档）。哪天字号变大了，
        // 门槛可以放宽到 3:1 —— 那时这条会红，是让你回来重算，不是它坏了。
        Assert.Contains("Property=\"Foreground\" Value=\"White\"", button, StringComparison.Ordinal);
        Assert.Contains("Property=\"FontSize\" Value=\"13\"", button, StringComparison.Ordinal);

        var accent = Regex.Match(theme, "x:Key=\"Accent\"\\s+Color=\"#FF([0-9A-Fa-f]{6})\"");
        Assert.True(accent.Success, "没从 Theme.xaml 里量到 Accent 的色值。");

        var ratio = WhiteOn(accent.Groups[1].Value);

        Assert.True(
            ratio >= 4.5,
            $"主按钮是白字 13px，压在 Accent #{accent.Groups[1].Value} 上只有 {ratio:0.00}:1；"
            + "WCAG AA 对正文档要求 4.5:1。");
    }

    /// <summary>白字压在 <paramref name="hex"/>（`RRGGBB`，不带 alpha）上的 WCAG 对比度。</summary>
    private static double WhiteOn(string hex)
    {
        static double Linear(double channel) =>
            channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

        static double Luminance(string rgb) =>
            (0.2126 * Linear(Convert.ToInt32(rgb[..2], 16) / 255.0))
            + (0.7152 * Linear(Convert.ToInt32(rgb[2..4], 16) / 255.0))
            + (0.0722 * Linear(Convert.ToInt32(rgb[4..], 16) / 255.0));

        // 白是最亮的那一头，所以直接把白的 (L+0.05) 放分子。
        return 1.05 / (Luminance(hex) + 0.05);
    }

    /// <summary>
    /// 钉住「界面里引用的每一个资源键，都还有人定义」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 这一类错误**编译期查不出来**：WPF 的 <c>{StaticResource X}</c> 找不到 X 时
    /// 只在**运行时**抛 <c>XamlParseException</c>，而且是在那个窗**第一次打开**的那一秒
    /// —— 所以「删掉一个键 → 跑完测试 → 出包」可以全绿，坏在用户点开那个窗的一刻。
    /// </para>
    /// <para>
    /// ⚠️ 起这条的由头：2026-10-07 合并两套蓝时删掉了 <c>AccentStrong</c> 与
    /// <c>PrimaryButtonStrong</c> 两个键。当时确实 grep 核对过没有残留，**但那次核对
    /// 没有留下任何能失败的东西** —— 下次谁再删一个键，还是要等用户来报「这个窗打不开」。
    /// </para>
    /// <para>
    /// ⚠️ 天花板：只管界面（<c>.xaml</c>）里的引用。<c>.cs</c> 里用字符串
    /// <c>FindResource("X")</c> 取的键它看不到。
    /// </para>
    /// </remarks>
    [Fact]
    public void 界面里引用的资源键都还有人定义()
    {
        // ⚠️ 有意**不递归**：App 的 .xaml 全在顶层（14 个），递归会把 obj/ 下
        // 生成物也扫进来。（2026-10-07 实测：子目录里一个 .xaml 都没有。）
        var files = Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App"), "*.xaml").ToList();

        // 哨兵：扫到的是真文件，而不是对着一个空目录「通过」。
        Assert.True(files.Count >= 10, $"只扫到 {files.Count} 个 .xaml，路径大概不对。");

        var text = string.Join('\n', files.Select(File.ReadAllText));

        var defined = Regex.Matches(text, "x:Key=\"([A-Za-z0-9_]+)\"")
            .Select(one => one.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // 键名限定成标识符，于是 `{StaticResource {x:Type Button}}` 这种
        // 标出类型的写法**天然不匹配**，不会产生空名字的假阳性。
        var missing = Regex.Matches(text, "StaticResource\\s+([A-Za-z0-9_]+)")
            .Select(one => one.Groups[1].Value)
            .Where(one => !defined.Contains(one))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(one => one, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "这些键被界面引用了，但 App 的 .xaml 里没人定义（运行时会抛 XamlParseException）："
            + string.Join("、", missing));
    }

    /// <summary>
    /// 钉住「设置校验不过时，那一句**既说给用户、也落进日志**」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 为什么必须落日志：校验不过时窗**不关**，用户多半当场改一格再存 ——
    /// 于是「他第一次填的是什么」界面上再也看不到了，而那一句里就有那个越界值
    /// —— 这正是 2026-10-07 那个缺陷（保留期手输 <c>9999</c> 把整份设置打回默认值）
    /// 最缺的一环：用户即使来报「我设的东西全变回去了」，日志里也一个字都没有。
    /// </para>
    /// <para>
    /// ⚠️ <b>为什么只能看源码文本</b>：App 层没有测试工程（与上面几条同一个理由）。
    /// 天花板也一样：只挡「删了 / 改了」，挡不住「改成一条不跑的路径」。
    /// </para>
    /// <para>
    /// ⚠️ 两条断言**成对**：`SettingsStatus.Text` 那条是「用户看得见」，
    /// 它同时兼作「扫到的是真文件」的哨兵（`ReadSplit` 扫到零个文件时它会红）——
    /// 只留日志那一条的话，文件名哪天改了，这条会**空着绿**。
    /// </para>
    /// </remarks>
    [Fact]
    public void 设置校验不过时_那一句既说给用户也落进日志()
    {
        var settings = ReadSplit(
            Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App"), "SettingsWindow");

        Assert.Contains("SettingsStatus.Text = problem;", settings, StringComparison.Ordinal);
        Assert.Contains(
            "_host.Log(LogLevel.Warn, \"设置\", problem);", settings, StringComparison.Ordinal);
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
        var window = ReadSplit(app, "MainWindow");
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

        var window = ReadSplit(app, "MainWindow");

        // ① 有人**取**待批准的改名请求，而且会**决定**它。
        Assert.Contains("PendingRenamesAsync()", window, StringComparison.Ordinal);
        Assert.Contains("DecideRenameAsync(", window, StringComparison.Ordinal);

        // ② 那个轮询**真的起了** —— 少了 `Start()`，弹窗逻辑写得再对也永远不跑
        //    （而它在测试里完全看不出来：窗关着就永远收不到请求）。
        Assert.Contains("_renameWatch.Start()", window, StringComparison.Ordinal);

        // ③ 路由真的注册了（Core 那边）。
        var server = ReadSplit(
            Path.Combine(root, "src", "VidLog.Desktop.Core", "Web"), "PlaybackServer");

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

    /// <summary>
    /// 钉住「XAML 里一个硬编码颜色都没有 —— 颜色只许从 <c>Theme.xaml</c> 里取」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么要有它：2026-10-04 逐个文件核过一遍，散在外面的 hex 是**设计图那阵子
    /// 留下来的**（<c>WizardWindow.xaml</c> 两处、<c>HelpTip.xaml</c> 一处）。
    /// 它本身是暗色主题的前置债 —— 40 处不清零，暗色就得逐个打补丁
    /// （ZoneMinder 那个反面教材就是这个形状）。
    /// </para>
    /// <para>
    /// ⚠️ <c>Theme.xaml</c> 自己**豁免**（它就是唯一的定义处）。
    /// **注释里的色值也豁免** —— 那是在说「这个令牌长得像什么」，
    /// 2026-10-04 核实时发现几个文件里的 hex 全部落在注释里，真在写代码的只有 3 处。
    /// </para>
    /// <para>
    /// ⚠️ 天花板：只挡「新写了一个裸色」，**挡不住「引用了一个错的令牌」**
    /// （比如把边框色当文字色用）。那要人看，扫文本扫不出来。
    /// </para>
    /// </remarks>
    [Fact]
    public void 界面里没有硬编码颜色()
    {
        var app = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "Theme.xaml") continue;

            var text = Regex.Replace(
                File.ReadAllText(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

            foreach (Match hit in Regex.Matches(text, "#[0-9A-Fa-f]{6,8}"))
            {
                offenders.Add($"{Path.GetFileName(file)}: {hit.Value}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "XAML 里出现了硬编码颜色，改成引用 Theme.xaml 里的令牌：\n  "
                + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// 改造清单 T1。钉住「每个对话框都按 Esc 关得掉」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// T1 的验收是「每个对话框：回车确认、Esc 取消、Tab 顺序符合阅读序」，三条里
    /// **只有 Esc 这一条扫得出来**，另外两条要人看（回车是「哪个按钮算确认」，
    /// Tab 序要看渲染出来的阅读顺序）。
    /// </para>
    /// <para>
    /// ⚠️ <b>为什么必须有这条</b>：2026-10-05 核的时候 T1 已经做了一半（4 个窗口有
    /// <c>IsDefault</c>、6 个有 <c>IsCancel</c>），而**没有任何东西在管它** ——
    /// 也就是说新加一个对话框、忘了写 Esc，谁都不会知道。而「Esc 关不掉」是
    /// 用户第二天就会撞上的那一类（T1 排第一就是这个理由）。
    /// </para>
    /// <para>
    /// ⚠️ 两条路都算数：XAML 里的 <c>IsCancel="True"</c>，或者代码里的
    /// <c>Key.Escape</c>（全屏那种要自己接住键的先例：<c>MultiViewWindow</c>）。
    /// 扫文本只能扫这两样，**接不上「用别的键关窗」**，那是这个检查的天花板。
    /// </para>
    /// <para>
    /// ⚠️ 例外是**逐个写了理由的**，不是白名单：
    /// <list type="bullet">
    /// <item><c>App.xaml</c> / <c>HelpTip.xaml</c> —— 根元素不是 <c>Window</c>（资源字典 / 用户控件），</item>
    /// <item><c>MainWindow</c> / <c>SearchWindow</c> —— 主窗，Esc 关掉整个程序是灾难，</item>
    /// <item><c>StartupWindow</c> —— 开机的启动画面，一个按钮都没有，没有可取消的动作。</item>
    /// </list>
    /// </para>
    /// </remarks>
    [Fact]
    public void 每个对话框都按_Esc_关得掉()
    {
        var app = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App");

        var exempt = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["App.xaml"] = "资源字典，根元素不是 Window",
            ["HelpTip.xaml"] = "用户控件，不是窗口",
            ["MainWindow.xaml"] = "主窗；Esc 关掉整个程序是灾难",
            ["SearchWindow.xaml"] = "主窗；同上",
            ["StartupWindow.xaml"] = "启动画面，一个按钮都没有，没有可取消的动作",
        };

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (exempt.ContainsKey(name)) continue;

            var xaml = File.ReadAllText(file);

            // 只认窗口 —— 万一以后又冒出一个资源字典/用户控件，别让它悄悄混进来。
            if (!Regex.IsMatch(xaml, @"<Window[\s>]")) continue;

            if (Regex.IsMatch(xaml, @"IsCancel=""True""")) continue;

            // 另一条路：代码里自己接住 Esc。
            var code = Path.ChangeExtension(file, ".xaml.cs");
            if (File.Exists(code)
                && Regex.IsMatch(File.ReadAllText(code), @"Key\.Escape"))
            {
                continue;
            }

            offenders.Add(name);
        }

        Assert.True(
            offenders.Count == 0,
            "下面这些窗口按 Esc 关不掉 —— 给【取消/关掉】按钮加 IsCancel=\"True\"，"
                + "或者在代码里接住 Key.Escape（两个都行，见这条用例的说明）：\n  "
                + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// 改造清单 T4。改版前 App 下 8 个窗口 XAML 里散着 31 处写死的 <c>CornerRadius</c>。
    /// 数过一遍：<b>6 出现 16 次、8 十四次、4 五次</b> —— 那是三档有角色的；
    /// 再往后就是断崖（5 / 3 / 9 / 11 各一两次），每一个都只出现在
    /// <c>ControlTemplate</c> 里。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>模板内部豁免</b>：那里的圆角是控件的<b>形状</b>，而且多半是<b>算出来的</b>
    /// （`Height / 2` 的胶囊、滚动条的槽）。给它编一个令牌，等于把「22 的一半」
    /// 抄成一个看起来可调的数。
    /// </para>
    /// <para>
    /// ⚠️ <c>Theme.xaml</c> 自己豁免（令牌定义处），与上面那条颜色绊线同一个路数。
    /// </para>
    /// </remarks>
    [Fact]
    public void 界面里没有写死的圆角()
    {
        var app = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App");
        var offenders = new List<string>();
        var missing = new List<string>();

        // 模板内部的圆角**必须是真令牌**才允许 —— 所以先把 Theme.xaml 里定义过的键收齐。
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match hit in Regex.Matches(
            File.ReadAllText(Path.Combine(app, "Theme.xaml")), @"x:Key=""(Radius[A-Za-z]*)"""))
        {
            defined.Add(hit.Groups[1].Value);
        }

        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "Theme.xaml") continue;

            var text = Regex.Replace(
                File.ReadAllText(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

            // ⚠️ 深度得按**位置**算，不能按行算：`<ControlTemplate ...>` 与
            // `</ControlTemplate>` 可能落在同一行（空模板），按行算深度就永远回不到零，
            // 后面半张文件全被误判成「模板内」—— 一个不报错的假绿。
            var depth = 0;

            foreach (Match hit in Regex.Matches(
                text, @"<ControlTemplate[ >]|</ControlTemplate>|CornerRadius=""\d[^""]*""|""\{StaticResource (Radius[A-Za-z]*)\}"""))
            {
                if (hit.Groups[1].Success)
                {
                    // ⚠️ 引用了一个**不存在的键**在 WPF 里是**运行期**才炸的
                    // （StaticResource 与类型转换都不是编译期检查），
                    // `dotnet build` 全绿也照样在启动时抛 —— 所以它值得一条绊线。
                    var key = hit.Groups[1].Value;
                    if (!defined.Contains(key))
                    {
                        missing.Add($"{Path.GetFileName(file)}: {key}");
                    }
                }
                else if (hit.Value.StartsWith("</", StringComparison.Ordinal))
                {
                    depth--;
                }
                else if (hit.Value.StartsWith("CornerRadius", StringComparison.Ordinal))
                {
                    if (depth == 0)
                    {
                        offenders.Add($"{Path.GetFileName(file)}: {hit.Value}");
                    }
                }
                else
                {
                    depth++;
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "引用了 Theme.xaml 里没有的圆角令牌（**编译能过、启动时才抛**）：\n  "
                + string.Join("\n  ", missing));

        Assert.True(
            offenders.Count == 0,
            "XAML 里出现了写死的圆角，改成引用 Theme.xaml 里的令牌"
                + "（4 → RadiusTag，6 → RadiusControl，8 → RadiusCard，4,4,0,0 → RadiusTab）：\n  "
                + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// 钉住 T9：多画面那面墙**起几路 ffmpeg 是用户说了算的**，而且档位摆得下。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 这条盯的是三处**静默失效** —— 每一处的表现都是「点了没反应 / 少了一格」，
    /// 而屏幕上看起来完全正常，测试不红就没人会知道：
    /// </para>
    /// <list type="number">
    /// <item>工具栏最大那一档 &gt; <c>MaxCells</c>：<c>LiveWall.Layout</c> 会把它**夹小**，
    /// 点了「16」只摆 9 格，一个字都不说。</item>
    /// <item>摆格数时下界写成 <c>CellChoices[0]</c>（那是右键菜单上的 2）：
    /// 工具栏那颗「1」会被静默夹成 2 格 —— 单画面这一档等于没有。</item>
    /// <item>忘了 <c>Array.Fill(_eyes, true)</c>：<c>new bool[]</c> 全是 false，
    /// 那就是**一开窗整面墙都关着**（而且用户看不出哪里不对：每格都写着「已关闭」，
    /// 那本来是个正当状态）。</item>
    /// </list>
    /// <para>
    /// ⚠️ 为什么只能看源码文本：App 层没有测试工程（与这一组别的几条同一条理由）。
    /// 天花板也一样：它挡不住「判断写反了」—— 那条由
    /// <c>LiveWallTests</c> 里那几条（真跑对账）来挡，两边分工不重。
    /// </para>
    /// </remarks>
    [Fact]
    public void 多画面起几路是用户说了算的_档位一个都不许被夹掉()
    {
        var app = Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App");
        var window = ReadSplit(app, "MultiViewWindow");

        // 哨兵：扫到的是真文件（ReadSplit 扫到零个文件时它会红）。
        Assert.Contains("WindowState.Minimized", window, StringComparison.Ordinal);

        var max = Regex.Match(window, @"MaxCells\s*=\s*(\d+)");
        Assert.True(max.Success, "MultiViewWindow 里没量到 MaxCells。");

        var choices = Regex.Match(window, @"SplitChoices\s*=\s*\[([^\]]*)\]");
        Assert.True(choices.Success, "MultiViewWindow 里没量到 SplitChoices（T9 那四档）。");

        var presets = choices.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(one => int.Parse(one, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        // ⚠️ 逐字钉住这四档：它们是需求方定的手势（1 → 4 → 9 → 16），
        // 不是「随便几档」——少一档或多一档都得先问过。
        Assert.Equal(new[] { 1, 4, 9, 16 }, presets);

        var maxCells = int.Parse(max.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(
            presets.Max() <= maxCells,
            $"工具栏最大一档是 {presets.Max()} 格，而 MaxCells 只摆得下 {maxCells} 格 —— "
                + "点了会被 LiveWall.Layout 静默夹小，用户只会觉得「这一档坏了」。");

        // 下界必须是 1（那档单画面），不是菜单上的第一档（2）。
        Assert.Contains("LiveWall.Layout(count, 1, MaxCells)", window, StringComparison.Ordinal);

        // 摆不下的与关了眼睛的，一起走同一个判断；传进去的是**整个机位表**
        //（挑着传的话关掉的那几格会塌掉位置，后面几台往前挪一格）。
        Assert.Contains("SyncAsync(_cameras(), IsCellLive)", window, StringComparison.Ordinal);
        Assert.Contains("private bool IsCellLive(int index)", window, StringComparison.Ordinal);

        // 一开窗每一格都看（不 Fill 的话整面墙都是关着的）。
        Assert.Contains("Array.Fill(_eyes, true)", window, StringComparison.Ordinal);
    }

    /// <summary>
    /// 读一个**拆成了 partial** 的窗口 / 服务的全部源码（整行注释已剥掉）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 2026-10-06（T26）之后这是唯一正确的读法。<c>SettingsWindow</c> /
    /// <c>MainWindow</c> / <c>PlaybackServer</c> 都按页签或职责拆成了若干 partial，
    /// 而下面这些绊线多半是「某个字符串在源码里出现过」的形状 ——
    /// <b>只读主文件的话，那一行一搬走它们就假红</b>，而假红的惯常下场是有人把它
    /// 改成去读那个具体文件，于是下次再挪再红一次。这条弯路 2026-10-06 一天里真走了
    /// 三遍（T26 ① ② ④ 各一次）。剥注释：注释掉的代码不算数
    ///（<c>入网二维码…</c> 那条本来就这么做）。
    /// </remarks>
    private static string ReadSplit(string folder, string stem) =>
        string.Join(
            '\n',
            Directory.EnumerateFiles(folder, stem + "*.cs")
                .OrderBy(one => one, StringComparer.Ordinal)
                .SelectMany(one => File.ReadAllLines(one)
                    .Where(line => !line.TrimStart().StartsWith("//"))));

    /// <summary>从测试程序集往上找到仓库根（含 <c>src</c> 与 <c>tests</c> 的那一层）。</summary>
    /// <summary><paramref name="needle"/> 在 <paramref name="haystack"/> 里出现了几次。</summary>
    private static int CountOf(string haystack, string needle)
    {
        var count = 0;

        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

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

    // ─────────────────────────────────────────────
    // 出包（2026-10-01 补）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 打包脚本里的版本号**只有一个来源**（<c>VidLog.Desktop.App.csproj</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 本仓在 2026-10-01 之前**根本没有出包这条路** —— 只有 <c>dotnet build</c>，
    /// 产物落在 <c>bin\Release\</c>。那是一份**依赖开发机环境**的输出：目标机器
    /// 要先装 .NET 9 桌面运行时、PATH 上要有 FFmpeg，而缺了第二样的表现是
    /// 「软件能开、界面能点，采集与实时多画面全都说没有 FFmpeg」。
    /// <c>scripts/package.ps1</c> 就是补这一环的：自包含发布 + 随包
    /// <c>tools\ffmpeg.exe</c>（那是 <c>FfmpegLocator</c> 认的第三条路径）。
    /// </para>
    /// <para>
    /// 绊线的靶子是**版本号被抄成两份**：包名与 exe 属性走岔之后
    /// （「包名写着 0.2.0、属性里是 0.1.0」）事后没人对得出来。
    /// </para>
    /// <para>
    /// ⚠️ 天花板与上面几条文本绊线相同：只挡「有人把版本号写死进脚本」，
    /// 挡不住「脚本整体改错」。
    /// </para>
    /// </remarks>
    [Fact]
    public void 打包脚本的版本号只从_csproj_读()
    {
        var script = File.ReadAllText(
            Path.Combine(RepoRoot(), "scripts", "package.ps1"));

        // ① 版本号的来源是那个 csproj（不是脚本里另写的一份）。
        Assert.Contains("VidLog.Desktop.App.csproj", script, StringComparison.Ordinal);

        // ② 包名是拼出来的。
        Assert.Contains("VidLog-Desktop-$version-$Runtime.zip", script, StringComparison.Ordinal);

        // ③ 任何地方都不许再出现一个「VidLog-Desktop-<数字>」的字面量 ——
        //    写死版本号必然同时踩红 ② 与 ③。
        Assert.DoesNotMatch(@"VidLog-Desktop-\d", script);
    }

    // ─────────────────────────────────────────────
    // 安装程序（2026-10-02 补）
    // ─────────────────────────────────────────────

    private static string InstallerScript() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "installer", "VidLog.iss"));

    /// <summary>
    /// <c>.iss</c> 的**正文**：先把 Inno 的注释（行内第一个 <c>;</c> 起）剥掉。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不剥注释的话，下面几条全是**假红** —— 注释里会正当地写着「这里刻意没有
    /// <c>[UninstallDelete]</c>」「端口 8720 与 … 是同一个数」这类说明，
    /// 那正是要写给人看的话。绊线的靶子是**正文**，不是散文。
    /// <para>
    /// ⚠️ 天花板：按「行内第一个 <c>;</c>」剥，够用在本文件上（值里没有分号）。
    /// 真出现值里带分号的那天，这条得换成逐字符扫描。
    /// </para>
    /// </remarks>
    private static string InstallerCode() =>
        string.Join('\n', InstallerScript().Split('\n').Select(line =>
        {
            var i = line.IndexOf(';', StringComparison.Ordinal);
            return i >= 0 ? line[..i] : line;
        }));

    /// <summary>
    /// 安装脚本里的版本号**也只有一个来源**（那个 csproj，经
    /// <c>package.ps1</c> 用 <c>/D</c> 传进来）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="打包脚本的版本号只从_csproj_读"/> 同一个靶子，只是多了一层：
    /// 版本号现在是 zip 名、exe 属性、安装程序属性**三处**印出来，走岔了更难对。
    /// </remarks>
    [Fact]
    public void 安装脚本只认传进来的版本号()
    {
        var iss = InstallerCode();

        // ① 用的是 /D 传进来的量，不是自己写的。
        Assert.Contains("AppVersion={#AppVersion}", iss, StringComparison.Ordinal);
        Assert.Contains("VersionInfoVersion={#AppVersion4}", iss, StringComparison.Ordinal);

        // ② 任何形如 1.2.3 的字面量都不许出现 —— 出现就是抄了一份版本号。
        Assert.DoesNotMatch(@"\d+\.\d+\.\d+", iss);

        // ③ 缺了 /D 要**当场编译失败**，而不是编出一个版本号空着的包。
        Assert.Contains("#ifndef AppVersion", iss, StringComparison.Ordinal);
    }

    /// <summary>
    /// 安装脚本登记的回放端口，与 <see cref="DesktopServices.DefaultPlaybackPort"/>
    /// **必须是同一个数**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 这是**唯一**能挡住端口漂移的检查，而漂移的表现是最难查的那一类：
    /// 「软件装好了、界面一切正常，手机就是连不上」—— 两边分开看都是对的。
    /// 安装脚本引用不了 C# 常量，只能靠这条绊线把它们钉在一起。
    /// </para>
    /// <para>
    /// ⚠️ 天花板：端口被改过之后（<c>SettingsWindow</c> 的「回放端口」是可改的），
    /// 这条预留就管不着新端口了。那是个**已知边界**，由报名窗可见地提示（I3），
    /// 不是靠这里挡 —— 这里挡的是「两处默认值本身就不一致」。
    /// </para>
    /// </remarks>
    [Fact]
    public void 安装脚本登记的回放端口与代码里的默认端口是同一个数()
    {
        var iss = InstallerCode();
        var expected = DesktopServices.DefaultPlaybackPort.ToString();

        // 装的时候登记、卸的时候撤掉，凡是出现端口的**每一处**都得是这个数。
        var used = Regex.Matches(iss, @"url=http://\+:(\d+)/")
                        .Select(m => m.Groups[1].Value)
                        .Distinct()
                        .ToList();

        Assert.NotEmpty(used);
        Assert.Equal(expected, Assert.Single(used));
    }

    /// <summary>
    /// 卸载**不清用户数据**（母仓 AGENTS.md §3）。
    /// </summary>
    /// <remarks>
    /// 录像、索引、设置在 <c>%LOCALAPPDATA%\VidLog</c>，**不在** <c>{app}</c> 里，
    /// 所以 Inno 默认就碰不到它们。<c>[UninstallDelete]</c> 是唯一能删到
    /// <c>{app}</c> 之外东西的段 —— 加上它，「卸载重装」这个常规排查手段
    /// 就变成了一次数据灭失，而那正是本产品承诺「证据不丢」的反面。
    /// </remarks>
    [Fact]
    public void 安装脚本卸载时不删用户数据()
    {
        var iss = InstallerCode();

        // ① 那个段整个不许出现。
        Assert.DoesNotContain("[UninstallDelete]", iss, StringComparison.Ordinal);

        // ② 也不许绕过它去碰数据目录（`{localappdata}` 只会出现在真的拿它做事的地方）。
        Assert.DoesNotContain("{localappdata}", iss, StringComparison.OrdinalIgnoreCase);

        // ③ 装的时候登记了 urlacl，卸的时候就得撤掉 —— 只登记不撤会在那台机器上
        //    留下一条指向已卸软件的预留，下一个想用 8720 的人会莫名失败。
        var idx = iss.IndexOf("[UninstallRun]", StringComparison.Ordinal);
        Assert.True(idx >= 0, "`.iss` 里没有 [UninstallRun] —— 卸载会留下那条 urlacl 预留");
        Assert.Contains("netsh http delete urlacl", iss[idx..], StringComparison.Ordinal);
    }

    /// <summary>
    /// 安装脚本必须是 **UTF-8 带 BOM**。
    /// </summary>
    /// <remarks>
    /// ⚠️ Inno 认不出没有 BOM 的 UTF-8，会把中文按 ANSI 读 —— **编译器一个字都不说**，
    /// 装出来的向导与快捷方式名字全花，只有把人叫到机器前才看得见。
    /// <c>package.ps1</c> 撞见没 BOM 会**当场失败**（不替人改文件，改文件会让
    /// 一次打包悄悄改动工作区），所以这条得在这儿钉住。
    /// </remarks>
    [Fact]
    public void 安装脚本是带_BOM_的_UTF8()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot(), "installer", "VidLog.iss"));

        Assert.True(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "installer\\VidLog.iss 少了 UTF-8 BOM —— Inno 会把里面的中文按 ANSI 读");
    }

    /// <summary>
    /// 安装脚本加的那条**防火墙放行**，与 Core 里那份（<see cref="PlaybackFirewall"/>）
    /// 必须是同一个名字、同一个端口，而且**装上 / 卸下成对**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 这是 2026-10-03 那个缺陷的闸。回放服务绑的是 http.sys，Windows 那个
    /// 「允许访问」的弹窗**不会出现**，所以放行只能由安装程序做 —— 而少了它，
    /// 现场是「软件一切正常、二维码照画，手机报连不上、电脑端一个字都不显示」，
    /// 两边分开看都是对的。这个名字一旦与代码里那份走岔，
    /// 界面就会**说反**：明明放行了却报「没放行」（或者反过来，什么都不说）。
    /// </para>
    /// <para>
    /// ⚠️ 天花板：`remoteip=localsubnet` 是**故意**只放行本网段的（手机只可能从
    /// 局域网连过来），所以这条规则对「手机在另一个网段」那种部署无能为力 ——
    /// 那种情况由手机端的「手填地址」兜底，不是靠这里挡。
    /// </para>
    /// </remarks>
    [Fact]
    public void 安装脚本的防火墙放行与代码里的那一份对得上()
    {
        var iss = InstallerCode();

        // ① 名字逐字相同：加的那条在 [Run] 里，删的那条在 [UninstallRun] 里 ——
        //    删漏了会在那台机器上留下一个没人认领的入站口子。
        var ruleName = PlaybackFirewall.RuleName;

        Assert.Contains(
            $"netsh advfirewall firewall add rule name={ruleName}",
            iss,
            StringComparison.Ordinal);

        var uninstall = iss.IndexOf("[UninstallRun]", StringComparison.Ordinal);
        Assert.True(uninstall >= 0, "`.iss` 里没有 [UninstallRun]");
        Assert.Contains(
            $"netsh advfirewall firewall delete rule name={ruleName}",
            iss[uninstall..],
            StringComparison.Ordinal);

        // ② 端口与 urlacl 那条是同一个数（默认回放端口）。
        var ports = Regex.Matches(iss, @"localport=(\d+)")
                         .Select(m => m.Groups[1].Value)
                         .Distinct()
                         .ToList();

        Assert.Equal(DesktopServices.DefaultPlaybackPort.ToString(), Assert.Single(ports));

        // ③ 只放行本网段 —— 别对着整个人网开一道门；
        //    profile 用 any（这台机器的网卡常常被归到「公用网络」，只给「专用」放行等于没放）。
        Assert.Contains("remoteip=localsubnet", iss, StringComparison.Ordinal);
        Assert.Contains("profile=any", iss, StringComparison.Ordinal);

        // ④ 界面念给用户听的那一行命令，与安装程序做的是**同一件事** ——
        //    逐项对得上，不是「意思差不多」：少一个 profile 就是一个
        //    只在某些机器上才连不上的坑（而这条修复的立身之本正是「两处一致」）。
        var shown = PlaybackFirewall.AddCommandLine(DesktopServices.DefaultPlaybackPort);

        Assert.Contains(
            $"netsh advfirewall firewall add rule name={ruleName}",
            shown,
            StringComparison.Ordinal);
        Assert.Contains("remoteip=localsubnet", shown, StringComparison.Ordinal);
        Assert.Contains("profile=any", shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// 判据只用退出码：**0 在、1 不在、别的都是「说不准」**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 「说不准」不许塌成「不在」：那会让界面在别的杀软 / 被组策略管着的机器上
    /// 平白吓人一跳（而且报的是一件假事）。这两条都是「用户可见通道分得清吗」那条要求。
    /// </remarks>
    [Fact]
    public async Task 防火墙规则在不在只看退出码_说不准不许当成不在()
    {
        var exists = new StubRunner(0);
        var missing = new StubRunner(1);
        var unknown = new StubRunner(2);

        Assert.True(await PlaybackFirewall.IsRulePresentAsync(exists));
        Assert.False(await PlaybackFirewall.IsRulePresentAsync(missing));
        Assert.Null(await PlaybackFirewall.IsRulePresentAsync(unknown));

        // 问的是**那一条规则**，不是「防火墙开没开」之类别的东西。
        Assert.Contains($"name={PlaybackFirewall.RuleName}", exists.Arguments[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// 装配时要记一条「这一台在用哪把公钥」。
    /// </summary>
    /// <remarks>
    /// 2026-10-03 那个事故（客户机上没有 <c>VIDLOG_LICENSE_PUBKEY</c> ⇒ 机位恒 0 ⇒
    /// 粘什么码都激活不了）**诊断了整整一轮才定位**，因为日志里翻不出
    /// 「这一台到底在拿哪把公钥验」。这一条钉的就是那句话必须在。
    /// <para>
    /// 环境变量那一档也要留着 —— 开发机忘删就会拒收真码，而那时日志是唯一的线索。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 装配时记一条_用的是哪把公钥()
    {
        var previous = Environment.GetEnvironmentVariable("VIDLOG_LICENSE_PUBKEY");
        try
        {
            Environment.SetEnvironmentVariable("VIDLOG_LICENSE_PUBKEY", null);

            using var dir = new TempDir();
            var logger = new CapturingLogger();

            await using var services = DesktopServices.Create(
                new DataLayout(dir.Dir("data")), playbackPort: null, logger: logger);

            var line = Assert.Single(logger.Messages, m => m.Contains("激活码公钥", StringComparison.Ordinal));
            Assert.Contains("内置", line, StringComparison.Ordinal);
            // 装配没问题时**不该**出现那条警告（不然下面那条断言就是白过的）。
            Assert.DoesNotContain(
                services.Warnings, w => w.Contains("许可没有配置好", StringComparison.Ordinal));

            // 设上环境变量之后，那一句话必须**改口**（否则它就只是一句废话）。
            Environment.SetEnvironmentVariable("VIDLOG_LICENSE_PUBKEY", "AAAA");
            var overridden = new CapturingLogger();
            await using var withOverride = DesktopServices.Create(
                new DataLayout(dir.Dir("data2")), playbackPort: null, logger: overridden);

            var other = Assert.Single(
                overridden.Messages, m => m.Contains("激活码公钥", StringComparison.Ordinal));
            Assert.Contains("覆盖", other, StringComparison.Ordinal);

            // 公钥解析不了 ⇒ 没有 LicenseService，那条用户可见的警告要出来。
            Assert.Contains(
                withOverride.Warnings,
                w => w.Contains("许可没有配置好", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VIDLOG_LICENSE_PUBKEY", previous);
        }
    }

    /// <summary>
    /// 改造清单 T26。母仓 §4：单文件 ≤ 800 行是**建议**，「超过 1500 行**必须**拆」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 清单点名了三个破 1500 的文件（T26 的标题就是「三个文件破 1500 行硬上限」），
    /// 验收写的是「拆完后**每个文件** ≤ 800 行」—— 那个「每个」指的就是这三个。
    /// ⚠️ 本仓只钉得到其中两个：<c>recorder_page.dart</c> 在手机仓（T26③），
    /// 得在那边自己钉一条。
    /// </para>
    /// <para>
    /// ⚠️ <b>为什么不扫「全仓所有 .cs」</b>：实测本仓有 <b>18 个</b> .cs 超 800 行
    ///（含 8 个测试文件）。照字面扫全仓的话白名单要列 18 条 —— 那就不叫绊线了，
    /// 叫给现状盖章（新写一个 900 行的文件照样过得去）。这里钉的是清单点名那几个
    /// 拆出来的**家族**，而且是 glob 不是某一个路径 —— 钉路径的话，拆出来的新
    /// partial 就全在检查之外，当天下午再长回去也没人管。
    /// </para>
    /// <para>
    /// ⚠️ <b>2026-10-06：这条绊线把自己缩没了</b>。它原先还带一份「还没拆」的白名单
    /// 加一条**收缩断言**（白名单里的文件一旦不超了就必须删条目）。T26② 拆完之后
    /// 那条断言真的报了「<c>PlaybackServer.cs</c> 现在只有 590 行，已经不超 800 了」；
    /// 拆 ④ 又逼掉了 <c>MainWindow.xaml.cs</c> 那条 —— 白名单至此**空了**，收缩断言
    /// 随之退场（留着就是死代码）。它当初要挡的那个洞（「拆完了条目还留着，
    /// 往后谁再往里加行都没人管」）现在由下面那圈扫描接着守。
    /// </para>
    /// <para>
    /// ⚠️ 天花板照旧：冻结的是**文件**不是**行数** —— 在白名单里的文件还能继续变长
    /// （T7 的硬编码字号绊线用的是同一招，清单里明写了这个洞并接受）。
    /// </para>
    /// </remarks>
    [Fact]
    public void T26_点名的长文件拆完之后都不超过800行()
    {
        const int Limit = 800;
        var repo = RepoRoot();
        var app = Path.Combine(repo, "src", "VidLog.Desktop.App");

        // 清单点名的那几个文件（T26 ① ② ④）各自拆出来的家族。
        var named = Directory.EnumerateFiles(app, "SettingsWindow*.cs")
            .Concat(Directory.EnumerateFiles(app, "MainWindow*.cs"))
            .Concat(Directory.EnumerateFiles(
                Path.Combine(repo, "src", "VidLog.Desktop.Core", "Web"), "PlaybackServer*.cs"));

        var offenders = new List<string>();

        foreach (var file in named)
        {
            var relative = Path.GetRelativePath(repo, file).Replace('\\', '/');
            var lines = File.ReadLines(file).Count();
            if (lines > Limit)
            {
                offenders.Add($"{relative}: {lines} 行");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"这些文件超过 {Limit} 行 —— 按页签（UI）/ 按职责（服务）拆成 partial：\n  "
                + string.Join("\n  ", offenders));
    }

    /// <summary>退出码固定、并把 argv 留一份的假 netsh。</summary>
    private sealed class StubRunner(int exitCode) : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Arguments = [executable, .. arguments];
            return Task.FromResult(new ProcessResult(exitCode, string.Empty, string.Empty));
        }
    }

    /// <summary>把日志收起来的假 logger（本仓测试的惯例）。</summary>
    private sealed class CapturingLogger : IAppLogger
    {
        public List<string> Messages { get; } = [];

        public void Log(LogLevel level, string category, string message) => Messages.Add(message);

        public void Log(
            LogLevel level, string category, string message,
            IReadOnlyDictionary<string, object?> data) => Messages.Add(message);
    }
}
