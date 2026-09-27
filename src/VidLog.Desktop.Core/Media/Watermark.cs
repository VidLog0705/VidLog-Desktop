using System.Globalization;
using System.Text;

namespace VidLog.Desktop.Core.Media;

/// <summary>
/// 水印那两行文字（规格 §3.6.2）。
/// </summary>
/// <remarks>
/// <para>
/// 规格原话（2026-09-24 需求变更）：「自动在视频打水印，**年/月/日/时/分/秒**水印在进入
/// 发货或者退货模式页面时，顶部常驻。在开始工作扫描到快递单号时，对视频添加**完整快递单号**水印。」
/// 追问后补充：「水印里的时间是**按秒走的**，参照……**北京时间**」。
/// </para>
/// <para>
/// ⚠️ <b>时间是 UTC+8，与设备时区无关</b>（规格原话：「用户改时区不影响水印，
/// 两端显示也一致」）。所以这里**不用** <c>ToLocalTime()</c>。
/// </para>
/// <para>
/// ⚠️ <b>时刻来自可信时钟</b>，不是墙钟（规格 §3.6.3：「水印与时长都不得取自墙钟」）。
/// 这个类只负责**排版**，时刻由调用方从 <c>ITrustedClock</c> 取。
/// </para>
/// </remarks>
public static class WatermarkText
{
    /// <summary>水印固定用北京时间（UTC+8）。</summary>
    public static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    /// <summary>第一行：`年/月/日 时:分:秒`。</summary>
    public static string ClockLine(DateTimeOffset moment)
    {
        var beijing = moment.ToUniversalTime().ToOffset(BeijingOffset);
        return beijing.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>第二行：**完整**单号（规格 §3.6.2：不截断）。</summary>
    /// <remarks>
    /// ⚠️ **一个字都不许省**。界面上别处（列表、状态）可能做过省略号处理，
    /// 但水印是**唯一**随证据离开系统的自证载体 —— 截断了就等于没有。
    /// </remarks>
    public static string WaybillLine(string waybill) => waybill.Trim();
}

/// <summary>
/// 把水印做成 ASS 字幕，交给 ffmpeg 的 `ass` 滤镜烧进画面。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不是 <c>drawtext</c></b>：
/// </para>
/// <list type="bullet">
/// <item><c>drawtext</c> 里能拿到的时间只有 <c>%{localtime}</c>（**墙钟**，规格明令不许）
/// 和 <c>%{pts}</c>（帧时间戳）。要显示「年/月/日 时:分:秒」就得做日期运算，
/// 而 filtergraph 表达式里没有日期类型。</item>
/// <item><c>%{pts:gmtime:…}</c> 能把 pts 加到**进程启动那一刻**上再格式化，但那个
/// 基准是**本机时区**的墙钟，而且没法固定成 UTC+8 —— 规格要求与设备时区无关。</item>
/// </list>
/// <para>
/// ASS 则是**我们算好每一个字**：每秒一条字幕，文字由
/// <see cref="WatermarkText"/> 从可信时钟推出来 —— 时区、格式、按秒走，
/// 三件事都在我们手里，而且**纯数据、可测**。
/// </para>
/// <para>
/// ⚠️ <b>它必须在**采集那一次**编码时就烧进去</b>：录制期是 MKV 中间容器，
/// 收尾时是 <c>-c copy</c> 的 remux —— 那一步**加不了滤镜**（加了就要重编码，
/// 违反「不转码」）。所以水印是「录的时候就在画面里」，不是「导出时再叠」。
/// </para>
/// <para>
/// ⚠️ <b>字体走系统</b>（规格 §10 的许可证账目：不随包带任何字体文件）。
/// 名字给一个 Windows 上必定有的中文字体；找不到时 libass 会自己退到默认字体 ——
/// 那时字可能不好看，但**内容仍然对**。
/// </para>
/// </remarks>
public static class AssWatermark
{
    /// <summary>字体名。系统自带，**不随包带**。</summary>
    public const string FontName = "Microsoft YaHei";

