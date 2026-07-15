using TourkitAiProxy.Services.Deals;
using Xunit;

/// <summary>
/// Test cho <see cref="DealCooling"/> — định nghĩa "deal nguội" 1 nguồn.
/// Mỗi case = 1 tình huống thực tế (đặc biệt CASE 3 = bug gốc: deal "Hoàn thành" vẫn hiện nguội).
/// </summary>
public class DealCoolingTests
{
    static readonly int[] Empty = System.Array.Empty<int>();

    // CASE 1: trạng thái mở + CoolingDays ≥ ngưỡng → nguội
    [Fact]
    public void Open_and_stale_is_cooling()
        => Assert.True(DealCooling.IsCooling(status: 3, statusName: "Đang xử lý", coolingDays: 10, threshold: 7, coolingStatuses: Empty));

    // CASE 2: mở nhưng CoolingDays < ngưỡng → KHÔNG
    [Fact]
    public void Open_but_fresh_not_cooling()
        => Assert.False(DealCooling.IsCooling(3, "Đang xử lý", coolingDays: 3, threshold: 7, coolingStatuses: Empty));

    // CASE 3 (bug gốc): "Hoàn thành"/"Đã chốt"... + stale → KHÔNG nguội
    [Theory]
    [InlineData("Hoàn thành")]
    [InlineData("Đã chốt")]
    [InlineData("Đã chốt đơn")]
    [InlineData("Thành công")]
    [InlineData("Đã bán")]
    public void Closed_won_never_cooling(string statusName)
        => Assert.False(DealCooling.IsCooling(3, statusName, coolingDays: 40, threshold: 7, coolingStatuses: Empty));

    // CASE 4: Hủy (status=5) → KHÔNG
    [Fact]
    public void Cancelled_not_cooling()
        => Assert.False(DealCooling.IsCooling(status: 5, statusName: "Hủy", coolingDays: 40, threshold: 7, coolingStatuses: Empty));

    // CASE 5: coolingStatuses rỗng → keyword fallback (đóng/hủy loại trừ, còn lại tính)
    [Fact]
    public void Empty_config_uses_keyword_fallback()
    {
        Assert.True(DealCooling.IsCooling(2, "Chờ xử lý", 10, 7, Empty));   // mở → nguội
        Assert.False(DealCooling.IsCooling(2, "Đã chốt", 10, 7, Empty));    // chốt → không
    }

    // CASE 6: coolingStatuses có giá trị → CHỈ status trong list tính; ngoài list (kể cả mở) KHÔNG
    [Fact]
    public void Explicit_list_only_those_statuses()
    {
        var list = new[] { 2, 3 };
        Assert.True(DealCooling.IsCooling(2, "Chờ xử lý", 10, 7, list));         // 2 thuộc list → nguội
        Assert.False(DealCooling.IsCooling(4, "Đang giao dịch", 10, 7, list));   // 4 ngoài list → không (dù mở)
    }

    // CASE 7: list chứa status "đã chốt" (tenant cố ý) → vẫn tính (list thắng keyword)
    [Fact]
    public void Explicit_list_wins_over_keyword()
        => Assert.True(DealCooling.IsCooling(6, "Đã chốt", 10, 7, new[] { 6 }));

    // CASE 8: CoolingDays = 0 (thiếu/upstream chưa trả) → KHÔNG
    [Fact]
    public void Zero_cooling_days_not_cooling()
        => Assert.False(DealCooling.IsCooling(3, "Đang xử lý", coolingDays: 0, threshold: 7, coolingStatuses: Empty));

    // CASE 9: IsClosedWon bắt đúng, KHÔNG bắt nhầm "chưa chốt"/"sắp chốt"
    [Theory]
    [InlineData("Đã chốt", true)]
    [InlineData("Hoàn thành", true)]
    [InlineData("Thành công", true)]
    [InlineData("Chưa chốt", false)]
    [InlineData("Sắp chốt", false)]
    [InlineData("Đang xử lý", false)]
    public void IsClosedWon_matches_correctly(string statusName, bool expected)
        => Assert.Equal(expected, DealCooling.IsClosedWon(statusName));
}
