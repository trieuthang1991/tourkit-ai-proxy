// TourkitAiProxy.Tests/Chat/AgentGuardrailsTests.cs
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Tests.Chat;

public class AgentGuardrailsTests
{
    // ---------------------------------------------------------------
    // Task 4: StripEmDash
    // ---------------------------------------------------------------
    [Theory]
    [InlineData("Doanh thu — tháng này", "Doanh thu - tháng này")]
    [InlineData("Năm nay – cùng kỳ", "Năm nay - cùng kỳ")]
    [InlineData("Bình thường", "Bình thường")]
    [InlineData("", "")]
    public void StripEmDash_replaces_em_and_en_dash_with_hyphen(string input, string expected)
    {
        Assert.Equal(expected, AgentGuardrails.StripEmDash(input));
    }

    // ---------------------------------------------------------------
    // Task 5: TruncateInput + IsTooShort
    // ---------------------------------------------------------------
    [Theory]
    [InlineData("short", 100, "short", false)]
    [InlineData("a a a a a a a a a a a a a a a a a a a a", 10, "a a a a a", true)]
    public void TruncateInput_caps_at_maxLen(string input, int max, string expected, bool truncated)
    {
        var (result, wasTruncated) = AgentGuardrails.TruncateInput(input, max);
        Assert.Equal(expected, result);
        Assert.Equal(truncated, wasTruncated);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("ngắn", true)]
    [InlineData("Đây là phản hồi đủ dài cho người đọc bình thường nắm bắt được", false)]
    public void IsTooShort_threshold_30_chars(string text, bool expected)
    {
        Assert.Equal(expected, AgentGuardrails.IsTooShort(text));
    }

    // ---------------------------------------------------------------
    // Task 6: ValidateNumbers
    // ---------------------------------------------------------------
    [Fact]
    public void ValidateNumbers_no_drift_returns_null()
    {
        var stats = new List<ChatStat> {
            new("Doanh thu", 1000000000, "d"),
            new("Loi nhuan", 200000000, "d")
        };
        var text = "Doanh thu dat 1.000.000.000 dong, loi nhuan 200 trieu.";
        var warning = AgentGuardrails.ValidateNumbers(text, stats);
        Assert.Null(warning);
    }

    [Fact]
    public void ValidateNumbers_large_drift_returns_warning()
    {
        var stats = new List<ChatStat> { new("Doanh thu", 1000000000, "d") };
        // AI bia so 5 ty trong khi thuc 1 ty (drift 400%)
        var text = "Doanh thu dat 5.000.000.000 dong thang nay.";
        var warning = AgentGuardrails.ValidateNumbers(text, stats);
        Assert.NotNull(warning);
        Assert.Contains("stat", warning);
    }

    [Fact]
    public void ValidateNumbers_empty_stats_returns_null()
    {
        var warning = AgentGuardrails.ValidateNumbers("anything", new List<ChatStat>());
        Assert.Null(warning);
    }
}
