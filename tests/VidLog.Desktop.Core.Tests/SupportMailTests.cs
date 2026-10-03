using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「把诊断包发回给我们」那封信的内容（需求方 2026-10-03）。
/// </summary>
/// <remarks>
/// ⚠️ 这一组**只钉得住文本**（收件人 / 标题 / 正文）。
/// 「信到底有没有出去」在本机测不了 —— 那一步走的是用户机器上的邮件客户端，
/// 真验只能上手（见 <c>docs/真机验收清单.md</c> 与本节末尾那条）。
/// </remarks>
public class SupportMailTests
{
    /// <summary>
    /// ⚠️ **这条是这一组里最要紧的一条**：地址写错了，用户点「发送到开发者邮箱」
    /// 之后每一份诊断包都会寄到别人那儿，而**两边都不会报错**。
    /// </summary>
    [Fact]
    public void 收件人就是需求方给的那个地址()
    {
        Assert.Equal("Allen816@foxmail.com", SupportMail.Address);
    }

    [Fact]
    public void 标题里带机器名与时间_几台机器的包能分开()
    {
        var at = new DateTimeOffset(2026, 10, 3, 14, 5, 0, TimeSpan.FromHours(8));

        var subject = SupportMail.Subject("打包电脑-A", at);

        Assert.Contains("打包电脑-A", subject, StringComparison.Ordinal);
        // 时间按**本机时区**写出来（售后看的是用户那边的时间）。
        Assert.Contains("2026-10-03 14:05", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void 正文里带机器名与版本_且给用户留了一句位置()
    {
        var at = new DateTimeOffset(2026, 10, 3, 14, 5, 0, TimeSpan.FromHours(8));

        var body = SupportMail.Body("打包电脑-A", "0.4.0", at);

        Assert.Contains("打包电脑-A", body, StringComparison.Ordinal);
        Assert.Contains("0.4.0", body, StringComparison.Ordinal);
        Assert.Contains("2026-10-03 14:05:00", body, StringComparison.Ordinal);

        // ⚠️ 出问题的是用户那台机器，他比日志多知道「什么时候开始的」——
        // 那句留给他的话不许被删掉。
        Assert.Contains("出了什么问题", body, StringComparison.Ordinal);
    }
}
