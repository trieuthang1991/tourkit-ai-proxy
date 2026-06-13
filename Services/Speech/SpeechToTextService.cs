using System.ClientModel;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;
using OpenAI;
using OpenAI.Audio;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Services.Speech;

/// <summary>
/// Speech-to-Text orchestrator hybrid: Gemini primary + OpenAI Whisper fallback (configurable).
///
/// DÙNG SDK chính thức/community:
///   • Gemini: Mscc.GenerativeAI (community, .NET-first wrapper cho generativelanguage.googleapis.com)
///   • OpenAI: OpenAI 2.x (official .NET SDK)
///
/// Config (appsettings.json):
///   "Speech": {
///     "Provider": "gemini" | "openai",          // primary engine
///     "Model": "gemini-2.0-flash",              // model name của primary
///     "Fallback": true,                         // primary fail → thử secondary
///     "Gemini": { "ApiKey": "", "Model": "gemini-2.0-flash" },
///     "OpenAI": { "Model": "whisper-1" }        // ApiKey lấy chung Providers:OpenAI:ApiKey
///   }
///
/// Catalog model hỗ trợ:
///   Gemini:  gemini-2.0-flash | gemini-2.0-flash-lite | gemini-1.5-flash | gemini-1.5-pro
///   OpenAI:  whisper-1 | gpt-4o-transcribe | gpt-4o-mini-transcribe
///
/// Default: Gemini gemini-2.0-flash. Free tier rộng (15 RPM, 1500 req/day) + multilingual VN tốt.
/// Fallback: nếu Gemini fail (key sai, rate limit, network) → tự switch Whisper. Log warning.
/// </summary>
public class SpeechToTextService
{
    private readonly ProviderKeyStore _keys;
    private readonly IConfiguration _cfg;
    private readonly ILogger<SpeechToTextService> _log;

    private const long MAX_BYTES = 25 * 1024 * 1024;   // hard cap chung (Whisper 25MB; Gemini inline 20MB)

    public SpeechToTextService(ProviderKeyStore keys, IConfiguration cfg, ILogger<SpeechToTextService> log)
    { _keys = keys; _cfg = cfg; _log = log; }

    public record TranscribeResult(string Text, string Language, double DurationSec, long LatencyMs, string Engine);

