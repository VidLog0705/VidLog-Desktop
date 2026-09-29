using VidLog.Desktop.Core.Rendering;
using ZXing;
using ZXing.Common;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 一维条码（配置向导第 6 步的「扫码枪测试条码」）。
/// </summary>
/// <remarks>
/// ⚠️ <b>这一组里最要紧的是往返测试</b>：把编出来的图**用 ZXing 解回去**、
/// 与原文逐字比对。码表是手抄的（ISO/IEC 15417 那张表），
/// 抄错一格的表现是「图形看着像条码、但扫出来是别的单号或扫不出来」——
/// 而那种错**肉眼看不出来**，而用户拿它当「扫码枪能用」的证据。
/// </remarks>
public class Code128Tests
{
    /// <summary>设计图上印的那个测试单号。</summary>
    /// <remarks>
    /// 逐字照抄自设计图 `_33`：「可选扫码枪测试条码：**TEST20260928181639**」。
    /// </remarks>
    private const string DesignTestWaybill = "TEST20260928181639";

    /// <summary>
    /// 把矩阵放大成灰度位图（高 <paramref name="height"/> 像素），再用 ZXing 解回来。
    /// </summary>
    /// <remarks>
    /// ⚠️ 放大是**必须的**：一个模块一个字节时条码只有 1 像素高，
    /// 而 ZXing 的一维码识别要在竖直方向找到足够多的行。
    /// 界面上也是这么做的（WPF 拉伸 + 最近邻）。
    /// </remarks>
    private static string? DecodeRoundTrip(string payload, int height = 40)
    {
        var modules = Code128.Modules(payload);
        var width = modules.GetLength(0);

        // 竖直方向复制成 height 行。
        var pixels = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = modules[x, 0]
                    ? ModuleBitmap.Dark
                    : ModuleBitmap.Light;
            }
        }

        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions
            {
                PossibleFormats = [BarcodeFormat.CODE_128],
                TryHarder = true,
            },
        };

        return reader.Decode(pixels, width, height, RGBLuminanceSource.BitmapFormat.Gray8)?.Text;
    }

    [Fact]
    public void 设计图上那个测试单号能往返()
    {
        // ⚠️ 这一条是**码表对不对**的总判据。抄错一格它就会红。
        Assert.Equal(DesignTestWaybill, DecodeRoundTrip(DesignTestWaybill));
    }

    [Theory]
    [InlineData("SF1234567890")]          // 顺丰风格
    [InlineData("YT9876543210987")]       // 圆通风格
    [InlineData("A")]                     // 最短
    [InlineData("0123456789")]            // 纯数字
    [InlineData("abcXYZ-_.~")]            // 子集 B 里的符号
    [InlineData(" spaced out ")]          // 空格是 32，子集 B 的下界
    [InlineData("~")]                     // 126，子集 B 的上界
    public void 各种真实单号都能往返(string payload)
    {
        Assert.Equal(payload, DecodeRoundTrip(payload));
    }

    [Fact]
    public void 校验位按标准算()
    {
        // 起始 104；其后每个字符的值 = ASCII - 32，位置**从 1 起**加权。
        // 手算一遍「AB」：'A'=65 ⇒ 33、'B'=66 ⇒ 34；
        // 104 + 33×1 + 34×2 = 205 ⇒ 205 % 103 = **102**。
        // ⚠️ 我第一版把它算成 109 % 103（漏了 33 与 34 已经是减过 32 的值），
        // 而那条断言红的时候看着像实现错了 —— 校验位这种算术，先信实现再信手算。
        var symbols = Code128.Symbols("AB");

        Assert.Equal([104, 33, 34, 102, 106], symbols);
    }

    [Fact]
    public void 编不了的字符要抛而不是丢掉()
    {
        // ⚠️ 静默丢掉那个字符的话，条码扫出来是个**别的单号** ——
        // 而用户拿它当「扫码枪测试通过」的证据。那是这类功能最坏的一种假象。
        Assert.Throws<ArgumentOutOfRangeException>(() => Code128.Symbols("测试"));

        // 空格与波浪号是子集 B 的两端，都得能编。
        Assert.NotNull(Code128.Symbols(" ~"));
    }

    [Fact]
    public void 两侧静区按规格留够十个模块()
    {
        // ⚠️ 一维码靠**水平**方向的明暗边界定位，两侧没留够的话读码器会把
        // 「屏幕边缘」当成一条空。规格要求 ≥ 10×，比二维码那 4 个模块宽得多。
        Assert.Equal(10, Code128.QuietZoneModules);

        var modules = Code128.Modules("A");

        for (var x = 0; x < Code128.QuietZoneModules; x++)
        {
            Assert.False(modules[x, 0], $"左侧第 {x} 个模块应当是浅色（静区）");
            Assert.False(
                modules[modules.GetLength(0) - 1 - x, 0],
                $"右侧第 {x} 个模块应当是浅色（静区）");
        }
    }

    [Fact]
    public void 高度是一_画多高由界面决定()
    {
        // 条码的全部信息都在横向的条空序列里；高度是渲染细节。
        var modules = Code128.Modules("A");

        Assert.Equal(1, modules.GetLength(1));

        // 而宽度 = 静区 + 码字模块数（起始 11 + 数据 11×n + 校验 11 + 停止 13）。
        var expected = (Code128.QuietZoneModules * 2) + 11 + 11 + 11 + 13;
        Assert.Equal(expected, modules.GetLength(0));
    }

    [Fact]
    public void 深浅约定与二维码一致()
    {
        // ⚠️ 写反了是一张**反色码** —— 有静区、比例也对，看着完全正常，扫不出来。
        // 两个码种共用 `ModuleBitmap`，所以这一条其实是在守「它们确实共用」。
        var modules = Code128.Modules("A");

        Assert.Equal(ModuleBitmap.Light, Code128.Pixels(modules)[0]);   // 静区是浅色

        var pixels = Code128.Pixels(modules);

        for (var x = 0; x < modules.GetLength(0); x++)
        {
            // ⚠️ `modules[x, 0]` 为真是**深色**（条），写成取反就变成「第一个浅色模块」
            // 而静区正是浅色 —— 那样这条断言其实在测 x=0 那个静区，白测。
            if (modules[x, 0])
            {
                Assert.Equal(ModuleBitmap.Dark, pixels[x]);
                return;
            }
        }

        Assert.Fail("测试样本里应当至少有一个深色模块");
    }
}
