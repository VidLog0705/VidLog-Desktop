using VidLog.Desktop.Core.Configuration;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 写进 Run 键的那条命令行（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// ⚠️ 这是整套开机自启动里唯一一处「写错了会让 Windows 启动**别的程序**」的地方，
/// 而它的坏法很安静：路径里带空格却不加引号时，注册表那一条**登记成功**、
/// 开机时也**真的尝试了**，报错在别的进程里 —— 从 VidLog 这一侧什么都看不出来。
/// 所以哪怕只有一行，也要留一条能红的。
/// </remarks>
public class StartupCommandTests
{
    [Fact]
    public void 带空格的路径要加引号()
    {
        // 不加引号 Windows 会把它从空格切开，去启动 `C:\Program.exe`。
        Assert.Equal(
            "\"C:\\Program Files\\VidLog\\VidLog.exe\"",
            StartupCommand.Build(@"C:\Program Files\VidLog\VidLog.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 拿不到路径时不猜一个(string? exePath)
    {
        // ⚠️ 猜一个路径写进去，后果与上面那条一样：开机启动一个不存在甚至不对的东西。
        // 如实回 null，由 `StartupRegistration.Apply` 把那句「没法登记」说给用户。
        Assert.Null(StartupCommand.Build(exePath));
    }

    // ── 自动重启的参数（T31）───────────────────────────────────────

    [Fact]
    public void 自动重启的参数自己解得上()
    {
        // ⚠️ 造参数与解参数**必须成对**：只改一边的话，新进程不会去等旧进程 ——
        // 于是相机（dshow 独占）与回放端口抢不到，而表现只是「重启之后打不开」。
        // 这条就是那一对的闸。
        Assert.Equal(4321, StartupCommand.WaitForPid(StartupCommand.RestartArguments(4321).Split(' ')));
    }

    [Fact]
    public void 开关夹在别的参数中间也认得出来()
    {
        Assert.Equal(4321, StartupCommand.WaitForPid(["--别的", "--wait-for-pid", "4321", "--再一个"]));
    }

    [Theory]
    [InlineData]                                   // 一个参数都没有
    [InlineData("--别的", "1")]                    // 压根不是自动重启
    [InlineData("--wait-for-pid")]                 // 开关后面没数
    [InlineData("--wait-for-pid", "abc")]          // 不是个数
    [InlineData("--wait-for-pid", "0")]            // 0 不是能被等的进程
    [InlineData("--wait-for-pid", "-3")]
    public void 读不出等哪个进程时按普通启动走(params string[] args)
    {
        // 回 null = 按普通启动启动，**不抛异常**：这是启动路径的第一行代码，
        // 为一个坏参数把应用拦在门外比忽略它更糟。
        Assert.Null(StartupCommand.WaitForPid(args));
    }
}