    public async Task<TranscribeResult> TranscribeAsync(
        Stream audio, string fileName, string contentType,
        string? language, string? apiKeyOverride, CancellationToken ct)
    {
        if (audio == null) throw new ArgumentException("audio stream null");
        if (audio.CanSeek && audio.Length > MAX_BYTES)
            throw new InvalidOperationException($"File quá lớn: {audio.Length / 1024 / 1024}MB (max 25MB)");

        // Đọc 1 lần vào byte[] để có thể retry sang provider fallback (stream không re-readable).
        byte[] bytes;
        await using (var ms = new MemoryStream())
        {
            await audio.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        var primary  = (_cfg["Speech:Provider"] ?? "gemini").ToLowerInvariant();
        var fallback = _cfg.GetValue<bool?>("Speech:Fallback") ?? true;
        var secondary = primary == "gemini" ? "openai" : "gemini";

        try
        {
            return await DispatchAsync(primary, bytes, fileName, contentType, language, apiKeyOverride, ct);
        }
        catch (InvalidOperationException ex) when (fallback)
        {
            _log.LogWarning(ex, "STT primary {Primary} fail — fallback {Secondary}", primary, secondary);
            try
            {
                var res = await DispatchAsync(secondary, bytes, fileName, contentType, language, apiKeyOverride, ct);
                return res with { Engine = res.Engine + " (fallback)" };
            }
            catch (Exception ex2)
            {
                _log.LogError(ex2, "STT fallback {Secondary} cũng fail", secondary);
                throw new InvalidOperationException(
                    $"Cả {primary} lẫn {secondary} đều lỗi. Primary: {ex.Message}. Fallback: {ex2.Message}");
            }
        }
    }

    private Task<TranscribeResult> DispatchAsync(string engine, byte[] bytes,
        string fileName, string contentType, string? language, string? apiKeyOverride, CancellationToken ct)
        => engine switch
        {
            "gemini" => TranscribeGeminiAsync(bytes, fileName, contentType, language, apiKeyOverride, ct),
            "openai" => TranscribeOpenAiAsync(bytes, fileName, contentType, language, apiKeyOverride, ct),
            _ => throw new InvalidOperationException($"Unknown STT engine: {engine}")
        };

    // ─── Gemini (Mscc.GenerativeAI SDK) ─────────────────────────────────────────
    private async Task<TranscribeResult> TranscribeGeminiAsync(byte[] bytes,
        string fileName, string contentType, string? language, string? apiKeyOverride, CancellationToken ct)
    {
        var apiKey = !string.IsNullOrWhiteSpace(apiKeyOverride)
            ? apiKeyOverride
            : (_cfg["Speech:Gemini:ApiKey"] ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY"));
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Thiếu Gemini API key (Speech:Gemini:ApiKey hoặc env GOOGLE_API_KEY).");

        var modelId = _cfg["Speech:Gemini:Model"] ?? _cfg["Speech:Model"] ?? "gemini-2.0-flash";
        var mime    = NormalizeAudioMime(contentType, fileName, geminiSide: true);
        var t0      = DateTime.UtcNow;

        // Inline base64 — đủ cho ≤20MB. Lớn hơn cần File API (Mscc.GenerativeAI cũng support, TODO).
        if (bytes.Length > 20 * 1024 * 1024)
            throw new InvalidOperationException("Gemini inline max 20MB; cần upload qua File API cho file lớn hơn.");

        var prompt = BuildGeminiPrompt(language);

        try
        {
            var googleAi = new GoogleAI(apiKey: apiKey);
            var model = googleAi.GenerativeModel(model: modelId);

            // Build 2 part: text prompt + inline audio base64
            var parts = new List<IPart>
            {
                new Part { Text = prompt },
                new Part { InlineData = new InlineData { MimeType = mime, Data = Convert.ToBase64String(bytes) } }
            };
            var generationConfig = new GenerationConfig
            {
                Temperature = 0.1f,
                MaxOutputTokens = 4096
            };

            var response = await model.GenerateContent(
                parts: parts,
                generationConfig: generationConfig,
                cancellationToken: ct);

            var text = CleanGeminiTranscript(response.Text ?? "");
            var latencyMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
            _log.LogInformation("Gemini STT OK (SDK): {Chars}ch, {Lat}ms, model={Model}", text.Length, latencyMs, modelId);
            return new TranscribeResult(text, language ?? "vi", 0, latencyMs, $"gemini:{modelId}");
        }
        catch (Exception ex)
        {
            // Mscc.GenerativeAI throw HttpRequestException / generic Exception cho upstream lỗi.
            // Bắt rộng để có thể fallback sang OpenAI Whisper.
            _log.LogWarning(ex, "Gemini SDK STT fail: {Msg}", ex.Message);
            throw new InvalidOperationException($"Gemini SDK lỗi: {ex.Message}");
        }
    }

    // ─── OpenAI Whisper (official OpenAI SDK 2.x) ───────────────────────────────
    private async Task<TranscribeResult> TranscribeOpenAiAsync(byte[] bytes,
        string fileName, string contentType, string? language, string? apiKeyOverride, CancellationToken ct)
    {
        var apiKey = !string.IsNullOrWhiteSpace(apiKeyOverride) ? apiKeyOverride : _keys.Get("openai");
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Thiếu OpenAI API key (Providers:OpenAI:ApiKey hoặc env OPENAI_API_KEY).");

        var modelId = _cfg["Speech:OpenAI:Model"]
                 ?? (_cfg["Speech:Provider"]?.ToLowerInvariant() == "openai" ? _cfg["Speech:Model"] : null)
                 ?? "whisper-1";
        var t0 = DateTime.UtcNow;

        try
        {
            var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey));
            var audioClient = openAiClient.GetAudioClient(modelId);

            using var stream = new MemoryStream(bytes);
            var options = new AudioTranscriptionOptions
            {
                Language = language ?? "vi",
                // whisper-1 hỗ trợ Verbose (có duration); gpt-4o-transcribe chỉ Simple.
                ResponseFormat = modelId == "whisper-1"
                    ? AudioTranscriptionFormat.Verbose
                    : AudioTranscriptionFormat.Simple,
                // Initial prompt: bias Whisper sang lexicon du lịch + lọc filler.
                // Whisper sẽ "nghe" giống cảnh giới context này → tăng accuracy cho từ ngành.
                // Lưu ý: không có "rules", Whisper chỉ dùng prompt như hint, không obey instruction.
                Prompt = BuildWhisperPrompt(language ?? "vi"),
                // Temperature 0 → ít hallucinate, output ổn định nhất.
                Temperature = 0f,
            };

            var result = await audioClient.TranscribeAudioAsync(
                stream, fileName ?? "audio.webm", options, cancellationToken: ct);

            var transcription = result.Value;
            var text = (transcription.Text ?? "").Trim();
            var dur  = transcription.Duration?.TotalSeconds ?? 0;
            var lang = transcription.Language ?? language ?? "vi";
            var latencyMs = (long)(DateTime.UtcNow - t0).TotalMilliseconds;
            _log.LogInformation("OpenAI STT OK (SDK): {Chars}ch, {Dur:F1}s, {Lat}ms, model={Model}",
                text.Length, dur, latencyMs, modelId);
            return new TranscribeResult(text, lang, dur, latencyMs, $"openai:{modelId}");
        }
        catch (ClientResultException ex)
        {
            // SDK chính thức wrap HTTP error thành ClientResultException — message đã có lý do từ API.
            _log.LogWarning("OpenAI SDK STT {Code}: {Msg}", ex.Status, ex.Message);
            throw new InvalidOperationException($"OpenAI lỗi HTTP {ex.Status}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OpenAI SDK STT fail: {Msg}", ex.Message);
            throw new InvalidOperationException($"OpenAI SDK lỗi: {ex.Message}");
        }
    }

