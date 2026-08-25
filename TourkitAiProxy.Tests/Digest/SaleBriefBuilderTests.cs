using Xunit;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Tests.Digest;

/// Dựng nội dung bản tin cho nhân viên bán hàng — RULE THUẦN, không gọi AI.
/// Yêu cầu quan trọng: bản tin KHÔNG BAO GIỜ rỗng (vẫn gửi để giữ thói quen mở đọc),
/// và mỗi mục cắt top 5 để không thành bức tường chữ.
public class SaleBriefBuilderTests
{
    private static readonly DateTime Today = new(2026, 8, 11);

    private static SaleBriefInput Empty(string user = "sale1") => new(user, "Nguyễn A",
        new(), new(), new(), new(), 0, 0, new(), new(), MailSourceOk: true);

    [Fact]
    public void Ban_tin_rong_van_co_loi_chuc()
    {
        var m = SaleBriefBuilder.Build(Empty(), Today);
        Assert.Contains("chưa có việc gấp", m.BodyMarkdown);
        Assert.Equal(BriefTypes.Sale, m.Kind);
    }

    [Fact]
    public void Deal_nguoi_hien_ten_va_so_ngay()
    {
        var input = Empty() with { CoolingDeals = new() { new DealLine(9, "Tour Đà Nẵng", "Anh Tú", 70, 6, "Đang tư vấn") } };
        var m = SaleBriefBuilder.Build(input, Today);
        Assert.Contains("Tour Đà Nẵng", m.BodyMarkdown);
        Assert.Contains("6 ngày", m.BodyMarkdown);
        Assert.Contains("70%", m.BodyMarkdown);
    }

    [Fact]
    public void Top_5_moi_muc_khong_tran()
    {
        var deals = Enumerable.Range(1, 9)
            .Select(i => new DealLine(i, $"Deal {i}", null, 50, i, null)).ToList();
        var m = SaleBriefBuilder.Build(Empty() with { CoolingDeals = deals }, Today);
        Assert.Contains("Deal 5", m.BodyMarkdown);
        Assert.DoesNotContain("Deal 6", m.BodyMarkdown);
        Assert.Contains("và 4", m.BodyMarkdown);
    }

    [Fact]
    public void Nguon_mail_loi_ghi_na()
    {
        var m = SaleBriefBuilder.Build(Empty() with { MailSourceOk = false, TenantMailPending = 0 }, Today);
        Assert.Contains("Hộp thư: n/a", m.BodyMarkdown);
    }

    [Fact]
    public void Payment_alert_cua_toi_xuat_hien()
    {
        var input = Empty() with { MyPaymentAlerts = new() {
            new PaymentAlert(3, "Tour Huế", "Chị Lan", "Sale B", 5_000_000m, Today.AddDays(2), 2, 2, "payment:3") } };
        var m = SaleBriefBuilder.Build(input, Today);
        Assert.Contains("Tour Huế", m.BodyMarkdown);
        Assert.Contains("5.000.000", m.BodyMarkdown.Replace(",", "."));   // định dạng tiền vi-VN
    }

    [Fact]
    public void Tieu_de_co_ngay_va_ten()
    {
        var m = SaleBriefBuilder.Build(Empty(), Today);
        Assert.Contains("11/08", m.Title);
        Assert.Contains("Nguyễn A", m.Title);
    }

    // ── Ca thêm ngoài plan ────────────────────────────────────────────────────

    [Fact]
    public void Co_viec_thi_KHONG_con_loi_chuc_rong()
    {
        // Vừa có việc gấp vừa chúc "chưa có việc gấp" là tự mâu thuẫn.
        var input = Empty() with { CoolingDeals = new() { new DealLine(1, "D", null, 50, 3, null) } };
        var m = SaleBriefBuilder.Build(input, Today);
        Assert.DoesNotContain("chưa có việc gấp", m.BodyMarkdown);
    }

    [Fact]
    public void Chi_co_dong_hop_thu_van_tinh_la_ban_tin_rong()
    {
        // Dòng hộp thư LUÔN có nên không được tính là "có việc" — nếu tính thì lời chúc
        // không bao giờ xuất hiện, và người dùng mất tín hiệu "hôm nay rảnh".
        var m = SaleBriefBuilder.Build(Empty() with { TenantMailPending = 12, TenantMailQuoteRequests = 3 }, Today);
        Assert.Contains("chưa có việc gấp", m.BodyMarkdown);
        Assert.Contains("12 thư", m.BodyMarkdown);
    }

    [Fact]
    public void HTML_phai_vo_hieu_hoa_the_nguoi_dung_go_vao()
    {
        // Tên khách/tiêu đề deal là dữ liệu người dùng nhập — lọt vào email dạng thẻ thật là XSS.
        var input = Empty() with { CoolingDeals = new() {
            new DealLine(1, "<script>alert(1)</script>", "<b>Lan</b>", 50, 3, null) } };
        var m = SaleBriefBuilder.Build(input, Today);
        Assert.DoesNotContain("<script>", m.BodyHtml);
        Assert.Contains("&lt;script&gt;", m.BodyHtml);
    }

