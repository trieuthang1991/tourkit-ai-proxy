using TourkitAiProxy.Services.Digest;
using Xunit;
using TourkitAiProxy.Domain.Digest;

namespace TourkitAiProxy.Tests.Digest;

/// Bản tin điều hành. Ranh giới quan trọng nhất: SỐ do máy chủ tính, AI chỉ viết lời.
/// Và AI lỗi thì bản tin vẫn phải ra được — mất bản tin tệ hơn bản tin khô.
public class CeoBriefBuilderTests
{
    private static readonly DateTime Today = new(2026, 8, 11);

    private static CeoBriefData Data() => new(
        ThisMtd: new(1_000_000_000m, 700_000_000m, 300_000_000m),
        PrevMtd: new(800_000_000m, 600_000_000m, 200_000_000m),
        TopSellers: new() { "An — 500tr", "Bình — 300tr" },
        NewDealsYesterday: 4, OpenPaymentAlerts: 2,
        TodayAppointments: 8, OverdueAppointments: 3);

    [Theory]
    [InlineData(120, 100, "+20%")]
    [InlineData(80, 100, "-20%")]
    [InlineData(100, 0, "n/a")]
    public void PctChange_dung(decimal cur, decimal prev, string expected)
        => Assert.Equal(expected, CeoBriefBuilder.PctChange(cur, prev));

    [Fact]
    public void Prompt_chua_so_thuc_va_lenh_cam_bia()
    {
        var p = CeoBriefBuilder.BuildPrompt(Data(), Today);
        Assert.Contains("1.000.000.000", p.Replace(",", "."));
        Assert.Contains("không bịa", p.ToLowerInvariant());
    }

    [Fact]
    public void Fallback_render_du_3_so_chinh()
    {
        var m = CeoBriefBuilder.RenderFallback(Data(), Today);
        Assert.Contains("Doanh thu", m.BodyMarkdown);
        Assert.Contains("+25%", m.BodyMarkdown);   // 1000 so với 800
        Assert.Equal(BriefTypes.Ceo, m.Kind);
    }

    [Fact]
    public void WrapAiReply_giu_prose_va_gan_so_goc()
    {
        var m = CeoBriefBuilder.WrapAiReply("Doanh thu tăng tốt.", Data(), Today);
        Assert.StartsWith("Doanh thu tăng tốt.", m.BodyMarkdown);
        Assert.Contains("Doanh thu:", m.BodyMarkdown);   // bảng số gốc đính kèm dưới lời AI
    }

    // ── Ca thêm ngoài plan ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 100, "-100%")]     // sạch doanh thu
    [InlineData(100, 100, "+0%")]     // đứng yên — không được ra "0%" trơ hay "n/a"
    [InlineData(0, 0, "n/a")]         // cả hai kỳ đều 0
    // Đổi DẤU thì không in phần trăm: "-150%" vừa vô nghĩa (giảm quá cả số ban đầu) vừa đọc nhẹ
    // hơn thực tế, trong khi lãi hoá lỗ mới là điều đáng báo động nhất của bản tin.
    [InlineData(-50, 100, "chuyển từ lãi sang lỗ")]
    [InlineData(50, -100, "chuyển từ lỗ sang lãi")]
    // Âm sang âm: công thức % cho dấu NGƯỢC với ý nghĩa (lỗ 100 → lỗ 150 ra "+50%", đọc như
    // đang khá lên). Nói bằng chữ mới đúng chiều.
    [InlineData(-150, -100, "lỗ nặng thêm 50%")]
    [InlineData(-50, -100, "lỗ giảm 50%")]
    public void PctChange_cac_ca_bien(decimal cur, decimal prev, string expected)
        => Assert.Equal(expected, CeoBriefBuilder.PctChange(cur, prev));

    /// Quy đổi đơn vị là một PHÉP TÍNH nên phải do máy chủ làm. Lần chạy thật: AI viết
    /// "lỗ 644 tỷ đồng" cho số 644.211.149đ — lệch đúng một bậc, trong báo cáo cho giám đốc
    /// thì sai một bậc là hỏng cả bản tin.
    [Theory]
    [InlineData(4_531_460_000, "4,5 tỷ đồng")]
    [InlineData(644_211_149, "644,2 triệu đồng")]
    [InlineData(12_500, "12,5 nghìn đồng")]
    [InlineData(2_000_000_000, "2 tỷ đồng")]
    [InlineData(900, "900 đồng")]
    public void Doc_gon_dung_bac_don_vi(decimal v, string expected)
        => Assert.Equal(expected, CeoBriefBuilder.Short(v));

