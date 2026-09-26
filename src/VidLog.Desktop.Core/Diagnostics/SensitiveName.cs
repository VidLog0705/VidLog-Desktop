namespace VidLog.Desktop.Core.Diagnostics;

/// <summary>
/// 「这个字段名是不是密钥类」—— <b>全仓唯一一处判定</b>。
/// </summary>
/// <remarks>
/// <para>
/// 判据来自 <c>AGENTS.md</c> §6：配置变更的差量要记录，但<b>密钥类字段只记「已修改」，不记值</b>。
/// 原来这个谓词是 <c>AppSettings</c> 的私有方法（只管配置变更那一处）；
/// 日志落盘前也要按同一套判据脱敏，所以把它提出来共用 ——
/// <b>两份判据迟早会走岔</b>，而走岔的表现是「配置变更那里挡住的，
/// 日志这边漏了出去」。
/// </para>
/// <para>
/// ⚠️ <b>它挡得住什么、挡不住什么</b>：这里只看<b>名字</b>。
/// 一个叫 <c>hostAddress</c> 的键里塞了凭据，照样会落盘 ——
/// 那一层由 <c>Sanitizer</c> 的「按已知密钥值擦除」兜。
/// 反过来，名字命中也一样会误伤（<c>monkey</c> 里含 <c>key</c>），
/// 所以它只用于**把值换成「（已修改）」**，不做别的。
/// </para>
/// <para>
/// 中英双语：这个项目里 <c>data</c> 的键名是中文（<c>["会话"]</c>、<c>["原始"]</c>），
/// 而设置项的字段名是英文（<c>nameof(AppSettings.Mode)</c>）。两套都要挡。
/// </para>
/// </remarks>
public static class SensitiveName
{
    /// <summary>命中密钥类字段名时写进日志/变更记录的占位文本（<c>AGENTS.md</c> §6 的原话）。</summary>
    public const string Redacted = "（已修改）";

    private static readonly string[] Patterns =
    [
        "secret",
        "token",
        "password",
        "passwd",
        "key",
        "credential",
        // HTTP 头里的那个名字。它与上面几个不重叠，但同样绝不能带值落盘。
        "authorization",
        // 界面与实际 data 键名用的中文。少了这几条，中文键名那一半等于没挡。
        "凭据",
        "令牌",
        "密码",
        "口令",
        "密钥",
        "授权",
    ];

    /// <summary>这个名字要不要脱敏。</summary>
    public static bool Is(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var pattern in Patterns)
        {
            if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
