using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Upload;
using VidLog.Desktop.Core.Web;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的命名空间都带进来，于是 MessageBox 这类同名类型
// 变成「不明确」。这里用**别名钉死成 WPF 的那套** —— 这个文件里全是 WPF 的。
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace VidLog.Desktop.App;

/// <summary>
/// 【连接电脑/手机】弹出来的那张二维码（规格 §3.4.5）。
/// </summary>
/// <remarks>
/// <para>
/// 这一页是「人工批准」真的挡住东西的那一环：<b>二维码只显示在本机屏幕上</b>，
/// 站在屏幕前的人才扫得到。手机扫到之后**并不会**自动拿到凭据 ——
/// 它只发起一条请求，等这里的人点头。
/// </para>
/// <para>
/// <b>为什么要单独一个窗口而不是塞进主窗口</b>：这张码必须能被手机清楚地扫到，
/// 所以它得足够大、而且旁边不能堆着别的控件。主窗口密密麻麻。
/// </para>
/// <para>
/// ⚠️ <b>这里是 App 层，没有测试工程</b> —— 所以「装配少跳一步」对整套测试不可见
/// （见 <c>docs/实现决策.md</c>「装配的最后一跳」）。本文件里那几个调用点由
/// <c>DesktopServicesTests.入网二维码在界面上真的有出路</c> 这条文本绊线守着。
/// </para>
/// </remarks>
public partial class EnrollWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _ticker;

    /// <summary>已经弹过窗的设备 —— 同一条请求不再弹第二次。</summary>
    /// <remarks>
    /// 挡的是「一条关不掉的弹窗」：万一<see cref="DeviceRegistry.DecideAsync"/>
    /// 没能落上（请求已经不在了），下一轮轮询它又是个待批准的请求，
    /// 再弹一次就永远出不来。
    /// </remarks>
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);

    /// <summary>写进二维码的那个本机地址；挑不出来时为 <see langword="null"/>。</summary>
    private string? _address;

    /// <summary>上一次显示出来的设备列表，用来判断要不要重设 <c>ItemsSource</c>。</summary>
    private List<string> _deviceLines = [];

    /// <summary>弹窗还开着 —— 挡轮询重入。</summary>
    /// <remarks>
    /// WPF 的模态弹窗会跑一个嵌套消息循环，<see cref="DispatcherTimer"/> 在里面**照样触发**，
    /// 于是 <see cref="OnTick"/> 会在上一次还卡在 <c>MessageBox.Show</c> 里时被再调一次。
    /// 没有这个守卫，一轮申请能弹出一串一模一样的窗。
    /// </remarks>
    private bool _busy;

    public EnrollWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += OnTick;

        Loaded += async (_, _) => await RegenerateAsync();
        Closed += (_, _) => _ticker.Stop();
    }

    // ─────────────────────────────────────────────
    // 生成这张码
    // ─────────────────────────────────────────────

    private async void OnRegenerate(object sender, RoutedEventArgs e) => await RegenerateAsync();

    private async Task RegenerateAsync()
    {
        // 换一张码 = 上一张连同它下面那些请求一起作废（DeviceRegistry 的约定），
        // 所以「弹过谁」这份记录也跟着清掉。
        _asked.Clear();
        FootNote.Text = string.Empty;

        _address = LanAddress.Discover();

        if (BlockerReason() is { } blocker)
        {
            // 发不出码的时候**绝不显示一张假的**：宁可什么都没有，并说清为什么。
            _ticker.Stop();
            QrImage.Source = null;

            BlockerText.Text = blocker;
            BlockerText.Visibility = Visibility.Visible;
            StatusLine.Text = "这台电脑现在发不出二维码。";
            AddressLine.Text = string.Empty;
            RegenerateButton.IsEnabled = false;

            // 设备列表照旧要显示 —— 发不出新码不代表以前连过的那些不算数。
            await RefreshDevicesAsync();
            return;
        }

        BlockerText.Visibility = Visibility.Collapsed;
        RegenerateButton.IsEnabled = true;

        var session = await _host.Services.Devices.OpenSessionAsync();

        var payload = EnrollQr.Payload(_address!, _host.Services.PlaybackPort, session.Token);
        QrImage.Source = BuildBitmap(EnrollQr.Modules(payload));
        QrImage.Opacity = 1;
        AddressLine.Text =
            $"手机连不上时，可以在手机端手填地址：{_address}:{_host.Services.PlaybackPort}";

        // 先把心跳点上再轮询一次 —— 反过来的话，PollAsync 里万一走到 Expire()
        // 停掉了心跳，紧接着这一句又把它打开了（那之后就再也没人停它）。
        _ticker.Start();
        await PollAsync();
        await WarnIfFirewallBlockedAsync();
    }

    /// <summary>
    /// 防火墙没放行回放端口的话，把这件事和该敲的那一行写在码下面。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是 2026-10-03 报上来的那个缺陷留下来的：回放服务绑的是 <b>http.sys</b>，
    /// Windows 那个「允许访问」的弹窗**不会出现**（监听的是 System 进程，没人有点的
    /// 机会）—— 于是没有人会去放行，而界面上一切正常：服务绑上了、地址有了、
    /// 二维码也画出来了。手机那边报「连不上」，电脑端**一个字都不显示**，
    /// 用户只能对着一张好码干等。
    /// </para>
    /// <para>
    /// ⚠️ **只有问得出「确实不在」才提示**。「问不出来」（netsh 起不来、
    /// 被执行策略挡住、退出码不是 0/1）时**什么都不说** —— 在那种机器上平白
    /// 吓人一跳，比不说更坏（§6.1 那条「这条通道分得清吗」）。
    /// </para>
    /// </remarks>
    private async Task WarnIfFirewallBlockedAsync()
    {
        FirewallWarning.Visibility = Visibility.Collapsed;

        var port = _host.Services.PlaybackPort;

        // 端口被改过时那条规则管不着新端口 —— 但那种情况**根本绑不上 `+`**
        // （urlacl 只登记了 8720 这一个），窗口这时已经在说另一句话了，不在这里重复。
        if (port != DesktopServices.DefaultPlaybackPort)
        {
            return;
        }

        bool? present;
        try
        {
            present = await PlaybackFirewall.IsRulePresentAsync(
                new SystemProcessRunner(_host.Logger), PlaybackFirewall.RuleName);
        }
        catch (Exception ex)
        {
            // §6.1：catch 不许静默吞掉。这里用户可见的那条通道就是「什么都没提示」
            // （= 问不出来），所以至少要留一条痕，免得下次又查不出来。
            _host.Log(LogLevel.Warn, "入网", $"问不出防火墙那条规则在不在：{ex.Message}");
            return;
        }

        if (present is not false)
        {
            return;
        }

        _host.Logger.Log(
            LogLevel.Warn, "入网", "防火墙没放行回放端口，手机多半连不上",
            new Dictionary<string, object?>
            {
                ["端口"] = port,
                ["规则"] = PlaybackFirewall.RuleName,
            });

        FirewallWarning.Text =
            $"⚠️ 这台电脑的防火墙没有放行 {port} 端口 —— 手机扫了多半会报「连不上」。"
            + "让管理员在这台电脑上执行一次（安装程序本来会自动做，绿色包不会）：\n"
            + PlaybackFirewall.AddCommandLine(port);
        FirewallWarning.Visibility = Visibility.Visible;
    }

    /// <summary>现在发不出二维码的原因；发得出来返回 <see langword="null"/>。</summary>
    /// <remarks>
    /// ⚠️ 那四句话本身在 <see cref="EnrollBlockers.Blocker"/>（T27② 第 4 批）——
    /// 它们是**机器状态**、不是用户做错了什么，所以每条都给出「怎么才能好」；
    /// 而四条分支要用户做的事完全不同，说串了比不说更坏。这里只剩**取哪几样**。
    /// </remarks>
    private string? BlockerReason()
    {
        var server = _host.Services.Server;

        return EnrollBlockers.Blocker(
            hasServer: server is not null,
            baseUrl: server?.BaseUrl,
            usingFallback: server?.IsUsingFallback == true,
            fallbackReason: server?.FallbackReason,
            port: _host.Services.PlaybackPort,
            hasAddress: _address is not null);
    }

    // ─────────────────────────────────────────────
    // 轮询：谁在申请、还剩多久、已经连过谁
    // ─────────────────────────────────────────────

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await PollAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task PollAsync()
    {
        var registry = _host.Services.Devices;

        var session = await registry.SessionAsync();
        if (session is null)
        {
            Expire();
            return;
        }

        var left = DeviceRegistry.SessionLifetime - (DateTimeOffset.UtcNow - session.OpenedAt);
        var seconds = (int)Math.Max(0, left.TotalSeconds);
        StatusLine.Text = $"这张码还能用 {seconds / 60:00}:{seconds % 60:00}";

        await RefreshDevicesAsync();

        // 只看「还没决定的」。已经批过的不再问，被拒的也不再问 ——
        // 手机要一直等在那儿，它下一轮轮询拿到的就是同一个答复。
        var waiting = (await registry.PendingAsync())
            .FirstOrDefault(p => p.Decision == EnrollDecision.Pending && !_asked.Contains(p.DeviceId));

        if (waiting is not null)
        {
            await AskAsync(waiting);
        }
    }

    /// <summary>码失效了（超时，或者已经被手机领走）。</summary>
    /// <remarks>
    /// 两种情况**故意用同一句话**：从这台机器上看它们长得一模一样，
    /// 分不清就别说死是哪一种（说了就是编）。两边的下一步都是「重新生成」。
    /// </remarks>
    private void Expire()
    {
        _ticker.Stop();
        QrImage.Opacity = 0.15;
        StatusLine.Text = "这张码已经失效了（被手机领走，或者超过了 5 分钟）。";
        AddressLine.Text = "要再接一台设备，点【重新生成二维码】。";
    }

    private async Task AskAsync(PendingEnrollment pending)
    {
        _asked.Add(pending.DeviceId);

        var name = string.IsNullOrWhiteSpace(pending.DeviceName) ? "一台设备" : pending.DeviceName;

        var answer = MessageBox.Show(
            this,
            $"{name} 申请连接。\n\n同意之后，这台设备就能把录像传到本机。",
            "有一台设备申请连接",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            // 默认落在「否」—— 回车、或者直接关掉，都**不该**等于同意。
            MessageBoxResult.No);

        var approved = answer == MessageBoxResult.Yes;
        var decided = await _host.Services.Devices.DecideAsync(pending.DeviceId, approved);

        if (!decided)
        {
            // 落空不吭声的话，用户会以为「点了同意」，而手机上什么都不会发生。
            FootNote.Text = "这条请求已经不在了（手机可能重新扫了一次码）。";
            return;
        }

        FootNote.Text = approved ? $"已同意 {name}。" : $"已拒绝 {name}。";
    }

    private async Task RefreshDevicesAsync()
    {
        var devices = await _host.Services.Devices.DevicesAsync();

        var lines = devices
            .Select(d =>
                string.IsNullOrWhiteSpace(d.DeviceName)
                    ? $"(没报上名字)　·　{d.ApprovedAt.ToLocalTime():yyyy-MM-dd HH:mm} 连上"
                    : $"{d.DeviceName}　·　{d.ApprovedAt.ToLocalTime():yyyy-MM-dd HH:mm} 连上")
            .ToList();

        var any = lines.Count > 0;
        DeviceList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        DeviceEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

        // 列表每秒都被轮询刷一遍。内容没变就别重设 ItemsSource ——
        // 重设会把整个列表重建一次，用户正在看的那一行会跳一下。
        if (lines.SequenceEqual(_deviceLines))
        {
            return;
        }

        _deviceLines = lines;
        DeviceList.ItemsSource = lines;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ─────────────────────────────────────────────
    // 画码
    // ─────────────────────────────────────────────

    /// <summary>把模块矩阵转成位图：一个模块一个像素，放大交给最近邻。</summary>
    /// <remarks>
    /// 字节怎么摊由 <see cref="EnrollQr.Pixels"/> 决定（深色 0、浅色 255），
    /// 于是**喂给屏幕的这些字节，就是往返测试解回来过的那一份**。
    /// 这里用 <see cref="PixelFormats.Gray8"/>（一字节一像素）而不是 <c>BlackWhite</c>：
    /// 后者按位打包，0 是黑还是白得看约定 —— 而这个函数里就剩这一处会写反。
    /// </remarks>
    private static BitmapSource BuildBitmap(bool[,] modules)
    {
        var width = modules.GetLength(0);
        var height = modules.GetLength(1);

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Gray8, null, EnrollQr.Pixels(modules), width);

        bitmap.Freeze();
        return bitmap;
    }
}
