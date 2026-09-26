using VidLog.Desktop.Core.Upload;
using ZXing;
using ZXing.Common;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 入网二维码（规格 §3.4.5，2026-09-24：**配对码由二维码取代**）。
/// </summary>
/// <remarks>
/// 这个文件里最要紧的是那条**往返测试**：编出来再解回去。
/// 少了它，矩阵的轴序、静区、编码参数**任何一个写错都看不出来**——
/// 二维码画在屏幕上仍然是个像模像样的方块，只有拿手机去扫才知道不行。
/// </remarks>
public class EnrollQrTests
{
    private const string Host = "192.168.1.23";
    private const int Port = 8720;
    private const string Token = "k7Qm3Zp1Rt";

    private static string Payload => EnrollQr.Payload(Host, Port, Token);

    // ─────────────────────────────────────────────
    // 往返：编出来能解回去
    // ─────────────────────────────────────────────

    [Fact]
    public void 二维码能解回原串()
    {
        var text = Decode(EnrollQr.Modules(Payload));

        Assert.Equal(Payload, text);
    }

    [Fact]
    public void 解回来的串里带着地址与令牌()
    {
        // 上面那条只证明"编解码自洽"。这条证明**内容对** ——
        // 少一个参数、参数名拼错，往返测试照样是绿的。
        var text = Decode(EnrollQr.Modules(Payload));

        Assert.Contains($"host={Host}", text, StringComparison.Ordinal);
        Assert.Contains($"port={Port}", text, StringComparison.Ordinal);
        Assert.Contains($"token={Token}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 二维码里不含凭据之类的长串()
    {
        // 二维码**绝不含凭据**（§3.4.5）：凭据只在 claim 那一步发放，且只发一次。
        // 这条测试是防"以后有人图省事把凭据也塞进来"。
        var text = Decode(EnrollQr.Modules(Payload));

        Assert.DoesNotContain("credential", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(text.Length < 120, $"二维码内容不该这么长：{text.Length}");
    }

    // ─────────────────────────────────────────────
    // 静区
    // ─────────────────────────────────────────────

    [Fact]
    public void 四周有静区()
    {
        // 静区不足的二维码**看着正常、扫不出来**，属于最难排查的一类坏法。
        // 所以四周那一圈必须全是浅色（自己的模块全是 false）。
        var modules = EnrollQr.Modules(Payload);
        var width = modules.GetLength(0);
        var height = modules.GetLength(1);

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < EnrollQr.QuietZoneModules; y++)
            {
                Assert.False(modules[x, y], $"上边第 {y} 行不该有深色模块");
                Assert.False(modules[x, height - 1 - y], $"下边第 {y} 行不该有深色模块");
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < EnrollQr.QuietZoneModules; x++)
            {
                Assert.False(modules[x, y], $"左边第 {x} 列不该有深色模块");
                Assert.False(modules[width - 1 - x, y], $"右边第 {x} 列不该有深色模块");
            }
        }
    }

    [Fact]
    public void 矩阵是方的且比静区大()
    {
        var modules = EnrollQr.Modules(Payload);

        Assert.Equal(modules.GetLength(0), modules.GetLength(1));
        Assert.True(modules.GetLength(0) > EnrollQr.QuietZoneModules * 2);
    }

    // ─────────────────────────────────────────────
    // 挑地址
    // ─────────────────────────────────────────────

    [Fact]
    public void 挑地址_优先挑有网关的那块()
    {
        // 虚拟网卡（Hyper-V / VMware）通常**没有**默认网关，而它有地址、
        // 也报 Up —— 不按网关排的话很可能就挑到它，而那个地址手机永远连不上。
        var picked = LanAddress.Pick([
            new LanCandidate("172.28.96.1", IsUp: true, IsLoopback: false, HasGateway: false),
            new LanCandidate("192.168.1.23", IsUp: true, IsLoopback: false, HasGateway: true),
        ]);

        Assert.Equal("192.168.1.23", picked);
    }

    [Fact]
    public void 挑地址_排除回环_未启用_与自赋地址()
    {
        var picked = LanAddress.Pick([
            new LanCandidate("127.0.0.1", IsUp: true, IsLoopback: true),
            new LanCandidate("192.168.1.50", IsUp: false, IsLoopback: false),
            // APIPA：没拿到 DHCP 时自己编的，路由不到任何地方。
            new LanCandidate("169.254.10.7", IsUp: true, IsLoopback: false),
            new LanCandidate("10.0.0.8", IsUp: true, IsLoopback: false, HasGateway: true),
        ]);

        Assert.Equal("10.0.0.8", picked);
    }

    [Fact]
    public void 挑地址_一个都没有时返回空_不是抛()
    {
        // 挑不出来不是错误：界面显示"没挑到地址"，用户手填。
        // 抛的话这台机器整个入不了网 —— 而它本来还能手填。
        Assert.Null(LanAddress.Pick([new LanCandidate("127.0.0.1", IsUp: true, IsLoopback: true)]));
        Assert.Null(LanAddress.Pick([]));
    }

    [Fact]
    public void 挑地址_非IPv4的候选排除掉()
    {
        var picked = LanAddress.Pick([
            new LanCandidate("fe80::1", IsUp: true, IsLoopback: false, HasGateway: true),
            new LanCandidate("192.168.0.9", IsUp: true, IsLoopback: false),
        ]);

        Assert.Equal("192.168.0.9", picked);
    }

    // ─────────────────────────────────────────────
    // 脚手架
    // ─────────────────────────────────────────────

    /// <summary>把矩阵放大画成灰度图，再用同一个库解回来。</summary>
    /// <remarks>
    /// <para>
    /// <b>字节走 <see cref="EnrollQr.Pixels"/></b>（界面喂给屏幕的就是这一份），
    /// 这里只负责放大。所以那条往返测试实际证明的是
    /// 「<b>画到屏幕上的那些字节</b>解得回原串」—— 颜色写反、轴序写错都在里面。
    /// 以前这里自己写了一遍同样的循环，等于把约定抄了两份，而**界面那份没人看**。
    /// </para>
    /// <para>
    /// **解不出来就在这里断言失败**，不返回 null —— 那样每条用例都能直接拿字符串用，
    /// 而"解不出来"这件事只会有一个失败点，报错信息也只有一处。
    /// </para>
    /// </remarks>
    private static string Decode(bool[,] modules, int scale = 8)
    {
        var source = EnrollQr.Pixels(modules);
        var sourceWidth = modules.GetLength(0);

        var width = sourceWidth * scale;
        var height = modules.GetLength(1) * scale;
        var pixels = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = source[((y / scale) * sourceWidth) + (x / scale)];
            }
        }

        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions
            {
                PossibleFormats = [BarcodeFormat.QR_CODE],
                TryHarder = true,
            },
        };

        var text = reader.Decode(pixels, width, height, RGBLuminanceSource.BitmapFormat.Gray8)?.Text;

        Assert.False(string.IsNullOrEmpty(text), "生成的二维码自己解不回来 —— 画在屏幕上也是扫不出来的");

        return text!;
    }
}
