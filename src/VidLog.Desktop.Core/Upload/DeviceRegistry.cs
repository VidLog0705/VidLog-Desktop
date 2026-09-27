using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.License;

namespace VidLog.Desktop.Core.Upload;

/// <summary>
/// 一台已经入网的设备（端间契约 §1.1 第 4 步）。
/// </summary>
/// <param name="Credential">base64url 的 32 字节随机数。⚠️ 这是密钥，见 <see cref="DataLayout.DevicesPath"/>。</param>
public sealed record EnrolledDevice(
    string DeviceId,
    string DeviceName,
    string Credential,
    DateTimeOffset ApprovedAt);

/// <summary>
/// 电脑端开的一次「连接」会话 —— 用户点了【连接电脑/手机】，屏幕上有一张二维码。
/// </summary>
/// <remarks>
/// <para>
/// <b>同一时刻只有一张二维码有效</b>：再点一次【连接电脑/手机】就换一张新的，
/// 旧的那张连同它下面那些还没决定的请求一起作废。
/// 这样"屏幕上那张码"与"能被批准的请求"永远是一一对应的 ——
/// 否则屏幕上是新码、待批准列表里还挂着上一轮的请求，用户根本分不清自己在批谁。
/// </para>
/// <para>
/// <b>待批准请求与会话都不落盘</b>，只在内存里。它们最多活几分钟，
/// 重启后手机重扫一次即可（手机会一直轮询）。落盘反而要处理「重启后一堆过期请求」。
/// </para>
/// </remarks>
/// <param name="Token">二维码里那一串。**一次性、短时效**。</param>
public sealed record EnrollSession(string Token, DateTimeOffset OpenedAt);

/// <summary>一台设备这次连接请求的处置。</summary>
public enum EnrollDecision
{
    /// <summary>还没决定 —— 等用户在电脑端那个弹窗上点。</summary>
    Pending,

    /// <summary>用户点了同意。</summary>
    Approved,

    /// <summary>用户点了拒绝。**手机端必须看得见**（不能一直转圈）。</summary>
    Rejected,
}

/// <summary>
/// 一条等待人工批准的入网请求（规格 §3.4.5）。
/// </summary>
/// <param name="Decision">
/// 用户在电脑端弹窗上的处置。⚠️ **手机轮询时不能把它重置回 <see cref="EnrollDecision.Pending"/>** ——
/// 那会让"刚点过同意"在下一轮轮询里消失，表现是「点了同意，手机还在等」。
/// </param>
public sealed record PendingEnrollment(
    string DeviceId,
    string DeviceName,
    DateTimeOffset RequestedAt,
    EnrollDecision Decision = EnrollDecision.Pending);

/// <summary>
/// 一条等待人工批准的**改名**请求（规格 §3.4.5 ③）。
/// </summary>
/// <remarks>
/// <para>
/// 需求方 2026-09-24 原话：「……用户编写确认后，电脑端显示手机端的名字，
/// 并且以后该手机端连接电脑端都是这个名字，<b>如需要再次更改，需要电脑端同意才能更改</b>。」
/// </para>
/// <para>
/// ⚠️ <b>刻意不复用 <see cref="PendingEnrollment"/> 那个集合</b>：入网的待批准
/// 是绑在**屏幕上那张二维码**的生命周期上的（换一张码就把它们全清掉），
/// 而改名**没有二维码** —— 它靠的是设备自己的凭据。混在一起的话，
/// 「用户又点了一次【连接电脑/手机】」会把一条正在等的改名请求**一起清掉**，
/// 而手机上还在轮询，看不出为什么。
/// </para>
/// <para>
/// ⚠️ 弹窗那一层**复用同一套通道**（规格原话「走同一套弹窗通道」）——
/// 界面上它和入网请求长得一样，只是文案不同。
/// </para>
/// </remarks>
/// <param name="Decision">用户在电脑端弹窗上的处置（与入网共用同一个枚举）。</param>
public sealed record PendingRename(
    string DeviceId,
    string DeviceName,
    DateTimeOffset RequestedAt,
    EnrollDecision Decision = EnrollDecision.Pending);

/// <summary>入网的结果。</summary>
public enum EnrollStatus
{
    /// <summary>请求已收到，等主机端人工批准。</summary>
    Pending,

