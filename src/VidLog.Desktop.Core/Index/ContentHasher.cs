using System.Security.Cryptography;

namespace VidLog.Desktop.Core.Index;

/// <summary>
/// 算录像成品的内容哈希。
/// </summary>
/// <remarks>
/// 规格 §3.6.1：每条证据在**落盘时**即计算内容哈希，
/// 指纹 = 内容哈希 + 单号 + 录制时间。
/// <para>
/// 算的是**成品文件**（remux 之后的 MP4），不是中间容器 ——
/// 中间容器收尾后就没了，对它算哈希没有意义。
/// </para>
/// </remarks>
public static class ContentHasher
{
    public static async Task<ContentHash> ComputeFileHashAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);

        var digest = await SHA256.HashDataAsync(stream, cancellationToken);

        return ContentHash.Parse(Convert.ToHexString(digest));
    }
}
