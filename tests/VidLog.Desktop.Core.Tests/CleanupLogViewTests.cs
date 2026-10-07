using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Index;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 清理流水（T24）—— 规格 §6.2「禁止静默清理」的**后一半**：保留**可查的**清理记录。
/// </summary>
/// <remarks>
/// <para>
/// 前一半（清理前必须预告）早就在 <c>CleanupService.PreviewAsync</c> 里做完了；
/// 后一半的**记录**也从 T19 起就在写盘，缺的是「可查」—— 文件落在
/// <c>%LOCALAPPDATA%</c> 里，没有任何界面能打开它。于是
/// <c>CleanupAsk</c> 那句「明细见清理流水」曾经指向一个不存在的地方。
/// </para>
/// <para>
/// ⚠️ <b>这里守的是翻账</b>（时间格式 / 动作码翻人话 / 单号兜底 / 坏行计数），
/// 不是窗口。窗口在 App 层、**没有测试工程**，所以那三个判断题一个都不许留在那儿
/// —— 留在那儿就只能靠肉眼，而它们每一样写错了界面上都只是「看着有点怪」，
/// 不会有任何东西喊。
/// </para>
/// <para>
/// ⚠️ 时间那一组**刻意不写死本地时区**：这台机器与 CI 的 runner 时区不同，
/// 写死 +08:00 在 CI 上必红。构造时用 <c>new DateTimeOffset(DateTime)</c>
/// （Unspecified = 本地时区），于是断言在任何时区下都成立 ——
/// 而它仍然验到了「把 UTC 换算成本地时间」这件事（见那个跨时区的用例）。
/// </para>
/// </remarks>
public class CleanupLogViewTests
{
    // ─────────────────────────────────────────────
    // 时间
    // ─────────────────────────────────────────────

    [Fact]
    public void 审计里的时间翻成本地时刻()
    {
        // ⚠️ 这个 DateTime 是**本地**的（Kind=Unspecified 时 DateTimeOffset 按本地算），
        // 所以 15:04:05 在什么时区的机器上都该印成 15:04:05。
        var local = new DateTimeOffset(new DateTime(2026, 9, 27, 15, 4, 5));

        Assert.Equal("2026-09-27 15:04:05", CleanupLogView.DescribeAt(local.ToString("O")));
    }

    [Fact]
    public void 东八区写的十六点在北京看还是十六点()
    {
        // 审计里存的是带偏移的 ISO（`DateTimeOffset.UtcNow.ToString("O")` 是 +00:00），
        // 但别的机器 / 别的时区写进去的也可能是 +08:00。两者都该落到**本机**时刻上。
        var withOffset = new DateTimeOffset(2026, 9, 27, 16, 0, 0, TimeSpan.FromHours(8));
        var expected = withOffset.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

        Assert.Equal(expected, CleanupLogView.DescribeAt(withOffset.ToString("O")));
    }

    [Fact]
    public void 时间读不动就原样印出来_不许编也不许吞()
    {
        // ⚠️ 这一条是这本账的全部意义：宁可摆一串看不懂的东西，
        // 也不能让这一行凭空消失、或者显示一个编出来的时刻。
        Assert.Equal("（坏）", CleanupLogView.DescribeAt("（坏）"));
        Assert.Equal(string.Empty, CleanupLogView.DescribeAt(string.Empty));
    }

