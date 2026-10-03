using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.License;

/// <summary>试用记录的一条（`docs/04-许可设计.md` §6.3）。</summary>
/// <param name="FirstRunAt">首次激活试用码的时刻 —— **判定取的是所有记录里最早的那个**。</param>
/// <param name="MaxSeenAt">见过的最大时刻，**只增不减**（§6.4 的防回拨就靠它）。</param>
public sealed record TrialRecord(DateTimeOffset FirstRunAt, DateTimeOffset MaxSeenAt)
{
    /// <summary>校验尾码（§6.3）。</summary>
    /// <remarks>
    /// ⚠️ 它挡的是「用记事本把 <see cref="FirstRunAt"/> 改到未来」这**一种**改法，
    /// **不是密码学保护** —— 盐就硬编码在这儿。规格原话：「提高门槛，不是密码学保护」。
    /// </remarks>
    public string Tail => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(
            $"{TrialRecordStore.Salt}|{FirstRunAt.UtcTicks}|{MaxSeenAt.UtcTicks}")))[..16];
}

/// <summary>试用走到哪一步了。</summary>
public enum TrialPhase
{
    /// <summary>还在 168 小时里。</summary>
    Running,

    /// <summary>168 小时走完了。</summary>
    Ended,
}

/// <summary>一次试用判定的结论。</summary>
/// <param name="Phase">还在试用里，还是已经走完。</param>
/// <param name="Remaining">还剩多久（<see cref="TrialPhase.Ended"/> 时是 0）。</param>
/// <param name="FirstRunAt">起算点（所有记录里最早的那个）。</param>
public sealed record TrialState(TrialPhase Phase, TimeSpan Remaining, DateTimeOffset FirstRunAt);

/// <summary>
/// 试用记录的**一个**存放位置（§6.3 那四处里的任意一处）。
/// </summary>
/// <remarks>
/// ⚠️ 做成接口只为一件事：**测试不许碰真注册表、也不许写用户的真数据目录**。
/// 生产路径上四处的位置都在 <see cref="TrialRecordStore.ForThisMachine"/> 里钉死。
/// </remarks>
public interface ITrialLocation
{
    /// <summary>给人看的名字（写日志用，**不含路径细节**）。</summary>
    string Name { get; }

    /// <summary>读一条；这个位置没有 / 读不动 / 尾码对不上 → <see langword="null"/>。</summary>
    TrialRecord? Read();

    /// <summary>写一条。**写不进去要抛**，由调用方降级 —— 别在这里吞掉。</summary>
    void Write(TrialRecord record);
}

/// <summary>试用记录存在一个文件里（§6.3 的位置 ① ②）。</summary>
public sealed class FileTrialLocation(string name, string filePath) : ITrialLocation
{
    public string Name { get; } = name;

    public string FilePath { get; } = filePath;

    public TrialRecord? Read() =>
        File.Exists(FilePath) ? TrialRecordStore.Parse(File.ReadAllText(FilePath)) : null;

    public void Write(TrialRecord record)
    {
        var directory = Path.GetDirectoryName(FilePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(FilePath, TrialRecordStore.Serialize(record));
    }
}

/// <summary>试用记录存在注册表里（§6.3 的位置 ③ ④）。</summary>
/// <remarks>
/// ⚠️ <b>每处一个值，不是三个值</b> —— 三处分开写的话，读到「新 firstRunAt + 旧 maxSeenAt」
/// 那种半截状态就说不清该信谁了。一条字符串要么整条对、要么整条不算数（尾码管这个）。
/// </remarks>
public sealed class RegistryTrialLocation(string name, RegistryKey root) : ITrialLocation
{
    private const string SubKeyPath = @"Software\VidLog\trial";

    private const string ValueName = "record";

    public string Name { get; } = name;