    /// <summary>已批准，凭据已签发（**只在这一次返回**）。</summary>
    Approved,

    /// <summary>用户拒绝了这次连接。</summary>
    Rejected,

    /// <summary>令牌不对（不是这张码里的、已过期、或已经被用掉）。</summary>
    BadToken,

    /// <summary>没有这条待批准的请求（没发起过、屏幕上的码已经换了、或凭据已经被领走过）。</summary>
    NoPendingRequest,

    /// <summary>
    /// 机位满了，这台手机接不进来（`docs/04-许可设计.md` §5.1）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>只挡「新的接入」</b>：已经在录的手机照常上传，电脑端自己的录制与
    /// 检索回放更是一条都不看它（L5 / L8）。
    /// </remarks>
    SeatLimitExceeded,
}

/// <param name="Credential">仅 <see cref="EnrollStatus.Approved"/> 时非空。</param>
public sealed record ClaimResult(EnrollStatus Status, string? Credential, string? Detail);

/// <summary>
/// 已入网设备、待批准请求、以及"屏幕上那张二维码"的登记簿（规格 §3.4.5）。
/// </summary>
/// <remarks>
/// <para>
/// <b>凭据是逐台签发的，不在安装包里</b>（端间契约 §1.1 硬约束：客户端绝不内置 secret）。
/// </para>
/// <para>
/// <b>为什么要有令牌，而不是「一请求就把凭据发出去」</b>：如果凭据只凭 <c>deviceId</c>
/// 发放，那么局域网里任何一台机器只要拿到 <c>deviceId</c>，就能在批准的瞬间把凭据领走 ——
/// 那时「人工批准」就只是屏幕上多了一个按钮，它什么都没挡住。
/// </para>
/// <para>
/// <b>2026-09-24 起：6 位配对码换成二维码里的令牌</b>（需求方决定）。前提**一个字没放松** ——
/// 令牌只出现在**电脑端屏幕上那张二维码**里，扫不到就进不来。
/// 换掉的只是"用户把码读出来敲进手机"这一步，改成扫一下。
/// </para>
/// <para>
/// ⚠️ <b>令牌是明文走在局域网里的</b>（和原来的 6 位码一样，都是明文 HTTP）。
/// 所以这一层挡的是「不在屏幕前的人」，**不挡**"同在局域网里抓包的人"。与旧实现没有变化。
/// </para>
/// <para>
/// <b>会话与待批准请求都不落盘</b>，只在内存里（见 <see cref="EnrollSession"/> 的说明）。
/// </para>
/// </remarks>
public sealed class DeviceRegistry
{
    /// <summary>二维码里那个令牌的有效期。**沿用原配对码的 5 分钟**，不另起一套。</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions DiskOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly IAppLogger _logger;
    private readonly Func<int>? _seatLimit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, PendingEnrollment> _pending = new(StringComparer.Ordinal);

    /// <summary>屏幕上那张二维码对应的会话；没开时为 null。</summary>
    private EnrollSession? _session;

    /// <summary>等人工批准的改名请求（规格 §3.4.5 ③）。**与入网那批分开**，见 <see cref="PendingRename"/>。</summary>
    private readonly Dictionary<string, PendingRename> _renamePending = new(StringComparer.Ordinal);

    /// <param name="seatLimit">
    /// 这个激活码允许接入几台手机端（`docs/04-许可设计.md` §5.1）。
    /// <b>传 <see langword="null"/> 表示不限</b> —— 只有测试会那么传；
    /// 生产路径在 <c>DesktopServices</c> 里接的就是许可的档位，**未激活时是 0**。
    /// <para>
    /// ⚠️ 它是**函数**而不是一个数：许可状态要等启动校验跑完才落地，
    /// 而登记簿在装配那一刻就建好了。
    /// </para>
    /// </param>
    public DeviceRegistry(
        string path,
        Func<DateTimeOffset>? now = null,
        IAppLogger? logger = null,
        Func<int>? seatLimit = null)
    {
        _path = path;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _logger = logger ?? NullLogger.Instance;
        _seatLimit = seatLimit;
    }

