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
/// <param name="TrialEndsAt">
/// 试用到期的那一**刻**（需求方 2026-10-17 要的「天-时-分-秒」倒计时靠它现算）。
/// <see langword="null"/> = 不是试用 / 没有绝对时刻（老构造点与测试用
/// <see cref="TrialRemaining"/> 那个快照就够）。
/// </param>
/// <remarks>
/// ⚠️ <b>试用中借用 <see cref="Activated"/> 的语义（<c>true</c> + <c>Slots = 4</c>），
/// 这是刻意的。</b> 判「能不能录」的是
/// <c>RecordingCoordinator.LicenseBlockedReason</c>、判机位的是
/// <c>DesktopServices</c> 里那个 <c>seatLimit</c> lambda —— 两处读的都是
/// <see cref="Activated"/> 与 <see cref="Slots"/>。让试用复用这两个字段，
/// **下游一行都不用改**。
/// <para>
/// 代价是「<c>Activated = true</c>」会被读成「已激活」，所以**凡是把它渲染成人话的地方，
/// 都必须先看 <see cref="IsTrial"/>**。全仓渲染成人话的地方只有一处
/// （<see cref="SummaryText"/>），那个分支就写在那里 —— T30 之前它是两份，
/// 两份的分支顺序都对，但设置页那份**写死了机位数**（「4 机位」）。
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
    TimeSpan? TrialRemaining = null,
    DateTimeOffset? TrialEndsAt = null)
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
    public string TrialRemainingText => Countdown(RemainingAt(DateTimeOffset.Now));

    /// <summary>
    /// 到 <paramref name="now"/> 这一刻还剩多久。
    /// </summary>
    /// <remarks>
    /// ⚠️ 有 <see cref="TrialEndsAt"/> 时**按那一刻现算** —— 界面每秒刷一次，
    /// 拿启动时的 <see cref="TrialRemaining"/> 快照会让倒计时停在原地。
    /// 没有绝对时刻时才退回那个快照（老构造点 / 测试）。
    /// </remarks>
    public TimeSpan? RemainingAt(DateTimeOffset now) =>
        TrialEndsAt is { } ends ? ends - now : TrialRemaining;

    /// <summary>
    /// 倒计时那一段文字：`天-时-分-秒`（需求方 2026-10-17 裁定）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 还剩不到一天时**照样写「0 天」**：少了「天」那一段，读的人会以为
    /// 那串 `05:12:33` 是「5 小时」还是「5 分钟」要靠猜。
    /// 已经用完（或时钟被往回拨过头）时给全零，不给负数。
    /// </remarks>
    public static string Countdown(TimeSpan? left)
    {
        if (left is not { } span)
        {
            return "不到 1 小时";
        }

        if (span <= TimeSpan.Zero)
        {
            return "0 天 00:00:00";
        }

        return $"{(int)span.TotalDays} 天 {span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>
    /// 许可状态的一句话 —— 界面上**只此一处**（T30）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>试用必须先判</b>：试用中 <see cref="Activated"/> 也是 <see langword="true"/>
    /// （见本类型注释），不先看 <see cref="IsTrial"/> 的话，一行「✅ 已激活」就把
    /// 还剩几小时藏掉了 —— 而那正是用户此刻最该知道的一件事。
    /// </para>
    /// <para>
    /// ⚠️ <b>修之前这句话在 <c>StatusSummaries</c> 与 <c>SettingsWindow</c> 的许可页里
    /// 各写了一份</b>，而设置页那份把机位数**写死成「4 机位」**（不读
    /// <see cref="Slots"/>）。两边一旦漂开，用户会在两处看到**不一样的机位数** ——
    /// 与「右栏说已激活、状态栏说未激活」是同一类不可信（<c>MainWindow.Recording.cs</c>
    /// 里那条注释点名过这件事）。
    /// </para>
    /// <para>
    /// 这一句是**共用**的骨架；各处要补的上下文（设置页那段「已有的录像照常可以检索」
    /// 与降级警告）由调用点自己接在后面。
    /// </para>
    /// </remarks>
    public string SummaryText => SummaryTextAt(DateTimeOffset.Now);

    /// <summary>
    /// 同上，但按 <paramref name="now"/> 这一刻算试用倒计时
    /// （界面每秒刷一次就走这条；测试也走这条，才钉得住那个数）。
    /// </summary>
    public string SummaryTextAt(DateTimeOffset now)
    {
        if (IsTrial && Activated)
        {
            return $"✅ 试用中：还剩 {Countdown(RemainingAt(now))}，可接入 {Slots} 台手机端。";
        }

        return Activated
            ? $"✅ 已激活：允许接入 {Slots} 台手机端。"
            : $"⛔ {FailureReason}允许接入 0 台手机端。";
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
/// 唯一的例外是 <see cref="EndTrialNow"/>（需求方 2026-10-17：到期后不能再录新的）——
/// 它由主窗在**这一场录完之后**调用，而且只往「收」的方向翻，不碰正在录的段。
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

    /// <summary>
    /// 当前状态。启动时算一次，之后只在两处变：用户激活成功，
    /// 或者 <see cref="EndTrialNow"/>（运行中试用到期）。
    /// </summary>
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

        var now = _now();
        var state = _trial.Evaluate(now, fallbackFirstRunAt);

        return state.Phase == TrialPhase.Running
            ? new LicenseStatus(
                true, LicensePayload.TrialSlots, null,
                _identity.Display, _identity.IsDegraded, IsTrial: true,
                TrialRemaining: state.Remaining,
                // 界面要按「到期那一刻」现算天-时-分-秒，快照不够（它不会动）。
                TrialEndsAt: now + state.Remaining)
            : new LicenseStatus(
                false, 0, "试用已结束。",
                _identity.Display, _identity.IsDegraded, IsTrial: true, TrialRemaining: TimeSpan.Zero);
    }

    /// <summary>
    /// 运行中试用到期：把状态翻成「试用已结束」（需求方 2026-10-17）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>这是 L7「运行期冻结」唯一的例外，而且只许往「收」的方向翻</b>：
    /// 到期不会掐断**正在录**的那一场（协调器只在开始工作的两个入口看许可），
    /// 但之后新的录制与新的手机接入会被挡住 —— 那正是需求要的。
    /// </para>
    /// <para>
    /// 调用点是主窗那个每秒的计时器：录着的时候先不翻，等这一场结束再翻，
    /// 这样「录制结束才弹窗」与「弹完就不能再录」两件事是同一个时刻。
    /// </para>
    /// </remarks>
    public void EndTrialNow()
    {
        if (!_status.IsTrial || !_status.Activated)
        {
            return;
        }

        _status = new LicenseStatus(
            false, 0, "试用已结束。", _identity.Display, _identity.IsDegraded,
            IsTrial: true, TrialRemaining: TimeSpan.Zero);

        _logger.Log(LogLevel.Info, "许可", "运行中试用到期：新的录制与接入已停止（已有的录像照常可查）");
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

        // ⚠️ **在覆盖之前**读一次旧记录：下面那句 Save 会把「上一次激活」抹掉，
        //    而试用起算的兜底要靠它（见 `TrialAnchor`）。
        var previous = await _store.LoadAsync(cancellationToken);

        var entitlement = new Entitlement
        {
            Code = code.Trim(),
            Slots = check.Payload.Slots,
            Serial = check.Payload.Serial,
            IssuedAt = new DateTimeOffset(
                check.Payload.Issued.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            MachineCode = _identity.Display,
            // ⚠️ 走注入的时钟（`_now`），不是 `DateTimeOffset.Now`：
            //    它是试用起算的兜底（`TrialAnchor`），必须和 `Evaluate` 用同一个时间源。
            ActivatedAt = _now(),
        };

        await _store.SaveAsync(entitlement, cancellationToken);

        if (check.Payload.IsTrial)
        {
            // ⚠️ 起算兜底：真的首次传 `null`（激活这一刻正是起算那一刻）；
            //    而这台机器**以前激活过试用码**时，用那次的激活时刻兜底。
            //    不兜的话有一个洞：把四处试用记录删掉、再把**同一张旧码**粘一次，
            //    就能重新开始 168 小时（2026-10-17 查实）。
            //    机器上如果四处记录还在，`Evaluate` 取其中最早的，兜底根本用不上 ——
            //    「同一台机器再签一个试用码」照旧不重置，只接着用剩下的那段。
            _status = EvaluateTrial(check.Payload, fallbackFirstRunAt: TrialAnchor(previous));

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

    /// <summary>
    /// 这台机器上「上一次激活过试用码」的时刻 —— 给 <see cref="TrialRecordStore.Evaluate"/>
    /// 当四处记录都读不到时的起算兜底。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>不兜的话，试用只防君子</b>：把四处试用记录删掉、再把同一张旧试用码
    /// 粘一次，`Evaluate` 会把它当成首次运行，重新给 168 小时（2026-10-17 查实）。
    /// </para>
    /// <para>
    /// ⚠️ 判据是**旧记录里那串码自己验得过且是试用码**，不是「旧记录存在」：
    /// 机器上如果原来是一张终身码，就**不该**拿它的激活时刻去扣试用窗口。
    /// 旧记录验不过（换过硬件 / 手改过文件）时返回 <see langword="null"/>，
    /// 与没有旧记录同一个结果 —— 那时能挡的只有四处记录本身。
    /// </para>
    /// </remarks>
    private DateTimeOffset? TrialAnchor(Entitlement? previous)
    {
        if (previous is not { Code.Length: > 0 } || previous.ActivatedAt == default)
        {
            return null;
        }

        var prior = _verifier.Verify(previous.Code, _identity);

        return prior.Ok && prior.Payload is { IsTrial: true } ? previous.ActivatedAt : null;
    }
}
