# Tách Scheduler workflow ra Worker Service riêng

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tách `WorkflowSchedulerService` (tick 60s chạy mail-auto-sync / deal-auto-review / customer-auto-review) ra process riêng — không share dập chung với web app — để ổn định hơn: web restart / crash / IIS AppPool recycle KHÔNG rớt automation; ngược lại worker fail cũng KHÔNG rớt UI.

**Architecture:** Tạo project `.NET 8 Worker Service` mới `TourkitAiProxy.Worker` bên cạnh `TourkitAiProxy.csproj`. Worker `ProjectReference` sang main để dùng chung TOÀN BỘ `Services/` (không copy code, không duplicate DI). Trích phần DI cần cho workflow ra 1 extension method static `AddWorkflowStack(this IServiceCollection, IConfiguration)` trong file mới `Services/Bootstrap/WorkflowStackRegistration.cs`. Web `Program.cs` gọi extension đó (thay 100+ dòng `AddSingleton`); Worker `Program.cs` cũng gọi cùng extension đó + thêm `AddHostedService<WorkflowSchedulerService>()`. Cấu hình:
- Web (`appsettings.json`): `"Workflows": { "RunScheduler": false }` → main app KHÔNG chạy scheduler nữa (endpoint "Chạy ngay" vẫn dùng được, chỉ mất tick tự động).
- Worker (`appsettings.json` riêng): `"Workflows": { "RunScheduler": true }` + share `Redis:ConnectionString` + `ConnectionStrings:PushDb` với web.

Không đụng frontend (per user directive về skill taste). Zero UI change. Endpoints `/api/v1/workflows/*` giữ nguyên trong web app — Worker KHÔNG expose HTTP.

**Tech Stack:** .NET 8 Worker Service (`Microsoft.NET.Sdk.Worker`), ProjectReference sang `TourkitAiProxy.csproj` (main). Không thêm package mới. Deploy: Windows Service (`sc create`) primary; systemd unit + Docker sidecar là secondary options (documented).

---

## Decisions locked (không cần confirm thêm khi execute)

| Câu hỏi | Chọn | Lý do |
|---|---|---|
| Deploy target | **Windows Service (`sc create`)** | Match environment thực tế; systemd + Docker documented cho tương lai |
| Health endpoint HTTP? | **KHÔNG** | Worker thuần background. Observability qua `dbo.AppLogs` (đã có `DbLogSink`) + Windows Event Log. Nếu cần health check sau → thêm `MapGet("/healthz")` với Kestrel trên port riêng |
| Workflow files location | **GIỮ trong main project** | Share qua ProjectReference; tránh move file phá git blame + import path |
| Solution structure | **Cả 2 project trong cùng `tourkit-ai-proxy.sln`** | 1 lệnh `dotnet build` cả 2; VS Studio open 1 lần |
| Config split | **Worker có `appsettings.json` RIÊNG** (copy từ web + đổi `RunScheduler=true` + tắt các phần không dùng) | An toàn: worker crash không cần đụng web config |

---

## File Structure

**New files:**
- `TourkitAiProxy.Worker/TourkitAiProxy.Worker.csproj` — Worker SDK, ProjectReference tới main
- `TourkitAiProxy.Worker/Program.cs` — Host bootstrap, gọi `AddWorkflowStack` + `AddHostedService`
- `TourkitAiProxy.Worker/appsettings.json` — config template (git tracked)
- `TourkitAiProxy.Worker/appsettings.example.json` — bản mẫu để user copy
- `TourkitAiProxy.Worker/README.md` — hướng dẫn deploy (Windows Service / systemd)
- `Services/Bootstrap/WorkflowStackRegistration.cs` — extension method `AddWorkflowStack` (shared DI, ~130 dòng trích từ Program.cs hiện tại)

**Modified files:**
- `Program.cs` — thay ~100 dòng `AddSingleton` bằng 1 dòng `builder.Services.AddWorkflowStack(builder.Configuration);`. Xoá comment "Tách site: web chính..." vì nay là mặc định. Set `Workflows:RunScheduler` mặc định = `false` (KHÔNG đăng ký `AddHostedService`).
- `appsettings.example.json` — `"Workflows": { "RunScheduler": false }` (đổi default để deploy web mới không tự chạy scheduler nếu quên tách)
- `tourkit-ai-proxy.sln` — thêm entry Worker project

**Not modified (verify unchanged):**
- Toàn bộ `wwwroot/**` — zero UI change
- Toàn bộ `Services/Workflows/*.cs` — logic scheduler + workflow giữ nguyên
- Toàn bộ endpoints — `/api/v1/workflows/*` vẫn ở web app

---

## Task 1: Extract shared DI extension

**Files:**
- Create: `Services/Bootstrap/WorkflowStackRegistration.cs`
- Modify: `Program.cs:118-295` (đoạn `AddSingleton` chồng chất)

Mục tiêu: gom TẤT CẢ `AddSingleton` mà workflow chain phụ thuộc vào 1 extension method, KHÔNG đụng logic nào. Copy nguyên xi từ `Program.cs`, xoá `builder.Services.` prefix.

**Danh sách service PHẢI có trong extension** (traced từ `WorkflowSchedulerService` → 3 workflows → transitives):

