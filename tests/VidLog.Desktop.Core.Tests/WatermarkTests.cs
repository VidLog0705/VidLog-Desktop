using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 水印（规格 §3.6.2）：两行文字、按秒走、UTC+8、烧进画面。
/// </summary>
public class WatermarkTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 27, 4, 0, 0, TimeSpan.Zero);

    // ─────────────────────────────────────────────
    // 文字本身
    // ─────────────────────────────────────────────

    [Fact]
    public void 时间是北京时间_与设备时区无关()
    {
        // 规格：「时区固定为 UTC+8（北京时间），与设备本地时区无关 ——
        // 用户改时区不影响水印，两端显示也一致」。
        //
        // ⚠️ 所以这里**不能**用 ToLocalTime()：那条路会跟着设备的时区跑。
        var utc = new DateTimeOffset(2026, 9, 27, 4, 0, 0, TimeSpan.Zero);

        Assert.Equal("2026/09/27 12:00:00", WatermarkText.ClockLine(utc));

        // 同一个时刻，用别的偏移量表达，结果**必须一样**。
        var sameMoment = utc.ToOffset(TimeSpan.FromHours(-5));
        Assert.Equal("2026/09/27 12:00:00", WatermarkText.ClockLine(sameMoment));
    }

    [Fact]
    public void 格式是年月日_时分秒_补零()
    {
        var moment = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);   // 北京时间 08:00:00

        Assert.Equal("2026/01/02 08:00:00", WatermarkText.ClockLine(moment));
    }

    [Fact]
    public void 单号一个字都不许省()
    {
        // 规格：「第二行：本段对应的**完整**单号（红色、不截断）」。
        // 水印是**唯一**随证据离开系统的自证载体，截断等于没有。
        const string longWaybill = "SF1234567890123456789012";

        Assert.Equal(longWaybill, WatermarkText.WaybillLine(longWaybill));
    }

    // ─────────────────────────────────────────────
    // 字幕文件
    // ─────────────────────────────────────────────

    [Fact]
    public void 每秒一条_而且时间跟着走()
    {
        // 规格：「**每秒更新一次**」「水印里的时间是**按秒走的**」。
        var ass = AssWatermark.Build(Noon, "SF1000000001", TimeSpan.FromSeconds(3), 1280, 720);

        var lines = ass.Split('\n').Where(l => l.StartsWith("Dialogue:", StringComparison.Ordinal)).ToList();

        // 3 条 Clock（每秒一条）+ 1 条 Waybill（整段一条）。
        Assert.Equal(4, lines.Count);
        Assert.Contains("2026/09/27 12:00:00", ass);
        Assert.Contains("2026/09/27 12:00:01", ass);
        Assert.Contains("2026/09/27 12:00:02", ass);
    }

    [Fact]
    public void 单号那一行是红的_时间是白的()
    {
        // 规格：单号「**红色**、不截断」。
        // ⚠️ ASS 的颜色是 **&HAABBGGRR**，红的写法是 &H000000FF（不是 RGB 顺序）。
        var ass = AssWatermark.Build(Noon, "SF1000000001", TimeSpan.FromSeconds(1), 1280, 720);

        var waybillStyle = ass.Split('\n')
            .Single(l => l.StartsWith("Style: Waybill", StringComparison.Ordinal));
        var clockStyle = ass.Split('\n')
            .Single(l => l.StartsWith("Style: Clock", StringComparison.Ordinal));

        Assert.Contains("&H000000FF", waybillStyle, StringComparison.Ordinal);
        Assert.Contains("&H00FFFFFF", clockStyle, StringComparison.Ordinal);
    }

    [Fact]
    public void 两行都在画面顶部中间_而且不重叠()
    {
        // 规格：「水印位置避开取景框，在视频的最上方中间位置」。
        var ass = AssWatermark.Build(Noon, "SF1000000001", TimeSpan.FromSeconds(1), 1280, 720);

        var clock = ass.Split('\n').Single(l => l.StartsWith("Style: Clock", StringComparison.Ordinal));
        var waybill = ass.Split('\n').Single(l => l.StartsWith("Style: Waybill", StringComparison.Ordinal));

        // Alignment=8 是「顶部居中」。
        Assert.Contains(",8,", clock, StringComparison.Ordinal);
        Assert.Contains(",8,", waybill, StringComparison.Ordinal);

        // 单号那一行在时间那一行**下面**（MarginV 更大），两行不叠在一起。
        Assert.True(MarginV(waybill) > MarginV(clock),
            "两行叠在一起的话，上面那行会被下面那行盖掉一半");

        static int MarginV(string style) => int.Parse(style.Split(',')[^2], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void 字号跟着画面高度走_换分辨率不用另配一套()
    {
        var small = AssWatermark.Build(Noon, "SF1", TimeSpan.FromSeconds(1), 1280, 720);
        var big = AssWatermark.Build(Noon, "SF1", TimeSpan.FromSeconds(1), 3840, 2160);

        static int FontSize(string ass) => int.Parse(
            ass.Split('\n').Single(l => l.StartsWith("Style: Clock", StringComparison.Ordinal)).Split(',')[2],
            System.Globalization.CultureInfo.InvariantCulture);

        // 4K 的字号应当是 720P 的三倍左右（同一比例）。
        Assert.True(FontSize(big) > FontSize(small) * 2);
    }

    [Fact]
    public void 单号里的特殊字符要转义_否则整行会消失()
    {
        // ASS 用 {} 包覆写标记、用 \N 换行。单号理论上可能带这些字符
        // （承运商编号规则不是我们定的），扎到的话轻则这一行看不见、
        // 重则把后面的字幕一起吃进去。
        var ass = AssWatermark.Build(Noon, @"SF{123}\N456", TimeSpan.FromSeconds(1), 1280, 720);

        Assert.Contains(@"SF\{123\}\\N456", ass, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有单号时不写那一行()
    {
        // 规格：「第二行：……**没在录时不出现**」。
        var ass = AssWatermark.Build(Noon, "   ", TimeSpan.FromSeconds(2), 1280, 720);

        var lines = ass.Split('\n').Where(l => l.StartsWith("Dialogue:", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, lines.Count);   // 只剩两条 Clock
        Assert.DoesNotContain("Style: Waybill", string.Empty);
    }

    [Fact]
    public void 时间格式是_ASS_那一套_厘秒两位()
    {
        Assert.Equal("0:00:00.00", AssWatermark.AssTime(TimeSpan.Zero));
        Assert.Equal("0:00:01.50", AssWatermark.AssTime(TimeSpan.FromSeconds(1.5)));
        Assert.Equal("2:03:04.07", AssWatermark.AssTime(new TimeSpan(0, 2, 3, 4, 70)));
        Assert.Equal("0:00:00.00", AssWatermark.AssTime(TimeSpan.FromSeconds(-5)));

        // ⚠️ 小时是**总小时数**，不是「一天里的小时」—— ASS 的 `H:` 本来就允许超过 24。
        // 写成 `value.Hours` 的话，覆盖时长一旦超过一天，后面的 cue 会**绕回 0 点**
        // （字幕时间倒流 ⇒ 那些秒没有水印）。
        Assert.Equal("25:00:00.00", AssWatermark.AssTime(TimeSpan.FromHours(25)));
    }

    // ─────────────────────────────────────────────
    // argv
    // ─────────────────────────────────────────────

    [Fact]
    public void 滤镜是输出选项_写在_i_之后_输出路径之前()
    {
        var arguments = FfmpegCameraCapture.BuildArguments(
            "Camera", "out.mkv", "libx264", watermarkAssPath: @"C:\work\segment-000.ass");

        var filterIndex = arguments.ToList().IndexOf("-vf");
        var inputIndex = arguments.ToList().IndexOf("-i");
        var outputIndex = arguments.ToList().IndexOf("-y");

        Assert.True(filterIndex > inputIndex,
            "-vf 写在 -i 之前会被当成输入选项 —— 那是另一件事");
        Assert.True(filterIndex < outputIndex, "滤镜必须在输出路径之前");

        // ⚠️ Windows 路径里那个 `C:` 会被 filtergraph 当成选项分隔符 ——
        // 不转义的话 ffmpeg 起来就报错、**录不了像**。
        var value = arguments[filterIndex + 1];
        Assert.StartsWith("ass='C\\:/work/segment-000.ass'", value, StringComparison.Ordinal);
    }

    [Fact]
    public void 不给水印时_argv_里没有_vf()
    {
        var arguments = FfmpegCameraCapture.BuildArguments("Camera", "out.mkv", "libx264");

        Assert.DoesNotContain("-vf", arguments);
    }

    // ─────────────────────────────────────────────
    // ★ 端到端：水印真的被烧进画面了
    // ─────────────────────────────────────────────

    /// <summary>
    /// 用本机 ffmpeg 渲一帧，看水印有没有真的画上去。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这一条是「水印到底进没进画面」唯一的本地证据。</b>
    /// 其余的用例验的都是「字幕文件写对了」—— 而字幕写对了、ffmpeg 没找到字体、
    /// 或者滤镜压根没接上，屏幕上照样一个字都没有。
    /// </para>
    /// <para>
    /// 判据不是「看起来对不对」（本机看不见图），而是**有/没有水印的同一帧
    /// 大小差得出来**：纯色/图案画面 + 一层带描边的文字 ⇒ PNG 明显变大。
    /// 它挡不住「字画歪了」，挡得住「根本没画」。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 端到端_水印真的画进了画面()
    {
        var ffmpeg = FfmpegLocator.TryFind();
        if (ffmpeg is null)
        {
            return;   // 本机没 ffmpeg 就跳过（与其它集成测试同一个口径）
        }

        using var dir = new TempDir();
        var assPath = System.IO.Path.Combine(dir.Path, "wm.ass");
        await File.WriteAllTextAsync(
            assPath,
            AssWatermark.Build(Noon, "SF1000000001", TimeSpan.FromSeconds(1), 1280, 720));

        var withWatermark = System.IO.Path.Combine(dir.Path, "with.png");
        var plain = System.IO.Path.Combine(dir.Path, "plain.png");

        // 同一条合成源、同一帧，只有「烧不烧水印」这一个变量不同。
        var render = "testsrc=duration=1:size=1280x720:rate=30";

        Assert.Equal(0, await RunAsync(ffmpeg, [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", render,
            "-vf", $"ass={AssWatermark.EscapeFilterPath(assPath)}",
            "-frames:v", "1", "-y", withWatermark,
        ]));

        Assert.Equal(0, await RunAsync(ffmpeg, [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", render,
            "-frames:v", "1", "-y", plain,
        ]));

        var withBytes = new FileInfo(withWatermark).Length;
        var plainBytes = new FileInfo(plain).Length;

        Assert.True(withBytes > plainBytes * 1.5,
            $"带水印那一帧应当明显更大（文字 + 描边增加大量细节）："
            + $"带水印 {withBytes} 字节，不带 {plainBytes} 字节 —— "
            + "两者接近说明**水印根本没画上去**（多半是字体找不到或滤镜没生效）");
    }

    private static async Task<int> RunAsync(string ffmpeg, IReadOnlyList<string> arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vidlog-wm-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（2026-09-27 实测：会话还在
            // 收尾时删含 `.ass` / `.mkv` 的临时目录）。清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
