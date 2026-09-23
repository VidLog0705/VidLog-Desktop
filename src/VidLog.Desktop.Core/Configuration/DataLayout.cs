namespace VidLog.Desktop.Core.Configuration;

/// <summary>
/// 本机数据目录布局。
/// </summary>
/// <remarks>
/// 规格 §6.2 硬约束：**卸载不清用户数据**。
/// 所以根目录必须在安装目录**之外** —— 装到 <c>Program Files</c> 下的话，
/// 卸载或升级会把用户的录像记录一起带走。
/// <para>
/// 默认放在 <c>%LOCALAPPDATA%\VidLog</c>：这是 Windows 上「属于这个用户、
/// 不属于这个程序」的标准位置。
/// </para>
/// <para>
/// 录像**成品**不在这里 —— 它们按规格 §3.5 落在归档层（用户自选的百度网盘 / NAS /
/// 本机存储），由 <see cref="ArchiveRoot"/> 指向。这里只放索引与元数据。
/// </para>
/// </remarks>
public sealed class DataLayout
{
    public DataLayout(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    /// <summary>按默认规则构造：<c>%LOCALAPPDATA%\VidLog</c>。</summary>
    public static DataLayout Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VidLog"));

    /// <summary>本机数据的根目录。</summary>
    public string RootDirectory { get; }

    /// <summary>归档根 —— 索引里的相对路径相对它解析。</summary>
    public string ArchiveRoot => Path.Combine(RootDirectory, "archive");

    /// <summary>录制工作区 —— 分段未收尾时停在这里，重启后由此发现孤儿。</summary>
    public string WorkspaceRoot => Path.Combine(RootDirectory, "work");

    public string IndexPath => Path.Combine(RootDirectory, "index.jsonl");

    public string PunchLogPath => Path.Combine(RootDirectory, "punches.jsonl");

    public string LabelStorePath => Path.Combine(RootDirectory, "labels.jsonl");

    /// <summary>用户设置。损坏时回落默认值，不阻断启动（I4）。</summary>
    public string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>清理审计 —— 每次「删了什么 / 为什么没删」都要留痕（规格 §3.5.5）。</summary>
    public string CleanupAuditPath => Path.Combine(RootDirectory, "cleanup-audit.jsonl");

    /// <summary>
    /// 已入网的设备及其凭据（规格 §3.4.5）。
    /// </summary>
    /// <remarks>
    /// ⚠️ **这个文件里有密钥。** 它必须：不提交进任何仓库、不进诊断包、不随日志外发。
    /// <para>
    /// <b>已知缺口：明文存放。</b> 上 DPAPI 是本地一行的事，但**它挡不住真正的威胁** ——
    /// 拿到这台机器文件系统的人，同样可以直接改 <c>index.jsonl</c>。
    /// 凭据加密在这里不是最薄的那一环，所以先不做，记在这里。
    /// </para>
    /// </remarks>
    public string DevicesPath => Path.Combine(RootDirectory, "devices.jsonl");

    /// <summary>
    /// 上传暂存区 —— 远端传来的分片先落这里，**提交之后**才发布到归档层。
    /// </summary>
    /// <remarks>
    /// 规格 §5.1「原子发布」的落点：这个目录里的东西**不在索引里**，
    /// 因此不参与检索、不参与回放、不参与归档回查（端间契约 §1.4）。
    /// </remarks>
    public string UploadIncomingRoot => Path.Combine(RootDirectory, "incoming");

    /// <summary>
    /// 远端上传的回执（`docs/05-上传接口形状.md` §2.6）。
    /// </summary>
    /// <remarks>
    /// **为什么必须存**：发送方收到回执才算完成。回执在网络上丢了的时候，发送方会重发
    /// commit —— 这时若重新签发一份，`timeAnchor` 就变了，而**同一条证据两次归档给出
    /// 两个不同的时间锚，看起来就是被篡改了**（对取证产品来说是最坏的一种假象）。
    /// 存下来才能「原样再给一份」。
    /// </remarks>
    public string ReceiptsPath => Path.Combine(RootDirectory, "receipts.jsonl");

    /// <summary>日志目录。</summary>
    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>建好所有目录。幂等。</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ArchiveRoot);
        Directory.CreateDirectory(WorkspaceRoot);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(UploadIncomingRoot);
    }
}
