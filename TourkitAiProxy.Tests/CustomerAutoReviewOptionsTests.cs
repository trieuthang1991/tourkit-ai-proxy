using TourkitAiProxy.Services.Workflows;
using Xunit;

namespace TourkitAiProxy.Tests;

public class CustomerAutoReviewOptionsTests
{
    [Fact]
    public void Parse_Null_ReturnsDefaults()
    {
        var o = CustomerAutoReviewOptions.Parse(null);
        Assert.Equal(30, o.CreatedWithinDays);
        Assert.True(o.ReReview);
        Assert.Equal(30, o.ReReviewDays);
    }

    [Fact]
    public void Parse_ReadsFields()
    {
        var o = CustomerAutoReviewOptions.Parse("{\"createdWithinDays\":60,\"reReview\":false,\"reReviewDays\":14}");
        Assert.Equal(60, o.CreatedWithinDays);
        Assert.False(o.ReReview);
        Assert.Equal(14, o.ReReviewDays);
    }

    [Fact]
    public void Parse_Clamps()
    {
        var o = CustomerAutoReviewOptions.Parse("{\"createdWithinDays\":9999,\"reReviewDays\":0}");
        Assert.Equal(365, o.CreatedWithinDays);
        Assert.Equal(1, o.ReReviewDays);
    }

    [Fact]
    public void Parse_Garbage_Defaults()
    {
        var o = CustomerAutoReviewOptions.Parse("xxx");
        Assert.Equal(30, o.CreatedWithinDays);
    }
}