Nhóm | Services
---|---
JSON/Logging | `UtcDateTimeConverter` (không add — global http options, để web tự set), `DbLogQueue`, `ILogSink` → `DbLogSink`, `DbLoggerProvider` + `DbLogWriter` (hosted — có ở worker khi `Logging:Database:Enabled=true`)
HttpClient factory | `AddHttpClient("opencode"/"nine-routes"/"openai"/"anthropic"/"deepseek"/"tourkit")` + `AttachLogAndInsecure` handler wiring
Providers | `IAiProvider` × 6, `ProviderRegistry`, `ProviderKeyStore`, `AiModelRegistry`, `NativeToolScorer`, `AnthropicToolsClient`, `OpenCodeClient`
DB / Redis | `TourkitAiDb`, `RedisStore`, `RedisProvider`, `ChatCache`, `AiResponseCache`
Usage / Quota | `UsageRepository`, `UsageTracker`, `AiUsageHistoryRepository`, `AiUsageLog`, `AiCallContext`, `TenantQuotaRepository`, `TenantQuotaStore`, `QuotaFlushService` (hosted)
Trace | `IWorkflowTraceAccessor` → `WorkflowTraceAccessor`, `WorkflowTraceLog`
TourKit | `TourKitApiClient`, `TkSessionRepository`, `TkSessionStore`, `TenantServiceAccountStore`, `TourKitCustomerSource`, `CustomerReviewClient`
Review | `CustomerRepository`, `ReviewRepository`, `BatchJobStore`, `IReviewAgent` × 2, `ReviewService`, `BatchService`, `NccImportService`
Mail | `MailAccountStore`, `MailSyncStore`, `MailRepository`, `IMailSource` → `GmailImapClient`, `IMailSender` → `GmailSmtpClient`, `MailClassifier`, `MailSyncService`, `MailReplyService`, `MailQueueRepository`
Deal | `DealOpportunityClient`, `DealScoringService`, `DealRepository`, `DealBatchJobStore`, `DealBatchService`
Store | `TenantStore`, `TourKitNccClient`
Workflow | `WorkflowRepository`, `WorkflowRegistry`, 3× `IScheduledWorkflow` (Mail, Deal, Customer), `WorkflowSchedulerService` (Singleton, KHÔNG `AddHostedService` — caller quyết định)

**Danh sách service KHÔNG add trong extension** (chỉ web cần):
- `AddTourkitCors`, `ConfigureKestrel`, `Configure<FormOptions>`, `Configure<ForwardedHeadersOptions>`, `AddHsts`, `AddResponseCompression`
- `AddHttpContextAccessor`, `AddScoped<ITenantContext, HttpTenantContext>` (worker không có HttpContext)
- Admin: `AdminUserStore`, `AdminSessionStore`, `AdminUsageRepository`, `ConsultLeadRepository`
- Tingee: `TingeeHttpClient`/`MockTingeeClient`, `QuotaOrderRepository`
- Widget: `WidgetTokenRepository`, `WidgetChatService`, `WidgetCrmLinkService`, `WidgetChatCrmService`, `IAgentRuntime` × 2, `UnresolvedQuestionsLog`, `ChatAgentService`
- Tour builder / Visa / TourQuote / Speech: `TourBuilderService`, `VisaFileStore`, `VisaRepository`, `VisaQuestionRepository`, `VisaExtractionService`, `VisaScoringService`, `TourQuoteRepository`, `SpeechToTextService`

**Note:** `ITenantContext` scoped tới `HttpTenantContext` KHÔNG đưa vào extension. Workflow không dùng scope này (nhận `tenantId` qua parameter). Nếu resolve thiếu, add sau khi thấy exception cụ thể.

- [ ] **Step 1: Tạo file `Services/Bootstrap/WorkflowStackRegistration.cs`**

