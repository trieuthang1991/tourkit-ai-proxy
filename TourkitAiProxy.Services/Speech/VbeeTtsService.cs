using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TourkitAiProxy.Services.Speech;

/// <summary>
/// Text-to-Speech qua Vbee AIVoice (giọng Việt neural chất lượng cao, bản địa).
///
/// LƯU Ý GÓI DỊCH VỤ: gói hiện tại chỉ hỗ trợ <b>Batch API (mode=async)</b> — Realtime (sync)
/// trả lỗi "This feature is not supported in user package". Nên luồng ở đây là:
///   1) POST https://api.vbee.vn/v1/tts  {mode:"async", text, voiceCode, ...} → requestId
///   2) Poll GET /v1/tts/requests/{requestId} tới khi status=COMPLETED → audioLink
///   3) Tải audioLink (theo redirect 302) → mp3 bytes.
/// Batch cho tối đa 100.000 ký tự nên KHÔNG cần cắt chunk (JARVIS đọc ≤2000). Audio link
/// chỉ sống 3 phút nên tải về NGAY, không lưu link.
///
/// Auth: header Authorization: Bearer {token} + App-Id: {appId} (tạo tại studio.vbee.vn/apps).
/// Chống đốt tiền: cache in-mem theo hash(text|voice) — câu lặp = free.
///
/// Config (appsettings.json — appId/token là secret, để trong appsettings.json gitignored):
///   "Speech": { "Vbee": {
///       "Enabled": true,
///       "AppId": "...", "Token": "...",
///       "Voice": "hn_female_ngochuyen_full_48k-fhg",
///       "Bitrate": 128, "OutputFormat": "mp3",
///       "WebhookUrl": "https://tourkit.vn/vbee-callback",   // không dùng (ta poll) nhưng API yêu cầu có
///       "PollTimeoutSeconds": 25
///   } }
/// </summary>
public class VbeeTtsService
{
    private const string BaseUrl = "https://api.vbee.vn/v1";
    private const string DefaultVoice = "hn_female_ngochuyen_full_48k-fhg";
    private const int MAX_CHARS = 2000;        // đủ reply dài; batch API cho tới 100k nhưng cap cho an toàn
    private const int CACHE_CAP = 200;
    private static readonly ConcurrentDictionary<string, byte[]> _cache = new();

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _cfg;
    private readonly ILogger<VbeeTtsService> _log;
    // Giới hạn số synth Vbee ĐỒNG THỜI. Bắn quá nhiều call song song (frontend cắt ~10 đoạn) → hàng đợi
    // async Vbee nghẽn → poll quá hạn 25s → endpoint forward sang edge (trộn giọng + trễ ~33s). Semaphore
    // giữ ≤N slot → call dư xếp hàng ngắn thay vì đập Vbee cùng lúc. Singleton → 1 gate = trần toàn cục.
    private readonly SemaphoreSlim _gate;

    public VbeeTtsService(IHttpClientFactory httpFactory, IConfiguration cfg, ILogger<VbeeTtsService> log)
    {
        _httpFactory = httpFactory; _cfg = cfg; _log = log;
        _gate = new SemaphoreSlim(Math.Max(1, cfg.GetValue("Speech:Vbee:MaxConcurrency", 3)));
    }

    private string? AppId => _cfg["Speech:Vbee:AppId"] ?? Environment.GetEnvironmentVariable("VBEE_APP_ID");
    private string? Token => _cfg["Speech:Vbee:Token"] ?? Environment.GetEnvironmentVariable("VBEE_TOKEN");

    // GATEWAY MODE: khi server chính KHÔNG gọi được api.vbee.vn (vd Windows Server 2012 R2, Schannel
    // cũ, thiếu TLS 1.3/x25519) → trỏ sang relay Ubuntu+nginx (gateway/) làm trung gian. Khi có GatewayUrl,
    // service chỉ POST {text} tới gateway và nhận mp3 (KHÔNG cần AppId/Token ở đây — gateway giữ).
    private string? GatewayUrl => _cfg["Speech:Vbee:GatewayUrl"] ?? Environment.GetEnvironmentVariable("VBEE_GATEWAY_URL");
    private string? GatewayKey => _cfg["Speech:Vbee:GatewayKey"] ?? Environment.GetEnvironmentVariable("VBEE_GATEWAY_KEY");

    /// <summary>Có đủ cấu hình để dùng: bật + (có gateway HOẶC có appId+token).</summary>
    public bool Configured =>
        _cfg.GetValue("Speech:Vbee:Enabled", true) &&
        (!string.IsNullOrWhiteSpace(GatewayUrl) ||
         (!string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(Token)));

