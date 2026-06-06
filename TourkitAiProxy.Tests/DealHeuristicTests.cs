using TourkitAiProxy.Services.Deals;
using Xunit;

namespace TourkitAiProxy.Tests;

/// Test logic xếp hạng thuần (DealHeuristic) — chống regression công thức ưu tiên.
public class DealHeuristicTests
{
    [Theory]
    [InlineData("Đàm phán", 0.9)]
    [InlineData("dam phan", 0.9)]        // không dấu vẫn match
    [InlineData("Chờ đặt cọc", 0.9)]
    [InlineData("Đã báo giá", 0.65)]
    [InlineData("Đang tư vấn", 0.45)]
    [InlineData("Đang chăm sóc", 0.45)]
    [InlineData("Mới", 0.2)]
    [InlineData("Tiếp nhận", 0.2)]
    public void StageScore_maps_status_keywords(string name, double expected)
        => Assert.Equal(expected, DealHeuristic.StageScore(name, 2), 3);

    [Fact]
    public void StageScore_unknown_falls_back_by_int()
    {
        Assert.Equal(0.2, DealHeuristic.StageScore("Trạng thái lạ", 1), 3);
        Assert.Equal(0.5, DealHeuristic.StageScore("Trạng thái lạ", 3), 3);
        Assert.Equal(0.5, DealHeuristic.StageScore(null, 4), 3);
    }

    [Fact]
    public void ValueScore_is_zero_for_nonpositive_and_clamped_to_one()
    {
        Assert.Equal(0, DealHeuristic.ValueScore(0));
        Assert.Equal(0, DealHeuristic.ValueScore(-5));
        Assert.InRange(DealHeuristic.ValueScore(10_000_000_000), 0.0, 1.0);
    }

    [Fact]
    public void ValueScore_is_monotonic_increasing()
        => Assert.True(DealHeuristic.ValueScore(50_000_000) > DealHeuristic.ValueScore(5_000_000));

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(21, 1.0)]
    [InlineData(60, 1.0)]   // cap
    public void UrgencyScore_rises_then_caps(int age, double expected)
        => Assert.Equal(expected, DealHeuristic.UrgencyScore(age), 2);

    [Theory]
    [InlineData(20, null)]
    [InlineData(21, "nguoi")]
    [InlineData(45, "nguoi")]
    public void RiskFlag_flags_cold_deals(int age, string? expected)
        => Assert.Equal(expected, DealHeuristic.RiskFlag(age));

    [Fact]
    public void FinalPriority_computes_expected_value()
    {
        var (_, ev) = DealHeuristic.FinalPriority(50, 100_000_000, 10);
        Assert.Equal(50_000_000, ev);
    }

    [Fact]
    public void FinalPriority_dampens_dead_deals()
    {
        // Cùng giá trị + tuổi: deal khả năng thắng CAO phải ưu tiên hơn deal gần chết (win thấp).
        var high = DealHeuristic.FinalPriority(70, 100_000_000, 30).Priority;
        var dead = DealHeuristic.FinalPriority(5, 100_000_000, 30).Priority;
        Assert.True(high > dead, $"Kỳ vọng high>dead nhưng high={high}, dead={dead}");
    }

    [Theory]
    [InlineData("Đàm Phán", "dam phan")]
    [InlineData("Nội địa - Miền Nam", "noi dia - mien nam")]
    [InlineData(null, "")]
    public void Normalize_strips_vietnamese_diacritics(string? input, string expected)
        => Assert.Equal(expected, DealHeuristic.Normalize(input));
}
