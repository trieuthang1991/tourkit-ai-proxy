using TourkitAiProxy.Services.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// Khóa hành vi ChatTools.TitleOf — nhãn NGUỒN hiện cho người dùng.
/// Bất biến quan trọng: KHÔNG BAO GIỜ trả về tên tool kỹ thuật. Không tra được nguồn thì trả null
/// (UI ẩn chip) chứ không rơi về tên thô như "financial_summary".
public class ToolTitleTests
{
    [Theory]
    [InlineData("financial_summary", "Chi tiết tài chính")]
    [InlineData("cashflow", "Doanh thu & Lợi nhuận")]
    [InlineData("booking_tickets", "Cơ hội bán hàng")]
    [InlineData("employee_performance", "Hiệu suất nhân viên")]
    [InlineData("list_markets", "Thị trường")]
    public void Tra_ra_nhan_tieng_Viet(string name, string expected)
        => Assert.Equal(expected, ChatTools.TitleOf(name));

    [Fact]
    public void Khong_phan_biet_hoa_thuong()
        => Assert.Equal("Doanh thu & Lợi nhuận", ChatTools.TitleOf("CASHFLOW"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("none")]
    [InlineData("None")]
    [InlineData("khong_ton_tai")]
    public void Khong_co_nguon_thi_tra_null(string? name)
        => Assert.Null(ChatTools.TitleOf(name));

    /// Nếu ai đó thêm tool mới mà quên đặt Title, chip nguồn sẽ biến mất im lặng trên UI.
    /// Test này bắt lỗi đó ngay tại thời điểm thêm tool.
    [Fact]
    public void Moi_tool_deu_phai_co_Title()
    {
        var thieu = ChatTools.All.Where(t => string.IsNullOrWhiteSpace(t.Title)).Select(t => t.Name).ToList();
        Assert.True(thieu.Count == 0, "Tool thiếu Title (nhãn nguồn): " + string.Join(", ", thieu));
    }

    /// Nhãn nguồn phải là tiếng Việt cho người đọc — không được trùng y hệt tên kỹ thuật
    /// (vd Title="cashflow"), vì như vậy là lộ tên tool qua đường vòng.
    [Fact]
    public void Title_khong_duoc_trung_ten_ky_thuat()
    {
        var lo = ChatTools.All
            .Where(t => string.Equals(t.Title, t.Name, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name).ToList();
        Assert.True(lo.Count == 0, "Title trùng tên tool → lộ tên kỹ thuật: " + string.Join(", ", lo));
    }
}
