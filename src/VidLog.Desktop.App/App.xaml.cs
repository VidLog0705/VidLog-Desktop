using System.Windows;

namespace VidLog.Desktop.App;

/// <summary>
/// 应用引导。
/// </summary>
/// <remarks>
/// 启动顺序刻意是「先装配、再开窗口」：装配失败时还没有窗口可以显示错误，
/// 所以失败要用 MessageBox 说出来，而不是静默退出。
/// </remarks>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var host = await AppHost.StartAsync();

            // 装配里攒下的问题（读设置失败、没摄像头、没编码器）会显示在窗口的
            // 「需要注意」区里，不在这里拦。
            MainWindow = new MainWindow(host);
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            // 启动失败必须让用户看见，不能静默退出（I3 的同一条精神）。
            MessageBox.Show(
                $"启动失败：{ex.Message}",
                "VidLog", MessageBoxButton.OK, MessageBoxImage.Error);

            Shutdown(1);
        }
    }
}
