using System.Text.Json;

namespace TourkitAiProxy.Services.Speech;

/// <summary>
/// Speech-to-Text qua Vbee (cùng nền tảng với Vbee TTS — 1 AppId/Token).
///
/// RÀNG BUỘC GÓI (đã test): chỉ có <b>STT batch (mode=async)</b> — sync (realtime) trả
/// "Missing feature: stt-sync". Input BẮT BUỘC là <b>WAV</b> (mimetype audio/wav) với sample rate
/// ∈ {8000,16000,22050,32000,44100,48000}. Trình duyệt ghi webm → client PHẢI ghi WAV 16k (xem
/// lib/util.js recordWavPCM). File ≠ WAV → ném InvalidOperationException để SpeechToTextService fallback Whisper/Gemini.
///
/// Luồng: POST /v1/stt (multipart: audioContent + mode=async + webhookUrl) → transcriptId
///        → poll GET /v1/stt/transcripts/{id} tới status=COMPLETED → transcript.
/// LƯU Ý: chậm (async, ~chục giây) + chất lượng thấp hơn Whisper → chỉ primary khi Speech:Provider=vbee,
/// luôn có fallback ở SpeechToTextService.
///
/// Config: "Speech": { "Vbee": { "SttEnabled": true, ... } }  (AppId/Token dùng chung khối Speech:Vbee).
/// </summary>
public class VbeeSttService
{
    private const string BaseUrl = "https://api.vbee.vn/v1";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _cfg;
    private readonly ILogger<VbeeSttService> _log;

    public VbeeSttService(IHttpClientFactory httpFactory, IConfiguration cfg, ILogger<VbeeSttService> log)
    { _httpFactory = httpFactory; _cfg = cfg; _log = log; }

    private string? AppId => _cfg["Speech:Vbee:AppId"] ?? Environment.GetEnvironmentVariable("VBEE_APP_ID");
    private string? Token => _cfg["Speech:Vbee:Token"] ?? Environment.GetEnvironmentVariable("VBEE_TOKEN");

    /// <summary>Có creds (AppId+Token) — đủ để GỌI được (dùng cho engine override/test).</summary>
    public bool HasCreds => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(Token);

    /// <summary>Đã BẬT làm STT primary (SttEnabled + creds). SpeechToTextService dùng cờ này để chọn primary.</summary>
    public bool Configured => _cfg.GetValue("Speech:Vbee:SttEnabled", false) && HasCreds;

    /// <summary>Transcribe WAV bytes. Ném InvalidOperationException nếu không phải WAV hoặc Vbee lỗi (để fallback).</summary>
    public async Task<string> TranscribeAsync(byte[] audio, string fileName, string contentType, CancellationToken ct)
    {
        if (!HasCreds) throw new InvalidOperationException("Vbee STT thiếu creds (Speech:Vbee:AppId/Token).");
        if (!IsWav(audio, fileName, contentType))
            throw new InvalidOperationException("Vbee STT chỉ nhận WAV — audio hiện không phải WAV (client cần ghi WAV 16k).");

        var http = _httpFactory.CreateClient("vbee");
        http.Timeout = TimeSpan.FromSeconds(_cfg.GetValue("Speech:Vbee:SttPollTimeoutSeconds", 60) + 10);

        // ── Submit batch ─────────────────────────────────────────────────────────
        var transcriptId = await SubmitAsync(http, audio, ct);

        // ── Poll get-transcript ──────────────────────────────────────────────────
        var deadline = DateTime.UtcNow.AddSeconds(_cfg.GetValue("Speech:Vbee:SttPollTimeoutSeconds", 60));
        var delayMs = 800;
        while (DateTime.UtcNow < deadline)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/stt/transcripts/{transcriptId}");
            AddAuth(req);
            using var res = await http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Vbee STT poll lỗi {(int)res.StatusCode}: {ExtractError(raw)}");

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status == "COMPLETED")
            {
                var text = root.TryGetProperty("transcript", out var t) ? (t.GetString() ?? "") : "";
                text = text.Trim();
                if (string.IsNullOrEmpty(text))
                    throw new InvalidOperationException("Vbee STT COMPLETED nhưng transcript rỗng.");
                _log.LogInformation("Vbee STT OK: {Chars}ch", text.Length);
                return text;
            }
            if (status == "FAILED")
                throw new InvalidOperationException($"Vbee STT thất bại: {raw}");

            await Task.Delay(delayMs, ct);
            delayMs = Math.Min(delayMs + 300, 2000);
        }
        throw new InvalidOperationException($"Vbee STT poll quá hạn ({transcriptId}) — chưa COMPLETED.");
    }

    private async Task<string> SubmitAsync(HttpClient http, byte[] audio, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(file, "audioContent", "audio.wav");
        form.Add(new StringContent("async"), "mode");
        form.Add(new StringContent(_cfg["Speech:Vbee:WebhookUrl"] ?? "https://tourkit.vn/vbee-callback"), "webhookUrl");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/stt") { Content = form };
        AddAuth(req);
        using var res = await http.SendAsync(req, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vbee STT submit lỗi {(int)res.StatusCode}: {ExtractError(raw)}");

        using var doc = JsonDocument.Parse(raw);
        var tid = doc.RootElement.TryGetProperty("transcriptId", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(tid))
            throw new InvalidOperationException($"Vbee STT không trả transcriptId: {raw}");
        return tid;
    }

    private void AddAuth(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        req.Headers.TryAddWithoutValidation("App-Id", AppId);
    }

    // WAV = RIFF....WAVE header, hoặc đuôi .wav / content-type audio/wav|x-wav|vnd.wave.
    private static bool IsWav(byte[] audio, string? fileName, string? contentType)
    {
        if (audio.Length >= 12 &&
            audio[0] == 'R' && audio[1] == 'I' && audio[2] == 'F' && audio[3] == 'F' &&
            audio[8] == 'W' && audio[9] == 'A' && audio[10] == 'V' && audio[11] == 'E')
            return true;
        if (!string.IsNullOrEmpty(fileName) && fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(contentType) && contentType.Contains("wav", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string ExtractError(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                return m.GetString() ?? raw;
        }
        catch { }
        return raw.Length > 200 ? raw[..200] : raw;
    }
}
