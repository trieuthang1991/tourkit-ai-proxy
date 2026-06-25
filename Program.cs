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

// ─── Outbound TLS: ép TLS 1.2/1.3 ─────────────────────────────────────────────
// Defensive: Windows Server 2012 R2/2016 thường default về TLS 1.0/1.1 → OpenAI/Anthropic/DeepSeek
// reject → "The SSL connection could not be established". Set sớm trước khi HttpClient nào được tạo.
// Lý tưởng nên fix qua registry SCHANNEL + SchUseStrongCrypto + reboot, nhưng cờ này là backup
// để app vẫn chạy được nếu OS chưa kịp patch.
System.Net.ServicePointManager.SecurityProtocol =
    System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

// Visa upload có thể tới 25MB × 10 file (PDF nhiều trang). Tăng giới hạn body request global lên 300MB.
// Đủ cho mọi upload PDF/DOCX/ảnh; route khác không bị ảnh hưởng (chỉ là trần).
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 300L * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 300L * 1024 * 1024;
    o.ValueLengthLimit = int.MaxValue;
});

// ─── HTTPS redirect (PRODUCTION only) ───────────────────────────────────────
// App deploy phía sau reverse proxy (IIS / Nginx) — proxy terminate SSL rồi forward HTTP với
// header X-Forwarded-Proto: https. Cấu hình ForwardedHeaders để middleware sau biết scheme thật.
// Skip dev (localhost:5080 chỉ HTTP) để khỏi vỡ flow F5.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                       | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    // Clear known networks/proxies để chấp nhận header từ MỌI proxy ngược (an toàn nếu chỉ deploy
    // sau 1 layer proxy — nếu chain nhiều cấp cần whitelist từng IP).
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});
// HSTS — bảo trình duyệt "lần sau cứ HTTPS, đừng hỏi". Mặc định 30 ngày (đủ cho rollback an toàn).
builder.Services.AddHsts(o =>
{
    o.Preload = false;
    o.IncludeSubDomains = false;
    o.MaxAge = TimeSpan.FromDays(30);
});

// ─── DI / services ────────────────────────────────────────────────────────────
builder.Services.AddTourkitCors();

// Provider-wide TLS bypass (escape hatch cho Server 2012 R2 / OS không có root CA hiện đại).
// Bật `Providers:AllowInsecureTls=true` → mọi HttpClient AI bỏ qua cert chain check.
// Risk: nếu attacker MITM được giữa server và OpenAI/Anthropic/DeepSeek → đọc được API key.
// Acceptable khi server ở DC tin tưởng + chỉ gọi public endpoint cố định.
var allowInsecure = builder.Configuration.GetValue<bool>("Providers:AllowInsecureTls");
Console.WriteLine($"[Startup] Providers:AllowInsecureTls = {allowInsecure}");
HttpMessageHandler MakeInsecureHandler() => new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                 | System.Security.Authentication.SslProtocols.Tls13,
};

// Helper: gắn HttpLoggingHandler + (optional) bypass cert cho 1 named client.
// Mỗi outbound call qua client đó sẽ log: URL, status, duration, full exception chain nếu fail.
// → Biết NGAY upstream nào đang fail SSL + root cause là gì.
void AttachLogAndInsecure(IHttpClientBuilder cb, string name, bool insecure)
{
    cb.AddHttpMessageHandler(sp =>
        new TourkitAiProxy.Services.Http.HttpLoggingHandler(
            sp.GetRequiredService<ILogger<TourkitAiProxy.Services.Http.HttpLoggingHandler>>(), name));
    if (insecure) cb.ConfigurePrimaryHttpMessageHandler(MakeInsecureHandler);
}

AttachLogAndInsecure(
    builder.Services.AddHttpClient("opencode", c =>
    {
        c.BaseAddress = new Uri("https://opencode.ai/");
        c.Timeout     = TimeSpan.FromSeconds(120);
    }), "opencode", allowInsecure);

// 9routes có thể chạy HTTPS với cert tự ký (vd https tới IP). Override riêng `Providers:NineRoutes:AllowInsecureTls`.
AttachLogAndInsecure(
    builder.Services.AddHttpClient("nine-routes", c => c.Timeout = TimeSpan.FromSeconds(120)),
    "nine-routes",
    allowInsecure || builder.Configuration.GetValue<bool>("Providers:NineRoutes:AllowInsecureTls"));

AttachLogAndInsecure(builder.Services.AddHttpClient("openai",    c => c.Timeout = TimeSpan.FromSeconds(120)), "openai", allowInsecure);
AttachLogAndInsecure(builder.Services.AddHttpClient("anthropic", c => c.Timeout = TimeSpan.FromSeconds(120)), "anthropic", allowInsecure);
AttachLogAndInsecure(builder.Services.AddHttpClient("deepseek",  c => c.Timeout = TimeSpan.FromSeconds(120)), "deepseek", allowInsecure);

