using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Services.Bootstrap;
using TourkitAiProxy.Services.Chat.Inbox;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Trợ lý hộp thư chat tra dữ liệu tour — canh những chỗ mà hỏng thì <b>không ai thấy</b>.
///
/// <para>Tính năng này nói thẳng với khách hàng thật: chữ nó viết ra đi tới điện thoại khách, không
/// ai duyệt lại. Nên ba nhóm dưới đây không phải test cho đủ, mà là ba cách nó có thể gây hại thật:
/// nới quyền đọc, trả sai số chỗ, và cắt danh sách im lặng rồi khẳng định "không có tour".</para>
/// </summary>
public class ChatTourLookupGuardTests
{
    private static IConfiguration Cfg(params (string Key, string? Val)[] kv)
    {
        var g = new Dictionary<string, string?>();
        foreach (var (k, v) in kv) g[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(g).Build();
    }

    private static JsonObject Doc(string json) => (JsonObject)JsonNode.Parse(json)!;

    // ── 1. Danh sách trắng ──────────────────────────────────────────────────

    [Fact]
    public void Chi_cho_phep_hai_tool_tu_van()
    {
        // tour_catalog + tour_detail: hai đường dựng RIÊNG cho việc tư vấn khách. KHÔNG dùng
        // 'tours'/'departures' nữa — chúng trả DTO báo cáo nội bộ (tên khách, nhân viên bán,
        // doanh thu) và lại thiếu giá.
        Assert.Equal(new[] { "tour_catalog", "tour_detail" }, ChatTourLookup.AllowedTools);
    }

    [Theory]
    [InlineData("tours")]
    [InlineData("departures")]
    public void Khong_dung_nguon_bao_cao_noi_bo_nua(string ten)
    {
        Assert.DoesNotContain(ten, ChatTourLookup.AllowedTools);
    }

    /// <summary>
    /// Nguồn mới trả sẵn <c>available</c> nhưng KHÔNG trả <c>booked</c>/<c>onHold</c>. Tự trừ ở
    /// đây sẽ ra <c>slots - 0 - 0 = slots</c> — báo tour trống trơn trong khi nó sắp đầy, rồi đặt
    /// cạnh con số đúng cho mô hình chọn bừa.
    /// </summary>
    [Fact]
    public void Co_available_san_thi_KHONG_tu_tinh_lai()
    {
        var o = Doc("""{"items":[{"title":"Đà Nẵng","slots":20,"available":3}]}""");
        ChatTourLookup.ThemChoConLai(o);

        Assert.Null(o["items"]![0]!["choConLai"]);
        Assert.Equal(3, o["items"]![0]!["available"]!.GetValue<int>());
    }

    /// <summary>
    /// Đây là test QUAN TRỌNG NHẤT file này. Trợ lý chat trả lời cho KHÁCH, mà khách thì có danh
    /// tính và có hội thoại riêng — lọt <c>booking_tickets</c> hay <c>customers</c> là khách A hỏi
    /// trúng dữ liệu của khách B. Widget mặc định có <c>booking_tickets</c>; chỗ này cố ý hẹp hơn.
    /// </summary>
    [Theory]
    [InlineData("booking_tickets")]
    [InlineData("customers")]
    [InlineData("top_customers")]
    [InlineData("financial_summary")]
    [InlineData("cashflow")]
    [InlineData("employee_performance")]
    [InlineData("top_sellers")]
    [InlineData("list_markets")]
    [InlineData("tasks")]
    [InlineData("appointments")]
    public void Khong_bao_gio_cho_tool_du_lieu_noi_bo(string ten)
    {
        Assert.DoesNotContain(ten, ChatTourLookup.AllowedTools);
    }

    [Fact]
    public void Ten_tool_trong_danh_sach_trang_phai_co_that_trong_danh_muc()
    {
        // Gõ sai một ký tự là tính năng chết câm: bộ chọn không thấy tool nào nên không bao giờ
        // tra được gì, mà cũng không có lỗi nào hiện ra.
        foreach (var ten in ChatTourLookup.AllowedTools)
            Assert.NotNull(ChatTools.Find(ten));
    }

    // ── 2. Công tắc bật/tắt ─────────────────────────────────────────────────
    //
    // KHÔNG còn cờ Features:ChatTourLookup (bỏ 18/09/2026) — tra dữ liệu tour đi theo tính năng
    // chat. Công tắc duy nhất là ô theo từng công ty, kiểm ngay dưới đây.

    // ── 2b. Phạm vi: Ô CẤU HÌNH quyết, không phải ngữ cảnh ───────────────────
    //
    // Luật này đáng test kỹ vì nó quyết định công ty thấy dữ liệu nào, và vì bản đầu làm SAI theo
    // đúng hướng khó thấy: suy phạm vi từ ngữ cảnh, nên cùng một câu hỏi ra hai kết quả khác nhau
    // tuỳ nhân viên bấm hay bot tự trả lời — trong khi công ty không khai gì cả.

    [Theory]
    // Tắt ô (mặc định) → CẢ KHO, bất kể ai gọi. Đây là cái bản đầu làm sai.
    [InlineData(false, true,  true,  ChatTourLookup.DanhTinh.TaiKhoanDichVu)]
    [InlineData(false, true,  false, ChatTourLookup.DanhTinh.TaiKhoanDichVu)]
    [InlineData(false, false, true,  ChatTourLookup.DanhTinh.TaiKhoanDichVu)]
    [InlineData(false, false, false, ChatTourLookup.DanhTinh.TaiKhoanDichVu)]
    // Bật ô + nhân viên đang bấm Gợi ý → quyền chính người đó.
    [InlineData(true,  true,  true,  ChatTourLookup.DanhTinh.PhienNhanVien)]
    [InlineData(true,  true,  false, ChatTourLookup.DanhTinh.PhienNhanVien)]
    // Bật ô + bot tự trả lời + có người phụ trách → quyền người phụ trách.
    [InlineData(true,  false, true,  ChatTourLookup.DanhTinh.PhienNguoiPhuTrach)]
    // Bật ô + bot tự trả lời + hội thoại chưa gán ai → rơi về cả kho, KHÔNG im lặng bỏ qua.
    [InlineData(true,  false, false, ChatTourLookup.DanhTinh.TaiKhoanDichVu)]
    public void Chon_danh_tinh_theo_o_cau_hinh(bool theoQuyen, bool coPhienNhanVien,
        bool coNguoiPhuTrach, ChatTourLookup.DanhTinh mong)
    {
        Assert.Equal(mong, ChatTourLookup.ChonDanhTinh(theoQuyen, coPhienNhanVien, coNguoiPhuTrach));
    }

    [Fact]
    public void Tat_o_thi_hai_duong_goi_RA_CUNG_MOT_pham_vi()
    {
        // Chốt chặn cho đúng lỗi bản đầu: công ty không khai gì thì nhân viên bấm và bot tự trả
        // lời phải thấy CÙNG một kho. Khác nhau là khách được tư vấn khác nhau tuỳ giờ nhắn.
        var nhanVienBam = ChatTourLookup.ChonDanhTinh(false, coPhienNhanVien: true, coNguoiPhuTrach: true);
        var botTuTraLoi = ChatTourLookup.ChonDanhTinh(false, coPhienNhanVien: false, coNguoiPhuTrach: true);
        Assert.Equal(nhanVienBam, botTuTraLoi);
    }

    [Fact]
    public void Mac_dinh_cong_ty_la_XEM_CA_KHO()
    {
        // Mặc định phải là cả kho: đổi thành "theo quyền" sẽ lặng lẽ THU HẸP dữ liệu của công ty
        // đang dùng, mà thu hẹp thì không ai báo — chỉ thấy bot bỗng trả lời thiếu tour.
        Assert.False(ChatBotSettings.Default.TourLookupByUser);
    }

    [Fact]
    public void Cong_ty_mac_dinh_KHONG_bat_tra_tour()
    {
        // Công tắc thứ hai, theo từng công ty. Mặc định phải tắt: bật tính năng trên máy chủ không
        // được tự động gỡ lời hứa an toàn của mọi công ty đang dùng.
        Assert.False(ChatBotSettings.Default.TourLookup);
    }

    // ── 3. Số chỗ còn ───────────────────────────────────────────────────────

    /// <summary>
    /// Công thức đúng là <c>slots − (booked + onHold)</c>. CLAUDE.md ghi nhãn <c>OnHold</c>/
    /// <c>Booked</c> bị đảo CỐ Ý ở giao diện mobile — nên ai đọc tên trường rồi suy ra ý nghĩa sẽ
    /// suy ra sai. Tính sẵn ở máy chủ chính là để mô hình khỏi phải đoán.
    /// </summary>
    [Fact]
    public void Cho_con_lai_tru_ca_booked_lan_onhold()
    {
        var o = Doc("""{"items":[{"tourName":"Đà Nẵng","slots":20,"booked":6,"onHold":3}]}""");
        ChatTourLookup.ThemChoConLai(o);
        Assert.Equal(11, o["items"]![0]!["choConLai"]!.GetValue<int>());
    }

    [Fact]
    public void Thieu_onHold_thi_coi_nhu_khong()
    {
        var o = Doc("""{"items":[{"slots":20,"booked":6}]}""");
        ChatTourLookup.ThemChoConLai(o);
        Assert.Equal(14, o["items"]![0]!["choConLai"]!.GetValue<int>());
    }

    [Fact]
    public void Ban_qua_so_cho_thi_ve_khong_chu_khong_am()
    {
        // Số âm lọt tới model là nó viết "còn -2 chỗ" cho khách.
        var o = Doc("""{"items":[{"slots":10,"booked":8,"onHold":5}]}""");
        ChatTourLookup.ThemChoConLai(o);
        Assert.Equal(0, o["items"]![0]!["choConLai"]!.GetValue<int>());
    }

    [Fact]
    public void Tour_le_khong_khai_so_cho_thi_KHONG_gan_cho_con_lai()
    {
        // Dịch vụ lẻ, visa, vé bay: slots = 0 nghĩa là "không có khái niệm chỗ", không phải "hết
        // chỗ". Gắn choConLai=0 vào là bot báo khách hết chỗ cho một thứ không giới hạn chỗ.
        var o = Doc("""{"items":[{"tourName":"Visa Nhật","slots":0,"booked":0}]}""");
        ChatTourLookup.ThemChoConLai(o);
        Assert.Null(o["items"]![0]!["choConLai"]);
    }

    [Fact]
    public void Khong_co_items_thi_khong_no()
    {
        var o = Doc("""{"title":"Tour","total":0}""");
        ChatTourLookup.ThemChoConLai(o);       // không được ném
        Assert.Null(o["items"]);
    }

    // ── 3b. Lọc trường nội bộ ───────────────────────────────────────────────

    /// <summary>
    /// <c>/api/ai/tours</c> là API báo cáo NỘI BỘ: nó trả về tên khách đã đặt tour, tên nhân viên
    /// bán và doanh thu. Bơm nguyên gói vào lời nhắc trả lời một khách LẠ là đặt dữ liệu của người
    /// khác vào ngay ngữ cảnh của cuộc trò chuyện đó.
    /// </summary>
    [Theory]
    [InlineData("customerName")]
    [InlineData("sellerName")]
    [InlineData("sellerUserName")]
    [InlineData("sellerSource")]
    [InlineData("revenue")]
    [InlineData("actualRevenue")]
    [InlineData("customerPhone")]
    [InlineData("commissionAdults")]
    [InlineData("typeCommissionAdults")]
    [InlineData("totalExpense")]
    [InlineData("actualExpense")]
    [InlineData("refund")]
    [InlineData("nguoiTao")]
    public void Truong_noi_bo_KHONG_bao_gio_lot_toi_loi_nhac(string truong)
    {
        var o = Doc($$"""
            {"items":[{"title":"Đà Nẵng 3N2Đ","slots":20,"booked":4,"{{truong}}":"BÍ MẬT"}]}
            """);
        ChatTourLookup.LocTruongNoiBo(o);

        Assert.Null(o["items"]![0]![truong]);
        Assert.DoesNotContain("BÍ MẬT", o.ToJsonString());
    }

    [Fact]
    public void Giu_lai_dung_nhung_truong_khach_can_biet()
    {
        var o = Doc("""
            {"items":[{"title":"Đà Nẵng 3N2Đ","tourCode":"DN01","departureDate":"2026-12-15",
                       "slots":20,"booked":4,"onHold":2,"tourTypeLabel":"FIT","customerName":"Chị Lan"}]}
            """);
        ChatTourLookup.ThemChoConLai(o);
        ChatTourLookup.LocTruongNoiBo(o);

        var d = o["items"]![0]!;
        Assert.NotNull(d["title"]);
        Assert.NotNull(d["tourCode"]);
        Assert.NotNull(d["departureDate"]);
        Assert.NotNull(d["tourTypeLabel"]);
        Assert.Equal(14, d["choConLai"]!.GetValue<int>());
        // booked/onHold bỏ đi SAU khi đã tính: khách không cần bản tách chi tiết, mà nhãn hai
        // trường này lại là bẫy đảo nghĩa đã ghi trong CLAUDE.md.
        Assert.Null(d["booked"]);
        Assert.Null(d["onHold"]);
        Assert.Null(d["customerName"]);
    }

    [Fact]
    public void Truong_gia_thi_giu_nhung_doanh_thu_thi_bo()
    {
        // Chưa gọi API thật nên chưa biết upstream đặt tên trường giá là gì → giữ theo hình dạng
        // tên. Nhưng revenue là doanh thu của tour, không phải giá một khách.
        var o = Doc("""
            {"items":[{"title":"T","priceAdult":5500000,"giaTreEm":4000000,
                       "revenue":90000000,"totalCost":60000000}]}
            """);
        ChatTourLookup.LocTruongNoiBo(o);

        var d = o["items"]![0]!;
        Assert.NotNull(d["priceAdult"]);
        Assert.NotNull(d["giaTreEm"]);
        Assert.Null(d["revenue"]);
        Assert.Null(d["totalCost"]);
    }

    [Fact]
    public void Truong_la_chua_tung_thay_thi_MAC_DINH_bo()
    {
        // Danh sách TRẮNG: upstream thêm trường mới là nó bị bỏ cho tới khi có người cố ý thêm
        // vào danh sách. Danh sách đen thì mỗi trường mới là một lỗ mở ra trong im lặng.
        var o = Doc("""{"items":[{"title":"T","truongMoiChuaAiBiet":"gì đó"}]}""");
        ChatTourLookup.LocTruongNoiBo(o);
        Assert.Null(o["items"]![0]!["truongMoiChuaAiBiet"]);
    }

    // ── 4. Cắt danh sách ────────────────────────────────────────────────────

    [Fact]
    public void Bang_ngan_thi_giu_nguyen_khong_gan_nhan_cat()
    {
        var o = Doc("""{"items":[{"tourName":"Đà Nẵng","slots":20,"booked":1}]}""");
        var ra = ChatTourLookup.RutGon(o, 5000);
        Assert.DoesNotContain("_truncated", ra);
    }

    /// <summary>
    /// Cắt IM LẶNG là cách tạo ra phủ định sai: model đọc danh sách cụt, tưởng đủ, rồi trả lời
    /// "công ty không có tour đi đó" — nghe thuyết phục hơn hẳn một câu "em chưa thấy".
    /// </summary>
    [Fact]
    public void Bang_dai_bi_cat_thi_PHAI_noi_ra_la_chua_du()
    {
        var items = string.Join(",", Enumerable.Range(0, 200)
            .Select(i => $$"""{"tourName":"Tour số {{i}} hành trình rất dài dòng để chiếm chỗ","slots":20,"booked":2}"""));
        var o = Doc($$"""{"items":[{{items}}]}""");

        var ra = ChatTourLookup.RutGon(o, 1500);

        Assert.Contains("_truncated", ra);
        Assert.Contains("CHƯA đủ", ra);
        Assert.True(ra.Length <= 2200, $"cắt xong vẫn dài: {ra.Length}");
    }

    [Fact]
    public void Cat_xong_van_giu_cac_khoa_ngoai_items()
    {
        var items = string.Join(",", Enumerable.Range(0, 200)
            .Select(i => $$"""{"tourName":"Tour số {{i}} hành trình rất dài dòng để chiếm chỗ","slots":20}"""));
        var o = Doc($$"""{"title":"Danh sách tour","total":200,"items":[{{items}}]}""");

        var ra = ChatTourLookup.RutGon(o, 1500);

        // Mất "total" là model không còn biết danh sách gốc to cỡ nào.
        Assert.Contains("\"total\":200", ra);
        Assert.Contains("Danh sách tour", ra);
    }

    // ── 5. Toàn tuyến chuẩn bị bảng ─────────────────────────────────────────

    [Fact]
    public void ChuanBiBang_vua_tinh_cho_vua_tra_ve_json_doc_duoc()
    {
        using var doc = JsonDocument.Parse(
            """{"title":"Tour","items":[{"tourName":"Phú Quốc","slots":15,"booked":4,"onHold":1}]}""");

        var ra = ChatTourLookup.ChuanBiBang(doc.RootElement, 5000);

        using var lai = JsonDocument.Parse(ra);       // phải parse lại được
        Assert.Equal(10, lai.RootElement.GetProperty("items")[0].GetProperty("choConLai").GetInt32());
    }
}
