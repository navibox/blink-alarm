using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlinkAlarm.Core.Settings;

/// <summary>全部用户设置。字段名沿用旧版 config.json，升级后原有设置继续有效。</summary>
public sealed class AppSettings
{
    [JsonPropertyName("camera_index")] public int CameraIndex { get; set; }
    [JsonPropertyName("mirror")] public bool Mirror { get; set; } = true;
    [JsonPropertyName("show_landmarks")] public bool ShowLandmarks { get; set; } = true;
    [JsonPropertyName("auto_start")] public bool AutoStart { get; set; }

    [JsonPropertyName("sensitivity")] public double Sensitivity { get; set; } = 60;
    [JsonPropertyName("target_blinks")] public int TargetBlinks { get; set; } = 5;
    [JsonPropertyName("blink_window")] public double BlinkWindowSeconds { get; set; } = 10;
    [JsonPropertyName("cooldown")] public double CooldownSeconds { get; set; } = 8;
    [JsonPropertyName("min_closed_ms")] public int MinClosedMs { get; set; } = 60;
    [JsonPropertyName("max_closed_ms")] public int MaxClosedMs { get; set; } = 900;

    [JsonPropertyName("alarm_sound_enabled")] public bool SoundEnabled { get; set; } = true;
    [JsonPropertyName("alarm_duration")] public double AlarmDurationSeconds { get; set; } = 6;
    [JsonPropertyName("alarm_until_stopped")] public bool AlarmUntilStopped { get; set; }
    [JsonPropertyName("alarm_sound_file")] public string SoundFile { get; set; } = "";

    [JsonPropertyName("webhook_enabled")] public bool WebhookEnabled { get; set; }
    [JsonPropertyName("webhook_url")] public string WebhookUrl { get; set; } = "";
    [JsonPropertyName("webhook_method")] public string WebhookMethod { get; set; } = "POST";
    [JsonPropertyName("webhook_payload")] public string WebhookPayload { get; set; } = "{\"event\": \"blink_alarm\"}";

    [JsonPropertyName("relay_enabled")] public bool RelayEnabled { get; set; }
    [JsonPropertyName("relay_port")] public string RelayPort { get; set; } = "";

    /// <summary>把所有数值收拢到界面允许的范围内（配置文件可能被手工改坏）。</summary>
    public AppSettings Normalize()
    {
        CameraIndex = Math.Clamp(CameraIndex, 0, 9);
        Sensitivity = Clamp(Sensitivity, 0, 100, 60);
        TargetBlinks = Math.Clamp(TargetBlinks, 2, 10);
        BlinkWindowSeconds = Clamp(BlinkWindowSeconds, 3, 30, 10);
        CooldownSeconds = Clamp(CooldownSeconds, 1, 60, 8);
        MinClosedMs = Math.Clamp(MinClosedMs, 30, 300);
        MaxClosedMs = Math.Clamp(MaxClosedMs, 400, 3000);
        AlarmDurationSeconds = Clamp(AlarmDurationSeconds, 1, 120, 6);
        SoundFile ??= "";
        WebhookUrl ??= "";
        RelayPort ??= "";
        WebhookPayload ??= "";
        WebhookMethod = string.Equals(WebhookMethod, "GET", StringComparison.OrdinalIgnoreCase) ? "GET" : "POST";
        return this;
    }

    static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>读写 config.json。</summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    readonly string[] candidates;

    /// <param name="candidates">按优先级排列的配置文件路径；读取第一个存在的，写入第一个可写的。</param>
    public SettingsStore(params string[] candidates) => this.candidates = candidates;

    /// <summary>默认位置：程序所在文件夹（便携），不可写时退回到当前用户的 AppData。</summary>
    public static SettingsStore CreateDefault() => new(
        Path.Combine(AppContext.BaseDirectory, "config.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlinkAlarm", "config.json"));

    public AppSettings Load()
    {
        foreach (string path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (settings is not null) return settings.Normalize();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 文件损坏或无法读取：继续尝试下一个位置，最后退回默认设置。
            }
        }
        return new AppSettings();
    }

    /// <returns>实际写入的路径；全部位置都写不了时返回 null。</returns>
    public string? Save(AppSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        foreach (string path in candidates)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }
}