builder.Services.AddSingleton<UsageRepository>();
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
builder.Services.AddSingleton<TourkitAiProxy.Services.AiUsageHistoryRepository>(); // Dapper repo cho dbo.AiUsageHistory (granular per-request)
builder.Services.AddSingleton<TourkitAiProxy.Services.AiUsageLog>();
builder.Services.AddSingleton<TourkitAiProxy.Services.AiCallContext>();
// Cache prompt-hash 24h cho Visa/Deal/TourBuilder (Redis nếu có, fallback in-memory).
builder.Services.AddSingleton<TourkitAiProxy.Services.Cache.AiResponseCache>();

// Lưu API key provider (OpenAI/Anthropic) nhập từ UI — server-side, mã hóa, gitignored.
builder.Services.AddSingleton<ProviderKeyStore>();

// Admin governance — auth qua Admin:Users (JSON config) + in-mem session.
builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.AdminUserStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.AdminSessionStore>();

// AI providers — đăng ký 1 lần ở đây, ProviderRegistry tự pickup qua IEnumerable<IAiProvider>.
// Thêm provider mới: implement IAiProvider + AddSingleton<IAiProvider, NewProvider>().
builder.Services.AddSingleton<IAiProvider, OpenCodeProvider>();
builder.Services.AddSingleton<IAiProvider, NineRoutesProvider>();
builder.Services.AddSingleton<IAiProvider, OpenAIProvider>();
builder.Services.AddSingleton<IAiProvider, AnthropicProvider>();
builder.Services.AddSingleton<IAiProvider, DeepSeekProvider>();
builder.Services.AddSingleton<IAiProvider, GrokProvider>();
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
builder.Services.AddSingleton<TourkitAiProxy.Services.NccImport.NccImportService>();
builder.Services.AddSingleton<ReviewService>();
builder.Services.AddSingleton<BatchService>();

// Chat-Analytics ("Trợ lý số liệu") — gọi TourKit.Api (toutkit-app) qua JWT.
// BaseUrl: TourKit:BaseUrl (mặc định Production). Auth: client gửi token mã hóa (Crypton) → /login-token.
AttachLogAndInsecure(
    builder.Services.AddHttpClient("tourkit", c =>
    {
        // Staging có đủ surface /api/ai/* (prod chưa). Đổi sang prod khi đã deploy.
        var baseUrl = builder.Configuration["TourKit:BaseUrl"] ?? "https://mobile-test-api-2.tourkit.vn";
        c.BaseAddress = new Uri(baseUrl);
        c.Timeout     = TimeSpan.FromSeconds(60);
    }), "tourkit",
    allowInsecure || builder.Configuration.GetValue<bool>("TourKit:AllowInsecureTls"));
builder.Services.AddSingleton<TourKitApiClient>();
builder.Services.AddSingleton<TkSessionRepository>();
builder.Services.AddSingleton<TkSessionStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Cache.RedisStore>();  // generic Redis cho mọi feature
// Single source of truth cho cấu hình AI model per-feature.
builder.Services.AddSingleton<TourkitAiProxy.Services.Providers.AiModelRegistry>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Cache.ChatCache>();   // Redis (nếu có) / in-memory
builder.Services.AddSingleton<TourkitAiProxy.Services.Quota.TenantQuotaRepository>(); // Dapper repo cho dbo.TenantQuota
builder.Services.AddSingleton<TourkitAiProxy.Services.Quota.TenantQuotaStore>();      // Quota AI per-tenant: SQL nguồn thực + in-mem cache
builder.Services.AddHostedService<TourkitAiProxy.Services.Quota.QuotaFlushService>(); // Tick 5s: drain pendingDeltas → 1 UPDATE per tenant (batched flush)

// Tingee VietQR client cho luồng mua quota. Mock-first: dùng vietqr.io public, simulate-paid endpoint
// để dev test. Khi có ApiKey thật → set `Tingee:Mock=false` → switch sang TingeeHttpClient.
AttachLogAndInsecure(
    builder.Services.AddHttpClient("tingee", c => c.Timeout = TimeSpan.FromSeconds(30)),
    "tingee", allowInsecure);
if (builder.Configuration.GetValue<bool?>("Tingee:Mock") != false)
    builder.Services.AddSingleton<TourkitAiProxy.Services.Quota.ITingeeClient,
                                  TourkitAiProxy.Services.Quota.MockTingeeClient>();
