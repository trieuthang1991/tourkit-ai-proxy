using TourkitAiProxy.Services.Chat;
using Xunit;

public class ActionResolverTests
{
    [Theory]
    [InlineData("Nguyễn Văn A", "nguyen van a")]
    [InlineData("  Đặng   Minh ", "dang minh")]
    public void Norm_strips_diacritics_and_spaces(string input, string expected)
        => Assert.Equal(expected, ActionResolver.Norm(input));

    [Fact]
    public void TokenSubset_matches_partial_name()
    {
        Assert.True(ActionResolver.TokenSubsetMatch("Minh", "Đặng Văn Minh"));
        Assert.True(ActionResolver.TokenSubsetMatch("Nguyễn A", "Nguyễn Văn A"));
        Assert.False(ActionResolver.TokenSubsetMatch("Hoa", "Đặng Văn Minh"));
    }
}