    /// <summary>
    /// 某个分段对应的字幕文件路径。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这个约定只有这一处</b>：写它的是会话、读它的是采集器
    /// （`FfmpegCameraCapture` 从输出路径推同一个名字）。
    /// 两处各写一份的话，会出现「字幕写了、采集器没找到」——
    /// 而表现只是**没有水印**，不会有任何报错。
    /// </remarks>
    public static string PathFor(string segmentPath) =>
        Path.ChangeExtension(segmentPath, ".ass");

    /// <summary>当前行的字号占画面高度的比例。</summary>
    public const double ClockFontRatio = 0.040;

    /// <summary>单号那一行略大（它是要一眼看清的那一行）。</summary>
    public const double WaybillFontRatio = 0.050;

    /// <summary>
    /// 拼一份覆盖 [0, <paramref name="coverage"/>) 的字幕。
    /// </summary>
    /// <param name="startUtc">这一段**开录那一刻**的可信时刻。</param>
    /// <param name="waybill">这一段的完整单号。</param>
    /// <param name="coverage">
    /// 覆盖多久。比段时长**留一点余量** —— 少了的话，超出的那几秒就没有水印了
    /// （而「部分没有水印」比「全都没有」更难发现）。
    /// </param>
    /// <param name="width">画面宽（决定字号与 PlayRes）。</param>
    /// <param name="height">画面高。</param>
    public static string Build(
        DateTimeOffset startUtc, string waybill, TimeSpan coverage, int width, int height)
    {
        var clockSize = Math.Max(12, (int)Math.Round(height * ClockFontRatio));
        var waybillSize = Math.Max(12, (int)Math.Round(height * WaybillFontRatio));

        // 顶部居中（Alignment=8），留一点上边距。**两行都在画面的上方** ——
        // 规格 §3.6.2：「水印位置避开取景框，在视频的最上方中间位置」。
        var clockMargin = Math.Max(8, (int)Math.Round(height * 0.02));
        var waybillMargin = clockMargin + clockSize + Math.Max(4, (int)Math.Round(height * 0.012));

        var builder = new StringBuilder();

        builder.AppendLine("[Script Info]");
        builder.AppendLine("ScriptType: v4.00+");
        // PlayRes 与画面一致 ⇒ 字号就是「画面像素」，换分辨率不用另算一套。
        builder.Append("PlayResX: ").AppendLine(width.ToString(CultureInfo.InvariantCulture));
        builder.Append("PlayResY: ").AppendLine(height.ToString(CultureInfo.InvariantCulture));
        // 顶部对齐：ASS 的「上边距」默认是从画面顶端算的。
        builder.AppendLine("ScaledBorderAndShadow: yes");
        builder.AppendLine();
        builder.AppendLine("[V4+ Styles]");
        builder.AppendLine(
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, "
            + "Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, "
            + "Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");

        // ⚠️ 颜色是 **&HAABBGGRR**（不是 RGB），红是 `&H000000FF`。
        // 描边用黑色、半透明底：白字压在**白面单**上时，没有描边就完全看不见 ——
        // 与界面上那四角括号同一个理由（§3.2.2）。
        AppendStyle(builder, "Clock", clockSize, "&H00FFFFFF", clockMargin);
        AppendStyle(builder, "Waybill", waybillSize, "&H000000FF", waybillMargin);

        builder.AppendLine();
        builder.AppendLine("[Events]");
        builder.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        // 单号一行：整段都在（规格：一段录制只有一个单号）。
        var waybillText = WatermarkText.WaybillLine(waybill);
        if (waybillText.Length > 0)
        {
            AppendCue(builder, TimeSpan.Zero, coverage, "Waybill", waybillText);
        }

        // 时间那一行：**每秒一条**。
        //
        // ⚠️ 一秒一条，不是一条到位 —— 规格原话「水印里的时间是**按秒走的**」。
        // 秒数从**可信时钟**推：startUtc + i 秒。
        var seconds = (int)Math.Ceiling(coverage.TotalSeconds);

        for (var i = 0; i < seconds; i++)
        {
            AppendCue(
                builder,
                TimeSpan.FromSeconds(i),
                TimeSpan.FromSeconds(i + 1),
                "Clock",
                WatermarkText.ClockLine(startUtc.AddSeconds(i)));
        }

        return builder.ToString();
    }