    /// <summary>
    /// 开一次连接会话（用户点了【连接电脑/手机】），返回要写进二维码的那张令牌。
    /// </summary>
    /// <remarks>
    /// <b>会把上一张码连同它下面那些没决定的请求一起作废</b> ——
    /// 见 <see cref="EnrollSession"/> 的说明，这是刻意的。
    /// </remarks>
    public async Task<EnrollSession> OpenSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _pending.Clear();
            _session = new EnrollSession(NewToken(), _now());
            return _session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>屏幕上那张码还在不在；过期就当没有。</summary>
    public async Task<EnrollSession?> SessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return Live();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>关掉会话（用户点了【关掉】或者领走凭据之后）。</summary>
    public async Task CloseSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _session = null;
            _pending.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 记下一条入网请求，并**告诉它现在批没批**（手机扫到码之后调的那个）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>手机轮询的是这个方法，不是 <see cref="ClaimAsync"/></b>。
    /// 两件事分开是为了让"问"和"领"各只有一个意思：
    /// 这个方法**只报警不发货**（一个字节的凭据都不产出，所以轮询它没有副作用），
    /// <see cref="ClaimAsync"/> 才是真的来领凭据的那一下。
    /// 合成一个的话，手机上"我还在等"的那几轮轮询每次都在调一个会发货的接口。
    /// </para>
    /// <para>
    /// 因为手机要反复调，同一个 <paramref name="deviceId"/> 会被反复送进来。三条约束：
    /// </para>
    /// <list type="number">
    /// <item><b>不重置 <see cref="PendingEnrollment.Decision"/></b> —— 重置了就是
    /// 「点了同意，手机还在等」；</item>
    /// <item><b>不重置 <see cref="PendingEnrollment.RequestedAt"/></b> —— 那是这条请求的
    /// 时间基准，每次轮询都刷新的话它可以被无限续命；</item>
    /// <item>设备名**可以**刷新（用户可能在这中间改了机位名）。</item>
    /// </list>
    /// </remarks>
    /// <returns>
    /// <see cref="EnrollStatus.Pending"/>（还没批）/ <see cref="EnrollStatus.Approved"/>
    /// （批了，去 <see cref="ClaimAsync"/> 领）/ <see cref="EnrollStatus.Rejected"/>（被拒了）。
    /// </returns>
    public async Task<ClaimResult> RequestAsync(
        string deviceId,
        string deviceName,
        string? token,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = Live();
            if (session is null)
            {
                // 屏幕上的码换了、或者已经超时 —— 手机该重新扫一次。
                return new ClaimResult(EnrollStatus.NoPendingRequest, null, "电脑端屏幕上现在没有可用的二维码，请重新扫码");
            }

            if (!FixedTimeEquals(session.Token, token ?? string.Empty))
            {
                return new ClaimResult(EnrollStatus.BadToken, null, "这个令牌不对（不是屏幕上那张码里的，或者已经过期）");
            }

            // 机位满了就**不记这条请求**：记了的话它会出现在电脑端的弹窗上，
            // 用户点同意，然后手机才被告知接不进来 —— 让用户白点一次。
            if (await SeatBlockedAsync(deviceId, cancellationToken) is { } blocked)
            {
                return blocked;
            }

            var existing = _pending.TryGetValue(deviceId, out var previous) ? previous : null;
            var decision = existing?.Decision ?? EnrollDecision.Pending;

            _pending[deviceId] = new PendingEnrollment(
                deviceId,
                // ⚠️ **入口就截**（规格 §3.4.3 ②：上限属于「机位名」这个字段，
                // 不只属于手机端那个输入框）。
                // 不截的话，电脑端的待批准弹窗与设备表里会显示一个超长的名字，
                // 而批准之后落盘的那份是截过的 —— 用户看到的两处不一样。
                DeviceNameRules.Clamp(deviceName),
                existing?.RequestedAt ?? _now(),
                decision);

            var status = decision switch
            {
                EnrollDecision.Approved => EnrollStatus.Approved,
                EnrollDecision.Rejected => EnrollStatus.Rejected,
                _ => EnrollStatus.Pending,
            };

            return new ClaimResult(status, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 用户在电脑端弹窗上点了同意 / 拒绝。
    /// </summary>
    /// <returns>没找到这条待批准请求时返回 <see langword="false"/>（它可能刚被领走、或被新开的码作废了）。</returns>
    public async Task<bool> DecideAsync(
        string deviceId,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_pending.TryGetValue(deviceId, out var pending))
            {
                return false;
            }

            _pending[deviceId] = pending with
            {
                Decision = approved ? EnrollDecision.Approved : EnrollDecision.Rejected,
            };

            // 入网决策是**安全事件**：谁被放进来了、谁被挡在外面了，要留痕。
            // ⚠️ 只记 deviceId —— 凭据与令牌**绝不进日志**（见 Sanitizer）。
            _logger.Log(LogLevel.Info, "入网", approved ? "同意了一台设备连接" : "拒绝了一台设备连接",
                new Dictionary<string, object?>
                {
                    ["deviceId"] = deviceId,
                    ["设备名"] = pending.DeviceName,
                });

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>拿令牌换凭据。**凭据只在这一条路径上产出，且只发放一次。**</summary>
    public async Task<ClaimResult> ClaimAsync(
        string deviceId,
        string? token,
        CancellationToken cancellationToken = default)
    {
        PendingEnrollment pending;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = Live();
            if (session is null)
            {
                return new ClaimResult(EnrollStatus.NoPendingRequest, null, "电脑端屏幕上现在没有可用的二维码，请重新扫码");
            }

            if (!FixedTimeEquals(session.Token, token ?? string.Empty))
            {
                return new ClaimResult(EnrollStatus.BadToken, null, "这个令牌不对（不是屏幕上那张码里的，或者已经过期）");
            }

            if (!_pending.TryGetValue(deviceId, out var found))
            {
                return new ClaimResult(EnrollStatus.NoPendingRequest, null, "没有待批准的入网请求");
            }

            switch (found.Decision)
            {
                case EnrollDecision.Rejected:
                    // ⚠️ **故意留着这条，不清**：手机是轮询的，清了的话它下一次轮询
                    // 拿到的是「没有待批准请求」，而那句话在界面上是"重新扫一次码" ——
                    // 用户明明刚被拒绝，却看到一句叫他再试一次的话。
                    // 留着才能一直回到「电脑端拒绝了这次连接」，直到换一张码为止。
                    return new ClaimResult(EnrollStatus.Rejected, null, "电脑端拒绝了这次连接");

                case EnrollDecision.Pending:
                    // 还没批。**不是错误** —— 手机照着这个继续等。
                    return new ClaimResult(EnrollStatus.Pending, null, "还在等电脑端批准");

                default:
                    pending = found;
                    break;
            }

            // 领走了就销毁：凭据只发放一次。整张码也一起作废 ——
            // 一张码只服务一次入网，接着再扫的应该是新开的那张。
            _pending.Remove(deviceId);
            _session = null;
        }
        finally
        {
            _gate.Release();
        }

        var credential = NewCredential();
        await AppendAsync(new EnrolledDevice(deviceId, pending.DeviceName, credential, _now()), cancellationToken);

        // 签发了凭据 = 这台设备从此能上传。留痕，但**绝不记凭据本身**。
        _logger.Log(LogLevel.Info, "入网", "签发了设备凭据", new Dictionary<string, object?>
        {
            ["deviceId"] = deviceId,
            ["设备名"] = pending.DeviceName,
        });

        return new ClaimResult(EnrollStatus.Approved, credential, null);
    }

    /// <summary>
    /// 手机端请求改名（规格 §3.4.5 ③：「如需要再次更改，需要电脑端同意才能更改」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>用**凭据**证明身份</b>，不是用令牌 —— 改名发生在入网**之后**，
    /// 那时手机手上只有凭据。与 <c>upload/*</c> 那几条同一个信任模型
    /// （「身份从凭据来」，见 <see cref="FindByCredentialAsync"/> 的说明）。
    /// </para>
    /// <para>
    /// ⚠️ 同一台设备反复调（手机要轮询）时<b>不重置已有的决定</b> ——
    /// 与 <see cref="RequestAsync"/> 那条同一个理由：重置了就是「点了同意，
    /// 手机还在等」。但**名字可以刷新**（用户可能又改了一次，以最后一次为准）。
    /// </para>
    /// </remarks>
    public async Task<ClaimResult> RequestRenameAsync(
        string deviceId,
        string newName,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        var device = await FindByCredentialAsync(credential ?? string.Empty, cancellationToken);

        if (device is null || !string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal))
        {
            // ⚠️ 凭据对不上 ⇒ 这台设备没入过网、或凭据已作废。
            // 回 `BadToken` 那个码：手机端照它「重新配对」。
            return new ClaimResult(EnrollStatus.BadToken, null, "凭据无效，改名前要重新配对");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = _renamePending.TryGetValue(deviceId, out var previous) ? previous : null;

            _renamePending[deviceId] = new PendingRename(
                deviceId,
                // ⚠️ 入口就量尺（与入网那条路同一个函数）—— 规格 §3.4.3 ②
                // 「从**任何路径**写进来的名字都得是同一把尺子」。
                DeviceNameRules.Clamp(newName),
                existing?.RequestedAt ?? _now(),
                existing?.Decision ?? EnrollDecision.Pending);

            return new ClaimResult(
                _renamePending[deviceId].Decision switch
                {
                    EnrollDecision.Approved => EnrollStatus.Approved,
                    EnrollDecision.Rejected => EnrollStatus.Rejected,
                    _ => EnrollStatus.Pending,
                },
                null,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>当前待批准 / 已处置的改名请求（界面要列出来）。</summary>
    public async Task<IReadOnlyList<PendingRename>> PendingRenamesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _renamePending.Values.OrderBy(r => r.RequestedAt).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 用户在电脑端弹窗上点了同意 / 拒绝（改名）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 批准时**用同一个凭据**追加一条新名字的记录 —— **不重新签发凭据**：
    /// 改名不是重新入网，换凭据会让那台手机下一次上传撞 401，
    /// 而它以为自己只是改了个名字（表现是「改完名字就传不上来了」）。
    /// </remarks>
    public async Task<bool> DecideRenameAsync(
        string deviceId,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        PendingRename pending;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_renamePending.TryGetValue(deviceId, out var found))
            {
                return false;
            }

            pending = found with
            {
                Decision = approved ? EnrollDecision.Approved : EnrollDecision.Rejected,
            };

            _renamePending[deviceId] = pending;
        }
        finally
        {
            _gate.Release();
        }

        if (approved && await FindAsync(deviceId, cancellationToken) is { } device)
        {
            // 追加一条同凭据、新名字 —— `LatestAsync` 取最后一条，名字就此生效。
            await AppendAsync(device with { DeviceName = pending.DeviceName }, cancellationToken);
        }

        _logger.Log(LogLevel.Info, "入网", approved ? "同意了一次改名" : "拒绝了一次改名",
            new Dictionary<string, object?>
            {
                ["deviceId"] = deviceId,
                ["设备名"] = pending.DeviceName,
            });

        return true;
    }

    /// <summary>
    /// 这台设备还能不能占一个机位；占不到就返回那条要回给手机的处置。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>已经入网过的设备一律放行</b>：它占的是**已有的**那个机位，
    /// 重新扫一次码只是换一份凭据（见 <see cref="LatestAsync"/>），不是新接一台手机。
    /// 不放行的话，「机位满了之后手机丢了要重配」会变成死局 ——
    /// 而用户手里那个激活码明明是够的。
    /// </para>
    /// <para>
    /// ⚠️ <b>只在 <see cref="RequestAsync"/> 那一处查</b>，签发凭据的
    /// <see cref="ClaimAsync"/> 那边**不重复查**。这不是漏了一道，而是那道查不到东西：
    /// 一张码只服务一次入网（领走凭据时 <c>_session</c> 就被置空），
    /// 所以「批准与领取之间被插队」这个口子根本不存在 —— 同一张码下第二台设备
    /// 去领的时候拿到的是「没有待批准的请求」，压根走不到签发那一步。
    /// </para>
    /// <para>
    /// 更要紧的是：查不到的防线**写不出会红的测试**，而这个仓的规矩是
    /// 每一条防线都得有一条能证明它拦得住东西的测试（见 <c>docs/实现决策.md</c>）。
    /// 将来真要让一张码服务多台设备，**那时**必须把这道查搬到 <see cref="ClaimAsync"/> 去
    /// —— 搬的理由是那句「一张码只服务一次」不再成立了。
    /// </para>
    /// <para>
    /// <b>未激活</b>（机位数 0）与「档位用满」走的是同一句话：对这台手机来说，
    /// 事情是一样的 —— 没有一个空机位给它。原因在电脑端界面上说（那里能说清是没激活还是满了）。
    /// </para>
    /// </remarks>
    private async Task<ClaimResult?> SeatBlockedAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (_seatLimit is null)
        {
            return null;
        }

        var enrolled = await LatestAsync(cancellationToken);

        if (enrolled.Any(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal)))
        {
            return null;
        }

