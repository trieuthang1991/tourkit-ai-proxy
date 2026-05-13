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
                "POST /api/v1/completions/stream"
            }
        }));
        return routes;
    }
}
