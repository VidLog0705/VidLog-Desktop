using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Import;
using VidLog.Desktop.Core.Index;
using VidLog.Desktop.Core.Labels;
using VidLog.Desktop.Core.Live;
using VidLog.Desktop.Core.Playback;
using VidLog.Desktop.Core.Search;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Web;

public sealed partial class PlaybackServer : IAsyncDisposable
{
    private async Task WriteSearchAsync(HttpListenerContext context)
    {
        var query = context.Request.QueryString;

        var recordingQuery = new RecordingQuery
        {
            WaybillText = query["q"],
            MatchMode = query["mode"] switch
            {
                "prefix" => WaybillMatchMode.Prefix,
                "contains" => WaybillMatchMode.Contains,
                _ => WaybillMatchMode.Exact,
            },
            From = ParseInstant(query["from"]),
            To = ParseInstant(query["to"]),
            BusinessType = ParseBusinessType(query["type"]),
            // 「录像来源」下拉（设计图 `_36`）。空串按「不限」处理 ——
            // 那正是下拉里「全部设备」那一项的值。
            SourceDevice = query["source"] is { Length: > 0 } source ? source : null,
        };

        var hits = await _search.SearchAsync(recordingQuery);

        var payload = hits.Select(hit => new PlaybackSearchItem(
            hit.Entry.EvidenceId,
            hit.Entry.Waybill.Value,
            hit.Entry.StartedAt.ToString("O"),
            hit.Entry.Duration.TotalSeconds,
            hit.BusinessType?.ToString() ?? "unknown",
            // 规格 §3.1.7 的连带项：页面要如实告知「这条能不能在网页里播」。
            hit.Entry.Codec,
            // 规格 §3.4.3 的第 ⑦ 项（归档层这一份在哪儿）。
            // ⚠️ 本机磁盘那一档**要明说「仅本机」**：那时盘上这份是唯一副本（§3.5.1）。
            _options.ArchiveBackend is { Kind: ArchiveBackendKind.LocalDisk }
                ? "仅本机"
                : _options.ArchiveBackend?.Kind.ToString() ?? "仅本机"));

        await WriteJsonAsync(context, payload);
    }

    /// <summary>
    /// 某条证据所属会话的全部打点，已换算成「哪个文件、第几秒」。
    /// </summary>
    /// <remarks>
    /// 规格 §3.8 要求回放支持「跳转到打点位置」（§3.8）。
    /// 换算之所以要服务端做：打点是**会话内偏移**，而一次打包可能横跨多个分段文件，
    /// 浏览器拿到的只是单个文件的 URL，不知道跨段关系。
    /// </remarks>
    private async Task WritePunchesAsync(HttpListenerContext context)
    {
        var evidenceId = context.Request.QueryString["evidenceId"] ?? string.Empty;

        var targets = await _punches.ForEvidenceAsync(evidenceId);

        await WriteJsonAsync(context, targets);
    }

