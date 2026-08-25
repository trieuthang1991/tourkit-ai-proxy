using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// Thẻ chuẩn bị gặp khách (S4) — phần gom dữ kiện, thuần, không AI/DB.
///
/// Dữ kiện này vừa là thứ đưa cho AI, vừa là bản dự phòng hiện ra khi AI hỏng. Nên hai yêu cầu:
/// đúng (không bịa, không lẫn của khách khác) và tự đọc được khi không có lời AI nào đi kèm.
public class MeetingBriefServiceTests
{
    private static CustomerMetrics Metrics(
        int tours = 3, long spent = 90_000_000, long aov = 30_000_000,
        string? last = "01/06/2026", int complaints = 0, int cancels = 0, int? careAgo = null)
        => new(TotalTours: tours, TotalSpent: spent, Aov: aov, LastPurchaseDate: last,
               LastPurchaseDaysAgo: null, AvgDaysBetweenOrders: null, CareInteractions: 0,
               LastCareDaysAgo: careAgo, ComplaintCount: complaints, CancelCount: cancels);

    private static Customer Cust(
        string name = "Anh Nam", string? email = "nam@example.com", string? note = null,
        CustomerMetrics? metrics = null,
        List<TourPurchase>? purchases = null, List<CareLog>? careLogs = null)
        => new(Id: "15878", Code: "KH_001", Name: name, Phone: "0900000000", Email: email,
               Age: null, Gender: null, Location: "Hà Nội", Segment: "VIP",
               CreatedAt: "01/01/2026", Source: null,
               Metrics: metrics ?? Metrics(),
               Purchases: purchases ?? new(), CareLogs: careLogs ?? new(), Note: note);

    private static CustomerReview Review(
        string rank = "B", string rankReason = "Mua đều, giá trị cao",
        string alertLevel = "none", string? alertMsg = null,
        string preferences = "", List<string>? concerns = null,
        string actionTask = "", List<string>? products = null)
        => new(Id: "r1", CustomerId: "15878", Rank: rank, RankReason: rankReason,
               Alert: new ReviewAlert(alertLevel, alertMsg), Portrait: "",
               Strengths: new(), Concerns: concerns ?? new(), Preferences: preferences,
               ActionNow: new ReviewAction(actionTask, ""), Action30Days: new(),
               ProductSuggestions: products ?? new(), SummaryLine: "",
               DataFingerprint: "", AiModel: "", AiProvider: "", TokensIn: 0, TokensOut: 0,
               GeneratedAt: "2026-08-14T00:00:00Z", Feedback: null);

    private static MailItem Mail(string subject, string category = "hoi_dat_tour",
        string status = "moi", string received = "2026-08-10T03:00:00Z")
        => new(Id: "m1", From: new MailContact("Nam", "nam@example.com"), Subject: subject,
               Body: "", ReceivedAt: received, IsRead: false, Category: category,
               Status: status, AiSummary: null, Draft: null);

    // ── Dữ kiện tối thiểu ─────────────────────────────────────────────────────────

    [Fact]
    public void Khach_khong_co_lich_su_gi_van_ra_duoc_dong_nhan_dang()
    {
        var facts = MeetingBriefService.BuildFacts(Cust(), review: null, mails: Array.Empty<MailItem>());
        Assert.Contains("Anh Nam", facts);
        Assert.Contains("KH_001", facts);
        Assert.Contains("VIP", facts);
    }

    /// Số tiền phải theo định dạng Việt — cả thẻ dùng "90.000.000đ", lẫn kiểu Anh vào là đọc như
    /// ghép từ hai nguồn.
    [Fact]
    public void Tien_in_theo_dinh_dang_Viet()
    {
        var facts = MeetingBriefService.BuildFacts(Cust(), null, Array.Empty<MailItem>());
        Assert.Contains("90.000.000đ", facts);
    }

    // ── Rủi ro: thứ tuyệt đối không được nuốt ─────────────────────────────────────

    /// Vào gặp khách mà không biết họ từng phàn nàn là hỏng cả buổi.
    [Fact]
    public void Tung_phan_nan_thi_phai_hien_ra()
    {
        var facts = MeetingBriefService.BuildFacts(
            Cust(metrics: Metrics(complaints: 2, cancels: 1)), null, Array.Empty<MailItem>());
        Assert.Contains("phàn nàn 2 lần", facts);
        Assert.Contains("huỷ 1 lần", facts);
    }

    [Fact]
    public void Khong_phan_nan_khong_huy_thi_khong_them_dong_thua()
    {
        var facts = MeetingBriefService.BuildFacts(Cust(), null, Array.Empty<MailItem>());
        Assert.DoesNotContain("phàn nàn", facts);
    }

