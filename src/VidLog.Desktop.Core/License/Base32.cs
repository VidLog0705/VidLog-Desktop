namespace VidLog.Desktop.Core.License;

/// <summary>
/// Base32（RFC 4648 字母表，**不带填充**）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么不是 Base64</b>：这段码用户要**念给卖家听 / 手抄 / 在电话里对**，
/// 而 Base64 区分大小写、还带 <c>+/=</c> 这些在电话里说不清的字符。
/// Base32 只有 <c>A-Z2-7</c>，大写、无歧义（这也是 TOTP 用它的理由）。
/// </para>
/// <para>
/// ⚠️ <b>字母表里没有 <c>0</c> / <c>1</c> / <c>8</c> / <c>9</c></b> ——
/// 数字只有 <c>2-7</c>。抄错时最常被误认的三个字母是 <c>O</c> / <c>I</c> / <c>B</c>，
/// 而它们都在表里 —— 所以解析时**不做**「把 O 当成 0」那类替换
/// （表里没有 0，替换只会把合法的 O 改坏）。
/// </para>
/// </remarks>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>编码。5 字节进 → 8 字符出。</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                builder.Append(Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 解码。**认不出的字符一律返回 null**（不猜、不跳过）——
    /// 跳过的话，一个抄错字符的码会被解成另一串字节，而错误信息会变成
    /// 「此激活码不适用于本机」（把用户引向错误的方向）。
    /// </summary>
    public static byte[]? TryDecode(string? text, out string? failureReason)
    {
        failureReason = null;

        if (string.IsNullOrEmpty(text))
        {
            failureReason = "激活码是空的";
            return null;
        }

        var bytes = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;

        foreach (var c in text)
        {
            var index = Alphabet.IndexOf(char.ToUpperInvariant(c));

            if (index < 0)
            {
                failureReason = $"激活码里有一个认不出的字符「{c}」—— Base32 只用 A-Z 和 2-7";
                return null;
            }

            buffer = (buffer << 5) | index;
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
