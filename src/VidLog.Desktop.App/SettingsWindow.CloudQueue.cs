// 「上传队列」那一段（设置页 · 网盘那一页的下半截）。
//
// ⚠️ 从 `SettingsWindow.Cloud.cs` 里拆出来的，那一份已经过了 1000 行
// （仓里 `DesktopServicesTests.T26_点名的长文件拆完之后都不超过800行` 立着闸）。
// 拆的界线按**职责**划、不按行数凑：这一份管的是队列列表本身 ——
// 一行的长相（`CloudQueueRow`）、分页与筛选、那一列的刷新方式、以及每一行那三颗按钮。
// 登录 / 账号 / 补传范围 / 同步进度条那些留在 `SettingsWindow.Cloud.cs`。
using System.Collections.ObjectModel;
using System.Windows;

using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using VidLog.Desktop.Core;
using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Labels;

namespace VidLog.Desktop.App;

/// <summary>
/// 「上传队列」列表里的一行。
/// </summary>
/// <param name="Title">文件名（单号.mp4）—— 提示语里也用它，不要把整行话塞进去。</param>
/// <param name="EvidenceId">哪一条录像。**放弃 / 恢复 / 重试按它定位**。</param>
/// <param name="TypeLabel">「发货」或「退货」；认不出来时是空串。</param>
/// <param name="TypeVisibility">那颗分类徽章显不显示。</param>
/// <param name="TypeTag">
/// 徽章着哪一档色：<c>Outbound</c> / <c>Return</c>，认不出来时是空串。
/// </param>
/// <param name="StateLabel">状态那两个字。</param>
/// <param name="StateTag">状态着哪一档色 —— 就是 <see cref="CloudUploadState"/> 的**成员名**。</param>
/// <param name="Meta">第二行：多大 · 什么时候开录 · 试过几次 ·（失败时）下一拍什么时候再试。</param>
/// <param name="ToolTipText">鼠标停上去才看的东西：远端路径 + 上一次的错。</param>
/// <param name="Progress">这一条传到哪儿了（0~1）。只在上传中/已完成时有意义。</param>
/// <param name="ProgressVisibility">那根进度条显不显示。</param>
/// <param name="AbandonVisibility">【放弃】显不显示。</param>
/// <param name="ResumeVisibility">【恢复】显不显示。</param>
/// <param name="RetryVisibility">【重试】显不显示。</param>
/// <remarks>
/// <para>
/// ⚠️ 显隐直接给 <see cref="Visibility"/> 而不是给 <c>bool</c>：
/// 给 bool 就得再挂一个值转换器，而这一页到现在一个转换器都没有 ——
/// 为几颗按钮显隐多养一个类是白养的。判断本身在 <see cref="CloudUploadStates"/> 里
/// （与服务里那条判断共用一把尺子），这里只是把它翻译成显隐。
/// </para>
/// <para>
/// ⚠️ 用**具名类型**而不是匿名类型：这一行要能被强类型地取回来 ——
/// 三颗按钮的处理器要拿 <see cref="EvidenceId"/>（写法照 `SearchResultRow`）。
/// 也必须是**命名空间级**而不是嵌在窗口类里：模板里的 `{Binding}` 走的是
/// 反射，`SearchResultRow` 那样放在命名空间级是这个仓里已经验过的一条路。
/// </para>
/// <para>
/// ⚠️ <b>这里给的是「哪一档色」的**名字**，不是笔刷本身</b>（2026-10-14 改）。
/// 原来是从资源里当场取一支笔刷存进这一行，那样有两个毛病：
/// 其一，<c>TryFindResource</c> 给你的是一支**冻结的**笔刷、拿到就与资源字典脱了钩 ——
/// 开着程序换 Windows 主题时这几行**不会跟着变，而且不报错**（`ThemePaletteTests`
/// 那条整类闸守的正是这件事）；其二，容器会被复用，值一变就换不回新的颜色。
/// 现在改成把档名给出去，让 XAML 那边的 <c>DataTrigger</c> + <c>DynamicResource</c>
/// 去订阅 —— 换主题自动跟上，容器复用也自动重判。
/// </para>
/// <para>
/// ⚠️ 档名用**枚举成员名**（`Outbound` / `Return` / `Uploading` / `Done`…），
/// 与队列筛选那个下拉是同一套约定（那儿也明写了「`Tag` 就是成员名」）——
/// 改了枚举名就要改 XAML 里那几处 <c>Value</c>，反过来说写错了就是**没颜色**，不报错。
/// </para>
/// <para>
/// ⚠️ 这一行的**值相等**正是「这一行没变、别动它」的判据（见
/// <c>RefreshCloudQueueAsync</c>）：全是字符串/枚举/数字，相等判据照旧成立。
/// </para>
/// </remarks>
internal sealed record CloudQueueRow(
    string Title,
    string EvidenceId,
    string TypeLabel,
    Visibility TypeVisibility,
    string TypeTag,
    string StateLabel,
    string StateTag,
    string Meta,
    string? ToolTipText,
    double Progress,
    Visibility ProgressVisibility,
    Visibility AbandonVisibility,
    Visibility ResumeVisibility,
    Visibility RetryVisibility)
{
    /// <summary>
    /// 屏幕阅读器念这一行时念什么（A6 同族，2026-10-16 补）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 不覆盖它，念出来的就是 record 自动生成的那一串
    /// 「<c>CloudQueueRow { Title = … EvidenceId = … TypeVisibility = Collapsed … }</c>」，
    /// 屏幕阅读器一字不差念完（与 `SearchResultRow` 当年是同一个病）。
    /// </para>
    /// <para>
    /// 只留人看得懂的四样，**按屏幕上从上到下的顺序**：分类、标题、状态、大小与开录时间。
    /// ⚠️ `TypeLabel` 在「不是发货也不是退货」时是**空串**（见构造处），
    /// 不过滤就会念出「标题，，更新中」这种空断句。
    /// </para>
    /// <para>
    /// ⚠️ 落 <c>ToString</c> 而不是给容器挂 <c>AutomationProperties.Name</c> 的理由，
    /// 与 `SearchResultRow` 一字不差 —— 那边写全了，不重复。
    /// </para>
    /// </remarks>
    public override string ToString() => string.Join(
        "，",
        new[] { TypeLabel, Title, StateLabel, Meta }.Where(one => !string.IsNullOrWhiteSpace(one)));
}

