using System.Windows;
using System.Windows.Controls;
using VidLog.Desktop.Core.Commands;

// ⚠️ 本工程同时开了 UseWPF 与 UseWindowsForms，后者的 `KeyEventArgs` / `Keys`
// 会被 ImplicitUsings 带进来，跟 WPF 这边撞名（`SearchWindow.xaml.cs` 顶上
// 也是这么钉的）。这里只用 WPF 那套。
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace VidLog.Desktop.App;

/// <summary>
/// 命令面板（T12）—— <c>Ctrl+K</c> 弹出来的那一条。
/// </summary>
/// <remarks>
/// <para>
/// <b>这个窗是哑的</b>：匹配、排序全在
/// <see cref="PaletteMatcher"/>（Core，能测）。这里只做两件事 ——
/// 把用户敲的字<b>递上去</b>、把他选中的那一条<b>还回去</b>。
/// 动作表与「那条对应做什么」也**不在**这里，在 <see cref="MainWindow"/>：
/// 那些动作就是主窗上原来那几颗按钮，重复一份实现必然走样。
/// </para>
/// <para>
/// ⚠️ <b>它不认识「单号」这种业务概念</b>。候选表里有没有「检索单号 X」那一条，
/// 由 <see cref="MainWindow"/> 决定 —— 面板只管把它显示出来。
/// </para>
/// </remarks>
public partial class CommandPaletteWindow : Window
{
    private readonly Func<string, IReadOnlyList<PaletteCandidate>> _match;

    /// <summary>当前这一屏候选。<see cref="PaletteCandidate"/> 是位置 record，取的是同一个对象。</summary>
    private IReadOnlyList<PaletteCandidate> _hits = [];

    public CommandPaletteWindow(Func<string, IReadOnlyList<PaletteCandidate>> match)
    {
        _match = match;
        InitializeComponent();

        QueryBox.TextChanged += (_, _) => Refresh();

        // ⚠️ 挂在**窗**上而不是 `QueryBox` 上：焦点一旦跑到列表里（点过一条之后），
        // 挂在 TextBox 上的方向键与回车就再也收不到了。
        PreviewKeyDown += OnPreviewKeyDown;
        ResultList.PreviewMouseLeftButtonUp += OnResultClick;

        Loaded += (_, _) =>
        {
            Refresh();
            QueryBox.Focus();
        };
    }

    /// <summary>
    /// 用户选中的那一条。<b>按 Esc / 直接关窗 / 一条都没选中 = <see langword="null"/></b>。
    /// </summary>
    /// <remarks>
    /// 调用方看到 <see langword="null"/> 的正确反应是**什么都不做** —— 这就是取消。
    /// </remarks>
    public PaletteCandidate? Chosen { get; private set; }

    // ─────────────────────────────────────────────
    // 交互
    // ─────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Accept();
                break;

            case Key.Down:
                e.Handled = true;
                Move(1);
                break;

            case Key.Up:
                e.Handled = true;
                Move(-1);
                break;
        }

        // Esc 不在这里管：那颗看不见的 `IsCancel` 按钮收得到，而且它走的是
        // 「关窗、Chosen 保持 null」那条路，与这里要做的事一模一样。
    }

    private void Move(int step)
    {
        if (_hits.Count == 0)
        {
            return;
        }

        // 到头就停住，不绕回去 —— 从最后一条按下会跳回第一条，用户会以为列表重画了。
        var next = Math.Clamp(ResultList.SelectedIndex + step, 0, _hits.Count - 1);

        ResultList.SelectedIndex = next;

        // 列表比窗高的时候，翻下去的那一条得跟着滚进来，不然选中的是一条看不见的行。
        ResultList.ScrollIntoView(ResultList.SelectedItem);
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        // ⚠️ 单击就打开（VS Code 那套）：这是个「赶快选一条走人」的窗，要求双击
        // 属于平白多一步。**但不能用 `SelectionChanged` 代替** —— 那个用方向键
        // 上下移也会触发，等于选中即打开，第二条根本翻不到。
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(ResultList, source) is not ListBoxItem item)
        {
            return; // 点的是列表外面或者滚动条
        }

        ResultList.SelectedItem = item.DataContext;
        Accept();
    }

    private void Accept()
    {
        var index = ResultList.SelectedIndex;

        // 一条都没有的时候回车**什么都不做**，而不是关窗：用户多半只是打错了一个字，
        // 关掉等于把他刚敲的东西一起扔掉（这个窗一关，那个字就没了）。
        if (index < 0 || index >= _hits.Count)
        {
            return;
        }

        Chosen = _hits[index];
        Close();
    }

    private void Refresh()
    {
        _hits = _match(QueryBox.Text);

        // 只显示显示名：关键词（「回放」之于「检索录像」）是拿来**命中**的，不是拿来读的，
        // 摆在行里只会让每一行都长得像一串互相无关的词。
        ResultList.ItemsSource = _hits.Select(hit => hit.Label).ToList();

        ResultList.SelectedIndex = _hits.Count > 0 ? 0 : -1;
    }
}
