using System.Globalization;
using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Import;

/// <summary>
/// 把「导入」那一屏上填的几项收成一个 <see cref="ImportRequest"/>；收不成时说清是哪儿不行。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 这一段原先写在 <c>ImportWindow.TryBuildRequest</c> 里（T27② 第 2 批搬过来）——
/// 那个工程没有测试工程，而这里收进去的三项（单号、业务类型、时间）**直接进证据元数据**：
/// 错一项，界面上完全看不出来，事后翻录像才会发现挂错了单号或者时间差了一天。
/// </para>
/// </remarks>
public static class ImportRequestBuilder
{
    /// <summary>
    /// 收一次。成功时 <c>Request</c> 非空、<c>Problem</c> 是空串；
    /// 不成时 <c>Request</c> 为 <see langword="null"/>、<c>Problem</c> 是要说给用户听的那句话。
    /// </summary>
    /// <param name="sourcePath">用户挑的那个文件；没挑就是 <see langword="null"/>。</param>
    /// <param name="waybillText">单号那一格里的原文（没规整过）。</param>
    /// <param name="startedDate">录制日期；没选就是 <see langword="null"/>。</param>
    /// <param name="startedTimeText">录制时间那一格里的原文（<c>"14:05:30"</c> 这种）。</param>
    /// <param name="isReturn">业务类型那一组选的是不是「退货」。</param>
    /// <param name="offset">本地时区偏移 —— 用户填的就是**这台机器上看到的那个时刻**。</param>
    public static (ImportRequest? Request, string Problem) Build(
        string? sourcePath,
        string? waybillText,
        DateOnly? startedDate,
        string? startedTimeText,
        bool isReturn,
        TimeSpan offset)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return (null, "先按【浏览…】挑一个要导入的文件。");
        }

        if (!WaybillNumber.TryParse(waybillText, out var waybill, out var waybillError))
        {
            return (null, $"单号这一项不行：{waybillError}");
        }

        if (startedDate is not { } date)
        {
            return (null, "请选一个录制日期。");
        }

        if (!TryParseTimeOfDay(startedTimeText, out var time))
        {
            return (null, "录制时间要写成「时:分:秒」，比如 14:05:30。");
        }

        return (
            new ImportRequest(
                sourcePath,
                waybill,
                isReturn ? BusinessType.Return : BusinessType.Outbound,
                new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue) + time, offset)),
            string.Empty);
    }

    /// <summary>
    /// 把用户手敲的时刻收成一天之内的那个 <see cref="TimeSpan"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 用宽松的 <see cref="TimeSpan.TryParse(string?, IFormatProvider?, out TimeSpan)"/>
    /// 而不是 <c>TryParseExact</c>：用户手敲的时间可能是 <c>9:5:3</c>、<c>09:05:03</c>、
    /// <c>9:05</c> 里的任何一种，都该收。
    /// 真正要挡住的是「跨了一天」—— 那种值会让起止时刻落到两个日子上，
    /// 而用户以为自己只填了个时刻。
    /// </remarks>
    private static bool TryParseTimeOfDay(string? text, out TimeSpan time) =>
        TimeSpan.TryParse((text ?? string.Empty).Trim(), CultureInfo.InvariantCulture, out time)
        && time >= TimeSpan.Zero
        && time < TimeSpan.FromDays(1);
}
