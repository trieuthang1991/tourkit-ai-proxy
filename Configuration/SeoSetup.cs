using System.Text;
using Microsoft.AspNetCore.Http.Extensions;

namespace TourkitAiProxy.Configuration;

/// <summary>
/// Lớp SEO cho trang public. Toàn bộ nằm ở SERVER vì trang được vẽ bằng JavaScript: HTML gốc trả về
/// không có một chữ nào của nội dung, nên máy tìm kiếm và bộ xem trước link (Facebook, Zalo,
/// LinkedIn, Bing — phần lớn KHÔNG chạy JS) nhìn vào chỉ thấy trang trắng.
///
/// <para><b>Không hardcode tên miền.</b> Địa chỉ chuẩn (<c>canonical</c>) và <c>sitemap</c> dựng từ
/// chính request, có tính <c>X-Forwarded-*</c> vì sau reverse proxy thì <c>Request.Host</c> là host
/// nội bộ. Ghi cứng tên miền vào code thì lúc đổi domain hoặc chạy staging là hai thứ đó trỏ sai —
/// mà trỏ sai còn hại hơn không có.</para>
///
/// <para><b>Nội dung dựng sẵn phải TRÙNG với nội dung React vẽ ra.</b> Đây không phải mẹo che mắt
/// máy tìm kiếm: cùng một chữ, chỉ là gửi sớm hơn. Khác nhau thì Google coi là gian lận nội dung
/// (cloaking) và phạt. Có bộ kiểm <c>scripts/e2e/seo-prerender.check.js</c> đối chiếu từng câu ở đây
/// với <c>wwwroot/pages/landing.jsx</c> để lệch là báo, vì hai chỗ giữ cùng một câu chữ.</para>
/// </summary>
public static class SeoSetup
{
    /// <summary>
    /// Đường dẫn client + SEO của nó. <c>Index=false</c> là trang nội bộ: vẫn phục vụ bình thường
    /// nhưng gắn <c>noindex</c> để không lọt vào kết quả tìm kiếm.
    /// <para>⚠️ Danh sách này CŨNG là danh sách đường dẫn hợp lệ để trả 404 cho đường lạ. Thêm trang
    /// mới mà quên khai ở đây thì mở link trực tiếp vào trang đó sẽ ra 404 — bộ kiểm
    /// <c>seo-prerender.check.js</c> đối chiếu với các <c>&lt;Route path&gt;</c> trong app.jsx.</para>
    /// </summary>
    public record RouteSeo(string Path, string Title, string? Description = null, bool Index = false);

    /// Tiêu đề dùng chung khi thêm phần tên thương hiệu.
    private const string Brand = "TRAV-AI";

    /// <summary>
    /// Mô tả cho trang chủ. Giữ trong 150–160 ký tự: dài hơn Google tự cắt giữa câu.
    /// </summary>
    private const string HomeDesc =
        "TRAV-AI là trợ lý AI cho công ty du lịch Việt Nam: tự tính giá tour, trả mail khách, "
        + "chấm điểm khách hàng và phân tích số liệu từ CRM. Đăng ký tư vấn miễn phí.";

    public static readonly RouteSeo[] Routes =
    {
        // ── Public, cho index ──
        new("/", "TRAV-AI · Trợ lý AI cho công ty du lịch Việt Nam", HomeDesc, Index: true),
        new("/landing", "TRAV-AI · Trợ lý AI cho công ty du lịch Việt Nam", HomeDesc, Index: true),

        // ── Nội bộ: noindex. Đây là màn hình làm việc sau đăng nhập, lọt vào Google thì vừa
        //    không giúp ai tìm thấy gì (mở ra là màn đăng nhập), vừa loãng trang chủ.
        new("/travai", $"Trợ lý giọng nói · {Brand}"),
        // /jarvis: tên cũ của /travai, vẫn còn route nên vẫn phải khai — không khai thì mở link cũ
        // ra 404.
        new("/jarvis", $"Trợ lý giọng nói · {Brand}"),
        new("/wizard", $"AI tính giá tour · {Brand}"),
        new("/tour-builder", $"Bóc tour bằng AI · {Brand}"),
        new("/quotes", $"Báo giá đã lưu · {Brand}"),
        new("/customers", $"Chấm điểm khách hàng · {Brand}"),
        new("/deals", $"Phân tích cơ hội bán hàng · {Brand}"),
        new("/assistant", $"Trợ lý số liệu · {Brand}"),
        new("/mail", $"Hộp thư AI · {Brand}"),
        new("/chat-inbox", $"Hộp thư chat · {Brand}"),
        new("/visa", $"Visa AI · {Brand}"),
        new("/visa-config", $"Cấu hình Visa · {Brand}"),
        new("/ncc-import", $"Import nhà cung cấp · {Brand}"),
        new("/ncc-list", $"Nhà cung cấp · {Brand}"),
        new("/widget-admin", $"Widget chat khách · {Brand}"),
        new("/workflows", $"Tự động hoá · {Brand}"),
        new("/insights", $"Bảng tin · {Brand}"),
        new("/digest", $"Bản tin của tôi · {Brand}"),
        new("/ai-usage", $"Chi phí AI · {Brand}"),
        new("/home", $"Trang chủ · {Brand}"),
        new("/help", $"Hướng dẫn · {Brand}"),
        new("/flow-preview", $"Sơ đồ luồng · {Brand}"),
    };

