using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Scanning;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 全局低级键盘钩子（<c>WH_KEYBOARD_LL</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>默认一律放行；唯一会吞的是「消费方点头的那一个键」</b>。回调的返回值决定
/// 放行还是吞掉：消费方（<c>KeyboardScanBridge</c>）返回 <see langword="true"/> 时才吞。
/// 规格 §3.2.1 要求「不能与用户正常打字冲突」，那条保证现在落在**消费方的判定**上 ——
/// 判定是保守的（连击 + 合法形状 + 结束符三条同时成立才认），人打字达不到。
/// </para>
/// <para>
/// ⚠️ <b>为什么非吞不可</b>：被吞的那一键是扫码枪的结束符（Enter）。放行的话它会落到
/// 当时有焦点的控件上，实测（2026-10-10）重点在「关掉的窗口自己弹回来」
/// 「一次扫码开两个会话」。详见 <c>KeyboardScanBridge</c> 的类注释。
/// </para>
/// <para>
/// <b>跑在自己的线程上</b>。低级钩子的回调由**安装它的那个线程**执行，
/// 而 Windows 对它有 ~300ms 的超时（<c>LowLevelHooksTimeout</c>），
/// 超时会**静默摘钩**。放 UI 线程上跑，一次布局卡顿就可能让钩子失效。
/// </para>
/// <para>
/// 回调里只做微秒级的事（换算字符、喂判定），结果再派回 UI。
/// 时间戳用 <see cref="Stopwatch"/> 取，**单调**，不受用户改系统时间影响（I11）。
/// </para>
/// </remarks>
public sealed class WindowsKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    // 委托必须由字段持有 —— 否则被 GC 收掉，钩子就静默失效了。
    private readonly LowLevelKeyboardProc _callback;

    private IntPtr _hookId = IntPtr.Zero;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _running;

    /// <summary>装钩的**结果**已经出来（成功或失败）。</summary>
    /// <remarks>
    /// 装钩发生在钩子线程上，而 <see cref="Start"/> 在 UI 线程上 ——
    /// 没有这个信号的话，调用方会在钩子还没装完时就去读 <see cref="IsInstalled"/>，
    /// 读到 false 并据此报「装不上」。表现是**间歇性失败**（实测三次里错一次），
    /// 而且它会被当成本机不支持，很难查。
    /// </remarks>
    private readonly ManualResetEventSlim _installSettled = new(false);

    /// <summary>
    /// 消费方抛异常时留个痕的地方（可空：测试与向导那几处不关心日志）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这条日志**只在出异常时才写**，而它出异常就意味着扫码枪那个结束符会漏出去
    /// （见 <see cref="KeyboardScanBridge"/>）—— 不留痕的话，现场表现是
    /// 「有时候点一下就弹回老窗口」，没有任何东西能查（§6.1）。
    /// </remarks>
    private readonly IAppLogger? _logger;

    public WindowsKeyboardHook(IAppLogger? logger = null)
    {
        _logger = logger;
        _callback = HookCallback;
    }

    /// <summary>
    /// 按键事件。在**钩子线程**上触发。返回 <see langword="true"/> 表示**吞掉**这个键。
    /// </summary>
    /// <remarks>
    /// ⚠️ 返回值的语义就是「不要把这个键转发给前台窗口」。默认（没有订阅者、或返回
    /// <see langword="false"/>）一律放行 —— 见类注释里 §3.2.1 那条保证。
    /// </remarks>
    public event Func<RawKeyEvent, long, bool>? KeyEvent;

    public bool IsInstalled { get; private set; }

    /// <summary>装不上时的原因（I3：不允许静默失效）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 装上钩子并起消息循环。幂等。
    /// </summary>
    /// <remarks>
    /// **会等到装钩出结果再返回** —— 否则调用方读 <see cref="IsInstalled"/>
    /// 会读到「还没装完」，据此误报「装不上」。
    /// </remarks>
    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "VidLog 键盘钩子",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 等结果。两秒足够 —— 装钩本身是瞬时的事，等这么久只可能是线程没起来。
        _installSettled.Wait(TimeSpan.FromSeconds(2));
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();

        // 低级键盘钩子**不需要**模块句柄（那个参数只在钩子过程位于别的 DLL 里时才用）。
        // 传 null 是最常见的写法，也避开了「拿不到主模块句柄」这条失败路径 ——
        // 第一版传了 GetModuleHandle(主模块名)，在单文件发布等形态下会拿到 null。
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _callback, IntPtr.Zero, 0);

        if (_hookId == IntPtr.Zero)
        {
            var code = Marshal.GetLastWin32Error();
            LastError = code == 0
                ? "系统拒绝了钩子安装（未给出错误码）"
                : new Win32Exception(code).Message;

            IsInstalled = false;
            _running = false;
            _installSettled.Set();
            return;
        }

        IsInstalled = true;
        _installSettled.Set();

        // 低级钩子要求安装它的线程**有消息循环**，否则回调永远不会被调用。
        while (_running && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        IsInstalled = false;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        // 默认放行。吞键只发生在下面那个 `swallow` 为真时 —— 见类注释。
        if (code < 0 || !_running)
        {
            return CallNextHookEx(_hookId, code, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var isKeyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var isKeyUp = message is WM_KEYUP or WM_SYSKEYUP;
        var swallow = false;

        if (isKeyDown || isKeyUp)
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

            try
            {
                // 只有一个订阅者（AppHost）；多播下 `Invoke` 只会拿到最后一个返回值，
                // 所以这里按「单个订阅者」理解。真要多订阅者，改成遍历调用列表。
                swallow = KeyEvent?.Invoke(
                    new RawKeyEvent((int)data.VirtualKeyCode, isKeyDown),
                    Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000)) == true;
            }
            catch (Exception ex)
            {
                // 消费方抛异常绝不能把钩子线程带下去 —— 那会让**全系统的键盘**都没反应。
                // 出异常时**放行**（`swallow` 留在 false），宁可不吞也不要卡住键盘。
                //
                // ⚠️ 但**必须留痕**：这一次放行意味着扫码枪的结束符会被漏给前台窗口
                // （表现就是「点一下弹回老窗口」那类怪事）。这条日志是那件事唯一的痕迹。
                _logger?.Log(
                    LogLevel.Warn, "扫码", $"按键事件处理出错，已放行：{ex.GetType().Name}：{ex.Message}");
            }
        }

        // 吞 = 返回非零、**不调** CallNextHookEx（正规写法是返回 1）。放行照旧。
        return swallow ? new IntPtr(1) : CallNextHookEx(_hookId, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_running)
        {
            return;
        }

        _running = false;

        if (_threadId != 0)
        {
            // 消息循环卡在 GetMessage 上，靠投一条消息把它叫醒。
            PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }

        try
        {
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
        catch (ThreadStateException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookId, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
