using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Configuration;

/// <summary>一个可编辑保留期下拉当下长什么样（选中项下标 + 框里的文本）。</summary>
/// <param name="SelectedIndex">选中的是第几项；一个都没选中时是 -1。</param>
/// <param name="Text">框里的文本（可编辑下拉里用户敲的东西在这里，不在选中项上）。</param>
public readonly record struct RetentionInput(int SelectedIndex, string? Text);

/// <summary>
/// 设置页那些格子里的**原值** —— 没规整过、没校验过，就是控件上此刻的样子。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 刻意全是 <see cref="string"/> / <see cref="bool"/>：这样「读控件」与
/// 「算设置」之间就只隔一层数据，算的那一半进得了测试工程（T27② 第 3 批）。
/// </para>
/// <para>
/// ⚠️ 下拉一律传 **tag**，不传显示文本 —— 显示文本会随文案改，tag 是判据。
/// 保留期那四个例外：它们是**可编辑**下拉，用户敲的东西在文本里，
/// 所以两个都传（见 <see cref="RetentionInput"/>）。
/// </para>
/// </remarks>
public sealed record SettingsFormInput
{
    // ── 数字格 ──
    public string? SegmentMinutes { get; init; }
    public string? PlaybackPort { get; init; }
    public string? DuplicateCheckDays { get; init; }
    public string? IdleMinutes { get; init; }
    public string? PrerecordTag { get; init; }
    public string? LogRetainDays { get; init; }
    public string? CloudParallelText { get; init; }

    // ── 下拉的 tag ──
    public string? StationRoleTag { get; init; }
    public string? ModeTag { get; init; }
    public string? CodecTag { get; init; }
    public string? ResolutionTag { get; init; }
    public string? IdleReminderTag { get; init; }
    public string? DurationFallbackTag { get; init; }
    public string? CloseActionTag { get; init; }
    public string? ArchiveBackendTag { get; init; }
    public string? CloudBackfillScopeTag { get; init; }
    public string? LogLevelTag { get; init; }

    // ── 开关 ──
    public bool RunAtStartup { get; init; }
    public bool CheckForUpdates { get; init; }
    public bool RecordAudio { get; init; }
    public bool CloudAutoUpload { get; init; }
    public bool CloudCompareAndBackfill { get; init; }

    // ── 别的格子 ──
    /// <summary>摄像头下拉里选中的那个；没填过/禁用时是 <see langword="null"/>。</summary>
    public string? CameraDevice { get; init; }

    /// <summary>麦克风下拉里选中的那个；没填过/禁用时是 <see langword="null"/>。</summary>
    public string? MicrophoneDevice { get; init; }

    /// <summary>补传起始日期；没选就是 <see langword="null"/>。</summary>
    public DateTimeOffset? CloudBackfillFrom { get; init; }

    /// <summary>网盘应用名那一格。</summary>
    public string? CloudAppName { get; init; }

    // ── 保留期那四个可编辑下拉 ──
    public RetentionInput ArchivedOutbound { get; init; }
    public RetentionInput ArchivedReturn { get; init; }
    public RetentionInput UnarchivedOutbound { get; init; }
    public RetentionInput UnarchivedReturn { get; init; }
}

