using TourkitAiProxy.Services.Chat;
using Xunit;

namespace TourkitAiProxy.Tests;

/// Khóa hành vi JsonPlannerAgent.DetectCompareIntent — nhận diện câu hỏi có ý SO SÁNH kỳ
/// ("so với tháng trước", "cùng kỳ năm ngoái") để dịch params lấy kỳ đối chiếu.
///
/// LỊCH SỬ: giá trị này từng đi vào L2 cache key ("|cmp=") để vá bug so sánh (ca2d68f) — câu so sánh
/// bị trả nguyên văn đáp án câu thường. Ngày 2026-08-11 đã BỎ HẲN cache câu trả lời AI (r1/r2) vì
/// key nào cũng thiếu chiều (đã thủng 3 lần: ý so sánh, ngữ cảnh hội thoại, focus doanh thu/chi phí).
/// DetectCompareIntent vẫn sống vì LOGIC so sánh cần nó — chỉ không còn liên quan tới cache.
public class CompareIntentTests
{
    [Theory]
    // Câu SO SÁNH → shift ≠ None (có kỳ đối chiếu để dịch params).
    [InlineData("Doanh thu tháng này so với tháng trước")]
    [InlineData("so sánh doanh thu tháng trước")]
    [InlineData("Doanh thu so với cùng kỳ năm ngoái")]
    [InlineData("Lợi nhuận quý này so với quý trước")]
    [InlineData("compare revenue last month")]
    public void Compare_questions_yield_non_none_shift(string q)
        => Assert.NotEqual(JsonPlannerAgent.CompareShift.None, JsonPlannerAgent.DetectCompareIntent(q));

    [Theory]
    // Câu THƯỜNG (không so sánh) → None, không dịch params.
    [InlineData("Doanh thu tháng này")]
    [InlineData("Doanh thu tháng này thế nào?")]
    [InlineData("Top khách hàng tháng này")]
    [InlineData("")]
    public void Plain_questions_yield_none_shift(string q)
        => Assert.Equal(JsonPlannerAgent.CompareShift.None, JsonPlannerAgent.DetectCompareIntent(q));

    [Fact]
    // Cặp câu từng gây bug phải ra 2 shift KHÁC nhau (câu sau có kỳ đối chiếu, câu trước không).
    public void The_bug_pair_produces_different_shifts()
    {
        var plain   = JsonPlannerAgent.DetectCompareIntent("Doanh thu tháng này");
        var compare = JsonPlannerAgent.DetectCompareIntent("Doanh thu tháng này so với tháng trước");
        Assert.NotEqual(plain, compare);
    }

    [Fact]
    // Phân biệt đúng hướng dịch: "năm ngoái" ≠ "tháng trước" → 2 kỳ đối chiếu khác nhau.
    public void Year_and_month_compare_are_distinct()
        => Assert.NotEqual(
            JsonPlannerAgent.DetectCompareIntent("so với cùng kỳ năm ngoái"),
            JsonPlannerAgent.DetectCompareIntent("so với tháng trước"));

    [Fact]
    // "quý trước" phải ra kỳ riêng, không lẫn với tháng/năm.
    public void Quarter_compare_is_distinct()
    {
        var quy = JsonPlannerAgent.DetectCompareIntent("so với quý trước");
        Assert.NotEqual(JsonPlannerAgent.CompareShift.None, quy);
        Assert.NotEqual(JsonPlannerAgent.DetectCompareIntent("so với tháng trước"), quy);
    }
}
