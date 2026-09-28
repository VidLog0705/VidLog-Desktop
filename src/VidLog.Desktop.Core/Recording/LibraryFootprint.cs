using System.IO;

namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 本机录像库的占用情况。
/// </summary>
/// <param name="FileCount">数到的文件个数。</param>
/// <param name="TotalBytes">数到的文件字节数合计。</param>
/// <param name="UnreadableCount">
/// 数不到的**目录或文件**个数。
/// </param>
/// <remarks>
/// ⚠️ <see cref="UnreadableCount"/> 存在的全部理由是：**不许让那个字节数静默偏小**。
/// 读不到的目录下面可能有一堆 GB，而界面上只显示一个干净的「12.3 GB」——
/// 用户会拿它当真。宁可写「12.3 GB（含 3 个读不到的位置）」。
/// </remarks>
public sealed record LibraryFootprint(int FileCount, long TotalBytes, int UnreadableCount);

/// <summary>
/// 量一次本机录像目录占了多少盘。
/// </summary>
/// <remarks>
/// <para>
/// 放在 Core 而不是界面里，是因为它要在**临时目录上被测**，
/// 而 App 层（<c>net9.0-windows</c>）没有测试工程能引用。
/// </para>
/// <para>
/// ⚠️ 它是**全量遍历**：几万个文件时是秒级。调用方必须在
/// <c>Task.Run</c> 里调，别挂在 UI 线程上。
/// </para>
/// </remarks>
public static class LibraryFootprintProbe
{
    /// <summary>量一遍 <paramref name="root"/> 下面的所有文件。</summary>
    /// <remarks>
    /// 目录不存在（还没录过东西）返回全零，**不抛** —— 那是个正常状态，不是错误。
    /// </remarks>
    public static LibraryFootprint Measure(string root, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return new LibraryFootprint(0, 0, 0);
        }

        var files = 0;
        var bytes = 0L;
        var unreadable = 0;

        // 自己压栈而不是 SearchOption.AllDirectories：那个的
        // IgnoreInaccessible 会把读不了的目录**悄悄跳过**，于是上面那个
        // UnreadableCount 永远是 0 —— 那正是本类要防的事。
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        bytes += new FileInfo(file).Length;
                        files++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        unreadable++;
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
            }
        }

        return new LibraryFootprint(files, bytes, unreadable);
    }
}
