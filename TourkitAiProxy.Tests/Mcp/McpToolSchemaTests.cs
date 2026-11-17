using System.Text.Json;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Domain.Mcp;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>
/// Luật thuần của schema + ép kiểu tham số tool MCP. Máy khách MCP (Claude, Cursor…) đọc schema để biết
/// gửi gì; ép kiểu là chốt cuối trước khi chuỗi đi vào query của CRM.
/// </summary>
public class McpToolSchemaTests
{
    private static ChatTool Tool(string name) => ChatTools.Find(name)!;

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Moi_tham_so_trong_ChatTools_deu_co_kieu()
    {
        // Thêm tool mới vào ChatTools mà quên khai kiểu tham số ở McpToolSchemas → test này đỏ,
        // không để tham số lặng lẽ rơi về "chuỗi" rồi gửi "abc" vào cột số của CRM.
        var missing = ChatTools.All.SelectMany(t => t.Params)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !McpToolSchemas.ParamTypes.ContainsKey(p))
            .ToList();
        Assert.Empty(missing);

        foreach (var spec in McpToolSchemas.ParamTypes.Values)
            Assert.False(string.IsNullOrWhiteSpace(spec.Description));
    }

    [Fact]
    public void Kieu_tham_so_dung_bang_da_chot()
    {
        Assert.Equal(McpParamType.Date, McpToolSchemas.ParamTypes["startDate"].Type);
        Assert.Equal(McpParamType.Date, McpToolSchemas.ParamTypes["fromDate"].Type);
        Assert.Equal(McpParamType.Integer, McpToolSchemas.ParamTypes["pageSize"].Type);
        Assert.Equal(McpParamType.Integer, McpToolSchemas.ParamTypes["trangThai"].Type);
        Assert.Equal(McpParamType.Integer, McpToolSchemas.ParamTypes["careFilter"].Type);
        Assert.Equal(McpParamType.Boolean, McpToolSchemas.ParamTypes["birthdayThisMonth"].Type);
        Assert.Equal(McpParamType.Boolean, McpToolSchemas.ParamTypes["onlyWaiting"].Type);
        // CRM nhận branch/groupId/gender là chuỗi (mã chi nhánh, "M"/"F") — ép sang số là hỏng lọc.
        Assert.Equal(McpParamType.Text, McpToolSchemas.ParamTypes["branch"].Type);
        Assert.Equal(McpParamType.Text, McpToolSchemas.ParamTypes["gender"].Type);
    }

    [Fact]
    public void Schema_khong_cho_tham_so_la()
    {
        foreach (var tool in ChatTools.All)
        {
            var s = McpToolSchemas.InputSchemaFor(tool);
            Assert.Equal("object", s.GetProperty("type").GetString());
            Assert.False(s.GetProperty("additionalProperties").GetBoolean());
            var props = s.GetProperty("properties");
            foreach (var p in tool.Params) Assert.True(props.TryGetProperty(p, out _), $"{tool.Name}.{p}");
        }

        var cf = McpToolSchemas.InputSchemaFor(Tool("cashflow")).GetProperty("properties");
        Assert.Equal("string", cf.GetProperty("startDate").GetProperty("type").GetString());
        Assert.Equal("date", cf.GetProperty("startDate").GetProperty("format").GetString());
        var ct = McpToolSchemas.InputSchemaFor(Tool("customers")).GetProperty("properties");
        Assert.Equal("integer", ct.GetProperty("careFilter").GetProperty("type").GetString());
        Assert.Equal("boolean", ct.GetProperty("birthdayThisMonth").GetProperty("type").GetString());
        Assert.Equal(50, ct.GetProperty("pageSize").GetProperty("maximum").GetInt32());
    }

    [Fact]
    public void Tool_co_marketId_nhan_them_marketName()
    {
        // market_analysis chỉ khai marketId, nhưng AI biết TÊN thị trường chứ không biết id —
        // runner tự đổi marketName → marketId, nên schema phải cho gửi marketName.
        var props = McpToolSchemas.InputSchemaFor(Tool("market_analysis")).GetProperty("properties");
        Assert.True(props.TryGetProperty("marketName", out _));
        var (p, err) = McpToolSchemas.Coerce(Tool("market_analysis"), Args("""{"marketName":"Nội địa miền Nam"}"""));
        Assert.Null(err);
        Assert.Equal("Nội địa miền Nam", p!.Value.GetProperty("marketName").GetString());
    }

    [Fact]
    public void Ngay_sai_dinh_dang_bao_loi_ro()
    {
        var (p, err) = McpToolSchemas.Coerce(Tool("cashflow"), Args("""{"startDate":"15/10/2026","endDate":"2026-10-31"}"""));
        Assert.Null(p);
        Assert.NotNull(err);
        Assert.Contains("startDate", err);
        Assert.Contains("yyyy-MM-dd", err);
        Assert.Contains("2026-10-15", err);   // gợi ý đúng ngày người dùng muốn

        var (_, err2) = McpToolSchemas.Coerce(Tool("cashflow"), Args("""{"startDate":"2026-02-30"}"""));
        Assert.Contains("startDate", err2);
        var (_, err3) = McpToolSchemas.Coerce(Tool("cashflow"), Args("""{"startDate":20261015}"""));
        Assert.Contains("startDate", err3);

        var (ok, none) = McpToolSchemas.Coerce(Tool("cashflow"), Args("""{"startDate":" 2026-10-01 ","endDate":"2026-10-31"}"""));
        Assert.Null(none);
        Assert.Equal("2026-10-01", ok!.Value.GetProperty("startDate").GetString());
    }

    [Fact]
    public void So_dang_chuoi_duoc_ep_kieu()
    {
        var (p, err) = McpToolSchemas.Coerce(Tool("customers"),
            Args("""{"careFilter":"3","birthdayThisMonth":"true","filter":"Lan","pageIndex":2}"""));
        Assert.Null(err);
        Assert.Equal(JsonValueKind.Number, p!.Value.GetProperty("careFilter").ValueKind);
        Assert.Equal(3, p.Value.GetProperty("careFilter").GetInt32());
        Assert.Equal(JsonValueKind.True, p.Value.GetProperty("birthdayThisMonth").ValueKind);
        Assert.Equal("Lan", p.Value.GetProperty("filter").GetString());
        Assert.Equal(2, p.Value.GetProperty("pageIndex").GetInt32());

        var (_, e1) = McpToolSchemas.Coerce(Tool("customers"), Args("""{"careFilter":"ba"}"""));
        Assert.Contains("careFilter", e1);
        var (_, e2) = McpToolSchemas.Coerce(Tool("customers"), Args("""{"careFilter":2.5}"""));
        Assert.Contains("careFilter", e2);
        var (_, e3) = McpToolSchemas.Coerce(Tool("customers"), Args("""{"birthdayThisMonth":"co"}"""));
        Assert.Contains("birthdayThisMonth", e3);
        var (_, e4) = McpToolSchemas.Coerce(Tool("customers"), Args("""{"filter":["a"]}"""));
        Assert.Contains("filter", e4);
    }

    [Fact]
    public void PageSize_qua_50_bi_kep()
    {
        var (p, err) = McpToolSchemas.Coerce(Tool("booking_tickets"), Args("""{"pageSize":500,"pageIndex":0}"""));
        Assert.Null(err);
        Assert.Equal(50, p!.Value.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, p.Value.GetProperty("pageIndex").GetInt32());
        var (p2, _) = McpToolSchemas.Coerce(Tool("booking_tickets"), Args("""{"pageSize":"0"}"""));
        Assert.Equal(1, p2!.Value.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public void Tham_so_la_bi_tu_choi_va_liet_ke_tham_so_dung()
    {
        var (p, err) = McpToolSchemas.Coerce(Tool("top_sellers"), Args("""{"month":"10"}"""));
        Assert.Null(p);
        Assert.Contains("month", err);
        Assert.Contains("startDate", err);
    }

    [Fact]
    public void Khong_tham_so_hoac_null_la_hop_le()
    {
        var (p, err) = McpToolSchemas.Coerce(Tool("departures"), null);
        Assert.Null(err);
        Assert.Equal(JsonValueKind.Object, p!.Value.ValueKind);

        var (p2, err2) = McpToolSchemas.Coerce(Tool("cashflow"), Args("""{"startDate":null,"groupBy":""}"""));
        Assert.Null(err2);
        Assert.False(p2!.Value.TryGetProperty("startDate", out _));
        Assert.False(p2.Value.TryGetProperty("groupBy", out _));

        var (_, err3) = McpToolSchemas.Coerce(Tool("cashflow"), Args("[1,2]"));
        Assert.NotNull(err3);
    }
}
