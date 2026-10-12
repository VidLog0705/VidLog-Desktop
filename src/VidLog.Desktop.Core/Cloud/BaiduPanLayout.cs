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
/// (日期)/发货或退货/单号.mp4"，按开录日期与发货退货分类」。
/// **2026-10-12 起**，两端统一成（需求方裁定）：
/// </para>
/// <code>
/// /apps/&lt;应用名&gt;/2026-09-30/发货/SF1000000001.mp4
/// </code>
/// <para>
/// 它与归档相对路径的关系是「去掉 <c>VidLog-Video</c> 那一段」——
/// 本地 <c>VidLog-Video/2026-09-30/发货/SF1000000001.mp4</c>，
/// 远端 <c>/apps/&lt;应用名&gt;/2026-09-30/发货/SF1000000001.mp4</c>。
/// </para>
/// <para>
/// ⚠️ <b>老形状的路径必须继续认得</b>（见 <see cref="RemotePath"/> 第二支）：
/// 盘上/网盘上已经有按 <c>&lt;年&gt;/&lt;月&gt;/&lt;日&gt;/&lt;单号&gt;/&lt;会话&gt;_&lt;序号&gt;.mp4</c>
/// 传上去的录像，算不出它们的远端路径 = 回查一律说「网盘上没有」= 那些录像
/// **永远清不掉**（I8 是清理的前置闸）。
/// </para>
/// <para>
/// ⚠️ <b>回查永远两个分类目录都找</b>：分类取自可改的标签（I5）与老数据，
/// 只在当前分类下找的话，用户改过一次分类、或者那一条是老形状的，
/// 它在清理的前置闸（I8）眼里就永远是「归档层上没有」——
/// 于是它永远清不掉，而没人知道为什么
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

        // ── 新形状（2026-10-12 起）──────────────────────────────────
        // `VidLog-Video/<yyyy-MM-dd>/<发货|退货>/<单号>.mp4`
        // ⇒ 远端就是**去掉第一段**：`/apps/<应用名>/<yyyy-MM-dd>/<发货|退货>/<单号>.mp4`。
        //
        // ⚠️ 分类那一层取的是**路径里自己带的那一个**，不是传进来的 `type`
        // （那个来自标签，而标签按 I5 是可以改的）。理由与归档那边同一条：
        // 本地那份躺在 `发货/` 而远端把它挂到 `退货/` 的话，两边就各说各话；
        // 而 <c>UploadQueue</c> 里存的是**入队那一刻算出来的**远端路径，
        // 用标签算会让「同一份录像，改一次标签就换一个远端路径」——
        // 于是它会被当成没传过，重传一遍。路径就该由路径自己决定。
        if (parts is [var root, var date, var classification, var leaf]
            && root == ArchiveLayout.RootFolder)
        {
            return $"{Root}{date}/{classification}/{leaf}";
        }

        // ── 老形状（保留）────────────────────────────────────────────
        // `<年>/<月>/<日>/<单号>/<会话>_<序号>.mp4`
        //
        // ⚠️ **这一支不许删**：盘上/网盘上已经有按老形状传上去的录像。
        // 删了它，那些录像的远端路径会算成另一个值 ⇒ 回查一路说「网盘上没有」
        // ⇒ 它们**永远清不掉**（I8 是清理的前置闸），而用户完全不知道为什么。
        if (parts is not [var year, var month, var day, var waybill, var oldLeaf])
        {
            return $"{Root}{TypeSegment(type)}/{location.Value.Replace('\\', '/').Trim('/')}";
        }

        return $"{Root}{year}/{month}/{day}/{TypeSegment(type)}/{waybill}_{oldLeaf}";
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
        // ⚠️ 去重：新形状下这一支**根本不看 `type`**（分类就在路径里），
        // 两个候选算出来是同一个目录。不去重的话每次回查都白问一遍网盘，
        // 而它有限流（见 `CloudUploadService` 里那段「能把账号问进风控」）。
        [.. new[]
        {
            DirectoryOf(location, BusinessType.Outbound),
            DirectoryOf(location, BusinessType.Return),
        }.Distinct(StringComparer.Ordinal)];

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