    public async Task<(byte[] Mp3, bool Cached)> SynthesizeAsync(string text, string? voice, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Thiếu text để đọc.");
        if (!Configured) throw new InvalidOperationException("Vbee chưa cấu hình (Speech:Vbee:GatewayUrl HOẶC AppId/Token).");

        text = text.Trim();
        if (text.Length > MAX_CHARS) text = text.Substring(0, MAX_CHARS);

        var voiceCode = string.IsNullOrWhiteSpace(voice) ? (_cfg["Speech:Vbee:Voice"] ?? DefaultVoice) : voice;
        var bitrate = _cfg.GetValue("Speech:Vbee:Bitrate", 128);
        var format = _cfg["Speech:Vbee:OutputFormat"] ?? "mp3";

        var cacheKey = Hash($"vbee|{voiceCode}|{bitrate}|{text}");
        if (_cache.TryGetValue(cacheKey, out var hit)) return (hit, true);   // câu lặp → free

        // Giữ SLOT trước khi gọi Vbee — chống bắn ồ ạt gây poll timeout → forward edge (trộn giọng).
        await _gate.WaitAsync(ct);
        try
        {
        // Double-check cache: 1 request khác có thể đã synth xong CÙNG text trong lúc mình chờ slot → free.
        if (_cache.TryGetValue(cacheKey, out var hit2)) return (hit2, true);

        // ── GATEWAY MODE: relay qua Ubuntu (khi server chính không gọi được api.vbee.vn) ──
        if (!string.IsNullOrWhiteSpace(GatewayUrl))
        {
            var mp3g = await SynthesizeViaGatewayAsync(text, voiceCode, ct);
            if (_cache.Count < CACHE_CAP) _cache.TryAdd(cacheKey, mp3g);
            _log.LogInformation("Vbee TTS OK (gateway): {Chars}ch → {Kb}KB, voice={Voice}", text.Length, mp3g.Length / 1024, voiceCode);
            return (mp3g, false);
        }

        var http = _httpFactory.CreateClient("vbee");
        http.Timeout = TimeSpan.FromSeconds(_cfg.GetValue("Speech:Vbee:PollTimeoutSeconds", 25) + 10);

        // ── 1) Submit batch (async) ──────────────────────────────────────────────
        var body = new
        {
            text,
            voiceCode,
            mode = "async",
            outputFormat = format,
            bitrate,
            // sampleRate BẮT BUỘC set: bỏ trống → Vbee dùng 48kHz cho giọng nhưng mp3 lệch tần số
            // → phát chậm/méo NGHE NHƯ ĐÁNH VẦN. 24000 Hz cho giọng đọc tự nhiên, chuẩn.
            sampleRate = _cfg.GetValue("Speech:Vbee:SampleRate", 24000),
            speed = _cfg.GetValue("Speech:Vbee:Speed", 1.0),
            webhookUrl = _cfg["Speech:Vbee:WebhookUrl"] ?? "https://tourkit.vn/vbee-callback",
        };
        var requestId = await SubmitAsync(http, body, ct);

        // ── 2) Poll tới khi COMPLETED ────────────────────────────────────────────
        var audioLink = await PollAsync(http, requestId, ct);

        // ── 3) Tải audio (theo redirect) ─────────────────────────────────────────
        var mp3 = await http.GetByteArrayAsync(audioLink, ct);
        if (mp3.Length < 200) throw new InvalidOperationException("Vbee trả audio rỗng.");

        if (_cache.Count < CACHE_CAP) _cache.TryAdd(cacheKey, mp3);
        _log.LogInformation("Vbee TTS OK: {Chars}ch → {Kb}KB, voice={Voice}", text.Length, mp3.Length / 1024, voiceCode);
        return (mp3, false);
        }
        finally { _gate.Release(); }
    }

    // Gọi relay Ubuntu: POST {text, voice} + header X-Api-Key → nhận thẳng mp3. Toàn bộ TLS "khó"
    // với api.vbee.vn do gateway lo → server chính (Schannel cũ) chỉ cần TLS 1.2 tới gateway.
    private async Task<byte[]> SynthesizeViaGatewayAsync(string text, string voiceCode, CancellationToken ct)
    {
        var url = GatewayUrl!.TrimEnd('/') + "/vbee/tts";
        var http = _httpFactory.CreateClient("vbee-gateway");
        http.Timeout = TimeSpan.FromSeconds(_cfg.GetValue("Speech:Vbee:PollTimeoutSeconds", 45) + 15);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(GatewayKey)) req.Headers.TryAddWithoutValidation("X-Api-Key", GatewayKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { text, voice = voiceCode }), Encoding.UTF8, "application/json");

        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Vbee gateway lỗi {(int)res.StatusCode}: {ExtractError(err)}");
        }
        var mp3 = await res.Content.ReadAsByteArrayAsync(ct);
        if (mp3.Length < 200) throw new InvalidOperationException("Vbee gateway trả audio rỗng.");
        return mp3;
    }

    private async Task<string> SubmitAsync(HttpClient http, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/tts");
        AddAuth(req);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vbee submit lỗi {(int)res.StatusCode}: {ExtractError(raw)}");

        using var doc = JsonDocument.Parse(raw);
        var rid = doc.RootElement.TryGetProperty("requestId", out var r) ? r.GetString() : null;
        if (string.IsNullOrEmpty(rid))
            throw new InvalidOperationException($"Vbee không trả requestId: {raw}");
        return rid;
    }

    private async Task<string> PollAsync(HttpClient http, string requestId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_cfg.GetValue("Speech:Vbee:PollTimeoutSeconds", 25));
        var delayMs = 500;
        while (DateTime.UtcNow < deadline)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/tts/requests/{requestId}");
            AddAuth(req);
            using var res = await http.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Vbee poll lỗi {(int)res.StatusCode}: {ExtractError(raw)}");

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status == "COMPLETED")
            {
                var link = root.TryGetProperty("audioLink", out var a) ? a.GetString() : null;
                if (string.IsNullOrEmpty(link)) throw new InvalidOperationException("Vbee COMPLETED nhưng thiếu audioLink.");
                return link;
            }
            if (status == "FAILED")
                throw new InvalidOperationException($"Vbee xử lý thất bại: {raw}");

            await Task.Delay(delayMs, ct);
            delayMs = Math.Min(delayMs + 250, 1500);   // backoff nhẹ
        }
        throw new InvalidOperationException($"Vbee poll quá hạn ({requestId}) — chưa COMPLETED.");
    }

    private void AddAuth(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        req.Headers.TryAddWithoutValidation("App-Id", AppId);
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

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)), 0, 12);
}