/// <summary>
/// 设置页上填的那些东西 → 一份 <see cref="AppSettings"/>。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 这一段原先写在 <c>SettingsWindow.SaveAsync</c> 里（T27② 第 3 批搬过来）——
/// 那个工程没有测试工程，而这里全是**一条条独立的判断**，每一条的注释都记着
/// 「为什么不能那样写」。其中好几条的后果都不是「这一项不对」：
/// <c>AppSettings.IsPlausible</c> 判的是**整份设置**，一处放行了 Core 判不过的值，
/// 下次启动**整体回落到默认值**，而用户只碰过这一页。
/// </para>
/// <para>
/// ⚠️ 搬的是行为，一个字没改：连**校验的先后顺序**都照旧（先说分段、再说端口…），
/// 因为用户一次只看到第一句。
/// </para>
/// </remarks>
public static class SettingsForm
{
    /// <summary>
    /// 收一次。
    /// </summary>
    /// <param name="input">界面上那些格子的原值。</param>
    /// <param name="current">当前已保存的那一份（很多项「认不出来就保持原值」）。</param>
    /// <param name="diskRows">「录像保存位置」那张表的行。</param>
    /// <param name="backupDiskRows">「录像备份位置」那张表的行。</param>
    /// <returns>
    /// 成功时 <c>Next</c> 非空、<c>Problem</c> 是空串；不成时 <c>Next</c> 为
    /// <see langword="null"/>、<c>Problem</c> 是要说给用户听的那句话。
    /// </returns>
    public static (AppSettings? Next, string Problem) Build(
        SettingsFormInput input,
        AppSettings current,
        IReadOnlyList<(string Folder, string? ReservedText)> diskRows,
        IReadOnlyList<(string Folder, string? ReservedText)> backupDiskRows)
    {
        // ⚠️ 越界的输入**不静默吞掉** —— 说清楚、并且不保存，而不是存进去一个
        // 之后会让人莫名其妙的值。
        if (!int.TryParse(input.SegmentMinutes, out var segment) || segment is < 1 or > 10)
        {
            return (null, "分段时长要在 1~10 分钟之间，本次未保存。");
        }

        if (!int.TryParse(input.PlaybackPort, out var port) || port is < 1024 or > 65535)
        {
            return (null, "端口要在 1024~65535 之间，本次未保存。");
        }

        // 重复单号检测的天数（规格 §3.2.5「N 可配置」）。**0 = 关闭。**
        // ⚠️ 界面上写清「0 = 关闭」，而这里也接受 0 —— 否则那句话就是空话。
        if (!int.TryParse(input.DuplicateCheckDays, out var duplicateDays)
            || duplicateDays is < 0 or > 365)
        {
            return (null, "重复单号检测要填 0~365 天（0 = 关闭），本次未保存。");
        }

        // 两张磁盘表（批次 4，设计图 `_43`）。分开判，好让每一处各自说自己那一句。
        if (!TryReadDisks(diskRows, "录像保存位置", out var saveDisks, out var saveDiskError))
        {
            return (null, saveDiskError);
        }

        if (!TryReadDisks(backupDiskRows, "录像备份位置", out var backupDisks, out var backupDiskError))
        {
            return (null, backupDiskError);
        }

        // 自定义分钟数：只在选了「自定义」时才管它，否则保持原值
        //（用户先填了 7 分钟又改回 3 分钟，那 7 不该丢 —— 下次切回自定义还要用）。
        var idleMinutes = int.TryParse(input.IdleMinutes, out var parsedMinutes)
            ? Math.Clamp(parsedMinutes, WorkModeOptions.MinIdleMinutes, WorkModeOptions.MaxIdleMinutes)
            : current.IdleReminderMinutes;

        // 日志保留天数：认不出来、或者越界（`1..365`，与 `AppSettings` 的校验同一个
        // 范围）就**退回原值** —— 写个 0 进去的话，所有日志当场被清掉，
        // 而用户只是想改一个数。
        var logRetainDays = int.TryParse(input.LogRetainDays?.Trim(), out var days) && days is >= 1 and <= 365
            ? days
            : current.LogRetainDays;

        var next = current with
        {
            // 电脑用途（批次 4）。认不出来就保持原值 —— 这个下拉只有四项，
            // 认不出来说明界面坏了，而「静默换成默认用途」会让一台备份主机
            // 下次启动突然开始抢摄像头。
            StationRole = ParseOr(input.StationRoleTag, current.StationRole),
            Mode = ParseOr(input.ModeTag, current.Mode),
            Codec = ParseOr(input.CodecTag, current.Codec),
            Resolution = ParseOr(input.ResolutionTag, current.Resolution),
            IdleReminder = ParseOr(input.IdleReminderTag, current.IdleReminder),
            IdleReminderMinutes = idleMinutes,
            DurationFallback = ParseOr(input.DurationFallbackTag, current.DurationFallback),
            SegmentMinutes = segment,
            // 扫码预录缓冲（批次 C，规格 §3.1.3）。四个档位，正常取不到别的值；
            // 越界就保持原值而不是夹一下 —— 与「同时上传数」同一个理由，
            // 猜错一档要么白丢几秒画面、要么白占一份磁盘，不如不动。
            PrerecordSeconds = int.TryParse(input.PrerecordTag, out var prerecord)
                && prerecord is >= 0 and <= 30
                    ? prerecord
                    : current.PrerecordSeconds,
            DuplicateCheckDays = duplicateDays,
            PlaybackPort = port,

            // ── 外观与启动（批次 9，设计图 `_49`）──
            // ⚠️ 「界面语言」与「外观主题」**刻意不在这里读**：它们各自只有一档能选，
            // 落进设置文件就是一个永远为真的假开关（踩坑 #13）。
            RunAtStartup = input.RunAtStartup,
            CloseWindowAction = ParseOr(input.CloseActionTag, current.CloseWindowAction),
            CheckForUpdates = input.CheckForUpdates,

            // ── 日志（2026-10-01 需求方要「级别可配」）──
            // ⚠️ 级别认不出来退的是 **Info**（不是原值）：这一格四项写死，
            // 读不出 tag 就是界面坏了，而 Info 是那个「什么都记」的安全档。
            LogMinLevel = ParseOr(input.LogLevelTag, LogLevel.Info),
            LogRetainDays = logRetainDays,

            // ⚠️ 用着网络摄像头时那个下拉是禁用且空的，直接把 null 写进去
            // 会把记着的本机设备名抹掉 —— 用户哪天切回本机设备就得重选一遍。
            CameraDevice = input.CameraDevice ?? current.CameraDevice,

            // ⚠️ 关掉时麦克风那一栏**仍然记着**选的是哪个（与归档目录同一个道理）：
            // 用户来回拨开关时不必重选一遍。关着的时候那个下拉根本没被填过，
            // 直接读它会得到 null。
            RecordAudio = input.RecordAudio,
            MicrophoneDevice = input.MicrophoneDevice ?? current.MicrophoneDevice,

            ArchiveBackend = ParseOr(input.ArchiveBackendTag, current.ArchiveBackend),

            // 目录型那两档的根，现在是一张表（批次 4）。别的档位下那张表是藏着的，
            // 但行仍然记着 —— 用户在 NAS 与挂载盘之间来回切时不必重填一遍。
            //
            // ⚠️ 老的单个 `ArchiveDirectory` 在这里**清掉**：留着它的话，
            // `AppSettings.ArchiveDirectories` 会在表被清空时回落到它 ——
            // 于是用户删掉的最后一行会**原地复活**。值已经折进表里了（读的时候折的）。
            ArchiveDirectory = null,

            BackupDisks = backupDisks,
            SaveDisks = saveDisks,
            Retention = new RetentionSettings(
                RetentionOf(input.ArchivedOutbound), RetentionOf(input.ArchivedReturn),
                RetentionOf(input.UnarchivedOutbound), RetentionOf(input.UnarchivedReturn)),

            // ── 百度网盘（批次 5，设计图 `_45` / `_46`）──
            // ⚠️ `AutoUploadSince` **刻意不在这里**：盖章的是
            // `AppHost.SaveSettingsAsync`（拨开那一刻），因为这组设置还有别的入口，
            // 而这条时间戳是「仅此开关开启后**新开始录制**的视频会上传」唯一的判据 ——
            // 少盖一次，打开开关就会把整库历史录像一次全传上去。
            Cloud = current.Cloud with
            {
                AutoUpload = input.CloudAutoUpload,
                CompareAndBackfill = input.CloudCompareAndBackfill,
                BackfillScope = ParseOr(input.CloudBackfillScopeTag, current.Cloud.BackfillScope),

                // 换回「全部」时**不清**：用户来回切时不必重选一遍（与磁盘表同一个道理）。
                BackfillFrom = input.CloudBackfillFrom ?? current.Cloud.BackfillFrom,

                // 界面上是个 1~8 的下拉，正常取不到别的值；越界就保持原值而不是夹一下 ——
                // 静默把「同时上传数」改成 8 的后果不是慢一点，是整库被网盘风控限流。
                ParallelUploads = int.TryParse(input.CloudParallelText, out var parallel)
                    && parallel is >= 1 and <= 8
                        ? parallel
                        : current.Cloud.ParallelUploads,

                AppName = string.IsNullOrWhiteSpace(input.CloudAppName)
                    ? current.Cloud.AppName
                    : input.CloudAppName.Trim(),
            },
        };

        return (next, string.Empty);
    }

