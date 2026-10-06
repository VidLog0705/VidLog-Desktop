using System.ComponentModel;
// ⚠️ `System.IO` 要显式写：这台 SDK 一开 `UseWPF` 就不再把 `System.IO` 与
// `System.Net.Http` 放进隐式 using 里了（`Path` 会与 `Shapes.Path` 撞名）——
// 所以这个文件里 `File` / `Path` 不像普通 .NET 工程那样随手可用。
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的命名空间都带进来，于是 ComboBox / KeyEventArgs
// 这类同名类型变成「不明确」。这里用**别名钉死成 WPF 的那套**。
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
// ⚠️ 同 MainWindow：`PixelFormats` 两边都有，不钉死会选错那个（画二维码时表现是编不过）。
using PixelFormats = System.Windows.Media.PixelFormats;
using TextBlock = System.Windows.Controls.TextBlock;
using RadioButton = System.Windows.Controls.RadioButton;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using ToolTipService = System.Windows.Controls.ToolTipService;
using VidLog.Desktop.App.Platform;
using VidLog.Desktop.Core;
using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Clock;
using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Configuration;
using VidLog.Desktop.Core.Diagnostics;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;
using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.App;

public partial class SettingsWindow : Window
{
    // ─────────────────────────────────────────────
    // 百度网盘上传（批次 5，设计图 `_45` / `_46`）
    // ─────────────────────────────────────────────

    /// <summary>「补传范围」下拉（设计图 `_46`）。</summary>
    private static readonly (string Tag, string Label)[] CloudScopes =
    [
        ("All", "全部"),
        ("FromDate", "自定起始日"),
    ];

    /// <summary>队列下面那个「筛选」下拉。四档与队列状态一一对应。</summary>
    private static readonly (string Tag, string Label)[] CloudFilters =
    [
        ("All", "全部"),
        ("Pending", "等待中"),
        ("Uploading", "上传中"),
        ("Done", "已完成"),
        ("Failed", "失败"),
    ];

    /// <summary>把设置读进这一页的控件。只在开窗时跑一次（与别的节一样）。</summary>
    /// <remarks>
    /// ⚠️ <b>刻意不在「切到这一页」时重读</b>：那样会把用户还没保存的改动抹掉
    /// （改了补传范围、切走看一眼别处、再切回来 —— 改动没了）。
    /// 别的节也不重读，这里不能例外。
    /// </remarks>
    private void LoadCloud()
    {
        var settings = _host.Settings.Cloud;

        CloudAutoUploadToggle.IsChecked = settings.AutoUpload;
        CloudCompareToggle.IsChecked = settings.CompareAndBackfill;

        foreach (var (tag, label) in CloudScopes)
        {
            CloudScopeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        }

        SelectByTag(CloudScopeCombo, settings.BackfillScope.ToString());
        CloudFromDatePicker.SelectedDate = settings.BackfillFrom?.LocalDateTime;
        SyncCloudScopeControls();

        for (var n = 1; n <= 8; n++)
        {
            CloudParallelCombo.Items.Add(n.ToString());
        }

        CloudParallelCombo.SelectedItem = settings.ParallelUploads.ToString();

        CloudAppNameBox.Text = settings.AppName;

        foreach (var (tag, label) in CloudFilters)
        {
            CloudQueueFilterCombo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        }

        SelectByTag(CloudQueueFilterCombo, "All");
    }

    /// <summary>「全部」时那格日期框是禁用的（见 <see cref="SyncCloudScopeControls"/>）。</summary>
    private void SyncCloudScopeControls() =>
        CloudFromDatePicker.IsEnabled = TagOf(CloudScopeCombo) == "FromDate";

    private void OnCloudAutoUploadChanged(object sender, RoutedEventArgs e) => MarkDirty();
    private void OnCloudCompareChanged(object sender, RoutedEventArgs e) => MarkDirty();
    private void OnCloudFromDateChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();

