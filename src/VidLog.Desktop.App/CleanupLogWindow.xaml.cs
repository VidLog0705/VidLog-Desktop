using System.Windows;

using VidLog.Desktop.Core.Cleanup;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.App;

/// <summary>
/// 清理流水（T24）—— 规格 §6.2「保留可查的清理记录」的那个「可查」。
/// </summary>
/// <remarks>
/// <para>
/// 在这之前审计已经写了两轮（T19 起），入口只有一个：自己去
/// <c>%LOCALAPPDATA%\VidLog\cleanup-audit.jsonl</c> 里翻。于是设置页上
/// <see cref="CleanupAsk"/> 那句「明细见清理流水」指的是一份**用户打不开的文件**。
/// </para>
/// <para>
/// ⚠️ <b>这里是 App 层，没有测试工程</b>。所以这个文件里只剩三件事：
/// 读、绑、显示空/坏行的提示 —— 一个判断都没有。翻账（时间格式、动作码翻人话、
/// 单号查不到时写什么）全在 <see cref="CleanupLogView"/> 里，那边有测试。
/// 「装配少跳一步」由 <c>CleanupLogViewTests.清理流水在设置页上真的有出路</c>
/// 这条文本绊线守着。
/// </para>
/// </remarks>
public partial class CleanupLogWindow : Window
{
    private readonly AppHost _host;

    public CleanupLogWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>
    /// 读审计、翻成行、绑上去。
    /// </summary>
    /// <remarks>
    /// ⚠️ 读的是 <see cref="CleanupAuditLog.LoadPageAsync"/> 而不是
    /// <c>LoadAllAsync</c>：后者把坏行**静默跳过** —— 一个「不许静默」的窗口
    /// 悄悄藏掉几条，比打不开更糟。
    /// <para>
    /// ⚠️ 读不出来（文件正被别的进程占着、权限不对）时**不吞异常**：
    /// 弹一句为什么。审计是这个窗口存在的全部理由，读不到却看起来「没有记录」，
    /// 正是最容易被当真的那一种谎。
    /// </para>
    /// </remarks>
    private async Task LoadAsync()
    {
        IReadOnlyList<CleanupLogRow> rows;
        int unreadable;

        try
        {
            var page = await _host.Services.CleanupAudit.LoadPageAsync();

            // 单号要拿索引换（审计里只有证据 id）。索引本来就常驻内存，
            // 这里只是读一遍，不改任何东西。
            // ⚠️ 它与上面那次读**共用同一个 try**：这一句失败照样是「窗口打不开」，
            // 而 `Loaded` 那个 lambda 是 async void 的口子 —— 漏出去的异常
            // 会直接掀掉整个进程。
            rows = CleanupLogView.Build(page.Records, await _host.Services.Index.LoadAllAsync());
            unreadable = page.UnreadableLines;
        }
        catch (Exception ex)
        {
            // ⚠️ 界面上那句话是**瞬时**的（窗口一关就没了），而「审计读不出来」
            // 是**事后必须查得出来**的一件事 —— 它意味着这本「不许静默清理」的账
            // 本身出问题了。`AGENTS.md` §6.1：catch 不许静默吞掉，
            // 而这里光靠用户可见通道不够，所以两样都留。
            _host.Log(LogLevel.Warn, "清理", $"打开清理流水时读不出来：{ex.Message}");

            EmptyNote.Text = $"清理流水读不出来：{ex.Message}";
            EmptyNote.Visibility = Visibility.Visible;
            return;
        }

        LogList.ItemsSource = rows;

        // 一条都没有 —— 摆那句话，而不是一个空白的列表（空白看着像坏了）。
        if (rows.Count == 0)
        {
            EmptyNote.Text = CleanupLogView.EmptyText;
            EmptyNote.Visibility = Visibility.Visible;
        }

        // ⚠️ 有坏行就**必须说出来**：不说的话，用户看到的流水比真实发生的少，
        // 而少掉的那几条恰好是出事（写盘失败）时留下的。
        if (unreadable > 0)
        {
            UnreadableNote.Text = CleanupLogView.DescribeUnreadable(unreadable);
            UnreadableNote.Visibility = Visibility.Visible;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
