using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VidLog.Desktop.Core.License;

/// <summary>激活码的载荷（`docs/04-许可设计.md` §3.1）。</summary>
/// <remarks>
/// <para>
/// 字节布局（**逐字节钉死，别凭记忆改**）：
/// </para>
/// <list type="table">
/// <item><term>0</term><description>版本</description></item>
/// <item><term>1</term><description>机位数</description></item>
/// <item><term>2</term><description>最少匹配段数</description></item>
/// <item><term>3–5</term><description>签发日（小端 3 字节）</description></item>
/// <item><term>6–9</term><description>销售序号（小端 4 字节）</description></item>
/// <item><term>10–24</term><description>三段短哈希（各 5 字节）</description></item>
/// </list>
/// <para>
/// ⚠️ <b>短哈希从第 10 字节起，不是第 9</b>。第 9 字节是序号那个 4 字节字段的最后一字节 ——
/// 写成第 9 起的话两段会**重叠**：短哈希的首字节会把序号的高字节盖掉。
/// </para>
/// <para>
/// 这个坑真的发生过（2026-09-27）：写的时候是 <c>9 + i * 5</c>，读的时候也是
/// <c>9 + i * 5</c>，于是**自洽地错着** —— 码能验过、机器码能匹配，
/// 只有序号悄悄变成垃圾（77 读成 0xF600004D）。发现它的是**签发工具那边的固定测试向量**
/// （两个仓各写一份格式，错法不一样才暴露得出来）。
/// </para>
/// </remarks>
/// <param name="Version">格式版本，当前 1。</param>
/// <param name="Slots">机位数（2/4/6/8）。</param>
/// <param name="MinMatch">最少匹配段数（当前 3）。</param>
/// <param name="IssuedDay">签发日 = 自 2020-01-01 起的天数。</param>
/// <param name="Serial">递增销售序号，售后追溯用。</param>
/// <param name="Segments">三段机器码短哈希（各 5 字节）。</param>
public sealed record LicensePayload(
    byte Version,
    byte Slots,
    byte MinMatch,
    int IssuedDay,
    uint Serial,
    IReadOnlyList<byte[]> Segments)
{
    /// <summary>载荷长度：1 + 1 + 1 + 3 + 4 + 15。</summary>
    public const int Size = 25;

    /// <summary>本版支持的格式版本。</summary>
    public const byte CurrentVersion = 1;

    /// <summary>合法的机位数。</summary>
    public static readonly IReadOnlyList<int> ValidSlots = [2, 4, 6, 8];

    /// <summary>签发日的基准日。</summary>
    public static readonly DateOnly Epoch = new(2020, 1, 1);

    /// <summary>序列化成 25 字节。</summary>
    public byte[] ToBytes()
    {
        var buffer = new byte[Size];

        buffer[0] = Version;
        buffer[1] = Slots;
        buffer[2] = MinMatch;

        // 3 字节小端
        buffer[3] = (byte)(IssuedDay & 0xFF);
        buffer[4] = (byte)((IssuedDay >> 8) & 0xFF);
        buffer[5] = (byte)((IssuedDay >> 16) & 0xFF);

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(6, 4), Serial);

        // ⚠️ 第 10 字节起（见类注释里的字节表）—— 第 9 字节是序号的一部分。
        for (var i = 0; i < 3; i++)
        {
            Segments[i].CopyTo(buffer, 10 + i * 5);
        }

        return buffer;
    }

    /// <summary>从 25 字节读回来。</summary>
    public static LicensePayload FromBytes(ReadOnlySpan<byte> bytes)
    {
        var segments = new List<byte[]>(3);

        for (var i = 0; i < 3; i++)
        {
            segments.Add(bytes.Slice(10 + i * 5, 5).ToArray());
        }

        return new LicensePayload(
            bytes[0],
            bytes[1],
            bytes[2],
            bytes[3] | (bytes[4] << 8) | (bytes[5] << 16),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(6, 4)),
            segments);
    }

    /// <summary>签发日。</summary>
    public DateOnly Issued => Epoch.AddDays(IssuedDay);
}

/// <summary>一次验签的结果。</summary>
/// <param name="Ok">通过没有。</param>
/// <param name="FailureReason">没通过的原因（**给用户看的那句话**，按 §4.1 的顺序给）。</param>
/// <param name="Payload">通过时的载荷。</param>
public sealed record LicenseCheck(bool Ok, string? FailureReason, LicensePayload? Payload)
{
    public static LicenseCheck Failed(string reason) => new(false, reason, null);

    public static LicenseCheck Passed(LicensePayload payload) => new(true, null, payload);
}

