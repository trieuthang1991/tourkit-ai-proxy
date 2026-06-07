// TourkitAiProxy.Tests/Chat/AgentCacheKeysTests.cs
using TourkitAiProxy.Services.Chat;
using System.Text.Json;

namespace TourkitAiProxy.Tests.Chat;

public class AgentCacheKeysTests
{
    [Theory]
    [InlineData("Doanh thu tháng này", "doanh thu thang nay")]
    [InlineData("  DOANH THU   tháng này  ", "doanh thu thang nay")]
    [InlineData("Đặt tour Hà Nội", "dat tour ha noi")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_lowercases_strips_diacritics_collapses_whitespace(string? input, string expected)
    {
        Assert.Equal(expected, AgentCacheKeys.Normalize(input));
    }

    [Fact]
    public void CanonicalParams_sorts_keys_alphabetically()
    {
        var json = JsonDocument.Parse("""{"endDate":"2026-06-07","startDate":"2026-01-01"}""").RootElement;
        var canon = AgentCacheKeys.CanonicalParams(json);
        Assert.Equal("endDate=2026-06-07;startDate=2026-01-01", canon);
    }

    [Fact]
    public void CanonicalParams_handles_null_and_empty()
    {
        Assert.Equal("", AgentCacheKeys.CanonicalParams(null));
        var empty = JsonDocument.Parse("{}").RootElement;
        Assert.Equal("", AgentCacheKeys.CanonicalParams(empty));
    }

    [Fact]
    public void CanonicalParams_lowercases_string_values_except_marketName()
    {
        var json = JsonDocument.Parse("""{"marketName":"Bắc Âu","groupBy":"MONTH"}""").RootElement;
        var canon = AgentCacheKeys.CanonicalParams(json);
        // groupBy lowercased, marketName giữ nguyên
        Assert.Contains("groupBy=month", canon);
        Assert.Contains("marketName=Bắc Âu", canon);
    }
}