        var limit = _seatLimit();

        // 这台接进去之后占几个 —— 拿**接进去之后**的数比，而不是拿现在的数。
        if (SeatUsage.For(enrolled.Count + 1, SeatUsage.Cameras) <= limit)
        {
            return null;
        }

        _logger.Log(LogLevel.Warn, "入网", "机位已满，挡下了一台设备",
            new Dictionary<string, object?>
            {
                ["机位"] = limit,
                ["已接入"] = enrolled.Count,
            });

        return new ClaimResult(
            EnrollStatus.SeatLimitExceeded,
            null,
            $"电脑端没有空余机位了（这个激活码允许 {limit} 台手机端接入，已经接了 {enrolled.Count} 台）。"
            + "要在电脑端的【许可】里激活或升级，或者联系提供方。");
    }

    /// <summary>屏幕上那张码还活着吗；过期就顺手收掉。</summary>
    /// <remarks>**调用方必须已经持有 <c>_gate</c>** —— 它读写 <c>_session</c>。</remarks>
    private EnrollSession? Live()
    {
        if (_session is null)
        {
            return null;
        }

        if (_now() - _session.OpenedAt > SessionLifetime)
        {
            _session = null;
            _pending.Clear();
            return null;
        }

        return _session;
    }

    /// <summary>查这台设备的凭据；没入网过返回 <see langword="null"/>。</summary>
    /// <remarks>
    /// 每次请求都读一遍文件。<c>ponytail:</c> 这是 O(文件行数)，但设备数是「一个打包间几台手机」
    /// 的量级，而同一个请求里还要搬 4 MiB 的分片 —— 不值得为它加一层缓存与失效逻辑。
    /// 真要是设备多到读文件成了瓶颈，那时候再加。
    /// </remarks>
    public async Task<EnrolledDevice?> FindAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var all = await LatestAsync(cancellationToken);

        return all.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 凭据反查设备。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是「身份从凭据来」的落点。</b> 报文里的 <c>sourceDeviceId</c> 只是发送方**自称**，
    /// 信它的话，任何一台已入网的设备都能把别人的 id 写进索引。所以要用凭据反查，
    /// 再拿查出来的 id 去比对。
    /// </para>
    /// <para>
    /// 比较是定长的，且**只认每台设备最新那一份凭据** —— 重新入网换的新凭据一旦生效，
    /// 旧的就作废（不是两份都还能用，那等于重新入网没起到作用）。
    /// </para>
    /// </remarks>
    public async Task<EnrolledDevice?> FindByCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        var all = await LatestAsync(cancellationToken);
        var target = Encoding.UTF8.GetBytes(credential);

        EnrolledDevice? found = null;

        // 不提前 return：命中位置固定的话，从耗时上能看出「前几台不是」。
        // 设备数是个位数，扫完不亏。
        foreach (var device in all)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(device.Credential), target))
            {
                found ??= device;
            }
        }

        return found;
    }

    /// <summary>每台设备最新那一条。**重新入网换掉凭据**就是靠这里生效的。</summary>
    private async Task<List<EnrolledDevice>> LatestAsync(CancellationToken cancellationToken)
    {
        var all = await LoadAllAsync(cancellationToken);

        var latest = new Dictionary<string, EnrolledDevice>(StringComparer.Ordinal);
        foreach (var device in all)
        {
            // 后写的赢。
            latest[device.DeviceId] = device;
        }

        return [.. latest.Values];
    }

    /// <summary>当前待批准 / 已在等待配对的请求（界面要列出来给用户看）。</summary>
    public async Task<IReadOnlyList<PendingEnrollment>> PendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // 码过期 / 被换掉时，它下面那些请求一起作废 —— 这里顺手收干净，
            // 免得界面列出一堆连令牌都失效了的幽灵请求。
            if (Live() is null)
            {
                return [];
            }

            return _pending.Values.OrderBy(p => p.RequestedAt).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>已入网设备（不含凭据 —— 界面不该拿到它）。</summary>
    public async Task<IReadOnlyList<(string DeviceId, string DeviceName, DateTimeOffset ApprovedAt)>> DevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var all = await LatestAsync(cancellationToken);

        return all
            .Select(d => (d.DeviceId, d.DeviceName, d.ApprovedAt))
            .OrderBy(d => d.ApprovedAt)
            .ToList();
    }

    private async Task<IReadOnlyList<EnrolledDevice>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var result = new List<EnrolledDevice>();

        foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var dto = JsonSerializer.Deserialize<EnrolledDeviceDto>(line, DiskOptions);
                if (dto?.DeviceId is { Length: > 0 } id && dto.Credential is { Length: > 0 } credential)
                {
                    // 读回来的凭据也登记给脱敏层 —— 这一句覆盖的是**这次改动之前
                    // 就已经发出去的那些**（`NewCredential` 那处只覆盖新签发的）。
                    Sanitizer.RegisterSecret(credential);

                    result.Add(new EnrolledDevice(
                        id,
                        // ⚠️ **读盘这一处也要量**（与前两处同一个函数）——
                        // 它挡的是**这次改动之前就写进去的**那些：
                        // 手改过的登记簿、旧版本存下的长名字。
                        // 只量写入端的话，那些老数据会一路显示到界面上（撑破布局）。
                        DeviceNameRules.Clamp(dto.DeviceName ?? string.Empty),
                        credential,
                        dto.ApprovedAt));
                }
            }
            catch (JsonException)
            {
                // 坏行跳过 —— 与索引、打点、标签同一条规矩：
                // 一行坏掉不该让整份登记簿读不出来，那会让所有已入网设备一起失效。
            }
        }

        return result;
    }

    private async Task AppendAsync(EnrolledDevice device, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonSerializer.Serialize(
                new EnrolledDeviceDto(
                    device.DeviceId,
                    // ⚠️ **落盘这一处也要截**（与 `RequestAsync` 那处**同一个函数**，
                    // 不是第二份实现）—— 规格 §3.4.3 ② 点名「从**任何路径**写进来的
                    // 名字都得是同一把尺子」：手改过的配置文件、旧版本存下的长名字、
                    // 以及将来新加的任何入口，都要在这里被同一把尺子量过。
                    DeviceNameRules.Clamp(device.DeviceName),
                    device.Credential,
                    device.ApprovedAt),
                DiskOptions);

            await File.AppendAllTextAsync(
                _path,
                line + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 二维码里那个令牌。
    /// </summary>
    /// <remarks>
    /// <b>16 字节而不是 6 位数字</b>：原来那个 6 位码是被人**读出来敲进去**的，
    /// 短才能用；现在它是被**扫**的，没人需要念出来 —— 那就没有理由便宜它。
    /// （6 位码是 10⁶ 量级，得靠"连错 5 次作废"兜着；128 位不需要那道兜底。）
    /// </remarks>
    /// <remarks>
    /// ⚠️ 生成的那一刻顺手**登记给脱敏层**（<see cref="Sanitizer"/>）：
    /// 这两个串永远不该出现在日志里，而诊断包会把 <c>logs/*</c> 整个打包外发。
    /// 放在这里而不是调用点 —— 调用点将来会变多，**漏一处的表现是凭据静默落盘**。
    /// </remarks>
    private static string NewToken() => Register(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)));

    private static string NewCredential() =>
        Register(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));

    private static string Register(string secret)
    {
        Sanitizer.RegisterSecret(secret);
        return secret;
    }

    /// <summary>定长比较，不因为「第几位开始不一样」而泄露时间。</summary>
    private static bool FixedTimeEquals(string expected, string actual)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual);

        // 长度不同直接 false —— 令牌是定长的，长度本身不是秘密。
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private sealed record EnrolledDeviceDto(
        string DeviceId,
        string DeviceName,
        string Credential,
        DateTimeOffset ApprovedAt);
}