public partial class SettingsWindow : Window
{
    /// <summary>队列下面那个「筛选」下拉。每一档与一个队列状态一一对应。</summary>
    /// <remarks>
    /// ⚠️ `Tag` 就是 <see cref="CloudUploadState"/> 的**成员名**（筛的时候直接
    /// 拿 `ToString()` 比）。所以改了枚举名就要改这里 —— 反过来说，
    /// 这里加了新档却没加枚举值，筛出来会永远是空的。
    /// </remarks>
    private static readonly (string Tag, string Label)[] CloudFilters =
    [
        ("All", "全部"),
        ("Pending", "等待中"),
        ("Uploading", "上传中"),
        ("Done", "已完成"),
        ("Failed", "失败"),
        ("Abandoned", "已放弃"),
    ];

    /// <summary>
    /// 上传队列那一段有多高 —— 跟着窗口走，不是写死的一个数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 原来是 XAML 里写死的 <c>MaxHeight="280"</c>。窗口拉大、队列里同时有几十条时，
    /// 那 280 像素一寸不让（用户得在一巴掌大的框里滚），窗口拉小节内又不动 ——
    /// 两头都不合身。
    /// </para>
    /// <para>
    /// ⚠️ <b>只封上限、不定下限。</b>这一页是整页滚动的架子（左导航 + 一列卡片），
    /// 跟「Tab 里钉一张底卡」那种布局不是一回事：真要给它一个 <c>*</c> 比例，
    /// 就得把整页的滚动拆掉，那是拿一个已经好使的架子去换一个更合身的数字。
    /// 所以这里只把上限按窗口高度摊开，页面该滚照样滚。
    /// </para>
    /// </remarks>
    private void FitCloudQueueHeight() =>
        CloudQueueList.MaxHeight = Math.Clamp(ActualHeight * 0.4, 160, 720);

