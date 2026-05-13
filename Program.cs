using TourkitAiProxy.Configuration;
using TourkitAiProxy.Endpoints;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Reviews;

var builder = WebApplication.CreateBuilder(args);

// ─── DI / services ────────────────────────────────────────────────────────────
builder.Services.AddTourkitCors();

builder.Services.AddHttpClient("opencode", c =>
{
    c.BaseAddress = new Uri("https://opencode.ai/");
    c.Timeout     = TimeSpan.FromSeconds(120);
});
builder.Services.AddHttpClient("nine-routes", c =>
{
    c.Timeout = TimeSpan.FromSeconds(120);
});

builder.Services.AddSingleton<UsageTracker>();

// AI providers — đăng ký 1 lần ở đây, ProviderRegistry tự pickup qua IEnumerable<IAiProvider>.
// Thêm provider mới: implement IAiProvider + AddSingleton<IAiProvider, NewProvider>().
builder.Services.AddSingleton<IAiProvider, OpenCodeProvider>();
builder.Services.AddSingleton<IAiProvider, NineRoutesProvider>();
builder.Services.AddSingleton<ProviderRegistry>();

// Legacy OpenCodeClient cho code cũ còn reference (sẽ remove khi clean xong)
builder.Services.AddScoped<OpenCodeClient>();

// Customer Review feature services (file-backed persistence, in-memory job store).
builder.Services.AddSingleton<CustomerRepository>();
builder.Services.AddSingleton<ReviewRepository>();
builder.Services.AddSingleton<BatchJobStore>();
builder.Services.AddSingleton<ReviewService>();
builder.Services.AddSingleton<BatchService>();

// ─── Pipeline ────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseCors(CorsSetup.PolicyName);
app.UseTourkitStaticFiles();

// ─── Routes ──────────────────────────────────────────────────────────────────
app.MapSystemEndpoints();
app.MapAiEndpoints();
app.MapReviewEndpoints();

app.Run();
