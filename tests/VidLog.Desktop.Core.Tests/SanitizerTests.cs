using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 写盘前的脱敏。
/// </summary>
/// <remarks>
/// ⚠️ 这些用例共用一份**进程级**的密钥登记表，所以每条用例都要先清干净 ——
/// 不然用例之间会互相影响，而那种失败最难查（单跑必过、一起跑才红）。
/// </remarks>
public class SanitizerTests : IDisposable
{
    private const string Secret = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";

    public SanitizerTests() => Sanitizer.ResetForTesting();

    public void Dispose() => Sanitizer.ResetForTesting();

    // ─────────────────────────────────────────────
    // 键名层
    // ─────────────────────────────────────────────

    [Fact]
    public void 密钥类的键名连值一起换掉()
    {
        Assert.Equal(SensitiveName.Redacted, Sanitizer.RedactionFor("credential"));
        Assert.Equal(SensitiveName.Redacted, Sanitizer.RedactionFor("令牌"));
    }

    [Fact]
    public void 普通键名不动()
    {
        Assert.Null(Sanitizer.RedactionFor("会话"));
        Assert.Null(Sanitizer.RedactionFor("停因"));
    }

    // ─────────────────────────────────────────────
    // 值层
    // ─────────────────────────────────────────────

    [Fact]
    public void 登记过的密钥值_无论出现在哪里都被抹掉()
    {
        // 这一层挡的是「键名很无辜」那种：`["内容"] = credential`。
        // 光看键名看不出来，而值认得出来。
        Sanitizer.RegisterSecret(Secret);

        var text = Sanitizer.Sanitize($"发请求用的凭据是 {Secret}，就这个");

        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.Contains(Sanitizer.Mask, text, StringComparison.Ordinal);
    }

    [Fact]
    public void 没登记的密钥值挡不住_这是已知的天花板()
    {
        // 诚实写出来：没登记过的、被变换过的，一律挡不住。
        // 正则解决不了「有人把不该记的东西记了」，这里只是把爆炸半径压小。
        var 另一个凭据 = "ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ";

        Assert.Contains(另一个凭据, Sanitizer.Sanitize(另一个凭据), StringComparison.Ordinal);
    }

    [Fact]
    public void Bearer_头被抹掉()
    {
        var text = Sanitizer.Sanitize("Authorization: Bearer abc123def456");

        Assert.DoesNotContain("abc123def456", text, StringComparison.Ordinal);
        Assert.Contains("Bearer " + Sanitizer.Mask, text, StringComparison.Ordinal);
    }

    [Fact]
    public void 太短的值不登记()
    {
        // 登记一个 "1" 之后，日志里每一个 1 都会变成 ***，等于把日志毁了。
        Sanitizer.RegisterSecret("1");
        Sanitizer.RegisterSecret("abc");

        Assert.Equal("1 abc", Sanitizer.Sanitize("1 abc"));
    }

    [Fact]
    public void 空值不登记也不抛()
    {
        Sanitizer.RegisterSecret(null);
        Sanitizer.RegisterSecret("");
        Sanitizer.RegisterSecret("   ");

        Assert.Equal("随便什么", Sanitizer.Sanitize("随便什么"));
    }

    [Fact]
    public void 一个密钥是另一个的子串时_先换长的()
    {
        // 先换短的会把长的换成 `***MNOP` —— 尾巴那半截照样泄露，
        // 而**看不出少了什么**，所以断言尾巴，不是断言前缀。
        Sanitizer.RegisterSecret("ABCDEFGHIJKL");
        Sanitizer.RegisterSecret("ABCDEFGHIJKLMNOP");

        var text = Sanitizer.Sanitize("值是 ABCDEFGHIJKLMNOP 完");

        Assert.DoesNotContain("MNOP", text, StringComparison.Ordinal);
        Assert.Equal("值是 *** 完", text);
    }

    [Fact]
    public void 没有登记任何密钥时_原样返回()
    {
        Assert.Equal("一行普通的日志", Sanitizer.Sanitize("一行普通的日志"));
    }
}
