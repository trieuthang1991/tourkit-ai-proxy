using TourkitAiProxy.Configuration;
using TourkitAiProxy.Endpoints;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Bootstrap;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Reviews;
using TourkitAiProxy.Services.Reviews.Agents;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Services.Workflow;

var builder = WebApplication.CreateBuilder(args);

// ─── log4net: sink chính cho ILogger<T> — mọi log của app + ASP.NET Core routing
// qua log4net → file rolling logs/app-YYYY-MM-DD.log + logs/error-*.log + stdout.
// Config sống tại log4net.config (copy vào output, hot reload khi sửa).
// Xem thêm cấu hình middleware trong section RequestLoggingMiddleware bên dưới.
builder.Logging.ClearProviders();
builder.Logging.AddLog4Net(new Microsoft.Extensions.Logging.Log4NetProviderOptions
{
    Log4NetConfigFileName = "log4net.config",
    Watch = true,   // hot reload khi sửa log4net.config, không cần restart
});

// ─── JSON: serialize MỌI DateTime kèm 'Z' (UTC) ───────────────────────────────
// DateTime từ SQL (Kind=Unspecified) mặc định serialize KHÔNG có 'Z' → trình duyệt hiểu nhầm giờ local
// → lệch +7h. App lưu UTC toàn bộ nên gắn 'Z' là đúng. Chỉ tác động field DateTime-typed (an toàn,
// các entity lưu local đọc dạng string không bị đụng). Xem UtcDateTimeConverter.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new TourkitAiProxy.Services.Json.UtcDateTimeConverter()));

// ─── DB logging ĐỘNG (dbo.AppLogs) ────────────────────────────────────────────
// Site workflow sẽ tách riêng → log gom về DB để MỌI instance truy chung 1 nguồn (stdout không share được).
// Thiết kế động: cột Kind phân loại + DataJson payload tùy ý → thêm loại log mới khỏi đổi schema.
// ILogSink (đăng ký luôn, dù DB-log tắt) cho code ghi log CÓ CẤU TRÚC loại bất kỳ.
var dbLogQueue = new TourkitAiProxy.Services.Logging.DbLogQueue();
builder.Services.AddSingleton(dbLogQueue);
builder.Services.AddSingleton<TourkitAiProxy.Services.Logging.ILogSink,
                              TourkitAiProxy.Services.Logging.DbLogSink>();
if (builder.Configuration.GetValue("Logging:Database:Enabled", false))
{
    var dbLogMin = Enum.TryParse<LogLevel>(
        builder.Configuration["Logging:Database:MinLevel"], out var lv) ? lv : LogLevel.Information;
    builder.Logging.AddProvider(new TourkitAiProxy.Services.Logging.DbLoggerProvider(dbLogQueue, dbLogMin));
    builder.Services.AddHostedService<TourkitAiProxy.Services.Logging.DbLogWriter>();
}

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

// Log thông tin TLS bypass để debug dễ hơn khi vào startup log.
Console.WriteLine($"[Startup] Providers:AllowInsecureTls = {builder.Configuration.GetValue<bool>("Providers:AllowInsecureTls")}");

// ─── Workflow stack (shared với TourkitAiProxy.Worker) ────────────────────────
// TẤT CẢ service cho scheduler + 3 workflow built-in gộp vào 1 extension method.
// Xem Services/Bootstrap/WorkflowStackRegistration.cs. Web + worker gọi CÙNG method
// này → 1 nguồn wiring, không drift khi thêm workflow mới.
// ITenantContext scoped đọc HttpContext (web-only — worker không dùng scope này, resolve
// tenantId qua parameter). AddHttpContextAccessor() đã nằm trong AddWorkflowStack.
builder.Services.AddScoped<TourkitAiProxy.Services.TourKit.ITenantContext,
                          TourkitAiProxy.Services.TourKit.HttpTenantContext>();
builder.Services.AddWorkflowStack(builder.Configuration);

// Admin governance — auth qua Admin:Users (JSON config) + in-mem session.
builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.AdminUserStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.AdminSessionStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.AdminUsageRepository>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.ConsultLeadRepository>();

// Tingee VietQR client cho luồng mua quota. Mock-first: dùng vietqr.io public, simulate-paid endpoint
// để dev test. Khi có ApiKey thật → set `Tingee:Mock=false` → switch sang TingeeHttpClient.
{
    var allowInsecureTingee = builder.Configuration.GetValue<bool>("Providers:AllowInsecureTls");
    var tingeeBuilder = builder.Services.AddHttpClient("tingee", c => c.Timeout = TimeSpan.FromSeconds(30));
    tingeeBuilder.AddHttpMessageHandler(sp =>
        new TourkitAiProxy.Services.Http.HttpLoggingHandler(
            sp.GetRequiredService<ILogger<TourkitAiProxy.Services.Http.HttpLoggingHandler>>(), "tingee"));
    if (allowInsecureTingee) tingeeBuilder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                     | System.Security.Authentication.SslProtocols.Tls13,
    });
}
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

