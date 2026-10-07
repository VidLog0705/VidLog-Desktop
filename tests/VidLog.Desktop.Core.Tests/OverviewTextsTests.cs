using VidLog.Desktop.Core.Overview;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 右栏「本机录制动态」那一屏上的字（T27② 第 3 批块 2）。
/// </summary>
/// <remarks>
/// <para>
/// 这一族原先在 <c>MainWindow.Overview.cs</c> 里，而那个工程没有测试工程 ——
/// 于是下面每一条规矩**都只是注释**。它们各自的坏法都不显眼：
/// 「约」丢了是数字看着更准了，「读不到」没说是一个**偏小但看着正常**的数，
/// 而摄像头那一格印错字段是**把密码印在屏幕上**。
/// </para>
/// <para>
/// ⚠️ 所以这里的用例是**逐条钉坏法**，不是「随便调一下看返回什么」。
/// </para>
/// </remarks>
public class OverviewTextsTests
{
    // ─────────────────────────────────────────────
    // ① 今天
    // ─────────────────────────────────────────────

    [Fact]
    public void 今天一段都没录时_平均不会除零()
    {
        var (known, average) = OverviewTexts.TodayTotals([]);

        Assert.Equal(TimeSpan.Zero, known);
        Assert.Equal(TimeSpan.Zero, average);
    }

    [Fact]
    public void 平均与已知时长按条数算()
    {
        var (known, average) = OverviewTexts.TodayTotals(
            [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(60)]);

        Assert.Equal(TimeSpan.FromSeconds(180), known);
        Assert.Equal(TimeSpan.FromSeconds(60), average);
    }

    [Fact]
    public void 平均那一格是截断_不四舍五入()
    {
        // ⚠️ 33.9 秒印成 33，不是 34 —— 界面上那一格是 `(int)TotalSeconds`。
        // 别顺手改成 `Math.Round`：这个数是「大概每段多久」，
        // 而凑得越整齐越像实测出来的。
        //
        // ⚠️ 注意截断发生在**印的时候**，不在算的时候：`TodayTotals` 除出来的是
        // `FromTicks(ticks / count)`，保留亚秒（100 秒 / 3 段 = 33.3333333 秒）。
        // 第一版这条断言写的是「算出来就是 33 秒」，**前提就是错的**。
        Assert.Equal("平均 33 秒", OverviewTexts.TodayAverage(TimeSpan.FromSeconds(33.9)));
        Assert.Equal("平均 34 秒", OverviewTexts.TodayAverage(TimeSpan.FromSeconds(34)));
    }

    [Fact]
    public void 平均是总和除以段数_不是别的什么()
    {
        var (known, average) = OverviewTexts.TodayTotals(
            [TimeSpan.FromSeconds(100), TimeSpan.Zero, TimeSpan.Zero]);

        Assert.Equal(TimeSpan.FromSeconds(100), known);
        Assert.Equal(TimeSpan.FromTicks(TimeSpan.FromSeconds(100).Ticks / 3), average);
    }

    [Fact]
    public void 底部三条与右栏那一行的措辞()
    {
        var known = TimeSpan.FromSeconds(3725);   // 1 小时 2 分 5 秒

        Assert.Equal("今日 4 件", OverviewTexts.TodayCount(4));
        Assert.Equal("平均 93 秒", OverviewTexts.TodayAverage(TimeSpan.FromSeconds(93)));
        Assert.Equal("总耗时 3725 秒", OverviewTexts.TodayTotal(known));
        Assert.Equal("4 段 · 已知时长合计 1 小时 2 分", OverviewTexts.TodaySummary(4, known));
        Assert.Equal("7 段", OverviewTexts.IndexCount(7));
    }

