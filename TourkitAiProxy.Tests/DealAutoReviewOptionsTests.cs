using TourkitAiProxy.Services.Workflows;
using Xunit;

namespace TourkitAiProxy.Tests;

/// Test pure-logic Parse options của DealAutoReviewWorkflow (clamp + default + statuses).
public class DealAutoReviewOptionsTests
{
    [Fact]
    public void Parse_Null_ReturnsSafeDefaults()
    {
        var o = DealAutoReviewOptions.Parse(null);
        Assert.True(o.AutoReview);
        Assert.Equal(30, o.CreatedWithinDays);
        Assert.Equal(20, o.ReviewMax);
        Assert.Equal(5, o.MaxAutoReviews);
        Assert.Equal(7, o.CoolingDays);
        Assert.Equal(0, o.MinWinRateToNotify);
        Assert.Equal(3, o.MaxNotifications);
        Assert.Equal(24, o.NotifyMinGapHours);
        Assert.Empty(o.Statuses);   // rỗng = mọi trạng thái
    }

    [Fact]
    public void Parse_Garbage_FallsBackToDefaults()
    {
        var o = DealAutoReviewOptions.Parse("not json {{{");
        Assert.True(o.AutoReview);
        Assert.Equal(30, o.CreatedWithinDays);
    }

    [Fact]
    public void Parse_FullObject_ReadsAllFields()
    {
        var json = "{\"statuses\":[1,3],\"createdWithinDays\":14,\"autoReview\":false,\"reviewMax\":10," +
                   "\"maxAutoReviews\":2,\"coolingDays\":5,\"minWinRateToNotify\":50,\"maxNotifications\":2,\"notifyMinGapHours\":48}";
        var o = DealAutoReviewOptions.Parse(json);
        Assert.Equal(new[] { 1, 3 }, o.Statuses);
        Assert.Equal(14, o.CreatedWithinDays);
        Assert.False(o.AutoReview);
        Assert.Equal(10, o.ReviewMax);
        Assert.Equal(2, o.MaxAutoReviews);
        Assert.Equal(5, o.CoolingDays);
        Assert.Equal(50, o.MinWinRateToNotify);
        Assert.Equal(2, o.MaxNotifications);
        Assert.Equal(48, o.NotifyMinGapHours);
    }

    [Fact]
    public void Parse_ClampsOutOfRange()
    {
        var o = DealAutoReviewOptions.Parse(
            "{\"createdWithinDays\":9999,\"reviewMax\":0,\"coolingDays\":-5,\"minWinRateToNotify\":200,\"notifyMinGapHours\":99999}");
        Assert.Equal(365, o.CreatedWithinDays);   // max 365
        Assert.Equal(1, o.ReviewMax);             // min 1
        Assert.Equal(1, o.CoolingDays);           // min 1
        Assert.Equal(100, o.MinWinRateToNotify);  // max 100
        Assert.Equal(720, o.NotifyMinGapHours);   // max 720
    }

    [Fact]
    public void Parse_Statuses_DropsNonPositiveAndNonInt()
    {
        var o = DealAutoReviewOptions.Parse("{\"statuses\":[1,0,-2,5]}");
        Assert.Equal(new[] { 1, 5 }, o.Statuses);   // bỏ 0 và -2
    }

    // Mới: cảnh báo nguội mặc định BẬT (không breaking tenant đang chạy).
    [Fact]
    public void Parse_Null_AlertCoolingDefaultsOn()
        => Assert.True(DealAutoReviewOptions.Parse(null).AlertCooling);

    // Mới: tenant tắt cảnh báo nguội tường minh.
    [Fact]
    public void Parse_AlertCoolingCanBeDisabled()
        => Assert.False(DealAutoReviewOptions.Parse("{\"alertCooling\":false}").AlertCooling);

    // Mới: reReviewDays vắng (config CŨ) → giữ hành vi cũ = createdWithin/3.
    [Fact]
    public void Parse_ReReviewDays_AbsentFallsBackToCreatedWithinThird()
    {
        Assert.Equal(10, DealAutoReviewOptions.Parse("{\"createdWithinDays\":30}").ReReviewDays);   // 30/3
        Assert.Equal(20, DealAutoReviewOptions.Parse("{\"createdWithinDays\":60}").ReReviewDays);   // 60/3
        Assert.Equal(1, DealAutoReviewOptions.Parse("{\"createdWithinDays\":2}").ReReviewDays);     // 2/3=0 → min 1
    }

    // Mới: reReviewDays cấu hình tường minh (UI mới) → dùng đúng giá trị, clamp 1..365.
    [Fact]
    public void Parse_ReReviewDays_ExplicitWinsAndClamps()
    {
        Assert.Equal(7, DealAutoReviewOptions.Parse("{\"createdWithinDays\":30,\"reReviewDays\":7}").ReReviewDays);
        Assert.Equal(365, DealAutoReviewOptions.Parse("{\"reReviewDays\":9999}").ReReviewDays);
        Assert.Equal(1, DealAutoReviewOptions.Parse("{\"reReviewDays\":0}").ReReviewDays);
    }
}