    private static string BuildGeminiPrompt(string? language)
    {
        // Prompt được tối ưu cho call-center du lịch: lọc tạp âm + filler word + chuẩn hoá VN.
        // Gemini là LLM-based STT → prompt-driven, có thể custom hành vi rất sâu.
        var langHint = language switch
        {
            "vi" or null => "Vietnamese",
            "en"          => "English",
            _              => language!
        };
        var isVi = language == null || language == "vi";

        // Filler words VN phổ biến — chỉ liệt kê cho language=vi.
        var fillerExample = isVi
            ? "\"ờ\", \"ừm\", \"à\", \"ơ\", \"ờ thì\", \"thì là\", \"kiểu là\", \"như là\""
            : "\"uh\", \"um\", \"er\", \"like\", \"you know\"";

        var example = isVi
            ? "INPUT (raw): \"ờ.. ờ thì... tôi muốn ờ.. đặt tour Nhật à.. là Nhật Bản tháng tư nhé\"\n" +
              "OUTPUT: Tôi muốn đặt tour Nhật Bản tháng 4."
            : "INPUT (raw): \"uh.. um so I want to.. like book a tour to Japan in April you know\"\n" +
              "OUTPUT: I want to book a tour to Japan in April.";

        return $@"You are a professional {langHint} audio transcriptionist for a tour/travel booking call-center.

TASK: Convert the audio to clean, readable {langHint} text.

NOISE FILTERING — SKIP these entirely:
- Background music, traffic, typing, footsteps, door slam, baby crying
- Cough, throat clearing, breath, sigh, sneeze, yawn
- Silent pauses, dead air, ringing tones, beeps
- Crosstalk / second speaker — focus only on the loudest/clearest speaker

LANGUAGE CLEANUP:
- Skip filler words: {fillerExample}. Only keep if they convey meaning.
- Collapse stutter repetition (e.g., ""tôi tôi tôi muốn"" → ""tôi muốn"")
- Add natural punctuation (period, comma, question mark) where appropriate
- Capitalize sentence starts and proper nouns
- Use Arabic numerals for numbers (""tháng 4"" not ""tháng bốn"")
- Preserve EXACT speaker intent — DO NOT add, remove, paraphrase, or interpret meaning

TOURISM CONTEXT (important for accuracy):
- Common tour types: tour Châu Âu, tour Nhật Bản, tour Hàn Quốc, tour nội địa
- Common services: visa, vé máy bay, hướng dẫn viên, khách sạn, ăn uống
- Common questions: giá tour, lịch khởi hành, còn chỗ, đặt tour, hoàn tiền

OUTPUT RULES — STRICT:
- Plain {langHint} text only, on a single block (no headers)
- NO quotes around the result
- NO labels like ""Transcript:"" / ""Phiên âm:"" / ""Bản phiên âm:""
- NO markdown (no **, __, ###, lists)
- NO commentary, NO explanation, NO English translation if {langHint} ≠ English
- If audio is silent, unintelligible, or pure noise → output empty string """"

EXAMPLE:
{example}

Now transcribe the audio:";
    }

    private static string BuildWhisperPrompt(string language)
    {
        // Whisper KHÔNG đọc rule/instruction — chỉ dùng prompt như context để bias vocabulary.
        // Strategy: viết 1 đoạn ví dụ "tự nhiên" có chứa terminology du lịch + chuẩn punctuation.
        // Whisper sẽ "ngại" output filler/sai chính tả terminology vì context biased theo đoạn này.
        // Giới hạn 244 token Whisper — nén ngắn gọn.
        if (language == "vi" || string.IsNullOrEmpty(language))
            return "Cuộc gọi tư vấn tour du lịch. Khách hỏi giá tour Châu Âu, Nhật Bản, Hàn Quốc, " +
                   "nội địa. Trao đổi về lịch khởi hành, visa, vé máy bay, khách sạn, hướng dẫn viên, " +
                   "bảng giá, đặt tour, hoàn tiền. Ngôn ngữ chuẩn, không có \"ờ\", \"ừm\", \"à\".";
        return "Professional travel consultation call. Customer asks about tour pricing, " +
               "destinations, departure dates, visa, flights, hotels, tour guides. Clean speech.";
    }

    private static string CleanGeminiTranscript(string text)
    {
        text = text.Trim();
        // Bóc quote thừa hay gặp khi LLM cố stylize.
        if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0])
            text = text[1..^1].Trim();
        // Bóc prefix "Transcript:" / "Bản phiên âm:" nếu LLM cố thêm.
        foreach (var prefix in new[] { "Transcript:", "Transcription:", "Bản phiên âm:", "Phiên âm:" })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                text = text[prefix.Length..].Trim();
        return text;
    }

    /// <summary>Chuẩn hóa MIME type. Gemini chỉ chấp nhận một số mime cụ thể.</summary>
    private static string NormalizeAudioMime(string? contentType, string? fileName, bool geminiSide)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var clean = contentType.Split(';')[0].Trim().ToLowerInvariant();
            var ok = geminiSide
                ? new[] { "audio/wav", "audio/mp3", "audio/aiff", "audio/aac", "audio/ogg", "audio/flac", "audio/webm", "audio/mpeg", "audio/mp4" }
                : Array.Empty<string>();
            if (geminiSide && ok.Contains(clean)) return clean;
            if (!geminiSide) return clean;
            return clean.StartsWith("audio/") ? clean : "audio/webm";
        }
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext switch
        {
            ".mp3"  => "audio/mp3",
            ".wav"  => "audio/wav",
            ".m4a"  => "audio/mp4",
            ".ogg"  => "audio/ogg",
            ".flac" => "audio/flac",
            ".webm" => "audio/webm",
            _       => "audio/webm"
        };
    }
}
