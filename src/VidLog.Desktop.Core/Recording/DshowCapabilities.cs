using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 相机自报的一个采集模式（一行 <c>min s=… max s=…</c>）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>它记的是「范围」而不是「一个点」</b>：dshow 的 <c>-list_options</c> 每行给的是
/// <c>min</c> 与 <c>max</c> 两端，多数设备上两端相等（枚举出来的是离散档），
/// 但也有设备给的是一段区间（区间内任意尺寸都收）。把两端压成一个点的话，
/// 区间型设备的中间档会被判成「不支持」—— 而那是个**没有报错**的错判：
/// 表现只是「明明能用的档被跳过了」。
/// </para>
/// </remarks>
public readonly record struct CameraMode(
    int MinWidth,
    int MinHeight,
    double MinFrameRate,
    int MaxWidth,
    int MaxHeight,
    double MaxFrameRate)
{
    /// <summary>两端相等 ⇒ 这是一个离散档（<c>-list_options</c> 的常态）。</summary>
    public bool IsExact => MinWidth == MaxWidth && MinHeight == MaxHeight;

    /// <summary>这一行收不收「宽 <paramref name="width"/> 高 <paramref name="height"/> 帧率 <paramref name="frameRate"/>」这个模式。</summary>
    public bool Supports(int width, int height, double frameRate) =>
        width >= MinWidth && width <= MaxWidth
        && height >= MinHeight && height <= MaxHeight
        && frameRate <= MaxFrameRate;

    /// <summary>这一行能出的最大像素数 —— 挑落点时用它比大小。</summary>
    public long MaxPixels => (long)MaxWidth * MaxHeight;
}

