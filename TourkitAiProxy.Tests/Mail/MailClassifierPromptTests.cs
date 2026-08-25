using TourkitAiProxy.Domain.Models;
using TourkitAiProxy.Services.Mail;
using Xunit;
using TourkitAiProxy.Domain.Mail;

namespace TourkitAiProxy.Tests.Mail;

/// Prompt phân loại thư. Không gọi AI — chỉ kiểm phần dựng prompt, thứ quyết định model trả lời có
/// giống nhau giữa hai lần hay không.
///
/// Bối cảnh: soát 1.215 thư thật (14/08) thấy cùng một tiêu đề rơi vào các nhóm khác nhau. Gốc là
/// prompt cũ chỉ liệt kê TÊN nhóm, không định nghĩa và không có luật gỡ hoà.
public class MailClassifierPromptTests
{
    private static MailItem Mail(string subject, string body = "nội dung", string from = "ai@do.com")
        => new(Id: "m1", From: new MailContact("Người gửi", from), Subject: subject, Body: body,
               ReceivedAt: "2026-08-14T00:00:00Z", IsRead: false, Category: null,
               Status: "moi", AiSummary: null, Draft: null);

    // ── Taxonomy: định nghĩa phải phủ hết nhóm ────────────────────────────────────

    /// Thêm nhóm mới mà quên định nghĩa thì prompt sẽ ném KeyNotFound lúc chạy — mà chạy ở đây nghĩa
    /// là giữa lúc đồng bộ hộp thư của khách.
    [Fact]
    public void Moi_nhom_deu_co_dinh_nghia()
    {
        foreach (var key in MailTaxonomy.Categories.Keys)
        {
            Assert.True(MailTaxonomy.CategoryHints.ContainsKey(key), $"Nhóm '{key}' chưa có định nghĩa");
            Assert.False(string.IsNullOrWhiteSpace(MailTaxonomy.CategoryHints[key]));
        }
    }

    [Fact]
    public void Khong_co_dinh_nghia_thua_cho_nhom_khong_ton_tai()
    {
        foreach (var key in MailTaxonomy.CategoryHints.Keys)
            Assert.True(MailTaxonomy.Categories.ContainsKey(key), $"Định nghĩa thừa cho nhóm '{key}'");
    }

    // ── Prompt phải mang đủ dữ kiện để quyết định ─────────────────────────────────

    [Fact]
    public void Prompt_kem_dinh_nghia_chu_khong_chi_ten_nhom()
    {
        var p = MailClassifier.BuildPromptJson(Mail("Hỏi tour Đà Nẵng"));
        foreach (var kv in MailTaxonomy.CategoryHints)
        {
            Assert.Contains(kv.Key, p);
            Assert.Contains(kv.Value, p);
        }
    }

    [Fact]
    public void Prompt_kem_luat_go_hoa()
    {
        var p = MailClassifier.BuildPromptJson(Mail("Thông báo có công việc mới được giao"));
        Assert.Contains(MailTaxonomy.TieBreakRules, p);
    }

    /// Luật gỡ hoà là thứ trực tiếp dập lỗi đo được (thư máy-gửi lắc giữa spam/khac).
    /// Mất chúng là quay về đúng trạng thái hỏng cũ, nên chốt lại bằng test.
    [Fact]
    public void Luat_go_hoa_phai_noi_ro_thu_noi_bo_khong_phai_spam()
    {
        Assert.Contains("KHÔNG phải 'spam'", MailTaxonomy.TieBreakRules);
        Assert.Contains("chọn 'khac'", MailTaxonomy.TieBreakRules);
    }

    /// <summary>
    /// Chốt chặn CHỐNG SỬA QUÁ TAY. Bản luật đầu tiên viết "thư máy gửi từ dịch vụ đang dùng → không
    /// bao giờ spam", chạy thử trên dữ liệu thật thì **quảng cáo Grab cũng thoát khỏi spam** — vì
    /// quảng cáo Grab đúng là máy gửi từ dịch vụ đang dùng. Luật phải phân biệt theo MỤC ĐÍCH thư
    /// (thông báo vs chào mời), không theo người gửi; mất vế đó là nhóm 'spam' rỗng dần trong im lặng.
    /// </summary>
    [Fact]
    public void Luat_go_hoa_khong_duoc_xoa_so_nhom_spam()
    {
        Assert.Contains("MỤC ĐÍCH", MailTaxonomy.TieBreakRules);
        Assert.Contains("KHÔNG xét người gửi", MailTaxonomy.TieBreakRules);
        // Vế quyết định: quảng cáo VẪN là spam dù đến từ dịch vụ công ty đang dùng.
        Assert.Contains("kể cả khi đến từ dịch vụ công ty đang dùng", MailTaxonomy.TieBreakRules);
    }

    /// Thông báo nội bộ từng bị xếp 'xac_nhan' (nhóm dành cho giao dịch của KHÁCH) — 'phiếu thu mới'
    /// rải 3 nhóm. Luật 3 phải nêu đích danh mấy tiêu đề đó.
    [Fact]
    public void Luat_go_hoa_tach_thong_bao_noi_bo_khoi_xac_nhan()
    {
        Assert.Contains("xac_nhan", MailTaxonomy.TieBreakRules);
        Assert.Contains("phiếu thu", MailTaxonomy.TieBreakRules);
    }

    [Fact]
    public void Prompt_van_co_tieu_de_va_nguoi_gui_cua_thu()
    {
        var p = MailClassifier.BuildPromptJson(Mail("Xin báo giá tour Nhật", from: "khach@gmail.com"));
        Assert.Contains("Xin báo giá tour Nhật", p);
        Assert.Contains("khach@gmail.com", p);
    }

    /// Thư quảng cáo có thể dài cả chục nghìn ký tự — cắt để khỏi đốt token, nhưng phải báo là đã cắt
    /// để model không tưởng thư kết thúc giữa chừng.
    [Fact]
    public void Noi_dung_qua_dai_thi_cat_va_danh_dau()
    {
        var p = MailClassifier.BuildPromptJson(Mail("Quảng cáo", body: new string('x', 5000)));
        Assert.Contains("(cắt)", p);
        Assert.DoesNotContain(new string('x', 2500), p);
    }
}