    [Fact]
    public void HTML_van_giu_in_dam_cua_minh()
    {
        // Escape phải làm TRƯỚC khi đổi **x** → thẻ <b> mình tạo không bị escape theo.
        var input = Empty() with { CoolingDeals = new() { new DealLine(1, "Tour X", null, 50, 3, null) } };
        var m = SaleBriefBuilder.Build(input, Today);
        Assert.Contains("<b>Tour X</b>", m.BodyHtml);
    }

    [Fact]
    public void Muc_don_du_5_thi_khong_ghi_va_N_khac()
    {
        var deals = Enumerable.Range(1, 5).Select(i => new DealLine(i, $"D{i}", null, 50, i, null)).ToList();
        var m = SaleBriefBuilder.Build(Empty() with { CoolingDeals = deals }, Today);
        Assert.DoesNotContain("và 0", m.BodyMarkdown);
    }

    [Fact]
    public void Nhieu_muc_cung_luc_deu_xuat_hien()
    {
        var input = Empty() with
        {
            CoolingDeals = new() { new DealLine(1, "Deal A", null, 60, 4, null) },
            TodayAppointments = new() { new ApptLine("09:30", "Tư vấn tour Nhật", "Chị Mai") },
            SleepingVips = new() { new CustomerLine("Anh Hùng", "A", 75) },
            StaleQuotes = new() { new QuoteLine("Báo giá Hàn 5N4Đ", "Anh Long", 9) },
            HygieneDeals = new() { new DealLine(2, "Deal B", null, 30, 20, "Chờ xử lý") },
        };
        var m = SaleBriefBuilder.Build(input, Today);
        foreach (var s in new[] { "Deal A", "09:30", "Anh Hùng", "Báo giá Hàn", "Deal B" })
            Assert.Contains(s, m.BodyMarkdown);
    }

    [Fact]
    public void Viec_can_lam_hien_va_danh_dau_tre_han()
    {
        var input = Empty() with
        {
            TodayTasks = new() { new TaskLine("Gửi hợp đồng đoàn Nhật", "Cao", true),
                                 new TaskLine("Gọi lại khách Đà Nẵng", "Trung bình", false) },
            OverdueTaskCount = 1,
        };
        var md = SaleBriefBuilder.Build(input, Today).BodyMarkdown;
        Assert.Contains("Việc cần làm hôm nay (2)", md);
        Assert.Contains("1 trễ hạn", md);
        Assert.Contains("⚠️ Gửi hợp đồng", md);          // việc trễ có dấu
        Assert.Contains("ưu tiên cao", md);
        Assert.DoesNotContain("⚠️ Gọi lại", md);         // việc chưa trễ thì không
    }

    [Fact]
    public void Khong_co_viec_thi_khong_hien_muc_do()
    {
        var md = SaleBriefBuilder.Build(Empty(), Today).BodyMarkdown;
        Assert.DoesNotContain("Việc cần làm", md);
    }

    [Fact]
    public void Lich_hen_qua_han_ghi_ngay_tren_tieu_de_muc()
    {
        var input = Empty() with
        {
            TodayAppointments = new() { new ApptLine("09:00", "Tư vấn", "Anh A") },
            OverdueAppointments = 2,
        };
        Assert.Contains("Lịch hẹn hôm nay (1) · 2 quá hạn", SaleBriefBuilder.Build(input, Today).BodyMarkdown);
    }

    [Fact]
    public void Chi_co_viec_can_lam_cung_KHONG_con_la_ngay_ranh()
    {
        var input = Empty() with { TodayTasks = new() { new TaskLine("Việc A", null, false) } };
        Assert.DoesNotContain("chưa có việc gấp", SaleBriefBuilder.Build(input, Today).BodyMarkdown);
    }

    // 4 ca them sau khi chay THAT va doc ban tin: so lieu dung nhung kho dung

    [Fact]
    public void Chua_AI_cham_thi_KHONG_in_kha_nang_chot_0()
    {
        // 0% nghia la CHUA AI cham, khong phai "vo vong". In ra lam nguoi doc hieu nguoc.
        var input = Empty() with { CoolingDeals = new() { new DealLine(1, "Tour X", "Anh A", 0, 9, null) } };
        var md = SaleBriefBuilder.Build(input, Today).BodyMarkdown;
        Assert.DoesNotContain("khả năng chốt", md);
        Assert.Contains("im lặng 9 ngày", md);
    }

    [Fact]
    public void Tieu_de_rong_thi_roi_ve_ten_khach()
    {
        // Du lieu that co co hoi khong dat tieu de -> truoc in ra "**** - Ten khach".
        var input = Empty() with { CoolingDeals = new() { new DealLine(1, "", "Nguyễn Hạnh", 60, 5, null) } };
        var md = SaleBriefBuilder.Build(input, Today).BodyMarkdown;
        Assert.Contains("**Nguyễn Hạnh**", md);
        Assert.DoesNotContain("****", md);
    }