    [Fact]
    public void Prompt_dua_san_cach_doc_gon_va_cam_tu_quy_doi()
    {
        var p = CeoBriefBuilder.BuildPrompt(Data(), Today);
        Assert.Contains("đọc gọn:", p);
        Assert.Contains("không tự quy đổi", p);
    }

    /// Bảng số in cho NGƯỜI ĐỌC thì không kèm cách đọc gọn — "4.531.460.000đ (4,5 tỷ đồng)" là thừa.
    [Fact]
    public void Bang_so_cho_nguoi_doc_khong_kem_doc_gon()
        => Assert.DoesNotContain("đọc gọn", CeoBriefBuilder.RenderFallback(Data(), Today).BodyMarkdown);

    /// Chi phí 0đ ở CRM đang chạy = CHƯA GHI NHẬN. In "0đ (n/a)" rồi để dòng lợi nhuận bằng đúng
    /// doanh thu khiến người đọc lướt tưởng lãi trọn — kết luận tài chính sai (gặp thật ở erp).
    [Fact]
    public void Chua_ghi_nhan_chi_phi_thi_noi_thang_va_gan_nhan_loi_nhuan()
    {
        var d = Data() with { ThisMtd = new(31_190_000m, 0m, 31_190_000m) };
        var md = CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown;
        Assert.Contains("Chi phí: chưa ghi nhận trong hệ thống", md);
        Assert.Contains("Lợi nhuận (CHƯA trừ chi phí): 31.190.000đ", md);
        Assert.DoesNotContain("Chi phí: 0đ", md);
    }

    [Fact]
    public void Quy_mo_nho_thi_prompt_nhac_dung_ket_luan_xu_huong_tu_phan_tram()
        => Assert.Contains("quy mô nhỏ", CeoBriefBuilder.BuildPrompt(Data(), Today));

    /// Bảng số gắn nhãn "(CHƯA trừ chi phí)" là chưa đủ: bản chạy thật vẫn viết "lợi nhuận tạm
    /// tính bằng doanh thu là 31,2 triệu". Gắn chữ "tạm tính" không cứu được — người đọc vẫn nhớ
    /// đúng con số đó và tưởng là lãi.
    [Fact]
    public void Prompt_cam_neu_con_so_loi_nhuan_khi_chua_ghi_nhan_chi_phi()
        => Assert.Contains("không nêu một con số lợi nhuận nào", CeoBriefBuilder.BuildPrompt(Data(), Today));

    [Fact]
    public void So_am_goi_thang_la_LO()
    {
        var d = Data() with { ThisMtd = new(1_000_000_000m, 1_600_000_000m, -600_000_000m) };
        var md = CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown;
        Assert.Contains("Lỗ: 600.000.000đ", md);
        // Dấu trừ lẫn giữa một dòng toàn chữ số là thứ dễ lướt qua nhất
        Assert.DoesNotContain("Lợi nhuận: -", md);
    }

    [Fact]
    public void Khong_co_top_seller_thi_ghi_na_chu_khong_de_trong()
    {
        var d = Data() with { TopSellers = new() };
        Assert.Contains("n/a", CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown);
    }

    /// Mức trần số nhân viên nay là CẤU HÌNH (sellerCount), cắt ở lúc lấy số trong
    /// CeoBriefWorkflow — builder in đủ những gì được đưa. Cắt ở cả hai nơi thì công ty
    /// đặt 5 người vẫn chỉ thấy 3, mà nhìn cấu hình không hiểu vì sao.
    [Fact]
    public void In_du_danh_sach_nhan_vien_duoc_dua_vao()
    {
        var d = Data() with { TopSellers = new() { "A", "B", "C", "D", "E" } };
        var md = CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown;
        Assert.Contains("; D", md);
        Assert.Contains("; E", md);
    }

    /// Tổng việc treo một mình nó không nói được gì (công ty nào chả có việc đang làm) — phần
    /// "trong đó quá hạn" mới là chỗ giám đốc cần can thiệp, nên luôn đi kèm.
    [Fact]
    public void Viec_con_treo_luon_kem_phan_qua_han()
    {
        var d = Data() with { OpenTasks = 1234, LateTasks = 56 };
        var md = CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown;
        Assert.Contains("Công việc chưa hoàn thành: 1.234 việc · trong đó 56 việc đã quá hạn", md);
    }

