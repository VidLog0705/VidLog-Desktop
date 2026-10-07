using VidLog.Desktop.Core.Web;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 入网配对窗口「发不出二维码」那四句话（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// ⚠️ 四条分支要用户做的事**完全不同**（配端口 / 看主窗口 / 执行一条 <c>netsh</c> /
/// 去接网线），所以「说串了」不是措辞问题而是**把人指错方向**。
/// 它们原先长在 <c>EnrollWindow</c> 里，那个工程没有测试工程。
/// </remarks>
public class EnrollBlockersTests
{
    private const string 地址 = "http://192.168.101.20:8080";

    [Fact]
    public void 服务没装配时说端口设空了()
    {
        var text = EnrollBlockers.Blocker(
            hasServer: false, baseUrl: null, usingFallback: false,
            fallbackReason: null, port: 8080, hasAddress: true);

        Assert.NotNull(text);
        Assert.Contains("没有装配", text);
    }

    [Fact]
    public void 服务没起来时让人去看主窗口那一段()
    {
        var text = EnrollBlockers.Blocker(
            hasServer: true, baseUrl: null, usingFallback: false,
            fallbackReason: null, port: 8080, hasAddress: true);

        Assert.NotNull(text);
        Assert.Contains("没起来", text);
        Assert.Contains("主窗口", text);
    }

    [Fact]
    public void 只绑到本机时要把那条命令整条给出来()
    {
        // ⚠️ 这一条最要紧：用户照着它执行一次就好了，所以 **URL 里的端口必须是
        // 真的那一个** —— 抄错一个数字，他会以为命令没用，然后放弃。
        var text = EnrollBlockers.Blocker(
            hasServer: true, baseUrl: 地址, usingFallback: true,
            fallbackReason: "没权限", port: 8080, hasAddress: true);

        Assert.NotNull(text);
        Assert.Contains(地址, text);
        Assert.Contains("没权限", text);
        Assert.Contains("netsh http add urlacl url=http://+:8080/ user=Everyone", text);
    }

    [Fact]
    public void 四种原因各说各的_不许串()
    {
        // ⚠️ 「服务没起来」与「只绑到本机」说成一句的话，用户会去执行一条
        // 根本不对症的命令，而那条命令**不会报错**（只是没用）。
        var 没装配 = EnrollBlockers.Blocker(false, 地址, true, "没权限", 8080, true);
        var 没起来 = EnrollBlockers.Blocker(true, null, true, "没权限", 8080, true);
        var 只本机 = EnrollBlockers.Blocker(true, 地址, true, "没权限", 8080, true);
        var 没地址 = EnrollBlockers.Blocker(true, 地址, false, null, 8080, false);

        Assert.NotNull(没装配);
        Assert.NotNull(没起来);
        Assert.NotNull(只本机);
        Assert.NotNull(没地址);

        Assert.DoesNotContain("netsh", 没装配);
        Assert.DoesNotContain("netsh", 没起来);
        Assert.DoesNotContain("netsh", 没地址);
        Assert.DoesNotContain("局域网地址", 只本机);

        Assert.Equal(4, new[] { 没装配, 没起来, 只本机, 没地址 }.Distinct().Count());
    }

    [Fact]
    public void 都好时不给一句话()
    {
        // null 与「有一句空话」是两回事：调用处据此决定那一段显不显示。
        Assert.Null(EnrollBlockers.Blocker(
            hasServer: true, baseUrl: 地址, usingFallback: false,
            fallbackReason: null, port: 8080, hasAddress: true));
    }
}
