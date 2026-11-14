using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Infrastructure.Mcp;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

public class McpOAuthStoreTests
{
    private static string Src(string rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "TourkitAiProxy.csproj"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, rel));
    }

    private static string N(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");

    private const string RepoPath = "TourkitAiProxy.Infrastructure/Mcp/McpOAuthRepository.cs";

    /// <summary>Lấy thân một phương thức theo dấu "-- X:" tới dấu kế tiếp / hết tệp.</summary>
    private static string Block(string src, string marker)
    {
        var i = src.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, marker);
        var j = src.IndexOf("public async", i, StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(j < 0 ? src[i..] : src[i..j], @"\s+", " ");
    }

    private static McpOAuthNewGrant NewGrant(string tenant = "t1", string user = "a", string acc = "A1", string refr = "R1")
        => new("cid", "Claude", tenant, user, "sid", "https://x/mcp", "mcp", acc, DateTime.UtcNow.AddHours(1), refr, DateTime.UtcNow.AddDays(30));

    [Fact]
    public async Task Ma_chi_doi_duoc_mot_lan()
    {
        var s = new FakeMcpOAuthStore();
        await s.SaveCodeAsync(new McpOAuthCode("h1", "cid", "https://cb", "ch", "res", "mcp", "t1", "a", "sid", DateTime.UtcNow.AddMinutes(5), null), default);
        Assert.NotNull(await s.ConsumeCodeAsync("h1", default));
        Assert.Null(await s.ConsumeCodeAsync("h1", default));
    }

    [Fact]
    public async Task Ma_het_han_khong_doi_duoc()
    {
        var s = new FakeMcpOAuthStore();
        await s.SaveCodeAsync(new McpOAuthCode("h1", "cid", "https://cb", "ch", "res", "mcp", "t1", "a", "sid", DateTime.UtcNow.AddMinutes(-1), null), default);
        Assert.Null(await s.ConsumeCodeAsync("h1", default));
    }

    [Fact]
    public async Task Xoay_refresh_hop_le_roi_tai_su_dung_ban_cu_bi_phat_hien()
    {
        var s = new FakeMcpOAuthStore();
        await s.CreateGrantAsync(NewGrant(), default);
        var r1 = await s.RotateRefreshAsync("R1", "A2", DateTime.UtcNow.AddHours(1), "R2", DateTime.UtcNow.AddDays(30), default);
        Assert.Equal(McpRefreshStatus.Rotated, r1.Status);
        Assert.Null(await s.FindGrantByAccessAsync("A1", default));
        Assert.NotNull(await s.FindGrantByAccessAsync("A2", default));

        var reuse = await s.RotateRefreshAsync("R1", "A3", DateTime.UtcNow.AddHours(1), "R3", DateTime.UtcNow.AddDays(30), default);
        Assert.Equal(McpRefreshStatus.Reused, reuse.Status);
        Assert.NotNull(reuse.Grant);

        var unknown = await s.RotateRefreshAsync("zzz", "A4", DateTime.UtcNow.AddHours(1), "R4", DateTime.UtcNow.AddDays(30), default);
        Assert.Equal(McpRefreshStatus.NotFound, unknown.Status);
    }

    [Fact]
    public async Task Xoay_refresh_tren_grant_da_thu_hoi_khong_xoay_duoc()
    {
        var s = new FakeMcpOAuthStore();
        var id = await s.CreateGrantAsync(NewGrant(), default);
        Assert.True(await s.RevokeGrantAsync(id, "t1", "a", default));
        var r = await s.RotateRefreshAsync("R1", "A2", DateTime.UtcNow.AddHours(1), "R2", DateTime.UtcNow.AddDays(30), default);
        Assert.Equal(McpRefreshStatus.NotFound, r.Status);
        Assert.Null(r.Grant);
    }

    [Fact]
    public async Task Xoay_refresh_het_han_khong_xoay_duoc()
    {
        var s = new FakeMcpOAuthStore();
        await s.CreateGrantAsync(NewGrant() with { RefreshExpiresUtc = DateTime.UtcNow.AddMinutes(-1) }, default);
        var r = await s.RotateRefreshAsync("R1", "A2", DateTime.UtcNow.AddHours(1), "R2", DateTime.UtcNow.AddDays(30), default);
        Assert.NotEqual(McpRefreshStatus.Rotated, r.Status);
        Assert.NotNull(await s.FindGrantByAccessAsync("A1", default));
    }

    [Fact]
    public void Guard_Ma_bam_gui_kieu_ANSI_64_de_khong_CONVERT_IMPLICIT()
    {
        var repo = Src(RepoPath);
        Assert.Contains("IsAnsi = true, IsFixedLength = true, Length = 64", N(repo));
        Assert.Contains("accessHash = H(accessHash)", repo);
        Assert.Contains("codeHash = H(codeHash)", repo);
    }

    [Fact]
    public async Task Thu_hoi_va_liet_ke_chi_trong_pham_vi_chu_so_huu()
    {
        var s = new FakeMcpOAuthStore();
        var id = await s.CreateGrantAsync(NewGrant(), default);
        Assert.False(await s.RevokeGrantAsync(id, "t1", "b", default));
        Assert.False(await s.RevokeGrantAsync(id, "t2", "a", default));
        Assert.Empty(await s.ListGrantsAsync("t1", "b", default));
        Assert.Single(await s.ListGrantsAsync("t1", "a", default));
        Assert.True(await s.RevokeGrantAsync(id, "t1", "a", default));
        Assert.Null(await s.FindGrantByAccessAsync("A1", default));
    }

    [Fact]
    public void Guard_SQL_ConsumeCode_nguyen_tu_mot_cau()
    {
        var b = Block(Src(RepoPath), "-- CONSUME");
        Assert.Contains("UPDATE dbo.McpOAuthCodes SET UsedUtc = SYSUTCDATETIME()", b);
        Assert.Contains("OUTPUT inserted.", b);
        Assert.Contains("UsedUtc IS NULL", b);
        Assert.Contains("ExpiresUtc > SYSUTCDATETIME()", b);
    }

    [Fact]
    public void Guard_SQL_Rotate_nguyen_tu_va_khong_thu_hoi()
    {
        var b = Block(Src(RepoPath), "-- ROTATE");
        Assert.Contains("PrevRefreshHash = RefreshHash", b);
        Assert.Contains("OUTPUT", b);
        Assert.Contains("RevokedUtc IS NULL", b);
        Assert.Contains("PrevRefreshHash = @oldRefreshHash", b);
        Assert.Contains("RefreshExpiresUtc > SYSUTCDATETIME()", b);
        Assert.Contains("RefreshHash = @oldRefreshHash AND RevokedUtc IS NULL", b);
    }

    [Fact]
    public void Guard_SQL_List_Revoke_kep_TenantId_Username()
    {
        var repo = Src(RepoPath);
        foreach (var marker in new[] { "-- LIST", "-- REVOKE" })
        {
            var b = Block(repo, marker);
            Assert.Contains("TenantId = @tenant", b);
            Assert.Contains("Username = @username", b);
        }
    }

    [Fact]
    public void Guard_SQL_Tra_theo_bam_phai_chua_thu_hoi()
    {
        var b = Block(Src(RepoPath), "-- FIND");
        Assert.Contains("AccessHash = @accessHash AND RevokedUtc IS NULL", b);
    }

    [Fact]
    public void Guard_Schema_chi_muc_loc_va_chi_luu_bam()
    {
        var schema = Src("TourkitAiProxy.Infrastructure/Db/TourkitAiDb.cs");
        Assert.Contains("IF OBJECT_ID('dbo.McpOAuthClients', 'U') IS NULL", schema);
        Assert.Contains("IF OBJECT_ID('dbo.McpOAuthCodes', 'U') IS NULL", schema);
        Assert.Contains("IF OBJECT_ID('dbo.McpOAuthGrants', 'U') IS NULL", schema);
        Assert.Contains("ON dbo.McpOAuthGrants(AccessHash) WHERE AccessHash IS NOT NULL", N(schema));
        Assert.Contains("ON dbo.McpOAuthGrants(RefreshHash) WHERE RefreshHash IS NOT NULL", N(schema));
        Assert.Contains("ON dbo.McpOAuthGrants(TenantId, Username)", N(schema));
        Assert.DoesNotContain("DROP TABLE dbo.McpOAuth", schema);
        Assert.DoesNotContain("SELECT *", Src(RepoPath));
    }

    [Fact]
    public void Guard_Da_dang_ky_trong_AddMcp()
    {
        var reg = Src("TourkitAiProxy.Services/Bootstrap/WebFeatureRegistration.cs");
        Assert.Contains("IMcpOAuthStore, Infrastructure.Mcp.McpOAuthRepository", reg);
        Assert.Equal(OAuthRules.Hash("x"), McpTokens.Hash("x"));
    }
}
