namespace TourkitAiProxy.Services.Mail;

/// Nguồn duy nhất cho danh mục phân loại, trạng thái, ngữ điệu — nhãn tiếng Việt.
/// Dùng cho cả prompt AI, validate endpoint, lẫn hiển thị.
public static class MailTaxonomy
{
    public static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>
    {
        ["hoi_dat_tour"] = "Hỏi đặt tour",
        ["xin_bao_gia"]  = "Xin báo giá",
        ["khieu_nai"]    = "Khiếu nại",
        ["xac_nhan"]     = "Xác nhận",
        ["spam"]         = "Spam",
        ["khac"]         = "Khác",
    };

    public static readonly IReadOnlyDictionary<string, string> Statuses = new Dictionary<string, string>
    {
        ["moi"]         = "Mới",
        ["dang_xu_ly"]  = "Đang xử lý",
        ["da_phan_hoi"] = "Đã phản hồi",
        ["da_dong"]     = "Đã đóng",
    };

    /// tone key → mô tả ngữ điệu (nhúng vào prompt + hiển thị nút chọn).
    public static readonly IReadOnlyDictionary<string, string> Tones = new Dictionary<string, string>
    {
        ["lich_su"]    = "Lịch sự, trang trọng",
        ["than_thien"] = "Thân thiện, cởi mở",
        ["dam_phan"]   = "Đàm phán thương lượng",
        ["xin_loi"]    = "Lời xin lỗi chuyên biệt",
    };

    private const string DefaultCategory = "khac";
    private const string DefaultTone = "lich_su";

    /// Chuẩn hóa category AI trả về: trim + lowercase, nếu không thuộc 6 nhóm → "khac".
    public static string NormalizeCategory(string? raw)
    {
        var k = (raw ?? "").Trim().ToLowerInvariant();
        return Categories.ContainsKey(k) ? k : DefaultCategory;
    }

    public static bool IsCategory(string? k) => k != null && Categories.ContainsKey(k);
    public static bool IsStatus(string? k) => k != null && Statuses.ContainsKey(k);

    public static string ToneLabel(string? toneKey)
    {
        var k = (toneKey ?? "").Trim().ToLowerInvariant();
        return Tones.TryGetValue(k, out var v) ? v : Tones[DefaultTone];
    }
}
