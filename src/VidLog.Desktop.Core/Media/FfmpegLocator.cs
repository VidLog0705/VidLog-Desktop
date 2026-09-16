namespace VidLog.Desktop.Core.Media;

/// <summary>
/// 找到本机的 FFmpeg。
/// </summary>
/// <remarks>
/// 查找顺序（先命中先返回）：
/// <list type="number">
/// <item>调用方显式给的路径（用户设置里指定的）</item>
/// <item>环境变量 <c>FFMPEG_EXE</c>（开发机与 CI 用）</item>
/// <item>应用目录 / 应用目录下的 <c>tools\</c>（发布时 FFmpeg 随包分发）</item>
/// <item><c>PATH</c></item>
/// </list>
/// 按这个顺序的理由：越靠前越「离这个应用近」，越不受机器全局环境干扰。
/// 把 PATH 放最后 —— 机器上装过别的 FFmpeg 不该悄悄改变取证行为。
/// </remarks>
public static class FfmpegLocator
{
    public const string EnvironmentVariable = "FFMPEG_EXE";

    /// <summary>随包分发的相对位置（见发布脚本的依赖缓存约定）。</summary>
    public static string BundledRelativePath =>
        Path.Combine("tools", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    public static IReadOnlyList<string> ExecutableNames { get; } =
        OperatingSystem.IsWindows() ? ["ffmpeg.exe"] : ["ffmpeg"];

    /// <summary>按上述顺序找 FFmpeg；找不到返回 <see langword="null"/>。</summary>
    public static string? TryFind(string? explicitPath = null, string? applicationDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        if (!string.IsNullOrWhiteSpace(applicationDirectory))
        {
            foreach (var name in ExecutableNames)
            {
                var beside = Path.Combine(applicationDirectory, name);
                if (File.Exists(beside))
                {
                    return beside;
                }

                var bundled = Path.Combine(applicationDirectory, "tools", name);
                if (File.Exists(bundled))
                {
                    return bundled;
                }
            }
        }

        return FindOnPath();
    }

    private static string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var name in ExecutableNames)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim(), name);
                }
                catch (ArgumentException)
                {
                    // PATH 里可能有非法路径片段，跳过。
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
