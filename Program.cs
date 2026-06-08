using TourkitAiProxy.Configuration;
using TourkitAiProxy.Endpoints;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Reviews;
using TourkitAiProxy.Services.Reviews.Agents;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Services.Workflow;

var builder = WebApplication.CreateBuilder(args);

// Visa upload có thể tới 25MB × 10 file (PDF nhiều trang). Tăng giới hạn body request global lên 300MB.
// Đủ cho mọi upload PDF/DOCX/ảnh; route khác không bị ảnh hưởng (chỉ là trần).
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 300L * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 300L * 1024 * 1024;
    o.ValueLengthLimit = int.MaxValue;
});

// ─── DI / services ────────────────────────────────────────────────────────────
builder.Services.AddTourkitCors();

builder.Services.AddHttpClient("opencode", c =>
{
    c.BaseAddress = new Uri("https://opencode.ai/");
    c.Timeout     = TimeSpan.FromSeconds(120);
});
var nineRoutes = builder.Services.AddHttpClient("nine-routes", c =>
{
    c.Timeout = TimeSpan.FromSeconds(120);
});
// 9routes có thể chạy HTTPS với chứng chỉ TỰ KÝ (vd https tới IP). Bật cờ này để chấp nhận
// cert tự ký CHỈ cho client 9routes (không ảnh hưởng các call khác). Tắt khi đã có cert hợp lệ.
if (builder.Configuration.GetValue<bool>("Providers:NineRoutes:AllowInsecureTls"))
{
    nineRoutes.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });
}

