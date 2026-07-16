using TourkitAiProxy.Services.Chat;
using Xunit;

namespace TourkitAiProxy.Tests;

// Nhãn + đơn vị của thẻ KPI (Chat-Analytics). Hai lỗi từng gặp:
//  • "total"/"tong" nằm trong MoneyHints → totalTours (số đếm) bị gắn đơn vị "đ" → hiện "6đ".
//  • Friendly() trượt map Labels → trả key thô tiếng Anh ra UI.
public class ChatLabelFormatTests
{
    // ─── IsMoney: KHÔNG phải tiền ────────────────────────────────────────────
    // "total"/"tong" là từ GỘP, không phải từ chỉ tiền. Tiền phải đến từ danh từ
    // thật (revenue/tien/payment/expense/gia/price…).
    [Theory]
    [InlineData("totalTours")]      // Số tour
    [InlineData("totalTour")]
    [InlineData("totalCustomers")]  // Số khách
    [InlineData("totalPax")]
    [InlineData("totalSlots")]
    [InlineData("totalGuest")]
    [InlineData("rank")]
    [InlineData("available")]
    [InlineData("booked")]
    [InlineData("count")]
    [InlineData("soLuong")]
    public void IsMoney_false_cho_cot_dem(string key)
        => Assert.False(JsonPlannerAgent.IsMoney(key), $"'{key}' là số đếm, không được gắn đơn vị 'đ'");

    // ─── IsMoney: LÀ tiền ────────────────────────────────────────────────────
    [Theory]
    [InlineData("totalRevenue")]
    [InlineData("totalPayment")]
    [InlineData("totalExpense")]
    [InlineData("actualRevenue")]
    [InlineData("revenue")]
    [InlineData("expense")]
    [InlineData("profit")]
    [InlineData("tourPrice")]       // chứa 'tour' — không được chặn nhầm
    [InlineData("pricePerSlot")]
    [InlineData("kpiGrossProfit")]
    [InlineData("tongTien")]
    [InlineData("doanhThu")]
    [InlineData("giaTriCoHoi")]     // Giá trị cơ hội = tiền (từng bị 'cohoi' chặn nhầm)
    [InlineData("thucThu")]
    public void IsMoney_true_cho_cot_tien(string key)
        => Assert.True(JsonPlannerAgent.IsMoney(key), $"'{key}' là tiền, phải gắn đơn vị 'đ'");

    // Từ gộp trần: không có danh từ nào đi kèm → coi là tiền (thẻ "Tổng" ở financial-summary).
    [Theory]
    [InlineData("total")]
    [InlineData("tong")]
    [InlineData("tongCong")]
    public void IsMoney_true_cho_tu_gop_tran(string key)
        => Assert.True(JsonPlannerAgent.IsMoney(key), $"'{key}' đứng một mình = tổng tiền");

    // ─── Friendly: không bao giờ trả key thô ─────────────────────────────────
    [Theory]
    [InlineData("totalTours", "Số tour")]
    [InlineData("totalRevenue", "Tổng chi tiêu")]
    [InlineData("revenue", "Doanh thu")]
    public void Friendly_tra_nhan_Viet_khi_co_trong_map(string key, string expected)
        => Assert.Equal(expected, JsonPlannerAgent.Friendly(key));

    // Key lạ → tách camelCase cho dễ đọc, KHÔNG trả nguyên key thô.
    [Theory]
    [InlineData("someUnknownKey", "Some unknown key")]
    [InlineData("rank", "Rank")]
    public void Friendly_prettify_khi_truot_map(string key, string expected)
        => Assert.Equal(expected, JsonPlannerAgent.Friendly(key));
}