/// <summary>
/// 问相机「你能出哪些尺寸/帧率」。
/// </summary>
/// <remarks>
/// <para>
/// <b>它解决的是什么。</b> 在这之前，选规格的唯一办法是**逐个真开一次相机试**
/// （<c>SpecSelectionPolicy</c> 的回落梯子），而梯子上的档是我们**写死**的三档
/// （4K / 1080P / 720P）。于是：
/// </para>
/// <list type="number">
/// <item>相机根本不支持那三档时，每一次尝试都要**把相机打开再失败一次**（冷启动慢）；</item>
/// <item>三档全失败后落到「原生档」—— 那是**相机自己出什么就是什么**，
/// 可能远低于它能给的上限（一台能出 1600×1200 的相机可能只给到 640×480，
/// 而这是个**静默的画质损失**）。</item>
/// </list>
/// <para>
/// 有了这张表，就能**先问再试**：表里没有的档直接跳过（省掉开相机那一次），
/// 一档都不剩时也不是盲目回落到原生档，而是**从表里挑一个离用户意图最近的、真实存在的档**。
/// </para>
/// <para>
/// ⚠️ <b>它是「只改进、不挡路」的一层</b>：表拿不到（ffmpeg 起不来、设备被别的进程占着、
/// 输出格式变了）时**返回空表**，调用方拿到空表就完全走今天那条老路 ——
/// 这个新功能**没有任何一条路径能变成新的故障**。
/// </para>
/// </remarks>
public static class DshowCapabilities
{
    /// <summary>
    /// 一行模式。<b>实测</b>（2026-10-11，本机 <c>PC CAMERA-</c>）长这样：
    /// <code>
    /// [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=1920x1080 fps=30 max s=1920x1080 fps=30
    /// [in#0 @ 000001ef17c70cc0]   vcodec=mjpeg  min s=1920x1080 fps=30 max s=1920x1080 fps=30 (pc, bt470bg/bt709/unknown, center)
    /// [in#0 @ 000001ef17c70cc0]   pixel_format=yuyv422  min s=640x480 fps=30 max s=640x480 fps=30
    /// </code>
    /// </summary>
    /// <remarks>
    /// ⚠️ 前缀（<c>vcodec=</c> / <c>pixel_format=</c>）与后缀（<c>(pc, …)</c> 那一串）
    /// 都不参与匹配 —— 我们只认中间那对 <c>min/max</c>。**同一个模式会出现两次**
    /// （一次带色彩范围后缀、一次不带），所以结果要**去重**，
    /// 否则相机的能力表会整体翻倍，而「挑最近的档」在重复项上照样对 ——
    /// 这种错**不会报错**，只是白占内存。
    /// </remarks>
    private static readonly Regex ModePattern = new(
        @"min s=(?<mrw>\d+)x(?<mrh>\d+) fps=(?<mrf>[\d.]+)\s+max s=(?<mxw>\d+)x(?<mxh>\d+) fps=(?<mxf>[\d.]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 枚举命令。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="DshowDevices.BuildListArguments"/> 同族：<c>-list_options</c> 是**枚举模式**，
    /// ffmpeg 打印完就以非零码退出 —— 退出码在这里没有意义，看的是 stderr。
    /// </remarks>
    public static IReadOnlyList<string> BuildArguments(string deviceName) =>
    [
        "-hide_banner",
        "-f", "dshow",
        "-list_options", "true",
        "-i", $"video={deviceName}",
    ];

    /// <summary>
    /// 从 <c>-list_options</c> 的输出里解析出能力表。解析不出来就是空表。
    /// </summary>
    public static IReadOnlyList<CameraMode> Parse(string ffmpegOutput)
    {
        if (string.IsNullOrEmpty(ffmpegOutput))
        {
            return [];
        }

        var modes = new List<CameraMode>();

        foreach (Match match in ModePattern.Matches(ffmpegOutput))
        {
            var mode = new CameraMode(
                ParseInt(match.Groups["mrw"].Value),
                ParseInt(match.Groups["mrh"].Value),
                ParseDouble(match.Groups["mrf"].Value),
                ParseInt(match.Groups["mxw"].Value),
                ParseInt(match.Groups["mxh"].Value),
                ParseDouble(match.Groups["mxf"].Value));

            if (!modes.Contains(mode))
            {
                modes.Add(mode);
            }
        }

        return modes;
    }

    /// <summary>表里有没有「宽 × 高 @ 帧率」这个模式。</summary>
    public static bool Supports(
        IReadOnlyList<CameraMode> modes, int width, int height, double frameRate) =>
        modes.Any(mode => mode.Supports(width, height, frameRate));

    /// <summary>
    /// 表里离「宽 × 高 @ 帧率」最近的那个模式；表是空的就返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 打分抄的是 PackingProof <c>MainViewModel.Camera.cs</c> 那一套
    /// （<c>|Δ宽| + |Δ高|</c> 乘 10、再加 <c>|Δ帧率|</c>）—— **像素差比帧率差重要一个量级**，
    /// 因为帧率差几帧看不出来，而分辨率差一档是看得出来的。
    /// </para>
    /// <para>
    /// ⚠️ <b>帧率是一道**先过的闸**，不是打分里的一项。</b> 一张 1920×1080@15 的表
    /// 在尺寸上离「要 1080P@30」是**零距离**，而我们从命令行钉的正是 30 ——
    /// 挑中它等于挑了一个**打开就失败**的档（`-framerate 30` 那个模式设备没有）。
    /// 所以先在「能跑 30 的那些」里挑尺寸最近的；一个都没有时（整台相机都到不了 30）
    /// 才回落到全表按尺寸挑，把「帧率其实不够」留给下一层的真开相机去证伪。
    /// </para>
    /// <para>
    /// ⚠️ 打分相同时**取大的那个**（<c>ThenByDescending(MaxPixels)</c>）：这是取证录像，
    /// 同样的接近程度下画质高的那个更没有理由输。不加这一条的话顺序取决于表里的先后，
    /// 而那是个**不稳定**的结果。
    /// </para>
    /// </remarks>
    public static CameraMode? Nearest(
        IReadOnlyList<CameraMode> modes, int width, int height, double frameRate)
    {
        if (modes.Count == 0)
        {
            return null;
        }

        var fastEnough = modes.Where(mode => frameRate <= mode.MaxFrameRate).ToList();
        var pool = fastEnough.Count > 0 ? fastEnough : [.. modes];

        return pool
            .OrderBy(mode => Score(mode, width, height, frameRate))
            .ThenByDescending(mode => mode.MaxPixels)
            .First();
    }

    /// <summary>「离目标有多远」——<c>|Δ宽| + |Δ高|</c> 加倍算帧率差。</summary>
    /// <remarks>
    /// 区间型模式（<c>min</c> 与 <c>max</c> 不等）按它**最接近目标的那一端**算距离，
    /// 否则一个「320×240 … 1920×1080」的区间会被当成 1080 那一头，
    /// 而目标 640×480 明明就落在它里面。
    /// </remarks>
    private static double Score(CameraMode mode, int width, int height, double frameRate)
    {
        var dx = Math.Max(0, Math.Max(mode.MinWidth - width, width - mode.MaxWidth));
        var dy = Math.Max(0, Math.Max(mode.MinHeight - height, height - mode.MaxHeight));
        var df = frameRate <= mode.MaxFrameRate ? 0 : frameRate - mode.MaxFrameRate;

        return ((dx + dy) * 10) + df;
    }

    private static int ParseInt(string value) =>
        int.Parse(value, CultureInfo.InvariantCulture);

    private static double ParseDouble(string value) =>
        double.Parse(value, CultureInfo.InvariantCulture);
}

/// <summary>
/// 拿一台相机的采集模式表。
/// </summary>
/// <remarks>
/// 抽成接口只有一个理由：<c>SpecSelectionPolicy</c> 要能在**没有相机**的测试里跑 ——
/// 表决定它挑哪一档，而那段挑法是要被测住的逻辑。
/// </remarks>
public interface ICameraCapabilities
{
    /// <summary>枚举 <paramref name="source"/> 这台设备的模式表。拿不到就是空表（不是异常）。</summary>
    Task<IReadOnlyList<CameraMode>> ListAsync(
        CameraSource source, CancellationToken cancellationToken = default);
}

/// <summary>真实现：跑一次 <c>ffmpeg -f dshow -list_options true</c>。</summary>
/// <remarks>
/// <para>
/// ⚠️ <b>直接起 <see cref="Process"/>，不用 <c>IProcessRunner</c></b> —— 与
/// <see cref="DshowDevices"/> 同一条理由：枚举模式**必定非零退出**，
/// 而 <c>SystemProcessRunner</c> 会在非零退出时记一条 WARN，
/// 于是每次启动都多一条「ffmpeg 以 1 退出」的假警报，把真警报淹掉。
/// </para>
/// </remarks>
public sealed class DshowCameraCapabilities : ICameraCapabilities
{
    private readonly string _ffmpegPath;
    private readonly IAppLogger? _logger;