    private static readonly Dictionary<string, RouteSeo> _byPath =
        Routes.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);

    /// <summary>Đường dẫn có phải trang client hợp lệ không (tính cả đường con như /help/abc).</summary>
    public static bool IsKnownRoute(string path)
    {
        var p = Normalize(path);
        if (_byPath.ContainsKey(p)) return true;
        // Trang có tham số (/help/{slug}, /quote-view/{id}) → khớp theo đoạn đầu.
        var firstSeg = p.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstSeg != null && _byPath.ContainsKey("/" + firstSeg);
    }

    public static RouteSeo For(string path)
    {
        var p = Normalize(path);
        if (_byPath.TryGetValue(p, out var exact)) return exact;
        var firstSeg = p.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstSeg != null && _byPath.TryGetValue("/" + firstSeg, out var byRoot))
            // Trang con KHÔNG kế thừa Index của trang gốc: /quote-view/123 là dữ liệu của một khách
            // cụ thể, tuyệt đối không để lọt vào kết quả tìm kiếm.
            return byRoot with { Index = false };
        return new RouteSeo(p, $"{Brand}");
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        // Bỏ dấu / cuối (trừ chính "/"): /mail và /mail/ là MỘT trang, để cả hai index là trùng lặp.
        return path.Length > 1 ? path.TrimEnd('/') is { Length: > 0 } t ? t : "/" : path;
    }

    /// <summary>
    /// Gốc địa chỉ công khai, ưu tiên header của reverse proxy. Sau IIS/nginx thì
    /// <c>Request.Scheme</c> hay là http và <c>Host</c> là host nội bộ — dùng thẳng sẽ ra canonical
    /// kiểu <c>http://localhost/</c>, tức khai với Google rằng bản chính nằm ở localhost.
    /// </summary>
    public static string BaseUrl(HttpContext ctx)
    {
        var h = ctx.Request.Headers;
        var host = First(h["X-Forwarded-Host"]) ?? ctx.Request.Host.Value;
        var scheme = First(h["X-Forwarded-Proto"]) ?? ctx.Request.Scheme;
        return $"{scheme}://{host}".TrimEnd('/');

        static string? First(string? raw)
            => string.IsNullOrWhiteSpace(raw) ? null : raw.Split(',')[0].Trim();
    }

    // ── Thẻ trong <head> ────────────────────────────────────────────────────────
    public static string Head(HttpContext ctx, string path)
    {
        var seo = For(path);
        var baseUrl = BaseUrl(ctx);
        // canonical LUÔN trỏ về "/" cho trang chủ: /landing là bản sao dùng cho đường dẫn cũ, để cả
        // hai cùng index là tự chia điểm cho một nội dung.
        var canonicalPath = seo.Index ? "/" : Normalize(path);
        var canonical = baseUrl + canonicalPath;
        var ogImage = baseUrl + "/images/intro/image2.webp";

        var sb = new StringBuilder();
        if (seo.Description != null)
            sb.Append("<meta name=\"description\" content=\"").Append(Esc(seo.Description)).Append("\" />\n");

        sb.Append("<link rel=\"canonical\" href=\"").Append(Esc(canonical)).Append("\" />\n");

        if (!seo.Index)
            // noindex,follow: không vào kết quả tìm kiếm nhưng vẫn cho đi theo link — chặn cả
            // follow thì máy tìm kiếm không lần ra được trang chủ từ những trang này.
            sb.Append("<meta name=\"robots\" content=\"noindex, follow\" />\n");

        // Open Graph — thứ quyết định link dán vào Zalo/Facebook có hiện ảnh và tiêu đề hay không.
        sb.Append("<meta property=\"og:type\" content=\"website\" />\n")
          .Append("<meta property=\"og:site_name\" content=\"TRAV-AI\" />\n")
          .Append("<meta property=\"og:locale\" content=\"vi_VN\" />\n")
          .Append("<meta property=\"og:title\" content=\"").Append(Esc(seo.Title)).Append("\" />\n")
          .Append("<meta property=\"og:url\" content=\"").Append(Esc(canonical)).Append("\" />\n")
          .Append("<meta property=\"og:image\" content=\"").Append(Esc(ogImage)).Append("\" />\n");
        if (seo.Description != null)
            sb.Append("<meta property=\"og:description\" content=\"").Append(Esc(seo.Description)).Append("\" />\n");

        sb.Append("<meta name=\"twitter:card\" content=\"summary_large_image\" />\n")
          .Append("<meta name=\"twitter:title\" content=\"").Append(Esc(seo.Title)).Append("\" />\n")
          .Append("<meta name=\"twitter:image\" content=\"").Append(Esc(ogImage)).Append("\" />\n");
        if (seo.Description != null)
            sb.Append("<meta name=\"twitter:description\" content=\"").Append(Esc(seo.Description)).Append("\" />\n");

        // Dữ liệu cấu trúc: chỉ gắn ở trang chủ. Gắn khắp nơi thì mỗi trang nội bộ lại khai một
        // Organization, Google đọc thấy hàng chục bản trùng.
        if (seo.Index) sb.Append(JsonLd(baseUrl));

        return sb.ToString();
    }

    private static string JsonLd(string baseUrl) =>
        "<script type=\"application/ld+json\">"
        + "{\"@context\":\"https://schema.org\",\"@graph\":["
        + "{\"@type\":\"Organization\",\"name\":\"TRAV-AI\",\"url\":\"" + baseUrl + "/\","
        + "\"logo\":\"" + baseUrl + "/images/tourkit-logo.png\","
        + "\"areaServed\":\"VN\"},"
        + "{\"@type\":\"SoftwareApplication\",\"name\":\"TRAV-AI\","
        + "\"applicationCategory\":\"BusinessApplication\","
        + "\"operatingSystem\":\"Web\","
        + "\"inLanguage\":\"vi\","
        + "\"url\":\"" + baseUrl + "/\","
        + "\"description\":\"" + Esc(HomeDesc) + "\"}"
        + "]}</script>\n";

    // ── Nội dung dựng sẵn cho trang chủ ────────────────────────────────────────
    //
    // ⚠️ MỌI câu ở đây phải có nguyên văn trong wwwroot/pages/landing.jsx — xem ghi chú đầu lớp về
    // chuyện trùng nội dung. Cố ý chỉ lấy phần chữ ỔN ĐỊNH (tiêu đề, tên tính năng, các bước) chứ
    // không lấy đoạn giới thiệu dài của từng tính năng: chữ càng dài càng hay sửa, mà sửa một bên
    // quên bên kia là thành lệch nội dung.
    private static readonly string[] FeatureTitles =
    {
        "AI Tính giá Tour", "Trợ lý số liệu", "Hộp thư AI", "Chấm điểm khách hàng",
        "AI phân tích Cơ hội", "Visa AI", "Bóc tour bằng AI", "Widget chat khách",
        "Import NCC bằng AI",
    };

    private static readonly string[] QuickValues =
    {
        "Tạo tour trong 5 phút", "Trả mail khách tự động",
        "Chấm điểm hồ sơ + cơ hội", "Báo cáo nói tiếng Việt",
    };

    private static readonly (string N, string T)[] Steps =
    {
        ("01", "Đăng ký tư vấn"), ("02", "Kết nối CRM Tourkit"), ("03", "AI bắt đầu phụ trợ"),
    };

    /// <summary>
    /// Nội dung chữ của trang chủ, nhét vào <c>#root</c>. React sẽ thay thế toàn bộ khi khởi động —
    /// nên đây vừa là thứ máy tìm kiếm đọc được ngay, vừa là thứ hiện ra nếu JS lỗi/chậm.
    /// </summary>
    public static string LandingBody() =>
        "<div id=\"seo-prerender\">"
        + "<h1>AI gánh việc tour, bạn dồn vào chốt deal.</h1>"
        + "<p>TRAV-AI tự tạo tour, trả mail khách, chấm điểm hồ sơ và phân tích số liệu. "
        + "Bạn chỉ duyệt và quyết.</p>"
        + "<p>Dành cho công ty du lịch Việt Nam</p>"
        + "<ul>" + string.Concat(QuickValues.Select(v => $"<li>{Esc(v)}</li>")) + "</ul>"
        + "<h2>09 tính năng AI gánh việc cho team tour mỗi ngày.</h2>"
        + "<ul>" + string.Concat(FeatureTitles.Select(t => $"<li><h3>{Esc(t)}</h3></li>")) + "</ul>"
        + "<h2>Ba bước, dưới một tuần.</h2>"
        + "<ol>" + string.Concat(Steps.Select(s => $"<li><h3>{Esc(s.T)}</h3></li>")) + "</ol>"
        + "<h2>Sẵn sàng để AI gánh việc lặp lại?</h2>"
        + "<p>15 phút demo trực tiếp với team Tourkit, chưa cần thanh toán ngay.</p>"
        + "<p>Đăng ký tư vấn miễn phí</p>"
        + "</div>";

    // ── robots.txt + sitemap.xml ───────────────────────────────────────────────
    public static WebApplication MapSeoEndpoints(this WebApplication app)
    {
        app.MapGet("/robots.txt", (HttpContext ctx) =>
        {
            var b = BaseUrl(ctx);
            var sb = new StringBuilder("User-agent: *\n");
            // Chặn theo ĐƯỜNG DẪN, không chỉ dựa vào thẻ noindex: thẻ chỉ có tác dụng sau khi máy
            // tìm kiếm tải trang về và chạy được nó, còn đây là chặn từ đầu.
            sb.Append("Disallow: /api/\n")
              .Append("Disallow: /admin-trav-ai\n")
              .Append("Disallow: /admin-trav-ai.html\n")
              .Append("Disallow: /widget-demo.html\n")
              .Append("Disallow: /stt-compare.html\n")
              .Append("Disallow: /docs/\n")
              .Append("Disallow: /dist/\n");
            foreach (var r in Routes.Where(r => !r.Index))
                sb.Append("Disallow: ").Append(r.Path).Append('\n');
            sb.Append("\nSitemap: ").Append(b).Append("/sitemap.xml\n");
            return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
        });

        app.MapGet("/sitemap.xml", (HttpContext ctx) =>
        {
            var b = BaseUrl(ctx);
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
                .Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
            // Chỉ trang chủ. Khai cả trang nội bộ vào sitemap là tự tay mời Google index đúng những
            // trang mình vừa gắn noindex — hai tín hiệu chỏi nhau, và Search Console báo lỗi.
            sb.Append("  <url><loc>").Append(b).Append("/</loc><changefreq>weekly</changefreq>")
              .Append("<priority>1.0</priority></url>\n");
            sb.Append("</urlset>\n");
            return Results.Text(sb.ToString(), "application/xml; charset=utf-8");
        });

        return app;
    }

    /// <summary>
    /// Escape TỐI THIỂU — chỉ 4 ký tự có thể phá cấu trúc HTML/thuộc tính.
    /// <para>CỐ Ý không dùng <c>WebUtility.HtmlEncode</c>: nó mã hoá cả chữ có dấu thành
    /// <c>&amp;#7897;</c>, nên "Hộp thư AI" biến thành một dãy số. Vẫn hiển thị đúng, nhưng thẻ meta
    /// phình gấp mấy lần, mô tả bị vượt giới hạn ký tự của Google, và người soát SEO xem mã nguồn
    /// thì không đọc được gì. Trang khai <c>charset=UTF-8</c> nên để nguyên chữ Việt là đúng.</para>
    /// </summary>
    private static string Esc(string s) => EscapeText(s);

    /// Bản public của <see cref="Esc"/> — StaticFilesSetup dùng cho thẻ &lt;title&gt;.
    public static string EscapeText(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