    public TrialRecord? Read()
    {
        // ⚠️ 这两行守卫不是形式主义：本项目的 Core 是**纯 net9.0**（不是 net9.0-windows），
        //    注册表 API 在那儿是「仅 Windows 支持」，少了守卫 build 会报 CA1416，
        //    而本仓是 0 警告。
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = root.OpenSubKey(SubKeyPath, writable: false);

        return key?.GetValue(ValueName) is string text ? TrialRecordStore.Parse(text) : null;
    }

    public void Write(TrialRecord record)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = root.CreateSubKey(SubKeyPath, writable: true);

        key.SetValue(ValueName, TrialRecordStore.Serialize(record), RegistryValueKind.String);
    }
}

/// <summary>
/// 试用记录的四路交叉读写与判定（§6.3 / §6.4）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>四处位置是「数据」，不是代码</b> —— 加减一处改 <see cref="ForThisMachine"/> 那张表。
/// </para>
/// <para>
/// ⚠️ <b>写不进去绝不能挡住用户。</b> 规格 §6.3 原话：「不得因为写不了试用记录而阻止用户试用
/// —— 那会把正常用户挡在门外。」所以每一处都独立 try/catch，失败的只留一条痕。
/// 非管理员机器上的 ②（<c>%ProgramData%</c>）与 ④（HKLM）**就是会写不进去**，
/// 那是**预期内**的降级，不是缺陷。
/// </para>
/// </remarks>
public sealed class TrialRecordStore
{
    /// <summary>试用的窗口：7 个自然日（§6.1）。</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(168);

    /// <summary>尾码用的盐。⚠️ 硬编码是**故意的** —— 见 <see cref="TrialRecord.Tail"/>。</summary>
    internal const string Salt = "vidlog-trial-1";

    private const char Separator = '|';

    private readonly IReadOnlyList<ITrialLocation> _locations;
    private readonly IAppLogger _logger;

    public TrialRecordStore(IReadOnlyList<ITrialLocation> locations, IAppLogger? logger = null)
    {
        _locations = locations;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>这台机器上的四处（§6.3）。</summary>
    /// <param name="localTrialPath">
    /// 位置 ① 的路径。**由调用方按 <c>DataLayout</c> 给**（不是在这里拼 <c>%LOCALAPPDATA%</c>）——
    /// 数据根是可以被用户改的，跟着它走才和录像记录待在一起。
    /// </param>
    public static TrialRecordStore ForThisMachine(string localTrialPath, IAppLogger? logger = null)
    {
        var locations = new List<ITrialLocation>
        {
            new FileTrialLocation("本用户数据目录", localTrialPath),
            new FileTrialLocation("公共数据目录", Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "VidLog",
                "trial.dat")),
        };

        // ⚠️ 注册表那两处只在 Windows 上有。这一行守卫同时消掉 CA1416（见 Read/Write 里的注释）。
        if (OperatingSystem.IsWindows())
        {
            locations.Add(new RegistryTrialLocation("当前用户注册表", Registry.CurrentUser));
            locations.Add(new RegistryTrialLocation("本机注册表", Registry.LocalMachine));
        }

        return new TrialRecordStore(locations, logger);
    }

    /// <summary>四处的名字（诊断与测试要看「到底写了哪几处」）。</summary>
    public IReadOnlyList<string> LocationNames => [.. _locations.Select(l => l.Name)];

