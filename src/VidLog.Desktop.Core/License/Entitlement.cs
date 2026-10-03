using System.Text.Json;
using System.Text.Json.Serialization;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.License;

/// <summary>落盘的激活记录（`docs/04-许可设计.md` §4.4）。</summary>
/// <remarks>
/// ⚠️ <b>它只是「激活过」的记录，不是判据</b>：启动时**重新完整验签 + 重新比对
/// 机器码**（§4.4 / L7），**不读这里缓存下来的 `Slots`** ——
/// 读缓存的话，用户手改一下这个文件就能给自己加机位。
/// </remarks>
public sealed record Entitlement
{
    /// <summary>激活码原文（重新验签要用它）。</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>解析出来的档位 —— **只用于显示**，判定一律以重新验签为准。</summary>
    public int Slots { get; init; }

    /// <summary>销售序号（售后追溯用）。</summary>
    public uint Serial { get; init; }

    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>激活那一刻的机器码（**只用于诊断**，不参与校验）。</summary>
    public string MachineCode { get; init; } = string.Empty;

    public DateTimeOffset ActivatedAt { get; init; }
}

/// <summary>激活记录的读写（`&lt;root&gt;/license.json`）。</summary>
public sealed class EntitlementStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public EntitlementStore(string path)
    {
        _path = path;
    }

    public async Task<Entitlement?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken);
            return JsonSerializer.Deserialize<Entitlement>(json, Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 读坏了当作「没激活过」—— 那会挡住录制，所以调用方要把原因说出来
            // （不是「静默回落到默认值」那种情况）。
            return null;
        }
    }

    public async Task SaveAsync(Entitlement entitlement, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(entitlement, Options);
        var temporary = _path + ".tmp";

        await File.WriteAllTextAsync(temporary, json, cancellationToken);

        try
        {
            File.Move(temporary, _path, overwrite: true);
        }
        catch (IOException)
        {
            await Task.Delay(50, cancellationToken);
            File.Move(temporary, _path, overwrite: true);
        }
    }
}

/// <summary>许可当前的状态。</summary>
/// <param name="Activated">激活了没有（启动时那次校验的结论）。**试用中也是 <see langword="true"/>**。</param>
/// <param name="Slots">允许接入的手机端台数。</param>
/// <param name="FailureReason">没激活 / 校验失败的原因（**给用户看的那句话**）。</param>
/// <param name="MachineCode">本机机器码（给用户抄给卖家用）。</param>
/// <param name="Degraded">机器码有降级（某一段读不到）。</param>
/// <param name="IsTrial">这是**试用**而不是买断（规格 §6）。</param>
/// <param name="TrialRemaining">试用还剩多久（<see cref="IsTrial"/> 为假时是 <see langword="null"/>）。</param>
/// <remarks>
/// ⚠️ <b>试用中借用 <see cref="Activated"/> 的语义（<c>true</c> + <c>Slots = 4</c>），
/// 这是刻意的。</b> 判「能不能录」的是
/// <c>RecordingCoordinator.LicenseBlockedReason</c>、判机位的是
/// <c>DesktopServices</c> 里那个 <c>seatLimit</c> lambda —— 两处读的都是
/// <see cref="Activated"/> 与 <see cref="Slots"/>。让试用复用这两个字段，
/// **下游一行都不用改**。
/// <para>
/// 代价是「<c>Activated = true</c>」会被读成「已激活」，所以**凡是把它渲染成人话的地方，
/// 都必须先看 <see cref="IsTrial"/>**。全仓这样的地方只有两处
/// （<c>StatusSummaries</c> 与 <c>SettingsWindow</c> 的许可页），两处都分了支。
/// </para>
/// <para>
/// ⚠️ 新字段**必须带默认值、且放在最后**：这个 record 是位置参数，
/// 老构造点（含测试）不加默认值就全编不过。
/// </para>
/// </remarks>
public sealed record LicenseStatus(
    bool Activated,
    int Slots,
    string? FailureReason,
    string MachineCode,
    bool Degraded,
    bool IsTrial = false,
    TimeSpan? TrialRemaining = null)
{
    /// <summary>能不能录 —— **只挡住新录**（L5）。</summary>
    public bool CanRecord => Activated;

    /// <summary>
    /// 试用还剩多久 —— 给界面看的一句话。
    /// </summary>
    /// <remarks>
    /// ⚠️ 放在这里而不是各写一份：主窗导航栏与许可页都要显示它，
    /// 而仓规不许同一个说法有两份（<c>MainWindow.xaml.cs</c> 里那条注释点名过这件事）。
    /// </remarks>
    public string TrialRemainingText
    {
        get
        {
            if (TrialRemaining is not { } left || left <= TimeSpan.Zero)
            {
                return "不到 1 小时";
            }

            return left.TotalDays >= 1
                ? $"{(int)left.TotalDays} 天 {left.Hours} 小时"
                : $"{Math.Max(1, (int)Math.Ceiling(left.TotalHours))} 小时";
        }
    }
}

