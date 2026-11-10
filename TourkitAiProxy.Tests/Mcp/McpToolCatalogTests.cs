using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Domain.Mcp;
using TourkitAiProxy.Infrastructure.TourKit;
using TourkitAiProxy.Services.Chat;
using TourkitAiProxy.Services.Mcp;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>
/// Lớp nối SDK MCP: 20 tool đọc sinh từ ChatTools, và mọi lỗi (CRM, tham số, bất ngờ) phải thành
/// kết quả tool <c>isError</c> tiếng Việt — không bao giờ thành lỗi JSON-RPC hay lộ phiên/JWT.
/// </summary>
public class McpToolCatalogTests
{
    private static readonly McpCaller Caller = new(7, "sid-bi-mat-193c69ac", "staging", "admin");

    private static ChatTool Tool(string name) => ChatTools.Find(name)!;

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Text(CallToolResult r) => string.Concat(r.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static Task<CallToolResult> Run(ChatTool tool, JsonElement? args,
        Func<McpCaller, JsonElement?, CancellationToken, Task<CrmToolOutcome>> run, McpCaller? caller = null)
        => McpToolCatalog.ExecuteAsync(tool, caller ?? Caller, args, run, NullLogger.Instance, CancellationToken.None);

    private static Task<CrmToolOutcome> Ok(ChatTool tool, string path, string json)
    {
        var data = JsonDocument.Parse(json).RootElement.Clone();
        return Task.FromResult(new CrmToolOutcome(tool, path, data, ChatDataBuilder.Build(tool, data), false));
    }

    [Fact]
    public void Sinh_moi_tool_doc_tu_ChatTools_va_chi_doc()
    {
        var tools = McpToolCatalog.Create();
        Assert.Equal(ChatTools.All.Count, tools.Count);
        foreach (var (mcp, chat) in tools.Zip(ChatTools.All))
        {
            var t = mcp.ProtocolTool;
            Assert.Equal(chat.Name, t.Name);
            Assert.Equal(chat.Title, t.Title);
            Assert.Equal(chat.Description, t.Description);
            Assert.True(t.Annotations!.ReadOnlyHint);
            Assert.False(t.Annotations.OpenWorldHint);
            Assert.False(t.InputSchema.GetProperty("additionalProperties").GetBoolean());
        }
    }

    [Fact]
    public async Task Thanh_cong_tra_van_ban_va_structured()
    {
        var r = await Run(Tool("top_sellers"), Args("""{"startDate":"2026-11-01","endDate":"2026-11-30"}"""),
            (_, p, _) => Ok(Tool("top_sellers"), "/api/ai/top-sellers",
                """{"title":"Top Seller","total":1,"items":[{"fullName":"Lan","revenue":1000}]}"""));
        Assert.NotEqual(true, r.IsError);
        Assert.Contains("Lan", Text(r));
        Assert.NotNull(r.StructuredContent);
    }

    [Fact]
    public async Task Loi_CRM_sau_thu_lai_thanh_ket_qua_loi_tieng_Viet()
    {
        var r = await Run(Tool("cashflow"), null,
            (_, _, _) => throw new CrmRetryFailedException(new TourKitApiException("Bạn không có quyền xem báo cáo này", 403)));
        Assert.True(r.IsError);
        Assert.Contains("Bạn không có quyền xem báo cáo này", Text(r));
        Assert.DoesNotContain("sid-bi-mat", Text(r));
    }

    [Fact]
    public async Task Loi_bat_ngo_khong_lo_chi_tiet()
    {
        var r = await Run(Tool("cashflow"), null,
            (_, _, _) => throw new InvalidOperationException("jwt eyJhbGciOi sid-bi-mat-193c69ac"));
        Assert.True(r.IsError);
        Assert.DoesNotContain("eyJ", Text(r));
        Assert.DoesNotContain("sid-bi-mat", Text(r));
        Assert.Contains("thử lại", Text(r));
    }

    [Fact]
    public async Task Tham_so_sai_khong_goi_CRM()
    {
        var called = false;
        var r = await Run(Tool("cashflow"), Args("""{"startDate":"15/10/2026"}"""),
            (_, _, _) => { called = true; return Ok(Tool("cashflow"), "/x", "{}"); });
        Assert.True(r.IsError);
        Assert.False(called);
        Assert.Contains("yyyy-MM-dd", Text(r));
    }

    [Fact]
    public async Task Thieu_caller_thi_tu_choi()
    {
        var r = await McpToolCatalog.ExecuteAsync(Tool("cashflow"), null, null,
            (_, _, _) => throw new Exception("không được gọi"), NullLogger.Instance, CancellationToken.None);
        Assert.True(r.IsError);
    }

    [Fact]
    public async Task Khong_khop_thi_truong_thi_canh_bao_so_lieu_chua_loc()
    {
        // Runner không khớp tên thì lặng lẽ bỏ lọc → số liệu là của MỌI thị trường. Phải nói ra,
        // không thì mô hình trình bày số toàn công ty như số của thị trường người dùng hỏi.
        var r = await Run(Tool("market_analysis"), Args("""{"marketName":"Sao Hoả"}"""),
            (_, _, _) => Ok(Tool("market_analysis"), "/api/ai/market-analysis",
                """{"title":"Phân tích thị trường","items":[{"marketName":"Nhật","revenue":1}]}"""));
        Assert.NotEqual(true, r.IsError);
        Assert.Contains("Sao Hoả", Text(r));
        Assert.Contains("KHÔNG", Text(r));

        var ok = await Run(Tool("market_analysis"), Args("""{"marketName":"Nhật"}"""),
            (_, _, _) => Ok(Tool("market_analysis"), "/api/ai/market-analysis?marketId=3",
                """{"title":"Phân tích thị trường","items":[{"marketName":"Nhật","revenue":1}]}"""));
        Assert.DoesNotContain("KHÔNG lọc", Text(ok));
    }

    [Fact]
    public async Task Ten_nhan_vien_khong_khop_thi_bao_khong_loc_va_cat_ten_dai()
    {
        var tool = Tool("employee_performance");
        var longName = new string('A', 200);
        var args = Args("{\"employeeName\":\"" + longName + "\"}");
        var r = await Run(tool, args, (_, _, _) => Ok(tool, "/api/x?pageSize=20",
            """{"title":"T","count":0,"total":0,"items":[]}"""));
        var text = Text(r);
        Assert.StartsWith("⚠️ Không tìm thấy nhân viên", text);
        Assert.Contains("KHÔNG lọc theo nhân viên", text);
        Assert.DoesNotContain(new string('A', 61), text);
    }

    [Fact]
    public async Task Canh_bao_dat_truoc_van_nam_trong_gioi_han_ky_tu()
    {
        var tool = Tool("employee_performance");
        var rows = string.Join(",", Enumerable.Range(1, 20).Select(i => "{\"id\":" + i + ",\"fullName\":\"" + new string('x', 3000) + "\"}"));
        var args = Args("""{"employeeName":"Lan"}""");
        var r = await Run(tool, args, (_, _, _) => Ok(tool, "/api/x",
            "{\"title\":\"T\",\"count\":20,\"total\":20,\"items\":[" + rows + "]}"));
        var text = Text(r);
        Assert.StartsWith("⚠️", text);
        Assert.True(text.Length <= McpResultFormatter.MaxChars);
    }
}
