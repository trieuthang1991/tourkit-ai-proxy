using TourkitAiProxy.Services.Digest;

namespace TourkitAiProxy.Tests.Digest;

/// <summary>
/// Thư nhắc thu tiền phải đi theo NGƯỜI PHỤ TRÁCH, giống hệt luật của Bảng tin.
///
/// <para>Bối cảnh (20/08/2026): Bảng tin ghi đích danh (<c>ResolveOwner</c>) nhưng email lại gửi
/// cho MỌI người đã bật email — cùng một cảnh báo mà hai cách chọn người nhận. Đo trên staging:
/// một lá thư chứa 3 tour của 2 người phụ trách khác nhau, gửi tới một người thứ ba. Thư có tên
/// khách và số tiền còn thiếu, nên đó không chỉ là gửi thừa.</para>
///
/// <para>Lỗi kiểu này không làm gãy gì: gửi vẫn báo thành công, người nhận vẫn có thư. Chỉ khi
/// ngồi đọc kỹ mới thấy tour trong thư không phải của mình. Nên khoá bằng test.</para>
/// </summary>
public class PaymentMailPlanTests
{
    private static PaymentAlert A(int tourId, string? seller) => new(
        TourId: tourId, Title: $"Tour {tourId}", CustomerName: "Khách", SellerName: seller,
        Outstanding: 1_000_000m, DepartureDate: new DateTime(2026, 8, 22), DaysLeft: 2,
        Severity: 2, AlertKey: $"payment:{tourId}", SellerUserName: seller);

    private static Dictionary<string, string> Emails(params (string User, string Mail)[] xs)
        => xs.ToDictionary(x => x.User, x => x.Mail, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Moi_nguoi_phu_trach_mot_thu_chi_chua_tour_cua_ho()
    {
        var alerts = new[] { A(1, "an"), A(2, "hoa"), A(3, "hoa") };
        var mails = PaymentWatchdogRule.PlanMails(alerts, true,
            Emails(("an", "an@x.vn"), ("hoa", "hoa@x.vn"))).Mails;

        Assert.Equal(2, mails.Count);
        var an = mails.Single(m => m.ToEmail == "an@x.vn");
        var hoa = mails.Single(m => m.ToEmail == "hoa@x.vn");

        Assert.Equal(new[] { 1 }, an.Alerts.Select(a => a.TourId));
        Assert.Equal(new[] { 2, 3 }, hoa.Alerts.Select(a => a.TourId));
    }

    [Fact]
    public void Nguoi_ngoai_cuoc_bat_email_KHONG_nhan_tour_cua_nguoi_khac()
    {
        // Đây chính là lỗi đã xảy ra: "ai bật email thì gửi" nên người thứ ba đọc được tour
        // của hai người kia, kèm tên khách và số tiền.
        var alerts = new[] { A(1, "an"), A(2, "hoa") };
        var mails = PaymentWatchdogRule.PlanMails(alerts, true,
            Emails(("an", "an@x.vn"), ("hoa", "hoa@x.vn"), ("ngoai", "ngoai@x.vn"))).Mails;

        Assert.DoesNotContain(mails, m => m.ToEmail == "ngoai@x.vn");
    }

    [Fact]
    public void Phu_trach_chua_khai_email_thi_DEM_chu_khong_gui_cho_nguoi_khac_thay()
    {
        var alerts = new[] { A(1, "an"), A(2, "chuakhai") };
        var r = PaymentWatchdogRule.PlanMails(alerts, true, Emails(("an", "an@x.vn")));

        // Không ai nhận thay tour số 2.
        Assert.Single(r.Mails);
        Assert.Equal(new[] { 1 }, r.Mails[0].Alerts.Select(a => a.TourId));
        // Nhưng cũng không im lặng — có tên để tóm tắt lần chạy nói ra.
        Assert.Equal(new[] { "chuakhai" }, r.OwnersWithoutEmail);
    }

    [Fact]
    public void Api_chua_nang_cap_thi_giu_hanh_vi_cu_gui_ca_cong_ty()
    {
        // apiHasSellerField=false: chưa có căn cứ chia người. Im lặng còn tệ hơn gửi rộng.
        var alerts = new[] { A(1, null), A(2, null) };
        var mails = PaymentWatchdogRule.PlanMails(alerts, false,
            Emails(("an", "an@x.vn"), ("hoa", "hoa@x.vn"))).Mails;

        Assert.Equal(2, mails.Count);
        Assert.All(mails, m => Assert.Equal(new[] { 1, 2 }, m.Alerts.Select(a => a.TourId)));
    }

    [Fact]
    public void Dia_chi_go_tay_nhan_ban_day_du()
    {
        // Ô "Gửi thêm tới" là người dùng CHỦ Ý gõ (kế toán/quản lý) → nhận trọn danh sách.
        var alerts = new[] { A(1, "an"), A(2, "hoa") };
        var mails = PaymentWatchdogRule.PlanMails(alerts, true,
            Emails(("an", "an@x.vn")), new[] { "ketoan@x.vn" }).Mails;

        var ketoan = mails.Single(m => m.ToEmail == "ketoan@x.vn");
        Assert.Equal(new[] { 1, 2 }, ketoan.Alerts.Select(a => a.TourId));
    }

    [Fact]
    public void Vua_la_phu_trach_vua_go_tay_thi_chi_MOT_thu()
    {
        var alerts = new[] { A(1, "an"), A(2, "hoa") };
        var mails = PaymentWatchdogRule.PlanMails(alerts, true,
            Emails(("an", "an@x.vn")), new[] { "an@x.vn" }).Mails;

        var an = Assert.Single(mails, m => m.ToEmail == "an@x.vn");
        // Gộp chứ không nhân đôi dòng tour 1.
        Assert.Equal(new[] { 1, 2 }, an.Alerts.Select(a => a.TourId).OrderBy(x => x));
    }

    [Fact]
    public void Khong_ai_khai_email_thi_khong_co_thu_nao()
    {
        var r = PaymentWatchdogRule.PlanMails(new[] { A(1, "an") }, true,
            new Dictionary<string, string>());

        Assert.Empty(r.Mails);
        Assert.Equal(new[] { "an" }, r.OwnersWithoutEmail);
    }
}
