using System.Text.RegularExpressions;
using TourkitAiProxy.Services.TextUtil;

namespace TourkitAiProxy.Services.TourPrices;

/// <summary>
/// Luật thuần cho catalog — tách khỏi repository/workflow để test không cần DB.
///
/// Số liệu thật (TopTour, đo 2026-07-15 — xem spec §2.2):
///   • Bóc hạng sao từ tên NCC chỉ đúng 346/588 = 59% → Stars NULLABLE, và
///     KHÔNG BAO GIỜ được dùng làm lọc cứng (sẽ âm thầm bỏ sót 41%).
///   • contract_price sạch 99,7% — chỉ 25/9.460 dòng < 50k (25đ, 330đ, 700đ).
/// </summary>
public static class PriceCatalogRules
{
    /// Ngưỡng rác nhập tay. Dưới ngưỡng này chắc chắn là gõ nhầm, không phải giá thật.
    public const decimal MinPrice = 50_000m;

    /// Loại DV chặn mặc định. "Vé máy bay" chứa TÊN HÀNH KHÁCH THẬT
    /// ("TRINH/XUAN PHONG MR (ADT)") → đồng bộ sang DB khác là bê PII đi không lý do;
    /// mà vé theo từng chuyến nên tái dùng cũng vô nghĩa. Xem spec §4.3.
    public static readonly IReadOnlyList<string> DefaultBlockedCategories = new[] { "ve may bay" };

    // "5*" / "4 sao" / "3*". KHÔNG khớp năm ("- 2025") vì bắt buộc chữ số 1-5 ĐƠN LẺ
    // (không có chữ số liền trước/sau) rồi mới tới '*' hoặc " sao".
    private static readonly Regex StarRx = new(
        @"(?<![0-9])([1-5])\s*(?:\*|sao\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// Bóc hạng sao từ tên NCC. Không rõ → null (KHÔNG đoán).
    public static int? ParseStars(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return null;
        var m = StarRx.Match(providerName);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// Dòng có bị loại khỏi catalog không?
    public static bool IsExcluded(string? categoryName, decimal contractPrice,
        IReadOnlyList<string> blockedCategories)
    {
        if (contractPrice < MinPrice) return true;
        var cat = VietnameseText.Norm(categoryName);
        if (cat.Length == 0) return false;
        foreach (var b in blockedCategories)
            if (cat.Contains(VietnameseText.Norm(b), StringComparison.Ordinal)) return true;
        return false;
    }
}
