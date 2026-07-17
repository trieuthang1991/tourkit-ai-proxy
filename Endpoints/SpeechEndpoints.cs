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

        // ── TTS: text → audio. Engine CHỌN THEO CONFIG (giống Models:{Feature}:Provider):
        //    Speech:Tts:Provider = vbee | edge | piper | openai (mặc định "vbee").
        //    Speech:Tts:Fallback = true → engine chính lỗi/chưa cấu hình thì thử các engine còn lại theo thứ tự.
        //    Frontend LUÔN gọi endpoint này (không dùng giọng trình duyệt) → mọi máy nghe cùng 1 giọng.
        v1.MapPost("/speech/tts", async (HttpContext ctx, TtsRequest req, GoogleTtsService google, VbeeTtsService vbee, EdgeTtsService edge,
            PiperTtsService piper, TextToSpeechService openai, TkSessionStore sessions, IConfiguration cfg, ILogger<Program> log) =>
        {
            var sid = Sid(ctx);
            if (sessions.Get(sid) == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req?.Text))
                return Results.BadRequest(new { error = "Thiếu 'text'" });

            var primary = (cfg["Speech:Tts:Provider"] ?? "vbee").Trim().ToLowerInvariant();
            var fallback = cfg.GetValue("Speech:Tts:Fallback", true);
            var order = new List<string> { primary };
            if (fallback)
                foreach (var e in new[] { "google", "vbee", "edge", "piper", "openai" })
                    if (!order.Contains(e)) order.Add(e);

            IResult Emit(byte[] bytes, string mime, string engine, bool cached)
            {
                ctx.Response.Headers["X-Tts-Cached"] = cached ? "1" : "0";
                ctx.Response.Headers["X-Tts-Engine"] = engine;
                return Results.File(bytes, mime);
            }

            Exception? last = null;
            foreach (var eng in order)
            {
                try
                {
                    switch (eng)
                    {
                        case "google":
                            if (!google.Configured) continue;
                            var (mg, cg) = await google.SynthesizeAsync(req.Text, req.Voice, ctx.RequestAborted);
                            return Emit(mg, "audio/mpeg", "google", cg);
                        case "vbee":
                            if (!vbee.Configured) continue;               // KHÔNG truyền req.Voice — Vbee dùng voiceCode ở config
                            var (mv, cv) = await vbee.SynthesizeAsync(req.Text, null, ctx.RequestAborted);
                            return Emit(mv, "audio/mpeg", "vbee", cv);
                        case "edge":
                            if (!edge.Enabled) continue;
                            var (me, ce) = await edge.SynthesizeAsync(req.Text, req.Voice, ctx.RequestAborted);
                            return Emit(me, "audio/mpeg", "edge", ce);
                        case "piper":
                            if (!piper.Configured) continue;
                            var (mw, cw) = await piper.SynthesizeAsync(req.Text, ctx.RequestAborted);
                            return Emit(mw, "audio/wav", "piper", cw);
                        case "openai":
                            var (mo, co) = await openai.SynthesizeAsync(req.Text, req.Voice, ctx.RequestAborted);
                            return Emit(mo, "audio/mpeg", "openai", co);
                        default:
                            continue;   // tên engine lạ trong config → bỏ qua
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                    log.LogWarning("TTS engine '{Engine}' lỗi ({Msg}) — thử engine kế", eng, ex.Message);
                }
            }

            log.LogWarning("TTS: không engine nào chạy được (primary={Primary}, fallback={Fallback})", primary, fallback);
            return Results.Json(new { error = "Không engine TTS nào khả dụng: " + (last?.Message ?? "chưa cấu hình engine") },
                statusCode: 400);
        });
    }

    public record TtsRequest(string Text, string? Voice);

    private static string? Sid(HttpContext ctx)
        => ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? ctx.Request.Query["sessionId"].FirstOrDefault();
    private static IResult Unauthorized()
        => Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);
}
