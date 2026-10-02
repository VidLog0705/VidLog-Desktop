using System.Windows;
using System.Windows.Media;

using VidLog.Desktop.Core.Configuration;

// 本工程同时开了 UseWPF 与 UseWindowsForms（后者只为托盘图标），
// ImplicitUsings 会把两边的命名空间都带进来，于是同名类型变成「不明确」。
// 这里用**别名钉死成 WPF 的那套**（与拆窗那几个文件同一条口径）。
using Geometry = System.Windows.Media.Geometry;

namespace VidLog.Desktop.App;

/// <summary>
/// 「选择这台电脑的用途」（照需求方设计图 `_11`–`_15`）。
/// </summary>
/// <remarks>
/// <para>
/// 它只做一件事：把两个是非题收成 <see cref="StationRole"/>。
/// 四档用途的**文案**在 Core 的 <see cref="StationRoles.Describe"/> 里
/// （放那边才测得到 —— App 层没有测试工程），这里只管画。
/// </para>
/// <para>
/// ⚠️ 两张卡是 <c>RadioButton</c>（见 XAML 里 <c>RoleCard</c> 那段说明），
/// 所以「互斥」是白拿的：<c>CheckBox</c> 式的两个独立开关需要自己保证
/// 「选了这个就把那个取消」，而漏一次就会出现「两个都选中」。
/// </para>
/// </remarks>
public partial class RoleWindow : Window
{
    /// <summary>
    /// 结果卡左边那个图标，四档各一个。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与卡里的 ✓ / ✕ 一样，**全是内联矢量，不引任何图片或图标库**
    /// （设计图上的图标只给了形状，路径是本仓按那个形状画的）。
    /// 坐标系一律 22×22 —— <c>Path.Stretch</c> 是 <c>None</c>，
    /// 所以这里的数字就是屏幕上的像素。
    /// </remarks>
    private static readonly Geometry VideoGlyph = Geometry.Parse(
        "M 3,5 L 13,5 A 2,2 0 0 1 15,7 L 15,15 A 2,2 0 0 1 13,17 L 3,17 "
        + "A 2,2 0 0 1 1,15 L 1,7 A 2,2 0 0 1 3,5 Z "
        + "M 15,10.5 L 21,6 L 21,16 L 15,11.5 Z");

    private static readonly Geometry WifiGlyph = Geometry.Parse(
        "M 1.5,9 A 13,13 0 0 1 20.5,9 "
        + "M 5.5,13 A 8,8 0 0 1 16.5,13 "
        + "M 9.5,17 A 3.5,3.5 0 0 1 12.5,17 "
        + "M 9.8,20 A 1.2,1.2 0 1 0 12.2,20 A 1.2,1.2 0 1 0 9.8,20 Z");

    private static readonly Geometry DatabaseGlyph = Geometry.Parse(
        "M 3,5.5 A 8,3 0 1 1 19,5.5 A 8,3 0 1 1 3,5.5 "
        + "M 3,5.5 L 3,16.5 A 8,3 0 0 0 19,16.5 L 19,5.5 "
        + "M 3,11 A 8,3 0 0 0 19,11");

    private static readonly Geometry PlayGlyph = Geometry.Parse(
        "M 6.5,3.5 L 19,11 L 6.5,18.5 Z");

    /// <summary>还没答完时那个 ✓。</summary>
    private static readonly Geometry PendingGlyph = Geometry.Parse(
        "M 16.5,2 L 6,13 L 1.5,8");

    /// <param name="current">
    /// 现在的用途。<see langword="null"/> = **一张卡都不预选**
    /// （图上 `_11` 那张「请完成上面两个选择」的状态）—— 首次运行时就是这样。
    /// </param>
    public RoleWindow(StationRole? current = null)
    {
        InitializeComponent();

        _current = current;

        if (current is { } role)
        {
            var choice = StationRoleChoice.Of(role);
            RecordsYes.IsChecked = choice.Records;
            RecordsNo.IsChecked = !choice.Records;
            KeepsYes.IsChecked = choice.Keeps;
            KeepsNo.IsChecked = !choice.Keeps;
        }

        Refresh();
    }

    /// <summary>用户选的用途。取消退出时它没意义（调用方要看 <c>DialogResult</c>）。</summary>
    public StationRole SelectedRole { get; private set; } = StationRole.RecordAndKeep;

    /// <summary>打开时本机已有的用途；首次运行时是 <see langword="null"/>。</summary>
    /// <remarks>
    /// ⚠️ 只为结果卡上那六个字：选中的**就是当前用途**时写「当前用途」，
    /// 与当前不同才写「将切换到」。这一屏现在已经**每次打开都弹**，
    /// 而绝大多数时候用户什么都没改 —— 那时写「将切换到」是一句**假话**
    /// （I3 同一条精神：界面上说出口的话要成立）。
    /// </remarks>
    private readonly StationRole? _current;

    private void OnAnswerChanged(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>把当前的选择画到结果卡上；没答完就回到图上 `_11` 那个样子。</summary>
    private void Refresh()
    {
        // ⚠️ 用 `RecordsYes.IsChecked is not { } records` 而不是 `== true`：
        // 三态（true / false / null）里只有 null 是「还没答」，
        // 而 `== true` 会把「答了『不要』」也当成没答，于是选完「不要」按钮还是灰的。
        if (RecordsYes.IsChecked is not { } records || KeepsYes.IsChecked is not { } keeps)
        {
            ResultLabel.Text = "选择结果";
            ResultTitle.Text = "请完成上面两个选择";
            ResultSummary.Text = "完成后会在这里显示最终用途和支持的能力";
            ResultAbilities.ItemsSource = null;
            ResultGlyph.Data = PendingGlyph;
            ConfirmButton.IsEnabled = false;
            return;
        }

        var role = new StationRoleChoice(records, keeps).Role;
        var described = StationRoles.Describe(role);

        SelectedRole = role;

        ResultLabel.Text = _current == role ? "当前用途" : "将切换到";
        ResultTitle.Text = described.Title;
        ResultSummary.Text = described.Summary;
        ResultAbilities.ItemsSource = described.Abilities;
        ResultGlyph.Data = role switch
        {
            StationRole.RecordAndUpload => WifiGlyph,
            StationRole.BackupHost => DatabaseGlyph,
            StationRole.ViewerOnly => PlayGlyph,
            _ => VideoGlyph,
        };

        ConfirmButton.IsEnabled = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
