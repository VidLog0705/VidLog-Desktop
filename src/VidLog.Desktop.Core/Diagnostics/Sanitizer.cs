using System.Text.RegularExpressions;

namespace VidLog.Desktop.Core.Diagnostics;

/// <summary>
/// 写盘前的脱敏。<b>就在日志写入这一条路上，不在导出的时候补。</b>
/// </summary>
/// <remarks>
/// <para>
/// 为什么必须在写入这一层：诊断包会把 <c>logs/*</c> **整个**打包外发
/// （<c>DiagnosticsPackage.AddLogsAsync</c>），而 <c>devices.jsonl</c> 不进包
/// **只是靠约定** —— 哪天有人手滑加一句目录遍历，盘上的明文就跟着出去了。
/// <b>写入即干净，只需要一个过滤器；导出时再过滤就是第二个。</b>
/// </para>
/// <para>
/// 三层，按硬度排：
/// </para>
/// <list type="number">
/// <item><b>结构层（最硬）</b>：<c>FileLogger.WriteValue</c> 只认标量，
/// 其余写 <c>&lt;对象:Xxx&gt;</c> 占位。反射式序列化会把 <c>EnrolledDevice</c>
/// 的凭据原样写出去，而这里根本不认识对象图。</item>
/// <item><b>键名层</b>：<see cref="SensitiveName.Is"/> —— 与配置变更留痕
/// 用的是同一份判据（<c>AGENTS.md</c> §6）。</item>
/// <item><b>值层（本类）</b>：登记过的密钥值，以及 <c>Authorization: Bearer …</c>。</item>
/// </list>
/// <para>
/// ⚠️ <b>天花板（诚实说清楚）</b>：值层只挡得住**登记过的**那一个。
/// 没登记过的新凭据、被变换过的（重新 base64、转大写）、
/// 以及第三方库异常消息里内嵌的密钥 —— <b>一律挡不住</b>。
/// 正则解决不了「有人把不该记的东西记了」这件事；这里做的是把爆炸半径压小，
/// 不是消灭它。所以**新生成密钥的那段代码有义务顺手登记一次**
/// （见 <see cref="RegisterSecret"/> 的调用点：<c>DeviceRegistry</c>）。
/// </para>
/// </remarks>
public static class Sanitizer
{
    /// <summary>替换成这个。与 <see cref="SensitiveName.Redacted"/> 不同是有意的：
    /// 那一个是「这个字段改名了」，这一个是「这里本来有个值、被抹掉了」。</summary>
    public const string Mask = "***";

    private static readonly Lock Gate = new();
    private static readonly HashSet<string> Secrets = new(StringComparer.Ordinal);

    /// <summary>短于这个长度的值不登记 —— 见 <see cref="RegisterSecret"/>。</summary>
    private const int MinSecretLength = 8;

    /// <summary>HTTP 头：`Bearer` 后面那一串。</summary>
    private static readonly Regex BearerPattern =
        new(@"\bBearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 登记一个「永远不该出现在日志里」的值。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么是进程级静态：密钥是**进程级的事实**（谁签发的都一样不该落盘），
    /// 而登记点分散在生成密钥的那些类里（<c>DeviceRegistry.ClaimAsync</c> 等）。
    /// 为它拉一条构造参数链，代价是十几个类各加一个参数 ——
    /// 而这个登记**不需要**知道日志写去哪。
    /// </para>
    /// <para>
    /// ⚠️ 太短的值**不登记**：登记一个 <c>"1"</c> 或 <c>"true"</c> 之后，
    /// 日志里每一个 `1` 都会被抹成 `***`，等于把日志毁了。
    /// 真实的凭据是 32 字节 base64url、令牌是 16 字节，都远长于此。
    /// </para>
    /// </remarks>
    public static void RegisterSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < MinSecretLength)
        {
            return;
        }

        lock (Gate)
        {
            Secrets.Add(value);
        }
    }

    /// <summary>清空登记（只给测试用 —— 静态状态会跨用例）。</summary>
    public static void ResetForTesting()
    {
        lock (Gate)
        {
            Secrets.Clear();
        }
    }

    /// <summary>把一串文本里登记过的密钥与 <c>Bearer</c> 头抹掉。</summary>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = BearerPattern.Replace(text, "Bearer " + Mask);

        string[] secrets;
        lock (Gate)
        {
            if (Secrets.Count == 0)
            {
                return result;
            }

            // 长的先换：短的是长的子串时，先换短的会把长的换得拼不上。
            secrets = [.. Secrets.OrderByDescending(s => s.Length)];
        }

        foreach (var secret in secrets)
        {
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// 一个 `data` 项要不要连值一起换掉。
    /// </summary>
    /// <returns>
    /// 键名命中 ⇒ 返回占位文本；否则返回 <see langword="null"/>，由调用方照常写值。
    /// </returns>
    public static string? RedactionFor(string key) =>
        SensitiveName.Is(key) ? SensitiveName.Redacted : null;
}
