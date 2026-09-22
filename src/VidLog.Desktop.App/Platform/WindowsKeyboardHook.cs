using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VidLog.Desktop.Core.Scanning;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 全局低级键盘钩子（<c>WH_KEYBOARD_LL</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>只旁听，绝不吞键</b>：回调里一律 <c>CallNextHookEx</c> 把事件放行。
/// 规格 §3.2.1 要求「不能与用户正常打字冲突」，这就是那条要求的结构性保证 ——
/// 不靠判定逻辑写对，而是**根本没有拦的代码路径**。
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

    public WindowsKeyboardHook()
    {
        _callback = HookCallback;
    }

    /// <summary>按键事件。在**钩子线程**上触发，消费方自己负责派回 UI 线程。</summary>
    public event Action<RawKeyEvent, long>? KeyEvent;

    public bool IsInstalled { get; private set; }

    /// <summary>装不上时的原因（I3：不允许静默失效）。</summary>
    public string? LastError { get; private set; }

    /// <summary>装上钩子并起消息循环。幂等。</summary>
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
            return;
        }

        IsInstalled = true;

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
        // 一律放行 —— 这是「不吞键」的落点，不是为了性能。
        if (code < 0 || !_running)
        {
            return CallNextHookEx(_hookId, code, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var isKeyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var isKeyUp = message is WM_KEYUP or WM_SYSKEYUP;

        if (isKeyDown || isKeyUp)
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

            try
            {
                KeyEvent?.Invoke(
                    new RawKeyEvent((int)data.VirtualKeyCode, isKeyDown),
                    Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000));
            }
            catch (Exception)
            {
                // 消费方抛异常绝不能把钩子线程带下去 —— 那会让**全系统的键盘**都没反应。
            }
        }

        return CallNextHookEx(_hookId, code, wParam, lParam);
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