```csharp
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Reviews;
using TourkitAiProxy.Services.Reviews.Agents;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Services.Bootstrap;

/// <summary>
/// Đăng ký TẤT CẢ service cần cho <see cref="Workflows.WorkflowSchedulerService"/> +
/// 3 workflow built-in (mail-auto-sync / deal-auto-review / customer-auto-review) +
/// transitives (Providers, DB, Redis, Quota, TourKit, Mail, Deal, Review, Trace).
///
/// Dùng chung web (`TourkitAiProxy` main) và worker (`TourkitAiProxy.Worker`) →
/// 1 nguồn wiring. KHÔNG add UI-specific service (Kestrel/CORS/Widget/Admin/HttpContext).
///
/// Caller quyết định có `AddHostedService(sp => sp.GetRequiredService&lt;WorkflowSchedulerService&gt;())`
/// hay không (thường: worker true, web false).
/// </summary>
public static class WorkflowStackRegistration
{
    public static IServiceCollection AddWorkflowStack(this IServiceCollection s, IConfiguration cfg)
    {
        // ─── HttpClient factory với logging + insecure TLS bypass ────────────
        var allowInsecure = cfg.GetValue<bool>("Providers:AllowInsecureTls");
        HttpMessageHandler MakeInsecureHandler() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                         | System.Security.Authentication.SslProtocols.Tls13,
        };
        void AttachLogAndInsecure(IHttpClientBuilder cb, string name, bool insecure)
        {
            cb.AddHttpMessageHandler(sp =>
                new TourkitAiProxy.Services.Http.HttpLoggingHandler(
                    sp.GetRequiredService<ILogger<TourkitAiProxy.Services.Http.HttpLoggingHandler>>(), name));
            if (insecure) cb.ConfigurePrimaryHttpMessageHandler(MakeInsecureHandler);
        }

        AttachLogAndInsecure(
            s.AddHttpClient("opencode", c =>
            {
                c.BaseAddress = new Uri("https://opencode.ai/");
                c.Timeout     = TimeSpan.FromSeconds(120);
            }), "opencode", allowInsecure);
        AttachLogAndInsecure(
            s.AddHttpClient("nine-routes", c => c.Timeout = TimeSpan.FromSeconds(120)),
            "nine-routes",
            allowInsecure || cfg.GetValue<bool>("Providers:NineRoutes:AllowInsecureTls"));
        AttachLogAndInsecure(s.AddHttpClient("openai",    c => c.Timeout = TimeSpan.FromSeconds(120)), "openai", allowInsecure);
        AttachLogAndInsecure(s.AddHttpClient("anthropic", c => c.Timeout = TimeSpan.FromSeconds(120)), "anthropic", allowInsecure);
        AttachLogAndInsecure(s.AddHttpClient("deepseek",  c => c.Timeout = TimeSpan.FromSeconds(120)), "deepseek", allowInsecure);
        AttachLogAndInsecure(
            s.AddHttpClient("tourkit", c =>
            {
                var baseUrl = cfg["TourKit:BaseUrl"] ?? "https://mobile-test-api-2.tourkit.vn";
                c.BaseAddress = new Uri(baseUrl);
                c.Timeout     = TimeSpan.FromSeconds(60);
            }), "tourkit",
            allowInsecure || cfg.GetValue<bool>("TourKit:AllowInsecureTls"));

        // ─── Usage / Quota ────────────────────────────────────────────────────
        s.AddSingleton<UsageRepository>();
        s.AddSingleton<UsageTracker>();
        s.AddSingleton<AiUsageHistoryRepository>();
        s.AddSingleton<AiUsageLog>();
        s.AddSingleton<AiCallContext>();
        s.AddSingleton<TourkitAiProxy.Services.Cache.AiResponseCache>();
        s.AddSingleton<TourkitAiProxy.Services.Quota.TenantQuotaRepository>();
        s.AddSingleton<TourkitAiProxy.Services.Quota.TenantQuotaStore>();
        s.AddHostedService<TourkitAiProxy.Services.Quota.QuotaFlushService>();

        // ─── Provider stack ──────────────────────────────────────────────────
        s.AddSingleton<ProviderKeyStore>();
        s.AddSingleton<IAiProvider, OpenCodeProvider>();
        s.AddSingleton<IAiProvider, NineRoutesProvider>();
        s.AddSingleton<IAiProvider, OpenAIProvider>();
        s.AddSingleton<IAiProvider, AnthropicProvider>();
        s.AddSingleton<IAiProvider, DeepSeekProvider>();
        s.AddSingleton<IAiProvider, GrokProvider>();
        s.AddSingleton<ProviderRegistry>();
        s.AddSingleton<AiModelRegistry>();
        s.AddScoped<OpenCodeClient>();
        s.AddSingleton<AnthropicToolsClient>();
        s.AddSingleton<NativeToolScorer>();

        // ─── Trace ────────────────────────────────────────────────────────────
        s.AddSingleton<IWorkflowTraceAccessor, WorkflowTraceAccessor>();
        s.AddSingleton<WorkflowTraceLog>();

        // ─── DB + Redis ──────────────────────────────────────────────────────
        s.AddSingleton<TourkitAiProxy.Services.Db.TourkitAiDb>();
        s.AddSingleton<TourkitAiProxy.Services.Cache.RedisStore>();
        s.AddSingleton<TourkitAiProxy.Services.Cache.RedisProvider>();
        s.AddSingleton<TourkitAiProxy.Services.Cache.ChatCache>();

        // ─── TourKit (session + service account) ─────────────────────────────
        s.AddSingleton<TourKitApiClient>();
        s.AddSingleton<TkSessionRepository>();
        s.AddSingleton<TkSessionStore>();
        s.AddSingleton<TenantServiceAccountStore>();
        s.AddSingleton<TourKitCustomerSource>();
        s.AddSingleton<TourKitNccClient>();

        // ─── Review (customer + KH ingest) ───────────────────────────────────
        s.AddSingleton<CustomerRepository>();
        s.AddSingleton<ReviewRepository>();
        s.AddSingleton<BatchJobStore>();
        s.AddSingleton<IReviewAgent, NativeToolReviewAgent>();
        s.AddSingleton<IReviewAgent, JsonPromptReviewAgent>();
        s.AddSingleton<TourkitAiProxy.Services.NccImport.NccImportService>();
        s.AddSingleton<ReviewService>();
        s.AddSingleton<BatchService>();
        s.AddSingleton<CustomerReviewClient>();

        // ─── Mail (auto-sync + reply + queue) ────────────────────────────────
        s.AddSingleton<TourkitAiProxy.Services.Mail.MailAccountStore>();
        s.AddSingleton<TourkitAiProxy.Services.Mail.MailSyncStore>();
        s.AddSingleton<TourkitAiProxy.Services.Mail.MailRepository>();
        s.AddSingleton<TourkitAiProxy.Services.Mail.IMailSource, TourkitAiProxy.Services.Mail.GmailImapClient>();
        s.AddSingleton<TourkitAiProxy.Services.Mail.IMailSender, TourkitAiProxy.Services.Mail.GmailSmtpClient>();
        s.AddSingleton<TourkitAiProxy.Services.Mail.MailClassifier>();
        s.AddSingleton<TourkitAiProxy.Services.Mail.MailSyncService>();
        s.AddSingleton<TourkitAiProxy.Services.Mail.MailReplyService>();
        s.AddSingleton<TourkitAiProxy.Services.Mail.MailQueueRepository>();

        // ─── Deal (score + cảnh báo nguội) ───────────────────────────────────
        s.AddSingleton<TourkitAiProxy.Services.Deals.DealOpportunityClient>();
        s.AddSingleton<TourkitAiProxy.Services.Deals.DealScoringService>();
        s.AddSingleton<TourkitAiProxy.Services.Deals.DealRepository>();
        s.AddSingleton<TourkitAiProxy.Services.Deals.DealBatchJobStore>();
        s.AddSingleton<TourkitAiProxy.Services.Deals.DealBatchService>();

        // ─── Store bền vững ──────────────────────────────────────────────────
        s.AddSingleton<TourkitAiProxy.Services.Store.TenantStore>();

        // ─── Workflow scheduler + built-in workflows ─────────────────────────
        s.AddSingleton<Workflows.WorkflowRepository>();
        s.AddSingleton<Workflows.WorkflowRegistry>();
        s.AddSingleton<Workflows.IScheduledWorkflow, Workflows.MailAutoSyncWorkflow>();
        s.AddSingleton<Workflows.IScheduledWorkflow, Workflows.DealAutoReviewWorkflow>();
        s.AddSingleton<Workflows.IScheduledWorkflow, Workflows.CustomerAutoReviewWorkflow>();
        s.AddSingleton<Workflows.WorkflowSchedulerService>();

        return s;
    }
}
```

