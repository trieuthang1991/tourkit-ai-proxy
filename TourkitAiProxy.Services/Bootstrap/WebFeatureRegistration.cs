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
        return s;
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
    }

    /// <summary>Tác vụ chạy nền của tiến trình WEB — hai thứ, hai lý do khác hẳn nhau.</summary>
    private static void AddWorkerNen(this IServiceCollection s, IConfiguration cfg)
    {
        // Scheduler: CHỈ instance có Workflows:RunScheduler=true mới tick nền. Mặc định false — sau
        // khi tách TourkitAiProxy.Worker thì worker mới chạy scheduler, web không tự tick.
        // Nút "Chạy ngay" (run-now) vẫn dùng được vì service đã đăng ký Singleton.
        var runScheduler = cfg.GetValue("Workflows:RunScheduler", false);
        Console.WriteLine($"[Startup] Workflows:RunScheduler = {runScheduler} "
                          + "(mặc định false — worker riêng TourkitAiProxy.Worker sẽ chạy)");
        if (runScheduler)
            s.AddHostedService(sp => sp.GetRequiredService<Workflows.WorkflowSchedulerService>());

        // Hai worker của chat chạy ở WEB (không phải worker riêng) vì tin chat phải đi NGAY — khách
        // đang chờ trước màn hình, không như bản tin sáng hẹn giờ. Vào: webhook chỉ GHI thân thô,
        // worker này mới xử lý.
        //
        // KHÔNG còn phụ thuộc cờ Features:Chat — cờ đó nay chỉ ẩn mục menu (xem MapHopThuChat).
        // Vẫn không tốn nhịp nào khi chưa dùng: cả hai worker tự dừng ngay vòng đầu nếu
        // ConnectionStrings:Chat để trống (repo.Configured == false).
        s.AddHostedService<Chat.Inbox.ChatInboundWorker>();
        s.AddHostedService<Chat.Inbox.ChatOutboxWorker>();
    }
}
