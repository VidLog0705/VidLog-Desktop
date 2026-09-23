using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
/// 一条等待人工批准的入网请求（规格 §3.4.5）。
/// </summary>
/// <param name="Code">
/// 6 位配对码。**显示在电脑端界面上**，由用户读出来敲进手机 —— 见
/// <see cref="DeviceRegistry.RequestAsync"/> 的说明，那是「人工批准」真的挡住东西的地方。
/// </param>
public sealed record PendingEnrollment(
    string DeviceId,
    string DeviceName,
    string Code,
    DateTimeOffset RequestedAt,
    int FailedAttempts = 0);

/// <summary>入网的结果。</summary>
public enum EnrollStatus
{
    /// <summary>请求已收到，等主机端人工批准。</summary>
    Pending,

    /// <summary>配对码正确，凭据已签发（**只在这一次返回**）。</summary>
    Approved,

    /// <summary>配对码不对。</summary>
    BadCode,

    /// <summary>没有这条待批准的请求（没发起过、已过期、或凭据已经被领走过）。</summary>
    NoPendingRequest,
}

/// <param name="Credential">仅 <see cref="EnrollStatus.Approved"/> 时非空。</param>
public sealed record ClaimResult(EnrollStatus Status, string? Credential, string? Detail);

/// <summary>
/// 已入网设备与待批准请求的登记簿（规格 §3.4.5）。
/// </summary>
/// <remarks>
/// <para>
/// <b>凭据是逐台签发的，不在安装包里</b>（端间契约 §1.1 硬约束：客户端绝不内置 secret）。
/// </para>
/// <para>
/// <b>为什么要有配对码，而不是「批准了凭据就直接发」</b>：如果凭据只凭 <c>deviceId</c>
/// 发放，那么局域网里任何一台机器只要拿到 <c>deviceId</c>，就能在批准的瞬间把凭据领走 ——
/// 那时「人工批准」就只是屏幕上多了一个按钮，它什么都没挡住。
/// 配对码让这个动作真的需要一个**只有站在主机屏幕前的人才知道**的东西。
/// </para>
/// <para>
/// <b>待批准请求不落盘</b>，只在内存里。它 5 分钟就过期，重启后手机重发一次即可
/// （手机会一直轮询）。落盘反而要处理「重启后一堆过期请求」的清理。
/// </para>
/// </remarks>
public sealed class DeviceRegistry
{
    /// <summary>配对码的有效期。</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>同一条请求最多试错多少次就作废。</summary>
    /// <remarks>
    /// 6 位码是 10⁶ 量级。没有次数限制的话，局域网里跑一晚上足够撞开。
    /// </remarks>
    public const int MaxFailedAttempts = 5;

    private static readonly JsonSerializerOptions DiskOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, PendingEnrollment> _pending = new(StringComparer.Ordinal);

    public DeviceRegistry(string path, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 记下一条入网请求，返回它对应的待批准记录（含配对码，供界面显示）。
    /// </summary>
    /// <remarks>
    /// 同一个 <paramref name="deviceId"/> 重复发起会**换一个新码并重置试错计数** ——
    /// 手机轮询时反复调这个方法（它不知道电脑端批没批），
    /// 沿用旧码的话，用户刚读到的那个码会在下一轮轮询里失效。
    /// </remarks>
    public async Task<PendingEnrollment> RequestAsync(
        string deviceId,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pending = new PendingEnrollment(
                deviceId,
                deviceName,
                NextCode(),
                _now());

            _pending[deviceId] = pending;
            return pending;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>拿配对码换凭据。**凭据只在这一条路径上产出，且只发放一次。**</summary>
    public async Task<ClaimResult> ClaimAsync(
        string deviceId,
        string? code,
        CancellationToken cancellationToken = default)
    {
        PendingEnrollment? pending;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_pending.TryGetValue(deviceId, out pending))
            {
                return new ClaimResult(EnrollStatus.NoPendingRequest, null, "没有待批准的入网请求");
            }

            if (_now() - pending.RequestedAt > CodeLifetime)
            {
                _pending.Remove(deviceId);
                return new ClaimResult(
                    EnrollStatus.NoPendingRequest, null, $"配对码已超过 {CodeLifetime.TotalMinutes:0} 分钟，请重新发起入网");
            }

            if (!FixedTimeEquals(pending.Code, code ?? string.Empty))
            {
                var attempts = pending.FailedAttempts + 1;
                if (attempts >= MaxFailedAttempts)
                {
                    // 作废，要重新走一遍入网。不这样的话就是敞开让人暴力撞码。
                    _pending.Remove(deviceId);
                    return new ClaimResult(
                        EnrollStatus.NoPendingRequest, null, $"配对码连错 {MaxFailedAttempts} 次，这次入网已作废，请重发入网请求");
                }

                _pending[deviceId] = pending with { FailedAttempts = attempts };
                return new ClaimResult(
                    EnrollStatus.BadCode, null, $"配对码不对（还可以试 {MaxFailedAttempts - attempts} 次）");
            }

            // 领走了就销毁：凭据只发放一次。
            _pending.Remove(deviceId);
        }
        finally
        {
            _gate.Release();
        }

        var credential = NewCredential();
        await AppendAsync(new EnrolledDevice(deviceId, pending.DeviceName, credential, _now()), cancellationToken);

        return new ClaimResult(EnrollStatus.Approved, credential, null);
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
            var now = _now();
            foreach (var stale in _pending.Values.Where(p => now - p.RequestedAt > CodeLifetime).ToList())
            {
                _pending.Remove(stale.DeviceId);
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
                    result.Add(new EnrolledDevice(id, dto.DeviceName ?? string.Empty, credential, dto.ApprovedAt));
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
                    device.DeviceName,
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

    private static string NextCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    private static string NewCredential() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>定长比较，不因为「第几位开始不一样」而泄露时间。</summary>
    private static bool FixedTimeEquals(string expected, string actual)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual);

        // 长度不同直接 false —— 配对码是定长的，长度本身不是秘密。
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private sealed record EnrolledDeviceDto(
        string DeviceId,
        string DeviceName,
        string Credential,
        DateTimeOffset ApprovedAt);
}
