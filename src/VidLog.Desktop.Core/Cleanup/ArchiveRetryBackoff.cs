namespace VidLog.Desktop.Core.Cleanup;

/// <summary>
/// 补发两趟之间该隔多久（3.2 掉线熔断）。
/// </summary>
/// <remarks>
/// 它就是「指数退避」那四行，抽出来只为一件事：**它是能被验的**。
/// 留在循环体里的话，「欠账一直划不掉 ⇒ 间隔翻倍」这句话要么靠读代码相信，
/// 要么写一条靠掐表的测试 —— 而掐表的那种测试在忙起来的 CI 机器上会自己红。
/// </remarks>
public static class ArchiveRetryBackoff
{
    /// <summary>下一趟的间隔。</summary>
    /// <param name="current">这一趟用的间隔。</param>
    /// <param name="owedBefore">这一趟**开始之前**欠着几条。</param>
    /// <param name="owedAfter">这一趟**结束之后**还欠着几条。</param>
    /// <param name="baseInterval">起点（有进展就回到这儿）。</param>
    /// <param name="maxInterval">上限。</param>
    public static TimeSpan Next(
        TimeSpan current,
        int owedBefore,
        int owedAfter,
        TimeSpan baseInterval,
        TimeSpan maxInterval)
    {
        // ⚠️ 判据是**欠账变少了**，不是「这一趟试了几条」：`RetryOutstandingAsync` 返回的是
        // 「试了几条」—— 发成了是 1、发败了也是 1。拿那个当判据会永远「有进展」，
        // 于是归档层掉线时还是每 60 秒敲一次（而那正是这一档要防的事）。
        //
        // ⚠️ `owedAfter == 0`（这本账本来就空）算**有进展**。不算的话，一台什么都没欠的
        // 机器会闲着把间隔一路涨到上限，而下一段录像真失败时，那一条欠账要等半小时
        // 才有人管 —— 那是拿「不该敲」惩罚了「该敲」。
        return owedAfter > 0 && owedAfter >= owedBefore
            ? TimeSpan.FromTicks(Math.Min(maxInterval.Ticks, current.Ticks * 2))
            : baseInterval;
    }
}
