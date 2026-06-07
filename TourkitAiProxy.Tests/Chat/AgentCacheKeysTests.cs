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

    [Fact]
    public void L1Key_same_inputs_same_key()
    {
        var k1 = AgentCacheKeys.L1Key("staging", "u1", "Doanh thu");
        var k2 = AgentCacheKeys.L1Key("staging", "u1", "  DOANH THU  ");
        Assert.Equal(k1, k2);
    }

    [Fact]
    public void L1Key_different_tenants_different_keys()
    {
        var k1 = AgentCacheKeys.L1Key("tenant-a", "u", "x");
        var k2 = AgentCacheKeys.L1Key("tenant-b", "u", "x");
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void L2Key_includes_tool_and_canonical_params()
    {
        var p = JsonDocument.Parse("""{"year":2026}""").RootElement;
        var k = AgentCacheKeys.L2Key("staging", "u1", "cashflow", p);
        Assert.StartsWith("staging|u1|cashflow|", k);
        Assert.Contains("year=2026", k);
    }

    [Fact]
    public void L1Key_different_users_same_tenant_different_keys()
    {
        var k1 = AgentCacheKeys.L1Key("tenant-a", "user1", "x");
        var k2 = AgentCacheKeys.L1Key("tenant-a", "user2", "x");
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void L2Key_different_users_same_tenant_different_keys()
    {
        var p = JsonDocument.Parse("""{"year":2026}""").RootElement;
        var k1 = AgentCacheKeys.L2Key("tenant-a", "user1", "cashflow", p);
        var k2 = AgentCacheKeys.L2Key("tenant-a", "user2", "cashflow", p);
        Assert.NotEqual(k1, k2);
    }
}