// SmartMail — template mail global (dbo.MailTemplates) cho admin CRUD + seed mặc định.
// Các Mail/Workflow service khác (MailAccountStore, MailQueueRepository, các workflow…) đã nằm
// trong AddWorkflowStack ở trên. MailTemplateRepository là UI/admin-specific (không cần cho
// scheduler worker) nên đăng ký riêng ở đây.
builder.Services.AddSingleton<TourkitAiProxy.Services.Mail.MailTemplateRepository>();

// Hàng đợi hành động CRM (dbo.CrmActionQueue) — trợ lý enqueue (giao việc/tạo lịch hẹn),
// worker app-side (toutkit-app) drain. Singleton như MailQueueRepository (cùng chỉ dùng
// TourkitAiDb.OpenAsync mở connection mới mỗi lần gọi, không giữ state).
builder.Services.AddSingleton<TourkitAiProxy.Services.Crm.CrmActionQueueRepository>();

// Resolver tên→id (khách/nhân viên/deal/workflow) cho các action ghi — dùng ActionExecutor.
// Stateless (chỉ gọi TourKitApiClient qua jwt truyền vào), singleton như TourKitCustomerSource/DealOpportunityClient.
builder.Services.AddSingleton<TourkitAiProxy.Services.Chat.ActionResolver>();

// Bộ nhớ resolve theo phiên (in-mem): nhớ lựa chọn user đã chọn ở action-clarify để lượt sau (bổ sung
// thông tin qua chat) KHÔNG bắt chọn lại. Singleton (chia sẻ state giữa /chat và /action/resolve).
builder.Services.AddSingleton<TourkitAiProxy.Services.Chat.ActionResolutionMemory>();

// Thực thi hành động đã xác nhận (route theo ActionKind). Task 8a: nhánh CrmQueue
// (assign_task/create_appointment). Task 8b: nhánh Internal (review_customer/score_deal) — reuse
// NGUYÊN ReviewService/DealScoringService (đã singleton qua AddWorkflowStack ở trên). Mail vẫn throw
// NotImplementedException("8b") — task 8c. Singleton — mọi dependency đều singleton, không captive.
builder.Services.AddSingleton<TourkitAiProxy.Services.Chat.ActionExecutor>();

// Soạn Tour GIT bằng AI — bóc mô tả tự do thành form Tour GIT (Type=3) cho NV prefill.
builder.Services.AddSingleton<TourkitAiProxy.Services.Tour.TourBuilderService>();

// Báo giá tour persist (replace flow localStorage cũ). DB-backed, per-tenant scope.
builder.Services.AddSingleton<TourkitAiProxy.Services.TourQuotes.TourQuoteRepository>();

// Speech-to-Text (Whisper) — chat assistant ghi âm / upload audio → text.
builder.Services.AddSingleton<TourkitAiProxy.Services.Speech.SpeechToTextService>();
// Text-to-Speech — JARVIS đọc reply khi máy không có giọng vi miễn phí.
// Ưu tiên edge-tts (giọng vi neural CHUẨN, free, cần mạng) → Piper (offline) → OpenAI (nếu có key).
builder.Services.AddSingleton<TourkitAiProxy.Services.Speech.EdgeTtsService>();            // FREE, giọng vi chuẩn
builder.Services.AddSingleton<TourkitAiProxy.Services.Speech.PiperTtsService>();           // FREE offline (fallback)
builder.Services.AddSingleton<TourkitAiProxy.Services.Speech.TextToSpeechService>();        // OpenAI (fallback nếu có key)
// Vbee AIVoice — giọng Việt neural chất lượng cao (batch async). Ưu tiên đầu chuỗi TTS nếu cấu hình
// Speech:Vbee:AppId/Token. Named HttpClient "vbee" (auto-follow redirect audioLink mặc định).
builder.Services.AddHttpClient("vbee");
builder.Services.AddSingleton<TourkitAiProxy.Services.Speech.VbeeTtsService>();             // Vbee TTS (ưu tiên nếu có key)
builder.Services.AddSingleton<TourkitAiProxy.Services.Speech.VbeeSttService>();             // Vbee STT (primary khi SttEnabled; WAV-only + fallback)
// Google Cloud Text-to-Speech — giọng Việt neural (Wavenet/Neural2/Chirp3-HD). Auth = API key (REST đồng bộ).
// Khác Vbee: endpoint Google (GFE) backward-compat với Schannel cũ → thường gọi thẳng được từ WinServer 2012 R2.
builder.Services.AddHttpClient("google-tts");
builder.Services.AddSingleton<TourkitAiProxy.Services.Speech.GoogleTtsService>();           // Google TTS (ưu tiên nếu có key)

// Thẩm định Visa AI — upload hồ sơ → AI vision đọc → chấm tỉ lệ đậu/rớt.
// File gốc lưu tạm data/visa-files/ (tự xóa 7 ngày), kết quả data/visa-assessments.json.
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaFileStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaRepository>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaQuestionRepository>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaExtractionService>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Visa.VisaScoringService>();

