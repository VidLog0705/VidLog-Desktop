using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using VidLog.Desktop.Core.Rendering;
using ZXing;
using ZXing.QrCode;

namespace VidLog.Desktop.Core.Upload;

/// <summary>
/// 一块网卡上的一个候选地址。抽出来是为了让「挑哪个」这件事**不依赖真实网卡**就能测。
/// </summary>
/// <param name="Address">IPv4 地址的字符串形式。</param>
/// <param name="IsUp">网卡是否是 Up 的。</param>
/// <param name="IsLoopback">是不是回环。</param>
/// <param name="HasGateway">这块网卡有没有默认网关 —— **这是"手机大概能连上"最强的信号**。</param>
public sealed record LanCandidate(
    string Address,
    bool IsUp,
    bool IsLoopback,
    bool HasGateway = false);

/// <summary>
/// 挑一个「手机大概能连上」的本机 IPv4 地址，写进入网二维码（规格 §3.4.5）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不能直接用回放服务的 <c>BaseUrl</c>：那是 <c>http://+:8720/</c> ——
/// **通配地址**，绑定时挺好用，写进二维码里手机没法连（它不是任何一个可路由的地址）。
/// </para>
/// <para>
/// ⚠️ <b>这里挑出来的只是"大概"</b>：一台机器可能同时插着有线、无线、
/// 虚拟网卡（VMware / Hyper-V / VPN）。真正的可达性只有手机自己知道，
/// 所以**扫码连不上时那条"手填地址"的兜底必须留着**（§2.2：手机与电脑
/// 可能不在同一网络）。这个函数负责挑一个最像的，不负责保证它对。
/// </para>
/// </remarks>
public static class LanAddress
{
    /// <summary>从候选里挑一个；挑不出来返回 <see langword="null"/>。</summary>
    /// <remarks>
    /// 排序规则：**有网关的优先**（说明这块网卡真的连着一个网），
    /// 其余按原顺序。回环、Up 都不是的、APIPA（169.254.x.x）、非 IPv4 一律排除。
    /// </remarks>
    public static string? Pick(IEnumerable<LanCandidate> candidates) =>
        candidates
            .Where(c => c.IsUp && !c.IsLoopback && IsUsable(c.Address))
            .OrderByDescending(c => c.HasGateway)
            .Select(c => c.Address)
            .FirstOrDefault();

    /// <summary>读真实网卡。</summary>
    public static string? Discover()
    {
        try
        {
            var candidates = new List<LanCandidate>();

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (IsVirtualLike(nic))
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Count > 0;

                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    candidates.Add(new LanCandidate(
                        address.Address.ToString(),
                        nic.OperationalStatus == OperationalStatus.Up,
                        IPAddress.IsLoopback(address.Address),
                        hasGateway));
                }
            }

            return Pick(candidates);
        }
        catch (NetworkInformationException)
        {
            // 读不出来不是错误 —— 界面会显示「没挑到地址」，让用户手填。
            return null;
        }
    }

    /// <summary>能不能拿它当"手机连得上"的地址。</summary>
    private static bool IsUsable(string address) =>
        IPAddress.TryParse(address, out var ip)
        && ip.AddressFamily == AddressFamily.InterNetwork
        && !IPAddress.IsLoopback(ip)
        // APIPA：没拿到 DHCP 时自己编的地址，路由不到任何地方。
        && !address.StartsWith("169.254.", StringComparison.Ordinal);

    /// <summary>
    /// 像虚拟网卡 / 隧道的那种。
    /// </summary>
    /// <remarks>
    /// 名字匹配是**启发式**，不是判据 —— 但它错的时候只是"没挑到最合适的那个"，
    /// 而挑错了的代价是用户扫不通、要手填地址。宁可漏掉一块真网卡，
    /// 也不要把 Hyper-V 那个 <c>172.x</c> 写进二维码：那个地址手机**永远连不上**。
    /// </remarks>
    private static bool IsVirtualLike(NetworkInterface nic) =>
        nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel
        || Contains(nic.Name) || Contains(nic.Description);

    private static bool Contains(string text) =>
        VirtualHints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] VirtualHints =
    [
        "virtual", "vmware", "hyper-v", "vethernet", "loopback", "tap-", "tun", "vpn", "docker", "wsl",
    ];
}

