using System.Globalization;
using VidLog.Desktop.Core.Export;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Search;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「导出单号」把当前检索结果拼成 CSV（设计图 `_41` 左栏那颗按钮）。
/// </summary>
/// <remarks>
/// <para>
/// 这一半是**纯字符串**，所以它测得动 —— 落盘（含那个 BOM）在调用点，测试碰不到
/// （App 层没有测试工程）。
/// </para>
/// <para>
/// ⚠️ 下面钉得最死的是**转义与公式注入**那几条：它们错了**不会报错**，
/// 只会让用户手上的表错位，或者在 Excel 里执行一段以单号伪装的公式。
/// 而单号是扫码枪扫进来的外部输入。
/// </para>
/// </remarks>
public class WaybillCsvTests
{
    /// <summary>一个合法的内容哈希 —— <see cref="ContentHash"/> 只收 64 位十六进制。</summary>
    private static readonly string Hash = "abc12300" + new string('0', 56);

    /// <summary>哈希前 12 位推出来的会话号（`RecordingImporter` 就是这么定的）。</summary>
    private const string SessionId = "import-abc123000000";

    // ─────────────────────────────────────────────
    // 形状
    // ─────────────────────────────────────────────

    [Fact]
    public void 没有结果时也有一行表头()
    {
        // 界面上 0 条时那颗按钮是禁用的，但这一层不该靠界面的禁用活着。
        var lines = Lines(WaybillCsv.Build([]));

        var header = Assert.Single(lines);
        Assert.Equal(
            "单号,录制开始,录制结束,时长(秒),业务类型,内容哈希,归档相对路径",
            header);
    }

    [Fact]
    public void 一条结果一行_列的顺序按表头来()
    {
        var started = new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.FromHours(8));

        var csv = WaybillCsv.Build(
        [
            Hit(started: started, duration: TimeSpan.FromSeconds(83.5)),
        ]);

        var lines = Lines(csv);
        Assert.Equal(2, lines.Length);

        var cells = lines[1].Split(',');
        Assert.Equal(7, cells.Length);
        Assert.Equal("SF1000000001", cells[0]);
        Assert.Equal("83.5", cells[3]);
        Assert.Equal(Hash, cells[5]);
        Assert.Equal($"2026/09/30/SF1000000001/{SessionId}_000.mp4", cells[6]);
    }

    [Fact]
    public void 时间是本地时间_与检索页列表同一个口径()
    {
        // ⚠️ 拿 UTC 偏移来构造：本机不在 UTC 上时，「印本地时间」与「原样印」
        // 会得出两个不同的字符串 —— 这条才真的在验东西。
        var started = new DateTimeOffset(2026, 9, 30, 6, 5, 30, TimeSpan.Zero);

        var cells = Cells(Hit(started: started, duration: TimeSpan.FromSeconds(83)));

        Assert.Equal(Local(started), cells[1]);
        Assert.Equal(Local(started.AddSeconds(83)), cells[2]);
    }

    [Fact]
    public void 秒数按不变文化印_小数点设置变了也不会多出一列()
    {
        // ⚠️ 德语/法语区域下 `123,4` 里那个逗号就是**一列变两列**，整张表当场错位。
        var cells = Cells(Hit(duration: TimeSpan.FromSeconds(123.4)));

        Assert.Equal("123.4", cells[3]);
    }

    // ─────────────────────────────────────────────
    // 转义
    // ─────────────────────────────────────────────

    [Fact]
    public void 单号里有逗号时整格加引号()
    {
        // `WaybillNumber` 除了去空白与转大写之外**不做任何限制**，所以这些
        // 字符真的到得了这里 —— 不是假想出来的边界。
        //
        // ⚠️ 这里**不能**按逗号切格子来断言：切出来的正是「没加引号」的样子。
        // 断的是整行（后面那一截本来就跟着逗号）。
        Assert.StartsWith("\"SF,1\",", DataLine(Hit(waybill: "SF,1")));
    }

    [Fact]
    public void 单号里有引号时引号翻倍()
    {
        var cells = Cells(Hit(waybill: "SF\"1"));

        Assert.Equal("\"SF\"\"1\"", cells[0]);
    }

    // ─────────────────────────────────────────────
    // 公式注入（以 = + - @ 开头的格子会被 Excel 当公式执行）
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    public void 以公式符号开头的单号会被前缀一个单引号(string waybill)
    {
        var cells = Cells(Hit(waybill: waybill));

        Assert.Equal("'" + waybill, cells[0]);
    }

    [Fact]
    public void 既要以公式开头又要加引号时_单引号在前_引号在后()
    {
        // ⚠️ **顺序不能反**：先加引号的话那个单引号会落在引号**里面**，
        // 而 Excel 对引号内的内容不再做公式判定 —— 等于没防。
        //
        // 顺带能看到归一化那一步：`WaybillNumber` 会把它转成大写（`=a,b` → `=A,B`）。
        Assert.StartsWith("\"'=A,B\",", DataLine(Hit(waybill: "=a,b")));
    }

    [Fact]
    public void 不是公式符号开头的不动它()
    {
        Assert.Equal("SF1000000001", Cells(Hit(waybill: "SF1000000001"))[0]);
    }

    // ─────────────────────────────────────────────
    // 业务类型
    // ─────────────────────────────────────────────

    [Fact]
    public void 业务类型印中文_没标注时说没标注()
    {
        Assert.Equal("发货", Cells(Hit(businessType: BusinessTypes.OutboundValue))[4]);
        Assert.Equal("退货", Cells(Hit(businessType: BusinessTypes.ReturnValue))[4]);

        // 没打过标签的证据就是这一档 —— 印成「发货」等于替用户认了一个他没做的选择。
        Assert.Equal("未标注", Cells(Hit(businessType: null))[4]);
    }

    [Fact]
    public void 标签值认不出来时也说未标注()
    {
        Assert.Equal("未标注", Cells(Hit(businessType: "出库"))[4]);
    }

    // ─────────────────────────────────────────────
    // 造一条结果
    // ─────────────────────────────────────────────

    private static string Local(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string DataLine(RecordingHit hit) => Lines(WaybillCsv.Build([hit]))[1];

    /// <summary>⚠️ 只对**没有加引号**的行按逗号切 —— 加过引号的行要用 <see cref="DataLine"/>。</summary>
    private static string[] Cells(RecordingHit hit) => DataLine(hit).Split(',');

    private static string[] Lines(string csv) =>
        csv.Split(WaybillCsv.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static RecordingHit Hit(
        string waybill = "SF1000000001",
        string? businessType = null,
        string? hash = null,
        string? location = null,
        DateTimeOffset? started = null,
        TimeSpan? duration = null)
    {
        var at = started ?? new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.FromHours(8));
        var span = duration ?? TimeSpan.FromSeconds(83);

        var entry = new RecordingEntry(
            EvidenceId: $"{SessionId}-000",
            SessionId: SessionId,
            Waybill: WaybillNumber.Parse(waybill),
            StartedAt: at,
            EndedAt: at + span,
            Duration: span,
            Location: RelativePath.Parse(
                location ?? $"2026/09/30/SF1000000001/{SessionId}_000.mp4"),
            ContentHash: ContentHash.Parse(hash ?? Hash),
            SourceDeviceId: "imported");

        var labels = businessType is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [LabelKeys.BusinessType] = businessType };

        return new RecordingHit(entry, labels);
    }
}
