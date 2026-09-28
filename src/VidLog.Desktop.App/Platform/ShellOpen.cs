using System.Diagnostics;
using System.IO;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 用系统默认程序打开一个路径或网址。
/// </summary>
/// <remarks>
/// 抽出来是因为<b>拆窗之后两个窗口都要用它</b>（主窗的「局域网回放页」、
/// 设置窗的「打开数据目录」）—— 各写一份的话，两边的「打不开怎么办」
/// 迟早会走岔，而这一类静默失效正是 I3 要防的。
/// </remarks>
internal static class ShellOpen
{
    /// <summary>
    /// 打开它。
    /// </summary>
    /// <returns>
    /// 成功返回 <see langword="null"/>；打不开返回**一句给人看的原因**
    /// （调用方负责把它显示出来，不许吞掉）。
    /// </returns>
    public static string? Try(string target)
    {
        if (string.IsNullOrWhiteSpace(target)
            || !File.Exists(target) && !target.StartsWith("http", StringComparison.Ordinal))
        {
            return $"打不开：{target} 不是一个存在的文件，也不是一个网址。";
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            // 打不开不是致命错 —— 但**必须说出来**，否则用户点了按钮什么都没发生。
            return $"打不开：{ex.Message}";
        }
    }
}