    /// <summary>
    /// 三张统计卡 + 「录像来源」下拉（设计图 `_36`）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>卡片上每一个数都能从索引或盘上算出来，一个都不编</b>（规格 §13.1）。
    /// 算不出来的（订单联动、上传成功率那类）<b>不在这里，也不在页面上编一个</b>。
    /// </para>
    /// <para>
    /// ⚠️ 所有字节数走 <see cref="RecordingStats.Summarize"/> ——
    /// 它底下是 <c>CleanupPlanner.EstimateBytes</c>，与「按空间清理」**同一个函数**。
    /// 另写一份求和的话，同一个库会在两个页面上报出两个容量。
    /// 所以**界面上凡是显示它的地方都带「约」**。
    /// </para>
    /// </remarks>
    private async Task WriteOverviewAsync(HttpListenerContext context)
    {
        var entries = await _index.LoadAllAsync();

        // 全库那一份（已用 = 全部录像的估算占用；一个区间都不限）
        var all = RecordingStats.Summarize(entries, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

        var earliest = entries.Count == 0 ? (DateTimeOffset?)null : entries.Min(e => e.StartedAt);

        // ── 盘上的容量 ────────────────────────────────────────────────
        //
        // ⚠️ 按**卷**去重：两个存储目录落在同一块盘上时（完全合法的配置），
        // 各算一遍会让「共 2 个存储目录」与容量条自相矛盾 —— 容量看起来翻了一倍。
        var roots = _options.Locations?.ReadRoots
            ?? (_options.ArchiveRoot.Length > 0 ? [_options.ArchiveRoot] : []);

        var probe = new Configuration.DriveVolumeProbe();
        var volumes = new Dictionary<string, Configuration.VolumeSpace>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (probe.Measure(root) is not { } space)
            {
                continue;
            }

            volumes.TryAdd(VolumeOf(root), space);
        }

        var now = DateTimeOffset.Now;

        // ── 预计可保留 ────────────────────────────────────────────────
        //
        // = 剩余空间 ÷ 最近这几天的平均每日占用。
        //
        // ⚠️ 分母是「**真的有录像的那些天**」摊出来的，不是窗口长度：
        // 一台刚装两天的机器上，拿 7 天当分母会把日均算成实际的 1/3.5，
        // 于是那张卡报出一个**乐观三倍**的天数 —— 用户照着它决定「不用加盘」。
        //
        // 历史不足一天时报「暂无法估算」（照图那句话：历史数据不足或平均每日占用为 0）。
        var windowDays = Math.Max(1, _options.RetentionEstimateWindowDays);
        var historyDays = earliest is { } first ? (now - first).TotalDays : 0;
        var spanDays = Math.Min(windowDays, historyDays);

        double? perDayBytes = null;
        if (spanDays >= 1)
        {
            var recent = RecordingStats.Summarize(entries, now.AddDays(-spanDays), now.AddDays(1));
            if (recent.EstimatedBytes > 0)
            {
                perDayBytes = recent.EstimatedBytes / spanDays;
            }
        }

        var freeBytes = volumes.Values.Sum(v => v.FreeBytes);
        var totalBytes = volumes.Values.Sum(v => v.TotalBytes);

        await WriteJsonAsync(context, new PlaybackOverview(
            Earliest: earliest?.ToString("O"),
            Count: all.Count,
            WaybillCount: all.WaybillCount,
            // 「已用」是**录像的**占用（估），不是整块盘已用：这张卡问的是
            // 「录下来的东西占了多少」，而整盘已用会把 Windows 与别的软件算进来。
            EstimatedUsedBytes: all.EstimatedBytes,
            TotalBytes: totalBytes,
            FreeBytes: freeBytes,
            DirectoryCount: roots.Count,
            VolumeCount: volumes.Count,
            RetentionDays: perDayBytes is > 0 && freeBytes > 0 ? freeBytes / perDayBytes.Value : null,
            // ⚠️ 估不出来时报 **0**，不报那个窗口长度：这个字段说的是「那个日均
            // 是拿几天摊出来的」，而一个没算出来的日均没有摊过任何天。
            // 报 7 的话页面上会出现「按最近 7 天估算」配着「暂无法估算」——
            // 一句自相矛盾的话，用户只能猜哪个是真的。
            RetentionWindowDays: perDayBytes is null ? 0 : (int)Math.Floor(spanDays),
            Sources: [.. entries
                .GroupBy(e => e.SourceDeviceId, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new PlaybackSource(g.Key, DescribeSource(g.Key), g.Count()))]));
    }

    /// <summary>
    /// 「录像来源」下拉里那个名字。
    /// </summary>
    /// <remarks>
    /// ⚠️ 本机录的**就是机器名本身**，不另起一个好听的名字：
    /// 索引里 `SourceDeviceId` 存的是写进去那一刻的机器名，而用户要认的正是那一台。
    /// 唯一特判的是导入进来的那一种 —— 它写的是一句实话（「不知道是哪台录的」），
    /// 直接印 `imported` 给用户看没有意义。
    /// </remarks>
    private static string DescribeSource(string sourceDeviceId) => sourceDeviceId switch
    {
        RecordingImporter.SourceDeviceId => "外部导入",
        "" => "未知来源",
        _ => sourceDeviceId,
    };

    /// <summary>
    /// 「用手机打开」的二维码（设计图 `_38`）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>地址里没有访问密钥</b>。设计图上那一串 <c>?key=…</c> 是那个产品的做法，
    /// 而<b>本仓的回放页本来就没有密钥</b> —— 局域网里谁打开这个地址都能看。
    /// 编一个 key 出来显示，用户会以为「有这个 key 才看得到」，
    /// 于是把它当成可以外发的链接。<b>那是把一句假话印在屏幕上。</b>
    /// 所以这里给的是真地址，而提醒说的是**这件事本身的真实风险**。
    /// </para>
    /// <para>
    /// ⚠️ 二维码只回**模块矩阵**（一行一串 0/1），不在这里生成 PNG：
    /// Core 不该知道怎么画图，而浏览器用 canvas 画几十行方块是白送的。
    /// 不做缩放插值 —— 二维码一糊就「看着正常、扫不出来」。
    /// </para>
    /// </remarks>
    private async Task WriteQrAsync(HttpListenerContext context)
    {
        // ⚠️ 不用 `BaseUrl` 里那个地址：它多半是 `localhost` 或通配地址，
        // 手机连不上（`LanAddress` 的说明里写了这条）。
        var host = _options.LanHost ?? LanAddress.Discover();

        if (string.IsNullOrWhiteSpace(host))
        {
            await WriteJsonAsync(context, new PlaybackQr(
                Url: null,
                Width: 0,
                Height: 0,
                Rows: [],
                Problem: $"没挑到局域网地址。请用这台电脑的 IP 手动拼：http://<电脑的IP>:{BoundPort()}/"));

            return;
        }

        var url = $"http://{host}:{BoundPort()}/";
        var modules = EnrollQr.Modules(url);

        var width = modules.GetLength(0);
        var height = modules.GetLength(1);
        var rows = new string[height];

        for (var y = 0; y < height; y++)
        {
            var line = new char[width];

            for (var x = 0; x < width; x++)
            {
                line[x] = modules[x, y] ? '1' : '0';
            }

            rows[y] = new string(line);
        }

        await WriteJsonAsync(context, new PlaybackQr(url, width, height, rows, null));
    }

