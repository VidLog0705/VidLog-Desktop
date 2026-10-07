using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Live;

// WinForms 那一侧也有 Button / ContextMenu / Brush / KeyEventArgs / Orientation，
// 钉死成 WPF 的那套（本仓每个窗口都这么办）。
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using VerticalAlignment = System.Windows.VerticalAlignment;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock = System.Windows.Controls.TextBlock;
// 眼睛那个图标是内联矢量（同 MainWindow 里那排图标）：`Path` 不钉的话
// 会和 WinForms 那边的同名类型撞车。
using Path = System.Windows.Shapes.Path;
using Shape = System.Windows.Shapes.Shape;

namespace VidLog.Desktop.App;

/// <summary>
/// 实时多画面（规格 §3.8，需求方 2026-10-01 定形）。
/// </summary>
/// <remarks>
/// <para>
/// <b>默认九宫格；工具栏那四档分割（1 / 4 / 9 / 16）与右键菜单（2~9）都能改格数；
/// 选几就摆几格</b>——机位不够时空格显示「未接入」（浅底；不隐藏格子：格数是**用户选的**，
/// 不是机位数决定的）。每格右上角一颗转动按钮（点一次 90°），双击进全屏，
/// 全屏里能换画质。每格下方居中显示 <c>F</c>（发货，绿）与 <c>T</c>（退货，红）。
/// </para>
/// <para>
/// ⚠️ <b>F / T 只在屏幕上，绝不进视频水印</b>（规格 §3.8 ⑦）。这一层压根不碰视频 ——
/// 「不写进去」是结构上成立的，不是靠自觉。
/// </para>
/// <para>
/// ★ <b>T9：起几路 ffmpeg 是用户说了算的。</b>两个口子，都走
/// <see cref="IsCellLive"/> 这一个判断：<b>摆不下的格子</b>（选了 4 格而报到了 9 台）
/// 与<b>用户关了眼睛的格子</b>。两者都省下一路 ffmpeg 与它的解码，
/// 而**格子的位置一个都不动**（见 <see cref="LiveWall.SyncAsync"/> 那一段）。
/// 加上最小化时整面墙都停 —— 「九格满负荷」从前是**从来没验过**的那条路。
/// </para>
/// </remarks>
public partial class MultiViewWindow : Window
{
    /// <summary>右键菜单上能选的画面数（需求方逐字定的这几档）。</summary>
    private static readonly int[] CellChoices = [2, 3, 4, 5, 6, 7, 8, 9];

    /// <summary>
    /// 工具栏上那四档分割（T9）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 四档就是**四的倍数**那一路：1 → 4 → 9 → 16（NVR 客户端的标准手势）。
    /// 它们与 <see cref="CellChoices"/> 不冲突，后者是右键菜单里的细档。
    /// ⚠️ 最大那一档**必须** ≤ <see cref="MaxCells"/>，否则会被
    /// <see cref="LiveWall.Layout"/> 静默夹小（点了 16 却只摆 9）。
    /// </remarks>
    private static readonly int[] SplitChoices = [1, 4, 9, 16];

    private const int MaxCells = 16;

    /// <summary>
    /// 开窗时摆几格（需求方 2026-10-07 拍的 P3：**照设计图，默认九宫格**）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它**不是** <see cref="MaxCells"/>：工具条上那档 16 是给「四分割再放大一档」
    /// 用的，不该变成开窗的默认值 —— 一开窗就起 16 路 ffmpeg，而用户多半只要看两台手机。
    /// </remarks>
    private const int DefaultCells = 9;

    /// <summary>
    /// 三档各自的好处与坏处。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这段文案归这里（App 层），不在 Core 的那个枚举上。</b>
    /// 它是**选择界面上的字**；放在领域枚举上，一个纯数据的东西会背上一段
    /// 只有界面才用得上的散文。手机端那边同样只有值、没有文案。
    /// </remarks>
    private static readonly Dictionary<LiveQuality, string> Tradeoffs = new()
    {
        [LiveQuality.P480] = "480P：省流量、省电，九宫格一起看也不挤；全屏时偏糊。",
        [LiveQuality.P720] = "720P：全屏看得清，带宽也压得住；机位多时会比 480P 吃力。",
        [LiveQuality.P1080] = "1080P：全屏最清楚；最费流量，机位多时手机容易发热、画面可能卡。",
    };

    /// <summary>每一拍重新问一次「现在还算数的机位有哪些」。</summary>
    private readonly Func<IReadOnlyList<LiveEndpoint>> _cameras;

    /// <summary>对账那面墙；**设计器那条路上是 <see langword="null"/>**（没有机位源）。</summary>
    private readonly LiveWall? _wall;

    private readonly VidLog.Desktop.Core.Diagnostics.IAppLogger _logger;
    private readonly List<Cell> _cells = [];
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _syncTimer;

    /// <summary>
    /// 每一格还看不看（T9 的「眼睛」）。**默认全看**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 下标是**格子的位置**，不是机位 —— 关掉第 3 格，就得一直关着第 3 格，
    /// 哪怕那台手机换了端口、或者中间某台退了休让后面的往前挪了位。
    /// 按机位记的话，用户关掉的那一格会在下一拍**自己又亮起来**。
    /// </remarks>
    private readonly bool[] _eyes = new bool[MaxCells];

    /// <summary>窗口最小化了：整面墙都停（T9 / P3 拍的「一并停」）。</summary>
    private bool _suspended;

    private int _cellCount = DefaultCells;

    /// <summary>墙上现在挂着几台机位（对完账才知道）。</summary>
    private int _onlineCount;

    /// <summary>计数那一拍正在跑（见 <see cref="PumpCounts"/> 的重入保护）。</summary>
    private bool _pumpingCounts;

    /// <summary>对账那一拍正在跑（理由同 <see cref="PumpCounts"/>：它要 await）。</summary>
    private bool _syncing;

    private Cell? _fullscreen;
    private LiveQuality _fullscreenQuality = LiveQualityExtensions.FullscreenDefault;
    private Image? _fullscreenImage;

    /// <summary>全屏时那句「为什么没有画面」（见 <see cref="PumpFullscreenNote"/>）。</summary>
    private TextBlock? _fullscreenNote;

    /// <summary>给 XAML 用的无参构造（设计器）。**别在运行时用它** —— 它没有机位源，墙上永远是空的。</summary>
    public MultiViewWindow() : this(() => [], null)
    {
    }

