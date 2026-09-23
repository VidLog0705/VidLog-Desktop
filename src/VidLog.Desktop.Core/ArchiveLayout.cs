namespace VidLog.Desktop.Core;

/// <summary>
/// 归档层里的目录与文件名规则。
/// </summary>
/// <remarks>
/// 规格 §6.2：**绝对路径在应用重装、容器变更后必然失效**，所以进索引的只有相对路径，
/// 而这条相对路径就是由这里拼出来的。
/// <para>
/// <b>为什么单独拎出来</b>：远端上传（M5）与本地录制收尾（M2）是两条完全不同的代码路径，
/// 但它们**必须落在同一个布局上** —— 用户在归档目录里看到的应该是一套东西，
/// 而不是「本机录的按日期分目录、手机传的全堆在根下」。
/// 两处各写一遍的话，它们会在某次改动里悄悄走岔，而走岔之后**不会报错**：
/// 文件照样存得下、索引照样查得到，只是目录慢慢变得没法按日期清理（§3.5.2）。
/// </para>
/// </remarks>
public static class ArchiveLayout
{
    /// <summary>
    /// 拼出归档层内的相对路径：<c>&lt;yyyy&gt;/&lt;MM&gt;/&lt;dd&gt;/&lt;单号&gt;/&lt;会话&gt;_&lt;序号&gt;.mp4</c>。
    /// </summary>
    /// <remarks>
    /// 按日期分目录：单号检索是主路径，但按日期清理（§3.5.2「保留最近 N 天」）
    /// 需要能按时间范围低成本地列文件，目录分层比全表扫描可靠。
    /// <para>
    /// ⚠️ 日期取的是 <paramref name="startedAt"/> **自带的那个时区**，不要在这里
    /// <c>ToLocalTime()</c>：本机录制与远端上传给的都是 UTC 时间戳，两条路径因此
    /// 落在同一个日期目录里。就地转本地时区会让**同一个时刻**在两条路径下
    /// 分进不同的日子 —— 而按日期清理（§3.5.2）正是靠这个分层，分岔了就会有
    /// 一段录像永远落在清理范围之外。
    /// </para>
    /// </remarks>
    public static RelativePath BuildLocation(
        WaybillNumber waybill,
        string sessionId,
        int sequence,
        DateTimeOffset startedAt)
    {
        var relative = string.Join(
            '/',
            startedAt.ToString("yyyy"),
            startedAt.ToString("MM"),
            startedAt.ToString("dd"),
            Sanitize(waybill.Value),
            $"{Sanitize(sessionId)}_{sequence:000}.mp4");

        return RelativePath.Parse(relative);
    }

    /// <summary>把单号 / 会话号里不能进文件名的字符换掉。</summary>
    /// <remarks>
    /// 只留 ASCII 字母数字与 <c>-</c> <c>_</c>。⚠️ 顺带也堵住了路径穿越 ——
    /// 单号是**外部输入**（扫码来的），它里面出现 <c>/</c> 或 <c>..</c> 不是不可能。
    /// </remarks>
    public static string Sanitize(string value)
    {
        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            buffer[i] = char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_';
        }

        return new string(buffer);
    }
}