    /// <summary>
    /// 实际绑上的那个端口。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不走 <c>new Uri(BaseUrl)</c>：正式装配里的前缀是 <c>http://+:8720/</c> 这种
    /// **通配形式**，而 <see cref="Uri"/> 解析不了 <c>+</c> 当主机名 —— 它会抛。
    /// 从文本里取反而稳。
    /// </remarks>
    private int BoundPort()
    {
        var text = BaseUrl;
        var colon = text.LastIndexOf(':');

        if (colon < 0)
        {
            return 80;
        }

        var end = text.IndexOf('/', colon);
        var digits = end < 0 ? text[(colon + 1)..] : text[(colon + 1)..end];

        return int.TryParse(digits, out var port) ? port : 80;
    }

    /// <summary>一个路径落在哪一块卷上（按卷去重要用它）。</summary>
    private static string VolumeOf(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private async Task WriteMediaAsync(HttpListenerContext context, string evidenceId)
    {
        var path = await ResolveEvidencePathAsync(evidenceId);

        if (path is null || !File.Exists(path))
        {
            context.Response.StatusCode = 404;
            return;
        }

        await WriteFileWithRangeAsync(context, path);
    }

    /// <summary>
    /// 把证据 id 解析成本机文件路径。
    /// </summary>
    /// <remarks>
    /// **只按索引里的相对路径解析，绝不拼接请求里的字符串** ——
    /// 否则 <c>/media/../../../windows/system32/config/sam</c> 就能读到任何文件。
    /// 解析完还要再确认落在**某一个**归档根之内，挡住索引本身被污染的情况。
    /// </remarks>
    private async Task<string?> ResolveEvidencePathAsync(string evidenceId)
    {
        if (string.IsNullOrWhiteSpace(evidenceId) || _options.ArchiveRoot.Length == 0)
        {
            return null;
        }

        var entries = await _index.LoadAllAsync();
        var entry = entries.FirstOrDefault(e => string.Equals(e.EvidenceId, evidenceId, StringComparison.Ordinal));
        if (entry is null)
        {
            return null;
        }

        // ⚠️ 挨个根试，但**每一个都要过那条包含判定** ——
        // 多根之后这一层更要紧了：找到的第一份不值得信任，
        // 值得信任的是「它确实落在某一个归档根下面」。
        foreach (var root in _options.Locations?.ReadRoots ?? [_options.ArchiveRoot])
        {
            var full = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(Path.Combine(full, entry.Location.Value));

            var rootWithSeparator = full.EndsWith(Path.DirectorySeparatorChar)
                ? full
                : full + Path.DirectorySeparatorChar;

            if (candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
                && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task WriteFileWithRangeAsync(HttpListenerContext context, string filePath)
    {
        var file = new FileInfo(filePath);
        var total = file.Length;

        context.Response.ContentType = "video/mp4";
        context.Response.AddHeader("Accept-Ranges", "bytes");

        var rangeHeader = context.Request.Headers["Range"];
        long start = 0;
        var end = total - 1;

        if (!string.IsNullOrEmpty(rangeHeader) && TryParseRange(rangeHeader, total, out var rangeStart, out var rangeEnd))
        {
            start = rangeStart;
            end = rangeEnd;
            context.Response.StatusCode = 206;
            context.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{total}");
        }
        else
        {
            context.Response.StatusCode = 200;
        }

        var length = end - start + 1;
        context.Response.ContentLength64 = length;

        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);

        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        var remaining = length;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, toRead));
            if (read <= 0)
            {
                break;
            }

            await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }

    /// <summary>
    /// 解析单段 Range 请求头（浏览器拖进度条用的就是这种）。
    /// </summary>
    /// <remarks>
    /// 多段 Range（<c>bytes=0-99,200-299</c>）**刻意不支持** —— 浏览器播放视频只用单段。
    /// 不支持时退回整文件（200），仍然能播，只是不省流量。
    /// </remarks>
    private static bool TryParseRange(string header, long total, out long start, out long end)
    {
        start = 0;
        end = total - 1;

        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = header["bytes=".Length..].Trim();
        if (spec.Contains(','))
        {
            return false;
        }

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var startText = spec[..dash].Trim();
        var endText = spec[(dash + 1)..].Trim();

        // "bytes=-500" = 最后 500 字节
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, out var suffix) || suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, total - suffix);
            end = total - 1;
            return true;
        }

        if (!long.TryParse(startText, out start) || start < 0 || start >= total)
        {
            return false;
        }

        if (endText.Length > 0)
        {
            if (!long.TryParse(endText, out end) || end < start)
            {
                return false;
            }

            end = Math.Min(end, total - 1);
        }
        else
        {
            end = total - 1;
        }

        return true;
    }

    private static DateTimeOffset? ParseInstant(string? text) =>
        DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static BusinessType? ParseBusinessType(string? text) =>
        BusinessTypes.TryParse(text, out var parsed) ? parsed : null;

    private static async Task WriteJsonAsync(HttpListenerContext context, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task WriteHtmlAsync(HttpListenerContext context, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

}
