using System.Windows;

using VidLog.Desktop.Core.Cloud;

namespace VidLog.Desktop.App;

/// <summary>
/// 百度网盘的登录窗：内嵌浏览器 + 等本机回环接住授权码。
/// </summary>
/// <remarks>
/// <para>
/// <b>用法是「先起监听、再开窗、然后 <see cref="Window.ShowDialog"/>」</b> ——
/// 窗自己会在两件事之一发生时关掉：回环接住了回调，或者等够了
/// <see cref="Patience"/> 那么久。关掉之后调用方读
/// <see cref="Callback"/> / <see cref="Failure"/> 就知道结果。
/// </para>
/// <para>
/// ⚠️ <b>它不是模态对话框那种「等用户点按钮」</b>，所以那两个属性是**结果**
/// 而不是「用户选了什么」：<see cref="Callback"/> 是空 = 没等到（超时，
/// 或者用户自己按了取消），<see cref="Failure"/> 非空 = 这个窗压根没起来
/// （最典型的是机器上没装 WebView2 运行时）。
/// </para>
/// <para>
/// ⚠️ <b>窗里不碰授权码</b>：接住它的是 <see cref="BaiduLoopbackListener"/>，
/// 这个窗只负责把页面显示出来。所以这里一行判断逻辑都没有 ——
/// App 层没有测试工程，写在这儿的判断只能靠肉眼。
/// </para>
/// </remarks>
public partial class BaiduLoginWindow : Window
{
    /// <summary>
    /// 等多久就不再等了。
    /// </summary>
    /// <remarks>
    /// 5 分钟。实测（探针，2026-10-14）从打开登录页到接住授权码是 <b>53 秒</b>，
    /// 那还是**一路顺风**的情况（扫码即时确认）。留 5 分钟是给「要翻手机、
    /// 要找回密码、微信要确认」这些真实情况。
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(5);

    private readonly string _passportUrl;
    private readonly BaiduLoopbackListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public BaiduLoginWindow(string passportUrl, BaiduLoopbackListener listener)
    {
        _passportUrl = passportUrl;
        _listener = listener;

        InitializeComponent();

        StatusNote.Text = $"等你在这一页登录并同意（最多 {Patience.TotalMinutes:F0} 分钟）。";
        BrowserNote.Text = "正在打开登录页…";

        // 浏览器控件要等 `EnsureCoreWebView2Async` 才好使，在那之前它是块空白 ——
        // 先藏着，让上面那句「正在打开」露面（不藏的话用户盯着的是一块全白）。
        Browser.Visibility = Visibility.Collapsed;

        Loaded += OnLoaded;
    }

    /// <summary>回环接住的东西；没等到（超时 / 用户取消）就是 <see langword="null"/>。</summary>
    public BaiduAuthCallback? Callback { get; private set; }

    /// <summary>这个窗没起来的原因（给人看的一句话）；正常起来时是 <see langword="null"/>。</summary>
    public string? Failure { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();

            // 百度那一页上的「微信 / 微博 / QQ 登录」是开**新窗**的。
            // 不接住它的话，点了那几颗按钮会什么都不发生（没注册 NewWindowRequested
            // 的 WebView2 直接吞掉这个请求）—— 用户只会以为这几个按钮坏了。
            // 就地导航：反正这一步的终点都是跳回那个回环地址。
            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                Browser.CoreWebView2.Navigate(args.Uri);
            };

            Browser.Source = new Uri(_passportUrl);

            Browser.Visibility = Visibility.Visible;
            BrowserNote.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // ⚠️ **不带 URL**：那个 URL 的查询串里有 client_id（= AppKey）与 state。
            // 它只能出现在浏览器控件里，不许进任何给人看的句子、也不许进日志。
            Failure = "内嵌的登录页打不开：" + ex.Message
                + "。这台机器可能没装 WebView2 运行时（Windows 11 自带、Windows 10 要单独装）——"
                + "装好之后再点一次【登录百度网盘】就行。";

            Close();
            return;
        }

        try
        {
            Callback = await _listener.WaitForCodeAsync(Patience, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户自己按了取消 / 关了窗。不是故障，也不必说什么。
            return;
        }

        // ⚠️ **超时也要关**：留着一个再也等不到东西的窗杵在那儿，
        // 比关掉它更让人以为程序卡住了。超时那句话由调用方去说
        // （它才知道该写在哪块界面上）。
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        // 关窗就等于「不等了」：不取消的话那个 await 会一直挂到 5 分钟，
        // 期间调用方已经往下走了。
        _cts.Cancel();
        _cts.Dispose();

        base.OnClosed(e);
    }
}
