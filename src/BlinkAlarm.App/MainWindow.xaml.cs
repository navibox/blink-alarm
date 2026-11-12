using System.ComponentModel;
using System.Windows;
using BlinkAlarm.App.ViewModels;

namespace BlinkAlarm.App;

public partial class MainWindow : Window
{
    readonly MainViewModel viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.AlarmRaised += BringToFront;

        // 小屏幕或高缩放比例下窗口放不下：直接最大化。
        var workArea = SystemParameters.WorkArea;
        if (Width > workArea.Width || Height > workArea.Height) WindowState = WindowState.Maximized;
    }

    /// <summary>报警时把窗口切到最前面，让"停止报警"按钮立即可见。</summary>
    void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Topmost = true;
        Activate();
        Topmost = false;
    }

    /// <summary>"关于"页里的链接：交给系统默认的浏览器或邮件程序打开。</summary>
    void OnLinkClicked(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 没有默认浏览器或邮件程序：忽略，地址本身就显示在界面上。
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (viewModel.IsMonitoring && !App.IsSelfTest)
        {
            var answer = MessageBox.Show(this, "正在监测中。退出后将无法通过眨眼报警，确定要退出吗？",
                "眨眼报警助手", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.Dispose();
        base.OnClosed(e);
    }
}