    /// <summary>
    /// 把一张磁盘表的行读成 <see cref="DiskSlot"/> 串。
    /// </summary>
    /// <param name="rows">表里的行；<c>ReservedText</c> 为 <see langword="null"/> 或空 = 没填。</param>
    /// <param name="label">说给用户听的那张表叫什么（「录像保存位置」）。</param>
    /// <param name="slots">读出来的槽位。</param>
    /// <param name="error">读不出来时的原因。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ 预留空间那一格**空着是合法的**（= 按默认值现算，<c>ReservedGb</c> 给
    /// <see langword="null"/>），但填了一个读不懂的数就不合法 —— 那种时候
    /// **不保存**并说清楚，而不是静默换成默认值（用户会以为他填的生效了）。
    /// </para>
    /// <para>
    /// ⚠️ <b>两个上限读的是 <see cref="AppSettings"/> 上的常量</b>（T29）。
    /// 这不是洁癖：这两处原本各写各的 32 与 1000000，一旦界面放行的值
    /// Core 判不过，坏掉的不是这一项 —— <see cref="AppSettings.IsPlausible"/> 判的是
    /// **整份设置**，它会**整体回落到默认值**，于是保留期、云端、关窗行为
    /// 一起被打回原样，而用户只碰过磁盘这一页。
    /// </para>
    /// <para>
    /// ⚠️ <b>2026-10-07 从 <c>SettingsWindow.TryReadDisks</c> 搬过来</b>（T27② 第 3 批）——
    /// 那一侧原先只能靠一条**读源码文本**的绊线挡着，它自己写着天花板
    /// 「挡不住常量引对了但比较方向写反」。搬进 Core 之后这些判据由真正的单测盖住，
    /// 那条绊线也跟着退休了。
    /// </para>
    /// </remarks>
    public static bool TryReadDisks(
        IReadOnlyList<(string Folder, string? ReservedText)> rows,
        string label,
        out List<DiskSlot> slots,
        out string error)
    {
        slots = [];
        error = string.Empty;

        if (rows.Count > AppSettings.MaxDiskSlots)
        {
            error = $"{label}最多 {AppSettings.MaxDiskSlots} 个，本次未保存。";
            return false;
        }

        foreach (var (folder, reservedText) in rows)
        {
            var text = reservedText?.Trim() ?? string.Empty;

            if (text.Length == 0)
            {
                slots.Add(new DiskSlot(folder));
                continue;
            }

            if (!int.TryParse(text, out var reserved)
                || reserved is < 0 or > AppSettings.MaxReservedGb)
            {
                error = $"{label}里「{folder}」的预留空间要是 0~{AppSettings.MaxReservedGb} 之间的整数"
                    + "（留空 = 用默认值），本次未保存。";
                return false;
            }

            slots.Add(new DiskSlot(folder, reserved));
        }

        return true;
    }

