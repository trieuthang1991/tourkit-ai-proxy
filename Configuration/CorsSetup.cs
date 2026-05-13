namespace TourkitAiProxy.Configuration;

public static class CorsSetup
{
    public const string PolicyName = "tourkit";

    public static IServiceCollection AddTourkitCors(this IServiceCollection services)
    {
        services.AddCors(o => o.AddPolicy(PolicyName, p => p
            .WithOrigins(
                "http://localhost:3000",
                "http://localhost:5173",
                "http://localhost:8080",
                "https://tourkit.vn",
                "http://localhost:5080"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .SetIsOriginAllowed(_ => true)   // dev only — siết lại khi deploy prod
        ));
        return services;
    }
}
