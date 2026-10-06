using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的命名空间都带进来，于是 MessageBox / Brush 这类
// 同名类型变成「不明确」。这里用**别名钉死成 WPF 的那套** ——
// 这个文件里的控件与画笔全是 WPF 的，WinForms 一个都不该出现。
using Brush = System.Windows.Media.Brush;
// ⚠️ 这两个也必须钉死：WinForms 那一侧有 `System.Drawing.Image`，
// 不钉的话 `Image` 会静默解析成**画图那个**（编译期只报一句「参数不对」，
// 而真正的问题是类型选错了）。别名块存在的理由就在这里。
using Image = System.Windows.Controls.Image;
using PixelFormats = System.Windows.Media.PixelFormats;
using TextBlock = System.Windows.Controls.TextBlock;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
// ⚠️ WinForms 那侧也有一个 `KeyEventArgs`（`MouseEventArgs` 那些倒是不撞，
// 所以只有这一个要钉）。T12 的命令面板开始用 WPF 那个。
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using VidLog.Desktop.App.Platform;
using VidLog.Desktop.Core;
using VidLog.Desktop.Core.Commands;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Live;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Rendering;
using VidLog.Desktop.Core.Scanning;
using VidLog.Desktop.Core.Search;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.App;

public partial class MainWindow : Window
{
    // ─────────────────────────────────────────────
    // 全局热键（T15）
    // ─────────────────────────────────────────────

    /// <summary>
    /// 挂上全局热键。<b>VidLog 不在前台时也收得到。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 选择 <c>Ctrl+Alt+…</c> 而不是 OBS 那样光秃秃的 F9/F10：`RegisterHotKey` 是
    /// <b>系统级独占</b>的，按下去前台程序收不到 —— 而工位电脑上多半同时开着表格、
    /// 浏览器、ERP，F9 在表格里是重算、F10 在老程序里是菜单栏。
    /// 带修饰键才谈得上「不打扰旁边的软件」。
    /// </para>
    /// <para>
    /// ⚠️ <b>清单里那句「开始/停止录制或推流」的「推流」在电脑端没有对应物</b>：
    /// 推流是手机在做，电脑端只是接收方。所以这里只有录制与多画面两个。
    /// </para>
    /// </remarks>
    private void AttachHotKeys()
    {
        var keys = new GlobalHotKeys(this, _host.Logger);
        _hotKeys = keys;

        // ⚠️ 多画面**两种用途下都挂**：那面墙是「看每台手机现在在拍什么」，
        // 与这台电脑录不录像无关（备份主机上也照样看）。
        keys.TryAdd(
            "实时多画面", ModifierKeys.Control | ModifierKeys.Alt, Key.M,
            () => OnOpenMultiView(this, new RoutedEventArgs()));

        // ⚠️ 录像那一个**只在本机真的录像时才挂**：备份主机那两档按下去什么动静
        // 都不会有，而一个按下去没反应的键比没有这个键更让人费解。
        if (_host.Settings.Role.Records)
        {
            keys.TryAdd(
                "开始/停止录像", ModifierKeys.Control | ModifierKeys.Alt, Key.R,
                () => OnStartOrStopWork(this, new RoutedEventArgs()));
        }
    }

    // ─────────────────────────────────────────────
    // 命令面板（T12）
    // ─────────────────────────────────────────────

    /// <summary>
    /// <c>Ctrl+K</c> —— 顺手敲两下就能到任何一个入口，不必先找那颗按钮在哪一屏。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 挂在窗的 <see cref="UIElement.PreviewKeyDown"/> 上，不在某个控件上：
    /// 焦点这会儿可能在单号框里（用户刚扫完一枪），而那个框的普通 <c>KeyDown</c>
    /// 会先把字母吃掉。
    /// </para>
    /// <para>
    /// ⚠️ <b>刻意不做成「可见的入口」</b>（按钮 / 菜单项）：清单没要求，
    /// 凭空在主窗上加一颗按钮是**改设计图**。代价是它只能靠人传人 ——
    /// 见汇报里的「已知上限」。
    /// </para>
    /// </remarks>
    private void OnPaletteShortcut(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.K || Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        // 标成已处理：不然这个 K 还会继续往焦点所在的框里走（单号框会多一个 k）。
        e.Handled = true;

        OpenPalette();
    }

    private void OpenPalette()
    {
        var palette = new CommandPaletteWindow(MatchPalette) { Owner = this };

        // ⚠️ **模态**，与其它几个入口一致：主窗这时是活的，回收站与保留期清理
        // 都在背后跑 —— 用户正盯着一条候选、底下的录像却少了，那更费解。
        palette.ShowDialog();

        if (palette.Chosen is { } chosen)
        {
            // §6.1：面板本身没有资源、没有不可逆动作，但它**替用户按了别处的按钮** ——
            // 用户事后说「我按了 Ctrl+K 选了检索，怎么没反应」时，
            // 「他到底选的是哪一条」只有这里记得下来。
            _host.Logger.Log(
                VidLog.Desktop.Core.Diagnostics.LogLevel.Info, "命令面板", $"选了：{chosen.Label}");

            RunPaletteCommand(chosen);
        }
    }

