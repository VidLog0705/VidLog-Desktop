using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography;
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
// ⚠️ `Brush` 两边都有（`System.Drawing.Brush` 是 WinForms 那一支），不钉死会「不明确」。
using Brush = System.Windows.Media.Brush;
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
using VidLog.Desktop.Core.Labels;
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

        // ⚠️ **只在这儿设一次**，之后一律改 `_cloudQueueRows` 的内容，绝不
        // 重新赋值 `ItemsSource`、也绝不 `Items.Clear()`（见 `RefreshCloudQueueAsync`）。
        CloudQueueList.ItemsSource = _cloudQueueRows;
        FitCloudQueueHeight();
    }


    /// <summary>「全部」时那格日期框是禁用的（见 <see cref="SyncCloudScopeControls"/>）。</summary>
    private void SyncCloudScopeControls() =>
        CloudFromDatePicker.IsEnabled = TagOf(CloudScopeCombo) == "FromDate";

    /// <summary>
    /// 自动上传开关拨了 —— 它**同时是「有没有第二份」的一个判据**（见
    /// <see cref="ArchiveTarget.FromConfig"/>：备份位置表空着时，第二份就是网盘，
    /// 而这个开关管着网盘到底传不传），所以保留期那一块要跟着重算。
    /// </summary>
    private void OnCloudAutoUploadChanged(object sender, RoutedEventArgs e)
    {
        MarkDirty();
        ShowRetention();
    }

    private void OnCloudCompareChanged(object sender, RoutedEventArgs e) => MarkDirty();
    private void OnCloudFromDateChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();

    /// <summary>补传范围变了 —— 顺手把「起始日」那一格的可用性跟上去。</summary>
    private void OnCloudScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        MarkDirty();
        SyncCloudScopeControls();
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
        var target = CurrentArchiveTarget();
        var service = _host.Services.CloudUploads;

        // ⚠️ 只有一种情况**整页禁用**：配了网络位置（网络硬盘 / NAS）——
        // 那时第二份走它、按规则不传网盘，这一页本来就不该拿来当第二份。
        //
        // ⚠️ 「【启用自动上传】关着」**不是**禁用理由，恰恰相反：界面正叫用户去
        // 把它打开。把开关自己也灰掉，用户关一次就再也打不开了 ——
        // 而服务是**启动那一刻**按当时的归档目标装配的（`DesktopServices.Create`），
        // 关着启动 ⇒ 服务压根不建 ⇒ 又落回「禁用」这一支，闭成一个死环。
        // （踩坑 #13「不许渲染一个按下去没反应的开关」的反面。）
        if (target.Kind == ArchiveBackendKind.Directory)
        {
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

            HideCloudActionProgress();
            return;
        }

        // 服务没起来（刚打开开关还没重启、或者压根没配凭据）：**上传设置那几张卡
        // 照样得能用** —— 改设置、按【应用】是唯一条出路，灰掉它等于把人锁在门外。
        // ⚠️ 队列那张卡同样不禁用：它只是显示，而「队列里积了多少」正是
        // 用户想看的 —— 灰掉它并不能让任何东西更安全。
        CloudUploadCard.IsEnabled = true;

        if (service is null)
        {
            // ⚠️ 只有账号卡要真服务（登录 / 同步 / 重试都走它），没服务时必须灰。
            CloudAccountCard.IsEnabled = false;
            CloudDisabledNote.Visibility = Visibility.Visible;
            CloudDisabledNote.Text = CloudUnavailableReason(target);

            CloudAccountNote.Text = string.Empty;
            CloudQueueNote.Text = "这一页现在不工作";
            CloudQueueList.Items.Clear();
            CloudQueuePageText.Text = string.Empty;
            CloudQueuePrevButton.IsEnabled = false;
            CloudQueueNextButton.IsEnabled = false;

            HideCloudActionProgress();
            return;
        }

        CloudAccountCard.IsEnabled = true;
        CloudDisabledNote.Visibility = Visibility.Collapsed;

        var session = service.Session;

        var status = await service.StatusAsync();

        // 网盘容量（建议 5）。**每 60 秒最多问一次** —— 这一页每 2 秒刷一次，
        // 照节拍问就是拿那 10 次/小时的额度去换一句「还剩多少」。
        // 没登录时不问：问了必定失败，还会白留一条日志。
        if (session.IsLoggedIn && DateTimeOffset.Now >= _cloudQuotaNextAt)
        {
            // ⚠️ 时刻**先**推、再 await：反过来的话，一次慢请求会让下一拍又发一遍，
            // 而「限频」在这儿的全部意义就是别多发。
            _cloudQuotaNextAt = DateTimeOffset.Now + TimeSpan.FromMinutes(1);
            _cloudQuota = await service.QuotaAsync();
        }

        CloudLoginButton.IsEnabled = !_cloudBusy && !session.IsLoggedIn;
        CloudLogoutButton.IsEnabled = !_cloudBusy && session.IsLoggedIn;
        CloudSyncButton.IsEnabled = !_cloudBusy;

        // 没有失败的就没什么可重试的 —— 但**不是**禁用到底：`Failed` 会在
        // 这一页开着的时候由后台变出来，所以它每两秒重算一次。
        CloudRetryButton.IsEnabled = !_cloudBusy && status.Failed > 0;

        // 【立即对比同步】跑起来之后那颗按钮换成一截进度条（见 RunCloudActionAsync）；
        // 进度本身由下面这一句按这一页的节拍刷新。
        UpdateCloudActionProgress(service);

        CloudAccountNote.Text = DescribeCloudAccount(session, status);

        await RefreshCloudQueueAsync();
    }

    /// <summary>这一页为什么不全工作（**三种情况，各说各的真话**）。</summary>
    private string CloudUnavailableReason(ArchiveTarget target) =>
        target.Kind == ArchiveBackendKind.Directory
            // 配了网络位置 ⇒ 按规则第二份走它、本来就不走网盘。
            ? $"「存储与备份」里配了「{target.Label}」，录像的第二份走它、不走百度网盘。"
              + "想改用百度网盘的话，把那张备份位置表清空、保存并重启。"
            // 没网络位置、也不是网盘 ⇒ 只剩一种可能：【启用自动上传】被关掉了。
            : target.Kind == ArchiveBackendKind.LocalDisk
                ? "【启用自动上传】现在关着，录像不会往网盘传。把它打开，"
                  + "录像存到本机的同时就会自动传到百度网盘。"
                // 推算是「网盘」，服务却没起来，只有两个可能：还没重启，或者压根没配凭据。
                // ⚠️ 这一句落在 `CloudDisabledNote`（TextBlock）上，**不认 Markdown** ——
                // 加了 `**` 会连星号一起显示出来（与 `ArchivePublishResult.Conflict` 同一条）。
                : _host.Settings.Archive.Kind != ArchiveBackendKind.Cloud
                    ? "备份位置刚改成走百度网盘，保存并重启之后这一页才会起来。"
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

        if (!session.IsLoggedIn)
        {
            lines.Add("还没登录百度网盘。点【登录百度网盘】会在本程序里开一个小窗，"
                + "在那儿登录并同意授权就行。");
        }
        else if (session.Login is { } login)
        {
            lines.Add($"已登录：{login.DisplayName}（登录信息记在本机，过期前会自动续期）。");
        }

        // 剩余容量（建议 5）。⚠️ 只在登录之后说：没登录时它必然是空的，
        // 而那一行「还没登录」已经把话说完了。
        if (session.IsLoggedIn)
        {
            // ⚠️ 用的是 `FreeBytes`（自己算的 Total-Used），**不是**接口回的那个
            // `free` 字段 —— 那个是「免费容量」，拿它当剩余会把快满的网盘显示成空的。
            lines.Add(_cloudQuota is { } quota
                ? $"网盘还剩 {Display.Bytes(quota.FreeBytes)}"
                  + $"（已用 {Display.Bytes(quota.UsedBytes)}／共 {Display.Bytes(quota.TotalBytes)}，"
                  + "每分钟更新一次）。"
                // 拿不到**不影响上传**，所以要说清「这不是故障」，否则用户会以为传不上去了。
                : "网盘容量还没问出来（不影响上传）。");
        }

        // ⚠️ **只列非零的那几档**：从前是「等待 0、上传中 0、已完成 0、失败 0、已放弃 0」
        // 一字排开 —— 五行里四行都是 0，真正要看的那一个数被埋在中间。
        // 队列空着时索性一句话说完（那正是设计图 `_45` 右上角那句「暂无录像」的位置）。
        var counts = new List<string>();

        void CountIfAny(int count, string label)
        {
            if (count > 0)
            {
                counts.Add($"{label} {count}");
            }
        }

        CountIfAny(status.Pending, "等待");
        CountIfAny(status.Uploading, "上传中");
        CountIfAny(status.Done, "已完成");
        CountIfAny(status.Failed, "失败");
        CountIfAny(status.Abandoned, "已放弃");

        lines.Add(counts.Count == 0
            ? "队列里现在没有录像。"
            : $"队列 {status.Total} 条：{string.Join("、", counts)}"
              + (status.Active > 0 ? $"；此刻正在传 {status.Active} 条。" : "。")
              + (status.LastChecked is { } checkedAt
                  ? $"最近一次与网盘对比：{checkedAt:yyyy-MM-dd HH:mm:ss}。"
                  : "还没与网盘对比过。"));

        if (status.LastError is { Length: > 0 } error)
        {
            lines.Add($"⚠️ 最近一次出错：{error}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 【登录百度网盘】：开一个**本机回环**接住百度的回调，同时弹一个内嵌浏览器窗。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 四步：起监听 → 拼授权 URL（带上一次性的 <c>state</c>）→ 开窗 →
    /// 拿接住的授权码换令牌。需求方 2026-10-14 拍板走这条（乙-1）。
    /// </para>
    /// <para>
    /// ⚠️ <b>授权 URL 一个字都不许进日志、也不许显示在界面上</b>：
    /// 它的查询串里带着 <c>client_id</c>（= AppKey）与 <c>state</c>。
    /// </para>
    /// <para>
    /// ⚠️ <b>这是个模态窗，而且是有意的。</b>与设备码那版相反 —— 那版不能模态，
    /// 因为用户要跑去**别的程序**里操作；这一版用户就在这个窗里操作，
    /// 它挡着设置窗正是对的（挡不住反而会让人在登录没完成时就改别的设置）。
    /// 窗自己会关：接到回调就关，等满 5 分钟也关。
    /// </para>
    /// </remarks>
    private async void OnCloudLogin(object sender, RoutedEventArgs e)
    {
        var service = _host.Services.CloudUploads;

        if (service is null)
        {
            return;
        }

        var credentials = BaiduPanCredentials.FromEnvironment();

        if (credentials is null)
        {
            // 连 AppKey 都没有就没必要开窗了 —— 那扇窗只会停在百度的报错页上。
            _cloudNote = BaiduPanCredentials.MissingMessage;
            await RefreshCloudPageAsync();
            return;
        }

        _cloudBusy = true;
        _cloudNote = null;
        await RefreshCloudPageAsync();

        // ⚠️ 一次性的、随机的、每次登录重来一个。它是**防登录 CSRF** 的那一道闸：
        // 没有它，本机随便哪个程序都能往 17895 塞一个 code，把**别人的**百度账号
        // 绑到用户的 VidLog 上。校验在 `BaiduLoopbackListener` 里（那边有测试）。
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        await using var listener = new BaiduLoopbackListener(state, _host.Logger);

        try
        {
            listener.Start();
        }
        catch (InvalidOperationException ex)
        {
            // 端口被占着（最常见的是 PackingProof 开着 —— 它用的是同一个口号）。
            // 那句话在 Core 里已经写成人话了，这里只是让它露面 + 留痕。
            _host.Logger.Log(LogLevel.Warn, "网盘", $"登录没起来：{ex.Message}");
            _cloudNote = $"登录没起来：{ex.Message}";
            _cloudBusy = false;
            await RefreshCloudPageAsync();
            return;
        }

        // ⚠️ 这两处用的 **必须是同一个 redirect_uri**（`listener.RedirectUri`）。
        // 各拼一份的话，表现是换令牌时报 redirect_uri 不匹配 —— 一个看起来要回
        // 百度控制台查、实际一行代码就能修好的错（改造清单第 23 条）。
        var window = new BaiduLoginWindow(
            BaiduPanClient.BuildPassportLoginUrl(
                BaiduPanClient.BuildAuthorizeUrl(credentials.AppKey, listener.RedirectUri, state)),
            listener)
        {
            Owner = this,
        };

        window.ShowDialog();

        var callback = window.Callback;

        if (callback?.Code is { Length: > 0 } code)
        {
            try
            {
                var login = await service.Session.LoginAsync(code, listener.RedirectUri);

                _cloudNote = $"百度网盘登录成功：{login.DisplayName}。";

                // 容量是**上一个账号**的读数，留着就是在显示别人的数字；
                // 清掉它、并把下一拍放开，登录后的下一拍就会重问。
                _cloudQuota = null;
                _cloudQuotaNextAt = DateTimeOffset.MinValue;
            }
            catch (Exception ex) when (ex is BaiduPanException or HttpRequestException)
            {
                // ⚠️ 授权码**十分钟过期且一次性** ⇒ 失败就是这次得重来。
                // 不吞：说清是哪一步、以及「再点一次就行」。
                _host.Logger.Log(LogLevel.Warn, "网盘", $"拿授权码换令牌失败：{ex.Message}");
                _cloudNote = $"登录没成：{ex.Message}（再点一次【登录百度网盘】重来。）";
            }
        }
        else if (callback?.Error is { Length: > 0 } error)
        {
            // 用户自己在百度那一页点了「拒绝」—— 这不是故障。
            _cloudNote = $"没有授权成功（百度回的：{error}）。想再试就再点一次【登录百度网盘】。";
        }
        else if (window.Failure is { Length: > 0 } failure)
        {
            _host.Logger.Log(LogLevel.Warn, "网盘", failure);
            _cloudNote = failure;
        }
        else
        {
            // ⚠️ **超时要留痕**（§6.1）。这一条正是「用户说登录转了半天没反应」时
            // 唯一能查的凭据 —— 2026-10-14 探针期就出过一次「窗开着、回环一个回调都
            // 没接到」，那次因为没留痕，根因至今没查明。只记「没等到」，
            // **不记 URL**（授权页地址的查询串里有 AppKey 与 state）。
            _host.Logger.Log(LogLevel.Warn, "网盘", "网盘登录：等了 5 分钟没等到百度的回调");
            _cloudNote = "等了 5 分钟没有等到百度的回调（窗关掉了）。再点一次【登录百度网盘】重来。";
        }

        _cloudBusy = false;
        await RefreshCloudPageAsync();
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
            // ⚠️ 不写 `**`：MessageBox 不认 Markdown，会连星号一起显示。
            "退出登录只会删掉本机记着的登录信息，不会删除百度网盘上的任何文件。\n\n要继续吗？",
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
            // 容量是**上一个账号**的读数，留着就是在显示别人的数字。
            // 下一次登录时那一拍会立刻重问（`_cloudQuotaNextAt` 一并清掉）。
            _cloudQuota = null;
            _cloudQuotaNextAt = DateTimeOffset.MinValue;
            _cloudNote = "已退出登录。网盘上的文件一个都没动。";
        }
        catch (Exception ex)
        {
            // ⚠️ 补的一条（§6.1 核 diff 时撞见的既有缺口，不在本轮改动里）：
            // 这一句原来只写在界面上。退登录失败时用户看到的是一句马上就被
            // 下一拍刷掉的话，而盘上**一个字都没留** —— 事后没人说得清是
            // 删不掉那个登录文件，还是别的。与上面每一条 catch 对齐。
            _host.Logger.Log(LogLevel.Warn, "网盘", $"退出登录没成功：{ex.Message}");
            _cloudNote = $"退出登录没成功：{ex.Message}";
        }
        finally
        {
            _cloudBusy = false;
            await RefreshCloudPageAsync();
        }
    }

    private async void OnCloudSync(object sender, RoutedEventArgs e) =>
        await RunCloudActionAsync("立即对比同步", sender as Button, (s, ct) => s.SyncNowAsync(ct));

    private async void OnCloudRetry(object sender, RoutedEventArgs e) =>
        await RunCloudActionAsync("立即重试失败上传", sender as Button, (s, ct) => s.RetryFailedAsync(ct));

    /// <summary>【立即对比同步】/【立即重试失败上传】共用的那一圈。</summary>
    /// <param name="trigger">点下去的那颗按钮 —— 跑的时候它**原地换成一截进度条**。</param>
    /// <remarks>
    /// ⚠️ <b>跑的时候那两个按钮要禁用</b>：一次同步可能要传好久，而重复点它
    /// 只会得到「已经有一轮在跑了，这次什么也没做」—— 那看起来像失败。
    /// ⚠️ 光禁用是不够的（C14）：一颗灰掉的按钮没有任何「还在动」的迹象，
    /// 而这一轮可能跑好几分钟。所以点下去那颗换成进度条，进度本身由这一页的
    /// 两秒节拍从 <see cref="CloudUploadService.SyncProgress"/> 读（见
    /// <see cref="UpdateCloudActionProgress"/>）。
    /// </remarks>
    private async Task RunCloudActionAsync(
        string title, Button? trigger, Func<CloudUploadService, CancellationToken, Task<int>> action)
    {
        var service = _host.Services.CloudUploads;

        if (service is null)
        {
            return;
        }

        _cloudBusy = true;
        _cloudActionTitle = title;
        _cloudNote = $"{title}：正在跑。";
        ShowCloudActionProgress(trigger);

        await RefreshCloudPageAsync();

        try
        {
            var count = await action(service, CancellationToken.None);

            _cloudNote = count > 0
                ? $"{title}：这次传上去 {count} 条。"
                : $"{title}：这次没有新传上去的 —— 要么网盘上都已经有了，要么都失败了。"
                  + "失败原因写在下面队列的每一行上（鼠标停在那一行上看）。";
        }
        catch (Exception ex)
        {
            _cloudNote = $"{title}出错：{ex.Message}";
        }
        finally
        {
            _cloudBusy = false;
            HideCloudActionProgress();
            await RefreshCloudPageAsync();
        }
    }

    /// <summary>把那一截进度条摆出来，顺手把点下去那颗按钮收掉。</summary>
    private void ShowCloudActionProgress(Button? trigger)
    {
        CloudActionProgressPanel.Visibility = Visibility.Visible;

        if (trigger is not null)
        {
            trigger.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>收起进度条、把两颗按钮都摆回来（跑完、以及整页不工作时都走它）。</summary>
    private void HideCloudActionProgress()
    {
        CloudActionProgressPanel.Visibility = Visibility.Collapsed;
        CloudSyncButton.Visibility = Visibility.Visible;
        CloudRetryButton.Visibility = Visibility.Visible;
        _cloudActionTitle = string.Empty;
    }

    /// <summary>
    /// 刷新那一截进度条（这一页每两秒喊一次）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>没在跑的时候什么也不做。</b>这一页的节拍也兼着别的事，
    /// 不判 <see cref="_cloudBusy"/> 的话，跑完之后那一截会停在上一次的百分比上
    /// —— 一条永远走不到头的进度条比没有进度条更糟。
    /// </remarks>
    private void UpdateCloudActionProgress(CloudUploadService service)
    {
        if (!_cloudBusy)
        {
            return;
        }

        var progress = service.SyncProgress;

        // ⚠️ `Total == 0` 是「还没数出总数」而不是「完成了 100%」——
        // `CloudSyncProgress.Fraction` 在这种情况下返回 1（空集就是全做完），
        // 拿它去填条会画出一根满格的、其实什么都没干的条。
        if (progress is null || progress.Total <= 0)
        {
            CloudActionProgressText.Text = $"{_cloudActionTitle}：正在数这一轮要处理几条…";
            return;
        }

        CloudActionProgressBar.Value = progress.Fraction;
        CloudActionProgressText.Text =
            $"{_cloudActionTitle}：已处理 {progress.Done}/{progress.Total}（{progress.Fraction:P0}）";
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