    public DshowCameraCapabilities(string ffmpegPath, IAppLogger? logger = null)
    {
        _ffmpegPath = ffmpegPath;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CameraMode>> ListAsync(
        CameraSource source, CancellationToken cancellationToken = default)
    {
        // 网络源没有「dshow 档位表」这回事：它的尺寸在输出侧才定（`PinnedFfmpegSize`）。
        // 地点为空同理 —— 那是「还没配摄像头」，不是一台相机。
        if (source.IsNetwork || source.IsEmpty)
        {
            return [];
        }

        var stdout = await RunAsync(DshowCapabilities.BuildArguments(source.Address), cancellationToken);
        return DshowCapabilities.Parse(stdout);
    }

    private async Task<string> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 只读 stderr：设备的能力表就打在它上面。
            RedirectStandardError = true,

            // ⚠️⚠️ **承重，与 `DshowDevices.ListAllAsync` 同一处坑**：ffmpeg 往管道里写的是
            // **UTF-8**，而 .NET 不给 `StandardErrorEncoding` 时用的是进程的控制台码页
            // （中文 Windows = 936）—— 非 ASCII 的设备名当场变乱码。
            // 这里的中文只出现在「设备名回显」里，但它同时是**失败时唯一的线索**。
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return string.Empty;
            }

            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return stderr;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // ⚠️ 返回空串 = 空表 = 调用方走老路。**但必须记一条**：
            // 「相机什么都不支持」与「ffmpeg 根本没起来」在空表上长得一样，
            // 而这两种处境该说的话完全不同（`AGENTS.md` §6）。
            _logger?.Log(LogLevel.Warn, "设备",
                $"问不了相机的档位表（{_ffmpegPath}）：{ex.Message}");

            return string.Empty;
        }
    }
}
