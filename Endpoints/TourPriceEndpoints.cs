using TourkitAiProxy.Models;
using TourkitAiProxy.Services.TextUtil;
using TourkitAiProxy.Services.TourKit;
using TourkitAiProxy.Services.TourPrices;

namespace TourkitAiProxy.Endpoints;

/// <summary>
/// GET /api/v1/tour-price/candidates — ứng viên giá NCC (kèm giá + nhãn nguồn) cho wizard dựng giá.
/// Nguồn: sample (NCC mẫu) | real (NCC thật catalog tenant) | both (cả 2, ưu tiên thật).
///
/// CHỈ để LÀM GIÀU prompt sinh giá ở wizard handleGenerate — KHÔNG đụng picker step2 (live TourKit,
/// shape room-type/pack riêng). Logic chọn NCC ở step2 giữ nguyên.
/// </summary>
public static class TourPriceEndpoints
{
    public static void MapTourPriceEndpoints(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapGroup("/api/v1");

        v1.MapGet("/tour-price/candidates", async (HttpContext ctx, TourPriceRetriever retriever, TkSessionStore sessions,
            string? source, string? city, int? categoryId, decimal? minPrice, decimal? maxPrice) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);

            var src = ParseSource(source);
            var cityNorm = string.IsNullOrWhiteSpace(city) ? null : VietnameseText.Norm(city);
            var q = new PriceQuery(cityNorm, categoryId, minPrice, maxPrice);
            var cands = await retriever.RetrieveAsync(sess.TenantId, q, src, ctx.RequestAborted);

            // Lọc city ra rỗng (DestinationMap chưa có / city không khớp CityNorm) → thử lại KHÔNG lọc city
            // để vẫn có gợi ý giá tham khảo. An toàn: chỉ nới lỏng, không đổi nguồn.
            if (cands.Count == 0 && cityNorm != null)
                cands = await retriever.RetrieveAsync(sess.TenantId, new PriceQuery(null, categoryId, minPrice, maxPrice), src, ctx.RequestAborted);

            var items = cands.Select(c => new
            {
                source        = c.Source,          // "real" | "sample"
                providerName  = c.Row.ProviderName,
                city          = c.Row.City,
                categoryId    = c.Row.CategoryId,
                categoryName  = c.Row.CategoryName,
                priceName     = c.Row.PriceName,
                description   = c.Row.Description,
                contractPrice = c.Row.ContractPrice,
                publicPrice   = c.Row.PublicPrice,
                stars         = c.Row.Stars,
            }).ToList();

            return Results.Json(new { source = src.ToString().ToLowerInvariant(), count = items.Count, items });
        });

        // GET /tour-price/hints — dải giá per-LOẠI (p25/p50/p75) cho wizard bơm mốc giá vào prompt AI.
        // GỒM loại city-less (vé máy bay/vận chuyển/HDV) → AI có mốc cho mọi mục lớn, không bịa số.
        v1.MapGet("/tour-price/hints", async (HttpContext ctx, TourPriceRetriever retriever, TkSessionStore sessions,
            string? source, string? city) =>
        {
            var sid = Sid(ctx);
            var sess = sessions.Get(sid);
            if (sess == null) return Results.Json(new { error = "Phiên không hợp lệ — đăng nhập lại" }, statusCode: 401);

            var src = ParseSource(source);
            var cityNorm = string.IsNullOrWhiteSpace(city) ? null : VietnameseText.Norm(city);
            var bands = await retriever.BandsAsync(sess.TenantId, cityNorm, src, ctx.RequestAborted);

            var items = bands
                .OrderByDescending(b => b.N)
                .Select(b => new
                {
                    source       = b.Source,
                    categoryId   = b.CategoryId,
                    categoryName = b.CategoryName,
                    n            = b.N,
                    p25          = decimal.Round(b.P25),
                    p50          = decimal.Round(b.P50),
                    p75          = decimal.Round(b.P75),
                })
                .ToList();

            return Results.Json(new { source = src.ToString().ToLowerInvariant(), city = cityNorm, count = items.Count, items });
        });
    }

    private static PriceSource ParseSource(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "real" or "that" => PriceSource.Real,
        "sample" or "mau" => PriceSource.Sample,
        _ => PriceSource.Both,   // mặc định: cả 2, ưu tiên thật
    };

    private static string? Sid(HttpContext ctx)
        => ctx.Request.Headers["X-Session-Id"].FirstOrDefault() ?? ctx.Request.Query["sessionId"].FirstOrDefault();
}
