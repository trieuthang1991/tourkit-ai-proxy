using TourkitAiProxy.Services.Speech;
using TourkitAiProxy.Services.TourKit;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Speech-to-Text endpoint cho ghi âm / upload audio file → text.
///
///   POST /api/v1/speech/transcribe (multipart/form-data)
///     - file: audio binary (≤25MB; webm/wav/mp3/m4a/ogg/flac)
///     - language: optional (default 'vi'); empty/none → Whisper auto-detect
///     - apiKey: optional header X-OpenAI-Key hoặc form field (BYO key client-side)
///   Response: { text, language, durationSec, latencyMs }
/// </summary>
public static class SpeechEndpoints
{
    private const long MAX_BYTES = 25 * 1024 * 1024;

    public static void MapSpeechEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        v1.MapPost("/speech/transcribe", async (HttpContext ctx, SpeechToTextService stt,
            TkSessionStore sessions, ILogger<Program> log) =>
        {
            var sid = Sid(ctx);
            if (sessions.Get(sid) == null) return Unauthorized();

            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "Cần multipart/form-data với field 'file'" });

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "Thiếu file audio (field 'file')" });
            if (file.Length > MAX_BYTES)
                return Results.BadRequest(new { error = $"File {file.Length / 1024 / 1024}MB > 25MB (Whisper max)" });

            // Language: form field OR ?language=vi query OR default 'vi'
            var lang = form["language"].FirstOrDefault()
                       ?? ctx.Request.Query["language"].FirstOrDefault()
                       ?? "vi";

            // Key: form field OR header (X-OpenAI-Key) — BYO client-side, không persist server
            var apiKey = form["apiKey"].FirstOrDefault()
                         ?? ctx.Request.Headers["X-OpenAI-Key"].FirstOrDefault();

            try
            {
                using var stream = file.OpenReadStream();
                var res = await stt.TranscribeAsync(
                    stream, file.FileName, file.ContentType,
                    string.IsNullOrWhiteSpace(lang) ? null : lang,
                    string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
                    ctx.RequestAborted);
                return Results.Json(new
                {
                    text = res.Text,
                    language = res.Language,
                    durationSec = Math.Round(res.DurationSec, 1),
                    latencyMs = res.LatencyMs,
                });
            }
            catch (InvalidOperationException ex)
            {
                log.LogWarning(ex, "STT fail");
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "STT crash");
                return Results.Json(new { error = "Lỗi nội bộ khi transcribe: " + ex.Message }, statusCode: 500);
            }
        })
        .DisableAntiforgery();        // multipart upload từ fetch không gửi anti-forgery token

        // ── TTS: text → audio. Ưu tiên Piper (open-source, offline, FREE) → fallback OpenAI (nếu có key).
        //    Chỉ gọi khi máy KHÔNG có giọng vi trình duyệt. Cache theo nội dung (câu lặp = free).
        v1.MapPost("/speech/tts", async (HttpContext ctx, TtsRequest req, VbeeTtsService vbee, EdgeTtsService edge,
            PiperTtsService piper, TextToSpeechService openai, TkSessionStore sessions, ILogger<Program> log) =>
        {
            var sid = Sid(ctx);
            if (sessions.Get(sid) == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req?.Text))
                return Results.BadRequest(new { error = "Thiếu 'text'" });
            try
            {
                // 0) Vbee: giọng Việt neural chất lượng cao (batch async). Ưu tiên nếu đã cấu hình.
                //    KHÔNG truyền req.Voice (đó là voice trình duyệt/edge) — Vbee dùng voiceCode ở config.
                if (vbee.Configured)
                {
                    try
                    {
                        var (mp3v, cv) = await vbee.SynthesizeAsync(req.Text, null, ctx.RequestAborted);
                        ctx.Response.Headers["X-Tts-Cached"] = cv ? "1" : "0";
                        ctx.Response.Headers["X-Tts-Engine"] = "vbee";
                        return Results.File(mp3v, "audio/mpeg");
                    }
                    catch (Exception ex) { log.LogWarning("vbee-tts fail ({Msg}) — thử engine khác", ex.Message); }
                }
                // 1) edge-tts: giọng vi neural CHUẨN, free (cần mạng). MS chặn/lỗi → thử engine sau.
                if (edge.Enabled)
                {
                    try
                    {
                        var (mp3e, ce) = await edge.SynthesizeAsync(req.Text, req.Voice, ctx.RequestAborted);
                        ctx.Response.Headers["X-Tts-Cached"] = ce ? "1" : "0";
                        ctx.Response.Headers["X-Tts-Engine"] = "edge";
                        return Results.File(mp3e, "audio/mpeg");
                    }
                    catch (Exception ex) { log.LogWarning("edge-tts fail ({Msg}) — thử engine khác", ex.Message); }
                }
                // 2) Piper offline (nếu cấu hình)
                if (piper.Configured)
                {
                    var (wav, cached) = await piper.SynthesizeAsync(req.Text, ctx.RequestAborted);
                    ctx.Response.Headers["X-Tts-Cached"] = cached ? "1" : "0";
                    ctx.Response.Headers["X-Tts-Engine"] = "piper";
                    return Results.File(wav, "audio/wav");
                }
                // 3) OpenAI (nếu có key)
                var (mp3, cached2) = await openai.SynthesizeAsync(req.Text, req.Voice, ctx.RequestAborted);
                ctx.Response.Headers["X-Tts-Cached"] = cached2 ? "1" : "0";
                ctx.Response.Headers["X-Tts-Engine"] = "openai";
                return Results.File(mp3, "audio/mpeg");
            }
            catch (InvalidOperationException ex)
            {
                log.LogWarning(ex, "TTS fail");
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "TTS crash");
                return Results.Json(new { error = "Lỗi nội bộ khi TTS: " + ex.Message }, statusCode: 500);
            }
        });
    }

    public record TtsRequest(string Text, string? Voice);

    private static string? Sid(HttpContext ctx)
        => ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? ctx.Request.Query["sessionId"].FirstOrDefault();
    private static IResult Unauthorized()
        => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);
}