    [Fact]
    public void Uu_tien_la_dau_gach_thi_bo_han()
    {
        var input = Empty() with { TodayTasks = new() { new TaskLine("Việc A", "—", false) } };
        Assert.DoesNotContain("ưu tiên", SaleBriefBuilder.Build(input, Today).BodyMarkdown);
    }

    [Fact]
    public void Tat_ca_deu_tre_thi_noi_thang_thay_vi_dem_so()
    {
        var tasks = Enumerable.Range(1, 4).Select(i => new TaskLine($"V{i}", null, true)).ToList();
        var input = Empty() with { TodayTasks = tasks, OverdueTaskCount = 4 };
        var md = SaleBriefBuilder.Build(input, Today).BodyMarkdown;
        Assert.Contains("TẤT CẢ đều trễ hạn", md);
        Assert.DoesNotContain("4 trễ hạn", md);
    }

    [Fact]
    public void Ten_rong_thi_dung_username()
    {
        var m = SaleBriefBuilder.Build(new SaleBriefInput("sale9", null,
            new(), new(), new(), new(), 0, 0, new(), new(), true), Today);
        Assert.Contains("sale9", m.Title);
    }

    // ── Lưới an toàn: AI được chọn lọc, người đọc KHÔNG được mất dấu một nhóm ──────────
    //
    // Đã gặp thật: 61 việc cần làm (có dòng "ưu tiên cao") bị AI lược sạch khỏi bản tin, mà dòng
    // tổng kết khi đó chỉ phủ 4/7 nhóm — thiếu đúng lịch hẹn và tour thiếu tiền, hai nhóm "trễ một
    // ngày là mất". Bị lược mà tổng kết cũng câm thì người đọc tưởng hôm nay không có gì.

    private static SaleBriefInput DuMoiNhom() => Empty() with
    {
        CoolingDeals = new() { new DealLine(1, "Tour Huế", "Chị Lan", 60, 4, "Đang tư vấn") },
        TodayAppointments = new() { new ApptLine("09:00", "Gặp khách", "Anh Nam") },
        SleepingVips = new() { new CustomerLine("Chị Mai", "A", 90) },
        StaleQuotes = new() { new QuoteLine("Báo giá Phú Quốc", "Anh Hùng", 7) },
        HygieneDeals = new() { new DealLine(2, "Tour Sapa", "Chị Hoa", 30, 20, "Kẹt") },
        MyPaymentAlerts = new() { new PaymentAlert(5, "Tour Nhật", "Anh Sơn", "sale1",
            12_000_000m, Today.AddDays(3), 3, 2, "k5") },
        TodayTasks = new() { new TaskLine("Hotfix 14/8", "Cao", true) },
        OverdueTaskCount = 61,
    };

    [Fact]
    public void AI_luoc_het_thi_dong_tong_ket_van_giu_dau_MOI_nhom()
    {
        // AI trả về một câu chẳng nhắc nhóm nào — trường hợp xấu nhất.
        var m = SaleBriefBuilder.WrapAiReply("Sáng nay tập trung chăm khách.", DuMoiNhom(), Today);
        var md = m.BodyMarkdown;

        Assert.Contains("1 lịch hẹn", md);
        Assert.Contains("1 tour thiếu tiền", md);
        Assert.Contains("1 cơ hội cần gọi", md);
        Assert.Contains("1 việc (61 trễ)", md);
        Assert.Contains("1 báo giá bỏ dở", md);
        Assert.Contains("1 cơ hội cần dọn", md);
        Assert.Contains("1 khách quen lâu chưa mua", md);
    }

    /// Hai nhóm "trễ một ngày là mất" phải đứng ĐẦU dòng tổng kết — người đọc lướt chỉ thấy vài chữ
    /// đầu, để cơ hội/khách quen lên trước thì phần khẩn bị đẩy xuống cuối.
    [Fact]
    public void Nhom_khan_dung_dau_dong_tong_ket()
    {
        var md = SaleBriefBuilder.WrapAiReply("x", DuMoiNhom(), Today).BodyMarkdown;
        var i = md.IndexOf("Đang có tổng cộng", StringComparison.Ordinal);
        var dong = md[i..];
        Assert.True(dong.IndexOf("lịch hẹn", StringComparison.Ordinal)
                  < dong.IndexOf("cơ hội cần gọi", StringComparison.Ordinal));
        Assert.True(dong.IndexOf("tour thiếu tiền", StringComparison.Ordinal)
                  < dong.IndexOf("cơ hội cần gọi", StringComparison.Ordinal));
    }

    [Fact]
    public void Khong_co_gi_thi_khong_in_dong_tong_ket_rong()
        => Assert.DoesNotContain("Đang có tổng cộng",
            SaleBriefBuilder.WrapAiReply("Hôm nay nhẹ nhàng.", Empty(), Today).BodyMarkdown);

    [Fact]
    public void Prompt_cam_bo_han_mot_nhom_va_ghim_2_nhom_khan()
    {
        var p = SaleBriefBuilder.BuildPrompt(DuMoiNhom(), Today, maxItems: 7);
        Assert.Contains("không bỏ hẳn một nhóm", p);
        Assert.Contains("BẮT BUỘC đưa vào danh sách việc", p);
    }
}
