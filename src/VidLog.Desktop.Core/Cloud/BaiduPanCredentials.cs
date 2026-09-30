namespace VidLog.Desktop.Core.Cloud;

/// <summary>
/// 百度网盘开放平台的**应用凭据**（AppKey / AppSecret / SignKey）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>只从环境变量读，绝不进配置文件、绝不进仓库。</b>
/// 这不是洁癖：配置文件（<c>settings.json</c>）会被用户复制到别的机器、
/// 会被贴进工单、会被备份工具扫走，而 AppSecret 一旦公开，
/// 任何人都能拿着它冒充这个应用去调网盘接口。
/// </para>
/// <para>
/// ⚠️ <b>本类绝不把凭据的值写进日志或异常消息。</b>
/// 要回答「配没配好」有 <see cref="Describe"/>，它只说哪个变量空着。
/// 「有没有配」与「配的是什么」是两件事，后者不该出现在任何留痕里。
/// </para>
/// <para>
/// 签名（<c>SignKey</c>）只在调**需要签名的接口**时才用得上。网盘的上传那一族
/// 只要 <c>access_token</c>，所以它是可选的 —— 缺了不妨碍上传，
/// 只影响将来可能用到签名的接口。
/// </para>
/// </remarks>
public sealed record BaiduPanCredentials(string AppKey, string AppSecret, string? SignKey)
{
    /// <summary>应用 AppKey 的环境变量名。</summary>
    public const string AppKeyVariable = "VIDLOG_BAIDU_APP_KEY";

    /// <summary>应用 AppSecret 的环境变量名。</summary>
    public const string AppSecretVariable = "VIDLOG_BAIDU_APP_SECRET";

    /// <summary>签名密钥的环境变量名（可选）。</summary>
    public const string SignKeyVariable = "VIDLOG_BAIDU_SIGN_KEY";

    /// <summary>读本机环境变量。缺 AppKey 或 AppSecret 时返回 <see langword="null"/>。</summary>
    public static BaiduPanCredentials? FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>
    /// 从给定的读取函数里取值（测试用得到 —— 直接改进程环境变量会串到别的用例）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 值两侧的空白一律去掉：从命令行 <c>set</c> 进来的值经常带一个尾空格
    /// （<c>set VIDLOG_BAIDU_APP_KEY=abc </c>），而那个空格会被原样拼进 OAuth 请求，
    /// 表现是**授权一直说「应用不存在」** —— 查起来要命。
    /// </remarks>
    public static BaiduPanCredentials? FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var key = read(AppKeyVariable)?.Trim();
        var secret = read(AppSecretVariable)?.Trim();

        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
        {
            return null;
        }

        var sign = read(SignKeyVariable)?.Trim();

        return new BaiduPanCredentials(key, secret, string.IsNullOrEmpty(sign) ? null : sign);
    }

    /// <summary>
    /// 「去哪配」那句话说给用户听。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只报**变量名**，不报值、也不报「值看着对不对」。
    /// 界面上要能一眼看出「是没配，还是配错了」：没配就照这句话去配。
    /// </remarks>
    public static string MissingMessage { get; } =
        $"没找到百度网盘的应用凭据。请设置环境变量 {AppKeyVariable} 与 {AppSecretVariable} "
        + "（在百度网盘开放平台后台建应用时拿到），然后重启本程序。"
        + "凭据不会写进配置文件。";

    /// <summary>
    /// 给人看的状态（**不含任何凭据值，连长度都不含**）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 长度也算关于值的信息 —— 而它换不来任何用户能做的事
    /// （「配了 12 位」既不能帮他判断配对了没有，也不能帮他修）。
    /// 所以这里只说「配了没有」，一个字都不多说。这一句会进诊断包。
    /// </remarks>
    public string Describe() =>
        $"AppKey 已配置，AppSecret 已配置，"
        + $"SignKey {(SignKey is null ? "没配（可选）" : "已配置")}";
}
