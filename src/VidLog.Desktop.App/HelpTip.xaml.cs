using System.Windows;
using System.Windows.Controls;

namespace VidLog.Desktop.App;

/// <summary>
/// 一个「?」圆圈，点开显示一段解释文字（需求方 2026-09-28 裁决）。
/// </summary>
/// <remarks>
/// <para>
/// 设置页原来把十几段「为什么 / 怎么办 / ⚠️」的说明**全铺在页面上**，
/// 视觉重量比控件本身还大。裁决是把它们收进「?」。
/// </para>
/// <para>
/// ⚠️ <b>但有几条规格明令「必须让用户看见」，不能折进来</b> ——
/// 「回落必须可见、不得静默回落」（§3.1.7）、「禁止静默清理」（§3.5）、
/// 「未激活/未校准不锁已有录像」（L8）。那几条在设置页里**保持直接可见**。
/// </para>
/// </remarks>
public partial class HelpTip : System.Windows.Controls.UserControl
{
    /// <summary>点开之后显示的那段文字。</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(HelpTip),
            new PropertyMetadata(string.Empty));

    public HelpTip()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        Tip.IsOpen = Toggle.IsChecked == true;
    }

    /// <summary>气泡被点别处收起时，把圆圈的状态跟着复位。</summary>
    /// <remarks>
    /// ⚠️ 少了这一句：用户点别处把气泡关掉之后，那个「?」**还亮着实心蓝**
    /// —— 看着像还开着，再点一下却要按两次才出得来。
    /// </remarks>
    private void OnTipClosed(object? sender, EventArgs e)
    {
        Toggle.IsChecked = false;
    }
}
