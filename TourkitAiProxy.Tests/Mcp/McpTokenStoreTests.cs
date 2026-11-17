using TourkitAiProxy.Domain.Mcp;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

public class McpTokenStoreTests
{
    private static string Src(string rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, rel));
    }

    [Fact]
    public async Task Khoa_thu_hoi_khong_dung_duoc_nua()
    {
        var s = new FakeMcpTokenStore();
        var (id, token) = await s.CreateAsync("t1", "a", "sid", "may toi", default);
        Assert.NotNull(await s.FindActiveAsync(McpTokens.Hash(token), default));

        Assert.False(await s.RevokeAsync(id, "t1", "b", default));      // người khác không thu hồi được
        Assert.False(await s.RevokeAsync(id, "t2", "a", default));      // tenant khác cũng không
        Assert.NotNull(await s.FindActiveAsync(McpTokens.Hash(token), default));

        Assert.True(await s.RevokeAsync(id, "t1", "a", default));
        Assert.Null(await s.FindActiveAsync(McpTokens.Hash(token), default));
        var list = await s.ListAsync("t1", "a", default);
        Assert.NotNull(Assert.Single(list).RevokedUtc);
    }

    [Fact]
    public void Chi_luu_bam_khong_luu_khoa()
    {
        var repo = Src("TourkitAiProxy.Infrastructure/Mcp/McpTokenRepository.cs");
        var schema = Src("TourkitAiProxy.Infrastructure/Db/TourkitAiDb.cs");
        var i = schema.IndexOf("dbo.McpTokens", StringComparison.Ordinal);
        Assert.True(i >= 0);
        var block = schema.Substring(i, Math.Min(900, schema.Length - i));
        Assert.Contains("TokenHash", block);
        Assert.DoesNotContain("Token ", block.Replace("TokenHash", ""));
        Assert.DoesNotContain("@Token,", repo.Replace("@TokenHash", ""));
        Assert.DoesNotContain("SELECT *", repo);
    }

    [Fact]
    public void Guard_cau_SQL_kep_TenantId_Username()
    {
        var repo = Src("TourkitAiProxy.Infrastructure/Mcp/McpTokenRepository.cs");
        foreach (var marker in new[] { "-- LIST", "-- REVOKE" })
        {
            var i = repo.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(i >= 0, marker);
            var sql = repo.Substring(i, repo.IndexOf("\";", i) - i);
            Assert.Contains("TenantId = @tenant", sql);
            Assert.Contains("Username = @username", sql);
        }
        var f = repo.IndexOf("-- FIND", StringComparison.Ordinal);
        Assert.Contains("RevokedUtc IS NULL", repo.Substring(f, repo.IndexOf("\";", f) - f));
    }
}
