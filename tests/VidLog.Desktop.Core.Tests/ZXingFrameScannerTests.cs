using VidLog.Desktop.Core.Camera;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 摄像头识码（规格 §3.2.1）。
/// </summary>
/// <remarks>
/// 条码图**用代码生成**，不引入任何图片素材 —— 既省事，也避开洁净室那条
/// 「不得查阅任何既有实现（含截图）」的边界。
/// </remarks>
public class ZXingFrameScannerTests
{
    [Fact]
    public void 能读出自己生成的Code128条码()
    {
        var frame = RenderCode128("SF1234567890");

        var text = new ZXingFrameScanner().TryDecode(frame);

        Assert.Equal("SF1234567890", text);
    }

    [Fact]
    public void 能读出二维码()
    {
        var frame = RenderQr("SF9876543210");

        Assert.Equal("SF9876543210", new ZXingFrameScanner().TryDecode(frame));
    }

    [Fact]
    public void 空白画面读不出东西_返回空而不是抛()
    {
        var blank = new CameraFrame(new byte[640 * 480], 640, 480, 0);

        // 解不出来是**常态**（大多数帧里根本没有码），不是错误。
        Assert.Null(new ZXingFrameScanner().TryDecode(blank));
    }

    [Fact]
    public void 噪声画面读不出东西_不抛()
    {
        var random = new Random(1234);
        var noise = new byte[640 * 480];
        random.NextBytes(noise);

        Assert.Null(new ZXingFrameScanner().TryDecode(new CameraFrame(noise, 640, 480, 0)));
    }

    /// <summary>把 ZXing 编出来的位图转成灰度裸帧（与 ffmpeg 给的形状一致）。</summary>
    private static CameraFrame Render(BitMatrix matrix)
    {
        // 外面留白：条码贴边时 ZXing 经常读不出来（缺静区）。
        const int margin = 24;
        var width = matrix.Width + margin * 2;
        var height = matrix.Height + margin * 2;

        var gray = new byte[width * height];
        Array.Fill(gray, (byte)255);

        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                if (matrix[x, y])
                {
                    gray[(y + margin) * width + (x + margin)] = 0;
                }
            }
        }

        return new CameraFrame(gray, width, height, 0);
    }

    private static CameraFrame RenderCode128(string content)
    {
        var writer = new BarcodeWriterGeneric
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions { Width = 400, Height = 120, Margin = 0 },
        };

        return Render(writer.Encode(content));
    }

    private static CameraFrame RenderQr(string content)
    {
        var writer = new BarcodeWriterGeneric
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions { Width = 300, Height = 300, Margin = 0 },
        };

        return Render(writer.Encode(content));
    }
}