/// <summary>
/// 激活码的验签与校验（`docs/04-许可设计.md` §3 / §4.1）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>校验顺序严格按 §4.1</b>，而且**先验签再比对机器码**：
/// 反过来的话，攻击者能用一个畸形码**探测**机器码匹配逻辑。
/// </para>
/// <para>
/// ⚠️ <b>客户端只含公钥</b>（L2）—— 它只能验，不能签。
/// 私钥在**独立的签发工具**里，绝不进任何一个仓库（L1）。
/// </para>
/// </remarks>
public sealed class LicenseVerifier
{
    /// <summary>码的正文长度：25（载荷）+ 64（签名）。</summary>
    /// <remarks>
    /// ⚠️ 设计文档 §4.1 第 ② 步写的是「必须 86 字节」—— 那个数**是错的**
    /// （25 + 64 = 89）。按算式落地，并顺手改文档：Ed25519 与 ECDSA P-256
    /// 的签名都是 64 字节，换算法**不影响长度**，所以那个 86 不来自换算法，
    /// 是当初写文档时算错了。
    /// </remarks>
    public const int CodeSize = LicensePayload.Size + 64;

    /// <summary>显示用的前缀（用户一眼认出这是 VidLog 的码）。</summary>
    public const string DisplayPrefix = "VLG";

    private readonly ECDsa? _publicKey;

    public LicenseVerifier(ECDsa publicKey)
    {
        _publicKey = publicKey;
    }

    /// <summary>
    /// 用**内置的销售方公钥**构造（生产路径）。
    /// </summary>
    /// <returns>公钥还没配好时返回 <see langword="null"/> —— 那时激活一律失败，
    /// 而失败原因是「这台电脑里的软件没配好公钥」，不是「你的码不对」。</returns>
    public static LicenseVerifier? FromEmbeddedKey(string embeddedPublicKey)
    {
        if (string.IsNullOrWhiteSpace(embeddedPublicKey))
        {
            return null;
        }

        try
        {
            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(embeddedPublicKey), out _);

            return new LicenseVerifier(key);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// 校验一个激活码。
    /// </summary>
    /// <param name="code">用户粘进来的原文（带前缀、横线、空格都行）。</param>
    /// <param name="identity">本机的机器标识。</param>
    public LicenseCheck Verify(string code, MachineIdentity identity)
    {
        // ① 规范化 + Base32 解码
        var compact = MachineCode.Compact(StripPrefix(code));
        var bytes = Base32.TryDecode(compact, out var decodeError);

        if (bytes is null)
        {
            return LicenseCheck.Failed($"激活码格式不正确（{decodeError}）。");
        }

        // ② 长度检查
        if (bytes.Length != CodeSize)
        {
            return LicenseCheck.Failed(
                $"激活码格式不正确（长度是 {bytes.Length} 字节，应当是 {CodeSize}）。");
        }

        // ③ 拆出载荷与签名
        var payloadBytes = bytes.AsSpan(0, LicensePayload.Size);
        var signature = bytes.AsSpan(LicensePayload.Size);

        if (_publicKey is null)
        {
            // 软件没配好公钥（部署时漏了环境变量）—— 这不是用户激活码的问题，
            // 所以那句话要说准（不然用户会一直去问卖家要新码）。
            return LicenseCheck.Failed("本机软件没配好许可公钥，没法校验激活码。请重新安装或联系提供方。");
        }

        // ④ 验签 —— **必须在比对机器码之前**
        var ok = _publicKey.VerifyData(
            payloadBytes,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        if (!ok)
        {
            // ⚠️ 篡改会在这里被挡下（改机位数、改机器码段、换一台机器的码…）。
            return LicenseCheck.Failed("激活码无效。");
        }

        var payload = LicensePayload.FromBytes(payloadBytes);

        // ⑤ 版本
        if (payload.Version != LicensePayload.CurrentVersion)
        {
            return LicenseCheck.Failed("激活码版本不支持，请升级软件。");
        }

        // ⑥ 机位数合法性
        if (!LicensePayload.ValidSlots.Contains(payload.Slots))
        {
            return LicenseCheck.Failed("激活码无效（机位数不在可选档位里）。");
        }

        // ⑦⑧ 本机三段短哈希与码里的比对
        var matchCount = 0;
        var hashes = identity.AllHashes;

        for (var i = 0; i < 3; i++)
        {
            if (hashes[i].AsSpan().SequenceEqual(payload.Segments[i]))
            {
                matchCount++;
            }
        }

        if (matchCount < payload.MinMatch)
        {
            return LicenseCheck.Failed(
                $"此激活码不适用于本机（本机机器码 {identity.Display}）。"
                + "换主板或刷过 BIOS 之后需要重新签发。");
        }

        return LicenseCheck.Passed(payload);
    }

    /// <summary>把显示前缀去掉（用户可能整段粘进来）。</summary>
    public static string StripPrefix(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        var trimmed = code.Trim();

        // 前缀可能带横线（`VLG-…`）也可能不带 —— 只认开头那几个字母。
        var letters = new StringBuilder();

        foreach (var c in trimmed)
        {
            if (char.IsLetter(c))
            {
                letters.Append(char.ToUpperInvariant(c));
                if (letters.Length == DisplayPrefix.Length)
                {
                    break;
                }
            }
            else if (c is '-' or ' ')
            {
                continue;
            }
            else
            {
                break;
            }
        }

        return letters.ToString() == DisplayPrefix ? trimmed[letters.Length..] : trimmed;
    }
}
