using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Overview;

/// <summary>
/// 右栏「本机录制动态」那一屏上的**字**（T27② 第 3 批块 2）。
/// </summary>
/// <remarks>
/// <para>
/// 搬的是**算与写**，不是**读**：取值那几个 <c>await</c>（检索、读索引、量目录、
/// 枚举设备、清理预告）留在壳里 —— 它们是 I/O，而且一半的结果喂两处
/// （今天的命中也进「备份主机」那一屏）。这里只收**已经取回来**的值。
/// </para>
/// <para>
/// ⚠️ <b>为什么一句一个函数，而不是收一个大 DTO 再一次性 Build</b>：
/// 这里几乎每一句都各自压着一条**承重规矩**（「约」不能省、读不到必须说出来、
/// 网络档不许印地址里的密码……下面每一处都记着是哪一条）。
/// 一句一个函数就一句一条测试；打成一个 Build 的话，「约」丢了和
/// 「读不到没说」会挤进同一条断言里，坏了也说不清是哪句。
/// </para>
/// <para>
/// ⚠️ 留壳的只有两样：<c>Brush</c>/<c>Visibility</c> 这类**类型**，
/// 以及下面返回 <see langword="bool"/> 的那些**判据**对应的控件动作
/// （颜色名、折叠）。判据本身在这里，壳只负责照着做。
/// </para>
/// </remarks>
public static class OverviewTexts
{
    // ─────────────────────────────────────────────
    // ① 今天
    // ─────────────────────────────────────────────

    /// <summary>今日命中里的两个合计：已知时长、平均时长。</summary>
    /// <param name="durations">今天那几段的时长（**已经取出来的值**，不是命中本身）。</param>
    /// <remarks>
    /// <para>
    /// ⚠️ 收的是时长、不是检索命中：这个函数的活是**算术**，
    /// 收命中就要把 <c>RecordingHit</c>/<c>RecordingEntry</c> 一串拖进来，
    /// 而它一个字段都不看（只看 <c>Duration</c>）。
    /// </para>
    /// <para>
    /// ⚠️ 平均那一条要先挡 <c>Count == 0</c>：除零会抛，
    /// 而「今天一段都还没录」是**常态**（清早打开就是这样）。
    /// </para>
    /// </remarks>
    public static (TimeSpan Known, TimeSpan Average) TodayTotals(IReadOnlyList<TimeSpan> durations)
    {
        var known = durations.Aggregate(TimeSpan.Zero, (sum, one) => sum + one);

        return (known,
            durations.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(known.Ticks / durations.Count));
    }

    /// <summary>底部统计条：件数。</summary>
    public static string TodayCount(int count) => $"今日 {count} 件";

    /// <summary>底部统计条：平均。</summary>
    public static string TodayAverage(TimeSpan average) => $"平均 {(int)average.TotalSeconds} 秒";

    /// <summary>底部统计条：总耗时。</summary>
    /// <remarks>
    /// ⚠️ 写的是**已知**时长合计：时长是从文件名/元数据读的，读不到的那一段
    /// 计 0，所以这个数天然偏小。界面上不能把它说成「总时长」。
    /// </remarks>
    public static string TodayTotal(TimeSpan known) => $"总耗时 {(int)known.TotalSeconds} 秒";

    /// <summary>右栏那一行：几段、合计多久。</summary>
    public static string TodaySummary(int segments, TimeSpan known) =>
        $"{segments} 段 · 已知时长合计 {(int)known.TotalHours} 小时 {known.Minutes} 分";

    /// <summary>索引里一共多少段。</summary>
    public static string IndexCount(int segments) => $"{segments} 段";

    /// <summary>录像库总容量那一行。</summary>
    /// <remarks>
    /// ⚠️ 读不到的位置**必须说出来**：不说的话那个字节数是**静默偏小**的，
    /// 而用户正是拿它判断「盘还够不够用」—— 他会以为还能录很久。
    /// </remarks>
    public static string Library(LibraryFootprint footprint) =>
        footprint.UnreadableCount == 0
            ? $"{Display.Bytes(footprint.TotalBytes)} · {footprint.FileCount} 个文件"
            : $"{Display.Bytes(footprint.TotalBytes)} · {footprint.FileCount} 个文件"
              + $"（另有 {footprint.UnreadableCount} 处读不到，实际只会更多）";

    /// <summary>统计时刻。</summary>
    public static string Updated(DateTimeOffset at) => $"统计于 {at:HH:mm:ss}";

    /// <summary>统计没算完时顶上那一格写的话。</summary>
    /// <remarks>
    /// ⚠️ 概览算不出来**不该让整个界面出错**，但也不能装作没事 ——
    /// 见下面 <see cref="Camera"/> 那一格为什么不能留着上一次的数。
    /// </remarks>
    public static string Failed(string reason) => $"⚠️ 统计没算完：{reason}";

    // ─────────────────────────────────────────────
    // ② 状态
    // ─────────────────────────────────────────────

    /// <summary>摄像头那一格。</summary>
    /// <remarks>
    /// ⚠️ 显示用 <see cref="CameraSource.Display"/>（网络那一档写成
    /// 「网络摄像头 · 地址」），而**不是** <see cref="CameraSource.Address"/> ——
    /// 后者在网络那一档**带着摄像头密码**，那是要给人看的一格。
    /// </remarks>
    public static string Camera(bool hasFfmpeg, CameraSource camera) =>
        !hasFfmpeg
            ? "没有 FFmpeg，无法采集"
            : camera.IsEmpty
                ? "没有找到摄像头"
                : camera.Display;

