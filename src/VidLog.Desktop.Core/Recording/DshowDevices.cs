using System.Diagnostics;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 枚举本机的 DirectShow 设备：摄像头与麦克风。
/// </summary>
/// <remarks>
/// <para>
/// 形状仿 <see cref="FfmpegLocator"/>：静态、不抛、找不到就给空。
/// 「没有摄像头」是一个**正常的运行环境**（上一台机器就是），
/// 不是异常 —— 界面该把它显示成一句说明，而不是一条崩溃。
/// </para>
/// <para>
/// ⚠️ 名字里**没有 Camera**：规格 §3.1.8 之后它同时管麦克风，而一个叫
/// <c>CameraDevices</c> 的类返回麦克风是句谎话（2026-09-29 改名）。
/// </para>
/// <para>
/// ⚠️ 视频与音频是**两次各自独立**的调用，不是一次返回两类 —— 调用点几乎都只关心
/// 其中一类（配置向导的摄像头一步、麦克风一步），合成一个「设备表」只会让
/// 每个调用点都去挑自己那半。代价是两次枚举时各起一次 ffmpeg（各约 0.3 秒），
/// 而这两处都在启动/开设置窗时，不在录制路径上。
/// </para>
/// </remarks>
public static class DshowDevices
{
    /// <summary>列出可用的**视频**设备名。返回空列表 = 没有摄像头（或 ffmpeg 跑不起来）。</summary>
    public static async Task<IReadOnlyList<string>> ListVideoAsync(
        string ffmpegPath,
        CancellationToken cancellationToken = default) =>
        ParseVideoDevices(await ListAllAsync(ffmpegPath, cancellationToken));

    /// <summary>列出可用的**音频**设备名（规格 §3.1.8）。返回空列表 = 没有麦克风。</summary>
    public static async Task<IReadOnlyList<string>> ListAudioAsync(
        string ffmpegPath,
        CancellationToken cancellationToken = default) =>
        ParseAudioDevices(await ListAllAsync(ffmpegPath, cancellationToken));

    /// <summary>跑一次 <c>-list_devices</c>，把 stderr 原样拿回来。</summary>
    /// <remarks>
    /// 失败时返回空串而不是抛：调用方拿到空串，解析出来就是空表 ——
    /// 与「本机没这个设备」同一条路（上层已经有一条「找不到 FFmpeg」的警告了，
    /// 这里再抛一次只会变成重复噪声）。
    /// </remarks>
    private static async Task<string> ListAllAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 只读 stderr：ffmpeg 的设备表就打在它上面，stdout 是空的。
            RedirectStandardError = true,
        };

        foreach (var argument in BuildListArguments())
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
            return string.Empty;
        }
    }

    /// <summary>
    /// 枚举命令。单独抽出来是为了和参数一样可被断言。
    /// </summary>
    /// <remarks>
    /// 故意让命令**失败**：<c>-list_devices</c> 是枚举模式，
    /// ffmpeg 打印完设备表就以非零码退出 —— 退出码在这里没有意义，看的是 stderr。
    /// </remarks>
    public static IReadOnlyList<string> BuildListArguments() =>
    [
        "-hide_banner",
        "-list_devices", "true",
        "-f", "dshow",
        "-i", "dummy",
    ];

    /// <summary>
    /// 从 <c>-list_devices</c> 的输出里挑出**视频**设备名。
    /// </summary>
    /// <remarks>
    /// 真实输出长这样（本机实测）：
    /// <code>
    /// [dshow @ 0000...] "Lenovo EasyCamera" (video)
    /// [dshow @ 0000...]   Alternative name "@device_pnp_..."
    /// [dshow @ 0000...] "麦克风 (USB Audio)" (audio)
    /// </code>
    /// 只有 <c>(video)</c> 结尾的才是我们要的；<c>Alternative name</c> 是给
    /// <c>-i</c> 用的另一种写法，不是另一个设备，必须排除 ——
    /// 不过滤的话同一台相机会被数成好几个。
    /// </remarks>
    public static IReadOnlyList<string> ParseVideoDevices(string ffmpegOutput) =>
        ParseDevices(ffmpegOutput, "(video)");

    /// <summary>同上，挑**音频**设备名（规格 §3.1.8）。</summary>
    /// <remarks>
    /// 与视频分开是有意的：`(video)` 与 `(audio)` 是同一份输出里的两类行，
    /// 混在一起解析的话，「本机只有麦克风没有摄像头」会被当成「有设备」，
    /// 而那条路的下一步是拿麦克风的名字去开视频设备。
    /// </remarks>
    public static IReadOnlyList<string> ParseAudioDevices(string ffmpegOutput) =>
        ParseDevices(ffmpegOutput, "(audio)");

    private static IReadOnlyList<string> ParseDevices(string ffmpegOutput, string suffix)
    {
        if (string.IsNullOrEmpty(ffmpegOutput))
        {
            return [];
        }

        var devices = new List<string>();

        foreach (var rawLine in ffmpegOutput.Split('\n'))
        {
            var line = rawLine.Trim();

            if (!line.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            // 设备名是这一行里第一个引号对之间的内容；前缀是 [dshow @ ...] 之类的上下文。
            var firstQuote = line.IndexOf('"');
            if (firstQuote < 0)
            {
                continue;
            }

            var secondQuote = line.IndexOf('"', firstQuote + 1);
            if (secondQuote <= firstQuote + 1)
            {
                continue;
            }

            var name = line[(firstQuote + 1)..secondQuote];
            if (!devices.Contains(name, StringComparer.Ordinal))
            {
                devices.Add(name);
            }
        }

        return devices;
    }
}