    // ─────────────────────────────────────────────
    // 动作
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData("deleting", "准备清理")]
    [InlineData("deleted", "已清理")]
    [InlineData("refused", "保留（没清）")]
    [InlineData("failed", "清理失败")]
    public void 动作码都有人话(string action, string text) =>
        Assert.Equal(text, CleanupLogView.DescribeAction(action));

    [Fact]
    public void 准备清理与已清理不是同一句话()
    {
        // ⚠️ 一次成功的清理留**两条**（意图 + 结果，T19）。两行写成同一个词的话，
        // 用户会以为同一条被删了两遍 —— 而这两条恰恰是「先写意图再动手」那个设计的证据。
        Assert.NotEqual(
            CleanupLogView.DescribeAction("deleting"),
            CleanupLogView.DescribeAction("deleted"));
    }

    [Fact]
    public void 认不出的动作原样印出来_不许吞()
    {
        // 将来加了第五种动作（或读到了手改过的文件），这一格必须是那个词本身，
        // 而不是空白 —— 空白在这本「不许静默」的账上就是一次静默。
        Assert.Equal("archived", CleanupLogView.DescribeAction("archived"));
        Assert.Equal(string.Empty, CleanupLogView.DescribeAction(string.Empty));
    }

    // ─────────────────────────────────────────────
    // 原因
    // ─────────────────────────────────────────────

    [Fact]
    public void 失败时把失败原因接在后面() =>
        Assert.Equal(
            "到期了 —— 文件被占用",
            CleanupLogView.DescribeReason(
                new CleanupAuditRecord("ev-1", "failed", "到期了", "文件被占用")));

    [Fact]
    public void 没有失败原因时就只有原因() =>
        Assert.Equal(
            "归档层上找不到这一份，按 I8 不删",
            CleanupLogView.DescribeReason(
                new CleanupAuditRecord("ev-1", "refused", "归档层上找不到这一份，按 I8 不删", null)));

    [Fact]
    public void 只有失败原因时不许印出一个空的原因格() =>
        Assert.Equal(
            "写不进去",
            CleanupLogView.DescribeReason(
                new CleanupAuditRecord("ev-1", "failed", string.Empty, "写不进去")));

    // ─────────────────────────────────────────────
    // 翻成一页
    // ─────────────────────────────────────────────

    [Fact]
    public void 最近的排在最上面()
    {
        var rows = CleanupLogView.Build(
            [
                new CleanupAuditRecord("ev-1", "deleted", "先发生的", null),
                new CleanupAuditRecord("ev-2", "deleted", "后发生的", null),
            ],
            []);

        Assert.Equal(2, rows.Count);
        Assert.Equal("后发生的", rows[0].ReasonText);
        Assert.Equal("先发生的", rows[1].ReasonText);
    }

    [Fact]
    public void 证据id换成用户认得的单号()
    {
        var rows = CleanupLogView.Build(
            [new CleanupAuditRecord("ev-1", "deleted", "到期", null)],
            [Entry("ev-1", "SF1000000001")]);

        Assert.Equal("SF1000000001", rows[0].WaybillText);
        // ⚠️ 原值也要留着 —— 查不到单号时它是唯一能对上号的东西。
        Assert.Equal("ev-1", rows[0].EvidenceId);
    }

    [Fact]
    public void 索引里查不到那一份时写无单号()
    {
        // 索引里没有它（比如索引文件被重建过）。这时**不能空着** ——
        // 空白看着像界面坏了。
        var rows = CleanupLogView.Build(
            [new CleanupAuditRecord("ev-9", "refused", "回查不通过", null)], []);

        Assert.Equal(CleanupLogView.NoWaybillText, rows[0].WaybillText);

        // ⚠️ 与局域网回放页那个 JS 兜底逐字一致 —— 两处两个说法的话，
        // 同一件事在两个界面上看起来是两件事。
        Assert.Equal("（无单号）", CleanupLogView.NoWaybillText);
    }

    [Fact]
    public void 索引里有重复的证据id也不许把窗口炸掉()
    {
        // ⚠️ 用 `Add` 建对照表的话这里会抛 —— 而那意味着**流水窗口整个打不开**，
        // 比「单号显示得不够准」严重得多。这一条钉的就是那个取舍。
        var rows = CleanupLogView.Build(
            [new CleanupAuditRecord("ev-1", "deleted", "到期", null)],
            [Entry("ev-1", "SF1000000001"), Entry("ev-1", "SF1000000002")]);

        Assert.Single(rows);
        Assert.False(string.IsNullOrWhiteSpace(rows[0].WaybillText));
    }

    // ─────────────────────────────────────────────
    // 坏行
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 读不动的行要被数出来_而不是悄悄跳过()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"vidlog-audit-{Guid.NewGuid():N}.jsonl");

        try
        {
            var audit = new CleanupAuditLog(path);

            await audit.AppendAsync(new CleanupAuditRecord("ev-1", "deleted", "到期", null));

            // 写一半就断电了的那一行。
            await File.AppendAllTextAsync(path, "{\"EvidenceId\":\"ev-2\",\"Act");

            var page = await audit.LoadPageAsync();

            Assert.Single(page.Records);
            Assert.Equal(1, page.UnreadableLines);

            // ⚠️ 老入口的行为**一个字没变**（半截的一行跳过，不能因为一行坏了
            // 就丢掉整份审计）—— 变的是它现在说得出自己跳了几行。
            Assert.Single(await audit.LoadAllAsync());

            Assert.Contains("1 行读不动", CleanupLogView.DescribeUnreadable(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task 审计文件还不存在时是一条空白流水()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"vidlog-audit-{Guid.NewGuid():N}.jsonl");

        var page = await new CleanupAuditLog(path).LoadPageAsync();

        Assert.Empty(page.Records);
        Assert.Equal(0, page.UnreadableLines);
    }

    // ─────────────────────────────────────────────
    // 验收：删一条录像 → 打开界面 → 看得见那条
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 删掉一条之后流水上看得见那一条()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"vidlog-audit-{Guid.NewGuid():N}.jsonl");

        try
        {
            var audit = new CleanupAuditLog(path);

            // ⚠️ 照 `CleanupExecutor` 真实的写入顺序：**先写意图、再写结果**（T19）。
            await audit.AppendAsync(new CleanupAuditRecord("ev-1", "deleting", "超过保留期", null));
            await audit.AppendAsync(new CleanupAuditRecord("ev-1", "deleted", "超过保留期", null));

            // 被拒的那一条也要看得见 —— 「哪几条没清、为什么」与「清了什么」同样重要。
            await audit.AppendAsync(new CleanupAuditRecord(
                "ev-2", "refused", "归档层上找不到这一份，按 I8 不删", null));

            var rows = CleanupLogView.Build(
                (await audit.LoadPageAsync()).Records,
                [Entry("ev-1", "SF1000000001"), Entry("ev-2", "SF1000000002")]);

            Assert.Equal(3, rows.Count);

            // 最新的一条（拒绝）在最上面。
            Assert.Equal("SF1000000002", rows[0].WaybillText);
            Assert.Equal("保留（没清）", rows[0].ActionText);
            Assert.Contains("I8", rows[0].ReasonText);

            // 删掉的那条：意图与结果两条都在，且**看得出先后**。
            Assert.Equal("SF1000000001", rows[1].WaybillText);
            Assert.Equal("已清理", rows[1].ActionText);
            Assert.Equal("SF1000000001", rows[2].WaybillText);
            Assert.Equal("准备清理", rows[2].ActionText);
            Assert.Equal("超过保留期", rows[1].ReasonText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ─────────────────────────────────────────────
    // 绊线：这条路真的接上了没有
    // ─────────────────────────────────────────────

    /// <summary>
    /// 「零件好、没人接」是本仓反复踩过的病（<c>CleanupServiceTests</c> 的注释里
    /// 记着同一条）。这一条钉的就是**那个入口真的存在**。
    /// </summary>
    [Fact]
    public void 清理流水在设置页上真的有出路()
    {
        var settings = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App", "SettingsWindow.xaml"));
        var handlers = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App", "SettingsWindow.Recording.cs"));
        var window = WindowSource();

        // ① 按钮在【按时间清理…】/【按空间释放…】那颗旁边（清理那一块里），
        //    且它的点击接的是我们那个处理器。
        Assert.Contains("x:Name=\"CleanupLogButton\"", settings);
        Assert.Contains("Click=\"OnCleanupLog\"", settings);

        // ② 处理器真的把那个窗口弹出来。
        Assert.Contains("void OnCleanupLog(", handlers);
        Assert.Contains("new CleanupLogWindow(_host)", handlers);

        // ③ 窗口读的是**带坏行计数**的那个入口。
        //    ⚠️ 用 `LoadAllAsync` 的话，坏掉的行会被**静默跳过** ——
        //    一个「不许静默」的窗口悄悄少显示几条，是这本账最不该有的错。
        Assert.Contains("CleanupAudit.LoadPageAsync(", window);
        Assert.DoesNotContain(".CleanupAudit.LoadAllAsync(", window);

        // ④ 绑定真的设了 ItemsSource（少这一句窗口会是永久空白，而且不报错）。
        Assert.Contains("LogList.ItemsSource = rows", window);

        // ⑤ `AGENTS.md` §6.1：读不出来那条 catch **不许只有界面上那句话** ——
        //    窗口一关它就没了，而「审计读不出来」是事后最该查的那一件事。
        //    （§6.1.1 说这条规矩三轮回溯都漏，根因是「没有能失败的检查」——
        //    这一条就是给它补的那个检查。）
        Assert.Contains("_host.Log(LogLevel.Warn", window);
    }

    private static RecordingEntry Entry(string id, string waybill) => new(
        id,
        "sess-1",
        WaybillNumber.Parse(waybill),
        new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(8)),
        new DateTimeOffset(2026, 9, 27, 12, 5, 0, TimeSpan.FromHours(8)),
        TimeSpan.FromMinutes(5),
        RelativePath.Parse($"2026/09/27/{waybill}/{id}.mp4"),
        ContentHash.Parse(new string('a', 64)),
        "device-1");

    /// <summary>清理流水窗口那几个源文件拼起来（去掉注释行）。</summary>
    private static string WindowSource() =>
        string.Join(
            '\n',
            Directory.EnumerateFiles(
                    Path.Combine(RepoRoot(), "src", "VidLog.Desktop.App"), "CleanupLogWindow.*")
                .OrderBy(one => one, StringComparer.Ordinal)
                .SelectMany(one => File.ReadAllLines(one)
                    .Where(line => !line.TrimStart().StartsWith("//"))));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 往上找不到仓库根（含 src 与 tests 的目录）。");
    }
}
