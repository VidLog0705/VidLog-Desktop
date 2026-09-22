// WinForms 只在这一个文件里出现，并且**用别名而不是 using** ——
// 本工程同时开了 UseWPF，两边的 Application / ComboBox / KeyEventArgs 全都同名，
// 直接 using 会让别处的代码突然「类型不明确」。关在这一处，别处不必知道它存在。
using WinForms = System.Windows.Forms;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 托盘图标。
/// </summary>
/// <remarks>
/// <para>
/// 规格 §3.2.1 要求「后台/托盘状态下仍然生效」—— 关窗口只是收进托盘，
/// 应用继续跑、钩子继续收码。
/// </para>
/// <para>
/// 图标借系统自带的应用图标，**不带任何图片资源** —— 与规格 §3.3.6
/// 「不得引入音频素材」是同一条精神：能不给用户塞文件就不塞。
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly SynchronizationContext? _ui;

    public TrayIcon(string tooltip)
    {
        _ui = SynchronizationContext.Current;

        _icon = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = tooltip.Length > 63 ? tooltip[..63] : tooltip,
            Visible = true,
        };

        _icon.DoubleClick += (_, _) => Invoke(UiRequested);
    }

    /// <summary>用户双击图标 —— 要求把窗口显示出来。</summary>
    public event Action? UiRequested;

    /// <summary>用户从菜单选了「退出」。</summary>
    public event Action? ExitRequested;

    /// <summary>建好右键菜单。窗口那边拿到菜单项引用后自己挂。</summary>
    public void BuildMenu(Action onShow, Action onExit)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开 VidLog", null, (_, _) => Invoke(() => onShow()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Invoke(() => onExit()));

        _icon.ContextMenuStrip = menu;
    }

    /// <summary>弹一个气泡。可以从任何线程调。</summary>
    public void Notify(string title, string message)
    {
        // 气泡可能从钩子线程来（比如「后台扫码不可用」），而 NotifyIcon 是 UI 线程的。
        Invoke(() =>
        {
            try
            {
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = message;
                _icon.ShowBalloonTip(5000);
            }
            catch (Exception)
            {
                // 托盘提示失败不是要命的事，不能因此把录制带下去。
            }
        });
    }

    /// <summary>把提示文字换成当前状态（悬浮时显示）。</summary>
    public void UpdateTooltip(string text) =>
        Invoke(() => _icon.Text = text.Length > 63 ? text[..63] : text);

    private void Invoke(Action action)
    {
        if (_ui is null)
        {
            return;
        }

        _ui.Post(_ => action(), null);
    }

    public void Dispose()
    {
        // 先隐藏再释放：不隐藏的话图标会**留在托盘里直到鼠标划过**，
        // 那是个很常见的「程序退了图标还在」的观感问题。
        _icon.Visible = false;
        _icon.Dispose();
    }
}