    /// <summary>回放服务那一格。</summary>
    /// <remarks>
    /// ⚠️ 退回本机地址那一档要说出来：不说的话用户会拿这个地址去别的设备上试，
    /// 然后以为是自己网断了。
    /// </remarks>
    public static string Server(string? baseUrl, bool usingFallback) =>
        baseUrl is { Length: > 0 } url
            ? usingFallback
                ? $"已启动 · {url}（只绑到本机，别的设备访问不了）"
                : $"已启动 · {url}"
            : "未启动";

    /// <summary>归档层那一格。</summary>
    /// <remarks>
    /// ⚠️ 配不好的时候**只写标签是不够的**：标签会说「NAS」，而实际根本连不上 ——
    /// 用户会以为东西已经备份出去了（I1/I3 那条线上最贵的一种误解）。
    /// </remarks>
    public static string Archive(string label, string? configurationProblem) =>
        configurationProblem is { Length: > 0 } problem
            ? $"⚠️ {label} —— 没配好：{problem}"
            : label;

    /// <summary>盘还剩下多少。</summary>
    public static string FreeSpace(long bytes) => $"可用 {Display.Bytes(bytes)}";

    /// <summary>
    /// 读不到可用空间时写的话。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>不许渲染成一个数字</b>：读盘那条路是**抛**的（不是返回 -1），
    /// 而一个「0 GB」会被当成真的 —— 用户会据此判断「盘满了」，
    /// 然后去删录像或者换机器。说不出原因也要说「读不到」。
    /// </remarks>
    public static string FreeSpaceUnreadable(string reason) => $"读不到可用空间（{reason}）";

    /// <summary>接进来几台设备。</summary>
    public static string Devices(int count) => count == 0 ? "还没有手机接进来" : $"{count} 台";

    // ─────────────────────────────────────────────
    // ④ 备份主机那一屏（设计图 `_39`）
    // ─────────────────────────────────────────────

    /// <summary>有没有设备接进来 —— 决定那个标签是绿的还是橙的。</summary>
    public static bool DeviceMissing(int count) => count == 0;

    /// <summary>设备状态那个小标签。</summary>
    public static string BackupDeviceTag(int count) => count == 0 ? "暂无设备" : "已就绪";

    /// <summary>设备那一句说明。</summary>
    /// <remarks>
    /// ⚠️ 没设备时**要把下一步写出来**（去哪儿加）：这一屏是「备份主机」，
    /// 而它唯一的毛病就是还没配过，不说下一步的话用户会去别处找。
    /// </remarks>
    public static string BackupDeviceText(int count) =>
        count == 0
            ? "还没有手机或电脑接进来。用上面的【连接电脑/手机】把它们加进来。"
            : $"已接入 {count} 台设备，录像会存到本机。";

    // ─────────────────────────────────────────────
    // ③ 待办（没有就不出现）
    // ─────────────────────────────────────────────

    /// <summary>清理预告那一行；没有候选就是空串。</summary>
    /// <remarks>
    /// ⚠️ <b>「约」不能省</b>：那是估算值，而且这一层自己就承认没标定过。
    /// ⚠️ 括号里那句也不能省：清理前会再算一次，两次的数**本来就会不一样**，
    /// 不先说的话用户会把第一次那个数当成准的。
    /// </remarks>
    public static string Cleanup(int candidates, long totalBytes) =>
        candidates == 0
            ? string.Empty
            : $"{candidates} 条 · 约 {totalBytes / 1024 / 1024} MB（估的，清理前会再算一次）";

    /// <summary>「未备份的已过保留期」那一行；没有就是空串。</summary>
    /// <remarks>
    /// ⚠️ 后半句（不会被自动删、只是提醒）**必须写出来**：这一列到期的动作
    /// 本来就不是删（规格 §3.5.3①），而用户看到「已过保留期」的第一反应是
    /// 「它是不是要删我东西」。
    /// </remarks>
    public static string Overdue(int count) =>
        count == 0
            ? string.Empty
            : $"{count} 条未备份的已过保留期（不会被自动删，只是提醒上传）";

    /// <summary>启动时的警告。</summary>
    /// <remarks>
    /// ⚠️ <b>全列出来，不只第一条。</b>
    /// 原先主窗有一块专门的「需要注意」清单（拆窗时删掉了，明细现在在设置窗里），
    /// 这里若只写第一条，用户就得去设置窗才知道后面还说了什么 ——
    /// 而警告里有「没有摄像头，无法录制」这种**必须当场知道**的。
    /// </remarks>
    public static string Warnings(IReadOnlyList<string> warnings) =>
        warnings.Count == 0
            ? string.Empty
            : "⚠️ 启动时有需要注意的地方：\n"
              + string.Join('\n', warnings.Select(w => $"· {w}"));

    /// <summary>「待办」那张卡出不出来。</summary>
    /// <remarks>
    /// ⚠️ 判据是三样**或**起来，不是看有没有警告：清理预告与过期提醒
    /// 也走这张卡，只看警告的话那两样会**无声消失**。
    /// </remarks>
    public static bool TodoVisible(string cleanupLine, int overdue, int warningCount) =>
        cleanupLine.Length > 0 || overdue > 0 || warningCount > 0;
}