    /// <summary>补传范围变了 —— 顺手把「起始日」那一格的可用性跟上去。</summary>
    private void OnCloudScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        MarkDirty();
        SyncCloudScopeControls();
    }

    private void OnCloudQueueFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // 换了筛选就回第一页：留在第 3 页而新的筛选只有 1 页，会看到一片空白。
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

    /// <summary>
    /// 这一页整页刷新（能不能用、账号、状态、队列）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这里一个字都不往「可编辑」的控件里写</b>（开关、下拉、日期、输入框）——
    /// 它每两秒被定时器喊一次，写进去就是在跟用户抢方向盘。
    /// 只写那些**读的**东西：说明、状态、按钮可用性、队列列表。
    /// </remarks>
    private async Task RefreshCloudPageAsync()
    {
        if (_cloudRefreshing)
        {
            // 定时器与「切到这一页」会撞上。**不排队** —— 再跑一遍读的是同一份东西。
            return;
        }

        _cloudRefreshing = true;

        try
        {
            await RefreshCloudCoreAsync();
        }
        catch (Exception ex)
        {
            // 刷不出来**不许把窗口弄崩**（I4 的同一条精神），但也不许装作没事：
            // 这一页上全是「传没传上去」，静默失败等于让用户以为都传好了。
            CloudAccountNote.Text = $"⚠️ 这一页没刷新出来：{ex.Message}";
        }
        finally
        {
            _cloudRefreshing = false;
        }
    }

    private async Task RefreshCloudCoreAsync()
    {
        var target = SelectedArchiveTarget();
        var service = _host.Services.CloudUploads;

        if (target.Kind != ArchiveBackendKind.Cloud || service is null)
        {
            // 整页禁用 + 说清为什么（踩坑 #13：绝不渲染一个按下去没反应的开关）。
            // ⚠️ 队列那张卡**不禁用**：它只是显示，而「队列里积了多少」正是
            // 用户想看的 —— 灰掉它并不能让任何东西更安全。
            CloudAccountCard.IsEnabled = false;
            CloudUploadCard.IsEnabled = false;
            CloudDisabledNote.Visibility = Visibility.Visible;
            CloudDisabledNote.Text = CloudUnavailableReason(target);

            CloudAccountNote.Text = string.Empty;
            CloudQueueNote.Text = "这一页现在不工作";
            CloudQueueList.Items.Clear();
            CloudQueuePageText.Text = string.Empty;
            CloudQueuePrevButton.IsEnabled = false;
            CloudQueueNextButton.IsEnabled = false;

            // 归档层不是网盘（或服务没起来）时这一页整页不工作 —— 那张码也一并收起：
            // 一边写着「这一页不工作」、一边摆一张扫码就能授权的图，是自己打自己。
            ShowCloudLoginQr(null);
            return;
        }

        CloudAccountCard.IsEnabled = true;
        CloudUploadCard.IsEnabled = true;
        CloudDisabledNote.Visibility = Visibility.Collapsed;

        var session = service.Session;

        // 设备码还没批准 → 按网盘要求的最小间隔问一次（不是每两秒问一次：
        // 问得太勤是接口在文档里明说会被拒的行为）。
        if (_cloudLogin is not null && DateTimeOffset.Now >= _cloudNextPollAt)
        {
            await PollCloudLoginAsync(service);
        }

        var status = await service.StatusAsync();

        CloudLoginButton.IsEnabled = !_cloudBusy && !session.IsLoggedIn && _cloudLogin is null;
        CloudLogoutButton.IsEnabled = !_cloudBusy && session.IsLoggedIn;
        CloudSyncButton.IsEnabled = !_cloudBusy;

        // 没有失败的就没什么可重试的 —— 但**不是**禁用到底：`Failed` 会在
        // 这一页开着的时候由后台变出来，所以它每两秒重算一次。
        CloudRetryButton.IsEnabled = !_cloudBusy && status.Failed > 0;

        // 二维码跟着 _cloudLogin 走：它在就画出来，不在就收起。
        // 放在这里（而不是 OnCloudLogin / PollCloudLoginAsync 里各写一次）是刻意的：
        // 显隐只有一个来源，批准、超时、退出登录三条路都不会漏掉收起那一步。
        ShowCloudLoginQr(_cloudLogin);

        CloudAccountNote.Text = DescribeCloudAccount(session, status);

        await RefreshCloudQueueAsync();
    }

    /// <summary>
    /// 把设备码登录的二维码画出来；没有待批准的登录就收起。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 内容是 <see cref="BaiduDeviceCode.QrPayload"/>，照 009 的「二维码内容拼接规则」
    /// 逐字拼出来 —— 手机上扫一下直接落到**已经填好验证码**的授权页上，
    /// 用户只剩「登录 + 同意」两步，不用把码从电脑屏幕手抄进手机。
    /// </para>
    /// <para>
    /// ⚠️ <b>画不出来不许把这一页带下水。</b>这个函数在每两秒一次的刷新节拍里跑，
    /// 异常抛出去会被 <c>RefreshCloudPageAsync</c> 兜住、把整页文字换成一句错误 ——
    /// 而「网址 + 那串码」本来就是能用的（手抄一样办得成）。
    /// 为了一张画不出来的图把那条路也盖掉，是拿能用的换不能用的。
    /// </para>
    /// </remarks>
    private void ShowCloudLoginQr(BaiduDeviceCode? pending)
    {
        if (pending is null)
        {
            CloudQrPanel.Visibility = Visibility.Collapsed;
            CloudQrImage.Source = null;
            _cloudQrUserCode = null;
            return;
        }

        // 码没变就原样放着（理由见 _cloudQrUserCode 的说明）。
        if (CloudQrPanel.Visibility == Visibility.Visible
            && string.Equals(_cloudQrUserCode, pending.UserCode, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var modules = EnrollQr.Modules(pending.QrPayload);

            // 一个模块一个像素，放大交给 XAML 那个 NearestNeighbor（与 EnrollWindow 同一手法）。
            var bitmap = BitmapSource.Create(
                modules.GetLength(0), modules.GetLength(1), 96, 96,
                PixelFormats.Gray8, null, EnrollQr.Pixels(modules), modules.GetLength(0));

            bitmap.Freeze();

            CloudQrImage.Source = bitmap;
            _cloudQrUserCode = pending.UserCode;
            CloudQrPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // 不静默吞掉（§6.1 的 catch 那一条）：走 _cloudNote，
            // 也就是这一页顶上那句用户看得见的话。按【登录百度网盘】会把它清掉。
            CloudQrPanel.Visibility = Visibility.Collapsed;
            CloudQrImage.Source = null;
            _cloudQrUserCode = null;
            _cloudNote = $"二维码没画出来（{ex.Message}），照上面那个网址和验证码手输一样能登录。";
        }
    }

    /// <summary>这一页现在为什么不能用（**两句不同的话，别混**）。</summary>
    private string CloudUnavailableReason(ArchiveTarget target) =>
        target.Kind != ArchiveBackendKind.Cloud
            ? "这一页只在「存储与备份」里的归档层选成**百度网盘**时才有用 —— 现在选的是"
              + $"「{target.Label}」。换过去并保存、重启之后，这里的登录与上传才会真的跑起来。"
            // 档位对了，服务却没起来，只有两个可能：还没重启，或者压根没配凭据。
            : _host.Settings.ArchiveBackend != ArchiveBackendKind.Cloud
                ? "归档层刚改成百度网盘，**保存并重启**之后这一页才会起来。"
                : BaiduPanCredentials.MissingMessage;

    /// <summary>
    /// 「账号与上传状态」那张卡上的一段话。
    /// </summary>
    /// <remarks>
    /// ⚠️ 「已停用」那句是设计图 `_45` 的**原文**，按的是**已保存**的那个开关
    /// （原文写的就是「勾选…**并保存后**」）—— 用界面上还没保存的那个值去判的话，
    /// 这句话会在用户勾上的那一刻就变成「已启用」，而他还没按【应用】。
    /// </remarks>
    private string DescribeCloudAccount(BaiduPanSession session, UploadStatus status)
    {
        var lines = new List<string>();

        if (_cloudNote is { Length: > 0 } note)
        {
            lines.Add(note);
        }

        if (!status.Enabled)
        {
            lines.Add(
                "已停用：勾选「启用自动上传」并保存后，本机新录制的录像才会上传。"
                + (CloudAutoUploadToggle.IsChecked == true
                    ? "（已经勾上了，按【应用】或【确定】之后生效。）"
                    : string.Empty));
        }
        else
        {
            lines.Add("已启用：本机新录制的录像会自动上传到百度网盘。");
        }

        if (_cloudLogin is { } pending)
        {
            var minutes = Math.Max(1, (int)(_cloudLoginExpiresAt - DateTimeOffset.Now).TotalMinutes);

            lines.Add(
                $"请打开 {pending.VerificationUrl} 输入验证码 {pending.UserCode}，批准后这里会自动变成已登录"
                + $"（这串码还有约 {minutes} 分钟有效）。");
        }
        else if (!session.IsLoggedIn)
        {
            lines.Add("还没登录百度网盘。");
        }
        else if (session.Login is { } login)
        {
            lines.Add($"已登录：{login.DisplayName}（登录信息记在本机，过期前会自动续期）。");
        }

        lines.Add(
            $"队列 {status.Total} 条：等待 {status.Pending}、上传中 {status.Uploading}、"
            + $"已完成 {status.Done}、失败 {status.Failed}；此刻正在传 {status.Active} 条。"
            + (status.LastChecked is { } checkedAt
                ? $"最近一次与网盘对比：{checkedAt:yyyy-MM-dd HH:mm:ss}。"
                : "还没与网盘对比过。"));

        if (status.LastError is { Length: > 0 } error)
        {
            lines.Add($"⚠️ 最近一次出错：{error}");
        }

        return string.Join("\n", lines);
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

        var pageSize = int.TryParse(CloudQueuePageSizeBox.Text, out var size)
            ? Math.Clamp(size, 1, 200) : 10;

        var pages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
        _cloudQueuePage = Math.Clamp(_cloudQueuePage, 1, pages);

        CloudQueueList.Items.Clear();

        foreach (var item in filtered.Skip((_cloudQueuePage - 1) * pageSize).Take(pageSize))
        {
            CloudQueueList.Items.Add(DescribeCloudQueueItem(item));
        }

        // 设计图 `_45` 右上角那句（队列空着的时候）。
        CloudQueueNote.Text = filtered.Count == 0 ? "暂无录像" : string.Empty;
        CloudQueuePageText.Text = $"第 {_cloudQueuePage}/{pages} 页 · 共 {filtered.Count} 条";
        CloudQueuePrevButton.IsEnabled = _cloudQueuePage > 1;
        CloudQueueNextButton.IsEnabled = _cloudQueuePage < pages;
    }

    /// <summary>队列里的一行。</summary>
    /// <remarks>
    /// ⚠️ 远端路径**要显示出来**：用户上网页版找不到文件时，
    /// 唯一能把话说清楚的凭据就是它（它记的是上传那一刻的标签，不跟着后来的改动变）。
    /// </remarks>
    private static string DescribeCloudQueueItem(UploadQueueItem item) =>
        $"{CloudStateLabel(item.State)}｜{BaiduPanLayout.FileNameOf(item.RemotePath)}｜"
        + $"{Display.Bytes(item.SizeBytes)}｜开录 {item.StartedAt:MM-dd HH:mm}｜试过 {item.Attempts} 次"
        + (item.LastError is { Length: > 0 } error ? $"\n　　⚠️ {error}" : string.Empty)
        + $"\n　　远端：{item.RemotePath}";

    private static string CloudStateLabel(CloudUploadState state) => state switch
    {
        CloudUploadState.Pending => "等待中",
        CloudUploadState.Uploading => "上传中",
        CloudUploadState.Done => "已完成",
        _ => "失败",
    };

    /// <summary>
    /// 【登录百度网盘】：拿一串设备码，把浏览器打开，然后**不挡着界面**等用户去点同意。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>不做模态对话框。</b>那个框会挡住设置窗，而用户此刻正是要去浏览器里操作 ——
    /// 框杵在那儿只会被当成「卡住了」。状态记在 <see cref="_cloudLogin"/> 上，
    /// 由这一页每两秒的刷新节拍去问一次。
    /// </remarks>
    private async void OnCloudLogin(object sender, RoutedEventArgs e)
    {
        var service = _host.Services.CloudUploads;

        if (service is null)
        {
            return;
        }

        _cloudBusy = true;

        try
        {
            var pending = await service.Session.BeginLoginAsync();

            _cloudLogin = pending;
            _cloudNextPollAt = DateTimeOffset.Now + TimeSpan.FromSeconds(Math.Max(1, pending.IntervalSeconds));
            _cloudLoginExpiresAt = DateTimeOffset.Now + TimeSpan.FromSeconds(Math.Max(60, pending.ExpiresInSeconds));

            // 打不开浏览器**不是失败**：码在界面上写着，用户手打那个网址一样能办成。
            var problem = ShellOpen.Try(pending.VerificationUrl);

            _cloudNote = problem is null
                ? null
                : $"浏览器没自动打开（{problem}），手动打开 {pending.VerificationUrl} 也一样。";
        }
        catch (Exception ex) when (ex is BaiduPanException or HttpRequestException)
        {
            _cloudNote = $"登录没起来：{ex.Message}";
        }
        finally
        {
            _cloudBusy = false;
            await RefreshCloudPageAsync();
        }
    }

    /// <summary>问一次「批准了没有」。批准了就记下来 —— <c>PollLoginAsync</c> 会把令牌落盘。</summary>
    private async Task PollCloudLoginAsync(CloudUploadService service)
    {
        var pending = _cloudLogin!;

        _cloudNextPollAt = DateTimeOffset.Now + TimeSpan.FromSeconds(Math.Max(1, pending.IntervalSeconds));

        if (DateTimeOffset.Now >= _cloudLoginExpiresAt)
        {
            _cloudLogin = null;
            _cloudNote = "那串验证码过期了，请重新点【登录百度网盘】。";
            return;
        }

        try
        {
            if (await service.Session.PollLoginAsync(pending.DeviceCode) is not null)
            {
                _cloudLogin = null;
                _cloudNote = "百度网盘登录成功。";
            }
        }
        catch (Exception ex) when (ex is BaiduPanException or HttpRequestException)
        {
            // ⚠️ 「网络抖了一下」与「用户还没点同意」**不能混成同一句话**：
            // 前者要重试，后者只要等。这里把原因记下来但**不放弃等** ——
            // 那串码还有效，下一拍照样问。
            _cloudNote = $"问授权状态时出错（会继续试）：{ex.Message}";
        }
    }

    /// <summary>【退出登录】。⚠️ 只删本机记着的登录信息，**不碰网盘上的任何文件**。</summary>
    private async void OnCloudLogout(object sender, RoutedEventArgs e)
    {
        var service = _host.Services.CloudUploads;

        if (service is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "退出登录只会删掉本机记着的登录信息，**不会**删除百度网盘上的任何文件。\n\n要继续吗？",
            "退出百度网盘",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _cloudBusy = true;

        try
        {
            await service.Session.LogoutAsync();
            _cloudLogin = null;
            _cloudNote = "已退出登录。网盘上的文件一个都没动。";
        }
        catch (Exception ex)
        {
            _cloudNote = $"退出登录没成功：{ex.Message}";
        }
        finally
        {
            _cloudBusy = false;
            await RefreshCloudPageAsync();
        }
    }

    private async void OnCloudSync(object sender, RoutedEventArgs e) =>
        await RunCloudActionAsync("立即对比同步", (service, ct) => service.SyncNowAsync(ct));

    private async void OnCloudRetry(object sender, RoutedEventArgs e) =>
        await RunCloudActionAsync("立即重试失败上传", (service, ct) => service.RetryFailedAsync(ct));

    /// <summary>【立即对比同步】/【立即重试失败上传】共用的那一圈。</summary>
    /// <remarks>
    /// ⚠️ <b>跑的时候那两个按钮要禁用</b>：一次同步可能要传好久，而重复点它
    /// 只会得到「已经有一轮在跑了，这次什么也没做」—— 那看起来像失败。
    /// 队列那张卡**照旧两秒刷一次**，所以进度是活的。
    /// </remarks>
    private async Task RunCloudActionAsync(
        string title, Func<CloudUploadService, CancellationToken, Task<int>> action)
    {
        var service = _host.Services.CloudUploads;

        if (service is null)
        {
            return;
        }

        _cloudBusy = true;
        _cloudNote = $"{title}：正在跑，进度见下面的队列。";
        await RefreshCloudPageAsync();

        try
        {
            var count = await action(service, CancellationToken.None);

            _cloudNote = count > 0
                ? $"{title}：这次传上去 {count} 条。"
                : $"{title}：这次没有新传上去的 —— 要么网盘上都已经有了，要么都失败了。"
                  + "失败原因写在下面队列的每一行上。";
        }
        catch (Exception ex)
        {
            _cloudNote = $"{title}出错：{ex.Message}";
        }
        finally
        {
            _cloudBusy = false;
            await RefreshCloudPageAsync();
        }
    }

    // ─────────────────────────────────────────────
    // 小的取值助手
    // ─────────────────────────────────────────────

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static string? TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    /// <summary>把一组横排的单选按 <c>Tag</c> 选中一个。</summary>
    /// <remarks>
    /// 与 <see cref="SelectByTag"/> 同一件事，只是单选按钮不是 <c>Items</c> 集合 ——
    /// 而这几个选项**必须是横排的**（规格 §3.1.7：「横排（**不用下拉**）」）。
    /// </remarks>
    private static void SelectRadio(IEnumerable<RadioButton> group, string tag)
    {
        foreach (var button in group)
        {
            button.IsChecked = string.Equals(button.Tag as string, tag, StringComparison.Ordinal);
        }
    }

    /// <summary>横排单选里被选中的那个的 <c>Tag</c>。</summary>
    private static string? TagOf(IEnumerable<RadioButton> group) =>
        group.FirstOrDefault(b => b.IsChecked == true)?.Tag as string;
}