    [Fact]
    public void Canh_bao_cua_ban_cham_duoc_dua_vao()
    {
        var facts = MeetingBriefService.BuildFacts(
            Cust(), Review(alertLevel: "high", alertMsg: "6 tháng không mua lại"), Array.Empty<MailItem>());
        Assert.Contains("6 tháng không mua lại", facts);
    }

    /// Mức "none" nghĩa là KHÔNG có cảnh báo — in ra thành một dòng cảnh báo là báo động giả.
    [Fact]
    public void Muc_canh_bao_none_thi_khong_in_thanh_canh_bao()
    {
        var facts = MeetingBriefService.BuildFacts(
            Cust(), Review(alertLevel: "none", alertMsg: "không có gì"), Array.Empty<MailItem>());
        Assert.DoesNotContain("Cảnh báo", facts);
    }

    // ── Bản chấm hạng ─────────────────────────────────────────────────────────────

    [Fact]
    public void Hang_kem_ly_do_va_diem_can_luu_y()
    {
        var facts = MeetingBriefService.BuildFacts(
            Cust(),
            Review(rank: "A", rankReason: "Chi nhiều, mua đều",
                   concerns: new() { "Hay mặc cả", "Đổi lịch nhiều" },
                   preferences: "Thích tour biển",
                   actionTask: "Gọi mời tour Nhật tháng 9",
                   products: new() { "Tour Nhật" }),
            Array.Empty<MailItem>());

        Assert.Contains("Hạng đã chấm: A — Chi nhiều, mua đều", facts);
        Assert.Contains("Hay mặc cả", facts);
        Assert.Contains("Thích tour biển", facts);
        Assert.Contains("Gọi mời tour Nhật tháng 9", facts);
        Assert.Contains("Tour Nhật", facts);
    }

    [Fact]
    public void Chua_cham_hang_thi_khong_in_dong_hang()
    {
        var facts = MeetingBriefService.BuildFacts(Cust(), review: null, mails: Array.Empty<MailItem>());
        Assert.DoesNotContain("Hạng đã chấm", facts);
    }

    // ── Lịch sử mua + chăm sóc ────────────────────────────────────────────────────

    [Fact]
    public void Tour_da_mua_in_du_diem_den_va_so_khach()
    {
        var facts = MeetingBriefService.BuildFacts(
            Cust(purchases: new() { new("01/06/2026", "Đà Nẵng", 3, 4, 20_000_000, "Hotline") }),
            null, Array.Empty<MailItem>());
        Assert.Contains("Đà Nẵng", facts);
        Assert.Contains("3 đêm", facts);
        Assert.Contains("4 khách", facts);
        Assert.Contains("20.000.000đ", facts);
    }

    /// Upstream trả lẫn ISO và dd/MM/yyyy. Để lẫn thì AI chép nguyên ISO vào lời khuyên — chạy thật
    /// đã ra một câu chứa cả "2026-08-08" lẫn "04/08/2026".
    [Fact]
    public void Ngay_ISO_duoc_doi_ve_dang_Viet()
    {
        var facts = MeetingBriefService.BuildFacts(
            Cust(metrics: Metrics(last: "2026-08-08T00:00:00"),
                 purchases: new() { new("2026-08-04T00:00:00", "Nha Trang", 2, 2, 5_000_000, null) }),
            null, Array.Empty<MailItem>());
        Assert.Contains("08/08/2026", facts);
        Assert.Contains("04/08/2026", facts);
        Assert.DoesNotContain("2026-08-", facts);
    }

    [Fact]
    public void Ngay_da_dung_dang_Viet_thi_giu_nguyen()
    {
        var facts = MeetingBriefService.BuildFacts(
            Cust(metrics: Metrics(last: "01/06/2026")), null, Array.Empty<MailItem>());
        Assert.Contains("01/06/2026", facts);
    }

    /// 0 đêm / 0 khách nghĩa là CRM chưa ghi, KHÔNG phải "tour 0 đêm". Chạy thật AI đã bám vào đó mà
    /// khuyên "cần kiểm tra thêm" — một lời khuyên sinh ra từ chỗ trống trong dữ liệu.
    [Fact]
    public void So_0_khong_in_ra_vi_nghia_la_chua_ghi()
    {
        var facts = MeetingBriefService.BuildFacts(
            Cust(purchases: new() { new("01/06/2026", "Đà Lạt", 0, 0, 3_000_000, null) }),
            null, Array.Empty<MailItem>());
        Assert.DoesNotContain("0 đêm", facts);
        Assert.DoesNotContain("0 khách", facts);
        Assert.Contains("Đà Lạt", facts);
    }