    private void OnCloudQueueFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // 换了筛选就回第一页：留在第 3 页而新的筛选只有 1 页，会看到一片空白。
        _cloudQueuePage = 1;
        _ = RefreshCloudPageAsync();
    }

    /// <summary>
    /// 换了「每页几条」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 同样回第一页，理由和换筛选一样：每页从 5 改成 20，原来那一页多半已经不存在了。
    /// </para>
    /// <para>
    /// ⚠️ <b>`!IsLoaded` 那道挡必须有。</b>下拉在 XAML 里写着 `SelectedIndex="1"`（默认 10 条），
    /// 而 `SelectedIndex` 在 `InitializeComponent` 解析到那一行时**就会发一次**
    /// `SelectionChanged` —— 那时排在它后面的控件（`CloudQueuePageText`、两颗翻页按钮）
    /// **一个都还没建出来**，可刷新会去写它们。这与 `OnSectionChanged` 撞的是同一堵墙
    /// （它那份注释写了「构造期间就会触发」），挡法也一样。
    /// 挡掉的只是这一发：显示上那个「10」照样在（选中状态与处理器无关），
    /// 而构造器末尾自己会刷一次，那一次是齐的。
    /// </para>
    /// </remarks>
    private void OnCloudQueuePageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _cloudQueuePage = 1;
        _ = RefreshCloudPageAsync();
    }

    private void OnCloudQueuePrev(object sender, RoutedEventArgs e)
    {
        _cloudQueuePage--;
        _ = RefreshCloudPageAsync();
    }

    private void OnCloudQueueNext(object sender, RoutedEventArgs e)
    {
        _cloudQueuePage++;
        _ = RefreshCloudPageAsync();
    }

    private async Task RefreshCloudQueueAsync()
    {
        if (_cloudQueueReader is null)
        {
            return;
        }

        var all = await _cloudQueueReader.LoadAsync();
        var filter = TagOf(CloudQueueFilterCombo) ?? "All";

        var filtered = all
            .Where(i => filter == "All" || i.State.ToString() == filter)
            // 刚动过的排前面：队列可能有几千条，用户想看的是「现在这条到哪了」。
            .OrderByDescending(i => i.UpdatedAt)
            .ToList();

        // ⚠️ 从下拉的 `Tag` 里取（与「筛选」同一套约定），不再是自由输入的文本框 ——
        // 需求方 2026-10-14：每页数量只给 5 / 10 / 15 / 20 四档。
        // 取不到时（还没选中）回退 10，与 XAML 里的 `SelectedIndex="1"` 对齐。
        var pageSize = int.TryParse(TagOf(CloudQueuePageSizeCombo), out var size)
            ? Math.Clamp(size, 1, 200) : 10;

        var pages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
        _cloudQueuePage = Math.Clamp(_cloudQueuePage, 1, pages);

        var service = _host.Services.CloudUploads;

        // ⚠️ **只增删改，不重建**（2026-10-14）。原来是 `Items.Clear()` + 逐条 `Add`，
        // 而这一页每两秒刷一次 ⇒ 用户翻到队列中间，下一拍就被顶回顶部；
        // 队列几千条时这一条尤其难看（而且滚动条每一拍都在闪）。
        // 行是 record ⇒ **值相等就是「这一行没变」**，那一行连对象都不换。
        var rows = filtered
            .Skip((_cloudQueuePage - 1) * pageSize)
            .Take(pageSize)
            .Select(item => DescribeCloudQueueItem(item, service?.ProgressOf(item.EvidenceId)))
            .ToList();

        SyncRows(_cloudQueueRows, rows);

        // 设计图 `_45` 右上角那句（队列空着的时候）。
        CloudQueueNote.Text = filtered.Count == 0 ? "暂无录像" : string.Empty;
        CloudQueuePageText.Text = $"第 {_cloudQueuePage}/{pages} 页 · 共 {filtered.Count} 条";
        CloudQueuePrevButton.IsEnabled = _cloudQueuePage > 1;
        CloudQueueNextButton.IsEnabled = _cloudQueuePage < pages;
    }

    /// <summary>
    /// 把 <paramref name="target"/> 改成和 <paramref name="source"/> 一样，
    /// 但**值相等的行原样留着不动**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这是「队列不要每两秒重建一遍」的全部实现（C16）。逐项比的是
    /// <c>Equals</c>（行是 record ⇒ 逐字段比），所以只有真的变了的那一行会换对象，
    /// 其余的行连容器都不重建 ⇒ <c>ScrollViewer</c> 的滚动位置不受影响。
    /// </remarks>
    private static void SyncRows<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }

        for (var i = 0; i < source.Count; i++)
        {
            if (i >= target.Count)
            {
                target.Add(source[i]);
            }
            else if (!EqualityComparer<T>.Default.Equals(target[i], source[i]))
            {
                target[i] = source[i];
            }
        }
    }

    /// <summary>队列里的一行。</summary>
    /// <param name="progress">这一条传到哪儿了（0~1）；服务没起来、或者压根没在传时是 <see langword="null"/>。</param>
    private CloudQueueRow DescribeCloudQueueItem(UploadQueueItem item, double? progress)
    {
        var name = BaiduPanLayout.FileNameOf(item.RemotePath);
        var type = CloudBusinessTypeOf(item.RemotePath);

        // ⚠️ `StartedAt` 是 `DateTimeOffset`，落盘存的是 `+00:00`（A4，2026-10-16）：
        // 不 `ToLocalTime()` 就是拿 UTC 直接印，同一条录像在这里写 `07:06`、
        // 在回放页写 `15:06` —— 差 8 小时，用户会以为队列串了行。
        // 回放页那条路一直是带 `ToLocalTime()` 的写法，这里跟上。
        var meta = $"{Display.Bytes(item.SizeBytes)}｜开录 {item.StartedAt.ToLocalTime():MM-dd HH:mm}"
            + $"｜试过 {item.Attempts} 次"
            // 失败的那一行要说清「还会不会自己再试」：光写「失败」两个字，
            // 「1 分钟后自己会重试」与「要你动手」是同一句话 —— 而那是两件事。
            + (item.State == CloudUploadState.Failed
                ? item.NextAttemptAt is { } at
                    ? $"｜{at:HH:mm:ss} 自己再试"
                    : "｜不再自动重试"
                : string.Empty);

        // ⚠️ 进度条只在**两种**状态出现：传中（实时的），与已完成（满格）。
        // 「已完成」那一根不是装饰：同一列行里一眼扫过去，「传完的」与「还没轮到」
        // 差着一根条，比读文字快。**失败的那一行没有条** —— 传到几成就断了这件事
        // 现在没有落在盘上（服务在一轮结束时就把那个数撤了），要显示就只能编一个。
        var (fraction, showProgress) = item.State switch
        {
            CloudUploadState.Uploading => (progress ?? 0, true),
            CloudUploadState.Done => (1d, true),
            _ => (0d, false),
        };

        return new CloudQueueRow(
            name,
            item.EvidenceId,
            type is null ? string.Empty : BaiduPanLayout.TypeSegment(type.Value),
            type is null ? Visibility.Collapsed : Visibility.Visible,
            // ⚠️ 只给**档名**，颜色由模板那边的 `DataTrigger` 去订阅（见这一行的注释）。
            // 发货走蓝、退货走橙：两档都是现成的令牌，亮暗两套主题里都有值。
            type?.ToString() ?? string.Empty,
            CloudStateLabel(item.State),
            item.State.ToString(),
            meta,
            CloudQueueToolTip(item),
            fraction,
            showProgress ? Visibility.Visible : Visibility.Collapsed,
            CloudUploadStates.CanAbandon(item.State) ? Visibility.Visible : Visibility.Collapsed,
            CloudUploadStates.CanResume(item.State) ? Visibility.Visible : Visibility.Collapsed,
            CloudUploadStates.CanRetry(item.State) ? Visibility.Visible : Visibility.Collapsed);
    }

    /// <summary>
    /// 鼠标停上去才看的东西：远端路径，以及上一次的错。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>远端路径要留着</b>：用户上网页版找不到文件时，唯一能把话说清楚的凭据
    /// 就是它（它记的是上传那一刻的事实，不跟着后来的标签改动变）。
    /// 只是它从「铺在行里」挪到了提示上 —— 一行里塞三段话、长短还都不一样，
    /// 扫起来比读还慢。
    /// </remarks>
    private static string CloudQueueToolTip(UploadQueueItem item)
    {
        var lines = new List<string> { $"远端：{item.RemotePath}" };

        if (item.LastError is { Length: > 0 } error)
        {
            lines.Add($"上次出错：{error}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>这条远端路径落在「发货」还是「退货」那一层；认不出来就是 <see langword="null"/>。</summary>
    /// <remarks>
    /// ⚠️ 认的是**路径里那一段**，不是标签 —— 标签按 I5 可以改，而远端路径记的是事实。
    /// 分类名走 <see cref="BaiduPanLayout.TypeSegment"/>，与真正拼路径时用的是同一处；
    /// 在这儿另写一遍那两个中文词的话，改了那边这里会静静地认不出来（不报错，只是没徽章）。
    /// </remarks>
    private static BusinessType? CloudBusinessTypeOf(string remotePath)
    {
        var segments = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Contains(BaiduPanLayout.TypeSegment(BusinessType.Outbound)))
        {
            return BusinessType.Outbound;
        }

        return segments.Contains(BaiduPanLayout.TypeSegment(BusinessType.Return))
            ? BusinessType.Return
            : null;
    }

    private static string CloudStateLabel(CloudUploadState state) => state switch
    {
        CloudUploadState.Pending => "等待中",
        CloudUploadState.Uploading => "上传中",
        CloudUploadState.Done => "已完成",
        CloudUploadState.Abandoned => "已放弃",
        _ => "失败",
    };

    /// <summary>队列某一行的【放弃】：这一条不再传了。</summary>
    private async void OnCloudQueueAbandon(object sender, RoutedEventArgs e) =>
        await RunQueueRowActionAsync(sender, "已放弃上传", (s, id) => s.AbandonAsync(id));

    /// <summary>队列某一行的【恢复】：把它放回「等着传」。</summary>
    private async void OnCloudQueueResume(object sender, RoutedEventArgs e) =>
        await RunQueueRowActionAsync(sender, "已恢复上传", (s, id) => s.ResumeAsync(id));

    /// <summary>队列某一行的【重试】：**只把这一条**立刻再传一次。</summary>
    /// <remarks>
    /// ⚠️ 它是【立即重试失败上传】那颗全局按钮的补充，不是替代：那颗会把**所有**失败的
    /// 一起翻出来，队列里失败十几条、只想救其中一条时它就是错的工具
    /// （而且一起重试会一起撞那 10 次/小时的额度）。
    /// </remarks>
    private async void OnCloudQueueRetry(object sender, RoutedEventArgs e) =>
        await RunQueueRowActionAsync(sender, "已开始重试", (s, id) => s.RetryAsync(id));

    /// <summary>队列那一行两颗按钮共用的那一圈。</summary>
    /// <remarks>
    /// ⚠️ <b>两个动作都不弹确认框，而且是有意的。</b>
    /// 【放弃】不动本机那份录像、也不动网盘上的东西（I2），只改这一行的状态，
    /// 而且旁边就摆着【恢复】—— 为一个点错了也能一步撤回的动作弹一个模态框，
    /// 是在用「多一次点击」换「用户随手点一下确认」。真正不可逆的是【删除录像】，
    /// 那个才有框（见清理那一段）。
    /// </remarks>
    private async Task RunQueueRowActionAsync(
        object sender, string done, Func<CloudUploadService, string, Task<bool>> action)
    {
        var service = _host.Services.CloudUploads;

        if (service is null || (sender as FrameworkElement)?.DataContext is not CloudQueueRow row)
        {
            return;
        }

        try
        {
            _cloudNote = await action(service, row.EvidenceId)
                ? $"{done}：{row.Title}"
                // 说的不算数只有两个可能：这一行在点下去之前已经变了状态
                // （后台正好推进了它），或者它已经不在队列里了 —— 服务那边会留痕。
                : $"没改：{row.Title} 的状态刚刚变过，刷新一下再看。";
        }
        catch (Exception ex)
        {
            _cloudNote = $"改队列没成功：{ex.Message}";
        }
        finally
        {
            await RefreshCloudPageAsync();
        }
    }
}
