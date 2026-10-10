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
/// <b><c>TryHarder</c> 开着、<c>TryInverted</c> 关着</b>：
/// <c>TryHarder</c> 会抬高单帧成本，但它正是「倾斜、稍远、稍糊也能认出来」的那一档 ——
/// 2026-10-10 真机实测：关着它时，面单倾斜 15–20° 或离镜头稍远就**45 秒零识别**，
/// 正对贴近才认得出；工位场景里没人精确定位，所以这一档必须开。
/// </para>
/// <para>
/// ⚠️ 它抬高的成本由上游的**取景框裁剪 + 降频**抵掉（见 <c>PrerecordController</c>：
/// 只解中央那一片、每 N 帧才解一次）—— 不是无脑对整幅 640×480 做重活。
/// </para>
/// <para>
/// <c>TryInverted</c> 仍关着：它管的是**浅底深条**的反色码，面单上的码都是深底浅条，
/// 开了只会多付一份成本。<b>本库这个版本没有旋转相关的选项</b>（反射确认过属性表），
/// 所以「无论什么角度」靠的是 <c>TryHarder</c> 的多路扫描，不是旋转搜索。
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
                // ⚠️ TryHarder **开着**（2026-10-10 真机实测后改的）：关着时倾斜/稍远
                // 一律认不出。成本由上游裁剪 + 降频抵掉，见类注释。
                // （这个版本没有旋转相关的选项 —— 反射确认过属性表。）
                TryHarder = true,
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
