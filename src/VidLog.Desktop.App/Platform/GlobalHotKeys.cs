using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 全局热键（T15）：<b>VidLog 不在前台时也收得到</b>。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>用 <c>RegisterHotKey</c>，不是复用 <see cref="WindowsKeyboardHook"/>。</b>
/// 那个钩子的契约是「只旁听、绝不吞键」（规格 §3.2.1），拿它做热键的话，
/// 用户在别的程序里按那个键会<b>同时</b>触发 VidLog 与那个程序 ——
/// 而热键存在的全部意义就是「不必先切回 VidLog」。
/// <c>RegisterHotKey</c> 是系统级的<b>独占</b>注册：按下去前台程序收不到，
/// 这也正是 OBS 的做法。
/// </para>
/// <para>
/// ⚠️ <b>注册会被别的程序抢走</b>（同一个组合键是独占的）。抢不到不是异常，
/// 是「这个键没生效」—— 所以 <see cref="TryAdd"/> 返回 <see langword="bool"/> 而不是抛，
/// 并且当场留一条痕（I3：不允许静默失效）。
/// </para>
/// <para>
/// ⚠️ <b>要在窗口有句柄之后建</b>（<c>Loaded</c>）：消息钩子挂在那个 HWND 上，
/// 句柄还没有时 <see cref="HwndSource.FromHwnd"/> 返回 <see langword="null"/>。
/// </para>
/// </remarks>
public sealed class GlobalHotKeys : IDisposable
{
    /// <summary>热键消息（发到挂热键的那个 HWND 上）。</summary>
    private const int WmHotKey = 0x0312;

    /// <summary>按住不放时<b>不重复</b>触发。</summary>
    /// <remarks>
    /// 没有它的话，按住 Ctrl+Alt+R 一秒会开关录像十几次 —— 而那个键的语义是「按一下」。
    /// </remarks>
    private const uint ModNoRepeat = 0x4000;

    private readonly IAppLogger _logger;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = [];
    private int _nextId = 1;
    private bool _disposed;

    /// <param name="owner">挂在哪一个窗口上。它<b>必须有句柄了</b>（`Loaded` 之后）。</param>
    public GlobalHotKeys(Window owner, IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(owner);

        _logger = logger ?? NullLogger.Instance;

        _source = HwndSource.FromHwnd(new WindowInteropHelper(owner).Handle)
            ?? throw new InvalidOperationException(
                "这个窗口还没有句柄 —— 全局热键要在 Loaded 之后挂。");

        _source.AddHook(WndProc);
    }

    /// <summary>
    /// 挂一个热键。返回<b>是不是抢到了</b> —— 抢不到不是异常。
    /// </summary>
    /// <param name="what">给日志和用户看的事由（「开始/停止录像」这种）。</param>
    /// <param name="onPressed">按下时做什么。它在 <b>UI 线程</b>上跑（消息循环就是那个线程）。</param>
    public bool TryAdd(string what, ModifierKeys modifiers, Key key, Action onPressed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(onPressed);

        var id = _nextId++;
        var combo = Describe(modifiers, key);

        if (!RegisterHotKey(
                _source.Handle, id, (uint)modifiers | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(key)))
        {
            var code = Marshal.GetLastWin32Error();

            _logger.Log(
                LogLevel.Warn,
                "热键",
                $"{what}（{combo}）没挂上：{new Win32Exception(code).Message}"
                    + " —— 多半是别的程序占着同一个组合键");

            return false;
        }

        _handlers[id] = onPressed;

        // ⚠️ 挂上了也要留一条（§6.1：有生命周期的组件，起 / 停 / 失败各一条）。
        // 只记失败的话，「按了没反应」永远分不出是没挂上、还是挂上了而那一段坏了。
        _logger.Log(LogLevel.Info, "热键", $"{what} 挂上了：{combo}");

        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotKey || _disposed)
        {
            return IntPtr.Zero;
        }

        if (!_handlers.TryGetValue(wParam.ToInt32(), out var run))
        {
            return IntPtr.Zero;
        }

        // 这是我们自己注册的那一个键（`RegisterHotKey` 是独占的）—— 不再往下传。
        handled = true;

        try
        {
            run();
        }
        catch (Exception ex)
        {
            // ⚠️ 热键那一头抛了**绝不能顺着消息循环冒出去**：`WndProc` 里漏出去的异常
            // 会把整个应用带下去 —— 而那正是用户按了一下键的时刻。
            _logger.Log(LogLevel.Warn, "热键", $"热键那一下出错了：{ex.Message}");
        }

        return IntPtr.Zero;
    }

    /// <summary>热键写成人看得懂的那一行（`Ctrl+Alt+R`）。日志、提示都用它。</summary>
    private static string Describe(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(4);

        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        parts.Add(key.ToString());

        return string.Join("+", parts);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var id in _handlers.Keys)
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _source.RemoveHook(WndProc);

        // ⚠️ 「停」也留一条（§6.1），但**真的挂过才记** —— 一个都没挂上时
        // 上面每次失败已经各记了一条，这里再来一句只是重复。
        if (_handlers.Count > 0)
        {
            _logger.Log(LogLevel.Info, "热键", $"热键全撤了（{_handlers.Count} 个）");
        }

        _handlers.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