// CHỈ instance có Workflows:RunScheduler=true mới CHẠY scheduler nền.
// Mặc định false — sau khi tách TourkitAiProxy.Worker, worker mới chạy scheduler;
// web deploy KHÔNG tự tick nền. Endpoint "Chạy ngay" (run-now) vẫn dùng được (Singleton).
var runScheduler = builder.Configuration.GetValue("Workflows:RunScheduler", false);
Console.WriteLine($"[Startup] Workflows:RunScheduler = {runScheduler} (mặc định false — worker riêng TourkitAiProxy.Worker sẽ chạy)");
if (runScheduler)
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<TourkitAiProxy.Services.Workflows.WorkflowSchedulerService>());

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

// ─── Global exception handler (IExceptionHandler .NET 8) ──────────────────────
// Bắt exception KHÔNG được endpoint handle → log ERROR có full stack + trả JSON 500 gọn.
// Wire vào pipeline qua UseExceptionHandler() (đặt sớm).
builder.Services.AddExceptionHandler<TourkitAiProxy.Services.Logging.GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

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

// Nạp NCC mẫu (__sample__) từ seed — CHỈ ở Development, idempotent (làm 1 lần khi __sample__ rỗng).
// Public/prod KHÔNG tự seed: dữ liệu mẫu nằm sẵn trong DB dùng chung (đưa lên 1 lần qua vận hành),
// không auto-chạy mỗi lần deploy. Không chặn startup nếu lỗi.
if (app.Environment.IsDevelopment())
{
    try { await app.Services.GetRequiredService<TourkitAiProxy.Services.TourPrices.SampleCatalogSeeder>().ReseedAsync(CancellationToken.None); }
    catch (Exception ex)
    {
        app.Services.GetRequiredService<ILogger<Program>>().LogWarning(ex, "[sample-seed] nạp NCC mẫu lỗi (bỏ qua)");
    }
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
    // Seed template mail mặc định (chỉ khi bảng rỗng) — chạy nền, KHÔNG block startup.
    try { await scope.ServiceProvider.GetRequiredService<TourkitAiProxy.Services.Mail.MailTemplateRepository>().SeedDefaultsAsync(); }
    catch (Exception ex)
    {
        scope.ServiceProvider.GetRequiredService<ILogger<Program>>()
            .LogWarning(ex, "Seed MailTemplates fail");
    }
});

// ─── Logging pipeline (SỚM NHẤT — trước mọi middleware để tag correlation id) ─
// CorrelationId → sinh/reuse X-Request-Id, push vào log4net LogicalThreadContext
// → mọi log trong request có %property{RequestId}. Grep 1 lần ra full request flow.
app.UseMiddleware<TourkitAiProxy.Services.Logging.CorrelationIdMiddleware>();
// RequestLogging → wrap toàn pipeline để lấy final status/duration. Resolve tenant
// từ session (nếu có) → tag vào log4net TenantId. Skip static asset.
app.UseMiddleware<TourkitAiProxy.Services.Logging.RequestLoggingMiddleware>();
// UseExceptionHandler → sink cuối cho unhandled exception. Đặt SAU 2 middleware trên
// để error log kèm RequestId + tenant. GlobalExceptionHandler (IExceptionHandler) đã DI.
app.UseExceptionHandler();

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
app.MapAssistantActionEndpoints();
app.MapMailEndpoints();
app.MapWorkflowEndpoints();
app.MapTourEndpoints();
app.MapTourPriceEndpoints();      // GET /api/v1/tour-price/candidates — ứng viên giá NCC (mẫu/thật/cả 2) cho wizard
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

// Admin shell — entry HTML riêng /admin-trav-ai.html, KHÔNG share index.html user-facing.
// MapGet explicit thắng MapFallback (StaticFilesSetup) → /admin-trav-ai/{**path} serve đúng file admin.
app.MapGet("/admin-trav-ai", (HttpContext ctx) => ServeAdminHtml(ctx, app.Environment.ContentRootPath));
app.MapGet("/admin-trav-ai/{**path}", (HttpContext ctx) => ServeAdminHtml(ctx, app.Environment.ContentRootPath));

static IResult ServeAdminHtml(HttpContext ctx, string contentRoot)
{
    var path = Path.Combine(contentRoot, "wwwroot", "admin-trav-ai.html");
    if (!File.Exists(path)) return Results.NotFound();
    ctx.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
    return Results.Content(File.ReadAllText(path), "text/html; charset=utf-8");
}

// SPA fallback (deep-link /mail, /customers, /assistant + F5) ĐÃ CHUYỂN vào UseTourkitStaticFiles
// → app.MapFallback(ServeIndex): deep-link/F5 nay cũng nhận bundle-injection + ?v=hash thay vì rớt
// về DEV-babel mode. Trước đây dùng MapFallbackToFile("index.html") serve file THÔ (bỏ qua ServeIndex).

app.Run();
