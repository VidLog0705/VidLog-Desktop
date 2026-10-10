using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
// ⚠️ 同理：WinForms 那侧也有一个 `Brushes`（`System.Drawing.Brushes`），
// 不钉的话 `Brushes.White` 会解析成**画图那个** —— 它根本不是 `Brush`，
// 报错只说「参数不对」，而真正的问题是类型选错了（与上面 `Image` 同一个坑）。
using Brushes = System.Windows.Media.Brushes;
// ⚠️ 同理：WinForms 那侧也有一个 `Point`（`System.Drawing.Point`）。
// 识别框那几段折线用的是 WPF 的几何类型。
using Point = System.Windows.Point;
using VidLog.Desktop.App.Platform;
using VidLog.Desktop.Core;
using VidLog.Desktop.Core.Camera;
using VidLog.Desktop.Core.Commands;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Live;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Punches;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Rendering;
using VidLog.Desktop.Core.Scanning;
using VidLog.Desktop.Core.Search;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.App;

/// <summary>
/// 主窗的**取景画面**那一块：取帧、画识别框、全屏。
/// </summary>
/// <remarks>
/// ⚠️ 2026-10-10 从 <c>MainWindow.xaml.cs</c> 里拆出来：加了「全屏」与「识别框按
/// 真实画面算」之后那个文件到了 839 行，撞上 <c>DesktopServicesTests</c> 的 800 行硬闸
/// （母仓 AGENTS.md §5）。搬的是**整块、零行为变化** —— 字段、取帧循环、识别框几何、
/// 全屏进出，都与主干共用同一个 partial 类，一个标识符都没改。
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>
    /// 取景画面的刷新。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>它是个「拉」的定时器，不是「推」的</b>：帧由 ffmpeg 那两条路投进
    /// <see cref="AppHost.Preview"/> 的单槽，界面在这里取最新一帧。
    /// 反过来的话（拿到帧就 <c>Dispatcher.Invoke</c>）等于**在管道的读线程上
    /// 做界面工作** —— 而那条线程一慢，管道就满，堵住的后果是录制进程被强杀、
    /// MKV 尾部丢掉（§54.2 真机复现过）。
    /// </para>
    /// <para>
    /// 30 fps 的间隔与配置向导预览区用的是同一个数（33ms）。
    /// </para>
    /// </remarks>
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    /// <summary>画面那块位图；尺寸变了就重建（灰度帧与彩色帧的尺寸不一样）。</summary>
    private WriteableBitmap? _frameBitmap;

    /// <summary>上一次真的画上帧的时刻（<see cref="Environment.TickCount64"/>）。</summary>
    /// <remarks>
    /// 判据是「<see cref="PreviewStaleAfter"/> 没帧就把说明放回来」而不是「进程还在不在」：
    /// 出画面的那两条路（识码、录制）**都在进程之间交接**，
    /// 而交接期的空档不该在屏幕上闪一下「没有画面」。
    /// </remarks>
    private long _lastFrameAtMs;

    /// <summary>
    /// 画面上那一幅的**宽高比**（真实画面那一块，不是带补边的整幅帧）。
    /// </summary>
    /// <remarks>
    /// 预览卡片按它定高（<see cref="FitPreviewCard"/>）。默认 16:9 —— 还没出画面时
    /// 先按这一档摆着，出画面之后按真值走。
    /// </remarks>
    private double _pictureAspect = 16.0 / 9.0;

    /// <summary>进全屏之前把窗口的形状存下来，退出时逐项还原（需求方：回原窗口原始尺寸）。</summary>
    private (WindowState State, WindowStyle Style, ResizeMode Resize, double Left, double Top,
        double Width, double Height)? _fullscreenRestore;

    /// <summary>现在是不是全屏看着取景画面。</summary>
    private bool IsPreviewFullscreen => _fullscreenRestore is not null;

    /// <summary>取景框「多久没有新帧，就把那句说明放回来」。</summary>
    /// <remarks>
    /// ⚠️ <b>10 秒是需求方 2026-10-09 拍的</b>（A3）。实测交接空档（停取景 → 起采集）
    /// 是 **2.5–4.5 秒**，而从前写死 1 秒 ⇒ <b>每一次交接都会在屏上闪一句「没有画面」</b>。
    /// 10 秒留了一倍余量；真断流照样会提示、照样记日志（就是下面那一条）。
    /// </remarks>
    private static readonly TimeSpan PreviewStaleAfter = TimeSpan.FromSeconds(10);

    /// <summary>识别框每个角的臂长（像素）。</summary>
    /// <remarks>
    /// ⚠️ 它是**四条直角边的长度**，不是框的粗细 —— 需求方要的是「四角实线直角、
    /// 互不相连」，所以每条臂短了看着像四颗点、长了四条边就快连成一圈了。
    /// 小框上由 <see cref="RecognitionBox.Corners"/> 按短边夹住，不会穿插。
    /// </remarks>
    private const double RecognitionBoxArmLength = 28;

    /// <summary>
    /// 预览区右上角那个时间水印。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 它读的是 <b>可信时钟</b>（<c>TrustedClock</c>），不是系统墙钟 ——
    /// 与录像里烧进去的那个时间同源。用户拿它核对快递单上的手写时间，
    /// 所以两者**必须是一个时间**。
    /// </para>
    /// <para>
    /// ⚠️ 那句话本身在 <see cref="PreviewTexts.Watermark"/>（T27② 第 4 批），
    /// 连同「未校准时不许显示一个时间」那条规矩；这里只剩**取哪一个时钟**、
    /// 以及未校准时换一支警示色。
    /// </para>
    /// </remarks>
    private void UpdatePreviewClock()
    {
        var clock = _host.Services.TrustedClock;

        PreviewClockText.Text = PreviewTexts.Watermark(clock.IsCalibrated, clock.Now);

        // ⚠️ **两种状态都要给颜色**，一个三元了事。
        //
        // 原先只给未校准那一支（另一支一个字都不动、靠 XAML 里那颗写死的
        // `Foreground="White"`），于是**校准成功之后它会一直挂着警示色** ——
        // 那次会话里水印已经写着可信时间了，颜色还在喊「不可信」，而用户学会
        // 忽略这个颜色之后，下一次真未校准就没人看了。
        // 而且当时那个警示色是琥珀（`#FFF59E0B`，比白字还亮），压在明亮的车间
        // 画面上**比白色更难读**。
        // ⚠️ 那句**只对旧值成立**：亮色 `Warning` 2026-10-07 已改成 orange-700
        // `#C2410C`（相对亮度 0.153，白是 1.00）—— 它现在比白字**清楚得多**，
        // 所以「可读性」这条论据**收回**。留下的是语义那条：两个状态都得给颜色。
        // 据此这条只钉「两个状态各有各的颜色」。（2026-10-07 需求方拍板：改。）
        //
        // ⚠️ 颜色**只在这里给**（XAML 那颗字上不再写 `Foreground`）：
        // 两处各写一份的话，「哪种状态是什么色」就有了两个来源。
        // ⚠️ 走 `SetResourceReference`：`Warning` 那支要是用 `FindResource` 取，
        // 取到的是一支**冻结的**笔刷，用户开着程序改系统主题时它不跟着换。
        if (clock.IsCalibrated)
        {
            PreviewClockText.Foreground = Brushes.White;
        }
        else
        {
            PreviewClockText.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        }
    }

    /// <summary>
    /// 把最新一帧画到取景框上；超过 <see cref="PreviewStaleAfter"/> 没帧就退回那句说明。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这是**拉**，不是推：帧由出画面的那个进程投进单槽，界面自己来取。
    /// 反过来（读线程上直接 <c>Dispatcher.Invoke</c>）等于在管道的读线程上做界面工作，
    /// 而那条线程一慢，管道就满 —— 录制进程会被强杀、MKV 尾部丢掉（§54.2）。
    /// </remarks>
    private void RefreshPreview()
    {
        var frame = _host.Preview.TakeLatest();

        if (frame is null)
        {
            // 判据是「超过 PreviewStaleAfter 没有新帧」而不是「进程还在不在」：出画面的那两个
            // （识码、录制）在交接时本来就有空档，空档不该在屏幕上闪成一句话。
            if (PreviewImage.Visibility == Visibility.Visible
                && Environment.TickCount64 - _lastFrameAtMs > PreviewStaleAfter.TotalMilliseconds)
            {
                PreviewImage.Source = null;
                PreviewImage.Visibility = Visibility.Collapsed;
                // 全屏那一层同样跟着空掉；全屏按钮也没意义了（没画面可看）。
                FullscreenImage.Source = null;
                FullscreenImage.Visibility = Visibility.Collapsed;
                FullscreenBoxPath.Visibility = Visibility.Collapsed;
                PreviewFullscreenButton.Visibility = Visibility.Collapsed;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                UpdatePreviewHint();

                // 画面没了，框也跟着走 —— 没有画面时**没有任何东西在解码**，
                // 那条框此时说的话是假的（它标的是「只认这一片」）。
                RecognitionBoxPath.Visibility = Visibility.Collapsed;

                // ⚠️ 这一条是**必须**的（§6.1）：上面那句提示写着「原因会记在通知里」，
                // 不留痕的话那句话就是假的 —— 而这正是 §6.1 点名的那个坑：
                // 「有个用户可见通道」看起来像缺口被满足了，其实没有。
                //
                // ⚠️ 只在**由有到无的那一次**记：判据与提示文字是同一处，
                // 而画面空着的时候这个分支不会再进来（`PreviewImage` 已经不是 Visible），
                // 所以长期没画面不会把日志刷满（§6.1「重复的问题只在变了的时候记」）。
                _host.Logger.Log(
                    VidLog.Desktop.Core.Diagnostics.LogLevel.Warn, "预览",
                    _host.Coordinator.IsWorking
                        ? "工作期间取景画面断了（录制本身不受影响）"
                        : "取景画面断了");

                // 全屏中画面断了就自己退出全屏：留在满屏黑底上，用户看不到
                // 上面那句「没有画面」的说明（它在被盖住的预览区里），
                // 只剩一块黑屏 —— 回到窗口态刚好把那句话露出来。
                ExitPreviewFullscreen();
            }

            return;
        }

        // ⚠️ 位图**不能只建一次**：两种画面的尺寸不一样
        // （识码那一路是 640×480，录制那一路是 640×360）——
        // 2026-10-09 起**两路都是彩色的**，只是框不一样大（从前识码那一路是灰度）。
        if (_frameBitmap is null
            || _frameBitmap.PixelWidth != frame.Width
            || _frameBitmap.PixelHeight != frame.Height)
        {
            _frameBitmap = new WriteableBitmap(
                frame.Width, frame.Height, 96, 96, PixelFormats.Rgb24, null);
        }

        _frameBitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height), frame.Rgb, frame.Stride, 0);

        // ⚠️ 上屏的是**真实画面那一块**，滤镜链补的那圈边一个像素都不画 ——
        // 需求方 2026-10-10：画面要铺满，一条黑边都不留。
        //
        // 那圈边是**滤镜链自己补的，不是相机烧进画面的**（2026-10-10 实测）：
        // 待扫那一路的取景帧写死 640×480，而它的输入是按**用户的录制规格**开的
        // （本机 1920×1080）—— `scale=640:480:force_original_aspect_ratio=decrease`
        // 把 16:9 缩成 640×360，`pad` 再上下各补 60 px 纯黑。
        // 本机量到的正是这个数：窗口里 88 px 容器底色（4:3 的帧贴进 1.65:1 的预览区）
        // ＋ 帧内上下各 69 px 纯黑（那圈 pad 被等比放大之后）。
        //
        // ⚠️ 裁在**显示**这一层，管道一根手指都没动：真正喂解码器的那一片由
        // `PrerecordController.ToGray` 按同一个 `PreviewFrame.Picture` 裁，共用一份数据。
        // 全屏那一层看的是**同一个源**（两张 `Image` 共用，写一次两处都变）。
        var picture = frame.PictureRect;
        BitmapSource shown = picture.X != 0 || picture.Y != 0
            || picture.Width != frame.Width || picture.Height != frame.Height
                // ⚠️ 每帧新建一个 `CroppedBitmap`：它只是围着 `_frameBitmap` 转的一层很薄的
                // 视图（位图那份数据没有第二份拷贝），十二分之一秒一个的分配可以忽略。
                // 建一次反复用则要赌它跟着位图重新取像素 —— 那是没验过的事，不赌。
                ? new CroppedBitmap(_frameBitmap,
                    new Int32Rect(picture.X, picture.Y, picture.Width, picture.Height))
                : _frameBitmap;
        PreviewImage.Source = shown;
        FullscreenImage.Source = shown;

        // 卡片按画面的宽高比定高（只在比例变了那一跳做，不是每帧都摆布局）。
        var aspect = (double)picture.Width / picture.Height;
        if (Math.Abs(aspect - _pictureAspect) > 1e-6)
        {
            _pictureAspect = aspect;
            FitPreviewCard();
        }

        _lastFrameAtMs = Environment.TickCount64;

        if (PreviewImage.Visibility != Visibility.Visible)
        {
            PreviewImage.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewFullscreenButton.Visibility = Visibility.Visible;
            FullscreenImage.Visibility = Visibility.Visible;
        }

        // ⚠️ 帧尺寸两种（待扫 640×480、录制 640×360），黑边不一样宽 ——
        // 位图换了尺寸的那一跳上面已经重建过，这里每次都重算是**把尺寸这条
        // 依赖整个消掉**（一次几条线段的算术，30ms 一跳可以忽略）。
        UpdateRecognitionBox();
    }

    /// <summary>
    /// 让预览卡片的高矮跟着画面的宽高比 —— 画面正好铺满，一条黑边都不留。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 只封<b>上限</b>（<c>MaxHeight</c>），不写死高度：窗口矮下去的时候宁可让画面
    /// 左右留一点边，也不能被顶出可视区（<c>Stretch</c> 会把它压回行高以内）。
    /// </para>
    /// <para>
    /// ⚠️ <b>窗口高度不足时左右仍会留边</b>（那时卡片比画面更「扁」）—— 要连那种情况
    /// 也填满，只能裁掉画面本身，那是另一回事。同理，4:3 的源（本机摄像头切「原生」
    /// 档）卡片比预览区那一行还高，封顶之后左右也留边。需求方当前的规格是
    /// 1920×1080（16:9），本机量到 928×553 的行里卡片是 928×522，封顶用不上。
    /// </para>
    /// </remarks>
    private void FitPreviewCard()
    {
        // ActualWidth 是 0 的时候（还没布局）不摆 —— 摆下去会得到一张 0 高的卡片。
        if (PreviewCard.ActualWidth <= 0)
        {
            return;
        }

        PreviewCard.MaxHeight = PreviewCard.ActualWidth / _pictureAspect;
    }

    /// <summary>
    /// 把识别框画到画面上（四角直角，互不相连）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 位置与那四段折线都算在 <see cref="RecognitionBox"/> 里（有测试盖着）——
    /// 这里只负责「摆上去」。几何一旦搬进代码后置，本仓就没有任何测试工程能盖住它了。
    /// <para>
    /// ⚠️ 画的是**正在显示的这幅画面的同一比例**，而不是容器的比例：画面等比缩放后
    /// 两侧（或上下）有黑边，拿容器尺寸乘比例会整体偏。
    /// </para>
    /// </remarks>
    private void UpdateRecognitionBox()
    {
        // 窗口里与全屏里各画一份（同一处几何）—— 只有一个看得见，另一个自带收起。
        DrawRecognitionBox(PreviewImage, RecognitionBoxPath, PreviewArea);
        DrawRecognitionBox(FullscreenImage, FullscreenBoxPath, PreviewFullscreenArea);
    }

    /// <summary>
    /// 把识别框画到 <paramref name="path"/> 上（它盖住的画面是 <paramref name="image"/>）。
    /// </summary>
    /// <param name="image">被盖住的那幅画面（判决它有没有、有多大）。</param>
    /// <param name="path">要画的四段直角。</param>
    /// <param name="container">框所在的那一格（<see cref="RecognitionBox.Fit"/> 要它的尺寸）。</param>
    private void DrawRecognitionBox(
        Image image, System.Windows.Shapes.Path path, FrameworkElement container)
    {
        if (image.Visibility != Visibility.Visible
            || image.Source is not BitmapSource source
            || container.ActualWidth <= 0
            || container.ActualHeight <= 0
            // ⚠️ 「摄像头识别」关着时**根本没有解码器**（两路的解码器都跟着它），
            // 那时画一条「只认这一片」的框就是一句假话 —— 与上面画面没了那条同理。
            || !_host.Settings.CameraRecognition)
        {
            path.Visibility = Visibility.Collapsed;
            return;
        }

        // ⚠️ 传的必须是**上屏那一幅**的尺寸 —— 它已经是按 `PreviewFrame.Picture`
        // 裁掉补边之后的那一块（见 `RefreshPreview`），所以框直接按它算，不再补偏移。
        // 传成整幅帧的尺寸（待扫那一路是 640×480）框就会整体偏、还偏得不一样多，
        // 表现是「眼看着在框里却认不出来」而另一种状态下又能认 —— 那种错没地方查。
        var box = RecognitionBox.Fit(
            container.ActualWidth, container.ActualHeight,
            source.PixelWidth, source.PixelHeight);

        var geometry = new PathGeometry();

        // 一个 Path 里的**四个 figure** —— 每个 figure 自己一条折线、不闭合，
        // 所以四条边之间没有任何连接（连起来就是普通矩形框了）。
        foreach (var (x1, y1, x2, y2, x3, y3) in box.Corners(RecognitionBoxArmLength))
        {
            var figure = new PathFigure
            {
                StartPoint = new Point(x1, y1),
                IsClosed = false,
                IsFilled = false,
            };

            figure.Segments.Add(new LineSegment(new Point(x2, y2), isStroked: true));
            figure.Segments.Add(new LineSegment(new Point(x3, y3), isStroked: true));

            geometry.Figures.Add(figure);
        }

        path.Data = geometry;
        path.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 进全屏：无边框、铺满**整个显示器**（连任务栏一起盖住），画面等比铺满不变形。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不用 <c>WindowState=Maximized</c>：最大化只占**工作区**（任务栏那条留白），
    /// 而需求方要的是「铺满显示器」。所以显式按**显示器**的边界摆窗口
    /// （<c>Screen.Bounds</c> 是物理像素，乘上「设备→DIP」的换算才对得上）。
    /// <para>
    /// ⚠️ 退出时**逐项还原**（<see cref="_fullscreenRestore"/>）—— 用户要的是「回原窗口
    /// 原始尺寸」，所以状态、有无边框、能不能缩放、位置与宽高一个都不能漏。
    /// </para>
    /// </remarks>
    private void EnterPreviewFullscreen()
    {
        if (IsPreviewFullscreen)
        {
            return;
        }

        _fullscreenRestore = (WindowState, WindowStyle, ResizeMode, Left, Top, Width, Height);

        var screen = System.Windows.Forms.Screen.FromHandle(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);

        // 设备像素 → DIP：高 DPI 下直接拿 Screen.Bounds 会摆到屏幕外面去。
        var toDips = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                     ?? Matrix.Identity;

        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Left = screen.Bounds.Left * toDips.M11;
        Top = screen.Bounds.Top * toDips.M22;
        Width = screen.Bounds.Width * toDips.M11;
        Height = screen.Bounds.Height * toDips.M22;

        FullscreenImage.Source = _frameBitmap;
        FullscreenImage.Visibility = _frameBitmap is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewFullscreenLayer.Visibility = Visibility.Visible;
        UpdateRecognitionBox();

        // 焦点落到退出按钮上：这样按一下 Esc/回车能出来（键盘用户不必去点它）。
        FullscreenExitButton.Focus();

        _host.Logger.Log(LogLevel.Info, "预览", "取景画面已铺满全屏");
    }

    /// <summary>退出全屏，把窗口还原成进来之前的样子。</summary>
    private void ExitPreviewFullscreen()
    {
        if (_fullscreenRestore is not { } saved)
        {
            return;
        }

        _fullscreenRestore = null;
        PreviewFullscreenLayer.Visibility = Visibility.Collapsed;

        // ⚠️ 顺序：先设回状态再摆位置。最大化时直接摆 Left/Width 是无效的
        // （最大化窗口的位置由系统管），得先 Normal 摆好再回最大化。
        WindowStyle = saved.Style;
        ResizeMode = saved.Resize;
        WindowState = WindowState.Normal;
        Left = saved.Left;
        Top = saved.Top;
        Width = saved.Width;
        Height = saved.Height;

        if (saved.State == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        UpdateRecognitionBox();
        _host.Logger.Log(LogLevel.Info, "预览", "已退出全屏，窗口回到原尺寸");
    }

    /// <summary>【全屏】按钮。</summary>
    private void OnEnterPreviewFullscreen(object sender, RoutedEventArgs e) => EnterPreviewFullscreen();

    /// <summary>【退出全屏】按钮。</summary>
    private void OnExitPreviewFullscreen(object sender, RoutedEventArgs e) => ExitPreviewFullscreen();

    /// <summary>全屏时按 Esc 退出。</summary>
    /// <remarks>
    /// ⚠️ 只在**全屏中**接管 Esc：没全屏时把它放过去，别的对话框/控件该收还收得到。
    /// </remarks>
    private void OnPreviewFullscreenKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !IsPreviewFullscreen)
        {
            return;
        }

        e.Handled = true;
        ExitPreviewFullscreen();
    }

    /// <summary>
    /// 预览区中央那行说明。
    /// </summary>
    /// <remarks>
    /// ⚠️ ⚠️ 三句话与它们的**先后次序**在 <see cref="PreviewTexts.Hint"/>
    /// （T27② 第 4 批）；这里只剩**把三个事实读出来**：
    /// 有没有 FFmpeg、有没有摄像头、在不在工作。
    /// 「已装 FFmpeg 但找不着设备」那条与「工作中却没有画面」那条在界面上
    /// 意思完全不同，次序排错了会把一台配置齐全、只是还没开工的机器
    /// 显示成像是坏了。
    /// </remarks>
    private void UpdatePreviewHint() =>
        PreviewHintText.Text = PreviewTexts.Hint(
            hasFfmpeg: _host.Services.FfmpegPath is not null,
            hasCamera: _host.DeviceName.Length != 0,
            isWorking: _host.Coordinator.IsWorking);
}
