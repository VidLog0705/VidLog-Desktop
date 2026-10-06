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
    /// 时间校准状态（规格 §3.6.4）。
    /// </summary>
    /// <remarks>
    /// ⚠️ **必须落盘**：规格原话「已校准状态**落盘持久化**，之后离线照常录制」——
    /// 不落盘的话，一台交付之后从没联过网的机器重启一次就再也录不了。
    /// </remarks>
    public string CalibrationPath => Path.Combine(RootDirectory, "calibration.json");

    /// <summary>
    /// 激活记录（`docs/04-许可设计.md` §4.4）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它与录像记录**同一个根、但独立文件** —— 而且**删掉它不会锁住已有录像**：
    /// 许可只挡住**新录**（L8），检索、回放、导出、交付一条都不看它。
    /// </remarks>
    public string LicensePath => Path.Combine(RootDirectory, "license.json");

    /// <summary>
    /// 试用记录（`docs/04-许可设计.md` §6.3 的四处位置之一）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 另外三处**不在这里** —— <c>%ProgramData%</c> 与两处注册表都不在数据根下面，
    /// 由 <c>License.TrialRecordStore.ForThisMachine</c> 自己拼。
    /// 四处的取舍见规格 §6.3：「要重置必须四个全找到并删掉」。
    /// </remarks>
    public string TrialPath => Path.Combine(RootDirectory, "trial.dat");

    /// <summary>
    /// 错误扫描记录（规格 §6.1「必须保存的事实」）。
    /// </summary>
    /// <remarks>
    /// 与手机端**同一个文件名、同一层位置**（<c>&lt;root&gt;/scan-errors.jsonl</c>）——
    /// 两端写的是同一份形态，键名也逐字相同（PascalCase）。
    /// </remarks>
    public string ScanErrorsPath => Path.Combine(RootDirectory, "scan-errors.jsonl");

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

    /// <summary>
    /// 桌面**自己**发布到归档层的那本账（T18）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ReceiptsPath"/> **分开两个文件**，理由见
    /// <c>PublishedStore</c>：回执是**发给手机的线上契约**，这本是**自己记的账**。
    /// 两者记的是同一个事实（「这条在 T 时刻进了归档层」），但读它们的人不一样，
    /// 混在一起会让前一个意思被后一个污染。
    /// </remarks>
    public string PublishedPath => Path.Combine(RootDirectory, "published.jsonl");

    /// <summary>
    /// 百度网盘的登录令牌（设计图 `_45`「账号与上传状态」）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这个文件是一份 bearer 凭据</b>：拿到它的人可以读写这个账号
    /// <c>/apps/&lt;应用名&gt;/</c> 下的东西。所以：
    /// </remarks>
    /// <list type="bullet">
    /// <item>它落在**应用自己的数据目录**里（只有本用户能读），不进仓库；</item>
    /// <item><b>绝不写进 <c>settings.json</c></b> —— 那个文件会被用户复制到别的机器、
    /// 被贴进工单、被备份工具扫走；</item>
    /// <item>诊断包**也不含它**（见 <c>DiagnosticsPackage</c>：它只收索引摘要、
    /// 日志、会话清单，从不整目录打包）。</item>
    /// </list>
    /// <para>
    /// ⚠️ 它是**明文**。理由与代价写在 <c>BaiduPanTokenStore</c> 与
    /// <c>docs/实现决策.md</c> §87，不藏着。
    /// </para>
    /// </remarks>
    public string CloudTokenPath => Path.Combine(RootDirectory, "baidu-pan-token.json");

    /// <summary>
    /// 百度网盘的上传队列（设计图 `_45` / `_46` 的「上传队列」卡）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它**不是证据的一部分**：丢了、读坏了，代价只是「不知道哪条传到哪了」，
    /// 重扫一遍索引就能重建。所以它与索引、标签那些**同一个根但独立文件**。
    /// </remarks>
    public string CloudQueuePath => Path.Combine(RootDirectory, "upload-queue.jsonl");

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
