namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「这一条在**某些机器上必然跑不了**」的标记。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>它是给 CI 的守卫看的</b>：`ci.yml` 那一步会读 TRX 里被跳过测试的
/// <c>Skip</c> 原因，**带这个标记的才放行**，没有的一律当「意外跳过」报红。
/// </para>
/// <para>
/// ⚠️ <b>为什么用标记而不是「按类名列名单」</b>：名单要写在**两个地方**
/// （测试类上的特性 + `ci.yml` 的字符串），而它们必然会走岔 —— 2026-09-29
/// 一天之内就走岔了**两次**（都是「新加了一个用 <c>Requires*Fact</c> 的类，
/// 忘了改名单」），而表现都是「测试全过、CI 必红」，排查还挺绕。
/// 改成按原因判之后，**新增一条允许项只需要改测试自己那一处**。
/// </para>
/// <para>
/// ⚠️ <b>什么样的 Skip 才配打这个标记</b>：只在「这台机器**本来就没有**这个设备/
/// 服务」时用。**「本该有、但这台机器没配好」的不许打** ——
/// 最典型的是 <c>RequiresFfmpegFact</c>：CI 会装 FFmpeg，
/// 它一旦跳过就说明**环境配错了**，必须红。所以 FFmpeg 那一组**没有**这个标记。
/// </para>
/// </remarks>
internal static class SkipMarker
{
    /// <summary>拼在 Skip 原因最前面的标记。</summary>
    public const string Allowed = "[允许跳过]";

    /// <summary>把原因包上标记。</summary>
    public static string Allow(string reason) => Allowed + reason;
}
