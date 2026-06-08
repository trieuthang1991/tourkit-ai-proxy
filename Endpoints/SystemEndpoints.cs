using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Endpoints;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/healthz", () => Results.Json(new
        {
            ok       = true,
            service  = "Tourkit AI Proxy",
            version  = "v1",
            endpoints = new[]
            {
                "GET  /api/v1/providers",
                "GET  /api/v1/models",
                "GET  /api/v1/usage",
                "POST /api/v1/completions",
                "POST /api/v1/completions/stream",
                "GET  /api/v1/workflow-traces"
            }
        }));

        // Xem lại workflow traces đã log (data/workflow-traces.jsonl).
        // Query: ?days=7 ?workflow=CustomerReview ?limit=100
        var v1 = routes.MapGroup("/api/v1");
        v1.MapGet("/workflow-traces", (WorkflowTraceLog log, int? days, string? workflow, int? limit) =>
        {
            var entries = log.Read(days ?? 7, workflow, limit ?? 100);
            var summary = log.Summary(days ?? 7);
            return Results.Json(new
            {
                days      = days ?? 7,
                workflow,
                count     = entries.Count,
                summary   = summary.Select(kv => new {
                    workflow = kv.Key, count = kv.Value.Count,
                    maxMs = kv.Value.MaxMs, minMs = kv.Value.MinMs
                }).ToArray(),
                entries
            });
        });
        return routes;
    }
}
