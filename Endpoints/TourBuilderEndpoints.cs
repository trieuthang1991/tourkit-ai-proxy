using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Tour;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Soạn Tour GIT bằng AI — bóc tách mô tả tự do thành form prefill.
///   POST /api/v1/tour-builder/parse — body {prompt, provider, model, apiKey} → TourBuilderDraft
/// </summary>
public static class TourBuilderEndpoints
{
    public static void MapTourBuilderEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        v1.MapPost("/tour-builder/parse", async (TourBuilderRequest req, TourBuilderService svc,
            TourkitAiProxy.Services.Workflow.IWorkflowTraceAccessor trace, CancellationToken ct) =>
        {
            try
            {
                var draft = await svc.ParseAsync(req, ct);
                var traceObj = trace.Current?.Enabled == true ? trace.Current.Build() : null;
                if (traceObj != null) return Results.Json(new { draft, _trace = traceObj });
                return Results.Json(draft);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = "Bóc tách lỗi: " + ex.Message }, statusCode: 502);
            }
        });
    }
}
