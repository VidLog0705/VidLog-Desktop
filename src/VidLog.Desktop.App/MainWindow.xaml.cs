using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using VidLog.Desktop.Core.Configuration;

namespace VidLog.Desktop.App;

/// <summary>
/// 主窗口。
/// </summary>
/// <remarks>
/// **刻意保持很薄** —— 只做三件事：装配（<see cref="DesktopServices.Create"/>）、
/// 呈现启动报告、把用户操作转成系统动作。
/// 「重启后收尾孤儿」「起回放服务」这些行为都在 Core 里，这样它们才测得到
/// （见 <see cref="DesktopServices.StartAsync"/>）。
/// </remarks>
public partial class MainWindow : Window
{
    private DesktopServices? _services;
    private string? _playbackUrl;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _services = DesktopServices.Create(DataLayout.Default());

            var report = await _services.StartAsync();

            ShowReport(report);
        }
        catch (Exception ex)
        {
            // 启动失败必须让用户看见，不能静默退出（I3 的同一条精神）。
            StatusText.Text = $"启动失败：{ex.Message}";
        }
    }

    private void ShowReport(StartupReport report)
    {
        _playbackUrl = report.PlaybackUrl;

        var recovered = report.RecoveredCount > 0
            ? $"，收尾了 {report.RecoveredCount} 段上次没走完的录像"
            : string.Empty;

        StatusText.Text = _playbackUrl is null
            ? $"服务已就绪{recovered}。回放服务未启动。"
            : $"服务已就绪{recovered}。\n回放地址：{_playbackUrl}";

        OpenPlaybackButton.IsEnabled = _playbackUrl is not null;
        OpenDataFolderButton.IsEnabled = true;

        if (report.Warnings.Count > 0)
        {
            WarningsHeader.Visibility = Visibility.Visible;
            WarningsList.Visibility = Visibility.Visible;

            foreach (var warning in report.Warnings)
            {
                WarningsList.Items.Add(warning);
            }
        }
    }

    private void OnOpenPlayback(object sender, RoutedEventArgs e)
    {
        OpenInShell(_playbackUrl ?? string.Empty);
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        if (_services is not null)
        {
            OpenInShell(_services.Layout.RootDirectory);
        }
    }

    private static void OpenInShell(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打不开：{ex.Message}", "VidLog", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var services = _services;
        _services = null;

        if (services is null)
        {
            return;
        }

        // 刻意**不**等它完成：关窗口不该卡住。监听套接字随进程退出一起收掉，
        // 主动停一下只是为了让端口尽快释放、方便马上重启。
        _ = services.DisposeAsync();
    }
}
