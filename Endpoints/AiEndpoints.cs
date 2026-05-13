using System.Text;
using System.Text.Json;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Providers;

namespace TourkitAiProxy.Endpoints;

/// REST endpoints (versioned dưới /api/v1):
///   GET  /api/v1/providers              — list providers + models (single source of truth cho UI)
///   GET  /api/v1/models                 — flat list models (alias cho compat)
///   GET  /api/v1/usage                  — usage tracker snapshot
///   POST /api/v1/completions            — buffered completion (returns JSON)
///   POST /api/v1/completions/stream     — SSE stream
///
/// Request body shape: {prompt, provider?, model?, maxTokens?, temperature?, system?}.
/// Provider/model routing là server-side; API keys không bao giờ leak ra client.
///
/// Legacy aliases (giữ tương thích frontend chưa migrate):
///   POST /api/ai/complete  → /api/v1/completions
///   POST /api/ai/stream    → /api/v1/completions/stream
public static class AiEndpoints
{
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        v1.MapGet("/providers", (ProviderRegistry reg) => Results.Json(
            reg.All.Select(p => new
            {
                id = p.Id,
                label = p.Label,
                models = p.Models.Select(m => new { id = m.Id, label = m.Label, recommended = m.Recommended })
            })
        ));

        // Flat models list (cho client chỉ cần model dropdown, không quan tâm provider)
        v1.MapGet("/models", (ProviderRegistry reg) => Results.Json(
            reg.All.SelectMany(p => p.Models.Select(m => new
            {
                id = m.Id, label = m.Label, recommended = m.Recommended, provider = p.Id
            }))
        ));

