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
}
