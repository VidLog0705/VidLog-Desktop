namespace VidLog.Desktop.Core.Camera;

/// <summary>
/// 取景识码的**识别框**（ROI）—— 按画面尺寸的**比例**给，不按像素。
/// </summary>
/// <remarks>
/// <para>
/// 需求方 2026-10-10 定形：实时预览上画一个「<b>四角实线直角、互不相连</b>」的框，
/// <b>面单在框里才识别，框外一律不识别</b>（防误扫）。
/// </para>
/// <para>
/// 用比例而不是像素：待扫那一路是 640×480、录制那一路是 640×360 ——
/// 写死像素的话换一路就会错位。
/// </para>
/// <para>
/// ⚠️ 框的<b>画</b>与<b>裁</b>共用这一个常量（<c>PrerecordController</c> 按它裁、
/// 主窗按同一组比例画）。两处各写一份的话，改了这处忘那处，表现就是
/// 「眼看着在框里、却认不出来」—— 而那种错没地方能查。
/// </para>
/// </remarks>
public readonly record struct RecognitionRoi(double Left, double Top, double Width, double Height)
{
    /// <summary>
    /// 默认框：画面正中的 <b>60% × 50%</b>。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这组比例是实现方取的默认值（2026-10-10），等需求方真机标定</b>——
    /// 「框多大才既好对准、又不把旁边的分拣码扫进来」只有拿真面单量得准。
    /// 改这里一处即可，画与裁都跟着变。
    /// </remarks>
    public static RecognitionRoi Default { get; } = new(0.20, 0.25, 0.60, 0.50);

    /// <summary>把比例换算成某一幅画面上的**像素矩形**（夹进画面内）。</summary>
    public (int X, int Y, int Width, int Height) ToPixels(int frameWidth, int frameHeight)
    {
        var x = (int)Math.Round(Left * frameWidth);
        var y = (int)Math.Round(Top * frameHeight);
        var w = (int)Math.Round(Width * frameWidth);
        var h = (int)Math.Round(Height * frameHeight);

        x = Math.Clamp(x, 0, frameWidth - 1);
        y = Math.Clamp(y, 0, frameHeight - 1);
        w = Math.Clamp(w, 1, frameWidth - x);
        h = Math.Clamp(h, 1, frameHeight - y);

        return (x, y, w, h);
    }
}

/// <summary>
/// 识别框**画在界面上**的位置与那四段直角（纯几何，便于用例盖住）。
/// </summary>
/// <remarks>
/// <para>
/// 它回答的是「屏幕上那条框该落在哪儿」，而屏幕上那幅画面是**等比缩放后**
/// 放进容器的（<c>Image.Stretch="Uniform"</c>，短边留黑）—— 所以不能直接拿
/// 容器尺寸乘比例，得先把**画面实际占的那一块**算出来。
/// 算错的表现是「框画得挺好看，但不在真正识别的区域上」，而那种错在真机上
/// 只有靠「把面单对准框反而扫不出来」才看得出来。
/// </para>
/// <para>
/// ⚠️ 比例只有一个来源（<see cref="RecognitionRoi"/>）—— 这里只做换算，不另存一组数。
/// </para>
/// </remarks>
public readonly record struct RecognitionBox(double X, double Y, double Width, double Height)
{
    /// <summary>
    /// 按容器尺寸与画面尺寸算出识别框在界面上的矩形。
    /// </summary>
    /// <param name="containerWidth">容器（预览区）宽。</param>
    /// <param name="containerHeight">容器高。</param>
    /// <param name="frameWidth">这一帧的像素宽。</param>
    /// <param name="frameHeight">这一帧的像素高。</param>
    /// <param name="roi">比例框；不传就是默认框。</param>
    /// <remarks>
    /// ⚠️ <b>传进来的宽高必须是「真实画面」那一块的，不是带补边的整幅帧。</b>
    /// 滤镜链会补黑边（<c>PreviewProcess.PreviewFilters</c>），而比例框是相对**画面**
    /// 算的 —— 拿整幅帧乘比例的话框会连黑边一起算进去，与真正喂解码器的那一片对不上。
    /// 界面上那幅画面是**先按 <c>PreviewFrame.Picture</c> 裁掉补边才上屏的**，
    /// 所以调用方把上屏那一幅的尺寸传进来就对了（见 <c>MainWindow.DrawRecognitionBox</c>）。
    /// </remarks>
    public static RecognitionBox Fit(
        double containerWidth,
        double containerHeight,
        int frameWidth,
        int frameHeight,
        RecognitionRoi? roi = null)
    {
        if (containerWidth <= 0 || containerHeight <= 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            return default;
        }

        // 与 `Image.Stretch="Uniform"` 同一套算法：等比放进容器，短的那一边留黑边。
        var scale = Math.Min(containerWidth / frameWidth, containerHeight / frameHeight);

        var shownWidth = frameWidth * scale;
        var shownHeight = frameHeight * scale;
        var left = (containerWidth - shownWidth) / 2;
        var top = (containerHeight - shownHeight) / 2;

        var box = roi ?? RecognitionRoi.Default;

        return new RecognitionBox(
            left + box.Left * shownWidth,
            top + box.Top * shownHeight,
            box.Width * shownWidth,
            box.Height * shownHeight);
    }

    /// <summary>
    /// 四个角，每角一段折线（三点：臂端 → 角 → 臂端）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 需求方要的是「**四角实线直角、互不相连**」—— 所以这里给的是**四段**，
    /// 画的人必须把它们画成四条独立折线（一个 <c>Path</c> 里的四个 figure 也算独立），
    /// 连成一圈就成了普通矩形框，那是另一种东西。
    /// <para>
    /// 臂长按框的短边夹住（最多一半），否则小框上四条臂会互相穿插成一团。
    /// </para>
    /// </remarks>
    public IReadOnlyList<(double X1, double Y1, double X2, double Y2, double X3, double Y3)> Corners(
        double armLength)
    {
        var arm = Math.Clamp(armLength, 1, Math.Max(1, Math.Min(Width, Height) / 2));
        var right = X + Width;
        var bottom = Y + Height;

        return
        [
            (X, Y + arm, X, Y, X + arm, Y),                          // 左上
            (right - arm, Y, right, Y, right, Y + arm),               // 右上
            (right, bottom - arm, right, bottom, right - arm, bottom), // 右下
            (X + arm, bottom, X, bottom, X, bottom - arm),            // 左下
        ];
    }
}