- [ ] **Step 2: Sửa `Program.cs` — thay các dòng `AddSingleton` bằng lời gọi extension**

Trong `Program.cs`, xoá TỪ dòng `builder.Services.AddSingleton<UsageRepository>();` (~line 118) đến hết block workflow (~line 264), thay bằng:

```csharp
// ─── Workflow stack (shared với TourkitAiProxy.Worker) ────────────────────────
// TẤT CẢ service cho scheduler + 3 workflow built-in gộp vào 1 extension method.
// Xem Services/Bootstrap/WorkflowStackRegistration.cs.
builder.Services.AddHttpContextAccessor();      // web-only: ITenantContext scoped đọc HttpContext
builder.Services.AddScoped<TourkitAiProxy.Services.TourKit.ITenantContext,
                          TourkitAiProxy.Services.TourKit.HttpTenantContext>();
builder.Services.AddWorkflowStack(builder.Configuration);

// Admin governance — CHỈ web (worker không có UI admin).
builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.AdminUserStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.AdminSessionStore>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.AdminUsageRepository>();
builder.Services.AddSingleton<TourkitAiProxy.Services.Admin.ConsultLeadRepository>();

// Tingee, Widget, Chat, Tour builder, Visa, TourQuote, Speech — CHỈ web.
// (giữ nguyên block hiện có từ line ~209 trở đi)
```

**Cẩn thận:** những service KHÔNG có trong `AddWorkflowStack` (Tingee, Widget, ChatAgentService, Tour, Visa, TourQuote, Speech, Admin) — GIỮ NGUYÊN block DI cho chúng.

- [ ] **Step 3: Đổi default `Workflows:RunScheduler` trong web = false**

`Program.cs` khoảng line 262:

```csharp
// CHỈ instance có Workflows:RunScheduler=true mới CHẠY scheduler nền.
// Default đổi false vì có site worker riêng (TourkitAiProxy.Worker) rồi.
// Web deploy chỉ nên set true khi CHƯA có worker (chưa split).
if (builder.Configuration.GetValue("Workflows:RunScheduler", false))   // DEFAULT ĐỔI false
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<TourkitAiProxy.Services.Workflows.WorkflowSchedulerService>());
```

- [ ] **Step 4: Build web project để verify KHÔNG vỡ**

Run: `dotnet build TourkitAiProxy.csproj -c Debug`
Expected: `Build succeeded. 0 Warning(s), 0 Error(s).`

Nếu lỗi CS0246 (namespace không thấy): mở `WorkflowStackRegistration.cs` bổ sung `using`. Nếu lỗi CS1061 (method không tồn tại): so lại tên service với `Program.cs` gốc (git diff).

- [ ] **Step 5: Smoke test web local**

Run: `dotnet run --project TourkitAiProxy.csproj`
Expected:
- Startup log KHÔNG có `[Scheduler] Khởi động` (vì `RunScheduler=false` default)
- `GET http://localhost:5080/healthz` → 200
- `GET http://localhost:5080/api/v1/workflows` (kèm `X-Session-Id`) → trả list workflow như cũ
- "Chạy ngay" 1 workflow qua UI `/workflows` → chạy được (endpoint dùng Singleton `WorkflowSchedulerService` đã đăng ký)

Nếu missing service: exception `Unable to resolve service for type` → xem type name → bổ sung vào `AddWorkflowStack`.

- [ ] **Step 6: Commit Task 1**

```bash
git add Services/Bootstrap/WorkflowStackRegistration.cs Program.cs
git commit -m "refactor(workflow): trich DI stack workflow ra extension method (chuan bi tach Worker)"
```

---

## Task 2: Tạo project TourkitAiProxy.Worker

**Files:**
- Create: `TourkitAiProxy.Worker/TourkitAiProxy.Worker.csproj`
- Create: `TourkitAiProxy.Worker/Program.cs`
- Create: `TourkitAiProxy.Worker/appsettings.json`
- Create: `TourkitAiProxy.Worker/appsettings.example.json`
- Create: `TourkitAiProxy.Worker/Properties/launchSettings.json`
- Modify: `tourkit-ai-proxy.sln`

- [ ] **Step 1: Tạo folder + csproj Worker**

Verify folder chưa tồn tại: `ls TourkitAiProxy.Worker 2>/dev/null || echo "OK-CREATE"`. Nếu OK-CREATE, tiếp tục.

