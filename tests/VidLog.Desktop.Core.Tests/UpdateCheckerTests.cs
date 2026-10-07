using System.Text.Json;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Update;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「有没有新版本」这一问（批次 9，设计图 `_49`）。
/// </summary>
/// <remarks>
/// ⚠️ 这里**刻意没有联网的用例**：真去请求那一步是外面递进来的
/// （<c>Func&lt;CancellationToken, Task&lt;string?&gt;&gt;</c>），正是为了让
/// 「比大小」与「什么时候提示」这两件会出错的事不依赖网络就能测。
/// </remarks>
public class UpdateCheckerTests
{
    // ─────────────────────────────────────────────
    // 比大小：纯函数
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData("1.0.0", "2.0.0", true)]
    [InlineData("v1.2.9", "v1.2.10", true)]      // ⚠️ 按段比数字，不是按字符串
    [InlineData("v1.2.10", "v1.2.9", false)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0", "1.0.1", true)]           // 段数不一样，短的补 0
    [InlineData("1.0.1", "1.0", false)]
    [InlineData("1.0.0+abc123", "1.0.0", false)] // 构建元数据不参与比大小
    [InlineData("1.0.0", "1.0.0+abc123", false)]
    [InlineData("1.0.0", "1.0.1+abc123", true)]
    public void 比版本号(string current, string candidate, bool expected) =>
        Assert.Equal(expected, VersionTag.IsNewer(current, candidate));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("1.0.0-beta")]
    [InlineData("v")]
    [InlineData("1..0")]
    [InlineData("1.0.0.0.0.0.x")]
    public void 认不出来的一律不当成有新版本(string? candidate)
    {
        // ⚠️ 这里**不是**该宽松的地方：判错的后果是让用户去下载一个不存在
        // 或更旧的版本。宁可少提示一次 —— 少提示只是晚几天知道，
        // 提示错了是让人白跑一趟发布页。
        Assert.False(VersionTag.IsNewer("1.0.0", candidate));
    }

    // ─────────────────────────────────────────────
    // 问一次：问到 / 没问到 / 空仓库
    // ─────────────────────────────────────────────

    [Fact]
    public async Task 问到了新版本就说有新版本()
    {
        var checker = new UpdateChecker(_ => Task.FromResult<string?>("v2.0.0"));

        var result = await checker.CheckAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("v2.0.0", result.LatestTag);
        Assert.True(result.SuggestUpdate("1.0.0"));
    }

    [Fact]
    public async Task 对端版本号带空白也认()
    {
        // 对端是别的东西写的 JSON，多一个换行不值得让整次检查作废。
        var checker = new UpdateChecker(_ => Task.FromResult<string?>("  v2.0.0\n"));

        var result = await checker.CheckAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("v2.0.0", result.LatestTag);
    }

    [Fact]
    public async Task 一个版本都没发布过不算失败_也不提示()
    {
        // ⚠️ 「对端空着」与「没问到」是**两件事**：混起来的话，一个刚建好、
        // 还没发过版的仓库会让每台机器都显示「检查更新失败」，
        // 而失败与录制、回放、许可全都没有关系（I4 / L8）。
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(null));

        var result = await checker.CheckAsync();

        Assert.True(result.Succeeded);
        Assert.Null(result.LatestTag);
        Assert.False(result.SuggestUpdate("1.0.0"));
    }

    [Fact]
    public async Task 取版本号抛了也不许往外抛_只说没问到()
    {
        // ⚠️ 它跑在启动之后的第一时间，而且是 fire-and-forget 起的 ——
        // 抛出去就是一个未观测的 Task 异常。断网是最常见的情况，不是异常情况。
        var logger = new CapturingLogger();
        var checker = new UpdateChecker(
            _ => throw new System.Net.Http.HttpRequestException("断网了"), logger);

        var result = await checker.CheckAsync();

        Assert.False(result.Succeeded);
        Assert.False(result.SuggestUpdate("1.0.0"));
        Assert.Contains("断网了", result.FailureReason ?? string.Empty);

        // AGENTS.md §6.1：不留痕的功能不算做完。
        // 而这一条尤其需要痕：用户说「它从来不提示有新版本」时，
        // 日志是唯一分得清「网断了」与「已经是最新」的东西。
        Assert.NotEmpty(logger.Entries);
    }

    [Fact]
    public async Task 问到低版本时不说有新版本()
    {
        var checker = new UpdateChecker(_ => Task.FromResult<string?>("v0.9.0"));

        var result = await checker.CheckAsync();

        Assert.True(result.Succeeded);
        Assert.False(result.SuggestUpdate("1.0.0"));
    }

    // ─────────────────────────────────────────────
    // 读发布列表那一段 JSON（T27② 第 4 批）
    // ─────────────────────────────────────────────
    //
    // ⚠️ 这一段原来在 `App/Platform/ReleaseFeed.cs` 里，而那个工程没有测试工程。
    // 搬下来正是因为「`[]`」与「被限流那个对象」分不分得开**只有这里能挡**。

    [Fact]
    public void 一个版本都没发过时读到空()
    {
        // ⚠️ 与上面那条「不算失败」是一对：`[]` 是**真的没有发布过**。
        Assert.Null(ReleaseFeedJson.ReadLatestTag("[]"));
    }

    [Fact]
    public void 列表里第一个就是最新的那一个()
    {
        // 接口按发布时间倒序给，取第 0 个。
        Assert.Equal("v1.2.0", ReleaseFeedJson.ReadLatestTag(
            """[{"tag_name":"v1.2.0"},{"tag_name":"v1.1.9"}]"""));
    }

    [Fact]
    public void 那一段里没有tag_name时当成没问到()
    {
        Assert.Null(ReleaseFeedJson.ReadLatestTag("""[{"name":"发布 1.2"}]"""));
    }

    [Fact]
    public void 被限流的那个对象要抛_不许当成没发布过()
    {
        // ⚠️ 这一段里最要紧的一条。被限流时 GitHub 回的是**一个对象**
        // （未认证的接口每小时只给 60 次），若它和 `[]` 一样回 null，
        // 界面上显示的就是「已经是最新」—— 而真实情况是**根本没问到**。
        // 抛出去由 UpdateChecker 收成 Succeeded=false，那才是实话。
        Assert.Throws<FormatException>(() =>
        {
            _ = ReleaseFeedJson.ReadLatestTag("""{"message":"API rate limit exceeded"}""");
        });
    }

    [Fact]
    public void 根本不是json的也要抛()
    {
        // 代理返回一个 502 的 HTML 页时就是这样。
        // ⚠️ 用 `ThrowsAny`：`JsonDocument.Parse` 抛的是内部的
        // `JsonReaderException`，它是 `JsonException` 的**子类**，
        // 而 `Assert.Throws<T>` 要求类型**完全相符**。这里要断的是
        // 「按 `JsonException` 接得住」，不是「恰好是基类那一个」。
        Assert.ThrowsAny<JsonException>(() =>
        {
            _ = ReleaseFeedJson.ReadLatestTag("<html>502 Bad Gateway</html>");
        });
    }

    // ─────────────────────────────────────────────
    // 「关于」页那一行的结论（T27② 第 4 批）
    // ─────────────────────────────────────────────

    private static readonly UpdateCheckResult 查不到 =
        new(Succeeded: false, LatestTag: null, FailureReason: "断网了");

    [Fact]
    public void 开关关掉时不许说没查到()
    {
        // ⚠️ 说明「没查到」会让用户去查网络 —— 而它只是被自己关掉了。
        var text = UpdateStatusText.Describe(checkEnabled: false, 查不到, "1.0.0");

        Assert.Contains("已关闭", text);
        Assert.DoesNotContain("没查到", text);
    }

    [Fact]
    public void 还没查完就如实说还没查完()
    {
        Assert.Contains("还没查完", UpdateStatusText.Describe(true, status: null, "1.0.0"));
    }

    [Fact]
    public void 没问到不许说成已是最新()
    {
        // ⚠️ 这一段里最要紧的一条：一个网断了的工位会一直显示「已是最新」——
        // 而它其实**一次都没查过**。用户拿这句话决定要不要去发布页看看。
        var text = UpdateStatusText.Describe(true, 查不到, "1.0.0");

        Assert.Contains("没查到", text);
        Assert.Contains("断网了", text);
        Assert.DoesNotContain("已是最新", text);
        Assert.DoesNotContain("对端还没有发布过", text);
    }

    [Fact]
    public void 对端一个版本都没发过不算没查到()
    {
        // ⚠️ 与前一条正好相反的那一面：这里的 `Succeeded` 是 true，只是对端空着。
        // 说成「没查到」的话，一个刚建好、还没发过版的仓库会让每台机器都像网断了。
        var text = UpdateStatusText.Describe(true, new UpdateCheckResult(true, null, null), "1.0.0");

        Assert.Contains("对端还没有发布过任何版本", text);
        Assert.DoesNotContain("没查到", text);
    }

    [Fact]
    public void 有新版本要说清是哪一个以及在哪儿下()
    {
        var text = UpdateStatusText.Describe(
            checkEnabled: true, new UpdateCheckResult(true, "v2.0.0", null), "1.0.0");

        Assert.Contains("v2.0.0", text);
        Assert.Contains("1.0.0", text);
        Assert.Contains("发布页", text);
    }

    [Fact]
    public void 已经是最新就直说()
    {
        var text = UpdateStatusText.Describe(
            true, new UpdateCheckResult(true, "v1.0.0", null), "1.0.0");

        Assert.Contains("已是最新", text);
        Assert.Contains("v1.0.0", text);
    }

    private sealed class CapturingLogger : IAppLogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void Log(LogLevel level, string category, string message) =>
            Entries.Add((level, message));

        public void Log(
            LogLevel level, string category, string message,
            IReadOnlyDictionary<string, object?> data) =>
            Entries.Add((level, message));
    }
}