    /// "Trong đó" mà số con lớn hơn số tổng thì bản tin tự mâu thuẫn ngay trên mặt giấy
    /// (đã xảy ra thật trên erp: 335 việc, "trong đó" 591 quá hạn).
    [Fact]
    public void Khong_noi_trong_do_khi_so_qua_han_lon_hon_tong()
    {
        var md = CeoBriefBuilder.RenderFallback(Data() with { OpenTasks = 335, LateTasks = 591 }, Today).BodyMarkdown;
        Assert.Contains("335 việc · 591 việc quá hạn", md);
        Assert.DoesNotContain("trong đó 591", md);
    }

    [Fact]
    public void Khong_con_viec_nao_thi_noi_thang()
        => Assert.Contains("Công việc chưa hoàn thành: không còn việc nào",
            CeoBriefBuilder.RenderFallback(Data() with { OpenTasks = 0, LateTasks = 0 }, Today).BodyMarkdown);

    [Fact]
    public void Muc_bi_tat_thi_KHONG_in_ra()
    {
        var d = Data() with
        {
            ShowSellers = false, ShowNewDeals = false,
            ShowAppointments = false, ShowAlerts = false, ShowTasks = false,
        };
        var md = CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown;
        // 3 số tài chính là phần lõi, luôn còn
        Assert.Contains("Doanh thu", md);
        Assert.Contains("Lợi nhuận", md);
        // Mục tắt thì biến mất hẳn — in "0" cho một mục không lấy số là nói dối
        Assert.DoesNotContain("Top nhân viên", md);
        Assert.DoesNotContain("Cơ hội mới", md);
        Assert.DoesNotContain("Lịch hẹn", md);
        Assert.DoesNotContain("Cảnh báo thanh toán", md);
        Assert.DoesNotContain("Công việc chưa hoàn thành", md);
    }

    [Fact]
    public void Tat_so_sanh_thi_khong_con_phan_tram_nao()
    {
        var d = Data() with { ShowCompare = false };
        var md = CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown;
        Assert.DoesNotContain("%", md);
        Assert.DoesNotContain("so cùng kỳ", md);
        Assert.Contains("Doanh thu", md);
    }

    /// Tắt so sánh mà không dặn thì AI vẫn viết "tăng so với tháng trước" theo thói quen —
    /// tức là bịa ra một so sánh không hề có số.
    [Fact]
    public void Tat_so_sanh_thi_prompt_cam_AI_noi_tang_giam()
    {
        var p = CeoBriefBuilder.BuildPrompt(Data() with { ShowCompare = false }, Today);
        Assert.Contains("KHÔNG có kỳ so sánh", p);
    }

    [Fact]
    public void Doi_ky_so_sanh_thi_nhan_doi_theo()
    {
        var d = Data() with { CompareLabel = "so cùng kỳ năm trước" };
        var md = CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown;
        Assert.Contains("so cùng kỳ năm trước", md);
        Assert.DoesNotContain("so cùng kỳ tháng trước", md);
    }

    [Fact]
    public void Tat_bang_so_thi_bai_AI_dung_mot_minh()
    {
        var m = CeoBriefBuilder.WrapAiReply("Doanh thu tháng này khả quan.",
            Data() with { ShowNumbers = false }, Today);
        Assert.Contains("khả quan", m.BodyMarkdown);
        Assert.DoesNotContain("Số liệu:", m.BodyMarkdown);
    }

    [Fact]
    public void AI_tra_loi_co_the_HTML_thi_bi_vo_hieu_hoa()
    {
        // AI bị chèn lệnh (prompt injection) trả về thẻ script — không được lọt vào email.
        var m = CeoBriefBuilder.WrapAiReply("<script>alert(1)</script>", Data(), Today);
        Assert.DoesNotContain("<script>", m.BodyHtml);
        Assert.Contains("&lt;script&gt;", m.BodyHtml);
    }

    [Fact]
    public void Fallback_va_WrapAiReply_deu_co_cung_bang_so()
    {
        // Hai đường ra khác nhau nhưng bảng số PHẢI giống — nếu lệch thì giám đốc thấy số
        // khác nhau tuỳ hôm đó AI có chạy được hay không.
        var d = Data();
        var fb = CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown;
        var ai = CeoBriefBuilder.WrapAiReply("Nhận định.", d, Today).BodyMarkdown;
        foreach (var line in fb.Split('\n'))
            Assert.Contains(line.Trim(), ai);
    }

