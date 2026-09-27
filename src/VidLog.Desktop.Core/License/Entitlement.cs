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
/// <param name="Activated">激活了没有（启动时那次校验的结论）。</param>
/// <param name="Slots">允许接入的手机端台数。</param>
/// <param name="FailureReason">没激活 / 校验失败的原因（**给用户看的那句话**）。</param>
/// <param name="MachineCode">本机机器码（给用户抄给卖家用）。</param>
/// <param name="Degraded">机器码有降级（某一段读不到）。</param>
public sealed record LicenseStatus(
    bool Activated,
    int Slots,
    string? FailureReason,
    string MachineCode,
    bool Degraded)
{
    /// <summary>能不能录 —— **只挡住新录**（L5）。</summary>
    public bool CanRecord => Activated;
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

    private LicenseStatus _status;

    public LicenseService(
        LicenseVerifier verifier,
        EntitlementStore store,
        MachineIdentity identity,
        IAppLogger? logger = null)
    {
        _verifier = verifier;
        _store = store;
        _identity = identity;
        _logger = logger ?? NullLogger.Instance;

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

        _status = new LicenseStatus(
            true, check.Payload.Slots, null, _identity.Display, _identity.IsDegraded);

        _logger.Log(LogLevel.Info, "许可", $"已激活：{check.Payload.Slots} 机位",
            new Dictionary<string, object?> { ["序号"] = check.Payload.Serial });

        return _status;
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

        _status = new LicenseStatus(
            true, check.Payload.Slots, null, _identity.Display, _identity.IsDegraded);

        _logger.Log(LogLevel.Info, "许可", $"激活成功：{check.Payload.Slots} 机位");

        return _status;
    }
}