/// <summary>
/// 许可的启动校验与状态（L7：**只在启动时校验一次，运行期间不再查**）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>L8 红线：许可**不得锁住已有录像**。</b> 这个类只回答一个问题
/// ——「能不能开始**新的**录制」。检索、回放、导出、交付、清理**一条都不看它**
/// （`LicenseIndependenceTests` 是这条红线的唯一防线）。
/// </para>
/// <para>
/// ⚠️ 「运行期间冻结」是**刻意的**：录到一半许可过期了，不该把这一段掐掉。
/// 下次启动才生效。
/// </para>
/// </remarks>
public sealed class LicenseService
{
    private readonly LicenseVerifier _verifier;
    private readonly EntitlementStore _store;
    private readonly MachineIdentity _identity;
    private readonly IAppLogger _logger;
    private readonly TrialRecordStore? _trial;
    private readonly Func<DateTimeOffset> _now;

    private LicenseStatus _status;

    /// <param name="trial">
    /// 试用记录（§6.3）。给 <see langword="null"/> 时**试用码一律激活不了**
    /// —— 组合根必须给，测试可以不给（那是在测别的）。
    /// </param>
    public LicenseService(
        LicenseVerifier verifier,
        EntitlementStore store,
        MachineIdentity identity,
        IAppLogger? logger = null,
        TrialRecordStore? trial = null,
        Func<DateTimeOffset>? now = null)
    {
        _verifier = verifier;
        _store = store;
        _identity = identity;
        _logger = logger ?? NullLogger.Instance;
        _trial = trial;
        _now = now ?? (() => DateTimeOffset.Now);

        _status = new LicenseStatus(false, 0, "还没有激活。", identity.Display, identity.IsDegraded);
    }

    /// <summary>当前状态。启动时算一次之后**不再变**（L7）。</summary>
    public LicenseStatus Status => _status;

    /// <summary>本机的机器标识（界面要拿它显示机器码）。</summary>
    public MachineIdentity Identity => _identity;

    /// <summary>启动时校验一次（§4.4）。</summary>
    public async Task<LicenseStatus> CheckAtStartupAsync(CancellationToken cancellationToken = default)
    {
        var entitlement = await _store.LoadAsync(cancellationToken);

        if (entitlement is null || string.IsNullOrWhiteSpace(entitlement.Code))
        {
            _status = new LicenseStatus(
                false, 0, "这台电脑还没有激活。", _identity.Display, _identity.IsDegraded);

            return _status;
        }

        // ⚠️ **重新完整验签 + 重新比对机器码** —— 不读缓存里的 Slots（§4.4）。
        var check = _verifier.Verify(entitlement.Code, _identity);

        if (!check.Ok || check.Payload is null)
        {
            _logger.Log(LogLevel.Warn, "许可", $"启动校验没通过：{check.FailureReason}");

            _status = new LicenseStatus(
                false, 0, check.FailureReason, _identity.Display, _identity.IsDegraded);

            return _status;
        }

        // ⚠️ 试用码走另一条路：它的「能不能用」不只看验签，还要看**这台机器还剩多少试用窗口**。
        //    起算点的兜底用 `ActivatedAt`（就是当初激活那一刻）—— 四个试用位置万一
        //    全都写不进去，它保证不会变成「每次启动重新开始 7 天」。
        if (check.Payload.IsTrial)
        {
            _status = EvaluateTrial(check.Payload, entitlement.ActivatedAt);

            _logger.Log(LogLevel.Info, "许可",
                $"启动校验：试用码，{_status.FailureReason ?? $"还剩 {_status.TrialRemainingText}"}");

            return _status;
        }

        _status = new LicenseStatus(
            true, check.Payload.Slots, null, _identity.Display, _identity.IsDegraded);

        _logger.Log(LogLevel.Info, "许可", $"已激活：{check.Payload.Slots} 机位",
            new Dictionary<string, object?> { ["序号"] = check.Payload.Serial });

        return _status;
    }

