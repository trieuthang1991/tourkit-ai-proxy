using System.Text.Json;
using TourkitAiProxy.Services.Chat;
using Xunit;

namespace TourkitAiProxy.Tests;

/// Khóa hành vi JsonPlannerAgent.DetectCompareIntent — giá trị này đi vào L2 cache key ("|cmp=").
/// Bug so sánh (2026-08): L2 key CŨ chỉ gồm tool+params kỳ-chính, KHÔNG gồm ý so sánh → hỏi
/// "doanh thu tháng này" rồi "doanh thu tháng này so với tháng trước" ra CÙNG key → L2 HIT trả
/// nguyên văn câu trước + nuốt mất cột so sánh. Fix: câu có so sánh phải ra shift ≠ None ⇒ key khác.
public class CompareIntentTests
{
    [Theory]
    // Câu SO SÁNH → phải ra shift ≠ None (để rơi vào ô cache khác câu thường cùng tool+params).
    [InlineData("Doanh thu tháng này so với tháng trước")]
    [InlineData("so sánh doanh thu tháng trước")]
    [InlineData("Doanh thu so với cùng kỳ năm ngoái")]
    [InlineData("Lợi nhuận quý này so với quý trước")]
    [InlineData("compare revenue last month")]
    public void Compare_questions_yield_non_none_shift(string q)
        => Assert.NotEqual(JsonPlannerAgent.CompareShift.None, JsonPlannerAgent.DetectCompareIntent(q));

    [Theory]
    // Câu THƯỜNG (không so sánh) → None. Đây là ô cache "gốc", không được lẫn với ô so sánh.
    [InlineData("Doanh thu tháng này")]
    [InlineData("Doanh thu tháng này thế nào?")]
    [InlineData("Top khách hàng tháng này")]
    [InlineData("")]
    public void Plain_questions_yield_none_shift(string q)
        => Assert.Equal(JsonPlannerAgent.CompareShift.None, JsonPlannerAgent.DetectCompareIntent(q));

    [Fact]
    // Cốt lõi của fix: chính cặp câu gây bug phải ra 2 shift KHÁC nhau → 2 L2 key khác nhau.
    public void The_bug_pair_produces_different_shifts()
    {
        var plain   = JsonPlannerAgent.DetectCompareIntent("Doanh thu tháng này");
        var compare = JsonPlannerAgent.DetectCompareIntent("Doanh thu tháng này so với tháng trước");
        Assert.NotEqual(plain, compare);   // khác nhau ⇒ "|cmp=" khác ⇒ không đè cache
    }

    [Fact]
    // Phân biệt đúng hướng dịch: "năm ngoái" ≠ "tháng trước" → 2 kỳ đối chiếu khác nhau.
    public void Year_and_month_compare_are_distinct()
        => Assert.NotEqual(
            JsonPlannerAgent.DetectCompareIntent("so với cùng kỳ năm ngoái"),
            JsonPlannerAgent.DetectCompareIntent("so với tháng trước"));

    // ── Tầng L2 KEY thật (không chỉ bộ phân loại) — khóa đúng cái sinh ra bug ──

    private const string T = "tenant1";
    private const string U = "user1";
    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    // Cùng tool + cùng params kỳ chính, chỉ khác ý so sánh → 2 KEY phải khác (đây là gốc bug).
    public void L2Key_differs_between_compare_and_plain_with_same_params()
    {
        var prms = P("{\"startDate\":\"2026-08-01\",\"endDate\":\"2026-08-31\"}");
        var plain   = JsonPlannerAgent.L2CacheKey(T, U, "cashflow", prms, JsonPlannerAgent.CompareShift.None);
        var compare = JsonPlannerAgent.L2CacheKey(T, U, "cashflow", prms, JsonPlannerAgent.CompareShift.PrevMonth);
        Assert.NotEqual(plain, compare);
    }

    [Fact]
    // Cache hợp lệ vẫn phải chạy: cùng input → cùng key (lặp câu y hệt vẫn được cache tăng tốc).
    public void L2Key_is_stable_for_identical_inputs()
    {
        var prms = P("{\"startDate\":\"2026-08-01\"}");
        Assert.Equal(
            JsonPlannerAgent.L2CacheKey(T, U, "cashflow", prms, JsonPlannerAgent.CompareShift.None),
            JsonPlannerAgent.L2CacheKey(T, U, "cashflow", prms, JsonPlannerAgent.CompareShift.None));
    }

    [Fact]
    // ĐÚNG luồng production: question → DetectCompareIntent → L2CacheKey. Cặp câu gây bug → 2 key khác.
    // Nếu ai revert fix (bỏ compareShift khỏi key) → test này ĐỎ ngay.
    public void End_to_end_bug_pair_yields_different_L2_keys()
    {
        var prms = P("{\"startDate\":\"2026-08-01\",\"endDate\":\"2026-08-31\"}");
        string KeyFor(string q) =>
            JsonPlannerAgent.L2CacheKey(T, U, "cashflow", prms, JsonPlannerAgent.DetectCompareIntent(q));

        Assert.NotEqual(
            KeyFor("Doanh thu tháng này"),
            KeyFor("Doanh thu tháng này so với tháng trước"));
    }

    [Fact]
    // Cross-user không rò: cùng câu, khác user → khác key (giữ nguyên tính chất của L2Key gốc).
    public void L2Key_isolates_users()
    {
        var prms = P("{\"startDate\":\"2026-08-01\"}");
        Assert.NotEqual(
            JsonPlannerAgent.L2CacheKey(T, "userA", "cashflow", prms, JsonPlannerAgent.CompareShift.None),
            JsonPlannerAgent.L2CacheKey(T, "userB", "cashflow", prms, JsonPlannerAgent.CompareShift.None));
    }
}
