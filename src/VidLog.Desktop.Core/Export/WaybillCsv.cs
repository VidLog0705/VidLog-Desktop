using System.Globalization;
using System.Text;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Search;

namespace VidLog.Desktop.Core.Export;

/// <summary>
/// 「导出单号」—— 把**当前这一次检索的结果**写成一个 CSV（设计图 `_41` 左栏那颗按钮）。
/// </summary>
/// <remarks>
/// <para>
/// 导的是**当前检索出来的那一批**，不是整个库：那颗按钮就摆在日期范围与单号框底下，
/// 「我筛出来的这一批」是它唯一说得通的读法。
/// </para>
/// <para>
/// ⚠️ <b>它不是录像，也不进索引</b>（I7 的同一条口径）：导出的是一张清单，
/// 落在用户自选的位置，不参与检索、回放、清理判定。
/// </para>
/// <para>
/// ⚠️ <b>这个类只产出字符串，不碰盘</b> —— 落盘（含那个 BOM）在调用点，
/// 因为「写哪儿」是界面的事。字符串这一半是纯函数，所以它**能测**。
/// </para>
/// </remarks>
public static class WaybillCsv
{
    /// <summary>另存为对话框里的默认文件名。</summary>
    public const string DefaultFileName = "单号.csv";

    /// <summary>
    /// Excel 在这一行上认列，而 Windows 上的 Excel / WPS 只认 <c>\r\n</c>。
    /// </summary>
    public const string NewLine = "\r\n";

    /// <summary>
    /// 表头。⚠️ 列的顺序就按这个来 —— 用户多半是拿去当对账单看的，
    /// 单号必须是最左边那一列。
    /// </summary>
    private static readonly string[] Columns =
        ["单号", "录制开始", "录制结束", "时长(秒)", "业务类型", "内容哈希", "归档相对路径"];

    /// <summary>把这一批结果拼成 CSV 正文（**不含 BOM**，见调用点）。</summary>
    public static string Build(IEnumerable<RecordingHit> hits)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(',', Columns.Select(Escape))).Append(NewLine);

        foreach (var hit in hits)
        {
            var entry = hit.Entry;

            builder
                .Append(Escape(entry.Waybill.Value)).Append(',')
                .Append(Escape(Local(entry.StartedAt))).Append(',')
                .Append(Escape(Local(entry.EndedAt))).Append(',')
                // 秒数按不变文化印：机器的小数点设置一变，`123.4` 会写成 `123,4`,
                // 而那个逗号是一列变成两列 —— 表格当场错位。
                .Append(Escape(entry.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)))
                .Append(',')
                .Append(Escape(Describe(hit.BusinessType))).Append(',')
                .Append(Escape(entry.ContentHash.Value)).Append(',')
                .Append(Escape(entry.Location.Value))
                .Append(NewLine);
        }

        return builder.ToString();
    }

    /// <summary>本地时间，与检索页列表上印的那一列**同一个口径**。</summary>
    /// <remarks>
    /// ⚠️ 索引里存的是带偏移的时间戳，这里转成本地时间印出来 —— 用户拿到 CSV
    /// 会去跟界面上的列表逐行对，两边时区口径不一致的话每一行都对不上，
    /// 而那看起来像导错了数据。
    /// </remarks>
    private static string Local(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Describe(BusinessType? type) =>
        type is { } known ? BusinessTypes.Describe(known) : "未标注";

    /// <summary>
    /// 一个格子：该加引号就加引号，该防公式注入就防。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>公式注入是真的可能发生的</b>：Excel / WPS 打开 CSV 时会把以
    /// <c>=</c> <c>+</c> <c>-</c> <c>@</c> 开头的格子**当公式执行**
    /// （`=HYPERLINK(...)`、`=cmd|...` 那一类）。而单号是**外部输入** ——
    /// 扫码枪扫进来的一串字符，`WaybillNumber` 除了去空白与转大写之外不做任何限制。
    /// 前缀一个单引号是通行做法（OWASP）：Excel 把它当「这是文本」的标记，
    /// 而且**不会把它显示出来**。
    /// </para>
    /// <para>
    /// ⚠️ 先防注入、后加引号，顺序不能反：反过来那个单引号就落在引号里面，
    /// 而 Excel 对引号内的内容不再做公式判定 —— 等于没防。
    /// </para>
    /// </remarks>
    private static string Escape(string value)
    {
        var guarded = value.Length > 0 && value[0] is '=' or '+' or '-' or '@'
            ? "'" + value
            : value;

        return guarded.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? guarded
            : $"\"{guarded.Replace("\"", "\"\"")}\"";
    }
}
