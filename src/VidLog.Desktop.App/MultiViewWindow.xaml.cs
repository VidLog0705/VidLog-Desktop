using System.Globalization;
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
/// 「无信号输入」（不隐藏格子：格数是**用户选的**，不是机位数决定的）。
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

    private readonly IReadOnlyList<LiveTile> _tiles;
    private readonly VidLog.Desktop.Core.Diagnostics.IAppLogger _logger;
    private readonly List<Cell> _cells = [];
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _statusTimer;

    private int _cellCount = MaxCells;

    /// <summary>计数那一拍正在跑（见 <see cref="PumpCounts"/> 的重入保护）。</summary>
    private bool _pumpingCounts;
    private Cell? _fullscreen;
    private LiveQuality _fullscreenQuality = LiveQualityExtensions.FullscreenDefault;
    private Image? _fullscreenImage;

    /// <summary>给 XAML 用的无参构造（设计器与测试）。**别在运行时用它**。</summary>
    public MultiViewWindow() : this([])
    {
    }

    /// <param name="logger">
    /// ⚠️ <b>调用方必须传</b>（`AppHost.Logger`）。它是可选参数、默认 `NullLogger` ——
    /// 不传的话这个窗的一切都**静默不落盘**，而编译器一个字都不会说。
    /// 这正是 `AppHost.Logger` 那段注释在警告的事。
    /// </param>
    public MultiViewWindow(IReadOnlyList<LiveTile> tiles, Core.Diagnostics.IAppLogger? logger = null)
    {
        InitializeComponent();

        _tiles = tiles;
        _logger = logger ?? Core.Diagnostics.NullLogger.Instance;

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

        Loaded += (_, _) =>
        {
            ApplyCellCount(_cellCount);
            _frameTimer.Start();
            _statusTimer.Start();

            // ⚠️ 「起」留一条（§6.1：有生命周期的组件）。带上机位数 ——
            // 「为什么只有一格有画面」的答案常常就是这里（压根只报到了 1 台）。
            _logger.Log(
                LogLevel.Info, "多画面",
                $"多画面窗口开了：{_cellCount} 格、{_tiles.Count} 台机位在线");
        };

        Closed += async (_, _) =>
        {
            _frameTimer.Stop();
            _statusTimer.Stop();

            foreach (var cell in _cells)
            {
                // 空格子没有机位可收（用户选了几格而机位不够）。
                if (cell.Tile is { } tile) await tile.DisposeAsync();
            }

            _logger.Log(LogLevel.Info, "多画面", "多画面窗口关了");
        };
    }

    // ─────────────────────────────────────────────
    // 搭界面
    // ─────────────────────────────────────────────

    private void BuildWall()
    {
        for (var index = 0; index < MaxCells; index++)
        {
            var tile = index < _tiles.Count ? _tiles[index] : null;
            var cell = new Cell(this, index, tile);

            _cells.Add(cell);
            Wall.Children.Add(cell.Root);
        }
    }

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
    /// 第三格**照摆**、里面写「无信号输入」—— 把它藏掉的话，用户只会以为
    /// 自己选错了，而不知道是那台手机没开共享。
    /// </remarks>
    private void ApplyCellCount(int count)
    {
        _cellCount = Math.Clamp(count, CellChoices[0], MaxCells);

        // 3×3 的墙要摆 N 格：列数按最接近方形的来，与设计图的九宫格同一个口径。
        Wall.Columns = _cellCount switch
        {
            <= 2 => _cellCount,
            <= 4 => 2,
            <= 6 => 3,
            <= 9 => 3,
            _ => 3,
        };

        Wall.Rows = (_cellCount + Wall.Columns - 1) / Wall.Columns;

        for (var index = 0; index < _cells.Count; index++)
        {
            _cells[index].Root.Visibility = index < _cellCount ? Visibility.Visible : Visibility.Collapsed;
        }

        TitleText.Text = $"实时多画面 · {_cellCount} 宫格";
        EmptyNote.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─────────────────────────────────────────────
    // 每帧 / 每秒
    // ─────────────────────────────────────────────

    private void PumpFrames()
    {
        if (_fullscreen is not null)
        {
            _fullscreen.Pump(_fullscreenImage);
            return;
        }

        for (var index = 0; index < _cellCount && index < _cells.Count; index++)
        {
            _cells[index].Pump(_cells[index].Surface);
        }
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

        FullscreenHost.Children.Clear();
        FullscreenHost.Children.Add(_fullscreenImage);

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
    /// 用户选了 9 格而只有 2 台手机，剩下 7 格就是这样 —— 里面写「无信号输入」。
    /// </remarks>
    private sealed class Cell
    {
        private readonly MultiViewWindow _owner;
        private readonly int _index;
        private readonly Brush _countGreen;
        private readonly Brush _countRed;
        private long _shownAt;

        public Cell(MultiViewWindow owner, int index, LiveTile? tile)
        {
            _owner = owner;
            _index = index;
            Tile = tile;

            // ⚠️ 颜色在这一刻就取好（构造里 `FindResource` 是**验过能用**的 ——
            // 别的几个键就是在这儿取的）。放到每帧的渲染路径里去查，一查不到
            // 就是**一条被丢掉的异常**：字不出现，而没有任何地方会说话。
            _countGreen = (Brush)owner.FindResource("Success");
            _countRed = (Brush)owner.FindResource("Danger");

            Surface = new Image
            {
                Stretch = Stretch.Uniform,
                // ⚠️ `LayoutTransform` 而不是 `RenderTransform`：前者**参与布局**，
                // 转 90° 之后容器会按新的宽高重新摆；后者只是画的时候转一下，
                // 结果是画面转到一半伸到格子外面、或者根本放不下。
                LayoutTransform = new RotateTransform(0),
            };

            UseSmoothScaling(Surface);

            Empty = new TextBlock
            {
                Text = "无信号输入",
                Foreground = (Brush)((FrameworkElement)owner).FindResource("TextDisabled"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Visible,
            };

            var name = new TextBlock
            {
                Text = tile?.Name ?? $"机位 {index + 1}",
                Foreground = Brushes.White,
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
            AutomationProperties.SetAutomationId(name, $"LiveCell{index + 1}");
            AutomationProperties.SetName(name, $"机位 {index + 1}");

            var rotate = new Button
            {
                Content = "⟳",
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 6, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Style = (Style)((FrameworkElement)owner).FindResource("IconButton"),
                ToolTip = "转 90°",
                Visibility = tile is null ? Visibility.Collapsed : Visibility.Visible,
            };
            rotate.Click += (_, _) => Rotate();

            Counts = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 6),
                FontFamily = (FontFamily)((FrameworkElement)owner).FindResource("MonoFont"),
                FontSize = 14,
                Visibility = Visibility.Collapsed,
            };

            var inner = new Grid();
            inner.Children.Add(Surface);
            inner.Children.Add(Empty);
            inner.Children.Add(name);
            inner.Children.Add(rotate);
            inner.Children.Add(Counts);

            Root = new Border
            {
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)((FrameworkElement)owner).FindResource("VideoBackground"),
                BorderBrush = (Brush)((FrameworkElement)owner).FindResource("CardBorder"),
                BorderThickness = new Thickness(1),
                Child = inner,
                Tag = this,
            };

            Root.MouseLeftButtonDown += OnMouseDown;
        }

        /// <summary>这一格的机位；**用户选了几格而机位不够时是 <see langword="null"/>**。</summary>
        public LiveTile? Tile { get; }

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
            if (frame is null || frame.CapturedAtMs == _shownAt) return;

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
        public void RenderCounts()
        {
            var counts = Tile?.Counts;

            Counts.Inlines.Clear();
            Counts.Inlines.Add(new Run(
                $"F {(counts is null ? "–" : counts.Outbound.ToString(CultureInfo.InvariantCulture))}")
            {
                Foreground = _countGreen,
            });
            Counts.Inlines.Add(new Run("   "));
            Counts.Inlines.Add(new Run(
                $"T {(counts is null ? "–" : counts.Returned.ToString(CultureInfo.InvariantCulture))}")
            {
                Foreground = _countRed,
            });

            Counts.Visibility = Visibility.Visible;
        }

        private Brush FindResource(string key) => (Brush)_owner.FindResource(key);
    }
}
