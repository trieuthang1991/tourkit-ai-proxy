using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TourkitAiProxy.Services.Speech;

/// <summary>
/// Text-to-Speech qua Google Cloud Text-to-Speech (giọng Việt neural: Wavenet / Neural2 / Chirp3-HD).
///
/// KHÁC Vbee: chỉ 1 call REST ĐỒNG BỘ (không submit+poll batch):
///   POST https://texttospeech.googleapis.com/v1/text:synthesize
///     { input:{text}, voice:{languageCode, name}, audioConfig:{audioEncoding:"MP3", speakingRate} }
///   → { audioContent: "&lt;base64 mp3&gt;" } → giải base64 ra mp3 bytes.
///
/// Auth = API KEY (không cần service-account JSON / OAuth / gRPC): header X-Goog-Api-Key: {key}.
/// Tạo key: Google Cloud Console → APIs &amp; Services → Credentials → Create API key, rồi
/// Enable "Cloud Text-to-Speech API" cho project + (khuyến nghị) hạn chế key chỉ API này.
///
/// WINDOWS SERVER 2012 / SCHANNEL CŨ: khác Vbee, endpoint Google (GFE) chào cả ECDHE_RSA + P-256 +
/// AES-GCM → 2012 R2 (đã bật TLS 1.2 + cài KB GCM) thường bắt tay THẲNG được, KHÔNG cần gateway.
/// Nếu vẫn fail TLS (vd 2012 non-R2 chưa bật TLS 1.2) → set Speech:Google:GatewayUrl để relay giống Vbee
/// (gateway cần thêm route POST /google/tts — xem gateway/).
///
/// Chống đốt tiền: cache in-mem theo hash(text|voice|rate) — câu lặp = free. Cap 200 mục.
///
/// Config (appsettings.json — ApiKey là secret, file gitignored):
///   "Speech": { "Google": {
///       "Enabled": true,
///       "ApiKey": "AIza...",                 // hoặc env GOOGLE_TTS_API_KEY
///       "LanguageCode": "vi-VN",
///       "Voice": "vi-VN-Wavenet-A",          // Wavenet-A/B/C/D | Neural2-A/D | Chirp3-HD-* | Standard-*
///       "SpeakingRate": 1.0,                 // 0.25..4.0
///       "Pitch": 0.0,                        // -20..20
///       "GatewayUrl": "", "GatewayKey": ""   // chỉ đặt khi server chính không gọi được googleapis.com
///   } }
/// </summary>
public class GoogleTtsService
{
    private const string SynthUrl = "https://texttospeech.googleapis.com/v1/text:synthesize";
    private const string DefaultLang = "vi-VN";
    private const string DefaultVoice = "vi-VN-Wavenet-A";
    private const int MAX_CHARS = 2000;        // đủ reply dài; Google cho ≤5000 byte input nhưng cap cho an toàn
    private const int CACHE_CAP = 200;
    private static readonly ConcurrentDictionary<string, byte[]> _cache = new();

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _cfg;
    private readonly ILogger<GoogleTtsService> _log;

    public GoogleTtsService(IHttpClientFactory httpFactory, IConfiguration cfg, ILogger<GoogleTtsService> log)
    { _httpFactory = httpFactory; _cfg = cfg; _log = log; }

    private string? ApiKey => _cfg["Speech:Google:ApiKey"] ?? Environment.GetEnvironmentVariable("GOOGLE_TTS_API_KEY");

    // GATEWAY MODE (tùy chọn): khi server chính KHÔNG gọi được texttospeech.googleapis.com (Schannel quá cũ)
    // → relay qua gateway giống Vbee. Có GatewayUrl thì chỉ cần gateway giữ key.
    private string? GatewayUrl => _cfg["Speech:Google:GatewayUrl"] ?? Environment.GetEnvironmentVariable("GOOGLE_TTS_GATEWAY_URL");
    private string? GatewayKey => _cfg["Speech:Google:GatewayKey"] ?? Environment.GetEnvironmentVariable("GOOGLE_TTS_GATEWAY_KEY");

