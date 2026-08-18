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
        // TourKitSourceHandler gắn X-TK-Source: ai + X-TK-Client-IP cho MỌI lệnh gọi TourKit.Api,
        // để ActivityLogsNewVersion phân biệt được thay đổi do AI với thay đổi do app mobile
        // (hai bên dùng chung tiến trình TourKit.Api nên DB không tự phân biệt được).
        s.AddTransient<TourkitAiProxy.Services.Http.TourKitSourceHandler>();
        AttachLogAndInsecure(
            s.AddHttpClient("tourkit", c =>
            {
                var baseUrl = cfg["TourKit:BaseUrl"] ?? "https://mobile-test-api-2.tourkit.vn";
                c.BaseAddress = new Uri(baseUrl);
                c.Timeout     = TimeSpan.FromSeconds(60);
            }).AddHttpMessageHandler<TourkitAiProxy.Services.Http.TourKitSourceHandler>(), "tourkit",
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
        s.AddSingleton<TourkitAiProxy.Services.Security.SsoCodeStore>();   // kho code 1-lần SSO (RAM/Redis theo Sso:ForceInMemory)
        s.AddSingleton<TourkitAiProxy.Services.Cache.ChatCache>();
        // Gợi ý "trạng thái nào còn phải làm" do AI đọc tên trạng thái của từng công ty.
        s.AddSingleton<TourkitAiProxy.Services.Workflows.StatusSemanticsService>();

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

        // ─── Tour Price Catalog (đồng bộ bảng giá NCC → dbo.TourPriceCatalog) ──
        s.AddSingleton<TourPrices.TourPriceCatalogRepository>();
        s.AddSingleton<Workflows.IScheduledWorkflow, TourPrices.TourPriceCatalogSyncWorkflow>();
        s.AddSingleton<TourPrices.TourPriceRetriever>();       // chọn nguồn giá (mẫu/thật/cả 2)
        s.AddSingleton<TourPrices.SampleCatalogSeeder>();      // nạp NCC mẫu từ seed lúc startup

        // ─── Bản tin sáng (Đợt 1) ────────────────────────────────────────────
        // Đăng ký ở ĐÂY (không phải Program.cs) để worker chạy nền cũng có — workflow gửi bản tin
        // sống bên worker, endpoint cấu hình sống bên web, cả hai cùng cần bộ này.
        s.AddSingleton<Digest.InsightRepository>();
        // Sổ ghi nhắc DÙNG CHUNG — tác vụ mới cần chặn nhắc lặp thì tiêm cái này, đừng thêm bảng.
        s.AddSingleton<Digest.NotifyLedgerRepository>();
        s.AddSingleton<Digest.DigestSubscriptionRepository>();
        // Cấu hình kênh gửi CỦA CÔNG TY — quay lại per-tenant 17/08 (xem TenantChannelSettingsStore):
        // đi gặp khách hàng thì không công ty nào chịu gửi ZNS bằng OA của bên cung cấp dịch vụ.
        s.AddSingleton<Digest.TenantChannelSettingsStore>();
        // Ghi chú cũ, giữ lại để hiểu vì sao từng có giai đoạn không có lớp này:
        // TenantChannelSettingsStore đã GỠ (14/08): proxy không còn đọc/ghi cấu hình kênh của
        // công ty — Zalo nay dùng OA chung khai ở config worker. Bảng dbo.TenantChannelSettings vẫn
        // còn, nhưng chủ của nó giờ là worker (lưu cặp token ZNS ở scope hệ thống).
        // KHÔNG có lớp gửi nào ở proxy nữa (gỡ 14/08). Proxy chỉ XẾP dòng vào dbo.OutboundMails;
        // gửi đi là việc của TourKit.PushWorker bên toutkit-app. Kể cả nút "Gửi thử" cũng xếp hàng
        // đợi — nhờ vậy thử thành công là bằng chứng bản tin thật gửi được, chứ không phải chứng
        // minh cho một đường code riêng chỉ tồn tại vì cái nút đó.

        // 3 tác vụ dưới đây nằm sau cờ Features:Digest (xem FeatureFlags.Digest).
        // Gỡ đăng ký là đủ để TẮT HẲN: WorkflowRegistry dựng từ IEnumerable<IScheduledWorkflow>
        // nên scheduler không có gì để chạy, GET /api/v1/workflows không liệt kê → thẻ tự biến mất
        // khỏi trang Tự động hoá, /run-now không resolve được. Không cần sửa gì ở frontend.
        //
        // Các service phụ trợ ở TRÊN vẫn đăng ký kể cả khi tắt: chúng vô hại lúc không ai resolve,
        // mà gỡ thì dễ vỡ chỗ khác (DealAutoReviewWorkflow — vẫn bật — dùng chung MailQueueRepository).
        //
        // Dữ liệu cũ trong dbo.UserWorkflows/dbo.DigestSubscriptions KHÔNG bị đụng tới. Scheduler gặp
        // loại không resolve được thì bỏ qua kèm cảnh báo → bật lại lúc nào cũng còn nguyên.
        if (FeatureFlags.Digest(cfg))
        {
            // Canh thanh toán: luồng THEO TỔ CHỨC (cảnh báo tenant-wide) nên dùng tài khoản tự động,
            // khác bản tin cá nhân chạy bằng tài khoản của chính người nhận.
            s.AddSingleton<Workflows.IScheduledWorkflow, Workflows.PaymentWatchdogWorkflow>();
            // Kiểm tra sẵn sàng khởi hành: cùng họ với canh thanh toán (tenant-wide, tài khoản tự
            // động, không AI). Nằm SAU cờ Digest vì nó ghi vào Bảng tin — tắt cờ thì
            // /api/v1/insights trả 404, thẻ ghi ra sẽ không ai xem được.
            // THÊM cờ RIÊNG Features:TourReadiness để ra mắt lệch nhịp với cụm bản tin: bản tin có
            // thể mở trước, tác vụ này giữ lại. FeatureFlags.TourReadiness đã gộp cả 2 điều kiện.
            if (FeatureFlags.TourReadiness(cfg))
                s.AddSingleton<Workflows.IScheduledWorkflow, Workflows.TourReadinessWorkflow>();
            // Canh doanh thu bất thường: cùng họ (tenant-wide, tài khoản tự động, không AI).
            if (FeatureFlags.AnomalyWatchdog(cfg))
                s.AddSingleton<Workflows.IScheduledWorkflow, Workflows.AnomalyWatchdogWorkflow>();
            // Nhắc chăm lại khách ngủ quên. KHÔNG gửi gì cho khách — chỉ dựng danh sách để gọi.
            if (FeatureFlags.AutoCare(cfg))
                s.AddSingleton<Workflows.IScheduledWorkflow, Workflows.CustomerAutoCareWorkflow>();
            // Bản tin sáng: fetch bằng phiên CỦA TỪNG NGƯỜI NHẬN (không phải tài khoản tự động)
            // → CRM tự áp quyền, lọc sai cũng chỉ thiếu chứ không lộ dữ liệu người khác.
            s.AddSingleton<Workflows.IScheduledWorkflow, Workflows.SaleBriefWorkflow>();
            // Bản tin điều hành: cùng nguyên tắc phiên riêng; khác ở chỗ có gọi AI viết lời (tốn lượt),
            // và AI lỗi thì rơi về bảng số chứ không bỏ gửi.
            s.AddSingleton<Workflows.IScheduledWorkflow, Workflows.CeoBriefWorkflow>();
        }

        s.AddSingleton<Workflows.WorkflowSchedulerService>();

        return s;
    }
}
