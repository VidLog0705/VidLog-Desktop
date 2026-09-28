namespace VidLog.Desktop.App;

/// <summary>
/// 把数字写成人看的样子。
/// </summary>
/// <remarks>
/// ⚠️ 这两个是**显示**用的，不是算的：字节数一律是估值（见
/// <c>CleanupPlanner.EstimateBytes</c>），所以调用处**必须自己带上「约」**
/// —— 这里不替它加，因为有的地方（比如磁盘的实测可用空间）不是估的，
/// 而一个无条件加「约」的格式化函数会把那种地方也说得含糊。
/// </remarks>
internal static class Display
{
    /// <summary>字节数写成人看的大小。</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    /// <summary>时长写成人看的样子。</summary>
    /// <remarks>
    /// ⚠️ 不写成 <c>HH:MM:SS</c>：那套是**播放进度条**上的写法（见
    /// <c>SearchWindow</c>），用在这里的话「累计工作时长 03:12:40」要用户
    /// 自己心算成「3 小时 12 分」。这一页全是给人看的总量。
    /// </remarks>
    public static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒"
        : span.TotalSeconds >= 1 ? $"{span.TotalSeconds:0.#} 秒"
        : "0 秒";
}
