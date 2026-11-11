using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using BlinkAlarm.Core.Blink;
using BlinkAlarm.Core.Vision;
using OpenCvSharp;

namespace BlinkAlarm.App.Services;

/// <summary>检测线程使用的实时参数。界面改动设置时整体替换。</summary>
public sealed record EngineOptions(BlinkOptions Blink, int Target, double WindowSeconds, double CooldownSeconds);

public enum EnginePhase
{
    Opening,
    Reconnecting,
    Calibrating,
    Watching,
    NoFace,
    Cooldown,
}

/// <summary>一帧画面及其分析结果，交给界面显示。</summary>
public sealed class FrameInfo
{
    public byte[] Pixels = Array.Empty<byte>();
    public int Width, Height, Stride;
    public EnginePhase Phase;
    public double CalibrationProgress;
    public double LeftOpenness, RightOpenness;
    public bool LeftClosed, RightClosed;
    public Vector2[]? LeftContour, RightContour;
    public int Count, Target;
    public double WindowRemaining;
}

/// <summary>
/// 后台检测线程：读摄像头 → 面部关键点 → 眨眼判定 → 计数。
/// 报警判定完全在本线程完成，不依赖界面线程是否繁忙。
/// </summary>
public sealed class MonitorEngine
{
    static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);

    readonly object gate = new();
    Thread? thread;
    CancellationTokenSource? cancellation;
    volatile EngineOptions options;
    volatile bool recalibrate, resetCounter;
    int uiBusy;

    public MonitorEngine(EngineOptions options) => this.options = options;

    public EngineOptions Options { get => options; set => options = value; }

    /// <summary>新的一帧（检测线程上触发）。界面处理完后必须调用 <see cref="FrameDone"/>。</summary>
    public event Action<FrameInfo>? Frame;
    /// <summary>阶段变化但没有画面时（打开摄像头、重连）触发。</summary>
    public event Action<EnginePhase>? PhaseChanged;
    /// <summary>达到目标眨眼次数（检测线程上触发）。</summary>
    public event Action<int>? Triggered;
    /// <summary>检测线程结束。参数为错误信息，正常停止时为 null。</summary>
    public event Action<string?>? Stopped;

    public bool IsRunning { get { lock (gate) return thread is not null; } }

    public void Start(int cameraIndex)
    {
        lock (gate)
        {
            if (thread is not null) return;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            thread = new Thread(() => Run(cameraIndex, token)) { IsBackground = true, Name = "BlinkAlarm.Monitor" };
            thread.Start();
        }
    }

    /// <summary>请求停止；线程真正结束时会触发 <see cref="Stopped"/>。</summary>
    public void Stop()
    {
        lock (gate) cancellation?.Cancel();
    }

    public void Recalibrate() => recalibrate = true;
    public void ResetCounter() => resetCounter = true;
    public void FrameDone() => Interlocked.Exchange(ref uiBusy, 0);

    void Run(int cameraIndex, CancellationToken token)
    {
        string? error = null;
        try
        {
            using var landmarker = new FaceLandmarker();
            var detector = new BlinkDetector();
            var counter = new BlinkCounter();
            var clock = Stopwatch.StartNew();
            using var mat = new Mat();
            byte[] work = Array.Empty<byte>();
            var display = new FrameInfo();
            bool everOpened = false;

            while (!token.IsCancellationRequested)
            {
                PhaseChanged?.Invoke(everOpened ? EnginePhase.Reconnecting : EnginePhase.Opening);
                using var camera = OpenCamera(cameraIndex);
                if (camera is null)
                {
                    if (!everOpened)
                    {
                        error = $"无法打开摄像头 {cameraIndex}。请检查摄像头是否已连接、是否被其它软件占用，或更换摄像头编号。";
                        return;
                    }
                    token.WaitHandle.WaitOne(ReconnectDelay);
                    continue;
                }
                everOpened = true;
                landmarker.Reset();
                detector.FaceLost();
                var lastFrame = clock.Elapsed;

                while (!token.IsCancellationRequested)
                {
                    if (!camera.Read(mat) || !ToBgr24(mat))
                    {
                        // 偶尔丢帧属正常；持续读不到画面则认为摄像头断开，释放后重连。
                        if (clock.Elapsed - lastFrame > ReadTimeout) break;
                        Thread.Sleep(30);
                        continue;
                    }
                    var now = lastFrame = clock.Elapsed;

                    int width = mat.Width, height = mat.Height, stride = (int)mat.Step();
                    int length = stride * height;
                    if (work.Length != length) work = new byte[length];
                    Marshal.Copy(mat.Data, work, 0, length);

                    var current = options;
                    counter.Target = current.Target;
                    counter.Window = TimeSpan.FromSeconds(current.WindowSeconds);
                    counter.Cooldown = TimeSpan.FromSeconds(current.CooldownSeconds);
                    if (recalibrate) { recalibrate = false; detector.Recalibrate(); }
                    if (resetCounter) { resetCounter = false; counter.Reset(); }

                    var face = landmarker.Process(work, width, height, stride);
                    BlinkSample sample = default;
                    if (face is null)
                    {
                        detector.FaceLost();
                    }
                    else
                    {
                        sample = detector.Update(face.LeftEar, face.RightEar, now, current.Blink);
                        if (sample.Blink && counter.Register(now))
                            Triggered?.Invoke(current.Target);
                    }
                    counter.Expire(now);

                    // 界面还没画完上一帧时，跳过这一帧的显示（检测不受影响）。
                    if (Frame is null || Interlocked.CompareExchange(ref uiBusy, 1, 0) != 0) continue;

                    if (display.Pixels.Length != length) display.Pixels = new byte[length];
                    Buffer.BlockCopy(work, 0, display.Pixels, 0, length);
                    display.Width = width;
                    display.Height = height;
                    display.Stride = stride;
                    display.Phase = face is null ? EnginePhase.NoFace
                        : sample.Calibrating ? EnginePhase.Calibrating
                        : counter.InCooldown(now) ? EnginePhase.Cooldown
                        : EnginePhase.Watching;
                    display.CalibrationProgress = sample.CalibrationProgress;
                    display.LeftOpenness = face is null ? 0 : sample.LeftOpenness;
                    display.RightOpenness = face is null ? 0 : sample.RightOpenness;
                    display.LeftClosed = sample.LeftState != EyeState.Open;
                    display.RightClosed = sample.RightState != EyeState.Open;
                    display.LeftContour = face?.LeftEyeContour;
                    display.RightContour = face?.RightEyeContour;
                    display.Count = counter.Count;
                    display.Target = current.Target;
                    display.WindowRemaining = counter.WindowRemaining(now);
                    Frame.Invoke(display);
                }
            }
        }
        catch (Exception ex)
        {
            error = "监测出错：" + ex.Message;
        }
        finally
        {
            lock (gate)
            {
                cancellation?.Dispose();
                cancellation = null;
                thread = null;
            }
            Interlocked.Exchange(ref uiBusy, 0);
            Stopped?.Invoke(error);
        }
    }

    /// <summary>把画面统一成 8 位 BGR（红外/灰度摄像头输出单通道，个别驱动输出 BGRA）。</summary>
    static bool ToBgr24(Mat mat)
    {
        if (mat.Empty() || mat.Depth() != MatType.CV_8U) return false;
        switch (mat.Channels())
        {
            case 3: return true;
            case 1: Cv2.CvtColor(mat, mat, ColorConversionCodes.GRAY2BGR); return true;
            case 4: Cv2.CvtColor(mat, mat, ColorConversionCodes.BGRA2BGR); return true;
            default: return false;
        }
    }

    static VideoCapture? OpenCamera(int index)
    {
        foreach (var api in new[] { VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.MSMF, VideoCaptureAPIs.ANY })
        {
            VideoCapture? camera = null;
            try
            {
                camera = new VideoCapture(index, api);
                if (camera.IsOpened())
                {
                    camera.Set(VideoCaptureProperties.FrameWidth, 640);
                    camera.Set(VideoCaptureProperties.FrameHeight, 480);
                    return camera;
                }
            }
            catch (OpenCVException)
            {
            }
            camera?.Dispose();
        }
        return null;
    }
}
