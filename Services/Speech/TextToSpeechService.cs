using System.ClientModel;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using OpenAI;
using OpenAI.Audio;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Speech;

/// <summary>
/// Text-to-Speech qua OpenAI (official SDK 2.x). Giọng neural đọc tiếng Việt tự nhiên hơn hẳn
/// speechSynthesis trình duyệt (khi máy không có giọng vi). Dùng chung key Providers:OpenAI:ApiKey
/// (như Whisper STT). Trả mp3 bytes.
///
/// CHỐNG ĐỐT TIỀN:
///   • Cache theo hash(text|voice|model) — câu đọc LẶP LẠI (vd "Xin chào! Tôi có thể hỗ trợ gì...")
///     → free, không gọi lại API. Cache in-mem cap 200 mục.
///   • Cap MAX_CHARS = 600 (tts-1 giá $15/1M ký tự → 1 câu ~400 ký tự ≈ 150đ).
///   • Chỉ frontend gọi khi user BẬT loa (opt-in, mặc định tắt) + máy không có giọng vi miễn phí.
///
/// Config (appsettings.json, optional):
///   "Speech": { "Tts": { "Model": "tts-1", "Voice": "nova" } }
///   Model: tts-1 (rẻ, ổn) | tts-1-hd (gấp đôi giá) | gpt-4o-mini-tts.
///   Voice: alloy|echo|fable|onyx|nova|shimmer (mặc định nova).
/// </summary>
public class TextToSpeechService
{
    private readonly ProviderKeyStore _keys;
    private readonly IConfiguration _cfg;
    private readonly ILogger<TextToSpeechService> _log;

    private const int MAX_CHARS = 600;
    private const int CACHE_CAP = 200;
    private static readonly ConcurrentDictionary<string, byte[]> _cache = new();

    public TextToSpeechService(ProviderKeyStore keys, IConfiguration cfg, ILogger<TextToSpeechService> log)
    { _keys = keys; _cfg = cfg; _log = log; }

    public async Task<(byte[] Mp3, bool Cached)> SynthesizeAsync(string text, string? voice, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Thiếu text để đọc.");
        text = text.Trim();
        if (text.Length > MAX_CHARS) text = text.Substring(0, MAX_CHARS);

        var modelId = _cfg["Speech:Tts:Model"] ?? "tts-1";
        var voiceName = (voice ?? _cfg["Speech:Tts:Voice"] ?? "nova").ToLowerInvariant();

        var cacheKey = Hash($"{modelId}|{voiceName}|{text}");
        if (_cache.TryGetValue(cacheKey, out var hit)) return (hit, true);   // câu lặp → free

        var apiKey = _keys.Get("openai");
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Thiếu OpenAI API key (Providers:OpenAI:ApiKey hoặc env OPENAI_API_KEY).");

        try
        {
            var client = new OpenAIClient(new ApiKeyCredential(apiKey));
            var audioClient = client.GetAudioClient(modelId);
            var options = new SpeechGenerationOptions { ResponseFormat = GeneratedSpeechFormat.Mp3 };

            var result = await audioClient.GenerateSpeechAsync(text, ResolveVoice(voiceName), options, ct);
            var mp3 = result.Value.ToArray();

            if (_cache.Count < CACHE_CAP) _cache.TryAdd(cacheKey, mp3);   // cap đơn giản: đầy thì thôi cache
            _log.LogInformation("OpenAI TTS OK: {Chars}ch → {Kb}KB, model={Model}, voice={Voice}",
                text.Length, mp3.Length / 1024, modelId, voiceName);
            return (mp3, false);
        }
        catch (ClientResultException ex)
        {
            _log.LogWarning("OpenAI TTS {Code}: {Msg}", ex.Status, ex.Message);
            throw new InvalidOperationException($"OpenAI TTS lỗi HTTP {ex.Status}: {ex.Message}");
        }
    }

    private static GeneratedSpeechVoice ResolveVoice(string name) => name switch
    {
        "alloy"   => GeneratedSpeechVoice.Alloy,
        "echo"    => GeneratedSpeechVoice.Echo,
        "fable"   => GeneratedSpeechVoice.Fable,
        "onyx"    => GeneratedSpeechVoice.Onyx,
        "shimmer" => GeneratedSpeechVoice.Shimmer,
        _         => GeneratedSpeechVoice.Nova,
    };

    private static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 12);
    }
}
