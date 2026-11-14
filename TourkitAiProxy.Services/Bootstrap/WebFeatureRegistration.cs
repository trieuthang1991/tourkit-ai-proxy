using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TourkitAiProxy.Services.Bootstrap;

/// <summary>
/// Đăng ký DI cho các tính năng CHỈ web dùng — phần mà <see cref="WorkflowStackRegistration"/>
/// (dùng chung với <c>TourkitAiProxy.Worker</c>) không đụng tới.
///
/// <para><b>Vì sao tách khỏi <c>Program.cs</c>.</b> Trước 25/08/2026 toàn bộ nằm thẳng trong
/// <c>Program.cs</c> và file đó phình tới 467 dòng. Vấn đề không phải độ dài mà là <b>ai sở hữu</b>:
/// thêm một tính năng bất kỳ đều phải sửa cùng một file, nên nó vừa là nơi hay đụng độ khi gộp
/// nhánh, vừa là chỗ mà đọc xong không biết tính năng nào cần gì.</para>
///
/// <para>Mỗi cụm dưới đây là MỘT tính năng và tự khai đủ thứ nó cần. Thêm tính năng mới = thêm một
/// method ở đây + một dòng trong <see cref="AddWebFeatures"/>, không đụng <c>Program.cs</c>.</para>
///
/// <para>⚠️ Đây <b>không phải</b> chỗ đăng ký thứ dùng chung với worker. Cái gì worker cũng cần thì
/// về <see cref="WorkflowStackRegistration"/> — khai ở đây là worker thiếu, mà worker thiếu thì
/// tác vụ nền hỏng lúc chạy chứ không hỏng lúc biên dịch.</para>
/// </summary>
public static class WebFeatureRegistration
{
    /// <summary>Gọi một lần từ <c>Program.cs</c>, SAU <c>AddWorkflowStack</c>.</summary>
    public static IServiceCollection AddWebFeatures(this IServiceCollection s, IConfiguration cfg)
    {
        s.AddQuanTri();
        s.AddMuaQuota();
        s.AddWidgetChat();
        s.AddTroLy();
        s.AddBaoGiaVaTour();
        s.AddGiongNoi();
        s.AddThamDinhVisa();
        s.AddWorkerNen(cfg);
        if (FeatureFlags.Mcp(cfg)) s.AddMcp(FeatureFlags.McpOAuth(cfg));
        return s;
    }

