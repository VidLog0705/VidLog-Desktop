using VidLog.Desktop.Core.Labels;

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
    /// 归档根下那第一层目录名 —— <b>所有存储端共用这一个名字</b>。
    /// </summary>
    /// <remarks>
    /// 需求方 2026-10-12 写死：录像是 <c>VidLog-Video/…</c> 下的东西，
    /// 别的东西（日志、索引、工作目录）不是。本机归档根、用户配的盘、NAS
    /// 三处都靠它把「这是录像」与「这是别的」分开。
    /// <para>
    /// ⚠️ 它**不是**兜底根 <c>DataLayout.ArchiveRoot</c> 的改名。改兜底根的话
    /// 老录像（躺在 <c>archive/</c> 下）会当场「不见了」；把它做成相对路径的第一段，
    /// 老文件原地不动、照旧回查得到。
    /// </para>
    /// </remarks>
    public const string RootFolder = "VidLog-Video";

    /// <summary>
    /// 同一个单号最多允许多少份成品（撞名时试到 <c>_2</c>…<c>_100</c> 为止）。
    /// </summary>
    /// <remarks>
    /// 这个数是**防呆**，不是业务规则：真堆到一百份说明用户从来没清理过，
    /// 那时该停下来让人看一眼，而不是继续静静地在盘上多铺一个名字。
    /// <para>
    /// ⚠️ <b>三个落点共用这一个数</b>（收尾 / 导入 / 接收）—— 各写各的常量迟早会走岔，
    /// 而走岔的表现是「同一单号在某一条路上能落到 `_150`、在另一条上却报错」。
    /// </para>
    /// </remarks>
    public const int MaxDuplicates = 99;

    /// <summary>
    /// 拼出归档层内的相对路径：<c>VidLog-Video/&lt;yyyy-MM-dd&gt;/&lt;发货|退货&gt;/&lt;单号&gt;.mp4</c>。
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
    /// <para>
    /// ⚠️ <b>「发货 / 退货」这一层是「开始工作时选的那一档」，不是标签的当前值</b>
    /// （需求方 2026-10-12 裁定）。标签按 I5 是可以改的，路径不跟着改 ——
    /// 否则改一次标签就得把已经发去 NAS / 网盘的成品搬一遍。
    /// </para>
    /// <para>
    /// ⚠️ <b>文件名里不再有会话号与序号</b>：一场录制收尾时已经合成一个文件
    /// （见 <c>SessionFinalizer</c>），所以一个单号就是一个文件。同一单号录第二场时
    /// 由调用方（<c>SessionFinalizer</c>）追加 <c>_2</c> 去重，**不在这里** ——
    /// 这里连磁盘都碰不到，判不出撞没撞。
    /// </para>
    /// </remarks>
    public static RelativePath BuildLocation(
        WaybillNumber waybill,
        BusinessType businessType,
        DateTimeOffset startedAt)
    {
        var relative = string.Join(
            '/',
            RootFolder,
            startedAt.ToString("yyyy-MM-dd"),
            BusinessTypes.Describe(businessType),
            $"{Sanitize(waybill.Value)}.mp4");

        return RelativePath.Parse(relative);
    }

    /// <summary>
    /// 同一个单号的**第几份**落盘路径：<c>0</c> → 原名，<c>1</c> → <c>_2</c>，<c>2</c> → <c>_3</c>…
    /// </summary>
    /// <remarks>
    /// 需求方 2026-10-12 裁定：一个单号一个文件，**同一单号录第二场就加 <c>_2</c>**。
    /// <para>
    /// ⚠️ 这里**只算名字，不碰磁盘**。撞没撞、撞上那个算不算「同一个东西」
    /// （重复导入、断点续传要判内容哈希），三个调用点的判据都不一样，
    /// 所以那个循环留在各自那儿 —— 硬抽成一个函数就得把三种策略塞进一堆参数里。
    /// </para>
    /// <para>
    /// ⚠️ 后缀插在**扩展名之前**（<c>SF123_2.mp4</c>），不是 <c>SF123.mp4_2</c> ——
    /// 后者不是 mp4，双击打不开。
    /// </para>
    /// </remarks>
    public static RelativePath Deduplicated(RelativePath location, int ordinal)
    {
        if (ordinal <= 0)
        {
            return location;
        }

        var value = location.Value;
        var slash = value.Replace('\\', '/').LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : value[..(slash + 1)];
        var name = value[(slash + 1)..];
        var dot = name.LastIndexOf('.');

        var stem = dot <= 0 ? name : name[..dot];
        var extension = dot <= 0 ? string.Empty : name[dot..];

        return RelativePath.Parse($"{directory}{stem}_{ordinal + 1}{extension}");
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