Tạo `TourkitAiProxy.Worker/TourkitAiProxy.Worker.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>TourkitAiProxy.Worker</RootNamespace>
    <UserSecretsId>tourkit-ai-proxy-worker</UserSecretsId>
    <!-- KHÔNG serve static → khỏi copy wwwroot. -->
    <StaticWebAssetsEnabled>false</StaticWebAssetsEnabled>
  </PropertyGroup>

  <ItemGroup>
    <!-- Share TOÀN BỘ Services/ + Models/ + Endpoints/ (không dùng) qua ProjectReference.
         Vì main dùng Sdk.Web nên assembly Worker sẽ mang theo AspNetCore.Http, Kestrel...
         Đó là footprint kỹ thuật — chấp nhận, worker vẫn chỉ CHẠY BackgroundService. -->
    <ProjectReference Include="..\TourkitAiProxy.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="8.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Tạo `TourkitAiProxy.Worker/Program.cs`**

```csharp
using TourkitAiProxy.Services.Bootstrap;
using TourkitAiProxy.Services.Workflows;

// Worker Service host: KHÔNG Kestrel, KHÔNG CORS, KHÔNG endpoint HTTP.
// Chỉ chạy WorkflowSchedulerService (tick 60s) + hỗ trợ deploy Windows Service / systemd.
var builder = Host.CreateApplicationBuilder(args);

// Windows Service integration — nếu tiến trình khởi động qua sc.exe, tự nối vào SCM.
// No-op ở dev (chạy console).
builder.Services.AddWindowsService(o => o.ServiceName = "TourkitAiProxyWorker");

// JSON UTC converter — giữ convention với web (mọi DateTime kèm 'Z').
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new TourkitAiProxy.Services.Json.UtcDateTimeConverter()));

// DB logging động (khuyến nghị BẬT cho worker → log gom về dbo.AppLogs, xem chung với web).
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

// TLS ép TLS 1.2/1.3 (giống web) — Windows Server 2012 R2/2016 default TLS 1.0.
System.Net.ServicePointManager.SecurityProtocol =
    System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

// Share DI stack với web.
builder.Services.AddWorkflowStack(builder.Configuration);

// Đăng ký scheduler làm hosted service — worker LUÔN chạy scheduler.
// Bỏ qua config RunScheduler ở worker (web có config này).
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkflowSchedulerService>());

var host = builder.Build();

// Schema init đồng bộ (giống web) — tránh race với TkSessionStore CTOR.
try
{
    await host.Services.GetRequiredService<TourkitAiProxy.Services.Db.TourkitAiDb>().InitAsync();
}
catch (Exception ex)
{
    host.Services.GetRequiredService<ILogger<Program>>()
        .LogWarning(ex, "[Worker] Schema init lỗi");
}

// Force-resolve AiUsageLog (giống web) → CTOR tự kick off migrate jsonl→SQL.
_ = host.Services.GetRequiredService<TourkitAiProxy.Services.AiUsageLog>();

host.Services.GetRequiredService<ILogger<Program>>()
    .LogInformation("[Worker] TourkitAiProxy.Worker khởi động — tick {Sec}s",
        builder.Configuration.GetValue("Workflows:TickSeconds", 60));

await host.RunAsync();
```

- [ ] **Step 3: Tạo `TourkitAiProxy.Worker/appsettings.example.json`**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    },
    "Database": {
      "_comment": "BẬT để log worker gom về dbo.AppLogs — xem chung với web trong 1 nguồn (stdout worker không share được).",
      "Enabled": true,
      "MinLevel": "Information"
    }
  },
  "ConnectionStrings": {
    "_comment": "PHẢI trùng với web (ENC: cũng được). Worker + web share dbo.UserWorkflows, dbo.WorkflowRuns, dbo.TkSessions, dbo.TenantServiceAccounts...",
    "PushDb": "REPLACE_WITH_PUSH_DB_CONNECTION"
  },
  "Redis": {
    "_comment": "PHẢI trùng với web — quota + cache cross-process. Prefix key riêng (tkai:) đã handle.",
    "ConnectionString": ""
  },
  "Workflows": {
    "RunScheduler": true,
    "TickSeconds": 60
  },
  "Providers": {
    "OpenCode":   { "ApiKey": "REPLACE_WITH_OPENCODE_KEY" },
    "NineRoutes": { "BaseUrl": "http://localhost:20128/v1", "ApiKey": "REPLACE_WITH_9ROUTES_KEY", "AllowInsecureTls": false },
    "OpenAI":     { "ApiKey": "" },
    "Anthropic":  { "ApiKey": "" },
    "DeepSeek":   { "ApiKey": "" },
    "Grok":       { "ApiKey": "REPLACE_WITH_GROK_KEY" }
  },
  "Models": {
    "Primary":         { "Provider": "anthropic", "Model": "claude-haiku-4-5", "ApiKey": "REPLACE_WITH_ANTHROPIC_KEY" },
    "MailClassify":    { "Provider": "grok", "Model": "grok-4.3", "ApiKey": null },
    "MailDraft":       { "Provider": "grok", "Model": "grok-4.3", "ApiKey": null },
    "DealScoring":     { "Provider": "grok", "Model": "grok-4.3", "ApiKey": null },
    "CustomerReview":  { "Provider": "grok", "Model": "grok-4.3", "ApiKey": null }
  },
  "TourKit": {
    "BaseUrl": "https://mobile-test-api-2.tourkit.vn"
  }
}
```

- [ ] **Step 4: Tạo `TourkitAiProxy.Worker/appsettings.json`**

Copy từ `appsettings.example.json` và điền key thật (chính là copy `appsettings.json` của web + bỏ block Speech/Tingee/Admin/Widget không dùng). File này SẼ gitignore.

Verify gitignore đã cover: `git check-ignore TourkitAiProxy.Worker/appsettings.json`. Nếu chưa (chưa có rule wild-card cho appsettings.json), thêm vào `.gitignore`:

```
TourkitAiProxy.Worker/appsettings.json
TourkitAiProxy.Worker/appsettings.Development.json
```

Verify sau khi thêm: `git check-ignore -v TourkitAiProxy.Worker/appsettings.json` → in ra rule.