else
    builder.Services.AddSingleton<TourkitAiProxy.Services.Quota.ITingeeClient,
                                  TourkitAiProxy.Services.Quota.TingeeHttpClient>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Quota.QuotaOrderRepository>();

// Widget Chat — token per-tenant, embed JS vào site khách.
//   • FAQ mode (WidgetChatService): chỉ system prompt + LLM kiến thức nền.
//   • CRM mode (WidgetChatCrmService): plan → call /api/ai/* whitelist → analyze. Cần link CRM.
builder.Services.AddSingleton<TourkitAiProxy.Services.Widget.WidgetTokenRepository>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Widget.WidgetChatService>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Widget.WidgetCrmLinkService>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Widget.WidgetChatCrmService>();
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

// Báo giá tour persist (replace flow localStorage cũ). DB-backed, per-tenant scope.
builder.Services.AddSingleton<TourkitAiProxy.Services.TourQuotes.TourQuoteRepository>();

// Speech-to-Text (Whisper) — chat assistant ghi âm / upload audio → text.
builder.Services.AddSingleton<TourkitAiProxy.Services.Speech.SpeechToTextService>();

// Thẩm định Visa AI — upload hồ sơ → AI vision đọc → chấm tỉ lệ đậu/rớt.
// File gốc lưu tạm data/visa-files/ (tự xóa 7 ngày), kết quả data/visa-assessments.json.
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaFileStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaRepository>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaQuestionRepository>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaExtractionService>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaScoringService>();

// Store bền vững (Redis dùng chung / fallback file) + nguồn dữ liệu THẬT TourKit (KH + NCC).
builder.Services.AddSingleton<TourkitAiProxy.Services.Cache.RedisProvider>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Store.TenantStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.TourKit.TourKitCustomerSource>();
builder.Services.AddSingleton<TourkitAiProxy.Services.TourKit.TourKitNccClient>();

// ─── Response compression (Brotli + Gzip) ─────────────────────────────────────
// Frontend bundle ~596KB + styles.css ~352KB gửi RAW trước đây → public landing/NCC
// tải dư ~700KB mỗi lần load lạnh. Brotli nén JS/CSS xuống ~20-25% → ~200KB tổng.
// EnableForHttps=true vì site chạy sau reverse proxy TLS (forwarded headers).
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    // SSE (text/event-stream) KHÔNG nén — buffering phá streaming. Chỉ nén static text.
    o.MimeTypes = new[]
    {
        "text/html", "text/css", "text/plain",
        "text/javascript", "application/javascript", "application/json",
        "image/svg+xml", "application/manifest+json"
    };
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Optimal);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Optimal);

// ─── Pipeline ────────────────────────────────────────────────────────────────
var app = builder.Build();

// Multi-tenant migration: backup legacy single-tenant data lần đầu deploy.
// Sync — chỉ move file, không cần fire-and-forget. Idempotent — lần sau noop.
TourkitAiProxy.Services.Db.MultiTenantMigration.Run(
    Path.Combine(app.Environment.ContentRootPath, "data"),
    app.Services.GetRequiredService<ILogger<Program>>());

// Schema init ĐỒNG BỘ — TkSessionStore CTOR (load active sessions) + UsageRepository (track) cần bảng sẵn.
// SchemaSql idempotent (IF OBJECT_ID IS NULL) → ~100-500ms cold, ~ms hot. Block startup là CHẤP NHẬN ĐƯỢC
// (an toàn hơn race condition fire-and-forget). DB chết → log warning, app vẫn boot — repos fallback theo logic riêng.
try
{
    await app.Services.GetRequiredService<TourkitAiProxy.Services.Db.TourkitAiDb>().InitAsync();
}
catch (Exception ex)
{
    app.Services.GetRequiredService<ILogger<Program>>()
        .LogWarning(ex, "Schema init lỗi — TkSessions/AiUsageCounters/Reviews/... có thể chưa sẵn sàng");
}

// One-shot migrate tk-sessions.json → SQL (idempotent: file → .migrated sau khi xong).
// Chạy fire-and-forget được vì schema đã ready ở bước trên + TkSessionStore CTOR đã load xong.
_ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<TourkitAiProxy.Services.TourKit.TkSessionStore>();
        await store.MigrateFromLegacyFileAsync(
            Path.Combine(app.Environment.ContentRootPath, "data"));
    }
    catch (Exception ex)
    {
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogWarning(ex, "Migrate tk-sessions file → SQL fail");
    }
});

// Force-resolve AiUsageLog singleton ở startup → CTOR tự kick off migrate
// data/ai-usage.jsonl → dbo.AiUsageHistory (fire-and-forget bên trong). Không có dòng này,
// singleton chỉ instantiate khi có AI call đầu tiên → migration trễ.
_ = app.Services.GetRequiredService<TourkitAiProxy.Services.AiUsageLog>();

