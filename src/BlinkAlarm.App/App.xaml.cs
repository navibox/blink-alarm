using System.IO;
using System.Windows;
using System.Windows.Threading;
using BlinkAlarm.App.ViewModels;
using BlinkAlarm.Core.Settings;

namespace BlinkAlarm.App;

public partial class App : Application
{
    Mutex? singleInstance;

    /// <summary>以 --self-test 启动：不弹任何对话框，自检后自动退出。</summary>
    public static bool IsSelfTest { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int selfTest = Array.FindIndex(e.Args, a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase));
        if (selfTest >= 0)
        {
            IsSelfTest = true;
            string output = e.Args.Length > selfTest + 1 ? e.Args[selfTest + 1] : Path.Combine(Path.GetTempPath(), "BlinkAlarm-self-test");
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => SelfTest.Run(this, output));
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            // 界面上的意外错误不应让监测中的程序直接消失：提示后继续运行。
            args.Handled = true;
            MessageBox.Show("程序遇到一个错误，但仍在继续运行：\n\n" + args.Exception.Message,
                "眨眼报警助手", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        singleInstance = new Mutex(true, @"Local\BlinkAlarm-SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("眨眼报警助手已经在运行中，请从任务栏切换到现有窗口。",
                "眨眼报警助手", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var viewModel = new MainViewModel(SettingsStore.CreateDefault());
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
        if (viewModel.AutoStart) viewModel.StartMonitoring();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
