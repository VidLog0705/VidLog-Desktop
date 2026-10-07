namespace VidLog.Desktop.Core.Web;

/// <summary>
/// 入网配对窗口里「现在发不出二维码」的原因（T27② 第 4 批）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>这些都是机器状态，不是用户做错了什么</b>，所以每条都必须给出
/// 「怎么才能好」。少一句的话，用户看到的是一张**扫不动的码** ——
/// 而他会以为是手机的问题，去折腾手机。
/// </para>
/// <para>
/// ⚠️ 发得出来时回 <see langword="null"/>（不是空串）：那是「没有拦住它的原因」
/// 与「有一句空话要说」的区别，调用处据此决定那一段显不显示。
/// </para>
/// <para>
/// ⚠️ 原先长在 <c>EnrollWindow</c> 里，而那个工程没有测试工程 ——
/// 四条分支里任何一条被改错（比如把「只绑到本机」与「服务没起来」说成一句）
/// 都没有东西能挡，而它们要用户做的事完全不同。
/// </para>
/// </remarks>
public static class EnrollBlockers
{
    /// <summary>发不出二维码的原因；发得出来返回 <see langword="null"/>。</summary>
    /// <param name="hasServer">回放服务装配起来了没有（端口设成空就是没起来）。</param>
    /// <param name="baseUrl">服务报出来的地址；没有时为 <see langword="null"/>。</param>
    /// <param name="usingFallback">是不是只绑到了本机。</param>
    /// <param name="fallbackReason">只绑本机的原因（给人看的一句话）。</param>
    /// <param name="port">回放端口（那句 <c>netsh</c> 命令里要用）。</param>
    /// <param name="hasAddress">挑到可用的局域网地址没有。</param>
    public static string? Blocker(
        bool hasServer,
        string? baseUrl,
        bool usingFallback,
        string? fallbackReason,
        int port,
        bool hasAddress)
    {
        if (!hasServer)
        {
            return "回放服务没有装配（端口设成了空）。手机连不上本机，先看主窗口里的提示。";
        }

        if (baseUrl is not { Length: > 0 })
        {
            return "回放服务没起来，手机连不上本机。原因见主窗口「需要注意」那一段。";
        }

        if (usingFallback)
        {
            return $"回放服务只绑到了本机（{baseUrl}），别的设备访问不了。"
                + $"原因：{fallbackReason} "
                + $"按主窗口里的提示，以管理员身份执行一次 netsh http add urlacl url=http://+:{port}/ user=Everyone 之后再试。";
        }

        if (!hasAddress)
        {
            return "没有挑到可用的局域网地址 —— 这台电脑现在可能没连在网络上（无线没连上，或者只插了虚拟网卡）。";
        }

        return null;
    }
}
