namespace TourkitAiProxy.Models;

/// <summary>
/// Catalog gói nạp quota AI. 3 mức theo chính sách:
///   • starter:    1.500.000đ → 1.000 lượt  (1.500đ/lượt) — test 1 tháng team 2-3 người
///   • growth:     3.000.000đ → 3.000 lượt  (1.000đ/lượt, -33%) — team marketing/sale 5-10 người [popular]
///   • enterprise: 5.000.000đ → 6.000 lượt  (~833đ/lượt, -44%) — công ty tour nhiều chi nhánh
///
/// Catalog hardcode trong code (KHÔNG đặt config) — đổi giá = đổi code + commit log, audit được.
/// </summary>
public record QuotaTier(
    string Id,
    string Name,
    long AmountVnd,
    int QuotaUnits,
    string Tagline,
    string[] Benefits,
    bool Popular
);

public static class QuotaTierCatalog
{
    public static readonly QuotaTier[] All =
    {
        new("starter", "Khởi đầu", 1_500_000, 1_000,
            "Test toàn diện 1 tháng cho team 2-3 người",
            new[]
            {
                "1.000 lượt AI cho mọi tính năng",
                "Không thời hạn — dùng hết mới hết",
                "Hỗ trợ qua email & chat",
                "Phù hợp dùng thử + văn phòng nhỏ",
            },
            Popular: false),

        new("growth", "Tăng tốc", 3_000_000, 3_000,
            "Cho team marketing/sale 5-10 người, dùng hằng ngày",
            new[]
            {
                "3.000 lượt AI — tiết kiệm 33% mỗi lượt",
                "Ưu tiên xử lý khi máy chủ bận",
                "Hỗ trợ trực tiếp Zalo nhân viên kỹ thuật",
                "Tặng kèm template báo giá + email mẫu",
            },
            Popular: true),

        new("enterprise", "Doanh nghiệp", 5_000_000, 6_000,
            "Công ty tour nhiều chi nhánh, dùng cả tháng không lo hết",
            new[]
            {
                "6.000 lượt AI — tiết kiệm 44% mỗi lượt",
                "Đào tạo riêng cho team (2 buổi online)",
                "Đường dây hotline ưu tiên 8h-22h",
                "Custom prompt theo dòng tour của công ty",
            },
            Popular: false),
    };

    public static QuotaTier? Find(string? id)
        => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Trả về cho FE để render 3 card.</summary>
public record TiersResp(QuotaTier[] Tiers);

/// <summary>Body POST /quota/order — FE gửi tierId, BE tạo order + Tingee QR.</summary>
public record CreateOrderReq(string TierId);

/// <summary>Response sau khi tạo order — đủ cho FE vẽ QR + countdown + bắt đầu poll status.</summary>
public record CreateOrderResp(
    string OrderId,
    string TierId,
    string TierName,
    long AmountVnd,
    int QuotaUnits,
    string Memo,
    string QrPayload,         // chuỗi EMV — FE render qua qrcode.js
    string BankBin,
    string AccountNumber,
    string AccountName,
    string ExpiresAt,         // ISO 8601 UTC
    int ExpiresInSeconds      // FE dùng cho countdown khỏi parse date
);

/// <summary>Response GET /quota/order/{id}/status — FE poll mỗi 3s.</summary>
public record OrderStatusResp(
    string OrderId,
    string Status,            // pending | paid | expired | cancelled
    string? PaidAt,
    int? AddedUnits,          // có khi status=paid → FE show "Đã cộng X lượt"
    int? QuotaUsed,           // snapshot quota mới (limit/used) cho FE refresh chip ngay
    int? QuotaLimit,
    int? QuotaRemaining
);

/// <summary>
/// Payload webhook IPN Tingee (best-guess shape — dựa generic VietQR/banking webhook). Tự bind tolerant từ JSON.
/// `Description` chứa nội dung CK do user nhập = OrderId → BE match.
/// Khi anh có API key Tingee + xem doc thật, sửa lại field names cho khớp.
/// </summary>
public record TingeeWebhookPayload(
    string? TransactionId,
    long? Amount,
    string? Description,      // nội dung CK = OrderId
    string? AccountNumber,
    string? BankCode,
    string? Timestamp
);
