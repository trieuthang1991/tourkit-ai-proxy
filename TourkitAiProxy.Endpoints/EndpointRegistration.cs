using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Services.Bootstrap;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// Một chỗ duy nhất khai MỌI đường dẫn của app.
///
/// <para><b>Vì sao gom về đây.</b> Danh sách route trước nằm thẳng trong <c>Program.cs</c>, lẫn giữa
/// cấu hình TLS, nén, log và middleware. Route là việc của tầng <c>Endpoints</c>, và gom lại thì
/// câu hỏi hay phải trả lời nhất — "đường này có được map không, và khi cờ tắt thì sao" — đọc một
/// chỗ là ra.</para>
///
/// <para>⚠️ <b>Không map ≠ 404.</b> <c>app.MapFallback</c> (trong <c>UseTourkitStaticFiles</c>, phục
/// vụ deep-link SPA) nhận mọi đường dẫn không khớp — <b>kể cả <c>/api/**</c></b> — và trả
/// <c>index.html</c> kèm status <b>200</b>. Client gọi API sẽ nhận một trang HTML thay vì lỗi, lần
/// ra nguyên nhân rất mất công. Đã dính thật một lần. Vì thế mỗi nhánh "tính năng tắt" dưới đây
/// phải map tay các tiền tố của nó về 404 JSON, chứ không được im lặng bỏ qua.</para>
/// </summary>
public static class EndpointRegistration
{
    /// <summary>Gọi một lần từ <c>Program.cs</c>, sau khi pipeline đã dựng xong.</summary>
    public static WebApplication MapTourkitEndpoints(this WebApplication app, IConfiguration cfg)
    {
        app.MapSystemEndpoints();
        app.MapConsultLeadEndpoints();   // POST /api/v1/consult-leads (public, lưu data/consult-leads.jsonl)
        app.MapNccImportEndpoints();     // /api/v1/ncc-import/* — bóc tách NCC từ file/text → Excel chuẩn
        app.MapAiEndpoints();
        app.MapReviewEndpoints();
        app.MapChatEndpoints();
        app.MapAssistantActionEndpoints();
        app.MapMailEndpoints();
        app.MapWorkflowEndpoints();

        MapBanTin(app, cfg);
        MapHopThuChat(app, cfg);

        app.MapTourEndpoints();
        app.MapTourPriceEndpoints();     // GET /api/v1/tour-price/candidates — ứng viên giá NCC cho wizard
        app.MapVisaEndpoints();
        app.MapDealEndpoints();
        app.MapTourQuoteEndpoints();
        app.MapSpeechEndpoints();
        app.MapTourBuilderEndpoints();
        app.MapAiUsageEndpoints();
        app.MapAdminAuthEndpoints();     // /api/v1/admin/auth/{login,logout,me}
        app.MapAdminUiEndpoints();       // /api/v1/admin/ui/* (require X-Admin-Session)
        app.MapQuotaEndpoints();
        app.MapQuotaOrderEndpoints();
        app.MapWidgetEndpoints();
        app.MapCrmSsoEndpoints();        // SSO 2 chiều với CRM

        MapVoAdmin(app);
        return app;
    }

    /// <summary>
    /// Cụm bản tin nằm sau cờ <c>Features:Digest</c>. Tắt thì KHÔNG map — ẩn ở giao diện thôi là
    /// chưa đủ, vì API vẫn đăng ký và vẫn gửi thật được.
    /// </summary>
    private static void MapBanTin(WebApplication app, IConfiguration cfg)
    {
        if (FeatureFlags.Digest(cfg))
        {
            app.MapInsightEndpoints();  // /api/v1/insights/* — bảng tin trong app (bản tin + cảnh báo)
            app.MapDigestEndpoints();   // /api/v1/digest/*   — đăng ký nhận + gửi thử + Zalo OA
            return;
        }

        ChanTuongMinh(app, new[] { "/api/v1/insights", "/api/v1/digest" },
            "Tính năng bản tin đang tắt (Features:Digest=false).");
    }

    /// <summary>
    /// Hộp thư chat đa kênh, sau cờ <c>Features:Chat</c>. Tắt thì webhook cũng không map: endpoint
    /// còn sống nghĩa là tin của khách vẫn chảy vào hệ dù tính năng "đang tắt".
    /// </summary>
    private static void MapHopThuChat(WebApplication app, IConfiguration cfg)
    {
        if (FeatureFlags.Chat(cfg))
        {
            app.MapChatInboxEndpoints();
            return;
        }

        // Đọc CHUNG danh sách với nhánh bật (ChatInboxEndpoints.DuongRieng). Liệt kê tay ở đây đã
        // lệch một lần — thêm /channels và /messages mà quên cập nhật, hai đường đó rơi vào
        // MapFallback và trả index.html kèm 200 thay vì 404.
        ChanTuongMinh(app, ChatInboxEndpoints.DuongRieng,
            "Tính năng hộp thư chat đang tắt (Features:Chat=false).");
    }

    /// <summary>Trả 404 JSON tường minh cho một nhóm tiền tố — xem cảnh báo ở đầu lớp.</summary>
    private static void ChanTuongMinh(WebApplication app, IEnumerable<string> tienTo, string loi)
    {
        IResult Tat() => Results.Json(new { error = loi }, statusCode: 404);
        foreach (var p in tienTo)
        {
            app.Map(p, Tat);
            app.Map(p + "/{**rest}", Tat);
        }
    }

    /// <summary>
    /// Vỏ HTML của trang quản trị — entry riêng <c>/admin-trav-ai.html</c>, KHÔNG dùng chung
    /// <c>index.html</c> của phần người dùng. <c>MapGet</c> tường minh thắng <c>MapFallback</c>.
    /// </summary>
    private static void MapVoAdmin(WebApplication app)
    {
        var goc = app.Environment.ContentRootPath;
        app.MapGet("/admin-trav-ai", (HttpContext ctx) => PhucVu(ctx, goc));
        app.MapGet("/admin-trav-ai/{**path}", (HttpContext ctx) => PhucVu(ctx, goc));

        static IResult PhucVu(HttpContext ctx, string contentRoot)
        {
            var path = Path.Combine(contentRoot, "wwwroot", "admin-trav-ai.html");
            if (!File.Exists(path)) return Results.NotFound();
            ctx.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
            return Results.Content(File.ReadAllText(path), "text/html; charset=utf-8");
        }
    }
}