    /// <param name="cameras">
    /// 现在还算数的机位（`DesktopServices.Live.Active`）。**是个函数、不是一份快照** ——
    /// 每一次对账都重新问一遍，手机换了端口或新报到了这里才看得见。
    /// </param>
    /// <param name="create">
    /// 建一格（真机上就是 `LiveTile.Start` 外加一次机位名查询）。
    /// ⚠️ 传 <see langword="null"/> = 这面墙不接任何机位（设计器那条路）。
    /// </param>
    /// <param name="logger">
    /// ⚠️ <b>调用方必须传</b>（`AppHost.Logger`）。它是可选参数、默认 `NullLogger` ——
    /// 不传的话这个窗的一切都**静默不落盘**，而编译器一个字都不会说。
    /// 这正是 `AppHost.Logger` 那段注释在警告的事。
    /// </param>
    public MultiViewWindow(
        Func<IReadOnlyList<LiveEndpoint>> cameras,
        Func<LiveEndpoint, Task<LiveTile>>? create,
        Core.Diagnostics.IAppLogger? logger = null)
    {
        InitializeComponent();

        _cameras = cameras;
        _logger = logger ?? Core.Diagnostics.NullLogger.Instance;

        // 每一格默认都看（`new bool[]` 全是 false —— 那就是一开窗整面墙都关着）。
        Array.Fill(_eyes, true);

        // ⚠️ 对账那面墙**归这个窗口造**：它要把阵容摆到下面那些格子上，
        // 而那件事只有窗口做得了（见 `ApplyLineup`）。
        _wall = create is null ? null : new LiveWall(create, ApplyLineup, _logger);

        BuildWall();
        BuildQualityRow();
        BuildLayoutMenu();
        BuildSplitRow();

        // ⚠️ 12 fps 与格子那一路的 ffmpeg 同频：更快只是白烧 CPU，
        // 更慢会让画面看起来「不如手机流畅」。
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / 12),
        };
        _frameTimer.Tick += (_, _) => PumpFrames();

        // 计数一秒问一次就够 —— 它是给人看「现在多少件」的，不是仪表盘。
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => PumpCounts();

        // ⚠️ **对账两秒一次**，不是一秒：手机那边报到本身就 20 秒一次，
        // 而这一拍要问一遍机位表（在内存里，很便宜）—— 但再快也没意义。
        // 两秒够用：手机重启之后，那一格最多两秒就指到新端口上了
        //（只有**新机位**才会去读设备表，见 `MainWindow.CreateLiveTileAsync`）。
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _syncTimer.Tick += async (_, _) => await SyncCamerasAsync();

        Loaded += async (_, _) =>
        {
            ApplyCellCount(_cellCount);
            _frameTimer.Start();
            _statusTimer.Start();
            _syncTimer.Start();

            // ⚠️ **开窗当场先对一次账**，别等第一拍：最常见的一种顺序就是
            // 「先把电脑这边摆好，再点手机上的【开始工作】」—— 等两秒才接上
            // 在一面空墙前面是很长的一段时间。
            await SyncCamerasAsync();

            // ⚠️ 「起」留一条（§6.1：有生命周期的组件）。带上机位数 ——
            // 「为什么只有一格有画面」的答案常常就是这里（压根只报到了 1 台）。
            _logger.Log(
                LogLevel.Info, "多画面",
                $"多画面窗口开了：{_cellCount} 格、{_onlineCount} 台机位在线");
        };

        // ★ T9（P3 拍的「最小化一并停」）：最小化时整面墙的画面都收掉，
        // 还原时自己接回来。收的是**解码与 ffmpeg 两样**—— 光是看不见，CPU 照样烧。
        // ⚠️ 判的是 `WindowState` 而不是 `e.NewState`：最大化/还原都会响这一下，
        // 用它就不必去猜「从最大化直接最小化」这种走法。
        StateChanged += async (_, _) =>
        {
            var shouldStop = WindowState == WindowState.Minimized;

            // 最大化、从最小化还原到最大化…… 都会响；状态没变就别去折腾那些 ffmpeg。
            if (shouldStop == _suspended) return;

            _suspended = shouldStop;

            _logger.Log(
                LogLevel.Info, "多画面",
                shouldStop
                    ? "多画面窗口最小化了：墙上的画面都停了（还原时自己接回来）"
                    : "多画面窗口还原了：墙上的画面接回来");

            // ⚠️ 这一拍要是正好撞上对账在跑，它会直接返回（`_syncing` 那道闸），
            // 最迟两秒后那一拍也会按新的 `_suspended` 摆一次 —— 结果一样，只是慢一点。
            await SyncCamerasAsync();
        };

        Closed += async (_, _) =>
        {
            _frameTimer.Stop();
            _statusTimer.Stop();
            _syncTimer.Stop();

            // ⚠️ 收 tile 这件事**归那面墙**（它才是那些 tile 的主）——
            // 这里再收一遍就是两份主人，而 `Repoint`/对账那边还以为它们活着。
            if (_wall is not null) await _wall.DisposeAsync();

            _logger.Log(LogLevel.Info, "多画面", "多画面窗口关了");
        };
    }

    // ─────────────────────────────────────────────
    // 搭界面
    // ─────────────────────────────────────────────

    /// <summary>
    /// 先摆出九格**空**的，机位由 <see cref="ApplyLineup"/> 那一拍接上来。
    /// </summary>
    /// <remarks>
    /// ⚠️ 从前是「开窗时按机位表建好再摆」—— 那一份快照用完就丢，于是窗口
    /// **永远停在开窗那一刻的机位上**（手机换端口就再也不出画面了）。
    /// 现在格子是固定的、谁挂在这一格上每一拍都在变（见 <see cref="LiveWall"/>）。
    /// </remarks>
    private void BuildWall()
    {
        for (var index = 0; index < MaxCells; index++)
        {
            var cell = new Cell(this, index);

            _cells.Add(cell);
            Wall.Children.Add(cell.Root);
        }
    }

    /// <summary>
    /// 对一次账：把机位表现在这几位摆到格子上。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>它是 <see cref="LiveWall"/> 回头叫的那个回调</b>（不是计时器直接叫的）——
    /// 顺序由那面墙保证：**先摆新的、再收旧的**。反过来的话，界面会有一瞬间
    /// 指着一个已经收掉的 tile。
    /// </remarks>
    private void ApplyLineup(IReadOnlyList<LiveTile?> lineup)
    {
        for (var index = 0; index < _cells.Count; index++)
        {
            // 两种空法**必须分开**（T9）：
            //  · 阵容里**没有这一号**（`index >= lineup.Count`）= 压根没有这么多台机位 → 「未接入」；
            //  · 阵容里**这一号是空的** = 有这台机位，只是现在不看它 → 「已关闭」。
            // 合成一种的话，用户关了眼睛的那一格会写着「未接入」，而那是**假话**
            //（它明明在推，只是你不看）—— 他会跑去找手机，而手机一直好好的。
            if (index >= lineup.Count)
            {
                _cells[index].Adopt(null, turnedOff: false);
                continue;
            }

            var tile = lineup[index];

            _cells[index].Adopt(tile, turnedOff: tile is null);
        }

        _onlineCount = lineup.Count;
        UpdateCameraNote();
    }

    /// <summary>墙上方那句「正在查找…」要不要露出来。</summary>
    /// <remarks>
    /// ⚠️ 一台机位都没有时**格子照摆**（每格写「未接入」），另外补一句说明 ——
    /// 曾经的做法是「没机位就把格子整体收掉」，那让整页只剩一句话、看着像坏了
    ///（需求方 2026-10-02：「改回原来我们自己的渲染」）。
    /// 这句话现在是**真的**：窗口自己在对账，有手机报到就会自己接上来。
    /// </remarks>
    private void UpdateCameraNote() =>
        EmptyNote.Visibility = _onlineCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void BuildQualityRow()
    {
        foreach (var quality in new[] { LiveQuality.P480, LiveQuality.P720, LiveQuality.P1080 })
        {
            var button = new Button
            {
                Content = quality.Label(),
                Style = (Style)FindResource("SecondaryButton"),
                MinWidth = 96,
                Margin = new Thickness(0, 0, 8, 0),
                Tag = quality,
            };

            button.Click += async (_, _) => await ChooseFullscreenQualityAsync(quality);

            QualityRow.Children.Add(button);
        }

        UpdateQualityButtons();
    }

    private void BuildLayoutMenu()
    {
        var menu = new ContextMenu();

        foreach (var count in CellChoices)
        {
            var item = new MenuItem { Header = $"{count} 画面", Tag = count };
            item.Click += (_, _) => ApplyCellCount(count);

            menu.Items.Add(item);
        }

        // ⚠️ 挂在**整个窗口**上而不是每一格上：右键落在格与格的缝里、或者落在
        // 「无信号输入」那几个字上时，挂在格上的菜单不会弹 —— 用户只会觉得
        // 「右键没用」。挂窗口上就没有缝。
        ContextMenu = menu;
    }

    /// <summary>摆成 [count] 格。</summary>
    /// <remarks>
    /// ⚠️ <b>格数由用户选，不由机位数决定。</b>选了 3 格但只有 2 台手机时，
    /// 第三格**照摆**、浅底写「未接入」—— 把它藏掉的话，用户只会以为
    /// 自己选错了，而不知道是那台手机没开共享。
    /// </remarks>
    private void ApplyCellCount(int count)
    {
        // 3×3 的墙要摆 N 格：夹到档位、算列、算行 —— 那三件都在 Core 里
        // （`LiveWall.Layout`，T27② 第 4 批搬下去的），这里只往控件上贴。
        //
        // ⚠️ 下界是 **1**（T9 那档单画面），不是 `CellChoices[0]`（那是菜单上的 2）——
        // 写成后者的话工具栏上那颗「1」会被静默夹成 2 格。
        var wall = LiveWall.Layout(count, 1, MaxCells);

        // ⚠️ 只在**真的变了**的时候记一句：装载时那一次 `_cellCount` 本来就是这个数
        //（而开窗那一条日志已经写了格数），每次都记会在启动时留下两句一样的话。
        // ⚠️ 这一句是**原因**：格数一改，墙那边就会「停了：… 第 N 格现在不看它」，
        // 但那边不知道是用户点了 16 还是 4 —— 用户来问「怎么只出来 4 格」时，
        // 答案在这一句里。
        if (wall.Count != _cellCount)
        {
            _logger.Log(LogLevel.Info, "多画面", $"画面数改成 {wall.Count} 格（原来是 {_cellCount} 格）");
        }

        _cellCount = wall.Count;
        Wall.Columns = wall.Columns;
        Wall.Rows = wall.Rows;

        for (var index = 0; index < _cells.Count; index++)
        {
            _cells[index].Root.Visibility = index < _cellCount
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        TitleText.Text = _cellCount == 1
            ? "实时多画面 · 单画面"
            : $"实时多画面 · {_cellCount} 宫格";

        UpdateSplitButtons();

        // 压字的问题由**说明另占一行**解决（见 .xaml），不是靠收格子。
        UpdateCameraNote();
    }

    /// <summary>
    /// 工具栏上那四档分割（T9）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与右键菜单**并存**，不是替代：菜单里那几档（2 / 3 / 5 / 6 / 7 / 8）没有按钮，
    /// 按钮上那四档也不进菜单 —— 两边合起来才是全部档位。
    /// ⚠️ 选中的那一档用 <c>PrimaryButton</c> 上色（与全屏那排画质按钮同一个口径）——
    /// 不给的话用户点完不知道自己在哪一档。
    /// </remarks>
    private void BuildSplitRow()
    {
        foreach (var count in SplitChoices)
        {
            var button = new Button
            {
                Content = count.ToString(),
                Style = (Style)FindResource("SecondaryButton"),
                MinWidth = 40,
                Margin = new Thickness(0, 0, 6, 0),
                Tag = count,
                ToolTip = $"{count} 格",
            };

            AutomationProperties.SetAutomationId(button, $"LiveSplit{count}");

            button.Click += (_, _) => ApplyCellCount(count);

            SplitRow.Children.Add(button);
        }

        UpdateSplitButtons();
    }

    private void UpdateSplitButtons()
    {
        foreach (Button button in SplitRow.Children)
        {
            if (button.Tag is not int count) continue;

            button.Style = (Style)FindResource(
                count == _cellCount ? "PrimaryButton" : "SecondaryButton");
        }
    }

    /// <summary>
    /// 第 <paramref name="index"/> 格现在该不该起画面（T9）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这是「起几路 ffmpeg」唯一的那个判断。</b>三个口子合到一处：摆不下的格子、
    /// 用户关了眼睛的格子、窗口最小化。散成三处的话，总有一处会漏掉 ——
    /// 而漏掉的表现是「进程还在跑」：用户看不见，风扇听得见。
    /// </remarks>
    private bool IsCellLive(int index) =>
        !_suspended && index < _cellCount && index < _eyes.Length && _eyes[index];

    /// <summary>关掉 / 重新看上第 <paramref name="index"/> 格（T9 的「眼睛」）。</summary>
    private async Task ToggleEyeAsync(int index)
    {
        if (index < 0 || index >= _eyes.Length) return;

        _eyes[index] = !_eyes[index];

        // ⚠️ 加一条日志：这一下**真的会杀掉一路 ffmpeg**（不只是把画面藏起来）——
        // 「画面少了一格」的答案在这里。
        _logger.Log(
            LogLevel.Info, "多画面",
            _eyes[index]
                ? $"第 {index + 1} 格又看上了：给它重新起一路画面"
                : $"第 {index + 1} 格不看了：停掉它那一路画面与解码");

        await SyncCamerasAsync();
    }

    /// <summary>对一次账：机位表 → 格子。</summary>
    /// <remarks>
    /// ⚠️ <b>必须 `await`，不能 `_ =` 把它丢掉</b> —— 与 <see cref="PumpCounts"/> 同一条
    /// 理由：丢掉的异常没有人观察，字不出现而日志里一个字都没有。
    /// </remarks>
    private async Task SyncCamerasAsync()
    {
        // ⚠️ 重入保护：这一拍里要读设备表、还可能收掉几路（每一路最多等它退 2 秒），
        // 叠起来可能超过两秒。不挡的话每一拍都叠一批新动作。
        if (_syncing || _wall is null) return;
        _syncing = true;

        try
        {
            // ⚠️ 传的是**整个机位表**（不是挑出来的那几台）：关掉的那几格必须在阵容里
            // **占着位子**，否则后面几台会往前挪一格（见 `LiveWall.SyncAsync`）。
            // 起不起由 `IsCellLive` 一格一格地说。
            await _wall.SyncAsync(_cameras(), IsCellLive);
        }
        catch (Exception ex)
        {
            // 走到这儿是**意料之外**的异常（正常的「手机没开共享」只是接不上，
            // 不是异常）—— 意料之外的就该吵。
            _logger.Log(LogLevel.Warn, "多画面", $"机位表没对上：{ex.Message}");
        }
        finally
        {
            _syncing = false;
        }
    }

    // ─────────────────────────────────────────────
    // 每帧 / 每秒
    // ─────────────────────────────────────────────

    private void PumpFrames()
    {
        if (_fullscreen is not null)
        {
            _fullscreen.Pump(_fullscreenImage);
            PumpFullscreenNote();
            return;
        }

        for (var index = 0; index < _cellCount && index < _cells.Count; index++)
        {
            _cells[index].Pump(_cells[index].Surface);
        }
    }

    /// <summary>全屏那一格没有画面时，把原因写在黑屏上。</summary>
    /// <remarks>
    /// ⚠️ 「有没有画面」这一判**问的是 <see cref="LiveTile.Latest"/>**，不另写一套超时
    /// —— 两处各判一次的话，迟早会出现「格子说断了、全屏还在放」这种自相矛盾。
    /// </remarks>
    private void PumpFullscreenNote()
    {
        if (_fullscreenNote is null) return;

        var tile = _fullscreen?.Tile;

        var why = tile is null
            ? "这一格已经不在墙上了"
            : tile.Latest() is not null ? null : tile.Problem ?? "无信号输入";

        if (why is null)
        {
            _fullscreenNote.Visibility = Visibility.Collapsed;
            return;
        }

        if (!string.Equals(_fullscreenNote.Text, why, StringComparison.Ordinal)) _fullscreenNote.Text = why;

        _fullscreenNote.Visibility = Visibility.Visible;
    }

    /// <summary>每格问一次计数。</summary>
    /// <remarks>
    /// ⚠️ <b>必须 `await`，不能用 `ContinueWith` + `_ =` 把它丢掉。</b>
    /// 那样写的话，`RenderCounts` 里一抛就是一条**没有人观察的异常** ——
    /// 字不出现，而日志里一个字都没有，查起来完全无从下手。
    /// **2026-10-01 真踩过这个**：颜色在渲染路径里查、查不到，F/T 两个字整晚不出现，
    /// 而假手机的日志证明数据一直好好地拿到了。
    /// </remarks>
    private async void PumpCounts()
    {
        // ⚠️ 重入保护：一格的 HTTP 超时是 5 秒，三格串起来可能超过这一拍的 1 秒。
        // 不挡的话每一拍都叠一批新请求，越叠越多。
        if (_pumpingCounts) return;
        _pumpingCounts = true;

        try
        {
            foreach (var cell in _cells)
            {
                if (!cell.IsOnScreen) continue;

                // `IsOnScreen` 已经保证了 Tile 非空，这里那个 `!` 是给编译器的。
                //
                // ⚠️ **采样在这一拍的最前面，不在 `await` 后面**（T11）：帧率是**两次采样之间**
                // 的差，而下面那个请求最长能等 5 秒。放到后面的话，一格卡住就会把
                // 别格的窗口一起拉长，几格之间的读数没法比 —— 而「哪一格不对」正是这行字要回答的。
                cell.Tile!.SampleReceiveRate();

                await cell.Tile!.RefreshStatusAsync();
                cell.RenderCounts();
            }
        }
        catch (Exception ex)
        {
            // 走到这儿说明是**意料之外**的异常（正常的问不到，`LiveTile` 自己在
            // 「变坏/变好」时各记了一条，不会冒到这里）。意料之外的**就该吵** ——
            // 不做去重：它要是一直响，那正说明有东西在坏。
            _logger.Log(LogLevel.Warn, "多画面", $"计数没画出来：{ex.Message}");
        }
        finally
        {
            _pumpingCounts = false;
        }
    }

    // ─────────────────────────────────────────────
    // 全屏
    // ─────────────────────────────────────────────

    private async Task EnterFullscreenAsync(Cell cell)
    {
        if (_fullscreen is not null || cell.Tile is null) return;

        var tile = cell.Tile;

        _fullscreen = cell;

        _fullscreenImage = new Image
        {
            Stretch = Stretch.Uniform,
            // ⚠️ **把格子里选的那个朝向带过来**（规格原话：「全屏时画面朝向与
            // 小窗口选择的一致」）。不带的话，用户在小窗里把画面转正了，
            // 一双击全屏又躺回去 —— 他会以为双击把设置弄丢了。
            LayoutTransform = new RotateTransform(cell.Rotation),
        };

        UseSmoothScaling(_fullscreenImage);

        // ⚠️ 格子里那个「无信号输入」的 TextBlock 在 `Cell` 里，**不在这一层** ——
        // 不补一个的话，全屏时一路断了就是**一整片黑，一个字都没有**
        //（而全屏正是用户用来看画质清楚不清楚的地方：他只会以为「这一格糊/坏了」）。
        _fullscreenNote = new TextBlock
        {
            Text = "无信号输入",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 520,
            Visibility = Visibility.Collapsed,
        };

        // ⚠️ 不写 `Foreground = (Brush)FindResource(...)`：那一支只在建的时候取一次，
        // 之后用户改系统主题它不跟着换（理由与 `Cell.Tint` 那段完全一样）。
        _fullscreenNote.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabled");

        FullscreenHost.Children.Clear();
        FullscreenHost.Children.Add(_fullscreenImage);
        FullscreenHost.Children.Add(_fullscreenNote);

        FullscreenTitle.Text = tile.Name;
        FullscreenLayer.Visibility = Visibility.Visible;

        // ⚠️ 先告诉手机、成了本地才按新档解（那条顺序的理由见 LiveTile.SetQualityAsync）。
        await tile.SetQualityAsync(_fullscreenQuality);
    }

    private async Task ExitFullscreenAsync()
    {
        var cell = _fullscreen;
        if (cell is null) return;

        _fullscreen = null;
        FullscreenLayer.Visibility = Visibility.Collapsed;
        FullscreenHost.Children.Clear();
        _fullscreenImage = null;
        _fullscreenNote = null;

        // ⚠️ **改回格子那一档**：不改的话那台手机会一直占着高码率，
        // 而没人再看它 —— 白耗它的电与整个局域网的带宽。
        if (cell.Tile is { } tile) await tile.SetQualityAsync(LiveQualityExtensions.Tile);
    }

    private async Task ChooseFullscreenQualityAsync(LiveQuality quality)
    {
        _fullscreenQuality = quality;
        UpdateQualityButtons();

        if (_fullscreen?.Tile is { } tile)
        {
            await tile.SetQualityAsync(quality);
        }
    }

    private void UpdateQualityButtons()
    {
        foreach (Button button in QualityRow.Children)
        {
            if (button.Tag is not LiveQuality quality) continue;

            button.Style = (Style)FindResource(
                quality == _fullscreenQuality ? "PrimaryButton" : "SecondaryButton");
        }

        QualityTradeoff.Text = Tradeoffs.TryGetValue(_fullscreenQuality, out var text) ? text : string.Empty;
    }

    // ─────────────────────────────────────────────
    // 事件
    // ─────────────────────────────────────────────

    /// <summary>
    /// 画面缩放用**线性**，不是最近邻。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与 `EnrollWindow` 那条「二维码的 Image 绝不改 NearestNeighbor」**不冲突**：
    /// 那是给**码**用的 —— 插值会把模块边缘糊成灰边、扫不出来。这是给人看的**画面**，
    /// 用最近邻反而会在缩小时出现锯齿与闪烁。
    /// </remarks>
    private static void UseSmoothScaling(Image image) =>
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (_fullscreen is not null)
        {
            _ = ExitFullscreenAsync();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 全屏里再双击一次就回网格（T9；NVR 的标准手势，不需要菜单）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>这一下从前没有。</b>进全屏那一路（<see cref="Cell"/> 的 `OnMouseDown`）挂在
    /// **格子的 Border** 上，而全屏层盖在它上面、铺满整个窗口 —— 双击落到全屏层上，
    /// 根本冒泡不到格子那儿。所以「再双击回网格」一直只有 Esc 那条路。
    /// </para>
    /// <para>
    /// ⚠️ 全屏层下面那排画质按钮**不会**误触：<c>ButtonBase</c> 自己把
    /// <c>MouseLeftButtonDown</c> 标成 handled，那一下到不了这里。
    /// </para>
    /// </remarks>
    private void OnFullscreenClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;

        e.Handled = true;
        _ = ExitFullscreenAsync();
    }

    /// <summary>一格。</summary>
    /// <remarks>
    /// ⚠️ <see cref="Tile"/> 为 <see langword="null"/> 是**正常的一种格子**：
    /// 用户选了 9 格而只有 2 台手机，剩下 7 格就是这样 —— 浅底、写「未接入」（T10）。
    /// 它与「有机位、但画面没上来」（深底、写明原因）**必须一眼分得开**。
    /// 而它**每一拍都可能换**（见 <see cref="Adopt"/>）。
    /// </remarks>
    private sealed class Cell
    {
        /// <summary>这一格没有机位时写的那句话（T10）。</summary>
        private const string NoSeat = "未接入";

        /// <summary>有机位、但一帧都还没上来时兜底的那句话。</summary>
        private const string NoSignal = "无信号输入";

        /// <summary>
        /// 用户关了眼睛的那一格写的话（T9）。
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>不能写成「未接入」</b>：那台手机在推，只是用户不看它。
        /// 写「未接入」的话，用户会去手机上找原因，而手机一直是好的。
        /// </remarks>
        private const string TurnedOff = "已关闭";

        /// <summary>
        /// 「已关闭」那一格的底色键；字色与「未接入」共用 <see cref="MutedKey"/>。
        /// </summary>
        /// <remarks>
        /// <para>
        /// ⚠️ <b>底色必须与「未接入」那格（<see cref="SlotKey"/>）**不是同一个键**。</b>
        /// 2026-10-07 之前两者长得一模一样、区别只在字，需求方当场点了名（「要一眼分得出」）。
        /// 这一对是个**能失败的检查**（见 `MultiViewWindowCellTests`）。
        /// </para>
        /// <para>
        /// ⚠️ <b>这一格的「颜色差」走底子、不走字。</b>字用 <see cref="MutedKey"/>，
        /// 亮色 <b>5.54:1</b>、暗色 <b>5.68:1</b>。
        /// ⚠️ 这里原来写的是「字不许用琥珀 —— `Warning` <c>#F59E0B</c> 压在
        /// `WarningSurface` 上只有 2.07:1」，那句**已经过期**：亮色 `Warning`
        /// 2026-10-07 改成 orange-700 `#C2410C`，现在那一对是 <b>4.99:1</b>
        ///（暗色 6.79:1）。结论不变，但理由变了 —— 现在是**设计选择**
        /// （带色相的底本身就是这一格的信号），不再是「换字色会不达标」逼出来的。
        /// 这两条一起钉在那条绊线里（亮暗各量一次）。
        /// </para>
        /// </remarks>
        private const string OffSlotKey = "WarningSurface";

        /// <summary>「未接入」那一格的底色。</summary>
        private const string SlotKey = "SurfaceMuted";

        /// <summary>两种空位共用的字色（也是「已关闭」那一格的字色）。</summary>
        private const string MutedKey = "TextSecondary";

        //  ⚠️ 「眼睛」是**画出来的**（内联矢量 Path），不是字形（`◉` / `○` 那种）——
        //  需求方 2026-10-07 拍的：字形看不出是眼睛。坐标系 18×18，两处（按钮上那颗、
        //  空位中间那颗大的）**共用同一份数据**，大的那个走 Viewbox 放大。
        private const string EyeOutline = "M1.8,9 C5.4,4.1 12.6,4.1 16.2,9 C12.6,13.9 5.4,13.9 1.8,9 Z";
        private const string EyePupil = "M9,6.8 A2.2,2.2 0 1 0 9,11.2 A2.2,2.2 0 1 0 9,6.8 Z";
        private const string EyeSlash = "M3.2,14.8 L14.8,3.2";

        /// <summary>眼睛的外形：关着的那一支多一道斜杠（同一个 Path 上换个 Data）。</summary>
        private static Geometry EyeShape(bool slashed) =>
            Geometry.Parse(slashed ? $"{EyeOutline} {EyeSlash}" : EyeOutline);

        private readonly MultiViewWindow _owner;
        private readonly int _index;

        // ⚠️ 这里原来有**九个 `readonly Brush` 字段**（`_countGreen` / `_textSub` / `_videoBg`
        // 那一批），构造里 `FindResource` 取一次就焊死。2026-10-07 全部删掉，改走
        // `SetResourceReference`（见 `Tint` 那段注释）：取一次的做法在「开机就是暗色」
        // 时没问题，但用户**开着程序去改 Windows 主题**时，已经建好的格子会一直停在
        // 亮色 —— 换字典换不掉已经赋给 DP 的那支笔刷。
        private readonly TextBlock _name;
        private readonly Button _rotate;
        private readonly Button _eye;
        private readonly Path _eyeShape;
        private readonly Path _eyeDot;
        private readonly Viewbox _offMark;
        private readonly StackPanel _emptyStack;
        private long _shownAt;

        /// <summary>这一格现在是被用户（或最小化）关着的（见 <see cref="Adopt"/>）。</summary>
        private bool _off;

        public Cell(MultiViewWindow owner, int index)
        {
            _owner = owner;
            _index = index;

            Surface = new Image
            {
                Stretch = Stretch.Uniform,
                // ⚠️ `LayoutTransform` 而不是 `RenderTransform`：前者**参与布局**，
                // 转 90° 之后容器会按新的宽高重新摆；后者只是画的时候转一下，
                // 结果是画面转到一半伸到格子外面、或者根本放不下。
                LayoutTransform = new RotateTransform(0),
            };

            UseSmoothScaling(Surface);

            // ⚠️ 这里**不写**脸色与那句话：由构造最后那一步 `Paint(true, NoSeat)` 统一画。
            Empty = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Visible,
                // ⚠️ 这两行是给 `LiveTile.Problem` 那几句用的：它们比「无信号输入」
                // 长得多（「……手机那边的实时共享可能关着，或者网络不通。」），
                // 默认不折行的话会从格子里伸出去。
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
            };

            // ★ 空位正中那颗**大的叉眼**（2026-10-07 需求方要求「一眼分得出」）：
            // 只有「已关闭」那一格露出来 —— 与「未接入」的差别除了底色，还有这么大一件东西。
            // ⚠️ 与按钮上那颗**共用同一份几何数据**（`EyeOutline`/`EyePupil`/`EyeSlash`），
            // 靠 Viewbox 放大到 34；抄第二份的话，下次改形状只会改到一处。
            // ⚠️ 挂在 `_offMark`（那个 **Viewbox**）上开关，不挂在里面那两支漆上：
            // Viewbox 的宽高是写死的，孩子收起来了它照样占着 34 像素。
            _offMark = IconBox(
                34,
                StrokeShape($"{EyeOutline} {EyeSlash}", MutedKey, 1.5),
                Pupil(MutedKey));

            _offMark.Margin = new Thickness(0, 0, 0, 10);
            _offMark.Visibility = Visibility.Collapsed;

            // ⚠️ `Empty` 挪进这个 StackPanel（原来直接挂在 inner 里）：两样都居中、
            // 又各自是 inner 的孩子的话，它们是**叠在一起**的。
            // 竖排的 StackPanel 量孩子时给的仍是**格子的宽度**，所以 `LiveTile.Problem`
            // 那几句长话照样折得开、伸不出去。
            _emptyStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _emptyStack.Children.Add(_offMark);
            _emptyStack.Children.Add(Empty);

            _name = new TextBlock
            {
                Text = $"机位 {index + 1}",
                Margin = new Thickness(8, 6, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            // ⚠️ AutomationId 挂在**这个名字**上，不在格子的 Border 上：
            // WPF 的 `Border`/`Grid`/`Image` **不产生 UIA 节点**（挂上去也没人看得见），
            // 而 `TextBlock` 有。截图脚本按 id 找控件（不按中文标题找 ——
            // PS 5.1 的控制台代码页会把中文读成乱码）。
            // 双击这个字也会**冒泡到格子**上，所以脚本照样进得了全屏。
            AutomationProperties.SetAutomationId(_name, $"LiveCell{index + 1}");
            AutomationProperties.SetName(_name, $"机位 {index + 1}");

            _rotate = new Button
            {
                Content = "⟳",
                Width = 28,
                Height = 28,
                Style = (Style)((FrameworkElement)owner).FindResource("IconButton"),
                ToolTip = "转 90°",
                Visibility = Visibility.Collapsed,
            };
            _rotate.Click += (_, _) => Rotate();

            // ★ T9 的「眼睛」：关掉这一格（**真的停掉它那一路 ffmpeg**，不是把画面藏起来）。
            // 两笔：外圈那道梭形（含斜杠）走 Stroke，瞳走 Fill —— 换色时两笔一起换。
            // ⚠️ 这两笔的墨色**由 `Paint` 每画一次重指**（跟着底走，见那儿的 `Tint`）。
            _eyeShape = StrokeShape(EyeOutline, MutedKey, 1.5);
            _eyeDot = Pupil(MutedKey);

            _eye = new Button
            {
                Content = IconBox(18, _eyeShape, _eyeDot),
                Width = 28,
                Height = 28,
                Style = (Style)((FrameworkElement)owner).FindResource("IconButton"),
                ToolTip = EyeTooltip(on: true),
                Visibility = Visibility.Collapsed,
            };
            _eye.Click += async (_, _) => await _owner.ToggleEyeAsync(_index);

            // ⚠️ AutomationId 挂在这两颗按钮上（T9）：截图脚本按 id 找，
            // 不按中文标题找 —— PS 5.1 的控制台代码页会把中文读成乱码。
            AutomationProperties.SetAutomationId(_eye, $"LiveCell{index + 1}Eye");

            // 两颗按钮并排钉在右上角。⚠️ 放同一个 StackPanel 里，别再各自 `HorizontalAlignment`——
            // 各自靠右的话它们会**叠在同一个位置**上。
            var corner = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 6, 0),
            };
            corner.Children.Add(_eye);
            corner.Children.Add(_rotate);

            Counts = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 6),
                FontFamily = (FontFamily)((FrameworkElement)owner).FindResource("MonoFont"),
                FontSize = 14,
                // ⚠️ 加了第二行（健康度）之后必须显式居中：不设的话整个块按**最宽那行**
                //     居中，两行各自左对齐 —— 短的那行会偏到一边去。
                TextAlignment = TextAlignment.Center,
                Visibility = Visibility.Collapsed,
            };

            var inner = new Grid();
            inner.Children.Add(Surface);
            inner.Children.Add(_emptyStack);
            inner.Children.Add(_name);
            inner.Children.Add(corner);
            inner.Children.Add(Counts);

            Root = new Border
            {
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Child = inner,
                Tag = this,
            };

            // ⚠️ 同上：指到键上，别在建的时候取一次（`Cell.Tint` 那段注释）。
            Root.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

            Root.MouseLeftButtonDown += OnMouseDown;

            // ⚠️ 新格子生下来就是**空位** —— 而且必须现在画：格子是先摆好、后对账的，
            // 对账那一路碰上「本来就没有机位」会被 `Adopt` 的同对象判断直接跳掉
            //（`null` 与初值 `null` 相等），不在这儿画就永远是裸的默认色。
            // 眼睛这时也收起来：还没有机位，没什么可关的。
            Paint(empty: true, NoSeat, turnedOff: false);
        }

        private static string EyeTooltip(bool on) =>
            on
                ? "不看这一格（停掉它的画面，省一路解码）"
                : "重新看这一格";

        /// <summary>一笔描边（外圈、斜杠那种）。</summary>
        /// <summary>
        /// 一道描边、不填色的形状。墨色收**资源键**而不是笔刷 —— 见 <see cref="Tint"/>。
        /// </summary>
        private static Path StrokeShape(string data, string inkKey, double thickness)
        {
            var path = new Path
            {
                Data = Geometry.Parse(data),
                StrokeThickness = thickness,
                // ⚠️ 外圈那道梭形是**闭合**的，不写这一行它会按默认的黑色填满 ——
                // 那颗眼睛会变成一个实心黑疙瘩。
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };

            path.SetResourceReference(Shape.StrokeProperty, inkKey);

            return path;
        }

        /// <summary>眼睛中间那个瞳。墨色同样走资源键。</summary>
        private static Path Pupil(string inkKey)
        {
            var dot = new Path { Data = Geometry.Parse(EyePupil) };

            dot.SetResourceReference(Shape.FillProperty, inkKey);

            return dot;
        }

        /// <summary>
        /// 把一个 DP 指到资源键上；<paramref name="key"/> 是 <c>null</c> 就是纯白。
        /// </summary>
        /// <remarks>
        /// <para>
        /// ⚠️ <b>全类不再缓存 <c>Brush</c> 字段</b>（2026-10-07 改）。
        /// <c>FindResource</c> 取一次就把那支**冻结**的笔刷焊在 DP 上：换主题字典换不掉
        /// 它。开机就是暗色没问题（构造时取的就是暗色那支），但用户**开着程序去改
        /// Windows 主题**时，已经建好的格子会一直停在亮色 —— 而没有任何东西会喊。
        /// </para>
        /// <para>
        /// <c>SetResourceReference</c> 两个毛病都没有：找不到**只是先不赋值**
        ///（不像 <c>FindResource</c> 那样抛 —— 原来那些字段正是为了躲这个才在构造里取的），
        /// 而且字典一换 WPF **自己重解析**，不需要谁去通知格子。
        /// </para>
        /// <para>
        /// 纯白那一支仍写字面量：它不是主题里的键（两套主题下画面区都是近黑底，
        /// 白不随之变），硬塞一个键反而要多维护一个值不会变的资源。
        /// </para>
        /// </remarks>
        private static void Tint(FrameworkElement target, DependencyProperty dp, string? key)
        {
            if (key is null)
            {
                target.SetValue(dp, Brushes.White);
            }
            else
            {
                target.SetResourceReference(dp, key);
            }
        }

        /// <summary>把两笔拢成一个固定大小的方框，并**缩放到这个大小**。</summary>
        /// <remarks>
        /// ⚠️ 必须走 <c>Viewbox</c>：几何数据是 18×18 的，直接塞进一个 34 的 Grid 里
        /// 不会被放大 —— 它照原样画，外面那块方框只是白占地方（WPF 默认也不裁）。
        /// </remarks>
        private static Viewbox IconBox(double size, Path shape, Path dot)
        {
            var strokes = new Grid();
            strokes.Children.Add(shape);
            strokes.Children.Add(dot);

            return new Viewbox
            {
                Width = size,
                Height = size,
                Child = strokes,
                Stretch = Stretch.Uniform,
            };
        }

        /// <summary>
        /// 这一格的机位；**用户选了几格而机位不够时是 <see langword="null"/>**。
        /// ⚠️ 它**每一拍都可能换**（换了地址的是同一个对象，退了休的才是新的/空的）——
        /// 读它之前那一刻的值不算数，所以别再缓存它。
        /// </summary>
        public LiveTile? Tile { get; private set; }

        /// <summary>
        /// 把这一格改挂到 <paramref name="tile"/> 上（<see langword="null"/> = 这一格没有画面）。
        /// </summary>
        /// <param name="turnedOff">
        /// <see langword="null"/> 的那一格是**哪种空**：<see langword="true"/> = 有这台机位、
        /// 只是现在不看它（T9）；<see langword="false"/> = 压根没有这么多台机位。
        /// </param>
        /// <remarks>
        /// ⚠️ <b>换了一路就得把上一路留下的东西全抹掉</b>：画面、那两个计数、
        /// 那句话。抹不干净的话，新机位还没出画面时屏幕上会是**上一台手机的
        /// 计数和旧图** —— 而那看起来完全正常。
        /// ⚠️ 同对象判断里**必须带上 <paramref name="turnedOff"/>**：关掉一格时
        /// tile 前后都是 <see langword="null"/>，只看 tile 的话这一下会被整个跳掉，
        /// 用户点了眼睛而屏幕上一动不动。
        /// </remarks>
        public void Adopt(LiveTile? tile, bool turnedOff)
        {
            if (ReferenceEquals(Tile, tile) && _off == turnedOff) return;

            Tile = tile;
            _off = turnedOff;

            _name.Text = tile?.Name ?? $"机位 {_index + 1}";

            // ★ T10：**空位**（用户选了 9 格、手机只有 2 台）与**有机位但画面没上来**
            // 原来是同一副样子 —— 都是一块深色 + 一句话，用户分不出今晚该去查哪一格。
            // ★ T9 加了第三种空：**用户自己关掉的**（见 `TurnedOff`）。
            Paint(
                empty: tile is null,
                turnedOff ? TurnedOff : tile?.Problem ?? NoSignal,
                turnedOff);

            UpdateEye();

            Surface.Source = null;
            Counts.Inlines.Clear();
            Counts.Visibility = Visibility.Collapsed;

            // 0 不可能等于任何一帧的时间戳 ⇒ 下一帧一定画得上（`Pump` 拿它去重）。
            _shownAt = 0;

            // 正全屏看着的那一格被收了（机位过期了，或者用户把它关了）：退出来 ——
            // 不退的话全屏层会冻在最后一帧上，标题还写着那台机位，而它已经不在了。
            if (tile is null && ReferenceEquals(_owner._fullscreen, this))
            {
                _ = _owner.ExitFullscreenAsync();
            }
        }

        /// <summary>把「眼睛」画成现在的样子（<see cref="_off"/> 是唯一的准）。</summary>
        /// <remarks>
        /// ⚠️ 就**换一根线**（同一支 Path 换个 <c>Data</c>），不是换一颗控件 ——
        /// 外圈那道梭形两支共用，关着的那支只多一道斜杠。
        /// </remarks>
        private void UpdateEye()
        {
            _eyeShape.Data = EyeShape(_off);
            _eye.ToolTip = EyeTooltip(on: !_off);
        }

        /// <summary>
        /// 把这一格的**脸色**画成 <paramref name="empty"/> 那一种，并写上 <paramref name="note"/>。
        /// </summary>
        /// <remarks>
        /// ⚠️ 全仓**只有这一处**决定「空位」与「有机位、画面没上来」各长什么样（T10：
        /// 原来两者都是一块深色 + 一句话，用户分不出今晚该去查哪一格）。构造函数也走它 ——
        /// 别把这几行抄回构造里：抄一份，两边就会慢慢长歪，而歪掉的那一种（永远没接过
        /// 机位的格子）正是最不容易被看见的。
        /// </remarks>
        private void Paint(bool empty, string note, bool turnedOff)
        {
            // ★ 三种脸色（T10 + 2026-10-07 追加的第三种）：
            //   · 未接入（浅底灰字）· 已关闭（**淡琥珀底**，一眼分得开）
            //   · 有机位没画面（黑底白字）。
            Root.SetResourceReference(
                Border.BackgroundProperty,
                turnedOff ? OffSlotKey : empty ? SlotKey : "VideoBackground");

            // ⚠️ 这几笔的墨色得跟着底走：有画面时格子是近黑的（白墨），空位是浅的（次级灰）。
            // 白是**字面量**、不是资源键 —— 两套主题下画面区都是近黑底，白不随之变。
            var inkKey = empty ? MutedKey : null;

            Tint(_name, TextBlock.ForegroundProperty, inkKey);
            Tint(_eyeShape, Shape.StrokeProperty, inkKey);
            Tint(_eyeDot, Shape.FillProperty, inkKey);

            // ⚠️ 这颗 `⟳` 的墨色**必须自己给** —— 它是全格唯一一笔没指定墨色的字
            //（`_name` / `Empty` / `Counts` 每一笔都显式给了画刷），不给的话它拿到的是
            // WPF 那个默认前景（黑），而它**只在 `empty == false` 时露面 ——
            // 也就是只出现在近黑底上**（`VideoBackground` #0F172A）：黑压黑 **1.18:1**，
            // 等于看不见（这台机器上一直如此，2026-10-07 核 diff 时量出来的）。
            // ⚠️ 这个数**改过一次**：`9c9067c` 的提交消息里写的是「1.09:1」，那是估的；
            // 把整个色板按 WCAG 相对亮度公式重算后是 1.18:1（结论不变，数字错了）。
            // 需求方 2026-10-07：「深底用白色、浅底用黑色，要让用户明显看到这个按钮。」
            // ⚠️ 浅底那一支走 `TextPrimary`（正文墨；主题里从来没有纯黑这一个值，
            // 而 #1E293B 压在浅底上是 **13.98:1** —— 一样是「黑色」，量的不是估的）。
            // 它眼下走不到（浅底的格子这颗按钮是收起来的），留着是给底下那条绊线
            // 一个**能失败**的形状 —— 写死一个颜色它就红。
            Tint(_rotate, TextBlock.ForegroundProperty, empty ? "TextPrimary" : null);
            _rotate.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

            // ⚠️ 眼睛**只有在这一格有机位、或者它正被关着**时才露面：
            // 没有机位就没有什么可关的；而关着的那一格必须留着眼睛，
            // 不然用户再也点不回来。
            _eye.Visibility = empty && !turnedOff ? Visibility.Collapsed : Visibility.Visible;

            _offMark.Visibility = turnedOff ? Visibility.Visible : Visibility.Collapsed;

            Tint(Empty, TextBlock.ForegroundProperty, empty ? MutedKey : "TextDisabled");
            Empty.Text = note;

            // ⚠️ 必须重新露出来：`Pump` 在出了第一帧之后把它按下去了，
            //     而这句话现在可能刚换成**另一句**（换了一路机位）。
            Empty.Visibility = Visibility.Visible;
        }

        public Border Root { get; }

        public Image Surface { get; }

        public TextBlock Counts { get; }

        public TextBlock Empty { get; }

        /// <summary>这一格现在该不该动（在屏上、而且有画面）。</summary>
        public bool IsOnScreen =>
            Root.Visibility == Visibility.Visible && Tile is not null;

        /// <summary>这一格被转到了哪个角度（0 / 90 / 180 / 270）。</summary>
        public double Rotation =>
            Surface.LayoutTransform is RotateTransform rotate ? rotate.Angle : 0;

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2 || Tile is null) return;

            e.Handled = true;
            _ = _owner._fullscreen is null
                ? _owner.EnterFullscreenAsync(this)
                : _owner.ExitFullscreenAsync();
        }

        private void Rotate()
        {
            if (Surface.LayoutTransform is not RotateTransform rotate) return;

            rotate.Angle = (rotate.Angle + 90) % 360;
        }

        /// <summary>把最新一帧贴上去。没有新帧就什么都不做。</summary>
        public void Pump(Image? target)
        {
            var tile = Tile;
            if (target is null || tile is null) return;

            var frame = tile.Latest();

            if (frame is null)
            {
                // ⚠️ 抹平那句「无信号输入」是个**四种情况共用**的说法：手机没开实时共享、
                // 网络不通、这边起不来、断了几次还没接回来。`LiveTile.Problem` 里那几句
                // 是分得清的，就该写出来 —— 不然用户只能看到一格黑着，没有任何下一步。
                var why = tile.Problem ?? "无信号输入";

                if (!string.Equals(Empty.Text, why, StringComparison.Ordinal)) Empty.Text = why;

                // ⚠️ **这句话要能重新露出来。** 出过画面之后 `Empty` 就被按下去了，
                // 而断了之后 `Surface` 里还留着最后那张图 —— 不重新露的话，格子里是
                // **一张冻住的旧画面，一个字都没有**，而它看起来与「正在看」一模一样
                //（`LiveTile.Latest` 那边判定「老画面不算画面」，这一行负责把它说出来）。
                Empty.Visibility = Visibility.Visible;

                // 旧图一起撤掉：留着它，那句话就压在图上（能看见，但底下是几秒前的
                // 现实，看的人会以为「还能看，只是有点卡」）。
                target.Source = null;
                _shownAt = 0;

                return;
            }

            if (frame.CapturedAtMs == _shownAt) return;

            _shownAt = frame.CapturedAtMs;

            var bitmap = BitmapSource.Create(
                frame.Width, frame.Height, 96, 96,
                PixelFormats.Rgb24, null, frame.Rgb, frame.Stride);

            bitmap.Freeze();

            target.Source = bitmap;
            Empty.Visibility = Visibility.Collapsed;
        }

        /// <summary>把那两个字写上去（发货绿、退货红）。</summary>
        /// <remarks>
        /// ⚠️ <b>两个字一直显示</b>（规格原话：「每个画面格下方中央显示**两个字符**
        /// F 为发货…T 为退货」）—— 数是问到了才填。
        /// 拿不到时显示 <c>–</c>，**不是 0**：一个假的 0 会被当成
        /// 「这台机位今天一单没做」，而用户不会去怀疑那两个数字。
        /// </remarks>
        /// <summary>把这一格的推流健康度与计数画到格子下沿。</summary>
        /// <remarks>
        /// ⚠️ 两行是**两件事**，别当成一件事看（T11）：
        /// 下面那行 F/T 是「今天扫了多少发货/退货」（**业务计数**，手机报的），
        /// 上面那行是「这条管子通不通」（**推流健康度**）。
        /// 用户嘴里那句「画面卡」只有看着上面那行才分得出是**手机编不出来**
        /// 还是**网络不行** —— 而这两件事的处置完全不同。
        /// </remarks>
        public void RenderCounts()
        {
            var counts = Tile?.Counts;
            var fps = Tile?.ReceiveFps;

            Counts.Inlines.Clear();

            Counts.Inlines.Add(Tinted(new Run(LiveCountsText.Fps(fps)), "TextDisabled"));

            // ⚠️ 两个丢帧数**没有就不出现**（而不是显示 0）：健康时这一行只有帧率，
            // 挂一串 0 会把「有东西要看了」这个信号淹掉 —— 而这行存在的全部意义
            // 就是让人**一眼**看出哪一格不对劲。
            //
            // ⚠️ 两个名字要分得开：「这边丢」是**这台电脑没跟上**（解码/贴图慢了，
            // 与网线无关），「手机丢」是**手机编码器整段扔掉**（网线再好也救不回来）。
            AppendLoss("这边丢", Tile?.ScreenDropped ?? 0);
            AppendLoss("手机丢", counts?.PhoneDropped ?? 0);

            Counts.Inlines.Add(new LineBreak());

            Counts.Inlines.Add(Tinted(new Run(LiveCountsText.Outbound(counts)), "Success"));
            Counts.Inlines.Add(new Run("   "));
            Counts.Inlines.Add(Tinted(new Run(LiveCountsText.Returned(counts)), "Danger"));

            Counts.Visibility = Visibility.Visible;
        }

        /// <summary>丢帧数非零才把那一段追加上去（T11）。</summary>
        /// <remarks>
        /// 那句话与「0 就不出现」这一判在 Core 里（`LiveCountsText.Loss`）；
        /// 这里只剩摆 <c>Run</c> 与上色。
        /// </remarks>
        private void AppendLoss(string label, long dropped)
        {
            if (LiveCountsText.Loss(label, dropped) is not { } text) return;

            Counts.Inlines.Add(Tinted(new Run(" · "), "TextDisabled"));
            Counts.Inlines.Add(Tinted(new Run(text), "Warning"));
        }

        /// <summary><c>Run</c> 那种墨色也走资源键（同 <see cref="Tint"/> 那段理由）。</summary>
        private static Run Tinted(Run run, string key)
        {
            run.SetResourceReference(Run.ForegroundProperty, key);

            return run;
        }
    }
}
