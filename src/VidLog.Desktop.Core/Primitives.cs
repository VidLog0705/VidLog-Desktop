using System.Diagnostics.CodeAnalysis;

namespace VidLog.Desktop.Core;

/// <summary>
/// 录像文件在落点层 / 归档层中的位置。
/// </summary>
/// <remarks>
/// 规格 §6.2 硬约束：路径只存相对路径。绝对路径在应用重装、容器变更后必然失效，
/// 因此本类型在构造时就拒绝一切绝对路径、UNC 路径与向上越级路径。
/// </remarks>
public sealed record RelativePath
{
    public string Value { get; }

    private RelativePath(string value) => Value = value;

    public static RelativePath Parse(string? raw)
    {
        if (!TryParse(raw, out var result, out var error))
        {
            throw new ArgumentException(error, nameof(raw));
        }

        return result;
    }

    public static bool TryParse(
        string? raw,
        [NotNullWhen(true)] out RelativePath? result,
        out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "相对路径不得为空";
            return false;
        }

        // 刻意不用 Path.IsPathRooted：它的判定结果随目标平台变化，
        // 而这里必须让桌面端（Windows）与 CI 上的判定完全一致。
        if (raw[0] is '/' or '\\')
        {
            error = "不得是根路径或 UNC 路径";
            return false;
        }

        if (raw.Length >= 2 && char.IsAsciiLetter(raw[0]) && raw[1] == ':')
        {
            error = "不得是盘符路径";
            return false;
        }

        foreach (var segment in raw.Split('/', '\\'))
        {
            if (segment is "..")
            {
                error = "不得包含向上越级的 '..' 段";
                return false;
            }
        }

        result = new RelativePath(raw);
        return true;
    }

    public override string ToString() => Value;
}

/// <summary>
/// 快递运单编号，归一化后的形态。
/// </summary>
/// <remarks>
/// 规格 §1 与不变量 I5：单号是系统唯一的事实标识，其他属性（公司、分类、备注）
/// 都只是可修正标签。这里用独立类型而不是裸 <see cref="string"/>，是为了让
/// "这个位置传进来的确实是单号"在编译期就成立。
/// <para>
/// 规格 §3.2.3：**归一化后的结果才是单号，一切关联以此为准** —— 所以
/// <see cref="Parse"/> / <see cref="TryParse"/> 都先归一化再构造。
/// </para>
/// <para>
/// <b>已知缺口</b>：§3.2.3 还要求「处理校验位」，本实现**刻意没做**。
/// 校验位规则按承运商而异，规格没有给出适用算法；凭空实现会改变单号的同一性，
/// 而 I5 说单号是唯一事实标识 —— 改错了等于毁掉证据关联。
/// 补这个之前必须先拿到具体承运商的校验位规则。
/// </para>
/// </remarks>
public sealed record WaybillNumber
{
    public string Value { get; }

    private WaybillNumber(string value) => Value = value;

    /// <summary>
    /// 规格 §3.2.3 的归一化：去除空白、统一大小写。
    /// </summary>
    /// <remarks>
    /// 「统一大小写」规格没规定方向，本实现定为**大写**（`02-数据模型.md` 记录该决策）。
    /// 校验位与分隔符**一律保留原样** —— 见类型注释里的已知缺口。
    /// </remarks>
    /// <returns>归一化后的字符串；若归一化后为空则返回 <see langword="null"/>。</returns>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var buffer = new char[raw.Length];
        var length = 0;
        var changed = false;

        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch))
            {
                changed = true;
                continue;
            }

            var upper = char.ToUpperInvariant(ch);
            if (upper != ch)
            {
                changed = true;
            }

            buffer[length++] = upper;
        }

        if (length == 0)
        {
            return null;
        }

        return changed ? new string(buffer, 0, length) : raw;
    }

    public static WaybillNumber Parse(string? raw)
    {
        if (!TryParse(raw, out var result, out var error))
        {
            throw new ArgumentException(error, nameof(raw));
        }

        return result;
    }

    public static bool TryParse(
        string? raw,
        [NotNullWhen(true)] out WaybillNumber? result,
        out string? error)
    {
        result = null;
        error = null;

        var normalized = Normalize(raw);
        if (normalized is null)
        {
            error = "单号不得为空（归一化后没有任何有效字符）";
            return false;
        }

        result = new WaybillNumber(normalized);
        return true;
    }

    public override string ToString() => Value;
}

/// <summary>
/// 录像成品的内容哈希。
/// </summary>
/// <remarks>
/// 规格 §3.6.1：指纹 = 内容哈希 + 单号 + 录制时间。
/// 算法定为 SHA-256（64 位十六进制）；比较时统一为小写，
/// 免得同一份内容因为大小写不同被当成两条证据。
/// </remarks>
public sealed record ContentHash
{
    public const string Algorithm = "sha256";
    public const int HexLength = 64;

    /// <summary>小写十六进制形态。</summary>
    public string Value { get; }

    private ContentHash(string value) => Value = value;

    public static ContentHash Parse(string? raw)
    {
        if (!TryParse(raw, out var result, out var error))
        {
            throw new ArgumentException(error, nameof(raw));
        }

        return result;
    }

    public static bool TryParse(
        string? raw,
        [NotNullWhen(true)] out ContentHash? result,
        out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrEmpty(raw))
        {
            error = "内容哈希不得为空";
            return false;
        }

        if (raw.Length != HexLength)
        {
            error = $"SHA-256 的十六进制形态必须是 {HexLength} 位，实际 {raw.Length} 位";
            return false;
        }

        foreach (var ch in raw)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                error = "内容哈希含非十六进制字符";
                return false;
            }
        }

        result = new ContentHash(raw.ToLowerInvariant());
        return true;
    }

    public override string ToString() => Value;
}