    private static void AppendStyle(
        StringBuilder builder, string name, int size, string primaryColour, int marginV)
    {
        builder.Append("Style: ").Append(name).Append(',')
            // fontname 里有空格也能直接用（ASS 不按空格切分样式字段）。
            .Append(FontName).Append(',')
            .Append(size.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(primaryColour).Append(',')          // PrimaryColour
            .Append("&H00FFFFFF,")                     // SecondaryColour（用不上）
            .Append("&H00000000,")                     // OutlineColour = 黑描边
            .Append("&H80000000,")                     // BackColour（半透明黑）
            .Append("-1,")                             // Bold
            .Append("0,0,0,")                          // Italic / Underline / StrikeOut
            .Append("100,100,0,0,")                    // ScaleX / ScaleY / Spacing / Angle
            .Append("1,")                              // BorderStyle=1（描边 + 阴影）
            .Append("3,")                              // Outline
            .Append("0,")                              // Shadow
            .Append("8,")                              // Alignment=8 = 顶部居中
            .Append("10,10,")                          // MarginL / MarginR
            .Append(marginV.ToString(CultureInfo.InvariantCulture)).Append(',')
            .AppendLine("1");                          // Encoding
    }

    private static void AppendCue(
        StringBuilder builder, TimeSpan from, TimeSpan to, string style, string text)
    {
        builder.Append("Dialogue: 0,")
            .Append(AssTime(from)).Append(',')
            .Append(AssTime(to)).Append(',')
            .Append(style).Append(",,")
            .Append("0,0,0,,")
            .AppendLine(EscapeText(text));
    }

    /// <summary>ASS 的时间是 <c>H:MM:SS.cc</c>（**厘秒**，两位）。</summary>
    public static string AssTime(TimeSpan value)
    {
        var total = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        var centiseconds = (int)Math.Round(total.TotalSeconds * 100) % 100;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)total.TotalHours}:{total.Minutes:00}:{total.Seconds:00}.{centiseconds:00}");
    }

    /// <summary>
    /// 字幕文本里的特殊字符。
    /// </summary>
    /// <remarks>
    /// ASS 用 <c>{}</c> 包覆写标记、用 <c>\N</c> 换行 —— 而**单号里理论上可能带这些字符**
    /// （承运商编号规则不是我们定的）。扎到的话轻则整行显示不出来，
    /// 重则把后面的字幕一起吃掉。这里逐个转义。
    /// </remarks>
    public static string EscapeText(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("{", "\\{", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\N", StringComparison.Ordinal);

    /// <summary>
    /// 把路径写成能塞进 <c>-vf ass=…</c> 的样子。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>filtergraph 有自己的转义规则，与命令行那层不是一回事。</b>
    /// Windows 路径里的 <c>C:\</c> 那个冒号会被 filtergraph 当成「选项分隔符」，
    /// 于是整条 <c>-vf</c> 直接解析失败（表现是 ffmpeg 起来就报错、录不了像）。
    /// 规则是：**用单引号把值包起来**，并把 <c>\</c> 换成 <c>/</c>（Windows 上等价），
    /// 值里的冒号转义成 <c>\:</c>。
    /// </remarks>
    public static string EscapeFilterPath(string path)
    {
        var normalized = path.Replace('\\', '/')
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal);

        return $"'{normalized}'";
    }
}