    /// <summary>
    /// Máy chủ MCP tại <c>/mcp</c> + kho khoá MCP. <b>Chỉ đăng ký khi bật <c>Features:Mcp</c></b> —
    /// đường dẫn cũng chỉ map khi bật (<c>EndpointRegistration.MapMcp</c>), hai nửa phải khớp nhau.
    ///
    /// <list type="bullet">
    /// <item><b>Không trạng thái</b> (mặc định của SDK 2.3 theo bản giao thức 2026-07-28): mỗi request
    /// dựng máy chủ mới, không cần dính phiên vào một instance. Nhờ vậy <c>ConfigureSessionOptions</c>
    /// chạy MỖI request — "hôm nay" trong chỉ dẫn luôn đúng ngày.</item>
    /// <item>Giới hạn tần suất móc vào <c>tools/call</c> (không đếm initialize/tools/list). Chưa xác thực
    /// mà lọt tới đây (không thể, middleware chặn trước) thì vẫn từ chối — đóng cửa là mặc định.</item>
    /// <item>Tool: 20 tool ĐỌC sinh từ <c>ChatTools.All</c> (<c>Mcp.McpToolCatalog</c>). Tool hành động
    /// là đợt sau, sau cờ <c>Features:McpActions</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="oauthOn"><c>FeatureFlags.McpOAuth</c> có hiệu lực → cổng <c>/mcp</c> nhận thêm access token
    /// OAuth (<c>tkoat_</c>) và mọi 401 kèm <c>resource_metadata</c>. Tắt → hành vi giai đoạn 1 nguyên vẹn.</param>
    private static void AddMcp(this IServiceCollection s, bool oauthOn)
    {
        s.AddSingleton<Infrastructure.Mcp.IMcpTokenStore, Infrastructure.Mcp.McpTokenRepository>();
        s.AddSingleton<Infrastructure.Mcp.IMcpOAuthStore, Infrastructure.Mcp.McpOAuthRepository>();
        s.AddSingleton<Mcp.McpRateLimiter>();
        s.AddSingleton(sp =>
        {
            var sessions = sp.GetRequiredService<Infrastructure.TourKit.TkSessionStore>();
            return new Mcp.McpAuthenticator(
                sp.GetRequiredService<Infrastructure.Mcp.IMcpTokenStore>(),
                sid => sessions.Get(sid) is { } x ? new Mcp.McpSessionOwner(x.TenantId, x.Username) : null,
                sp.GetRequiredService<ILogger<Mcp.McpAuthenticator>>(),
                clock: null,
                oauthStore: oauthOn ? sp.GetRequiredService<Infrastructure.Mcp.IMcpOAuthStore>() : null,
                oauth: oauthOn ? sp.GetRequiredService<Mcp.OAuth.McpOAuthOptions>() : null);
        });

        // OAuth cho Claude.ai / ChatGPT. Đăng ký cùng MCP (singleton, dựng lười); chỉ được resolve khi
        // FeatureFlags.McpOAuth có hiệu lực — lúc đó Mcp:PublicBaseUrl chắc chắn hợp lệ.
        s.AddSingleton(sp => Mcp.OAuth.McpOAuthOptions.FromConfig(sp.GetRequiredService<IConfiguration>())
            ?? throw new InvalidOperationException("Thiếu hoặc sai Mcp:PublicBaseUrl — không dựng được máy chủ OAuth MCP"));
        // Client tải CIMD: KHÔNG theo chuyển hướng (chuyển hướng sang 169.254.169.254 là SSRF), 5 giây là bỏ.
        s.AddHttpClient(Mcp.OAuth.ClientMetadataFetcher.HttpClientName, c => c.Timeout = Mcp.OAuth.ClientMetadataFetcher.DefaultTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = Mcp.OAuth.ClientMetadataFetcher.DefaultTimeout,
                MaxResponseHeadersLength = 16,   // KB
            });
        s.AddSingleton<Mcp.OAuth.IClientMetadataFetcher, Mcp.OAuth.ClientMetadataFetcher>();
        s.AddSingleton(sp =>
        {
            var sessions = sp.GetRequiredService<Infrastructure.TourKit.TkSessionStore>();
            return new Mcp.OAuth.McpOAuthService(
                sp.GetRequiredService<Infrastructure.Mcp.IMcpOAuthStore>(),
                sp.GetRequiredService<Mcp.OAuth.IClientMetadataFetcher>(),
                sid => sessions.Get(sid) is { } x ? new Mcp.McpSessionOwner(x.TenantId, x.Username) : null,
                sp.GetRequiredService<Mcp.OAuth.McpOAuthOptions>(),
                sp.GetRequiredService<ILogger<Mcp.OAuth.McpOAuthService>>());
        });

