using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Configuration;

/// <summary>
/// 配置向导**界面上现在这一份**设置（T27② 第 3 批块 4）。
/// </summary>
/// <remarks>
/// <para>
/// 全是当场从控件读出来的值 —— 与 <c>WizardWindow</c> 那边「不维护一份
/// <c>_pending</c> 字段」是同一个理由：那种字段与控件状态是一对会走岔的东西，
/// 而走岔的表现是「界面上选的是 A，存下去的是 B」—— 静默，且用户无法自查。
/// </para>
/// <para>
/// ⚠️ 收的是**已经读好的值**，不是控件本身：Core 是 <c>net9.0</c>，看不到 WPF。
/// </para>
/// </remarks>
public sealed record WizardDraft
{
    /// <summary>步 1：连续录（否则同单停）。</summary>
    public bool Continuous { get; init; }

    /// <summary>步 2 选中的那一档；一个都没选中就是 <see cref="CameraSource.None"/>。</summary>
    public CameraSource Camera { get; init; } = CameraSource.None;

    /// <summary>步 2 那个「用摄像头取景识码」。</summary>
    public bool Recognition { get; init; }

    /// <summary>步 2 那颗【旋转】按钮现在的档位。</summary>
    public CameraRotation Rotation { get; init; }

    /// <summary>步 4 选的麦克风；没选就是 <see langword="null"/>。</summary>
    public string? Microphone { get; init; }
}

/// <summary>
/// 向导的两条规矩：草稿怎么并进设置、哪一步取景（T27② 第 3 批块 4）。
/// </summary>
/// <remarks>
/// <para>
/// 原先都在 <c>WizardWindow.xaml.cs</c> 里，而那个工程没有测试工程 ——
/// 于是下面每一条都只是注释。它们各自的坏法都是**静默**的：
/// 摄像头种类换一下就把另一边忘干净、网络源没测通也去取景（用户看到的是
/// 「没画面」，看不出是「还没测」）、预览的键带着摄像头密码躺在内存里。
/// </para>
/// <para>
/// ⚠️ 控件怎么读（`IsChecked`、下拉里选中了哪一项）留在壳里 —— 那是控件的事。
/// 这里只收读出来的值。
/// </para>
/// </remarks>
public static class WizardPlan
{
    /// <summary>步 2：摄像头。</summary>
    public const int CameraStep = 1;

    /// <summary>步 3：识码。</summary>
    public const int RecognitionStep = 2;

    /// <summary>
    /// 把界面上这一份并进设置里（其余字段保持用户原来存的那些）。
    /// </summary>
    /// <param name="remembered">已经存着的那一份。</param>
    /// <param name="draft">界面上现在这一份。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>换摄像头种类时保留另一边的值</b>（换回本机设备时还记得上次是哪台），
    /// 与 <see cref="CameraSource.FromConfig"/> 是同一个口径：
    /// 反过来（另一边的值一起抹掉）的话，用户在两种之间来回试一次就得重填一遍地址，
    /// 而那是他手上唯一没有备份的一份东西。
    /// </para>
    /// <para>
    /// ⚠️ 麦克风那句是 <c>?? remembered</c>：这一步的下拉在**枚举不出来**时是空的，
    /// 直接写 <see langword="null"/> 的话，向导走到最后一步会把用户原来配好的
    /// 麦克风**清掉**（之后录出来全是默片）。
    /// </para>
    /// </remarks>
    public static AppSettings Build(AppSettings remembered, WizardDraft draft) => remembered with
    {
        Mode = draft.Continuous ? WorkMode.Continuous : WorkMode.StopOnSameWaybill,
        CameraSource = draft.Camera.Kind,
        CameraDevice = draft.Camera.IsNetwork ? remembered.CameraDevice : draft.Camera.Address,
        CameraNetworkUrl = draft.Camera.IsNetwork ? draft.Camera.Address : remembered.CameraNetworkUrl,
        CameraRecognition = draft.Recognition,
        Rotation = draft.Rotation,
        MicrophoneDevice = draft.Microphone ?? remembered.MicrophoneDevice,
    };

    /// <summary>
    /// 这一路现在**能不能**取景。
    /// </summary>
    /// <param name="source">界面上现在选的那一路。</param>
    /// <param name="networkVerified">网络那一档有没有「测试连接」成功过。</param>
    /// <remarks>
    /// ⚠️ <b>网络那一档在测通之前不取景</b>（照图 `_18` 那句话）。这一条**自己不出声**
    /// —— 不取景时界面上只是没有画面，看不出「是因为还没测」。
    /// 所以「为什么没画面」得由壳里那句补上（<c>ShowIdleHint</c>：
    /// 「地址已填好，点一下【测试连接】验证这一路」）—— 判据搬到这里，
    /// 那句话**不能**跟着一起当成「反正规则在 Core」就删掉。
    /// </remarks>
    public static bool SourceReady(CameraSource source, bool networkVerified) =>
        !source.IsEmpty && (!source.IsNetwork || networkVerified);

    /// <summary>这一步要不要取景。</summary>
    /// <param name="step">现在在第几步。</param>
    /// <param name="recognition">「用摄像头取景识码」开着没有。</param>
    /// <param name="sourceReady">这一路取不取得到帧（见 <see cref="SourceReady"/>）。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ 识码那一步**还要看识码开着没有**：关着识码却在取景，等于白占相机
    /// —— 而 dshow 上相机是**独占**的，占着会挡住别的程序。
    /// </para>
    /// <para>
    /// ⚠️ 其余几步一律不取景（性能 / 麦克风 / 确认那几步预览没有用途）。
    /// </para>
    /// </remarks>
    public static bool NeedsPreview(int step, bool recognition, bool sourceReady) => step switch
    {
        CameraStep => sourceReady,
        RecognitionStep => recognition && sourceReady,
        _ => false,
    };

    /// <summary>
    /// 正在跑的那一路预览是给谁跑的 —— 这个串变了就要重开。
    /// </summary>
    /// <remarks>
    /// ⚠️ 用 <see cref="CameraSource.Identity"/> 而**不是**
    /// <see cref="CameraSource.Address"/>：网络地址里是用户自己的摄像头密码，
    /// 而这个串会长期留在内存里当一个字典键（壳里的 <c>_previewKey</c>），
    /// 没有必要带着它。两个属性名字只差一个词，换错的表现是**没有任何症状** ——
    /// 只是密码一直躺在内存里。
    /// </remarks>
    public static string PreviewKey(CameraSource source, CameraRotation rotation) =>
        $"{source.Kind}|{source.Identity}|{(int)rotation}";
}
