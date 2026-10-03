namespace VidLog.Desktop.Core.License;

/// <summary>
/// 销售方的**公钥** —— 编进程序（`docs/04-许可设计.md` 的 L2 / S2）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>公钥不是秘密。</b> 它可以进仓、可以公开 —— 它只能**验签**，不能签发。
/// 「密钥绝不进任何仓库」那条（L1 / S1）管的是**私钥**：那把在独立的签发仓
/// （<c>D:\VidLog-Signing</c>）手里，只落到**仓外**的 <c>H:\vidlog-keys\license.pem</c>。
/// </para>
/// <para>
/// ⚠️ <b>这一串原先只从环境变量读（<c>VIDLOG_LICENSE_PUBKEY</c>），而那是条死路。</b>
/// 开发机上手设一个变量当然好用，但**装到客户机上必定没有这个变量** ⇒
/// <see cref="LicenseVerifier.FromEmbeddedKey"/> 拿到空串返回 <c>null</c> ⇒
/// 根本不建 <c>LicenseService</c> ⇒ 机位恒为 <b>0</b> ⇒ 客户粘什么码都激活不了。
/// 而它在外面的表现是**手机上说「电脑端的机位已经满了」、电脑端一个字都不显示**
/// —— 2026-10-03 报上来的就是这一条。
/// </para>
/// <para>
/// 环境变量仍然**优先**（见 <c>DesktopServices</c>）：开发与临时换密钥时不用重编程序。
/// </para>
/// </remarks>
public static class LicensePublicKey
{
    /// <summary>
    /// 签发工具 <c>pubkey</c> 导出的那串（base64 的 SubjectPublicKeyInfo）。
    /// </summary>
    /// <remarks>
    /// 来源：<c>dotnet run --project D:\VidLog-Signing -- pubkey --key H:\vidlog-keys\license.pem</c>
    /// <para>
    /// ⚠️ <b>换密钥 = 换这一行 + 重编程序</b>（S2 就是这么要求的），而换完之后
    /// **已经发出去的码全部作废**，客户得重新激活。所以私钥要备份好。
    /// </para>
    /// <para>
    /// ⚠️ 换行只是为了让这行读得下去，粘的时候要看成**一整串**。
    /// </para>
    /// </remarks>
    public const string Base64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE0gNgqZSUARDHgvGfvyLwPvsNN6OBUv3rVUx0zt25GTH"
        + "chy7pQPtYiVVHLWzIeEj9kHEtZWTETxCNkBSNjdbBHA==";
}