    /// <summary>
    /// 候选表。<b>每个动作就是主窗上原来那颗按钮</b>，不另写一份实现。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 每次敲字都会重来一遍：表里可能有**跟着输入变**的一条（「检索单号 X」），
    /// 而 <see cref="PaletteMatcher"/> 是纯函数，它只认递进来的这张表。
    /// </para>
    /// <para>
    /// ⚠️ 表是**手写**的、靠前的是更要紧的入口 —— 同分时
    /// <see cref="PaletteMatcher.Rank"/> 保持这个先后（见那边的注释）。
    /// </para>
    /// </remarks>
    private IReadOnlyList<PaletteCandidate> MatchPalette(string typed)
    {
        _paletteRuns.Clear();

        // ⚠️ **候选与它要做的事写在一起**，不分成两张表：分成两张的话，加一条候选
        // 却忘了加动作，症状就是「选了没反应」（踩坑 #13 点名的第三种）。
        var table = new List<(PaletteCandidate Candidate, Action Run)>
        {
            (new("search", "检索录像", "回放,查找,单号,播放"),
                () => OnOpenSearch(this, new RoutedEventArgs())),
            (new("data", "打开数据", "备份,存档,总览,统计"),
                () => OnOpenData(this, new RoutedEventArgs())),
            (new("settings", "打开设置", "配置,选项,许可,激活"),
                () => OnOpenSettings(this, new RoutedEventArgs())),
            (new("wall", "实时多画面", "九格,监控,机位"),
                () => OnOpenMultiView(this, new RoutedEventArgs())),
            (new("enroll", "连接手机", "二维码,扫码,入网"),
                () => OnEnroll(this, new RoutedEventArgs())),
        };

        var candidates = new List<PaletteCandidate>(table.Count);

        foreach (var (candidate, run) in table)
        {
            _paletteRuns[candidate] = run;
            candidates.Add(candidate);
        }

        var hits = PaletteMatcher.Rank(typed, candidates);

        // 打进去的既不是动作名、也不是关键词 —— 那多半就是个**单号**，
        // 于是补一条「去检索它」。这是清单点名要的四种用法里的第四种。
        //
        // ⚠️ **只在一条动作都没命中时才补**。不然「打」这种字（单号里的第一个字，
        // 也正好是「打开…」的第一个字）会同时冒出「打开数据」和「检索单号 打」，
        // 而后者永远不是用户想要的。
        //
        // 已知上限：单号长得**恰好**能子序列命中某个动作名时（比如某个单号里
        // 一个字不差地散落着「实、时、多、画、面」），跳单号那一条就不会出现。
        // 概率极低，代价是用户改用【回放】那颗按钮 —— 不为它加一层启发式。
        if (hits.Count == 0 && !string.IsNullOrWhiteSpace(typed))
        {
            var raw = typed.Trim();
            var jump = new PaletteCandidate(
                "waybill", $"检索单号 {WaybillNumber.Normalize(raw)} 的录像", "跳转,打开,查找");

            _paletteRuns[jump] = () => new SearchWindow(_host, raw) { Owner = this }.ShowDialog();

            return [jump];
        }

        return hits;
    }

    private void RunPaletteCommand(PaletteCandidate chosen)
    {
        // ⚠️ 拿**上一次匹配**留下的那张表来查，而 `chosen` 正是从那张表里选出来的，
        // 所以查得到（`PaletteCandidate` 是 record，比的是值）。
        //
        // 查不到只有一种可能：**这张表在选中之后又被重建过**。重建只发生在面板
        // 那一次 `MatchPalette` 调用里，而面板已经关掉了 —— 现在不会，将来也不会。
        // 真查不到就什么都不做：一个「选了没反应」比**开错窗**好解释得多。
        if (!_paletteRuns.TryGetValue(chosen, out var run))
        {
            _host.Logger.Log(
                VidLog.Desktop.Core.Diagnostics.LogLevel.Warn, "命令面板",
                $"选中了「{chosen.Label}」，但它没有对应的动作 —— 什么也没做");

            return;
        }

        run();
    }

    /// <summary>给多画面建一格：机位名从设备表来（§3.4.5），拿不到就退回设备号。</summary>
    /// <remarks>
    /// 显示一个内部号不好看，但**编一个名字更糟**。
    /// ⚠️ 查询只在**建格那一刻**做一次：机位名在这个窗里不会跟着改（改了名要重开窗口
    /// 才看得到）—— 而每一拍都查一遍设备表，代价比这一点收益大得多。
    /// </remarks>
    private async Task<LiveTile> CreateLiveTileAsync(LiveEndpoint endpoint, string ffmpeg)
    {
        var name = (await _host.Services.Devices.DevicesAsync())
            .FirstOrDefault(d => string.Equals(d.DeviceId, endpoint.DeviceId, StringComparison.Ordinal))
            .DeviceName;

        if (string.IsNullOrWhiteSpace(name)) name = endpoint.DeviceId;

        return LiveTile.Start(ffmpeg, endpoint.BaseUrl, name, logger: _host.Logger);
    }

}
