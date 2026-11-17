using System.Text.Json;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Infrastructure.TourKit;
using TourkitAiProxy.Services.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Khóa bộ quy đổi tên → mã dùng chung cho trợ lý và MCP. Danh sách thị trường đưa vào qua seam
/// nên test không cần phiên TourKit thật.
/// </summary>
public class CrmToolRunnerTests
{
    private static readonly List<(int, string)> Markets = new()
    {
        (1, "Nhật Bản"), (2, "Hàn Quốc"), (3, "Châu Âu"),
    };

    private static CrmToolRunner Runner()
        => CrmToolRunner.ForTest(() => Task.FromResult(Markets));

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Quy_doi_marketName_thanh_marketId_va_bo_ten_khoi_query()
    {
        var kq = await Runner().ResolveNamesAsync("s", J("{\"marketName\":\"nhat ban\",\"top\":5}"), default);

        Assert.NotNull(kq);
        Assert.Equal(1, kq!.Value.GetProperty("marketId").GetInt32());
        Assert.False(kq.Value.TryGetProperty("marketName", out _));
        Assert.Equal("5", kq.Value.GetProperty("top").GetString());
    }

    [Fact]
    public async Task Khong_doi_khi_da_co_marketId()
    {
        var kq = await Runner().ResolveNamesAsync("s", J("{\"marketName\":\"Hàn Quốc\",\"marketId\":9}"), default);

        Assert.Equal("9", kq!.Value.GetProperty("marketId").GetString());   // số được giữ dạng chuỗi như trước
        Assert.False(kq.Value.TryGetProperty("marketName", out _));
    }

    [Fact]
    public async Task Ten_thi_truong_khong_khop_thi_giu_nguyen_khong_doan()
    {
        var kq = await Runner().ResolveNamesAsync("s", J("{\"marketName\":\"Sao Hỏa\"}"), default);

        Assert.False(kq!.Value.TryGetProperty("marketId", out _));
    }

    [Fact]
    public async Task Khong_co_ten_thi_tra_nguyen_params()
    {
        var p = J("{\"top\":3}");
        var kq = await Runner().ResolveNamesAsync("s", p, default);
        Assert.Equal(p.GetRawText(), kq!.Value.GetRawText());
        Assert.Null(await Runner().ResolveNamesAsync("s", null, default));
    }

    [Fact]
    public void Guard_JsonPlannerAgent_khong_con_resolver_rieng()
    {
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Services/Chat/JsonPlannerAgent.cs");
        Assert.DoesNotContain("private async Task<JsonElement?> ResolveMarketAsync", src);
        Assert.DoesNotContain("private async Task<JsonElement?> ResolveEmployeeAsync", src);
        Assert.DoesNotContain("private static int? MatchMarket", src);
    }

    private static readonly ChatTool AnyTool = ChatTools.All[0];

    [Fact]
    public async Task Loi_khong_phai_401_o_lan_dau_khong_phat_tin_hieu_thu_lai_hong()
    {
        var runner = CrmToolRunner.ForTest((relogin, _) => throw new TourKitApiException("boom", 500));

        var ex = await Assert.ThrowsAsync<TourKitApiException>(
            () => runner.RunAsync("s", "t", AnyTool, null, default));
        Assert.Equal(500, ex.Status);
    }

    [Fact]
    public async Task Thu_lai_sau_401_van_hong_thi_phat_tin_hieu_thu_lai_hong()
    {
        var goi = new List<bool>();
        var runner = CrmToolRunner.ForTest((relogin, _) =>
        {
            goi.Add(relogin);
            throw relogin ? new TimeoutException("het gio") : new TourKitApiException("het han", 401);
        });

        var ex = await Assert.ThrowsAsync<CrmRetryFailedException>(
            () => runner.RunAsync("s", "t", AnyTool, null, default));
        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Equal(new[] { false, true }, goi);   // đúng một lần thử lại
    }

    // ─── Khoá đệm dữ liệu theo từng người ───────────────────────────────────────

    [Fact]
    public void Khoa_dem_du_lieu_gom_tenant_nguoi_dung_va_duong_dan()
        => Assert.Equal("d|staging.tourkit.vn|u:admin|/api/ai/x?a=1",
            CrmToolRunner.DataCacheKey("staging.tourkit.vn", "Admin", "/api/ai/x?a=1"));

    [Fact]
    public void Hai_nguoi_cung_cong_ty_khac_khoa_dem()
        => Assert.NotEqual(CrmToolRunner.DataCacheKey("t", "admin", "/p"), CrmToolRunner.DataCacheKey("t", "adminthoa", "/p"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Khong_co_username_thi_khong_dem(string? user)
        => Assert.Null(CrmToolRunner.DataCacheKey("t", user, "/p"));

    [Fact]
    public void Khoa_dem_van_bat_dau_bang_d_tenant_de_xoa_cache_duoc()
        => Assert.StartsWith("d|staging.tourkit.vn|", CrmToolRunner.DataCacheKey("staging.tourkit.vn", "a", "/p"));

    [Fact]
    public void Guard_CrmToolRunner_khong_con_khoa_dem_chung_tenant()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "TourkitAiProxy.Services", "Chat", "CrmToolRunner.cs")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var src = File.ReadAllText(Path.Combine(dir!, "TourkitAiProxy.Services", "Chat", "CrmToolRunner.cs"));
        Assert.DoesNotContain("\"d|\" + $\"{tenantId}|{path}\"", src);
    }
}
