// TourkitAiProxy.Tests/Chat/AgentGuardrailsTests.cs
using TourkitAiProxy.Services.Chat;

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
}
