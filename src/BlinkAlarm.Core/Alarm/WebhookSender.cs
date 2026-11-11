using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlinkAlarm.Core.Alarm;

public readonly record struct WebhookResult(int? StatusCode, string? Error)
{
    public bool Success => StatusCode is >= 200 and < 300;
}

/// <summary>向 Home Assistant 等外部系统发送 Webhook。</summary>
public sealed class WebhookSender
{
    readonly HttpClient client;

    public WebhookSender(HttpClient? client = null) =>
        this.client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    /// <param name="payload">用户填写的 JSON 载荷；是 JSON 对象时会并入 <paramref name="extra"/> 中的字段。</param>
    public async Task<WebhookResult> SendAsync(string url, string method, string payload,
        IReadOnlyDictionary<string, object>? extra = null, CancellationToken cancellation = default)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new WebhookResult(null, "地址无效，应以 http:// 或 https:// 开头");

        try
        {
            bool isGet = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
            using var request = new HttpRequestMessage(isGet ? HttpMethod.Get : HttpMethod.Post, uri);
            if (!isGet)
                request.Content = new StringContent(BuildBody(payload, extra), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
            return new WebhookResult((int)response.StatusCode, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new WebhookResult(null, ex is TaskCanceledException ? "连接超时" : ex.Message);
        }
    }

    internal static string BuildBody(string payload, IReadOnlyDictionary<string, object>? extra)
    {
        if (string.IsNullOrWhiteSpace(payload)) payload = "{}";
        try
        {
            if (JsonNode.Parse(payload) is JsonObject obj)
            {
                if (extra is not null)
                    foreach (var (key, value) in extra)
                        obj[key] = JsonSerializer.SerializeToNode(value);
                return obj.ToJsonString(new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            }
        }
        catch (JsonException)
        {
        }
        return payload; // 不是 JSON 对象：原样发送。
    }
}