    /// <summary>把下拉里的选择读回来。**认不出的写法一律回落「全部保留」**（朝少删的那头落）。</summary>
    /// <remarks>
    /// ⚠️ 可编辑下拉：用户敲的东西在 <c>Text</c> 里，不一定选中了某一项。
    /// 先按**选中项**认，认不出再看文本 —— 顺序反了的话，手输过一个数之后
    /// 再点列表里的项，读回来的会是旧文本。
    /// </remarks>
    public static RetentionSetting RetentionOf(RetentionInput input)
    {
        var standard = RetentionSetting.Standard;

        if (input.SelectedIndex >= 0
            && input.SelectedIndex < standard.Count
            && string.Equals(
                input.Text, standard[input.SelectedIndex].Label, StringComparison.Ordinal))
        {
            return standard[input.SelectedIndex];
        }

        var text = input.Text?.Trim() ?? string.Empty;

        if (text is "全部保留" or "")
        {
            return RetentionSetting.KeepAll;
        }

        if (text == "不保留")
        {
            return RetentionSetting.Immediate;
        }

        var digits = text.EndsWith('天') ? text[..^1].Trim() : text;

        return int.TryParse(digits, out var days)
            ? RetentionSetting.FromConfig(days)
            : RetentionSetting.KeepAll;
    }

    /// <summary>认得出就换成那个 enum 值，认不出就保持原值。</summary>
    private static T ParseOr<T>(string? tag, T fallback)
        where T : struct, Enum =>
        Enum.TryParse<T>(tag, out var value) ? value : fallback;
}
