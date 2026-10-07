using System.Text.Json;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Update;

/// <summary>
/// 版本号的比较 —— **纯函数，不许抛**。
/// </summary>
/// <remarks>
/// <para>
/// 比的是「<c>v1.2.10</c> 比 <c>v1.2.9</c> 新吗」这种问题。
/// ⚠️ <b>按段比数字，不是按字符串比</b>：字符串比的话 <c>1.2.10</c> 会**小于**
/// <c>1.2.9</c>（因为 <c>'1' &lt; '9'</c>），于是「有新版本」这条提示
/// 会在真正该提示的那一次**恰好不出现** —— 而且它永远不会报错。
/// </para>
/// <para>
/// ⚠️ 认不出来的输入一律返回 <see langword="false"/>（=「不比当前新」）。
/// 这里不是「宽松一点」的地方：判错了的后果是让用户去下载一个不存在或更旧的版本。
/// 宁可少提示一次。
/// </para>
/// </remarks>
public static class VersionTag
{
    /// <summary><paramref name="candidate"/> 比 <paramref name="current"/> 新吗。</summary>
    public static bool IsNewer(string? current, string? candidate)
    {
        var left = Parse(current);
        var right = Parse(candidate);

        if (left is null || right is null)
        {
            return false;
        }

        for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            var a = i < left.Count ? left[i] : 0;
            var b = i < right.Count ? right[i] : 0;

            if (b != a)
            {
                return b > a;
            }
        }

        return false;
    }

    /// <summary>
    /// 把 <c>v1.2.3</c> / <c>1.2.3</c> / <c>1.2.3+abcdef</c> 拆成段；认不出给 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>+</c> 后面是**构建元数据**（本仓的 `AssemblyInformationalVersion`
    /// 可能是 <c>1.0.0+&lt;sha&gt;</c>），它不参与比大小 —— 不切掉的话
    /// 那一段会被当成「认不出来」，于是**每个版本都提示"已是最新"**。
    /// </remarks>
    private static List<int>? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var segments = text.Split('.');
        var result = new List<int>(segments.Length);

        foreach (var segment in segments)
        {
            // 只要纯数字段：`1.0.0-beta` 这种预发布号本仓不产出，
            // 与其猜它的语义，不如说「认不出来」（见类注释）。
            if (segment.Length == 0 || !segment.All(char.IsAsciiDigit))
            {
                return null;
            }

            if (!int.TryParse(segment, out var number))
            {
                return null;
            }

            result.Add(number);
        }

        return result.Count == 0 ? null : result;
    }
}

/// <summary>一次检查更新的结论。</summary>
/// <param name="Succeeded">问到了没有（网络不通 / 限流都算没问到）。</param>
/// <param name="LatestTag">对端最新的版本号；还没有发布过任何版本时为 <see langword="null"/>。</param>
/// <param name="FailureReason">没问到时的原因（给人看的一句话）。</param>
public sealed record UpdateCheckResult(bool Succeeded, string? LatestTag, string? FailureReason)
{
    /// <summary>该不该提示用户。**只有"问到了、而且比当前新"才提示**。</summary>
    /// <remarks>
    /// ⚠️ 「没问到」**不是**「有新版本」，也不是「出错了要吓唬用户」——
    /// 它什么都不提示。这台机器十年不联网也该照常录像与回放（I4 / L8）。
    /// </remarks>
    public bool SuggestUpdate(string currentVersion) =>
        Succeeded && VersionTag.IsNewer(currentVersion, LatestTag);
}