    /// <summary>
    /// 按 §6.4 判定一次，并把更新后的记录写回所有写得进去的位置。
    /// </summary>
    /// <param name="now">当前时刻（注入是为了能测「7 天以后」）。</param>
    /// <param name="fallbackFirstRunAt">
    /// **四处都读不到**时的起算点。调用方给「激活那一刻」（<c>license.json</c> 里有）。
    /// <para>
    /// ⚠️ 这一条是**规格外补的**，补的是这样一个洞：四处都读不到并不等于「首次运行」——
    /// 也可能是**四处都写不进去**（非管理员机器上很常见）。不补的话，
    /// 那台机器每次启动都重新开始 7 天，试用等于无限。
    /// 给 <see langword="null"/> 才是真的「首次」（激活那一刻传它）。
    /// </para>
    /// </param>
    public TrialState Evaluate(DateTimeOffset now, DateTimeOffset? fallbackFirstRunAt = null)
    {
        var records = ReadAll();

        DateTimeOffset first;
        DateTimeOffset maxSeen;

        if (records.Count == 0)
        {
            first = fallbackFirstRunAt ?? now;
            maxSeen = now;
        }
        else
        {
            // ⚠️ 取**最早**的 firstRunAt：用户删掉三处只留一处，剩下的那个仍是最早的，
            //    重置失败（§6.4）。
            first = records.Min(r => r.FirstRunAt);

            // ⚠️ 只增不减：把系统时间调回一个月前，maxSeenAt 不动，used 照常算 —— 改时间无效。
            maxSeen = records.Max(r => r.MaxSeenAt) > now ? records.Max(r => r.MaxSeenAt) : now;
        }

        // 时钟被往回拨过 ⇒ used 会是负的。按 0 算（不白送时间，也不倒扣）。
        var used = maxSeen - first;
        if (used < TimeSpan.Zero)
        {
            used = TimeSpan.Zero;
        }

        var state = used >= Window
            ? new TrialState(TrialPhase.Ended, TimeSpan.Zero, first)
            : new TrialState(TrialPhase.Running, Window - used, first);

        _logger.Log(LogLevel.Info, "许可", $"试用判定：{Describe(state)}", new Dictionary<string, object?>
        {
            ["读到的记录数"] = records.Count,
            ["起算于"] = first.ToString("O", CultureInfo.InvariantCulture),
            ["已用小时"] = Math.Round(used.TotalHours, 1),
        });

        WriteAll(new TrialRecord(first, maxSeen));

        return state;
    }

    private static string Describe(TrialState state) => state.Phase == TrialPhase.Ended
        ? "试用已结束"
        : $"试用中，还剩 {state.Remaining.TotalHours:F1} 小时";

    private List<TrialRecord> ReadAll()
    {
        var records = new List<TrialRecord>(_locations.Count);

        foreach (var location in _locations)
        {
            try
            {
                if (location.Read() is { } record)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex)
            {
                // 读不出来不是错：这个位置**本来就可能从没写过**。
                _logger.Log(LogLevel.Debug, "许可", $"试用记录读不出「{location.Name}」：{ex.Message}");
            }
        }

        return records;
    }

    private void WriteAll(TrialRecord record)
    {
        foreach (var location in _locations)
        {
            try
            {
                location.Write(record);
            }
            catch (Exception ex)
            {
                // ⚠️ 见类注释：写不进去**绝不能**往上抛（§6.3 明文要求）。
                //    非管理员机器上的 ②④ 每台每次启动都会走到这里，所以是 Debug 不是 Warn
                //    —— 那会让每台正常机器的日志都挂两条「警告」。
                _logger.Log(LogLevel.Debug, "许可", $"试用记录写不进「{location.Name}」：{ex.Message}");
            }
        }
    }

    /// <summary>一条记录 → 一行文本。<c>起算|见过最大|尾码</c>。</summary>
    internal static string Serialize(TrialRecord record) => string.Join(
        Separator,
        record.FirstRunAt.ToString("O", CultureInfo.InvariantCulture),
        record.MaxSeenAt.ToString("O", CultureInfo.InvariantCulture),
        record.Tail);

    /// <summary>一行文本 → 一条记录。**尾码对不上就是没有**（§6.3）。</summary>
    internal static TrialRecord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split(Separator);

        if (parts.Length != 3
            || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var first)
            || !DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var maxSeen))
        {
            return null;
        }

        var record = new TrialRecord(first, maxSeen);

        // ⚠️ 对不上就当作**这个位置没有记录**（而不是「当作有效」）——
        //    被改过的那条不能参与「取最早」。
        return record.Tail == parts[2] ? record : null;
    }
}
