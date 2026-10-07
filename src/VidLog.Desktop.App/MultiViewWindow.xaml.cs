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

namespace VidLog.Desktop.App;

/// <summary>
/// 实时多画面（规格 §3.8，需求方 2026-10-01 定形）。
/// </summary>
/// <remarks>
/// <para>
/// <b>默认九宫格；右键选画面数（2~9）；选几就摆几格</b>——机位不够时空格显示
/// 「未接入」（浅底；不隐藏格子：格数是**用户选的**，不是机位数决定的）。
/// 每格右上角一颗转动按钮（点一次 90°），双击进全屏，全屏里能换画质。
/// 每格下方居中显示 <c>F</c>（发货，绿）与 <c>T</c>（退货，红）。
/// </para>
/// <para>
/// ⚠️ <b>F / T 只在屏幕上，绝不进视频水印</b>（规格 §3.8 ⑦）。这一层压根不碰视频 ——
/// 「不写进去」是结构上成立的，不是靠自觉。
/// </para>
/// </remarks>
public partial class MultiViewWindow : Window
{
    /// <summary>能选的画面数（需求方逐字定的这几档）。</summary>
    private static readonly int[] CellChoices = [2, 3, 4, 5, 6, 7, 8, 9];

    private const int MaxCells = 9;

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

    private int _cellCount = MaxCells;

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

        // ⚠️ 对账那面墙**归这个窗口造**：它要把阵容摆到下面那些格子上，
        // 而那件事只有窗口做得了（见 `ApplyLineup`）。
        _wall = create is null ? null : new LiveWall(create, ApplyLineup, _logger);

        BuildWall();
        BuildQualityRow();
        BuildLayoutMenu();

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
            // 机位比格子少是常态（用户选了几格而只有两台手机）—— 剩下的格子留空。
            _cells[index].Adopt(index < lineup.Count ? lineup[index] : null);
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

