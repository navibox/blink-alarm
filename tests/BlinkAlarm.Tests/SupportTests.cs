using System.Net;
using System.Text.Json;
using BlinkAlarm.Core.Alarm;
using BlinkAlarm.Core.Settings;

namespace BlinkAlarm.Tests;

public class SettingsTests
{
    [Fact]
    public void RoundTripsThroughFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "blinkalarm-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "config.json"));
            Assert.Equal(5, store.Load().TargetBlinks);   // 文件不存在：默认值

            var settings = new AppSettings { TargetBlinks = 3, WebhookUrl = "http://ha.local:8123/api/webhook/x", SoundFile = "C:\\声音\\a.wav" };
            Assert.NotNull(store.Save(settings));
            var loaded = store.Load();
            Assert.Equal(3, loaded.TargetBlinks);
            Assert.Equal(settings.WebhookUrl, loaded.WebhookUrl);
            Assert.Equal(settings.SoundFile, loaded.SoundFile);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReadsLegacyConfigAndClampsValues()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {"camera_index": 1, "mirror": false, "sensitivity": 250, "target_blinks": 1,
                 "blink_window": 10.0, "cooldown": 8.0, "min_closed_frames": 2, "max_blink_frames": 25,
                 "alarm_duration": 6.0, "webhook_method": "get", "webhook_payload": "{\"event\": \"blink_alarm\"}"}
                """);
            var settings = new SettingsStore(path).Load();
            Assert.Equal(1, settings.CameraIndex);
            Assert.False(settings.Mirror);
            Assert.Equal(100, settings.Sensitivity);
            Assert.Equal(2, settings.TargetBlinks);
            Assert.Equal("GET", settings.WebhookMethod);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Equal(60, new SettingsStore(path).Load().Sensitivity);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class WebhookTests
{
    sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? Method;
        public string? Body;
        public HttpStatusCode Status = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status);
        }
    }

    [Fact]
    public void MergesExtraFieldsIntoJsonObject()
    {
        string body = WebhookSender.BuildBody("{\"event\": \"blink_alarm\"}", new Dictionary<string, object> { ["blinks"] = 5, ["test"] = true });
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("blink_alarm", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("blinks").GetInt32());
        Assert.True(doc.RootElement.GetProperty("test").GetBoolean());
    }

    [Fact]
    public void NonJsonPayloadIsSentAsIs()
    {
        Assert.Equal("hello", WebhookSender.BuildBody("hello", new Dictionary<string, object> { ["a"] = 1 }));
        Assert.Equal("{}", WebhookSender.BuildBody("", null));
    }

    [Fact]
    public async Task PostsJsonBody()
    {
        var handler = new RecordingHandler();
        var sender = new WebhookSender(new HttpClient(handler));
        var result = await sender.SendAsync("http://ha.local/api/webhook/x", "POST", "{\"a\":1}");
        Assert.True(result.Success);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("{\"a\":1}", handler.Body);
    }

    [Fact]
    public async Task GetHasNoBodyAndReportsHttpErrors()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.NotFound };
        var sender = new WebhookSender(new HttpClient(handler));
        var result = await sender.SendAsync("http://ha.local/x", "GET", "{}");
        Assert.False(result.Success);
        Assert.Equal(404, result.StatusCode);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Null(handler.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/x")]
    public async Task RejectsInvalidUrl(string url)
    {
        var result = await new WebhookSender(new HttpClient(new RecordingHandler())).SendAsync(url, "POST", "{}");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}

public class SirenWaveTests
{
    [Fact]
    public void ProducesValidWavHeader()
    {
        byte[] wav = SirenWave.Create();
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40));
        Assert.Equal(44100, BitConverter.ToInt32(wav, 24));
    }
}
