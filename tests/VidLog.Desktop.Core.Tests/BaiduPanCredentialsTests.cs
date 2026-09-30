using VidLog.Desktop.Core.Cloud;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 百度网盘的应用凭据：**只从环境变量读，而且一个字都不许漏出去**（批次 5）。
/// </summary>
/// <remarks>
/// 这一条不是洁癖：设置文件会被用户复制到别的机器、会被贴进工单、会被备份工具扫走。
/// 而这个类的两个「给人看」的出口（<see cref="BaiduPanCredentials.Describe"/> 与
/// <see cref="BaiduPanCredentials.MissingMessage"/>）是**会被打进诊断包**的，
/// 所以下面钉的是「它们不含凭据值」，不是「它们好看」。
/// </remarks>
public class BaiduPanCredentialsTests
{
    private const string Key = "AK-abcdef123456";
    private const string Secret = "SK-supersecret-should-never-appear";
    private const string Sign = "SIGN-also-secret";

    private static Func<string, string?> Reader(params (string Name, string? Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void 缺任一个必需的都不算配好()
    {
        Assert.Null(BaiduPanCredentials.FromEnvironment(Reader()));
        Assert.Null(BaiduPanCredentials.FromEnvironment(
            Reader((BaiduPanCredentials.AppKeyVariable, Key))));
        Assert.Null(BaiduPanCredentials.FromEnvironment(
            Reader((BaiduPanCredentials.AppSecretVariable, Secret))));

        // 空串与纯空白都算**没配**（`set X=` 会留下这种情况）。
        Assert.Null(BaiduPanCredentials.FromEnvironment(
            Reader(
                (BaiduPanCredentials.AppKeyVariable, "   "),
                (BaiduPanCredentials.AppSecretVariable, Secret))));
    }

    [Fact]
    public void 值两侧的空白一律去掉()
    {
        // ⚠️ 命令行 `set VIDLOG_BAIDU_APP_KEY=abc ` 会带一个尾空格进来，
        // 而那个空格会被原样拼进 OAuth 请求 —— 表现是**授权一直说「应用不存在」**。
        var credentials = BaiduPanCredentials.FromEnvironment(Reader(
            (BaiduPanCredentials.AppKeyVariable, "  " + Key + "  "),
            (BaiduPanCredentials.AppSecretVariable, "\t" + Secret + "\n")));

        Assert.NotNull(credentials);
        Assert.Equal(Key, credentials.AppKey);
        Assert.Equal(Secret, credentials.AppSecret);
    }

    [Fact]
    public void 签名密钥是可选的()
    {
        var without = BaiduPanCredentials.FromEnvironment(Reader(
            (BaiduPanCredentials.AppKeyVariable, Key),
            (BaiduPanCredentials.AppSecretVariable, Secret)));

        Assert.NotNull(without);
        Assert.Null(without.SignKey);

        var with = BaiduPanCredentials.FromEnvironment(Reader(
            (BaiduPanCredentials.AppKeyVariable, Key),
            (BaiduPanCredentials.AppSecretVariable, Secret),
            (BaiduPanCredentials.SignKeyVariable, Sign)));

        Assert.NotNull(with);
        Assert.Equal(Sign, with.SignKey);
    }

    [Fact]
    public void 给人看的状态里一个凭据值都没有()
    {
        // ⚠️ 这一句会进诊断包、会被贴进工单 —— 里面出现 AppSecret 就等于公开它。
        var credentials = BaiduPanCredentials.FromEnvironment(Reader(
            (BaiduPanCredentials.AppKeyVariable, Key),
            (BaiduPanCredentials.AppSecretVariable, Secret),
            (BaiduPanCredentials.SignKeyVariable, Sign)))!;

        var text = credentials.Describe();

        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain(Sign, text);
        Assert.DoesNotContain(Key, text);

        // ⚠️ 连长度都不说：长度也是关于那个值的信息，而它换不来任何用户能做的事。
        Assert.DoesNotContain(credentials.AppKey.Length.ToString(), text);

        // 但「配了没有」必须说 —— 这一句的用处就这一个。
        Assert.Contains("AppKey 已配置", text);
        Assert.Contains("AppSecret 已配置", text);
    }

    [Fact]
    public void 缺凭据那句话说的是去哪儿配而不是配错了()
    {
        var message = BaiduPanCredentials.MissingMessage;

        Assert.Contains(BaiduPanCredentials.AppSecretVariable, message);
        Assert.Contains("环境变量", message);

        // 「不写进配置文件」这句要在：用户第一个念头就是把凭据填进设置页。
        Assert.Contains("不会写进配置文件", message);
    }
}
