namespace TourkitAiProxy.Domain.Mail;

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

    /// <summary>
    /// ĐỊNH NGHĨA từng nhóm cho AI. Nhãn ở <see cref="Categories"/> là để HIỂN THỊ; nhãn trần
    /// ("spam: Spam") không đủ để quyết định, và đó chính là chỗ hỏng: soát 1.215 thư thật (14/08)
    /// cho thấy cùng một tiêu đề rơi vào các nhóm khác nhau — "Thông báo có công việc mới được giao"
    /// 143 thư `spam` / 52 `khac`, "Thông báo có phiếu thu mới" rải 3 nhóm. Model không sai bản chất,
    /// nó bị hỏi một câu chưa đủ dữ kiện để trả lời giống nhau hai lần.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CategoryHints = new Dictionary<string, string>
    {
        ["hoi_dat_tour"] = "NGƯỜI THẬT hỏi mua/đặt tour cho chuyến đi của họ: hỏi lịch trình, còn chỗ không, muốn đi đâu, đi mấy người",
        ["xin_bao_gia"]  = "NGƯỜI THẬT đề nghị gửi báo giá hoặc hỏi giá cụ thể cho một hành trình/dịch vụ",
        ["khieu_nai"]    = "Khách phàn nàn, không hài lòng, đòi hoàn tiền, phản ánh chất lượng dịch vụ",
        ["xac_nhan"]     = "Xác nhận một giao dịch CỦA KHÁCH đã thống nhất: chốt booking, đã thanh toán, xác nhận ngày khởi hành",
        ["spam"]         = "Quảng cáo, mời chào, lừa đảo từ người gửi KHÔNG có quan hệ nào với công ty",
        ["khac"]         = "Mọi thứ còn lại: thông báo tự động từ hệ thống/dịch vụ công ty đang dùng, thư nội bộ giữa nhân viên, biên nhận, bản tin đã đăng ký",
    };

    /// <summary>
    /// Luật gỡ hoà — phần quan trọng nhất của prompt. Mỗi luật ứng với một lỗi ĐO ĐƯỢC trong dữ liệu
    /// thật, không phải phòng xa chung chung.
    /// </summary>
    public static readonly string TieBreakRules = string.Join("\n", new[]
    {
        "1. Xét MỤC ĐÍCH của thư, KHÔNG xét người gửi. Cùng một nơi gửi (Grab, Google, LinkedIn, ngân hàng…) vừa gửi thông báo vừa gửi quảng cáo: thư GHI NHẬN/THÔNG BÁO một việc đã xảy ra (biên nhận, hoá đơn, cảnh báo bảo mật, nhắc việc, thông báo nội bộ) → 'khac'; thư CHÀO MỜI/KHUYẾN MÃI/GIỚI THIỆU SẢN PHẨM → 'spam', kể cả khi đến từ dịch vụ công ty đang dùng.",
        "2. Thư NỘI BỘ giữa nhân viên, hoặc thông báo do chính hệ thống quản lý của công ty gửi → luôn 'khac', TUYỆT ĐỐI KHÔNG phải 'spam'.",
        "3. Phân vân giữa 'spam' và 'khac' MÀ thư không chào mời/bán gì → chọn 'khac'. Xếp nhầm vào 'spam' thì thư bị chôn luôn, còn 'khac' thì nhân viên vẫn thấy.",
        "4. 'xac_nhan' CHỈ khi thư xác nhận giao dịch của KHÁCH. Thông báo nội bộ kiểu 'có công việc mới được giao', 'có phiếu chi mới', 'có phiếu thu mới' là việc nội bộ → 'khac'.",
        "5. Chỉ chọn 'hoi_dat_tour'/'xin_bao_gia' khi người gửi là NGƯỜI THẬT hỏi cho chuyến đi của chính họ. Bản tin, quảng cáo hay tài liệu nội bộ có chữ 'tour' KHÔNG tính.",
    });

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
