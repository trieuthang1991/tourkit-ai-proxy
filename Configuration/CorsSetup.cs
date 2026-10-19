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
            // MỞ CHO MỌI ORIGIN — cố ý, cả trên prod (chốt 19/10/2026): widget chat nội bộ và widget
            // trợ lý được nhúng vào CRM của từng công ty, mỗi nơi một tên miền. Xác thực đi bằng
            // header X-Session-Id / token, KHÔNG dùng cookie (không AllowCredentials), nên mở origin
            // không cho trang lạ mượn phiên của ai.
            .SetIsOriginAllowed(_ => true)
        ));
        return services;
    }
}
