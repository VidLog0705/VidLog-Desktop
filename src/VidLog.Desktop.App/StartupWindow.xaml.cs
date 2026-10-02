using System.Windows;

namespace VidLog.Desktop.App;

/// <summary>
/// 装配期间那句「还在启动」的交代。
/// </summary>
/// <remarks>
/// ⚠️ <b>它不承载任何逻辑</b>，只有一句话和一根会动的进度条。存在的理由见
/// <c>StartupWindow.xaml</c> 顶部：装配期实测 4–6 秒，而此前那几秒里
/// <b>屏幕上什么都没有</b>。
/// <para>
/// ⚠️ 不能改成「把主窗先显示出来再填内容」：主窗要拿 <see cref="AppHost"/>
/// 才能构造（装配活得比窗口长），而那正是还没做完的事。
/// </para>
/// </remarks>
public partial class StartupWindow : Window
{
    public StartupWindow()
    {
        InitializeComponent();
    }
}
