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

    // ── GIỚI HẠN ĐÃ BIẾT: nhận diện từ khóa KHÔNG bỏ dấu tiếng Việt ──
    // HasDataKeyword so khớp chuỗi có dấu ("lợi nhuận", "khách", "cơ hội"), KHÁC IsProvenanceQuestion
    // (đã chuẩn hóa bỏ dấu qua Norm). Nên user gõ KHÔNG DẤU sẽ bị coi là "không phải câu hỏi số liệu"
    // → panel cũ vẫn bị giữ (bug gốc lọt lưới). Test này KHÓA hiện trạng để nếu sau này ai sửa
    // HasDataKeyword thành chuẩn hóa bỏ dấu thì test đỏ → nhớ cập nhật cả 2 nơi cho khớp.
    [Theory]
    [InlineData("loi nhuan thang nay")]     // "lợi nhuận" không dấu → KHÔNG khớp
    [InlineData("khach hang moi")]          // "khách" không dấu → KHÔNG khớp
    [InlineData("co hoi ban hang")]         // "cơ hội" không dấu → KHÔNG khớp
    public void GIOI_HAN_go_khong_dau_van_bi_coi_la_khong_phai_so_lieu(string q)
        => Assert.True(JsonPlannerAgent.ShouldKeepPanel(q));

    [Theory]
    // Ngược lại, từ khóa vốn KHÔNG có dấu vẫn nhận diện đúng dù user gõ không dấu.
    [InlineData("doanh thu thang nay")]
    [InlineData("tour sap khoi hanh")]
    [InlineData("deal dang mo")]
    public void Tu_khoa_khong_dau_san_thi_van_nhan_dien_duoc(string q)
        => Assert.False(JsonPlannerAgent.ShouldKeepPanel(q));
}