    /// Nhân viên chỉ có một phút trước khi vào gặp — 20 dòng lịch sử là không đọc kịp.
    [Fact]
    public void Chi_lay_5_lan_gan_nhat_cho_moi_nhom()
    {
        var purchases = Enumerable.Range(1, 9)
            .Select(i => new TourPurchase($"0{i}/01/2026", $"Điểm{i}", 2, 2, 1_000_000, null))
            // 9 dòng, chỉ 5 dòng đầu được giữ.
            .ToList();
        var facts = MeetingBriefService.BuildFacts(Cust(purchases: purchases), null, Array.Empty<MailItem>());
        Assert.Contains("Điểm5", facts);
        Assert.DoesNotContain("Điểm6", facts);
    }

    [Fact]
    public void Nhat_ky_cham_soc_kem_thai_do_va_ket_qua()
    {
        var facts = MeetingBriefService.BuildFacts(
            Cust(careLogs: new() { new("10/08/2026", "call", "Hỏi giá tour Hàn", "negative", "Chê đắt") }),
            null, Array.Empty<MailItem>());
        Assert.Contains("Hỏi giá tour Hàn", facts);
        Assert.Contains("negative", facts);
        Assert.Contains("Chê đắt", facts);
    }

    // ── Thư ───────────────────────────────────────────────────────────────────────

    /// "Khiếu nại" + "Mới" nghĩa là khách gửi khiếu nại mà CHƯA ai trả lời — không nên để khách
    /// nhắc trước, nên nhãn phải là tiếng Việt đọc được ngay chứ không phải mã `khieu_nai`.
    [Fact]
    public void Thu_hien_nhan_tieng_Viet_cua_nhom_va_trang_thai()
    {
        var facts = MeetingBriefService.BuildFacts(
            Cust(), null, new[] { Mail("Phàn nàn về HDV", category: "khieu_nai", status: "moi") });
        Assert.Contains("Khiếu nại", facts);
        Assert.Contains("Mới", facts);
        Assert.Contains("Phàn nàn về HDV", facts);
        Assert.DoesNotContain("khieu_nai", facts);
    }

    [Fact]
    public void Ngay_thu_in_dang_ngan_khong_phai_chuoi_ISO()
    {
        var facts = MeetingBriefService.BuildFacts(
            Cust(), null, new[] { Mail("Hỏi tour", received: "2026-08-10T03:00:00Z") });
        Assert.Contains("10/08/2026", facts);
        Assert.DoesNotContain("2026-08-10T", facts);
    }

    // ── Cắt chuỗi ─────────────────────────────────────────────────────────────────

    /// Ghi chú tự do có thể dài cả trang; cắt để không đẩy prompt phình ra, nhưng phải báo là đã cắt.
    [Fact]
    public void Ghi_chu_qua_dai_thi_cat_va_danh_dau()
    {
        var facts = MeetingBriefService.BuildFacts(
            Cust(note: new string('x', 500)), null, Array.Empty<MailItem>());
        Assert.Contains("…", facts);
        Assert.DoesNotContain(new string('x', 400), facts);
    }

    /// Xuống dòng trong ghi chú sẽ phá cấu trúc "mỗi dòng một dữ kiện" mà cả prompt lẫn thẻ UI dựa vào.
    [Fact]
    public void Xuong_dong_trong_ghi_chu_bi_dep_phang()
    {
        var facts = MeetingBriefService.BuildFacts(
            Cust(note: "Dòng một\nDòng hai"), null, Array.Empty<MailItem>());
        var noteLine = facts.Split('\n').Single(l => l.Contains("Dòng một"));
        Assert.Contains("Dòng hai", noteLine);
    }

    // ── Prompt ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Prompt_chua_ten_khach_va_toan_bo_du_kien()
    {
        var facts = MeetingBriefService.BuildFacts(Cust(), null, Array.Empty<MailItem>());
        var prompt = MeetingBriefService.BuildPrompt(Cust(), facts);
        Assert.Contains("Anh Nam", prompt);
        Assert.Contains(facts, prompt);
    }

    /// Ràng buộc quan trọng nhất của prompt: AI viết lời, KHÔNG được đẻ thêm số/tên tour.
    [Fact]
    public void Prompt_cam_bia_them_du_lieu()
    {
        var prompt = MeetingBriefService.BuildPrompt(Cust(), "- Khách: Anh Nam");
        Assert.Contains("không bịa", prompt);
    }

    // ── Dự phòng khi AI hỏng ──────────────────────────────────────────────────────

    [Fact]
    public void Ban_du_phong_giu_nguyen_du_kien()
    {
        var facts = MeetingBriefService.BuildFacts(Cust(), null, Array.Empty<MailItem>());
        var fallback = MeetingBriefService.RenderFallback(facts);
        Assert.Contains(facts, fallback);
        Assert.Contains("Anh Nam", fallback);
    }
}
