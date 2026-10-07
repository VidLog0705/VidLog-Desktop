using VidLog.Desktop.Core.Import;
using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「导入」那一屏收请求那一步（T27② 第 2 批从 <c>ImportWindow</c> 搬进 Core）。
/// </summary>
/// <remarks>
/// ⚠️ 收进去的三项（单号、业务类型、时间）**直接进证据元数据**：错一项，
/// 界面上完全看不出来 —— 事后翻录像才会发现挂错了单号，或者时间差了一天。
/// 原先它在没有测试工程的 App 里。
/// </remarks>
public class ImportRequestBuilderTests
{
    private static readonly DateOnly Day = new(2026, 3, 15);
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    private static (ImportRequest? Request, string Problem) Build(
        string? sourcePath = @"D:\in\SF123.mp4",
        string? waybillText = "SF123",
        DateOnly? startedDate = null,
        string? startedTimeText = "14:05:30",
        bool isReturn = false) =>
        ImportRequestBuilder.Build(
            sourcePath, waybillText, startedDate ?? Day, startedTimeText, isReturn, Offset);

    [Fact]
    public void 都填好了就成_时刻按本地偏移落在那一天那一刻()
    {
        var (request, problem) = Build();

        Assert.NotNull(request);
        Assert.Equal(string.Empty, problem);
        Assert.Equal(@"D:\in\SF123.mp4", request.SourcePath);
        Assert.Equal("SF123", request.Waybill.Value);
        Assert.Equal(BusinessType.Outbound, request.BusinessType);
        Assert.Equal(new DateTimeOffset(2026, 3, 15, 14, 5, 30, Offset), request.StartedAt);
    }

    [Fact]
    public void 退货那一档要如实带进去()
    {
        // ⚠️ 业务类型也进证据元数据 —— 映射写反了，界面上看不出任何异样。
        Assert.Equal(BusinessType.Return, Build(isReturn: true).Request!.BusinessType);
    }

    [Fact]
    public void 没挑文件就不收()
    {
        var (request, problem) = Build(sourcePath: null);

        Assert.Null(request);
        Assert.Contains("浏览", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 单号不行就不收_而且要说清单号这一项()
    {
        var (request, problem) = Build(waybillText: "   ");

        Assert.Null(request);
        Assert.Contains("单号", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 没选日期就不收()
    {
        var (request, problem) = ImportRequestBuilder.Build(
            @"D:\in\SF123.mp4", "SF123", startedDate: null, "14:05:30", false, Offset);

        Assert.Null(request);
        Assert.Contains("日期", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("14:05:30", 14, 5, 30)]
    [InlineData("9:5:3", 9, 5, 3)]
    [InlineData("09:05:03", 9, 5, 3)]
    [InlineData("9:05", 9, 5, 0)]
    [InlineData(" 14:05:30 ", 14, 5, 30)]
    public void 手敲的时刻宽松收_几种写法都算数(string text, int hour, int minute, int second)
    {
        // 用户手敲的时间可能是这几种里的任何一种，都该收（宽松 `TryParse`，非 `Exact`）。
        var (request, _) = Build(startedTimeText: text);

        Assert.NotNull(request);
        Assert.Equal(new TimeSpan(hour, minute, second), request.StartedAt.TimeOfDay);
        Assert.Equal(Day, DateOnly.FromDateTime(request.StartedAt.DateTime));
    }

    [Theory]
    [InlineData("1.02:00:00")]  // 跨了一天（带日的那写法）
    [InlineData("25:00:00")]    // 跨了一天（时刻直接写到 24 以上）
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("下午两点")]
    public void 时刻跨了一天或者写不成时刻就不收(string text)
    {
        // ⚠️ 这条是这一块的要害：跨了一天的值会让起止时刻**落到两个日子上**，
        // 而用户以为自己只填了个时刻 —— 界面上一点都看不出来。
        //
        // ⚠️ `"25:00:00"` 那一条：`TimeSpan.TryParse` **会成功**（25 小时），
        // 所以它靠的是下面那条「必须 < 一天」的范围检查，不是解析失败。
        // 2026-10-07 证伪时验到的 —— 把范围检查删掉，这条和 `"1.02:00:00"` 一起红。
        var (request, problem) = Build(startedTimeText: text);

        Assert.Null(request);
        Assert.Contains("时:分:秒", problem, StringComparison.Ordinal);
    }
}