// DB init: tạo schema dbo.Reviews/DealScores/AiHistory nếu chưa có + migrate JSON cũ vào DB.
// Chạy async fire-and-forget — không block startup. Nếu DB chưa sẵn sàng → log warning, fallback file.
_ = Task.Run(async () =>
{
    using var scope = app.Services.CreateScope();
    var reviewRepo = scope.ServiceProvider.GetRequiredService<ReviewRepository>();
    var dealRepo   = scope.ServiceProvider.GetRequiredService<TourkitAiProxy.Services.Deals.DealRepository>();
    try { await reviewRepo.InitAsync(); }
    catch (Exception ex)
    {
        scope.ServiceProvider.GetRequiredService<ILogger<Program>>()
            .LogError(ex, "Review DB init/migrate fail — fallback file");
    }
    try { await dealRepo.InitAsync(); }
    catch (Exception ex)
    {
        scope.ServiceProvider.GetRequiredService<ILogger<Program>>()
            .LogError(ex, "Deal DB init/migrate fail — fallback file");
    }
});

// ─── HTTPS pipeline (phải ở SỚM nhất — trước CORS/routing) ──────────────────
// UseForwardedHeaders TRƯỚC mọi thứ khác → Request.Scheme/Request.Host correct ngay từ đầu.
// CRITICAL với reverse proxy: thiếu cái này → ctx.Request.IsHttps luôn false → redirect loop.
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();                    // Strict-Transport-Security header (chỉ prod)
    // Custom HTTPS redirect: SKIP localhost / 127.0.0.1 / ::1 dù env=Production.
    // Tránh case: anh chạy `dotnet publish` exe local → env=Production → mặc định UseHttpsRedirection
    // ép http://localhost → https://localhost → 0 listener HTTPS → "SSL connection could not be established".
    // Domain thật vẫn redirect HTTPS bình thường.
    app.Use(async (ctx, next) =>
    {
        var host = ctx.Request.Host.Host;
        var isLocal = host == "localhost" || host == "127.0.0.1" || host == "::1";
        if (!isLocal && !ctx.Request.IsHttps)
        {
            var httpsUrl = $"https://{ctx.Request.Host}{ctx.Request.Path}{ctx.Request.QueryString}";
            ctx.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            ctx.Response.Headers.Location = httpsUrl;
            return;
        }
        await next();
    });
}

app.UseCors(CorsSetup.PolicyName);
// Nén RESPONSE — phải TRƯỚC static files để bundle/CSS được Brotli/Gzip.
// Sau UseForwardedHeaders (đã set IsHttps đúng) → EnableForHttps hoạt động sau TLS proxy.
app.UseResponseCompression();
// Trace middleware ĐẦU pipeline (trước routing/endpoints) — bất kỳ endpoint nào cũng có thể đọc trace.
app.UseMiddleware<WorkflowTraceMiddleware>();
// Quota guard — bắt QuotaExhaustedException provider ném ra → 429 JSON.
app.UseMiddleware<TourkitAiProxy.Services.Quota.QuotaExceptionMiddleware>();
app.UseTourkitStaticFiles();

// ─── Routes ──────────────────────────────────────────────────────────────────
app.MapSystemEndpoints();
app.MapConsultLeadEndpoints();   // POST /api/v1/consult-leads (public, lưu data/consult-leads.jsonl)
app.MapNccImportEndpoints();     // /api/v1/ncc-import/* — bóc tách NCC từ file/text → Excel chuẩn
app.MapAiEndpoints();
app.MapReviewEndpoints();
app.MapChatEndpoints();
app.MapMailEndpoints();
app.MapTourEndpoints();
app.MapVisaEndpoints();
app.MapDealEndpoints();
app.MapTourQuoteEndpoints();
app.MapSpeechEndpoints();
app.MapTourBuilderEndpoints();
app.MapAiUsageEndpoints();
app.MapAdminAuthEndpoints();    // /api/v1/admin/auth/{login,logout,me}
app.MapAdminUiEndpoints();      // /api/v1/admin/ui/* (require X-Admin-Session)
app.MapQuotaEndpoints();
app.MapQuotaOrderEndpoints();
app.MapWidgetEndpoints();

// SPA fallback (deep-link /mail, /customers, /assistant + F5) ĐÃ CHUYỂN vào UseTourkitStaticFiles
// → app.MapFallback(ServeIndex): deep-link/F5 nay cũng nhận bundle-injection + ?v=hash thay vì rớt
// về DEV-babel mode. Trước đây dùng MapFallbackToFile("index.html") serve file THÔ (bỏ qua ServeIndex).

app.Run();