- [ ] **Step 5: Tạo `TourkitAiProxy.Worker/Properties/launchSettings.json`**

```json
{
  "profiles": {
    "TourkitAiProxy.Worker": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "environmentVariables": {
        "DOTNET_ENVIRONMENT": "Development"
      }
    }
  }
}
```

- [ ] **Step 6: Add project vào solution**

Run: `dotnet sln tourkit-ai-proxy.sln add TourkitAiProxy.Worker/TourkitAiProxy.Worker.csproj`
Expected: `Project ... added to the solution.`

Verify: `cat tourkit-ai-proxy.sln | grep TourkitAiProxy.Worker` → thấy dòng `Project(...) = "TourkitAiProxy.Worker"`.

- [ ] **Step 7: Build cả solution**

Run: `dotnet build tourkit-ai-proxy.sln -c Debug`
Expected: `Build succeeded. 0 Warning(s), 0 Error(s).` cho CẢ 2 project.

- [ ] **Step 8: Commit Task 2**

```bash
git add TourkitAiProxy.Worker/ tourkit-ai-proxy.sln .gitignore
git commit -m "feat(worker): them project TourkitAiProxy.Worker — chay scheduler workflow doc lap"
```

---

## Task 3: Smoke test Worker local

- [ ] **Step 1: Chạy Worker + verify scheduler tick**

Terminal 1: `dotnet run --project TourkitAiProxy.Worker/TourkitAiProxy.Worker.csproj`

Expected log trong 90s:
```
[Worker] TourkitAiProxy.Worker khởi động — tick 60s
info: TourkitAiProxy.Services.Workflows.WorkflowSchedulerService[0]
      [Scheduler] Khởi động — tick mỗi 60s
info: TourkitAiProxy.Services.Workflows.WorkflowSchedulerService[0]
      [Scheduler] tick — {N} workflow due
```

Nếu process exit ngay: check `appsettings.json` (ConnectionString PushDb đúng?). Nếu `[Scheduler] tick — 0 workflow due` — bình thường khi chưa tenant nào bật workflow.

- [ ] **Step 2: Chạy web song song + verify KHÔNG double-fire**

Terminal 2: `dotnet run --project TourkitAiProxy.csproj`

Web startup KHÔNG log `[Scheduler] Khởi động` (vì `RunScheduler=false` default). Nếu vẫn thấy → check `appsettings.json` (nếu vẫn giữ `"RunScheduler": true` cũ, xóa hoặc set false).

- [ ] **Step 3: Trigger 1 workflow qua Worker → verify chạy được**

Trên UI `/workflows` (web port 5080), bật 1 workflow (vd mail-auto-sync) với `intervalMinutes=1` → chờ 60s → check log worker:

Expected:
```
[Scheduler] tick — 1 workflow due
[Workflow] mail-auto-sync tenant=... user=... trigger=scheduled ok=true dur=...ms
```

Check `dbo.WorkflowRuns` (SSMS):
```sql
SELECT TOP 5 * FROM dbo.WorkflowRuns ORDER BY StartedUtc DESC;
```
→ TriggerKind = `scheduled`, Status = `ok`. Verify UI `/workflows` "20 lần gần nhất" thấy record vừa chạy.

- [ ] **Step 4: Kill worker → verify web endpoints VẪN sống + "Chạy ngay" VẪN chạy**

Ctrl+C terminal 1 (worker). Trên UI `/workflows` bấm "Chạy ngay" trên 1 workflow → phải chạy được (endpoint dùng Singleton `WorkflowSchedulerService.RunOneAsync` — không phụ thuộc hosted).

Expected log web: `[Workflow] ... trigger=manual ok=true`.

Nếu error `Unable to resolve` — verify web `Program.cs` gọi `AddWorkflowStack` (Task 1 Step 2). Nếu error timeout/kết nối — root cause debugging (không phải kiến trúc).

- [ ] **Step 5: Commit smoke test result (chỉ commit nếu có sửa nhỏ)**

Không có file mới → skip commit. Nếu có sửa (vd `.gitignore` bổ sung, config) → commit riêng: `git commit -m "chore(worker): smoke test fixes"`.

---

## Task 4: Deploy documentation

**Files:**
- Create: `TourkitAiProxy.Worker/README.md`

- [ ] **Step 1: Viết README deploy**

```markdown
# TourkitAiProxy.Worker

Chạy scheduler workflow (`WorkflowSchedulerService`) độc lập với web `TourkitAiProxy`.
Web restart / crash → automation KHÔNG rớt; ngược lại worker fail → UI vẫn sống.

## Cấu hình

1. Copy `appsettings.example.json` → `appsettings.json`
2. Điền:
   - `ConnectionStrings:PushDb` — PHẢI trùng web (share `dbo.UserWorkflows`, `dbo.WorkflowRuns`, `dbo.TkSessions`, `dbo.TenantServiceAccounts`, `dbo.MailAccounts`...)
   - `Redis:ConnectionString` — PHẢI trùng web (quota + cache cross-process)
   - `Providers:*:ApiKey` + `Models:*:ApiKey` — giống web
   - `TourKit:BaseUrl` — giống web (upstream API mà `deal-auto-review` / `customer-auto-review` gọi)

3. Web `TourkitAiProxy` phải set `Workflows:RunScheduler = false` để KHÔNG double-fire.

## Chạy dev (console)

```bash
dotnet run --project TourkitAiProxy.Worker/TourkitAiProxy.Worker.csproj
```

Log trong 60s đầu: `[Scheduler] Khởi động — tick mỗi 60s`.

## Deploy — Windows Service (chính)

```powershell
# 1) Publish tự-chứa runtime
dotnet publish TourkitAiProxy.Worker/TourkitAiProxy.Worker.csproj -c Release -r win-x64 --self-contained true -o C:\Services\TourkitAiWorker

