using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.Core.Cloud;

/// <summary>
/// 录像在百度网盘上的落点。
/// </summary>
/// <remarks>
/// <para>
/// 网盘上开放平台的应用只能写自己的目录，远端根一律是
/// <c>/apps/&lt;应用名&gt;/</c> —— 这个「应用名」是用户在开放平台后台填的**产品名称**，
/// 不是 AppKey，所以它要能配（设计图 `_46` 那张卡的「网盘目录应用名」）。
/// </para>
/// <para>
/// 形状照设计图 `_46` 那句原话「录像上传到百度网盘"我的应用数据/[应用名]/
/// (日期)/发货或退货/单号.mp4"，按开录日期与发货退货分类」：
/// </para>
/// <code>
/// /apps/&lt;应用名&gt;/2026/09/30/发货/SF1000000001_ab12_000.mp4
/// </code>
/// <para>
/// ⚠️ <b>与归档相对路径不是逐段一致</b>（本机 / NAS 那一档才是）：
/// 设计图把「单号」放在**文件名**上并多插了一层「发货或退货」，
/// 所以云端这一份由单号 + 会话 + 序号合成文件名，而不再套一层单号目录。
/// 云端是**第二份**，它的目录形状不需要与第一份相同。
/// </para>
/// <para>
/// ⚠️ <b>「发货 / 退货」那一层取自标签，而标签是可以改的</b>（I5）。
/// 所以<b>永远不能只在一个分类目录里找</b> —— 用户把一条录像从发货改成退货之后，
/// 云端那一份仍在旧的目录下，而「回查归档层」是清理的前置闸（I8）：
/// 只找新目录的话它会一路说「不存在」，于是那条录像**永远清不掉**，
/// 而用户完全不知道为什么。两个目录都找就是这条不变量在这里的落法
/// （见 <see cref="CloudArchiveBackend.VerifyAsync"/>）。
/// </para>
/// </remarks>
public sealed class BaiduPanLayout
{
    /// <summary>远端根：<c>/apps/&lt;应用名&gt;</c>。</summary>
    public BaiduPanLayout(string appName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);

        // ⚠️ 应用名会被拼进 URL 和请求体，所以这里挡掉路径分隔符与控制字符：
        // 一个 `/` 就能让落点跑到应用目录之外（网盘那边的权限会拒，
        // 但报出来的错是「目录不存在」，查起来完全看不出是自己拼错了）。
        AppName = Sanitize(appName.Trim());
    }

    /// <summary>开放平台后台填的那个「产品名称」。</summary>
    public string AppName { get; }

    /// <summary>远端根，末尾带 <c>/</c>。</summary>
    public string Root => $"/apps/{AppName}/";

    /// <summary>两个分类目录的中文名。与界面上、导出的那份**同源**。</summary>
    public static string TypeSegment(BusinessType type) => BusinessTypes.Describe(type);

    /// <summary>一条录像在网盘上的绝对路径。</summary>
    /// <remarks>
    /// 归档相对路径是 <c>&lt;yyyy&gt;/&lt;MM&gt;/&lt;dd&gt;/&lt;单号&gt;/&lt;会话&gt;_&lt;序号&gt;.mp4</c>
    /// （见 <c>ArchiveLayout</c>），这里把它拆开重新拼成设计图那个形状。
    /// 拆不出来（形状不是本仓写的那种）时**不抛**：退化成
    /// 「分类目录 + 原样的相对路径」，照样能传上去、也照样回查得到，
    /// 只是目录不好看 —— 比为此让一条传不上去强。
    /// </remarks>
    public string RemotePath(RelativePath location, BusinessType type)
    {
        var parts = Segments(location);

        if (parts is not [var year, var month, var day, var waybill, var leaf])
        {
            return $"{Root}{TypeSegment(type)}/{location.Value.Replace('\\', '/').Trim('/')}";
        }

        return $"{Root}{year}/{month}/{day}/{TypeSegment(type)}/{waybill}_{leaf}";
    }

    /// <summary>
    /// 这个分类目录的远端路径（列表接口要它）。
    /// </summary>
    /// <remarks>
    /// 目录**只到分类那一层**，不含文件名 —— 列一遍就知道这个目录下都有谁，
    /// 回查与对比补传都靠它（一次请求问一批，不是一条一次）。
    /// </remarks>
    public string DirectoryOf(RelativePath location, BusinessType type)
    {
        var path = RemotePath(location, type);
        var slash = path.LastIndexOf('/');

        return slash < 0 ? Root : path[..(slash + 1)];
    }

    /// <summary>这个远端路径的文件名（列表接口只回名字，靠它判在不在）。</summary>
    public static string FileNameOf(string remotePath)
    {
        var slash = remotePath.LastIndexOf('/');

        return slash < 0 ? remotePath : remotePath[(slash + 1)..];
    }

    /// <summary>一条录像在网盘上的文件名。<b>与「发货 / 退货」无关</b>，只有目录那一段受影响。</summary>
    public string FileNameFor(RelativePath location) => FileNameOf(RemotePath(location, BusinessType.Outbound));

    /// <summary>
    /// 一条录像**可能**落在的那两个目录（发货一个、退货一个）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>回查必须两个都找</b>，理由见本类的说明：分类取自可改的标签（I5），
    /// 只在当前分类下找的话，用户改过一次分类，那条录像在清理的前置闸（I8）眼里
    /// 就永远是「归档层上没有」—— 于是它永远清不掉，而没人知道为什么。
    /// </remarks>
    public IReadOnlyList<string> DirectoriesFor(RelativePath location) =>
    [
        DirectoryOf(location, BusinessType.Outbound),
        DirectoryOf(location, BusinessType.Return),
    ];

    private static string[] Segments(RelativePath location) =>
        location.Value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Sanitize(string appName)
    {
        var cleaned = new string([.. appName.Where(c => c != '/' && c != '\\' && !char.IsControl(c))]);

        // 清完一个字符都不剩（名字全是斜杠）时退回一个一定能用的名字：
        // 抛异常会让设置页在图还没画出来时先崩，而这里是**配置**不是**数据**。
        return cleaned.Length == 0 ? "VidLog" : cleaned;
    }
}
