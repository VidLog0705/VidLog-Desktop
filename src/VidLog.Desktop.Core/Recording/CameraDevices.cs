using System.Diagnostics;
using VidLog.Desktop.Core.Media;

namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 枚举本机的 DirectShow 视频设备。
/// </summary>
/// <remarks>
/// 形状仿 <see cref="FfmpegLocator"/>：静态、不抛、找不到就给空。
/// 「没有摄像头」是一个**正常的运行环境**（上一台机器就是），
/// 不是异常 —— 界面该把它显示成一句说明，而不是一条崩溃。
/// </remarks>
public static class CameraDevices
{
    /// <summary>
    /// 列出可用的视频设备名。
    /// </summary>
    /// <returns>设备名；一个都没有（或 ffmpeg 跑不起来）时返回空列表。</returns>
    public static async Task<IReadOnlyList<string>> ListAsync(
        string ffmpegPath,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
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
                return [];
            }

            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return ParseVideoDevices(stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // ffmpeg 不在、或起不来。上层已经有一条「找不到 FFmpeg」的警告了，
            // 这里再抛一次只会变成重复噪声。
            return [];
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
    public static IReadOnlyList<string> ParseVideoDevices(string ffmpegOutput)
    {
        if (string.IsNullOrEmpty(ffmpegOutput))
        {
            return [];
        }

        var devices = new List<string>();

        foreach (var rawLine in ffmpegOutput.Split('\n'))
        {
            var line = rawLine.Trim();

            if (!line.EndsWith("(video)", StringComparison.Ordinal))
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