    /// <summary>Có đủ cấu hình để dùng: bật + (có gateway HOẶC có ApiKey).</summary>
    public bool Configured =>
        _cfg.GetValue("Speech:Google:Enabled", true) &&
        (!string.IsNullOrWhiteSpace(GatewayUrl) || !string.IsNullOrWhiteSpace(ApiKey));

    public async Task<(byte[] Mp3, bool Cached)> SynthesizeAsync(string text, string? voice, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Thiếu text để đọc.");
        if (!Configured) throw new InvalidOperationException("Google TTS chưa cấu hình (Speech:Google:ApiKey HOẶC GatewayUrl).");

        text = text.Trim();
        if (text.Length > MAX_CHARS) text = text.Substring(0, MAX_CHARS);

        var lang = _cfg["Speech:Google:LanguageCode"] ?? DefaultLang;
        var voiceName = string.IsNullOrWhiteSpace(voice) ? (_cfg["Speech:Google:Voice"] ?? DefaultVoice) : voice;
        var rate = _cfg.GetValue("Speech:Google:SpeakingRate", 1.0);
        var pitch = _cfg.GetValue("Speech:Google:Pitch", 0.0);

        var cacheKey = Hash($"google|{voiceName}|{rate}|{pitch}|{text}");
        if (_cache.TryGetValue(cacheKey, out var hit)) return (hit, true);   // câu lặp → free

        // ── GATEWAY MODE: relay khi server chính không gọi được googleapis.com ──
        if (!string.IsNullOrWhiteSpace(GatewayUrl))
        {
            var mp3g = await SynthesizeViaGatewayAsync(text, voiceName, ct);
            if (_cache.Count < CACHE_CAP) _cache.TryAdd(cacheKey, mp3g);
            _log.LogInformation("Google TTS OK (gateway): {Chars}ch → {Kb}KB, voice={Voice}", text.Length, mp3g.Length / 1024, voiceName);
            return (mp3g, false);
        }

        var http = _httpFactory.CreateClient("google-tts");
        http.Timeout = TimeSpan.FromSeconds(_cfg.GetValue("Speech:Google:TimeoutSeconds", 25));

        var body = new
        {
            input = new { text },
            voice = new { languageCode = lang, name = voiceName },
            audioConfig = new { audioEncoding = "MP3", speakingRate = rate, pitch },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, SynthUrl);
        req.Headers.TryAddWithoutValidation("X-Goog-Api-Key", ApiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var res = await http.SendAsync(req, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google TTS lỗi {(int)res.StatusCode}: {ExtractError(raw)}");

        var mp3 = ParseAudioContent(raw);
        if (mp3.Length < 200) throw new InvalidOperationException("Google TTS trả audio rỗng.");

        if (_cache.Count < CACHE_CAP) _cache.TryAdd(cacheKey, mp3);
        _log.LogInformation("Google TTS OK: {Chars}ch → {Kb}KB, voice={Voice}", text.Length, mp3.Length / 1024, voiceName);
        return (mp3, false);
    }

    // Relay: POST {text, voice} + header X-Api-Key → gateway trả thẳng mp3 (gateway giữ key + lo TLS "khó").
    private async Task<byte[]> SynthesizeViaGatewayAsync(string text, string voiceName, CancellationToken ct)
    {
        var url = GatewayUrl!.TrimEnd('/') + "/google/tts";
        var http = _httpFactory.CreateClient("google-tts-gateway");
        http.Timeout = TimeSpan.FromSeconds(_cfg.GetValue("Speech:Google:TimeoutSeconds", 30));

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(GatewayKey)) req.Headers.TryAddWithoutValidation("X-Api-Key", GatewayKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { text, voice = voiceName }), Encoding.UTF8, "application/json");

        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Google TTS gateway lỗi {(int)res.StatusCode}: {ExtractError(err)}");
        }
        var mp3 = await res.Content.ReadAsByteArrayAsync(ct);
        if (mp3.Length < 200) throw new InvalidOperationException("Google TTS gateway trả audio rỗng.");
        return mp3;
    }

    private static byte[] ParseAudioContent(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var b64 = doc.RootElement.TryGetProperty("audioContent", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(b64))
            throw new InvalidOperationException($"Google TTS không trả audioContent: {(raw.Length > 200 ? raw[..200] : raw)}");
        return Convert.FromBase64String(b64);
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