    [Fact]
    public void 总耗时写的是已知时长_不能省掉那两个字()
    {
        // ⚠️ 时长是从元数据读的，读不到的那一段计 0 ⇒ 这个数天然偏小。
        // 写成「总时长」的话，用户会拿它当准的（这一段是全仓最容易被顺手「简写」的一句）。
        Assert.Contains("已知", OverviewTexts.TodaySummary(3, TimeSpan.FromMinutes(5)), StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────
    // ① 录像库
    // ─────────────────────────────────────────────

    [Fact]
    public void 库容量正常时不提读不到()
    {
        var text = OverviewTexts.Library(new LibraryFootprint(12, 3L * 1024 * 1024, 0));

        Assert.Contains("12 个文件", text, StringComparison.Ordinal);
        Assert.DoesNotContain("读不到", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 有读不到的位置就必须说出来_否则那个数静默偏小()
    {
        var text = OverviewTexts.Library(new LibraryFootprint(12, 3L * 1024 * 1024, 2));

        Assert.Contains("读不到", text, StringComparison.Ordinal);
        Assert.Contains("实际只会更多", text, StringComparison.Ordinal);

        // ⚠️ 这句是**对着用户判断「盘还够不够用」**说的 —— 少了后半句，
        // 那个偏小的数看着完全正常。
        Assert.Contains("2 处", text, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────
    // ② 状态
    // ─────────────────────────────────────────────

    [Fact]
    public void 没FFmpeg比没摄像头更早说()
    {
        // ⚠️ 两样都缺时先说的是 FFmpeg：那是**装坏了**（要重装），
        // 而「没找到摄像头」是**插一根线**就行 —— 说反了用户会去翻设备管理器。
        Assert.Equal("没有 FFmpeg，无法采集", OverviewTexts.Camera(false, CameraSource.None));
        Assert.Equal("没有找到摄像头", OverviewTexts.Camera(true, CameraSource.None));
        Assert.Equal("USB 摄像头", OverviewTexts.Camera(true, CameraSource.Local("USB 摄像头")));
    }

    [Fact]
    public void 网络摄像头那一格不许印出地址里的密码()
    {
        // ⚠️⚠️ 这是这一族里**后果最贵**的一条：这一格是给人看的，
        // 而网络地址里有凭据（`rtsp://账号:密码@主机/流`）。
        // 用 `Address` 而不是 `Display`/`Identity` 的话，摄像头密码就印在屏幕上，
        // 而且用户会截图发给售后。
        var camera = CameraSource.Network("rtsp://admin:hunter2@192.168.101.55:8554/live");

        var text = OverviewTexts.Camera(true, camera);

        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("网络摄像头", text, StringComparison.Ordinal);
        Assert.Contains("192.168.101.55", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 回放服务三档()
    {
        Assert.Equal("未启动", OverviewTexts.Server(null, false));
        Assert.Equal("未启动", OverviewTexts.Server(string.Empty, false));
        Assert.Equal("已启动 · http://192.168.1.5:8720", OverviewTexts.Server("http://192.168.1.5:8720", false));
    }

    [Fact]
    public void 只绑到本机那一档必须说明白()
    {
        // ⚠️ 不说的话用户会拿这个地址去别的设备上试，然后以为是自己网断了。
        var text = OverviewTexts.Server("http://127.0.0.1:8720", true);

        Assert.Contains("只绑到本机", text, StringComparison.Ordinal);
        Assert.Contains("访问不了", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 归档层配不好时不能只写标签()
    {
        // ⚠️ 只写「NAS」的话，用户会以为东西已经备份出去了 ——
        // 这是 I1/I3 那条线上最贵的一种误解（他会去删本地那份）。
        var text = OverviewTexts.Archive("NAS", "共享目录连不上");

        Assert.Contains("没配好", text, StringComparison.Ordinal);
        Assert.Contains("共享目录连不上", text, StringComparison.Ordinal);

        Assert.Equal("本机磁盘", OverviewTexts.Archive("本机磁盘", null));
        Assert.Equal("本机磁盘", OverviewTexts.Archive("本机磁盘", string.Empty));
    }

    [Fact]
    public void 可用空间读不到时不许给出一个数字()
    {
        // ⚠️ 读盘那条路是**抛**的（不是返回 -1）—— 一个「0 GB」会被当成真的，
        // 用户据此判断「盘满了」，然后去删录像。
        var text = OverviewTexts.FreeSpaceUnreadable("拒绝访问");

        Assert.Contains("读不到", text, StringComparison.Ordinal);
        Assert.Contains("拒绝访问", text, StringComparison.Ordinal);

        // ⚠️ 判据是「一个容量数字都没有」：有 GB/MB 就说明它渲染成了一个数，
        // 而那正是用户会拿去判断「盘满了」的东西。
        Assert.DoesNotContain("GB", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MB", text, StringComparison.Ordinal);

        Assert.StartsWith("可用 ", OverviewTexts.FreeSpace(1024L * 1024 * 1024), StringComparison.Ordinal);
        Assert.Contains("GB", OverviewTexts.FreeSpace(1024L * 1024 * 1024), StringComparison.Ordinal);
    }

    [Fact]
    public void 设备数那句()
    {
        Assert.Equal("还没有手机接进来", OverviewTexts.Devices(0));
        Assert.Equal("3 台", OverviewTexts.Devices(3));
    }

    // ─────────────────────────────────────────────
    // ④ 备份主机那一屏
    // ─────────────────────────────────────────────

    [Fact]
    public void 备份主机那三样都跟着设备数走()
    {
        Assert.True(OverviewTexts.DeviceMissing(0));
        Assert.False(OverviewTexts.DeviceMissing(1));

        Assert.Equal("暂无设备", OverviewTexts.BackupDeviceTag(0));
        Assert.Equal("已就绪", OverviewTexts.BackupDeviceTag(1));

        Assert.Equal(
            "还没有手机或电脑接进来。用上面的【连接电脑/手机】把它们加进来。",
            OverviewTexts.BackupDeviceText(0));
        Assert.Equal("已接入 2 台设备，录像会存到本机。", OverviewTexts.BackupDeviceText(2));
    }

    [Fact]
    public void 备份主机没设备时要说下一步去哪儿()
    {
        // ⚠️ 这一屏唯一的毛病就是「还没配过」，不说下一步用户会去别处找。
        Assert.Contains("连接电脑/手机", OverviewTexts.BackupDeviceText(0), StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────
    // ③ 待办
    // ─────────────────────────────────────────────

    [Fact]
    public void 清理预告的估算口径要写在句子里()
    {
        Assert.Equal(string.Empty, OverviewTexts.Cleanup(0, 999));

        var text = OverviewTexts.Cleanup(3, 5L * 1024 * 1024);

        // ⚠️「约」丢了是数字看着更准了；括号里那句丢了，用户会把第一次那个数
        // 当成准的，而清理前会再算一次、两次本来就对不上。
        Assert.Contains("3 条", text, StringComparison.Ordinal);
        Assert.Contains("约 5 MB", text, StringComparison.Ordinal);
        Assert.Contains("估的", text, StringComparison.Ordinal);
        Assert.Contains("清理前会再算一次", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 过期那一条必须说明不会被自动删()
    {
        Assert.Equal(string.Empty, OverviewTexts.Overdue(0));

        var text = OverviewTexts.Overdue(4);

        Assert.Contains("4 条", text, StringComparison.Ordinal);
        Assert.Contains("不会被自动删", text, StringComparison.Ordinal);
        Assert.Contains("提醒上传", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 启动警告全列出来_不只第一条()
    {
        Assert.Equal(string.Empty, OverviewTexts.Warnings([]));

        var text = OverviewTexts.Warnings(["没有摄像头，无法录制", "回放端口被占用"]);

        // ⚠️ 只写第一条的话，用户得去设置窗才知道后面还说了什么 ——
        // 而第一条恰恰可能是次要的那条。
        Assert.Contains("没有摄像头，无法录制", text, StringComparison.Ordinal);
        Assert.Contains("回放端口被占用", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split('\n').Length - 1);
    }

    [Fact]
    public void 待办卡出不出来_三样各自都能让它出来()
    {
        // ⚠️ 判据是三样**或**起来。写成「只看警告」的话，清理预告与过期提醒
        // 会**无声消失**（这两个恰恰是用户最需要看到的）。
        Assert.False(OverviewTexts.TodoVisible(string.Empty, 0, 0));

        Assert.True(OverviewTexts.TodoVisible("3 条 · 约 5 MB（估的，清理前会再算一次）", 0, 0));
        Assert.True(OverviewTexts.TodoVisible(string.Empty, 1, 0));
        Assert.True(OverviewTexts.TodoVisible(string.Empty, 0, 1));
    }

    // ─────────────────────────────────────────────
    // 统计时刻 / 算不完
    // ─────────────────────────────────────────────

    [Fact]
    public void 统计时刻与算不完那句()
    {
        var at = new DateTimeOffset(2026, 10, 7, 9, 5, 3, TimeSpan.FromHours(8));

        Assert.Equal("统计于 09:05:03", OverviewTexts.Updated(at));
        Assert.Equal("⚠️ 统计没算完：索引文件坏了", OverviewTexts.Failed("索引文件坏了"));
    }
}
