using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlinkAlarm.App.Services;
using BlinkAlarm.App.ViewModels;
using BlinkAlarm.Core.Settings;
using BlinkAlarm.Core.Vision;

namespace BlinkAlarm.App;

/// <summary>
/// 发布后的自检（BlinkAlarm.exe --self-test 输出文件夹）：
/// 确认打包进 exe 的模型和原生库都能加载、界面每一页都能正常渲染，并保存界面截图。
/// 结果写入 result.txt；成功时退出码为 0。
/// </summary>
static class SelfTest
{
    public static void Run(Application app, string outputDirectory)
    {
        int exitCode = 0;
        Directory.CreateDirectory(outputDirectory);
        string resultFile = Path.Combine(outputDirectory, "result.txt");
        try
        {
            // 1. ONNX Runtime + 两个内置模型。
            const int width = 640, height = 480;
            var pixels = new byte[width * height * 3];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(40 + i / 3 % width * 60 / width);
            using (var landmarker = new FaceLandmarker())
                landmarker.Process(pixels, width, height, width * 3);

            // 2. OpenCV 原生库（摄像头采集用）。
            using (var mat = new OpenCvSharp.Mat(4, 4, OpenCvSharp.MatType.CV_8UC3))
                if (mat.Empty()) throw new InvalidOperationException("OpenCV 原生库不可用。");

            // 3. 串口库（USB 继电器用）。
            System.IO.Ports.SerialPort.GetPortNames();

            // 4. 界面：使用临时设置文件，不影响用户自己的 config.json。
            var store = new SettingsStore(Path.Combine(outputDirectory, "self-test-config.json"));
            var viewModel = new MainViewModel(store);
            var window = new MainWindow(viewModel) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            app.MainWindow = window;
            window.Show();
            Settle();
            Capture(window, Path.Combine(outputDirectory, "1-idle.png"));

            viewModel.ShowSelfTestFrame(new FrameInfo
            {
                Pixels = pixels, Width = width, Height = height, Stride = width * 3,
                Phase = EnginePhase.Watching,
                LeftOpenness = 0.96, RightOpenness = 0.41, RightClosed = true,
                LeftContour = Oval(400, 220, 44, 15), RightContour = Oval(240, 220, 44, 6),
                Count = 3, Target = 5, WindowRemaining = 0.6,
            });
            var tabs = (TabControl)window.FindName("SettingsTabs");
            for (int i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                Settle();
                Capture(window, Path.Combine(outputDirectory, $"{i + 2}-tab{i + 1}.png"));
            }

            viewModel.ShowAlarmPreview();
            Settle();
            Capture(window, Path.Combine(outputDirectory, "8-alarm.png"));

            window.Close();
            File.WriteAllText(resultFile, "OK");
        }
        catch (Exception ex)
        {
            exitCode = 1;
            File.WriteAllText(resultFile, "FAILED\n" + ex);
        }
        app.Shutdown(exitCode);
    }

    /// <summary>等界面把排队中的布局、绑定和渲染全部做完。</summary>
    static void Settle() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    static void Capture(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
        if (width < 100 || height < 100) throw new InvalidOperationException("窗口内容没有完成布局。");

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, width, height);
            context.DrawRectangle(window.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None }, null, bounds);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    static Vector2[] Oval(float cx, float cy, float rx, float ry)
    {
        var points = new Vector2[16];
        for (int i = 0; i < points.Length; i++)
        {
            double angle = i * 2 * Math.PI / points.Length;
            points[i] = new Vector2(cx + rx * (float)Math.Cos(angle), cy + ry * (float)Math.Sin(angle));
        }
        return points;
    }
}
