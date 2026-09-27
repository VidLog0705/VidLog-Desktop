using System.Security.Cryptography;
using System.Text;

namespace VidLog.Desktop.Core.License;

/// <summary>
/// 本机机器码（`docs/04-许可设计.md` §2）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>只采集「换机器才会变」的标识</b>（L4）：
/// 主板序列号、CPU ID、BIOS 版本。
/// <b>硬盘序列号、网卡 MAC、系统 MachineGuid 一律不要</b> ——
/// 前两个日常就会变（换硬盘、插 USB 网卡），最后一个**重装系统就变**
/// （用户会把「重装一次系统就要重新买」当成产品缺陷）。
/// </para>
/// <para>
/// ⚠️ <b>每段独立哈希，机器码是各段的拼接</b> —— 不是「把所有标识拼起来算一个哈希」。
/// 后者换任何一项整个机器码就变了，而那正是 L4 要避免的售后爆炸。
/// </para>
/// </remarks>
public static class MachineCode
{
    /// <summary>三段各 5 字节 —— 正好编成 8 个 Base32 字符，三段 24 字符好看好抄。</summary>
    public const int SegmentBytes = 5;

    /// <summary>
    /// 各家 OEM 的占位串 —— 读到这些**等同于没读到**。
    /// </summary>
    /// <remarks>
    /// 不排掉的话，一批同型号的机器会算出**同一个**机器码，
    /// 而表现是「别人的激活码能用在我这儿」——那比读不到更糟。
    /// </remarks>
    private static readonly HashSet<string> InvalidValues = new(StringComparer.Ordinal)
    {
        "TOBEFILLEDBYOEM", "DEFAULTSTRING", "SYSTEMSERIALNUMBER", "NONE",
        "NOTSPECIFIED", "NA", "0", "00000000", "FFFFFFFF", "INVALID", "UNKNOWN",
    };

    /// <summary>规范化一个标识值；读不到 / 无效时返回空串。</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);

        foreach (var c in raw.Trim().ToUpperInvariant())
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                builder.Append(c);
            }
        }

        var value = builder.ToString();

        return InvalidValues.Contains(value) ? string.Empty : value;
    }

    /// <summary>
    /// 某一段的短码（8 个 Base32 字符）。
    /// </summary>
    /// <param name="domain">域前缀（<c>MB:</c> / <c>CP:</c> / <c>BI:</c>）——
    /// 防止两个字段出现相同值时算出同一段。</param>
    /// <remarks>
    /// ⚠️ 读不到时返回**全零段** <c>AAAAAAAA</c>（Base32 里 <c>A</c> = 0，
    /// 所以**不是** <c>00000000</c> —— 字母表里根本没有数字 0）。
    /// </remarks>
    public static string Segment(string domain, string? raw)
    {
        var normalized = Normalize(raw);
        if (normalized.Length == 0)
        {
            return Base32.Encode(new byte[SegmentBytes]);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(domain + normalized));

        return Base32.Encode(hash.AsSpan(0, SegmentBytes).ToArray());
    }

    /// <summary>三段拼起来（24 个 Base32 字符）。</summary>
    public static string Compose(string motherboard, string cpu, string bios) =>
        Segment("MB:", motherboard) + Segment("CP:", cpu) + Segment("BI:", bios);

    /// <summary>显示格式：每 4 字符一组，用 <c>-</c> 分隔。</summary>
    public static string Display(string machineCode)
    {
        var compact = Compact(machineCode);
        var groups = new List<string>();

        for (var i = 0; i < compact.Length; i += 4)
        {
            groups.Add(compact.Substring(i, Math.Min(4, compact.Length - i)));
        }

        return string.Join('-', groups);
    }

    /// <summary>
    /// 输入的规范化：**忽略所有非 Base32 字符、大小写不敏感**。
    /// </summary>
    /// <remarks>
    /// 用户会抄错大小写、漏横线、或者从别处粘过来带上空格换行 ——
    /// 不规范化的话，那些全都表现为「此激活码不适用于本机」，而用户看不出哪里错了。
    /// </remarks>
    public static string Compact(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);

        foreach (var c in raw.ToUpperInvariant())
        {
            // ⚠️ Base32 字母表是 `A-Z2-7` —— **没有 0/1/8/9**。
            if (c is >= 'A' and <= 'Z' or >= '2' and <= '7')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 三段短哈希的**原始字节**（激活码里带的就是它们，不是 Base32）。
    /// </summary>
    public static byte[] RawSegment(string domain, string? raw)
    {
        var normalized = Normalize(raw);

        if (normalized.Length == 0)
        {
            return new byte[SegmentBytes];
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(domain + normalized))
            .AsSpan(0, SegmentBytes)
            .ToArray();
    }
}

/// <summary>本机三个硬件标识的采集。</summary>
/// <remarks>
/// 抽成接口是为了让机器码与激活那套逻辑**能在本机测到底** ——
/// 真采集要 WMI，而 CI 上没有主板序列号这种东西。
/// </remarks>
public interface IMachineIdentifiers
{
    string MotherboardSerial { get; }

    string ProcessorId { get; }

    string BiosVersion { get; }
}

/// <summary>一次采集的结果（三段原始值 + 它们各自的短哈希）。</summary>
public sealed record MachineIdentity(
    string MotherboardSerial,
    string ProcessorId,
    string BiosVersion)
{
    public static MachineIdentity From(IMachineIdentifiers source) => new(
        source.MotherboardSerial, source.ProcessorId, source.BiosVersion);

    public byte[] MotherboardHash => MachineCode.RawSegment("MB:", MotherboardSerial);

    public byte[] ProcessorHash => MachineCode.RawSegment("CP:", ProcessorId);

    public byte[] BiosHash => MachineCode.RawSegment("BI:", BiosVersion);

    /// <summary>三段短哈希（激活码里带的就是这三段）。</summary>
    public IReadOnlyList<byte[]> AllHashes => [MotherboardHash, ProcessorHash, BiosHash];

    /// <summary>人看的机器码。</summary>
    public string Display =>
        MachineCode.Display(MachineCode.Compose(MotherboardSerial, ProcessorId, BiosVersion));

    /// <summary>
    /// 有一段读不到 —— 界面**必须把这件事显示出来**（§2.4）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 全部读不到时那个机器码是**全零段**，于是**同型号的机器都会匹配** ——
    /// 这是降级方案的已知弱点。不说出来的话，用户会以为自己拿到的是个正常机器码。
    /// </remarks>
    public bool IsDegraded =>
        MachineCode.RawSegment("MB:", MotherboardSerial).All(b => b == 0)
        || MachineCode.RawSegment("CP:", ProcessorId).All(b => b == 0)
        || MachineCode.RawSegment("BI:", BiosVersion).All(b => b == 0);

    /// <summary>三段都读不到 —— 机器码没有任何区分度。</summary>
    public bool IsUnusable =>
        MachineCode.RawSegment("MB:", MotherboardSerial).All(b => b == 0)
        && MachineCode.RawSegment("CP:", ProcessorId).All(b => b == 0)
        && MachineCode.RawSegment("BI:", BiosVersion).All(b => b == 0);
}
