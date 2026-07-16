using System.Text.Json;
using TourkitAiProxy.Services.TourKit;
using Xunit;

namespace TourkitAiProxy.Tests.TourKit;

public class PermissionParseTests
{
    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ParsePermissions_reads_camelCase_array()
    {
        var el = Parse("""{"departmentId":3,"departmentName":"Sales","permissions":["CV_TAOMOI","CS_KH_TAOMOI"]}""");
        var got = TourKitApiClient.ParsePermissions(el);
        Assert.Equal(new[] { "CV_TAOMOI", "CS_KH_TAOMOI" }, got);
    }

    [Fact]
    public void ParsePermissions_trims_and_skips_blanks()
    {
        var el = Parse("""{"permissions":["  CH_HT_THAOTAC  ","",null,"CV_TAOMOI"]}""");
        var got = TourKitApiClient.ParsePermissions(el);
        Assert.Equal(new[] { "CH_HT_THAOTAC", "CV_TAOMOI" }, got);
    }

    [Fact]
    public void ParsePermissions_missing_field_returns_empty()
    {
        var el = Parse("""{"departmentId":0}""");
        Assert.Empty(TourKitApiClient.ParsePermissions(el));
    }
}