        // Live model list cho provider có model động (vd 9routes proxy nhiều upstream).
        // OpenCode trả static list. Lỗi → fallback static.
        v1.MapGet("/providers/{id}/models", async (string id, ProviderRegistry reg, ILogger<Program> log, HttpContext ctx) =>
        {
            var provider = reg.Resolve(id);
            try
            {
                var models = await provider.ListLiveModelsAsync(ctx.RequestAborted);
                return Results.Json(models.Select(m => new { id = m.Id, label = m.Label, recommended = m.Recommended }));
            }
            catch (UpstreamException ex)
            {
                log.LogWarning("List models upstream {Status}: {Body}", ex.Status, ex.Body);
                return Results.Json(new { error = ex.Message, status = ex.Status, body = ex.Body }, statusCode: ex.Status);
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "List models connect failed: {Provider}", provider.Id);
                return Results.Json(new { error = $"Không kết nối được {provider.Label}", detail = ex.Message }, statusCode: 502);
            }
        });

        v1.MapGet("/usage",  (UsageTracker u) => Results.Json(u.Snapshot()));
        v1.MapPost("/completions",        HandleCompleteAsync);
        v1.MapPost("/completions/stream", HandleStreamAsync);

        // Legacy aliases
        var legacy = routes.MapGroup("/api/ai");
        legacy.MapGet ("/models",   (ProviderRegistry reg) => Results.Json(
            reg.All.SelectMany(p => p.Models.Select(m => new
            {
                id = m.Id, label = m.Label, recommended = m.Recommended, provider = p.Id
            }))
        ));
        legacy.MapGet ("/usage",    (UsageTracker u) => Results.Json(u.Snapshot()));
        legacy.MapPost("/complete", HandleCompleteAsync);
        legacy.MapPost("/stream",   HandleStreamAsync);

        return routes;
    }

    // ─── POST /completions ────────────────────────────────────────────────────
    private static async Task<IResult> HandleCompleteAsync(
        CompleteRequest req,
        ProviderRegistry registry,
        UsageTracker usage,
        ILogger<Program> log,
        HttpContext ctx)
    {
        var provider = registry.Resolve(req.Provider);
        try
        {
            var result = await provider.CompleteAsync(req, ctx.RequestAborted);
            if (result.OutputTokens > 0)
                usage.Track($"{provider.Id}:{result.Model}", result.InputTokens, result.OutputTokens, result.LatencyMs);

            return Results.Json(new
            {
                text         = result.Text,
                provider     = provider.Id,
                model        = result.Model,
                latencyMs    = result.LatencyMs,
                inputTokens  = result.InputTokens,
                outputTokens = result.OutputTokens,
                finishReason = result.FinishReason,
                attempts     = result.Attempts,
                warning      = result.Warning,
                rawUpstream  = result.RawUpstream
            });
        }
        catch (UpstreamException ex)
        {
            log.LogWarning("Upstream {Status}: {Body}", ex.Status, ex.Body);
            return Results.Json(new { error = ex.Message, status = ex.Status, body = ex.Body }, statusCode: ex.Status);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Provider connect failed: {Provider}", provider.Id);
            return Results.Json(new { error = $"Không kết nối được {provider.Label}", detail = ex.Message }, statusCode: 502);
        }
        catch (InvalidOperationException ex)
        {
            log.LogError(ex, "Provider config error");
            return Results.Json(new { error = ex.Message }, statusCode: 500);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled provider error");
            return Results.Json(new { error = $"Server error ({ex.GetType().Name})", detail = ex.Message }, statusCode: 500);
        }
    }

    // ─── POST /completions/stream ────────────────────────────────────────────
    // SSE relay. No retry — connection failure surfaces as terminal `error` event.
    private static async Task HandleStreamAsync(
        CompleteRequest req,
        HttpContext ctx,
        ProviderRegistry registry,
        UsageTracker usage,
        ILogger<Program> log)
    {
        ctx.Response.Headers["Content-Type"]      = "text/event-stream";
        ctx.Response.Headers["Cache-Control"]     = "no-cache, no-transform";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        var bodyFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        bodyFeature?.DisableBuffering();
        await ctx.Response.StartAsync(ctx.RequestAborted);

        async Task WriteEventAsync(object payload)
        {
            var line = "data: " + JsonSerializer.Serialize(payload) + "\n\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }

        var provider = registry.Resolve(req.Provider);
        log.LogInformation("[stream] start provider={Provider} model={Model}", provider.Id, req.Model);

        try
        {
            var result = await provider.StreamAsync(req,
                async delta => await WriteEventAsync(new { delta }),
                ctx.RequestAborted);

            log.LogInformation("[stream] done provider={Provider} chunks={Chunks} len={Len} latencyMs={Ms}",
                provider.Id, result.Attempts, result.Text.Length, result.LatencyMs);

            if (result.OutputTokens > 0)
                usage.Track($"{provider.Id}:{result.Model}", result.InputTokens, result.OutputTokens, result.LatencyMs);

            await WriteEventAsync(new
            {
                done = true,
                text = result.Text,
                provider = provider.Id,
                model = result.Model,
                latencyMs    = result.LatencyMs,
                inputTokens  = result.InputTokens,
                outputTokens = result.OutputTokens,
                finishReason = result.FinishReason
            });
        }
        catch (OperationCanceledException)
        {
            log.LogInformation("[stream] client aborted");
        }
        catch (UpstreamException ex)
        {
            log.LogWarning("[stream] upstream {Status}: {Body}", ex.Status, ex.Body);
            try
            {
                await WriteEventAsync(new { error = ex.Message, status = ex.Status, body = ex.Body });
                await WriteEventAsync(new { done = true });
            }
            catch { }
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "[stream] connect failed");
            try
            {
                await WriteEventAsync(new { error = $"Không kết nối được {provider.Label}", detail = ex.Message });
                await WriteEventAsync(new { done = true });
            }
            catch { }
        }
        catch (InvalidOperationException ex)
        {
            // Missing config (vd: API key chưa setup)
            log.LogError(ex, "[stream] provider config error");
            try
            {
                await WriteEventAsync(new { error = ex.Message });
                await WriteEventAsync(new { done = true });
            }
            catch { }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[stream] failed");
            try
            {
                await WriteEventAsync(new { error = $"stream failed ({ex.GetType().Name})", detail = ex.Message });
                await WriteEventAsync(new { done = true });
            }
            catch { }
        }
    }
}