    [Fact]
    public void Lich_hen_hom_nay_hien_ca_so_qua_han()
    {
        var md = CeoBriefBuilder.RenderFallback(Data(), Today).BodyMarkdown;
        Assert.Contains("Lịch hẹn hôm nay: 8 cuộc", md);
        // "tồn đọng ... tích luỹ" chứ không phải "cần xử lý ngay": nguồn trả MỌI cuộc quá hạn
        // từ trước tới nay (thực tế đã thấy 2.338 cuộc), gọi là khẩn cấp trong ngày là báo động sai.
        Assert.Contains("tồn đọng 3 cuộc quá hạn", md);
        Assert.Contains("tích luỹ", md);
    }

    [Fact]
    public void Khong_co_hen_thi_noi_thang_chu_khong_de_trong()
    {
        var d = Data() with { TodayAppointments = 0, OverdueAppointments = 0 };
        Assert.Contains("Lịch hẹn hôm nay: không có cuộc nào", CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown);
    }

    [Fact]
    public void Khong_qua_han_thi_khong_nhac_qua_han()
    {
        var d = Data() with { TodayAppointments = 5, OverdueAppointments = 0 };
        var md = CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown;
        Assert.Contains("5 cuộc", md);
        Assert.DoesNotContain("quá hạn", md);
    }

    [Fact]
    public void Prompt_dan_AI_khong_coi_ton_dong_la_khan_cap_trong_ngay()
    {
        // Thiếu chỉ dẫn này thì AI lấy con số tồn đọng lớn nhất làm tiêu điểm và viết
        // "cần xử lý ngay lập tức" — đã xảy ra ở lần chạy thật đầu tiên.
        var p = CeoBriefBuilder.BuildPrompt(Data(), Today);
        Assert.Contains("tích luỹ từ trước", p);
        Assert.Contains("đừng gọi là khẩn cấp trong ngày", p);
    }

    [Fact]
    public void Prompt_dan_AI_khong_ket_luan_sai_khi_chi_phi_bang_0()
    {
        // Chi phí 0 = công ty chưa ghi chi phí vào CRM, KHÔNG phải "lãi trọn doanh thu".
        var p = CeoBriefBuilder.BuildPrompt(Data() with { ThisMtd = new CeoNumbers(100, 0, 100) }, Today);
        Assert.Contains("CHƯA GHI NHẬN", p);
    }

    [Fact]
    public void Tieu_de_co_ngay()
        => Assert.Contains("11/08", CeoBriefBuilder.RenderFallback(Data(), Today).Title);

    // ── Dự phóng cuối tháng (C4) ─────────────────────────────────────────────────

    /// Chưa khai chỉ tiêu → mục dự phóng KHÔNG được hiện. Bản tin của công ty chưa đặt kế hoạch
    /// phải giống hệt như trước khi có tính năng này.
    [Fact]
    public void Chua_khai_chi_tieu_thi_ban_tin_khong_co_dong_du_phong()
    {
        var text = CeoBriefBuilder.RenderFallback(Data(), Today).BodyMarkdown;
        Assert.DoesNotContain("Dự phóng", text);
    }

    /// Có chỉ tiêu → dòng dự phóng vào CẢ bảng số LẪN prompt gửi AI (dùng chung một khối số),
    /// nhờ vậy AI viết lời cũng nhắc được chuyện có kịp kế hoạch hay không.
    [Fact]
    public void Co_chi_tieu_thi_dong_du_phong_vao_ca_bang_so_lan_prompt()
    {
        var d = Data() with { Forecast = CeoForecast.Estimate(1_000_000_000m, 3_000_000_000m, Today) };

        Assert.Contains("Dự phóng cuối tháng", CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown);
        Assert.Contains("Dự phóng cuối tháng", CeoBriefBuilder.BuildPrompt(d, Today));
    }

    /// Dự phóng phải đứng CUỐI bảng số: nó là suy luận từ các số ở trên. Đưa lên đầu là bắt người
    /// đọc tin một ước lượng trước khi thấy căn cứ.
    [Fact]
    public void Du_phong_dung_cuoi_bang_so()
    {
        var d = Data() with { Forecast = CeoForecast.Estimate(1_000_000_000m, 3_000_000_000m, Today) };
        var body = CeoBriefBuilder.RenderFallback(d, Today).BodyMarkdown;

        Assert.True(body.IndexOf("Doanh thu", StringComparison.Ordinal)
                  < body.IndexOf("Dự phóng", StringComparison.Ordinal));
    }
}