# 2) Copy appsettings.json vào cùng thư mục publish (KHÔNG commit vào git)
Copy-Item TourkitAiProxy.Worker/appsettings.json C:\Services\TourkitAiWorker\

# 3) Đăng ký Windows Service (chạy dưới NetworkService để có quyền truy cập DB)
sc.exe create TourkitAiProxyWorker `
    binPath= "C:\Services\TourkitAiWorker\TourkitAiProxy.Worker.exe" `
    start= auto `
    obj= "NT AUTHORITY\NetworkService" `
    DisplayName= "Tourkit AI Proxy Worker"

# 4) Set restart policy — crash → SCM tự restart sau 60s (không quá 3 lần trong 24h)
sc.exe failure TourkitAiProxyWorker reset= 86400 actions= restart/60000/restart/60000/restart/60000

# 5) Start
sc.exe start TourkitAiProxyWorker
```

Xem log: **Event Viewer** → Windows Logs → Application → source `TourkitAiProxyWorker`.
Log chi tiết: `dbo.AppLogs` (nếu bật `Logging:Database:Enabled=true`).

Uninstall:
```powershell
sc.exe stop TourkitAiProxyWorker
sc.exe delete TourkitAiProxyWorker
```

## Deploy — systemd (Linux, tương lai)

`/etc/systemd/system/tourkit-ai-worker.service`:

```ini
[Unit]
Description=Tourkit AI Proxy Worker
After=network.target

[Service]
Type=notify
WorkingDirectory=/opt/tourkit-ai-worker
ExecStart=/usr/bin/dotnet /opt/tourkit-ai-worker/TourkitAiProxy.Worker.dll
Restart=always
RestartSec=60
User=tourkit
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

`sudo systemctl enable --now tourkit-ai-worker.service`.

## Deploy — Docker (tương lai)

Dockerfile riêng cho worker (không cần Node/esbuild vì không có wwwroot):

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish TourkitAiProxy.Worker/TourkitAiProxy.Worker.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "TourkitAiProxy.Worker.dll"]
```

Compose:
```yaml
services:
  tourkit-web:
    image: tourkit-ai-proxy
    environment:
      Workflows__RunScheduler: "false"
  tourkit-worker:
    build: { context: ., dockerfile: Dockerfile.worker }
    environment:
      Workflows__RunScheduler: "true"
      ConnectionStrings__PushDb: "..."
      Redis__ConnectionString: "..."
```

## Health check

Không có HTTP endpoint. Verify liveness:

1. **Event Viewer** (Windows) hoặc `journalctl -u tourkit-ai-worker` (Linux) — thấy log `[Scheduler] tick` mỗi 60s.
2. **SQL** — `SELECT MAX(StartedUtc) FROM dbo.WorkflowRuns` — timestamp update mỗi lần có workflow due.
3. **UI web** — trang `/workflows` "20 lần gần nhất" cho từng workflow.

## Troubleshooting

| Triệu chứng | Kiểm tra |
|---|---|
| Service không start | `sc.exe query TourkitAiProxyWorker`; log Event Viewer → tìm exception |
| Log `[Scheduler] tick — 0 workflow due` mãi | Có workflow nào `Enabled=1 AND PausedReason IS NULL` không? `SELECT * FROM dbo.UserWorkflows` |
| deal/customer báo "không kết nối được" | `TourKit:BaseUrl` reachable từ server worker chưa? `curl https://mobile-test-api-2.tourkit.vn/healthz` |
| Web + worker double-fire cùng 1 workflow | Web `Workflows:RunScheduler` phải = `false`. Check `appsettings.json` web |
| Log DB trống dù bật | `Logging:Database:Enabled=true` VÀ schema `dbo.AppLogs` đã tồn tại chưa (`SELECT * FROM sys.tables WHERE name='AppLogs'`) |
```

- [ ] **Step 2: Commit README**

```bash
git add TourkitAiProxy.Worker/README.md
git commit -m "docs(worker): huong dan deploy Windows Service + systemd + Docker"
```

---

## Task 5: Cập nhật CLAUDE.md + appsettings.example.json

- [ ] **Step 1: Cập nhật `CLAUDE.md` — thêm section Worker**

Chèn NGAY SAU section `## User Workflows` (khoảng line "MailSyncService (extract)..."), TRƯỚC section `## Admin governance`:

