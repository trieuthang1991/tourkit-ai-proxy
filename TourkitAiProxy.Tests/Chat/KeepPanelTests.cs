using TourkitAiProxy.Services.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// Khóa hành vi JsonPlannerAgent.ShouldKeepPanel — quyết định GIỮ hay BỎ panel/câu trả lời lượt trước
/// khi planner KHÔNG định tuyến được tool (nhánh tool == null).
///
/// Bug gốc (fix 6ddcf65): câu hỏi SỐ LIỆU MỚI mà AI bí → code cũ vẫn copy panel + câu trả lời cũ xuống
/// → user tưởng AI trả lời câu mới. Đúng phải: câu hỏi số liệu mới mà bí → data=null, toolName="none".
/// Ngược lại, follow-up KHÔNG phải câu hỏi số liệu (vd "giải thích thêm") thì GIỮ panel để user còn đối chiếu.
///
/// Lưu ý phân biệt với ProvenanceQuestionTests: câu hỏi VỀ NGUỒN GỐC ("số liệu này lấy từ đâu")
/// thoát sớm ở ProvenanceShortCircuit (đầu hàm), KHÔNG bao giờ chạy tới ShouldKeepPanel.
public class KeepPanelTests
{
    [Theory]
    // Câu hỏi SỐ LIỆU MỚI → BỎ panel cũ (không copy câu trả lời cũ xuống).
    [InlineData("Doanh thu tháng này bao nhiêu?")]
    [InlineData("Cho tôi xem lợi nhuận quý 2")]
    [InlineData("Chi phí marketing tháng trước")]
    [InlineData("Top khách hàng mua nhiều nhất")]
    [InlineData("Danh sách tour sắp khởi hành")]
    [InlineData("Có bao nhiêu deal đang mở?")]
    [InlineData("Công nợ khách còn lại")]
    [InlineData("revenue this month")]
    [InlineData("show me the booking list")]
    public void Cau_hoi_so_lieu_moi_thi_BO_panel(string q)
        => Assert.False(JsonPlannerAgent.ShouldKeepPanel(q));

    [Theory]
    // Follow-up KHÔNG phải câu hỏi số liệu → GIỮ panel lượt trước.
    [InlineData("Giải thích thêm giúp tôi")]
    [InlineData("Tại sao lại như vậy?")]
    [InlineData("Nói rõ hơn đi")]
    [InlineData("Ý bạn là sao?")]
    [InlineData("Cảm ơn nhé")]
    [InlineData("Viết lại ngắn gọn hơn")]
    public void Follow_up_khong_phai_so_lieu_thi_GIU_panel(string q)
        => Assert.True(JsonPlannerAgent.ShouldKeepPanel(q));

    [Theory]
    // Rỗng/null → GIỮ panel (an toàn: không coi là yêu cầu số liệu mới).
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Cau_rong_thi_GIU_panel(string? q)
        => Assert.True(JsonPlannerAgent.ShouldKeepPanel(q!));

    [Fact]
    public void Khong_phan_biet_hoa_thuong()
    {
        Assert.False(JsonPlannerAgent.ShouldKeepPanel("DOANH THU THÁNG NÀY"));
        Assert.False(JsonPlannerAgent.ShouldKeepPanel("Revenue THIS Month"));
    }

    [Fact]
    public void Cau_hoi_nguon_goc_da_thoat_som_nen_khong_phu_thuoc_ShouldKeepPanel()
    {
        // Chốt lại quan hệ 2 fix: câu hỏi nguồn gốc được ProvenanceShortCircuit xử lý TRƯỚC
        // (đầu RunAsync/StreamAsync) nên không bao giờ rơi vào nhánh tool==null.
        // Test này khóa điều kiện đó: câu vẫn được nhận diện là provenance.
        Assert.True(JsonPlannerAgent.IsProvenanceQuestion("Số liệu này lấy từ đâu?"));
    }

    // ── Gõ KHÔNG DẤU vẫn nhận diện đúng (HasDataKeyword dùng chung Norm với IsProvenanceQuestion) ──
    // Người Việt gõ không dấu rất phổ biến; trước đây so khớp chuỗi CÓ DẤU nên "loi nhuan" lọt lưới
    // → panel cũ bị copy xuống (đúng bug 6ddcf65 định vá).
    [Theory]
    [InlineData("loi nhuan thang nay")]     // lợi nhuận
    [InlineData("khach hang moi")]          // khách
    [InlineData("co hoi ban hang")]         // cơ hội
    [InlineData("chi phi marketing")]       // chi phí
    [InlineData("cong no con lai")]         // công nợ
    [InlineData("ngan sach quy nay")]       // ngân sách
    [InlineData("doanh thu thang nay")]     // vốn không dấu
    [InlineData("tour sap khoi hanh")]
    [InlineData("deal dang mo")]
    public void Go_khong_dau_van_nhan_dien_la_cau_hoi_so_lieu(string q)
        => Assert.False(JsonPlannerAgent.ShouldKeepPanel(q));

    // ── Chống nhận nhầm do bỏ dấu: đặt / đạt / đất đều thành "dat" ──
    // Vì vậy từ khóa "đặt" đứng một mình bị bỏ, thay bằng cụm rõ nghĩa (đặt tour / đặt chỗ / đặt phòng).
    [Theory]
    [InlineData("Kết quả đạt được thế nào?")]     // "đạt" — KHÔNG phải hỏi số liệu
    [InlineData("ket qua dat duoc the nao")]
    [InlineData("Giá đất khu đó ra sao?")]        // "đất"
    public void Khong_nham_dat_dat_thanh_cau_hoi_so_lieu(string q)
        => Assert.True(JsonPlannerAgent.ShouldKeepPanel(q));

    [Theory]
    // Nhưng cụm "đặt tour/chỗ/phòng" vẫn phải nhận ra là câu hỏi số liệu.
    [InlineData("Khách đặt tour tháng này")]
    [InlineData("khach dat cho chua")]
    [InlineData("Ai đặt phòng hôm nay?")]
    public void Cum_dat_tour_cho_phong_van_nhan_dien_duoc(string q)
        => Assert.False(JsonPlannerAgent.ShouldKeepPanel(q));

    [Theory]
    // Từ khóa tiếng Anh dạng số nhiều vẫn khớp (giữ hành vi cũ — so khớp chuỗi con).
    [InlineData("show me customers list")]
    [InlineData("all bookings today")]
    [InlineData("open opportunities")]
    public void Tieng_anh_so_nhieu_van_khop(string q)
        => Assert.False(JsonPlannerAgent.ShouldKeepPanel(q));
}