        s.AddMcpServer(o =>
            {
                o.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "tourkit", Title = "TourKit CRM", Version = "1.0.0" };
                // Bộ sưu tập phải KHÁC null: SDK chỉ khai năng lực "tools" (và nhận tools/list,
                // tools/call) khi có bộ sưu tập — null thì tools/list trả -32601 "not available".
                // Tool sinh một lần lúc khởi động từ ChatTools.All; dịch vụ (runner, người gọi) lấy
                // theo từng lời gọi nên dùng chung được cho mọi request không trạng thái.
                o.ToolCollection = new();
                foreach (var tool in Mcp.McpToolCatalog.Create()) o.ToolCollection.Add(tool);
            })
            .WithHttpTransport(o =>
            {
                o.ConfigureSessionOptions = (_, opts, _) =>
                {
                    opts.ServerInstructions = Mcp.McpServerInstructions.Build(DateTime.UtcNow);
                    return Task.CompletedTask;
                };
            })
            .WithRequestFilters(f => f.AddCallToolFilter(next => async (ctx, ct) =>
            {
                var http = ctx.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
                if (http?.Items[Mcp.McpAuthenticator.ItemKey] is not Mcp.McpCaller caller)
                    return ToolError(Mcp.McpAuthenticator.MissingKey);
                var limiter = ctx.Services!.GetRequiredService<Mcp.McpRateLimiter>();
                if (!limiter.TryAcquire(caller, out var wait))
                    return ToolError(Mcp.McpRateLimiter.Message(wait));
                return await next(ctx, ct);
            }));

        static ModelContextProtocol.Protocol.CallToolResult ToolError(string text) => new()
        {
            IsError = true,
            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = text }],
        };
    }

    /// <summary>Quản trị — xác thực qua <c>Admin:Users</c> (JSON config) + phiên in-mem.</summary>
    private static void AddQuanTri(this IServiceCollection s)
    {
        s.AddSingleton<Admin.AdminUserStore>();
        s.AddSingleton<Admin.AdminSessionStore>();
        s.AddSingleton<Infrastructure.Admin.AdminUsageRepository>();
        s.AddSingleton<Infrastructure.Admin.AdminDigestRepository>();
        s.AddSingleton<Admin.ConsultLeadRepository>();
    }

    /// <summary>
    /// Luồng mua quota. Tingee = webhook IPN-only (bắn về tourkit-web); QR = VietQR img.vietqr.io.
    /// Chỉ 1 client THẬT, không mock.
    /// </summary>
    private static void AddMuaQuota(this IServiceCollection s)
    {
        s.AddSingleton<Quota.ITingeeClient, Quota.TingeeClient>();
        s.AddSingleton<Infrastructure.Quota.QuotaOrderRepository>();
    }

    /// <summary>
    /// Widget Chat — token per-tenant, nhúng JS vào site khách.
    /// <list type="bullet">
    /// <item>FAQ mode (<c>WidgetChatService</c>): chỉ system prompt + LLM kiến thức nền.</item>
    /// <item>CRM mode (<c>WidgetChatCrmService</c>): plan → gọi <c>/api/ai/*</c> whitelist → phân
    /// tích. Cần link CRM.</item>
    /// </list>
    /// </summary>
    private static void AddWidgetChat(this IServiceCollection s)
    {
        s.AddSingleton<Infrastructure.Widget.WidgetTokenRepository>();
        s.AddSingleton<Widget.WidgetChatService>();
        s.AddSingleton<Widget.WidgetCrmLinkService>();
        s.AddSingleton<Widget.WidgetChatCrmService>();
    }

    /// <summary>Trợ lý số liệu + các hành động ghi (giao việc, lịch hẹn, mail, chuẩn bị gặp khách).</summary>
    private static void AddTroLy(this IServiceCollection s)
    {
        // ⚠️ THỨ TỰ QUAN TRỌNG: NativeToolUseAgent (Anthropic native tools) chạy trước,
        // JsonPlannerAgent là fallback cho mọi provider khác (OpenCode, 9routes…).
        // ChatAgentService resolve runtime ĐẦU TIÊN có Supports(provider)=true — đảo hai dòng này
        // là mọi provider rơi hết về đường JSON-prompt mà không lỗi nào hiện lên.
        // Runner dùng chung (đổi tên → mã, đệm "d|", re-login 401). Singleton vì cả hai agent là singleton;
        // MCP cũng gọi qua đây.
        s.AddSingleton<Chat.CrmToolRunner>();
        s.AddSingleton<Chat.IAgentRuntime, Chat.NativeToolUseAgent>();
        s.AddSingleton<Chat.IAgentRuntime, Chat.JsonPlannerAgent>();
        s.AddSingleton<Chat.UnresolvedQuestionsLog>();
        s.AddSingleton<Chat.ChatAgentService>();

        // Template mail global (dbo.MailTemplates) cho admin CRUD + seed mặc định. Các Mail service
        // khác (MailAccountStore, MailQueueRepository, workflow…) đã nằm trong AddWorkflowStack —
        // cái này là UI/admin-specific nên worker không cần.
        s.AddSingleton<Infrastructure.Mail.MailTemplateRepository>();

        // Hàng đợi hành động CRM (dbo.CrmActionQueue) — trợ lý enqueue (giao việc/tạo lịch hẹn),
        // worker app-side (toutkit-app) drain. Singleton như MailQueueRepository: cùng chỉ dùng
        // TourkitAiDb.OpenAsync mở connection mới mỗi lần gọi, không giữ state.
        s.AddSingleton<Infrastructure.Crm.CrmActionQueueRepository>();

        // Resolver tên→id (khách/nhân viên/deal/workflow) cho các hành động ghi. Stateless (chỉ gọi
        // TourKitApiClient qua jwt truyền vào) nên singleton.
        s.AddSingleton<Chat.ActionResolver>();

        // Nhớ lựa chọn user đã chọn ở bước hỏi lại, để lượt sau KHÔNG bắt chọn lại.
        // Singleton vì state phải chia sẻ giữa /chat và /action/resolve.
        s.AddSingleton<Chat.ActionResolutionMemory>();

        // Thực thi hành động đã xác nhận (route theo ActionKind). Singleton — mọi phụ thuộc đều
        // singleton nên không dính captive dependency.
        s.AddSingleton<Chat.ActionExecutor>();

        // Thẻ chuẩn bị gặp khách — gom hồ sơ + hạng đã chấm + thư gần nhất rồi để AI gợi ý nên nói
        // gì. Chạy THEO YÊU CẦU (action prepare_meeting), cố ý không phải tác vụ nền: dựng sẵn cho
        // mọi cuộc hẹn thì tốn một lượt AI cho cả những cuộc chẳng ai cần chuẩn bị.
        s.AddSingleton<Chat.MeetingBriefService>();
    }

    /// <summary>Soạn tour bằng AI + lưu báo giá.</summary>
    private static void AddBaoGiaVaTour(this IServiceCollection s)
    {
        // Bóc mô tả tự do thành form Tour GIT (Type=3) cho NV prefill.
        s.AddSingleton<Tour.TourBuilderService>();
        // Báo giá tour lưu DB, per-tenant (thay luồng localStorage cũ).
        s.AddSingleton<Infrastructure.TourQuotes.TourQuoteRepository>();
    }

    /// <summary>
    /// Nghe và đọc. Chuỗi ưu tiên TTS: Vbee / Google (nếu có khoá) → edge-tts (giọng Việt neural,
    /// miễn phí, cần mạng) → Piper (offline) → OpenAI.
    /// </summary>
    private static void AddGiongNoi(this IServiceCollection s)
    {
        s.AddSingleton<Speech.SpeechToTextService>();
        s.AddSingleton<Speech.EdgeTtsService>();          // FREE, giọng vi chuẩn
        s.AddSingleton<Speech.PiperTtsService>();         // FREE offline (fallback)
        s.AddSingleton<Speech.TextToSpeechService>();     // OpenAI (fallback nếu có key)

        // Vbee AIVoice — giọng Việt neural chất lượng cao (batch async). Named HttpClient "vbee"
        // để giữ mặc định auto-follow redirect của audioLink.
        s.AddHttpClient("vbee");
        s.AddSingleton<Speech.VbeeTtsService>();          // ưu tiên nếu có key
        s.AddSingleton<Speech.VbeeSttService>();          // primary khi SttEnabled; WAV-only + fallback

        // Google Cloud TTS — auth bằng API key (REST đồng bộ). Khác Vbee ở chỗ endpoint Google (GFE)
        // tương thích ngược với Schannel cũ, nên thường gọi thẳng được từ WinServer 2012 R2.
        s.AddHttpClient("google-tts");
        s.AddSingleton<Speech.GoogleTtsService>();
    }

    /// <summary>
    /// Thẩm định visa — upload hồ sơ → AI vision đọc → chấm tỉ lệ đậu/rớt.
    /// File gốc lưu tạm <c>data/visa-files/</c> (tự xoá sau 7 ngày), kết quả vào <c>dbo.VisaAssessments</c>.
    /// </summary>
    private static void AddThamDinhVisa(this IServiceCollection s)
    {
        s.AddSingleton<Visa.VisaFileStore>();
        s.AddSingleton<Infrastructure.Visa.VisaRepository>();
        s.AddSingleton<Infrastructure.Visa.VisaQuestionRepository>();
        s.AddSingleton<Visa.VisaExtractionService>();
        s.AddSingleton<Visa.VisaScoringService>();
        // Kiểm key AI riêng (BYO) trước khi lưu — chỉ endpoint web dùng.
        s.AddSingleton<AiKeys.TenantAiKeyValidator>();
    }

    /// <summary>Tác vụ chạy nền của tiến trình WEB — hai thứ, hai lý do khác hẳn nhau.</summary>
    private static void AddWorkerNen(this IServiceCollection s, IConfiguration cfg)
    {
        // Scheduler: CHỈ instance có Workflows:RunScheduler=true mới tick nền. Mặc định false — sau
        // khi tách TourkitAiProxy.Worker thì worker mới chạy scheduler, web không tự tick.
        // Nút "Chạy ngay" (run-now) vẫn dùng được vì service đã đăng ký Singleton.
        // Không in ở đây nữa — cả scheduler lẫn worker chat gộp vào MỘT dòng tự khai lúc khởi
        // động, xem InstanceInfo. In rải rác thì đọc log gộp của hai máy không ghép lại được.
        var runScheduler = cfg.GetValue("Workflows:RunScheduler", false);
        if (runScheduler)
            s.AddHostedService(sp => sp.GetRequiredService<Workflows.WorkflowSchedulerService>());

        // Ba worker của chat chạy ở WEB (không phải worker riêng) vì tin chat phải đi NGAY — khách
        // đang chờ trước màn hình, không như bản tin sáng hẹn giờ. Vào: webhook chỉ GHI thân thô,
        // worker này mới xử lý.
        //
        // KHÔNG còn phụ thuộc cờ Features:Chat — cờ đó nay chỉ ẩn mục menu (xem MapHopThuChat).
        // Vẫn không tốn nhịp nào khi chưa dùng: cả ba tự dừng ngay vòng đầu nếu
        // ConnectionStrings:Chat để trống (repo.Configured == false).
        //
        // ⚠️ MẶC ĐỊNH BẬT, và cố ý ngược với Workflows:RunScheduler ở trên. Hai cờ, hai hướng an
        // toàn khác nhau: quên khai scheduler thì bản tin sáng chậm một nhịp, không ai chết; còn
        // quên khai cờ này mà mặc định TẮT thì hộp thư ngừng nhận VÀ ngừng gửi trên mọi máy —
        // khách nhắn vào hư không, và không có lỗi nào hiện lên.
        //
        // Khi nào đặt false: máy chủ chạy NHIỀU instance trên cùng một CSDL. Hàng đợi vốn đã an
        // toàn nhờ FOR UPDATE SKIP LOCKED nên không sợ gửi hai lần — cái đáng sợ là hai instance
        // chạy HAI PHIÊN BẢN MÃ khác nhau, lúc đó con cũ hỏng mọi nhịp trong im lặng (đã dính thật
        // 28/08/2026). Chọn đúng MỘT con chạy worker, các con còn lại đặt false.
        if (cfg.GetValue("Workflows:RunChatWorkers", true))
        {
            s.AddHostedService<Chat.Inbox.ChatInboundWorker>();
            s.AddHostedService<Chat.Inbox.ChatOutboxWorker>();
        }

        // ẢNH tách khỏi TIN NHẮN bằng một cờ RIÊNG, vì hai việc này khác nhau ở mọi mặt đáng kể:
        //
        //   · tin nhắn — khách đang chờ trước màn hình, phải đi trong vài giây, mỗi lượt rất nhẹ;
        //   · ảnh — không ai chờ, nhưng mỗi tệp là một lượt tải mạng cộng một lượt nén (tốn CPU),
        //     và một hộp thư lâu năm có hàng nghìn tệp.
        //
        // Gộp chung một cờ thì muốn dời việc nặng sang máy khác là dời luôn cả việc gấp. Tách ra
        // mới đặt được cấu hình mà đội vận hành thật sự cần:
        //
        //   web  : RunWorkers = true  · RunMediaWorker = false   (nhẹ, chỉ lo tin của khách)
        //   máy ảnh: RunWorkers = false · RunMediaWorker = true   (một con chuyên vét ảnh)
        //
        // ⚠️ PHẢI có ĐÚNG MỘT con bật RunMediaWorker. Tắt hết thì ảnh cũ ngừng được cứu trong im
        // lặng — mà url của nhà cung cấp thì vẫn đếm ngược (ảnh Meta sống 5 ngày), nên "im lặng"
        // ở đây nghĩa là mất hẳn. Bật nhiều con không sai (hàng đợi khoá bằng FOR UPDATE SKIP
        // LOCKED) nhưng phí băng thông và dễ bị CDN chặn tốc độ.
        //
        // Xem con nào đang bật cái gì: GET /healthz → khối "instance".
        if (cfg.GetValue("Workflows:RunChatMediaWorker", true))
            s.AddHostedService<Chat.Inbox.ChatMediaBackfillWorker>();
    }
}
