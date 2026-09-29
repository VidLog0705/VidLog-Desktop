namespace VidLog.Desktop.Core.Diagnostics;

/// <summary>
/// 把 URL 里的 <c>账号:密码@</c> 抹掉。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>放在 <c>Diagnostics</c> 而不是某个业务类型上</b>：需要它的有两层 ——
/// 摄像头那一层（网络地址要进索引与界面）与外部进程那一层
/// （<c>SystemProcessRunner</c> 在失败时会把**整条 argv** 写进日志）。
/// 两处各写一份的话，迟早有一处漏掉，而漏掉的那一处正好是日志。
/// </para>
/// <para>
/// ⚠️ 它挡的是「**用户自己的**设备密码」（网络摄像头地址里的那种），
/// 与 <see cref="Sanitizer"/> 那套「我们自己的密钥」是两件事：
/// 那个靠**登记已知的值**，而这里靠**从形状上认出凭据**——
/// 前者要求有人记得登记，后者不要求。
/// </para>
/// <para>
/// ⚠️ <b>天花板</b>：它只认「`scheme://…@host`」这一种形状。
/// 凭据以别的方式出现（查询串里的 <c>?password=</c>、自定义头、路径里的令牌）
/// **一律挡不住** —— 那种得靠 <see cref="Sanitizer"/> 登记。
/// </para>
/// </remarks>
public static class UrlCredentials
{
    /// <summary>抹掉 <c>账号:密码@</c>；认不出来就原样返回。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>不用 <see cref="Uri"/></b>：它会对不合法的串抛异常，
    /// 而这里处理的是**用户手打的、随时可能是半截**的地址 ——
    /// 一个用来隐藏密码的函数因为「地址打错了」而抛出去，
    /// 调用方多半会连那半截地址一起写进日志。
    /// </para>
    /// <para>
    /// 只处理**主机之前**那一段里的 <c>@</c>：路径里出现 <c>@</c> 是完全可能的
    /// （<c>rtsp://host/path@v2</c>），把那个也当凭据分隔符会把路径切掉、
    /// 认成另一个地址。取**最后一个** <c>@</c> 与 RFC 一致
    /// （密码里带 <c>@</c> 时，最后一个才是主机之前那一刀）。
    /// </para>
    /// </remarks>
    public static string Strip(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return url;
        }

        var authorityStart = schemeEnd + 3;
        var slash = url.IndexOf('/', authorityStart);
        var authorityEnd = slash < 0 ? url.Length : slash;

        var authority = url[authorityStart..authorityEnd];
        var at = authority.LastIndexOf('@');
        if (at < 0)
        {
            return url;
        }

        return string.Concat(
            url.AsSpan(0, authorityStart),
            authority.AsSpan(at + 1),
            url.AsSpan(authorityEnd));
    }

    /// <summary>
    /// 把**一段文本里所有**带凭据的 URL 抹掉。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 有它是因为 2026-09-29 实测到的**第四条外泄路**：ffmpeg **自己**会把
    /// 带凭据的输入地址打在 stderr 上 ——
    /// </para>
    /// <code>
    /// Input #0, rtsp, from 'rtsp://admin:admin@192.168.101.55:8554/live':
    /// </code>
    /// <para>
    /// 而 <c>SystemProcessRunner</c> 在失败时**记的正是 stderr**（那条日志是排障的
    /// 主要依据，不能删）。所以只抹 argv 是不够的：凭据会从 stderr 那一路照样落盘，
    /// 而诊断包会把整个 <c>logs/</c> 打包外发。
    /// </para>
    /// <para>
    /// ⚠️ 一条都认不出来时**原样返回**（幂等）：正常的日志不该被这个函数改动。
    /// </para>
    /// </remarks>
    public static string StripIn(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return UrlPattern.Replace(text, match => Strip(match.Value));
    }

    /// <summary>
    /// 文本里「像 URL」的那些片段。
    /// </summary>
    /// <remarks>
    /// 停在空白与引号处：ffmpeg 那行的形状是 <c>from 'rtsp://…':</c>，
    /// 正好被单引号夹住。而 Windows 路径（<c>C:\…</c>）**没有</c> <c>://</c>，
    /// 所以不会被误伤。
    /// </remarks>
    private static readonly System.Text.RegularExpressions.Regex UrlPattern = new(
        "[A-Za-z][A-Za-z0-9+.-]*://[^\\s'\"]+",
        System.Text.RegularExpressions.RegexOptions.Compiled);
}