    /// <summary>
    /// 试用码的判定（§6.4）：读试用记录，算出还剩多少窗口。
    /// </summary>
    /// <param name="payload">已经验过签的载荷（必定是 <see cref="LicensePayload.IsTrial"/>）。</param>
    /// <param name="fallbackFirstRunAt">四处都读不到时的起算点，见 <see cref="TrialRecordStore.Evaluate"/>。</param>
    private LicenseStatus EvaluateTrial(LicensePayload payload, DateTimeOffset? fallbackFirstRunAt)
    {
        if (_trial is null)
        {
            // 只在测试里构造得出来（组合根一定会给）。真发生了要说准，
            // 不然用户会拿着一个没问题的码反复找卖家。
            _logger.Log(LogLevel.Warn, "许可", "试用码验过了，但本机没配试用记录 —— 没法判到期");

            return new LicenseStatus(
                false, 0, "本机没配上试用记录，这个试用码用不了。请重新安装或联系提供方。",
                _identity.Display, _identity.IsDegraded, IsTrial: true, TrialRemaining: TimeSpan.Zero);
        }

        var state = _trial.Evaluate(_now(), fallbackFirstRunAt);

        return state.Phase == TrialPhase.Running
            ? new LicenseStatus(
                true, LicensePayload.TrialSlots, null,
                _identity.Display, _identity.IsDegraded, IsTrial: true, TrialRemaining: state.Remaining)
            : new LicenseStatus(
                false, 0, "试用已结束。",
                _identity.Display, _identity.IsDegraded, IsTrial: true, TrialRemaining: TimeSpan.Zero);
    }

    /// <summary>
    /// 用户粘了一个激活码 → 校验 → 通过就落盘并解锁。
    /// </summary>
    /// <remarks>
    /// ⚠️ 失败时**不动**已激活的状态（一次手滑不该把已经激活的机器锁掉）。
    /// </remarks>
    public async Task<LicenseStatus> ActivateAsync(
        string code, CancellationToken cancellationToken = default)
    {
        var check = _verifier.Verify(code, _identity);

        if (!check.Ok || check.Payload is null)
        {
            // ⚠️ 失败**要留痕**（§6.1）。用户在电话里只会说「激活不了」，
            //    而那一句分不出「码抄漏了一段」「码是给别的机器的」「码是编的」——
            //    判据就在这个 `FailureReason` 里，不记下来就只能让他再抄一遍。
            // ⚠️ **绝不记那串码本身**：它是凭据（而且 `Sanitizer` 不认识它，
            //    不会替我们打码）。记的是结论。
            _logger.Log(LogLevel.Warn, "许可", $"激活没成功：{check.FailureReason}");

            // 保持原状态不变 —— 见方法注释。
            return _status with { FailureReason = check.FailureReason };
        }

        var entitlement = new Entitlement
        {
            Code = code.Trim(),
            Slots = check.Payload.Slots,
            Serial = check.Payload.Serial,
            IssuedAt = new DateTimeOffset(
                check.Payload.Issued.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            MachineCode = _identity.Display,
            ActivatedAt = DateTimeOffset.Now,
        };

        await _store.SaveAsync(entitlement, cancellationToken);

        if (check.Payload.IsTrial)
        {
            // ⚠️ 这里传 `null`（= 真的首次）：**激活这一刻正是试用起算的那一刻**。
            //    而机器上如果**已经**有试用记录，`Evaluate` 会取其中最早的 ——
            //    所以「同一台机器再签一个试用码」不会重置窗口，只会接着用剩下的那段。
            _status = EvaluateTrial(check.Payload, fallbackFirstRunAt: null);

            _logger.Log(LogLevel.Info, "许可",
                $"试用码激活：{_status.FailureReason ?? $"开始 7 天试用，机位 {_status.Slots}"}",
                new Dictionary<string, object?> { ["序号"] = check.Payload.Serial });

            return _status;
        }

        _status = new LicenseStatus(
            true, check.Payload.Slots, null, _identity.Display, _identity.IsDegraded);

        _logger.Log(LogLevel.Info, "许可", $"激活成功：{check.Payload.Slots} 机位");

        return _status;
    }
}
