using System.Collections.ObjectModel;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlinkAlarm.App.Services;
using BlinkAlarm.Core.Alarm;
using BlinkAlarm.Core.Blink;
using BlinkAlarm.Core.Settings;

namespace BlinkAlarm.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    static readonly PointCollection NoPoints = CreateEmptyPoints();

    readonly SettingsStore store;
    readonly AppSettings settings;
    readonly MonitorEngine engine;
    readonly SoundAlarm sound = new();
    readonly WebhookSender webhook = new();
    readonly Dispatcher dispatcher;
    readonly DispatcherTimer saveTimer, alarmTimer, testTimer;
    bool restartPending, disposed;

    public MainViewModel(SettingsStore store)
    {
        this.store = store;
        settings = store.Load();
        dispatcher = Dispatcher.CurrentDispatcher;
        engine = new MonitorEngine(BuildOptions());
        engine.Frame += frame => Post(DispatcherPriority.Render, () =>
        {
            try { ShowFrame(frame); }
            finally { engine.FrameDone(); }
        });
        engine.PhaseChanged += phase => Post(DispatcherPriority.Normal, () => ShowPhase(phase, 0));
        engine.Triggered += blinks => Post(DispatcherPriority.Send, () => RaiseAlarm(blinks));
        engine.Stopped += error => Post(DispatcherPriority.Normal, () => OnEngineStopped(error));

        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        saveTimer.Tick += (_, _) => SaveNow();
        alarmTimer = new DispatcherTimer();
        alarmTimer.Tick += (_, _) => StopAlarm();
        testTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        testTimer.Tick += (_, _) => StopSoundTest();

        ToggleMonitorCommand = new RelayCommand(ToggleMonitor, () => !IsStopping);
        StopAlarmCommand = new RelayCommand(StopAlarm);
        RecalibrateCommand = new RelayCommand(engine.Recalibrate, () => IsMonitoring);
        TestSoundCommand = new RelayCommand(ToggleSoundTest, () => !AlarmActive);
        BrowseSoundCommand = new RelayCommand(BrowseSound);
        ClearSoundCommand = new RelayCommand(() => SoundFile = "", () => SoundFile.Length > 0);
        TestWebhookCommand = new RelayCommand(() => _ = SendWebhookAsync(true), () => !IsSendingWebhook);

        RebuildDots();
    }

    // ───────────────────────── 运行状态 ─────────────────────────

    bool isMonitoring, isStopping, alarmActive, isTestingSound, isSendingWebhook, hasPreview, isCalibrating;
    bool leftClosed, rightClosed;
    string statusText = "未开始监测", statusTone = "idle", errorText = "", webhookStatus = "", footerText = "设置会自动保存";
    string lastAlarmText = "尚未触发报警";
    int count, triggerCount, frameWidth = 640, frameHeight = 480;
    double leftOpenness, rightOpenness, windowRemaining, calibrationProgress;
    WriteableBitmap? preview;
    PointCollection leftEyePoints = NoPoints, rightEyePoints = NoPoints;

    public ICommand ToggleMonitorCommand { get; }
    public ICommand StopAlarmCommand { get; }
    public ICommand RecalibrateCommand { get; }
    public ICommand TestSoundCommand { get; }
    public ICommand BrowseSoundCommand { get; }
    public ICommand ClearSoundCommand { get; }
    public ICommand TestWebhookCommand { get; }

    /// <summary>报警触发时通知窗口把自己切到最前面。</summary>
    public event Action? AlarmRaised;

    public ObservableCollection<BlinkDot> Dots { get; } = new();

    public bool IsMonitoring { get => isMonitoring; private set { if (Set(ref isMonitoring, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool IsStopping { get => isStopping; private set { if (Set(ref isStopping, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool AlarmActive { get => alarmActive; private set { if (Set(ref alarmActive, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool IsTestingSound { get => isTestingSound; private set => Set(ref isTestingSound, value); }
    public bool IsSendingWebhook { get => isSendingWebhook; private set { if (Set(ref isSendingWebhook, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool HasPreview { get => hasPreview; private set => Set(ref hasPreview, value); }
    public bool IsCalibrating { get => isCalibrating; private set => Set(ref isCalibrating, value); }

    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    /// <summary>状态颜色：idle / ok / warn / danger。</summary>
    public string StatusTone { get => statusTone; private set => Set(ref statusTone, value); }
    public string ErrorText { get => errorText; private set { if (Set(ref errorText, value)) Raise(nameof(HasError)); } }
    public bool HasError => errorText.Length > 0;
    public string WebhookStatus { get => webhookStatus; private set => Set(ref webhookStatus, value); }
    public string FooterText { get => footerText; private set => Set(ref footerText, value); }
    public string LastAlarmText { get => lastAlarmText; private set => Set(ref lastAlarmText, value); }

    public int Count { get => count; private set => Set(ref count, value); }
    public double WindowRemaining { get => windowRemaining; private set => Set(ref windowRemaining, value); }
    public double CalibrationProgress { get => calibrationProgress; private set => Set(ref calibrationProgress, value); }
    public double LeftOpenness { get => leftOpenness; private set => Set(ref leftOpenness, value); }
    public double RightOpenness { get => rightOpenness; private set => Set(ref rightOpenness, value); }
    public bool LeftClosed { get => leftClosed; private set => Set(ref leftClosed, value); }
    public bool RightClosed { get => rightClosed; private set => Set(ref rightClosed, value); }

    public WriteableBitmap? Preview { get => preview; private set => Set(ref preview, value); }
    public int FrameWidth { get => frameWidth; private set => Set(ref frameWidth, value); }
    public int FrameHeight { get => frameHeight; private set => Set(ref frameHeight, value); }
    public PointCollection LeftEyePoints { get => leftEyePoints; private set => Set(ref leftEyePoints, value); }
    public PointCollection RightEyePoints { get => rightEyePoints; private set => Set(ref rightEyePoints, value); }

    // ───────────────────────── 设置（改动即生效并自动保存） ─────────────────────────

    public double CameraIndex
    {
        get => settings.CameraIndex;
        set
        {
            int index = (int)Math.Round(value);
            if (settings.CameraIndex == index) return;
            settings.CameraIndex = index;
            SettingChanged();
            if (IsMonitoring && !IsStopping)
            {
                // 监测中切换摄像头：先停下，线程结束后自动用新编号重新开始。
                restartPending = true;
                StopMonitoring();
            }
        }
    }

    public bool Mirror { get => settings.Mirror; set { if (settings.Mirror != value) { settings.Mirror = value; SettingChanged(); Raise(nameof(MirrorScale)); } } }
    public double MirrorScale => settings.Mirror ? -1 : 1;
    public bool ShowLandmarks { get => settings.ShowLandmarks; set { if (settings.ShowLandmarks != value) { settings.ShowLandmarks = value; SettingChanged(); } } }
    public bool AutoStart { get => settings.AutoStart; set { if (settings.AutoStart != value) { settings.AutoStart = value; SettingChanged(); } } }

    public double Sensitivity
    {
        get => settings.Sensitivity;
        set
        {
            double rounded = Math.Round(value);
            if (settings.Sensitivity == rounded) return;
            settings.Sensitivity = rounded;
            SettingChanged();
            Raise(nameof(ThresholdText));
        }
    }

    public string RuleText => $"{settings.BlinkWindowSeconds:0} 秒内眨眼 {settings.TargetBlinks} 次即报警";

    public string ThresholdText => $"眼睛开合度低于 {BuildOptions().Blink.CloseRatio:P0} 时判定为闭眼。数值越高越容易触发。";

    public double TargetBlinks
    {
        get => settings.TargetBlinks;
        set
        {
            int target = (int)Math.Round(value);
            if (settings.TargetBlinks == target) return;
            settings.TargetBlinks = target;
            SettingChanged();
            engine.ResetCounter();
            Count = 0;
            RebuildDots();
            Raise(nameof(RuleText));
        }
    }

    public double BlinkWindowSeconds { get => settings.BlinkWindowSeconds; set { if (settings.BlinkWindowSeconds != value) { settings.BlinkWindowSeconds = value; SettingChanged(); Raise(nameof(RuleText)); } } }
    public double CooldownSeconds { get => settings.CooldownSeconds; set { if (settings.CooldownSeconds != value) { settings.CooldownSeconds = value; SettingChanged(); } } }
    public double MinClosedMs { get => settings.MinClosedMs; set { if (settings.MinClosedMs != (int)value) { settings.MinClosedMs = (int)value; SettingChanged(); } } }
    public double MaxClosedMs { get => settings.MaxClosedMs; set { if (settings.MaxClosedMs != (int)value) { settings.MaxClosedMs = (int)value; SettingChanged(); } } }

    public bool SoundEnabled { get => settings.SoundEnabled; set { if (settings.SoundEnabled != value) { settings.SoundEnabled = value; SettingChanged(); } } }
    public bool AlarmUntilStopped { get => settings.AlarmUntilStopped; set { if (settings.AlarmUntilStopped != value) { settings.AlarmUntilStopped = value; SettingChanged(); Raise(nameof(UsesDuration)); } } }
    public bool UsesDuration => !settings.AlarmUntilStopped;
    public double AlarmDurationSeconds { get => settings.AlarmDurationSeconds; set { if (settings.AlarmDurationSeconds != value) { settings.AlarmDurationSeconds = value; SettingChanged(); } } }

    public string SoundFile
    {
        get => settings.SoundFile;
        set
        {
            value ??= "";
            if (settings.SoundFile == value) return;
            settings.SoundFile = value;
            SettingChanged();
            Raise(nameof(SoundFileText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string SoundFileText => settings.SoundFile.Length == 0 ? "内置警笛声" : System.IO.Path.GetFileName(settings.SoundFile);

    public bool WebhookEnabled { get => settings.WebhookEnabled; set { if (settings.WebhookEnabled != value) { settings.WebhookEnabled = value; SettingChanged(); } } }
    public string WebhookUrl { get => settings.WebhookUrl; set { if (settings.WebhookUrl != value) { settings.WebhookUrl = value ?? ""; SettingChanged(); } } }
    public string WebhookPayload { get => settings.WebhookPayload; set { if (settings.WebhookPayload != value) { settings.WebhookPayload = value ?? ""; SettingChanged(); } } }
    public bool WebhookIsPost { get => settings.WebhookMethod == "POST"; set { if (value) SetWebhookMethod("POST"); } }
    public bool WebhookIsGet { get => settings.WebhookMethod == "GET"; set { if (value) SetWebhookMethod("GET"); } }

    void SetWebhookMethod(string method)
    {
        if (settings.WebhookMethod == method) return;
        settings.WebhookMethod = method;
        SettingChanged();
        Raise(nameof(WebhookIsPost));
        Raise(nameof(WebhookIsGet));
    }

    EngineOptions BuildOptions() => new(
        new BlinkOptions(settings.Sensitivity, settings.MinClosedMs, settings.MaxClosedMs),
        settings.TargetBlinks, settings.BlinkWindowSeconds, settings.CooldownSeconds);

    void SettingChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        Raise(name);
        engine.Options = BuildOptions();
        saveTimer.Stop();
        saveTimer.Start();
    }

    void SaveNow()
    {
        saveTimer.Stop();
        FooterText = store.Save(settings) is null ? "⚠ 设置无法保存：程序文件夹不可写" : "设置会自动保存";
    }

    void RebuildDots()
    {
        Dots.Clear();
        for (int i = 0; i < settings.TargetBlinks; i++) Dots.Add(new BlinkDot { IsOn = i < Count });
    }

    // ───────────────────────── 监测 ─────────────────────────

    void ToggleMonitor()
    {
        if (IsMonitoring) StopMonitoring();
        else StartMonitoring();
    }

    public void StartMonitoring()
    {
        if (IsMonitoring || engine.IsRunning) return;
        ErrorText = "";
        IsMonitoring = true;
        ShowPhase(EnginePhase.Opening, 0);
        engine.Options = BuildOptions();
        engine.Start(settings.CameraIndex);
    }

    void StopMonitoring()
    {
        if (!IsMonitoring || IsStopping) return;
        IsStopping = true;
        StatusText = "正在停止…";
        StatusTone = "idle";
        engine.Stop();
    }

    void OnEngineStopped(string? error)
    {
        IsMonitoring = false;
        IsStopping = false;
        ClearLiveView();
        StatusText = error is null ? "未开始监测" : "监测已停止";
        StatusTone = error is null ? "idle" : "danger";
        ErrorText = error ?? "";

        if (restartPending && error is null && !disposed)
        {
            restartPending = false;
            StartMonitoring();
        }
        restartPending = false;
    }

    void ClearLiveView()
    {
        HasPreview = false;
        Preview = null;
        LeftEyePoints = RightEyePoints = NoPoints;
        LeftOpenness = RightOpenness = WindowRemaining = CalibrationProgress = 0;
        LeftClosed = RightClosed = IsCalibrating = false;
        SetCount(0);
    }

    void ShowPhase(EnginePhase phase, double progress)
    {
        if (!IsMonitoring || IsStopping) return;
        (StatusText, StatusTone) = phase switch
        {
            EnginePhase.Opening => ("正在打开摄像头…", "warn"),
            EnginePhase.Reconnecting => ("摄像头断开，正在重新连接…", "danger"),
            EnginePhase.Calibrating => ("校准中 · 请睁眼正视摄像头", "warn"),
            EnginePhase.NoFace => ("未检测到人脸 · 请正对摄像头", "warn"),
            EnginePhase.Cooldown => ("报警冷却中", "idle"),
            _ => ("监测中", "ok"),
        };
        IsCalibrating = phase == EnginePhase.Calibrating;
        CalibrationProgress = IsCalibrating ? progress : 0;
        if (phase is EnginePhase.Opening or EnginePhase.Reconnecting) ClearLiveView();
    }

    void ShowFrame(FrameInfo frame)
    {
        if (!IsMonitoring || IsStopping) return;

        if (Preview is null || Preview.PixelWidth != frame.Width || Preview.PixelHeight != frame.Height)
        {
            Preview = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
            FrameWidth = frame.Width;
            FrameHeight = frame.Height;
        }
        Preview.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
        HasPreview = true;

        bool draw = settings.ShowLandmarks;
        LeftEyePoints = draw ? ToPoints(frame.LeftContour) : NoPoints;
        RightEyePoints = draw ? ToPoints(frame.RightContour) : NoPoints;
        LeftOpenness = Math.Clamp(frame.LeftOpenness, 0, 1);
        RightOpenness = Math.Clamp(frame.RightOpenness, 0, 1);
        LeftClosed = frame.LeftClosed;
        RightClosed = frame.RightClosed;
        WindowRemaining = frame.WindowRemaining;
        SetCount(frame.Count);
        ShowPhase(frame.Phase, frame.CalibrationProgress);
    }

    void SetCount(int value)
    {
        if (Count == value && Dots.Count == settings.TargetBlinks) return;
        Count = value;
        if (Dots.Count != settings.TargetBlinks) RebuildDots();
        for (int i = 0; i < Dots.Count; i++) Dots[i].IsOn = i < value;
    }

    static PointCollection ToPoints(Vector2[]? contour)
    {
        if (contour is null) return NoPoints;
        var points = new PointCollection(contour.Length);
        foreach (var p in contour) points.Add(new Point(p.X, p.Y));
        points.Freeze();
        return points;
    }

    static PointCollection CreateEmptyPoints()
    {
        var points = new PointCollection();
        points.Freeze();
        return points;
    }

    // ───────────────────────── 报警 ─────────────────────────

    void RaiseAlarm(int blinks)
    {
        if (disposed || AlarmActive) return;
        StopSoundTest();
        triggerCount++;
        LastAlarmText = $"已触发报警 {triggerCount} 次 · 最近一次 {DateTime.Now:HH:mm:ss}";
        AlarmActive = true;

        if (settings.SoundEnabled) sound.Play(settings.SoundFile);
        alarmTimer.Stop();
        if (!settings.AlarmUntilStopped)
        {
            alarmTimer.Interval = TimeSpan.FromSeconds(settings.AlarmDurationSeconds);
            alarmTimer.Start();
        }
        if (settings.WebhookEnabled && !string.IsNullOrWhiteSpace(settings.WebhookUrl))
            _ = SendWebhookAsync(false, blinks);
        AlarmRaised?.Invoke();
    }

    public void StopAlarm()
    {
        alarmTimer.Stop();
        if (!AlarmActive) return;
        sound.Stop();
        AlarmActive = false;
    }

    void ToggleSoundTest()
    {
        if (IsTestingSound) { StopSoundTest(); return; }
        if (AlarmActive) return;
        sound.Play(settings.SoundFile);
        IsTestingSound = true;
        testTimer.Stop();
        testTimer.Start();
    }

    void StopSoundTest()
    {
        testTimer.Stop();
        if (!IsTestingSound) return;
        IsTestingSound = false;
        if (!AlarmActive) sound.Stop();
    }

    void BrowseSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择报警声音文件",
            Filter = "WAV 音频 (*.wav)|*.wav",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true) SoundFile = dialog.FileName;
    }

    async Task SendWebhookAsync(bool isTest, int blinks = 0)
    {
        if (isTest && string.IsNullOrWhiteSpace(settings.WebhookUrl))
        {
            WebhookStatus = "请先填写地址";
            return;
        }

        if (isTest) IsSendingWebhook = true;
        WebhookStatus = "正在发送…";
        var extra = new Dictionary<string, object> { ["time"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz") };
        if (isTest) extra["test"] = true;
        else extra["blinks"] = blinks;

        try
        {
            var result = await webhook.SendAsync(settings.WebhookUrl, settings.WebhookMethod, settings.WebhookPayload, extra);
            string time = DateTime.Now.ToString("HH:mm:ss");
            WebhookStatus = result.Success ? $"✓ 发送成功（HTTP {result.StatusCode}）· {time}"
                : result.StatusCode is int code ? $"✕ 对方返回 HTTP {code} · {time}"
                : $"✕ 发送失败：{result.Error}";
        }
        finally
        {
            if (isTest) IsSendingWebhook = false;
        }
    }

    // ───────────────────────── 其它 ─────────────────────────

    /// <summary>仅供自检截图使用：不发声、不联动，只显示报警界面。</summary>
    public void ShowAlarmPreview() => AlarmActive = true;

    /// <summary>仅供自检截图使用：不打开摄像头，直接显示一帧给定的画面。</summary>
    public void ShowSelfTestFrame(FrameInfo frame)
    {
        IsMonitoring = true;
        ShowFrame(frame);
    }

    void Post(DispatcherPriority priority, Action action)
    {
        if (disposed) { if (priority == DispatcherPriority.Render) engine.FrameDone(); return; }
        dispatcher.BeginInvoke(priority, action);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (saveTimer.IsEnabled) SaveNow();
        alarmTimer.Stop();
        testTimer.Stop();
        engine.Stop();
        sound.Dispose();
    }
}
