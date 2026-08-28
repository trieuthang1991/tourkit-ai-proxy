using TourkitAiProxy.Configuration;
using TourkitAiProxy.Endpoints;
using TourkitAiProxy.Infrastructure;
using TourkitAiProxy.Services.Bootstrap;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Infrastructure.Reviews;
using TourkitAiProxy.Services.Reviews.Agents;
using TourkitAiProxy.Infrastructure.TourKit;
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
    o.SerializerOptions.Converters.Add(new TourkitAiProxy.Shared.Json.UtcDateTimeConverter()));

// ─── DB logging ĐỘNG (dbo.AppLogs) ────────────────────────────────────────────
// Site workflow sẽ tách riêng → log gom về DB để MỌI instance truy chung 1 nguồn (stdout không share được).
// Thiết kế động: cột Kind phân loại + DataJson payload tùy ý → thêm loại log mới khỏi đổi schema.
// ILogSink (đăng ký luôn, dù DB-log tắt) cho code ghi log CÓ CẤU TRÚC loại bất kỳ.
var dbLogQueue = new TourkitAiProxy.Infrastructure.Logging.DbLogQueue();
builder.Services.AddSingleton(dbLogQueue);
builder.Services.AddSingleton<TourkitAiProxy.Infrastructure.Logging.ILogSink,
                              TourkitAiProxy.Infrastructure.Logging.DbLogSink>();
if (builder.Configuration.GetValue("Logging:Database:Enabled", false))
{
    var dbLogMin = Enum.TryParse<LogLevel>(
        builder.Configuration["Logging:Database:MinLevel"], out var lv) ? lv : LogLevel.Information;
    builder.Logging.AddProvider(new TourkitAiProxy.Infrastructure.Logging.DbLoggerProvider(dbLogQueue, dbLogMin));
    builder.Services.AddHostedService<TourkitAiProxy.Infrastructure.Logging.DbLogWriter>();
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

// ─── Tính năng chỉ web dùng ───────────────────────────────────────────────────
// Mỗi tính năng tự khai đủ thứ nó cần trong Services/Bootstrap/WebFeatureRegistration.cs.
// Thêm tính năng mới KHÔNG phải sửa file này — trước đây phải, nên đây từng là chỗ hay đụng
// độ nhất khi gộp nhánh, và đọc xong vẫn không biết tính năng nào cần gì.
builder.Services.AddWebFeatures(builder.Configuration);

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
    await app.Services.GetRequiredService<TourkitAiProxy.Infrastructure.Db.TourkitAiDb>().InitAsync();
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
        var store = scope.ServiceProvider.GetRequiredService<TourkitAiProxy.Infrastructure.TourKit.TkSessionStore>();
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
_ = app.Services.GetRequiredService<TourkitAiProxy.Infrastructure.AiUsageLog>();

// DB init: tạo schema dbo.Reviews/DealScores/AiHistory nếu chưa có + migrate JSON cũ vào DB.
// Chạy async fire-and-forget — không block startup. Nếu DB chưa sẵn sàng → log warning, fallback file.
_ = Task.Run(async () =>
{
    using var scope = app.Services.CreateScope();
    var reviewRepo = scope.ServiceProvider.GetRequiredService<ReviewRepository>();
    var dealRepo   = scope.ServiceProvider.GetRequiredService<TourkitAiProxy.Infrastructure.Deals.DealRepository>();
    // CSDL chat là PostgreSQL RIÊNG (xem ChatDb). Thiếu chuỗi kết nối thì tự tắt, KHÔNG làm sập app.
    // Không gác sau cờ Features:Chat nữa: cờ đó nay chỉ ẩn mục menu, còn API và webhook vẫn sống
    // nên schema phải sẵn sàng — không thì tin đầu tiên tới là lỗi "bảng không tồn tại".
    await app.Services.GetRequiredService<TourkitAiProxy.Infrastructure.Chat.Inbox.ChatDb>().InitAsync();

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
    try { await scope.ServiceProvider.GetRequiredService<TourkitAiProxy.Infrastructure.Mail.MailTemplateRepository>().SeedDefaultsAsync(); }
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

// Kho ảnh/tệp CỤC BỘ của chat (chỉ có tác dụng khi Storage:Provider=local — R2/S3 phục vụ thẳng
// từ CDN của họ, không qua đây). Đường KHÁC hẳn wwwroot để không lẫn với file tĩnh của giao diện.
// Dùng CHUNG hàm dựng đường dẫn với nơi ghi (LocalChatFileStorage) — hai bên tự dựng riêng thì
// lệch nhau lúc nào không biết, mà triệu chứng chỉ là ảnh 404, không lỗi nào hiện lên.
var chatUploadsDir = TourkitAiProxy.Services.Storage.LocalChatFileStorage.ThuMucGoc(
    builder.Configuration["Storage:Local:Dir"], app.Environment.ContentRootPath);
Directory.CreateDirectory(chatUploadsDir);   // PhysicalFileProvider cần thư mục tồn tại từ lúc khởi động
app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(chatUploadsDir),
    RequestPath = "/chat-files",
});

// ─── Routes ──────────────────────────────────────────────────────────────────
// Danh sách đầy đủ + hai nhánh cờ tính năng ở TourkitAiProxy.Endpoints/EndpointRegistration.cs.
app.MapTourkitEndpoints(builder.Configuration);

// SPA fallback (deep-link /mail, /customers, /assistant + F5) ĐÃ CHUYỂN vào UseTourkitStaticFiles
// → app.MapFallback(ServeIndex): deep-link/F5 nay cũng nhận bundle-injection + ?v=hash thay vì rớt
// về DEV-babel mode. Trước đây dùng MapFallbackToFile("index.html") serve file THÔ (bỏ qua ServeIndex).

// BẢN TỰ KHAI của tiến trình này — in NGAY TRƯỚC khi chạy, khi mọi cấu hình đã chốt.
//
// Đây là thứ trả lời "con nào đang chạy worker" và "tệp đi đâu" mà không phải đoán. Hệ chạy
// nhiều tiến trình trên CÙNG một CSDL, còn appsettings.json thì gitignore và riêng từng máy —
// nên hai con chạy cùng một mã hoàn toàn có thể cư xử khác nhau, và trước dòng này thì không
// có chỗ nào nhìn ra được. Đã trả giá hai lần trong ngày 28/08/2026, xem InstanceInfo.
{
    var nhatKy = app.Services.GetRequiredService<ILogger<Program>>();
    var tuKhai = TourkitAiProxy.Services.Bootstrap.InstanceInfo.Doc(
        app.Configuration,
        app.Services.GetRequiredService<TourkitAiProxy.Services.Storage.IChatFileStorage>());
    nhatKy.LogInformation("{Dong}", TourkitAiProxy.Services.Bootstrap.InstanceInfo.MotDong(tuKhai));

    // Kho local KHÔNG hỏng, nhưng tệp nằm trên đĩa của chính máy chủ ứng dụng — mỗi lần deploy
    // là một lần có thể mất sạch, mà đường dẫn đã ghi vĩnh viễn vào CSDL. Nói to để không ai
    // chạy production ở chế độ này mà không biết.
    if (TourkitAiProxy.Services.Bootstrap.InstanceInfo.DangLo(tuKhai))
        nhatKy.LogWarning("[instance] kho tệp đang là LOCAL — tệp nằm trên đĩa máy chủ này và "
            + "có thể mất khi deploy. Khai Storage:Provider=r2 + Storage:R2:* nếu đây là máy thật.");
}

app.Run();