    /// <summary>
    /// 摆成 [count] 格。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>格数由用户选，不由机位数决定。</b>选了 3 格但只有 2 台手机时，
    /// 第三格**照摆**、浅底写「未接入」—— 把它藏掉的话，用户只会以为
    /// 自己选错了，而不知道是那台手机没开共享。
    /// </remarks>
    private void ApplyCellCount(int count)
    {
        // 3×3 的墙要摆 N 格：夹到菜单上的档位、算列、算行 —— 那三件都在 Core 里
        // （`LiveWall.Layout`，T27② 第 4 批搬下去的），这里只往控件上贴。
        var wall = LiveWall.Layout(count, CellChoices[0], MaxCells);

        _cellCount = wall.Count;
        Wall.Columns = wall.Columns;
        Wall.Rows = wall.Rows;

        for (var index = 0; index < _cells.Count; index++)
        {
            _cells[index].Root.Visibility = index < _cellCount
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        TitleText.Text = $"实时多画面 · {_cellCount} 宫格";

        // 压字的问题由**说明另占一行**解决（见 .xaml），不是靠收格子。
        UpdateCameraNote();
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
            await _wall.SyncAsync(_cameras());
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
            Foreground = (Brush)FindResource("TextDisabled"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 520,
            Visibility = Visibility.Collapsed,
        };

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

        private readonly MultiViewWindow _owner;
        private readonly int _index;
        private readonly Brush _countGreen;
        private readonly Brush _countRed;
        private readonly Brush _countDim;
        private readonly Brush _countWarn;
        private readonly Brush _textSub;
        private readonly Brush _videoBg;
        private readonly Brush _slotBg;
        private readonly TextBlock _name;
        private readonly Button _rotate;
        private long _shownAt;

        public Cell(MultiViewWindow owner, int index)
        {
            _owner = owner;
            _index = index;

            // ⚠️ 颜色在这一刻就取好（构造里 `FindResource` 是**验过能用**的 ——
            // 别的几个键就是在这儿取的）。放到每帧的渲染路径里去查，一查不到
            // 就是**一条被丢掉的异常**：字不出现，而没有任何地方会说话。
            _countGreen = (Brush)owner.FindResource("Success");
            _countRed = (Brush)owner.FindResource("Danger");

            // 推流健康度那一行（T11）用的两个：帧率是**说明**（暗），丢帧是**要看的**（琥珀）。
            _countDim = (Brush)owner.FindResource("TextDisabled");
            _countWarn = (Brush)owner.FindResource("Warning");

            // ★ T10 的两种格子各有各的脸色（见 `Paint`）：空位是浅底 + 次级灰字，
            // 「有机位没画面」是黑底 + 白字 —— 两句话之外再给一眼就能看出的底色差。
            _textSub = (Brush)owner.FindResource("TextSecondary");
            _videoBg = (Brush)owner.FindResource("VideoBackground");
            _slotBg = (Brush)owner.FindResource("SurfaceMuted");

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
                Margin = new Thickness(0, 6, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Style = (Style)((FrameworkElement)owner).FindResource("IconButton"),
                ToolTip = "转 90°",
                Visibility = Visibility.Collapsed,
            };
            _rotate.Click += (_, _) => Rotate();

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
            inner.Children.Add(Empty);
            inner.Children.Add(_name);
            inner.Children.Add(_rotate);
            inner.Children.Add(Counts);

            Root = new Border
            {
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(6),
                BorderBrush = (Brush)((FrameworkElement)owner).FindResource("CardBorder"),
                BorderThickness = new Thickness(1),
                Child = inner,
                Tag = this,
            };

            Root.MouseLeftButtonDown += OnMouseDown;

            // ⚠️ 新格子生下来就是**空位** —— 而且必须现在画：格子是先摆好、后对账的，
            // 对账那一路碰上「本来就没有机位」会被 `Adopt` 的同对象判断直接跳掉
            //（`null` 与初值 `null` 相等），不在这儿画就永远是裸的默认色。
            Paint(empty: true, NoSeat);
        }

        /// <summary>
        /// 这一格的机位；**用户选了几格而机位不够时是 <see langword="null"/>**。
        /// ⚠️ 它**每一拍都可能换**（换了地址的是同一个对象，退了休的才是新的/空的）——
        /// 读它之前那一刻的值不算数，所以别再缓存它。
        /// </summary>
        public LiveTile? Tile { get; private set; }

        /// <summary>
        /// 把这一格改挂到 <paramref name="tile"/> 上（<see langword="null"/> = 摘空）。
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>换了一路就得把上一路留下的东西全抹掉</b>：画面、那两个计数、
        /// 那句话。抹不干净的话，新机位还没出画面时屏幕上会是**上一台手机的
        /// 计数和旧图** —— 而那看起来完全正常。
        /// </remarks>
        public void Adopt(LiveTile? tile)
        {
            if (ReferenceEquals(Tile, tile)) return;

            Tile = tile;

            _name.Text = tile?.Name ?? $"机位 {_index + 1}";

            // ★ T10：**空位**（用户选了 9 格、手机只有 2 台）与**有机位但画面没上来**
            // 原来是同一副样子 —— 都是一块深色 + 一句话，用户分不出今晚该去查哪一格。
            Paint(tile is null, tile is null ? NoSeat : tile.Problem ?? NoSignal);

            Surface.Source = null;
            Counts.Inlines.Clear();
            Counts.Visibility = Visibility.Collapsed;

            // 0 不可能等于任何一帧的时间戳 ⇒ 下一帧一定画得上（`Pump` 拿它去重）。
            _shownAt = 0;

            // 正全屏看着的那一格被收了（机位过期了）：退出来 —— 不退的话全屏层会
            // 冻在最后一帧上，标题还写着那台机位，而它已经不在了。
            if (tile is null && ReferenceEquals(_owner._fullscreen, this))
            {
                _ = _owner.ExitFullscreenAsync();
            }
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
        private void Paint(bool empty, string note)
        {
            Root.Background = empty ? _slotBg : _videoBg;
            _name.Foreground = empty ? _textSub : Brushes.White;
            _rotate.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

            Empty.Foreground = empty ? _textSub : _countDim;
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

            Counts.Inlines.Add(new Run(LiveCountsText.Fps(fps)) { Foreground = _countDim });

            // ⚠️ 两个丢帧数**没有就不出现**（而不是显示 0）：健康时这一行只有帧率，
            // 挂一串 0 会把「有东西要看了」这个信号淹掉 —— 而这行存在的全部意义
            // 就是让人**一眼**看出哪一格不对劲。
            //
            // ⚠️ 两个名字要分得开：「这边丢」是**这台电脑没跟上**（解码/贴图慢了，
            // 与网线无关），「手机丢」是**手机编码器整段扔掉**（网线再好也救不回来）。
            AppendLoss("这边丢", Tile?.ScreenDropped ?? 0);
            AppendLoss("手机丢", counts?.PhoneDropped ?? 0);

            Counts.Inlines.Add(new LineBreak());

            Counts.Inlines.Add(new Run(LiveCountsText.Outbound(counts)) { Foreground = _countGreen });
            Counts.Inlines.Add(new Run("   "));
            Counts.Inlines.Add(new Run(LiveCountsText.Returned(counts)) { Foreground = _countRed });

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

            Counts.Inlines.Add(new Run(" · ") { Foreground = _countDim });
            Counts.Inlines.Add(new Run(text) { Foreground = _countWarn });
        }

        private Brush FindResource(string key) => (Brush)_owner.FindResource(key);
    }
}