builder.Services.AddHttpClient("openai",    c => c.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddHttpClient("anthropic", c => c.Timeout = TimeSpan.FromSeconds(120));

builder.Services.AddSingleton<UsageTracker>();
// AI usage log per-request (data/ai-usage.jsonl) — biết feature/user/tenant nào tiêu bao nhiêu.
builder.Services.AddHttpContextAccessor();

// ITenantContext — đọc tenantId từ X-Session-Id header. Phase 1 RESTful sẽ dùng nhiều
// qua TenantFilter; ở plan này chỉ register, services vẫn nhận tenantId qua parameter.
builder.Services.AddScoped<TourkitAiProxy.Services.TourKit.ITenantContext,
                          TourkitAiProxy.Services.TourKit.HttpTenantContext>();

// Workflow debug trace: middleware detect ?debug=1 / X-Debug header → tạo TraceCollector per-request.
// Service nào cần ghi trace inject IWorkflowTraceAccessor.Current?.Step(...). No-op khi debug off.
builder.Services.AddSingleton<IWorkflowTraceAccessor, WorkflowTraceAccessor>();
// Trace nào có data thì lưu data/workflow-traces.jsonl để xem lại sau (audit, post-mortem).
builder.Services.AddSingleton<WorkflowTraceLog>();
builder.Services.AddSingleton<TourkitAiProxy.Services.AiUsageLog>();
builder.Services.AddSingleton<TourkitAiProxy.Services.AiCallContext>();
// Cache prompt-hash 24h cho Visa/Deal/TourBuilder (Redis nếu có, fallback in-memory).
builder.Services.AddSingleton<TourkitAiProxy.Services.Cache.AiResponseCache>();

// Lưu API key provider (OpenAI/Anthropic) nhập từ UI — server-side, mã hóa, gitignored.
builder.Services.AddSingleton<ProviderKeyStore>();

// AI providers — đăng ký 1 lần ở đây, ProviderRegistry tự pickup qua IEnumerable<IAiProvider>.
// Thêm provider mới: implement IAiProvider + AddSingleton<IAiProvider, NewProvider>().
builder.Services.AddSingleton<IAiProvider, OpenCodeProvider>();
builder.Services.AddSingleton<IAiProvider, NineRoutesProvider>();
builder.Services.AddSingleton<IAiProvider, OpenAIProvider>();
builder.Services.AddSingleton<IAiProvider, AnthropicProvider>();
builder.Services.AddSingleton<ProviderRegistry>();

// Legacy OpenCodeClient cho code cũ còn reference (sẽ remove khi clean xong)
builder.Services.AddScoped<OpenCodeClient>();

// Workflow: connection factory SQL Server (PushDb shared instance, decrypt ENC: tự động).
builder.Services.AddSingleton<TourkitAiProxy.Services.Db.TourkitAiDb>();

// Reusable Anthropic native-tools client — share giữa các feature single-shot
// (Review/Visa/Deal/Wizard) qua AnthropicToolsClient.RunAsync(..., terminalToolName).
builder.Services.AddSingleton<TourkitAiProxy.Services.Workflow.AnthropicToolsClient>();
// Thin wrapper cho score-like service (Visa/Deal/Tour) — gọi RunAsync<T> với 1 terminal tool + parser.
builder.Services.AddSingleton<TourkitAiProxy.Services.Workflow.NativeToolScorer>();

// Customer Review feature services. ReviewRepository nay DB-backed (PushDb.dbo.Reviews) với fallback file.
builder.Services.AddSingleton<CustomerRepository>();
builder.Services.AddSingleton<ReviewRepository>();
builder.Services.AddSingleton<BatchJobStore>();
// Review agent runtimes — thứ tự quan trọng: NativeToolReviewAgent (Anthropic native function-calling)
// chạy trước, JsonPromptReviewAgent là fallback cho mọi provider khác (OpenCode/9routes/OpenAI).
// ReviewService resolve agent đầu tiên Supports(defaultProviderId).
builder.Services.AddSingleton<IReviewAgent, NativeToolReviewAgent>();
builder.Services.AddSingleton<IReviewAgent, JsonPromptReviewAgent>();
builder.Services.AddSingleton<ReviewService>();
builder.Services.AddSingleton<BatchService>();

// Chat-Analytics ("Trợ lý số liệu") — gọi TourKit.Api (toutkit-app) qua JWT.
// BaseUrl: TourKit:BaseUrl (mặc định Production). Auth: client gửi token mã hóa (Crypton) → /login-token.
builder.Services.AddHttpClient("tourkit", c =>
{
    // Staging có đủ surface /api/ai/* (prod chưa). Đổi sang prod khi đã deploy.
    var baseUrl = builder.Configuration["TourKit:BaseUrl"] ?? "https://mobile-test-api-2.tourkit.vn";
    c.BaseAddress = new Uri(baseUrl);
    c.Timeout     = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<TourKitApiClient>();
builder.Services.AddSingleton<TkSessionStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Cache.ChatCache>();   // Redis (nếu có) / in-memory
// Agent runtimes -- thu tu quan trong: NativeToolUseAgent (Anthropic native tools) chay truoc,
// JsonPlannerAgent la fallback cho moi provider khac (OpenCode, 9routes...).
// ChatAgentService resolve runtime dau tien co Supports(provider)=true.
builder.Services.AddSingleton<IAgentRuntime, NativeToolUseAgent>();
builder.Services.AddSingleton<IAgentRuntime, JsonPlannerAgent>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Chat.UnresolvedQuestionsLog>();
builder.Services.AddSingleton<ChatAgentService>();

// SmartMail AI — hộp thư Gmail (IMAP/MailKit) + phân loại AI + soạn nháp trả lời.
// Creds Gmail: lưu per-tenant trong DB qua MailAccountStore (App Password Crypton-encrypted), nhập từ UI.
builder.Services.AddSingleton<TourkitAiProxy.Services.Mail.MailAccountStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Mail.MailSyncStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Mail.MailRepository>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Mail.IMailSource, TourkitAiProxy.Services.Mail.GmailImapClient>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Mail.IMailSender, TourkitAiProxy.Services.Mail.GmailSmtpClient>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Mail.MailClassifier>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Mail.MailReplyService>();

// Soạn Tour GIT bằng AI — bóc mô tả tự do thành form Tour GIT (Type=3) cho NV prefill.
builder.Services.AddSingleton<TourkitAiProxy.Services.Tour.TourBuilderService>();

// Ưu tiên Deal AI — phân tích cơ hội bán hàng (booking-ticket), chấm khả năng thắng, xếp hạng ưu tiên.
// 2 tầng: heuristic xếp sơ bộ → AI chấm sâu top N (kèm lịch sử hành động Sale). Cần session TourKit.
builder.Services.AddSingleton<TourkitAiProxy.Services.Deals.DealOpportunityClient>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Deals.DealScoringService>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Deals.DealRepository>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Deals.DealBatchJobStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Deals.DealBatchService>();

// Thẩm định Visa AI — upload hồ sơ → AI vision đọc → chấm tỉ lệ đậu/rớt.
// File gốc lưu tạm data/visa-files/ (tự xóa 7 ngày), kết quả data/visa-assessments.json.
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaFileStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaRepository>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaExtractionService>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaScoringService>();

// Store bền vững (Redis dùng chung / fallback file) + nguồn dữ liệu THẬT TourKit (KH + NCC).
builder.Services.AddSingleton<TourkitAiProxy.Services.Cache.RedisProvider>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Store.TenantStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.TourKit.TourKitCustomerSource>();
builder.Services.AddSingleton<TourkitAiProxy.Services.TourKit.TourKitNccClient>();

// ─── Pipeline ────────────────────────────────────────────────────────────────
var app = builder.Build();

// Multi-tenant migration: backup legacy single-tenant data lần đầu deploy.
// Sync — chỉ move file, không cần fire-and-forget. Idempotent — lần sau noop.
TourkitAiProxy.Services.Db.MultiTenantMigration.Run(
    Path.Combine(app.Environment.ContentRootPath, "data"),
    app.Services.GetRequiredService<ILogger<Program>>());

// DB init: tạo schema dbo.Reviews/DealScores/AiHistory nếu chưa có + migrate JSON cũ vào DB.
// Chạy async fire-and-forget — không block startup. Nếu DB chưa sẵn sàng → log warning, fallback file.
_ = Task.Run(async () =>
{
    using var scope = app.Services.CreateScope();
    var reviewRepo = scope.ServiceProvider.GetRequiredService<ReviewRepository>();
    try { await reviewRepo.InitAsync(); }
    catch (Exception ex)
    {
        scope.ServiceProvider.GetRequiredService<ILogger<Program>>()
            .LogError(ex, "DB init/migrate fail — proxy vẫn chạy với fallback file");
    }
});

app.UseCors(CorsSetup.PolicyName);
// Trace middleware ĐẦU pipeline (trước routing/endpoints) — bất kỳ endpoint nào cũng có thể đọc trace.
app.UseMiddleware<WorkflowTraceMiddleware>();
app.UseTourkitStaticFiles();

// ─── Routes ──────────────────────────────────────────────────────────────────
app.MapSystemEndpoints();
app.MapAiEndpoints();
app.MapReviewEndpoints();
app.MapChatEndpoints();
app.MapMailEndpoints();
app.MapTourEndpoints();
app.MapVisaEndpoints();
app.MapDealEndpoints();
app.MapTourBuilderEndpoints();
app.MapAiUsageEndpoints();

// SPA fallback: mọi GET không match API/file (vd /mail, /customers, /assistant) → trả index.html.
// Cho phép HTML5 history routing thay vì hash (#). F5 trên /mail không còn 404.
// MapFallbackToFile chạy SAU cùng — chỉ catch khi không có route nào match trước đó.
app.MapFallbackToFile("index.html");

app.Run();