/// <summary>
/// 入网二维码的内容与图形（规格 §3.4.5，2026-09-24 决定：**配对码由二维码取代**）。
/// </summary>
/// <remarks>
/// <para>
/// <b>二维码里只放两样东西：电脑端的可达地址 + 一次性短时效的令牌。</b>
/// <b>绝不含凭据</b> —— 凭据只在 <c>claim</c> 那一步发放，而且只发一次。
/// </para>
/// <para>
/// ⚠️ <b>"必须站在主机屏幕前"这个前提一个字都没放松。</b> 原配对码的用意是
/// 「只有站在主机屏幕前的人才知道那串码」；二维码**原地继承**它 ——
/// 它显示在电脑端屏幕上，扫不到就进不来。
/// </para>
/// <para>
/// ⚠️ 令牌是**明文走在局域网里**的（和原来的 6 位码一样，都是明文 HTTP）。
/// 所以这一层挡的是「不在屏幕前的人」，**不挡**「同在局域网里抓包的人」。
/// 这一点与旧实现**没有变化**，不要误以为二维码更安全。
/// </para>
/// </remarks>
public static class EnrollQr
{
    /// <summary>二维码约定里那个最小的静区（四周留白）宽度，单位是模块。</summary>
    /// <remarks>
    /// 自己补，不依赖编码库 —— 静区不足的二维码在屏幕上**看着正常、扫不出来**，
    /// 那是排查起来最费劲的一种坏法。自己补就能用测试钉住。
    /// </remarks>
    public const int QuietZoneModules = 4;

    /// <summary>二维码里那串文本。</summary>
    public static string Payload(string host, int port, string token) =>
        $"vidlog://connect?host={host}&port={port.ToString(System.Globalization.CultureInfo.InvariantCulture)}&token={token}";

    /// <summary>
    /// 把内容编成一张模块矩阵：<c>[x, y]</c> 为 <see langword="true"/> 表示深色。
    /// </summary>
    /// <remarks>
    /// 返回矩阵而不是位图：Core 层不该知道 WPF / GDI 怎么画，
    /// 而矩阵**能被测试**（编出来再解回去，见 <c>EnrollQrTests</c>）。
    /// </remarks>
    public static bool[,] Modules(string payload, int quietZone = QuietZoneModules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentOutOfRangeException.ThrowIfNegative(quietZone);

        var matrix = new QRCodeWriter().encode(payload, BarcodeFormat.QR_CODE, 0, 0)
            ?? throw new InvalidOperationException("二维码编码返回了空矩阵");

        var width = matrix.Width + quietZone * 2;
        var height = matrix.Height + quietZone * 2;
        var modules = new bool[width, height];

        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                // ⚠️ ZXing 的 BitMatrix 索引顺序是 [x, y] 还是 [y, x] 在两种移植里不一样，
                // 写反了的表现是**二维码整体转置**——仍然是个合法图形，但扫不出来。
                // 所以这里不靠"读文档"，靠 EnrollQrTests 的往返测试钉住。
                modules[x + quietZone, y + quietZone] = matrix[x, y];
            }
        }

        return modules;
    }

    /// <summary>
    /// 把模块矩阵摊成一幅灰度图：**一个模块一个字节**，深色 0、浅色 255。
    /// </summary>
    /// <remarks>
    /// 转调 <see cref="ModuleBitmap.Pixels"/> —— 那个约定现在只有那一处。
    /// 原来它写在本类里，而第二个用户（一维条码 <see cref="Code128"/>）一来
    /// 就会抄第二遍，两份约定迟早会有一份写反。
    /// </remarks>
    public static byte[] Pixels(bool[,] modules) => ModuleBitmap.Pixels(modules);
}
