using ZXing;
using ZXing.Common;

namespace VidLog.Desktop.Core.Camera;

/// <summary>从一帧灰度画面里读码。</summary>
/// <remarks>
/// 抽成接口是为了让「读不出来怎么办」「读到了怎么上报」这些逻辑
/// 能**不依赖 ZXing** 被测到，也为了将来换库时只动一个实现。
/// </remarks>
public interface IFrameScanner
{
    /// <summary>读一帧。读不到返回 <see langword="null"/>。</summary>
    string? TryDecode(CameraFrame frame);
}

/// <summary>
/// 用 ZXing.Net 读码。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类是全仓唯一引用 ZXing 的地方。</b> 换库只需改这一个文件。
/// </para>
/// <para>
/// 许可证 Apache-2.0（规格 §10 要求逐个核对，记录见 <c>docs/实现决策.md</c>）：
/// 商用安全、不要求衍生作品开源、无署名义务。
/// </para>
/// <para>
/// 喂的是**裸灰度像素**（紧凑的 w×h），不是图片文件 ——
/// 所以 .NET 5+ 下的核心包不需要任何图像库绑定。
/// </para>
/// <para>
/// <b>关掉 TryHarder 与旋转</b>：这两项会显著抬高单帧成本，而工位场景里
/// 面单基本是正对镜头的。真机标定时如果识别率不够，先开 <c>TryHarder</c>。
/// </para>
/// </remarks>
public sealed class ZXingFrameScanner : IFrameScanner
{
    private readonly BarcodeReaderGeneric _reader;

    public ZXingFrameScanner()
    {
        _reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions
            {
                // 关掉贵的那两项。识别率不够时第一个该动的是 TryHarder。
                // （这个版本没有旋转相关的选项 —— 反射确认过属性表。）
                TryHarder = false,
                TryInverted = false,

                // 面单上常见的一维码型。QR 也带上 —— 有些面单用它。
                PossibleFormats =
                [
                    BarcodeFormat.CODE_128,
                    BarcodeFormat.CODE_39,
                    BarcodeFormat.CODE_93,
                    BarcodeFormat.ITF,
                    BarcodeFormat.EAN_13,
                    BarcodeFormat.CODABAR,
                    BarcodeFormat.QR_CODE,
                ],
            },
        };
    }

    public string? TryDecode(CameraFrame frame)
    {
        try
        {
            // Gray8 要求紧凑的 w×h 亮度数组，ffmpeg 的 rawvideo gray 正好是。
            var result = _reader.Decode(
                frame.Gray, frame.Width, frame.Height, RGBLuminanceSource.BitmapFormat.Gray8);

            return string.IsNullOrWhiteSpace(result?.Text) ? null : result.Text;
        }
        catch (Exception)
        {
            // 解不出来是**常态**（大多数帧里根本没有码），不是错误。
            // 这里兜住任何 ZXing 内部异常 —— 一帧读失败绝不能影响录制。
            return null;
        }
    }
}
