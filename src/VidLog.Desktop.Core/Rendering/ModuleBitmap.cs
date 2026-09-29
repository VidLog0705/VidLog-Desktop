namespace VidLog.Desktop.Core.Rendering;

/// <summary>
/// 把「模块矩阵」摊成灰度图。**深浅约定只有这一处。**
/// </summary>
/// <remarks>
/// <para>
/// 二维码（<c>EnrollQr</c>）与一维条码（<see cref="Code128"/>）都用它 ——
/// 抽出来之前那份约定写在 <c>EnrollQr.Pixels</c> 里，而第二个用户一来就会抄第二遍。
/// </para>
/// <para>
/// ⚠️ <b>为什么在 Core 而不是界面层</b>：让「深色到底是 0 还是 255」这个约定
/// 只有一处、而且被往返测试盯着。写反了的表现是一张**反色码** ——
/// 有静区、比例也对，**看着完全正常，扫不出来**。
/// </para>
/// <para>
/// ⚠️ **一个模块一个字节**，放大交给画画的那一层（WPF 用最近邻）。
/// 在 Core 里按屏幕像素画的话，「几个像素一个模块」就成了渲染细节，
/// 缩放时容易糊出灰边 —— 而糊掉的码同样是看着正常、扫不出来。
/// </para>
/// </remarks>
public static class ModuleBitmap
{
    /// <summary>深色（条 / 码点）的灰度值。</summary>
    public const byte Dark = 0;

    /// <summary>浅色（空 / 背景）的灰度值。</summary>
    public const byte Light = 255;

    /// <summary>
    /// 摊平：<c>pixels[y * width + x]</c>，深色 <see cref="Dark"/>、浅色 <see cref="Light"/>。
    /// </summary>
    public static byte[] Pixels(bool[,] modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var width = modules.GetLength(0);
        var height = modules.GetLength(1);
        var pixels = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = modules[x, y] ? Dark : Light;
            }
        }

        return pixels;
    }
}
