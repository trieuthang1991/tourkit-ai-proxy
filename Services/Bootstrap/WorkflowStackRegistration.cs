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
/// Dùng chung web (<c>TourkitAiProxy</c> main) và worker (<c>TourkitAiProxy.Worker</c>) →
/// 1 nguồn wiring. KHÔNG add UI-specific service (Kestrel/CORS/Widget/Admin/HttpContext).
///
/// Caller quyết định có <c>AddHostedService(sp => sp.GetRequiredService&lt;WorkflowSchedulerService&gt;())</c>
/// hay không (thường: worker true, web false).
/// </summary>
public static class WorkflowStackRegistration
{
    public static IServiceCollection AddWorkflowStack(this IServiceCollection s, IConfiguration cfg)
    {
        // ─── HttpContextAccessor ─────────────────────────────────────────────
        // AiCallContext + WorkflowTraceAccessor consume IHttpContextAccessor để đọc HttpContext
        // (đẩy feature/tenant xuống AI call). Ở worker (generic host) accessor này trả null
        // context → các Push background dùng AiCallContext.Push() vẫn hoạt động qua AsyncLocal.
        // AddHttpContextAccessor idempotent — web gọi trước cũng không sao.
        s.AddHttpContextAccessor();

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