/// <summary>
/// 「有没有新版本」这一问。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>只做检查 + 提示</b>：需求方 2026-09-30 裁决原话「只做检查 + 提示」
/// （不做启动器 exe、不自替换）。所以这里**不下载任何东西**、不写任何文件，
/// 返回值只够界面说一句话。
/// </para>
/// <para>
/// ⚠️ <b>取版本号的动作由外面递进来</b>（<c>Func&lt;CancellationToken, Task&lt;string?&gt;&gt;</c>）：
/// 这样「比大小」与「什么时候提示」这两件会出错的事**不依赖网络就能测**，
/// 而真去请求那一步在 App 层（它才知道要走哪个代理、用哪个 User-Agent）。
/// </para>
/// <para>
/// ⚠️ <b>绝不许抛、绝不许挡启动</b>：任何异常都收成
/// <see cref="UpdateCheckResult.Succeeded"/> = <see langword="false"/>。
/// 检查更新失败与录制、回放、许可**全都没有关系**。
/// </para>
/// </remarks>
public sealed class UpdateChecker
{
    private readonly Func<CancellationToken, Task<string?>> _fetchLatestTag;
    private readonly IAppLogger _logger;

    /// <param name="fetchLatestTag">
    /// 去问对端最新的版本号。**还没有发布过任何版本时返回 <see langword="null"/>**
    /// （那不是失败）。网络不通时**抛** —— 这里会收成「没问到」。
    /// </param>
    /// <param name="logger">留痕（`AGENTS.md` §6.1）。</param>
    public UpdateChecker(Func<CancellationToken, Task<string?>> fetchLatestTag, IAppLogger? logger = null)
    {
        _fetchLatestTag = fetchLatestTag;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>问一次。<b>永远返回一个结果，永远不抛。</b></summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tag = await _fetchLatestTag(cancellationToken);

            if (string.IsNullOrWhiteSpace(tag))
            {
                // 这不是失败：仓库还一个版本都没发过。留痕是为了让「为什么没提示」
                // 有答案 —— 否则它与「网断了」在日志上看不出区别。
                _logger.Log(LogLevel.Info, "更新检查", "对端还没有发布过任何版本。");
                return new UpdateCheckResult(true, null, null);
            }

            _logger.Log(LogLevel.Info, "更新检查", $"对端最新版本是 {tag.Trim()}。");
            return new UpdateCheckResult(true, tag.Trim(), null);
        }
        catch (Exception ex)
        {
            // 断网、代理没开、被限流、对端改了接口 —— 一律「没问到」。
            // ⚠️ 这里**必须**吞掉：它跑在启动之后的第一时间，
            // 让一个可选功能把主流程带下去是本末倒置。
            _logger.Log(LogLevel.Warn, "更新检查", $"检查更新失败（不影响使用）：{ex.Message}");
            return new UpdateCheckResult(false, null, ex.Message);
        }
    }
}

/// <summary>
/// GitHub「最新发布」那一段响应的读法（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>「对端一个版本都没发过」与「没问到」必须分得开</b>：前者是**空数组**
/// （正常状态，永远可能是这样），被限流时对端给的是一个**对象**
/// <c>{"message":"API rate limit exceeded…"}</c>。
/// </para>
/// <para>
/// ⚠️ 所以结构认不出来时**抛**（<see cref="UpdateChecker.CheckAsync"/> 会收成
/// 「没问到」），只有「确实是空数组」与「有元素但没有 <c>tag_name</c>」才回
/// <see langword="null"/>。两个都当成 <see langword="null"/> 的话，
/// 一次被限流的请求会变成界面上那句「对端还没有发布过任何版本」——
/// 而它的意思是**对端的问题**，与「我们没问到」正好相反。
/// </para>
/// <para>
/// ⚠️ 只读 <c>tag_name</c>，**不建一个与对端字段一一对应的 DTO**：那等于把
/// GitHub 的响应结构变成我们的编译期契约，对端加一个字段我们就得跟着改。
/// </para>
/// </remarks>
public static class ReleaseFeedJson
{
    /// <summary>最新一个发布的 tag；一个都没发过时给 <see langword="null"/>。</summary>
    /// <exception cref="JsonException">这段文本不是 JSON。</exception>
    /// <exception cref="FormatException">是 JSON，但根不是一个数组（多半是限流/报错）。</exception>
    public static string? ReadLatestTag(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException(
                $"发布列表的根是 {root.ValueKind}，不是一个数组 —— 多半是被限流或者报错了。");
        }

        if (root.GetArrayLength() == 0)
        {
            return null;
        }

        return root[0].TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
    }
}
