using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Mail;
using Xunit;

namespace TourkitAiProxy.Tests.Mail;

/// Đọc lại nội dung thư đã lưu (sau khi bản bóc thư được sửa).
///
/// Bối cảnh: bản sửa 14/08 cho thư chuyển tiếp + tệp đính kèm chỉ áp dụng lúc BÓC thư, nên thư đã nằm
/// trong hộp vẫn giữ nội dung hỏng — mà đó đúng là những thư người dùng đang nhìn. Cần đường kéo lại
/// từ IMAP và ghi đè nội dung.
///
/// Điều nguy hiểm nhất ở đây KHÔNG phải chuyện nội dung, mà là làm mất việc đang làm dở: nháp trả lời
/// đang soạn, trạng thái "đang xử lý", nhóm đã phân loại. Đó là lý do phép gộp được tách riêng và
/// khoá lại bằng test.
public class MailContentRefreshTests
{
    private static MailItem Existing() => new(
        Id: "m1",
        From: new MailContact("Khách", "khach@example.com"),
        Subject: "Fwd: Báo giá",
        Body: "",                                   // nội dung hỏng: rỗng vì bản bóc cũ
        ReceivedAt: "2026-08-01T00:00:00Z",
        IsRead: true,
        Category: "xin_bao_gia",                    // AI đã phân loại
        Status: "dang_xu_ly",                       // nhân viên đang xử lý
        AiSummary: "Khách hỏi báo giá tour Nhật",
        Draft: new MailDraft("lich_su", "giảm 5%", "Dạ em gửi anh báo giá…", "2026-08-02T00:00:00Z"),
        BodyHtml: null,
        AutoReplyError: null);

    private static MailItem Fetched() => new(
        Id: "m1",
        From: new MailContact("Khách", "khach@example.com"),
        Subject: "Fwd: Báo giá",
        Body: "Nội dung thư gốc đã đọc được + 📎 Tệp đính kèm: baogia.pdf",
        ReceivedAt: "2026-08-01T00:00:00Z",
        IsRead: false,                              // cờ \Seen lúc kéo lại có thể khác
        Category: null,                             // bản kéo mới CHƯA phân loại
        Status: "moi",
        AiSummary: null,
        Draft: null,
        BodyHtml: "<p>Nội dung thư gốc</p>",
        AutoReplyError: null);

    [Fact]
    public void Lay_noi_dung_moi()
    {
        var m = MailSyncService.MergeForContentRefresh(Existing(), Fetched());
        Assert.Contains("Nội dung thư gốc", m.Body);
        Assert.Contains("baogia.pdf", m.Body);
        Assert.Equal("<p>Nội dung thư gốc</p>", m.BodyHtml);
    }

    /// Đè nháp là làm mất công nhân viên đã ngồi soạn — tệ hơn nhiều so với cái đang định chữa.
    [Fact]
    public void Giu_nguyen_nhap_dang_soan()
    {
        var m = MailSyncService.MergeForContentRefresh(Existing(), Fetched());
        Assert.NotNull(m.Draft);
        Assert.Equal("Dạ em gửi anh báo giá…", m.Draft!.Text);
        Assert.Equal("giảm 5%", m.Draft.Instruction);
    }

    [Fact]
    public void Giu_nguyen_trang_thai_xu_ly()
    {
        var m = MailSyncService.MergeForContentRefresh(Existing(), Fetched());
        Assert.Equal("dang_xu_ly", m.Status);
    }

    /// Giữ nhóm đã phân loại → đọc lại nội dung KHÔNG tốn lượt AI nào.
    [Fact]
    public void Giu_nguyen_nhom_va_tom_tat_nen_khong_ton_luot_AI()
    {
        var m = MailSyncService.MergeForContentRefresh(Existing(), Fetched());
        Assert.Equal("xin_bao_gia", m.Category);
        Assert.Equal("Khách hỏi báo giá tour Nhật", m.AiSummary);
    }

    /// Thư đã đọc mà quay lại thành chưa đọc thì chuông báo nhảy số vô cớ.
    [Fact]
    public void Giu_nguyen_da_doc_hay_chua()
    {
        var m = MailSyncService.MergeForContentRefresh(Existing(), Fetched());
        Assert.True(m.IsRead);
    }

    [Fact]
    public void Khong_dung_toi_dinh_danh_va_nguoi_gui()
    {
        var m = MailSyncService.MergeForContentRefresh(Existing(), Fetched());
        Assert.Equal("m1", m.Id);
        Assert.Equal("khach@example.com", m.From.Email);
        Assert.Equal("Fwd: Báo giá", m.Subject);
    }
}
