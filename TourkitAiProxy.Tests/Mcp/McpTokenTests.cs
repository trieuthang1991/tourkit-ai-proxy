using System.Text.RegularExpressions;
using TourkitAiProxy.Domain.Mcp;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>Luật thuần của khoá MCP: dạng khoá và băm. Phần lưu trữ ở McpTokenStoreTests.</summary>
public class McpTokenTests
{
    [Fact]
    public void Khoa_dung_dinh_dang_tkmcp_32_hex()
    {
        var t = McpTokens.New();
        Assert.Matches(new Regex("^tkmcp_[0-9a-f]{32}$"), t);
        Assert.True(McpTokens.LooksValid(t));
        Assert.False(McpTokens.LooksValid(null));
        Assert.False(McpTokens.LooksValid(""));
        Assert.False(McpTokens.LooksValid("trav_" + new string('a', 32)));
        Assert.False(McpTokens.LooksValid("tkmcp_" + new string('g', 32)));
        Assert.False(McpTokens.LooksValid("tkmcp_abc"));
    }

    [Fact]
    public void Hai_lan_tao_ra_hai_khoa_khac_nhau()
        => Assert.NotEqual(McpTokens.New(), McpTokens.New());

    [Fact]
    public void Bam_la_sha256_hex_64_ky_tu_on_dinh()
    {
        var h = McpTokens.Hash("tkmcp_abc");
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), h);
        Assert.Equal(h, McpTokens.Hash("tkmcp_abc"));
        Assert.NotEqual(h, McpTokens.Hash("tkmcp_abd"));
    }
}
