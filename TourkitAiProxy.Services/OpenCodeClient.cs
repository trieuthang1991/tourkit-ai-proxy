using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services;

/// Wrapper quanh HttpClient("opencode"): build request body theo fmt, gắn header,
/// thực hiện call (buffered hoặc streaming). Endpoint layer chỉ orchestrate retry/parse.
public class OpenCodeClient
{
    public const string DefaultSystem =
        "Output ONLY the requested format. No thinking, no explanation, no markdown fences. " +
        "Respond directly with the final answer. / Trả lời trực tiếp, không suy luận, không markdown.";

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;

    public OpenCodeClient(IHttpClientFactory http, IConfiguration cfg)
    {
        _http = http;
        _cfg = cfg;
    }

    public string? ApiKey =>
        _cfg["OPENCODE_API_KEY"] ?? Environment.GetEnvironmentVariable("OPENCODE_API_KEY");

    public HttpClient Create() => _http.CreateClient("opencode");

    /// Build body cho non-streaming call.
    public object BuildBody(ModelRegistry.Entry entry, string prompt, int maxTokens, double temperature, string systemMsg)
        => entry.Fmt == "anthropic"
            ? new
              {
                  model = entry.Id, max_tokens = maxTokens, temperature, system = systemMsg,
                  messages = new[] { new { role = "user", content = prompt } }
              }
            : new
              {
                  model = entry.Id, max_tokens = maxTokens, temperature,
                  messages = new[]
                  {
                      new { role = "system", content = systemMsg },
                      new { role = "user",   content = prompt }
                  }
              };

    /// Build body cho streaming call. OpenAI cần stream_options.include_usage để nhận
    /// usage trong chunk cuối; Anthropic stream tự gửi qua message_delta/message_start.
    public object BuildStreamBody(ModelRegistry.Entry entry, string prompt, int maxTokens, double temperature, string systemMsg)
        => entry.Fmt == "anthropic"
            ? new
              {
                  model = entry.Id, max_tokens = maxTokens, temperature, stream = true, system = systemMsg,
                  messages = new[] { new { role = "user", content = prompt } }
              }
            : new
              {
                  model = entry.Id, max_tokens = maxTokens, temperature, stream = true,
                  stream_options = new { include_usage = true },
                  messages = new[]
                  {
                      new { role = "system", content = systemMsg },
                      new { role = "user",   content = prompt }
                  }
              };

    /// Helper: tạo HttpRequestMessage đã gắn auth + content-type + (anthropic header nếu cần).
    public HttpRequestMessage Build(ModelRegistry.Entry entry, object body, bool sse, string apiKey)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, entry.Path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        msg.Headers.Add("Authorization", $"Bearer {apiKey}");
        if (sse) msg.Headers.Add("Accept", "text/event-stream");
        if (entry.Fmt == "anthropic")
        {
            msg.Headers.Add("anthropic-version", "2023-06-01");
            msg.Headers.Add("x-api-key", apiKey);   // OpenCode Go /messages reject bearer-only
        }
        return msg;
    }
}