```markdown
## Deploy tách site: TourkitAiProxy.Worker

Scheduler workflow (`WorkflowSchedulerService` tick 60s) chạy trên project riêng `TourkitAiProxy.Worker` (Sdk=Microsoft.NET.Sdk.Worker) để ổn định:
- Web restart / IIS AppPool recycle / crash → automation KHÔNG rớt.
- Worker fail → UI + API vẫn sống. Deploy độc lập.
- Share code qua `<ProjectReference Include="../TourkitAiProxy.csproj" />` — worker dùng NGUYÊN `Services/Workflows/*`, `Services/Mail/*`, `Services/Deals/*`... không copy code.
- DI wiring shared qua `Services/Bootstrap/WorkflowStackRegistration.cs` extension `AddWorkflowStack()` — cả web và worker gọi cùng 1 method → 1 nguồn wiring.

**Cấu hình tách:**
- Web `appsettings.json`: `"Workflows": { "RunScheduler": false }` (default sau khi split)
- Worker `appsettings.json`: `"Workflows": { "RunScheduler": true }` + `ConnectionStrings:PushDb` + `Redis:ConnectionString` TRÙNG web (share `dbo.UserWorkflows` / `dbo.WorkflowRuns` / `dbo.TkSessions` / quota / cache).

**Endpoint `/api/v1/workflows/*` giữ nguyên trên WEB** (worker không expose HTTP). "Chạy ngay" gọi `WorkflowSchedulerService.RunOneAsync` — Singleton đã đăng ký ở web nên vẫn chạy được dù không có hosted tick.

Deploy: Windows Service via `sc.exe create` (mặc định), systemd + Docker documented. Xem `TourkitAiProxy.Worker/README.md`.

**Khi thêm workflow mới:** implement `IScheduledWorkflow` + `AddSingleton<IScheduledWorkflow, X>()` **trong `WorkflowStackRegistration`** (không phải `Program.cs`) → worker tự pickup, không cần deploy 2 lần.
```

- [ ] **Step 2: Cập nhật `appsettings.example.json` — default `RunScheduler: false`**

Sửa block `Workflows`:

```json
  "Workflows": {
    "_comment": "RunScheduler: web mặc định FALSE vì có site worker riêng (TourkitAiProxy.Worker). Đặt true CHỈ khi chưa tách worker (single-node deploy). TickSeconds: chu kỳ quét workflow due (sàn 5s).",
    "RunScheduler": false,
    "TickSeconds": 60
  },
```

- [ ] **Step 3: Commit doc updates**

```bash
git add CLAUDE.md appsettings.example.json
git commit -m "docs: doc kien truc tach worker + doi default RunScheduler=false"
```

---

## Task 6: Test kịch bản "user offline"

Kịch bản gốc user báo lỗi: "online chạy ngon, offline lỗi 'không kết nối được'". Kiểm chứng đã fix.

- [ ] **Step 1: Setup**

- Web + Worker chạy trên **server**, không phải máy user
- User `/workflows` bật `deal-auto-review` + `customer-auto-review` (với service account thật, interval 5 phút)

- [ ] **Step 2: Baseline — user online, để chạy 15 phút**

Verify:
- `dbo.WorkflowRuns` có 3+ record `deal-auto-review` `Status=ok`
- Cùng `customer-auto-review` cũng ok

- [ ] **Step 3: User offline (đóng browser / tắt máy)**

Chờ 15 phút. Verify:
- Worker log VẪN `[Scheduler] tick — N workflow due` (N ≥ 2)
- `dbo.WorkflowRuns` có record MỚI với `Status=ok`, không có "không kết nối được"
- `SELECT NextRunUtc FROM dbo.UserWorkflows WHERE WorkflowType IN ('deal-auto-review','customer-auto-review')` — thời gian tương lai (~intervalMinutes)

Nếu vẫn thấy `Status=failed` với error "không kết nối được":
- **KHÔNG PHẢI vấn đề kiến trúc worker** (worker chạy đúng — có tick, có gọi workflow).
- Root cause là `TourKit:BaseUrl` upstream flaky. Debug:
  - Check `Services/TourKit/TourKitApiClient.cs:51/89/124` — throw ở `HttpRequestException`.
  - Từ server worker: `curl -v https://mobile-test-api-2.tourkit.vn/api/health` — reachable?
  - `dbo.AppLogs` filter `Kind='http'` → xem full status code + duration. Nếu toàn timeout → upstream vấn đề, không phải worker.

- [ ] **Step 4: Ghi kết luận**

Nếu Step 3 pass: root cause thực = **web IIS AppPool idle unload** (web unload → không còn process nào tick scheduler → offline user = zero tick). Worker split FIX vấn đề.

Nếu Step 3 fail: worker OK nhưng upstream flaky. Cần separate fix (retry policy trong `TourKitApiClient` hoặc production upstream migration). Log kết luận vào PR description.

- [ ] **Step 5: (Optional) Cleanup + PR**

```bash
git checkout -b feature/tourkit-ai-proxy-worker
git push -u origin feature/tourkit-ai-proxy-worker
# Tạo PR review
```

---

## Self-review checklist

Trước khi execute, xác nhận:

1. **Spec coverage** — plan có: (a) tạo project Worker ✓; (b) share `Services/` qua ProjectReference ✓; (c) trích DI shared ✓; (d) web default `RunScheduler=false` ✓; (e) deploy Windows Service ✓; (f) không đụng UI ✓; (g) test kịch bản offline ✓.

2. **No placeholders** — mỗi Step có code cụ thể / command chính xác / expected output. Không có "TODO" / "add error handling".

3. **Type consistency** — `AddWorkflowStack` (Task 1) khớp lời gọi ở Program.cs (Task 1 Step 2) và Worker Program.cs (Task 2 Step 2). Tên service verify từ Program.cs gốc — không sáng tác.

4. **Rollback plan** nếu vỡ:
   - Revert commit Task 1 → web trở về default cũ (all-in-one)
   - Xoá service Windows: `sc.exe delete TourkitAiProxyWorker`
   - Set `Workflows:RunScheduler=true` trong web `appsettings.json`
   - Zero data loss (worker và web share DB — không có state riêng)

## Ghi chú thực thi

- **UTF-8 encoding**: Windows PowerShell mặc định UTF-16. Khi tạo file JSON qua PowerShell dùng `Out-File -Encoding utf8`. Ưu tiên dùng `Write` tool (đã UTF-8) thay vì `echo >` từ shell.
- **Vietnamese comment convention**: giữ nguyên style code hiện tại — comment tiếng Việt cho user-facing/business logic; keep English chỉ khi dùng thuật ngữ chuẩn (`hosted service`, `Singleton`).
- **Không đụng frontend**: Skill "taste" — plan này KHÔNG modify bất kỳ file nào trong `wwwroot/`. Verify bằng: `git diff --name-only main` — không có path bắt đầu `wwwroot/`.
