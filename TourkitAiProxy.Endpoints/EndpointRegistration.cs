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
    /// Hộp thư chat đa kênh. <b>Đường dẫn LUÔN được map, không phụ thuộc cờ</b>.
    ///
    /// <para><b>Cờ <c>Features:Chat</c> nay chỉ ẩn/hiện MỤC MENU</b> (qua
    /// <c>/api/v1/features</c>), không chặn API, không chặn webhook, không tắt worker. Quyết định
    /// của chủ dự án ngày 26/08/2026: chặn cả cụm thì không setup và kiểm thử được trên bản chạy
    /// thật — muốn thử một tính năng chưa ra mắt thì buộc phải mở nó cho mọi người.</para>
    ///
    /// <para>⚠️ <b>Cái giá, biết mà nhận:</b> tắt cờ giờ chỉ là <b>giấu</b>. Ai biết đường dẫn
    /// <c>/chat-inbox</c> vẫn vào được, và webhook vẫn sống nên <b>tin của khách vẫn chảy vào hệ
    /// thống</b> dù menu đang ẩn. Đừng trỏ webhook về đây rồi tưởng "tính năng đang tắt thì không
    /// sao" — tin vào thật, chỉ là không ai ngồi nhìn.</para>
    ///
    /// <para>Muốn tắt THẬT thì bỏ chuỗi kết nối <c>ConnectionStrings:Chat</c>: không có CSDL thì
    /// repository báo chưa cấu hình, worker tự dừng, mọi đường trả 503.</para>
    /// </summary>
    private static void MapHopThuChat(WebApplication app, IConfiguration cfg)
        => app.MapChatInboxEndpoints();

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
