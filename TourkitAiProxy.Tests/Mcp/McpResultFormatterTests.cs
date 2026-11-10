using System.Text.Json;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Domain.Mcp;
using Xunit;

namespace TourkitAiProxy.Tests.Mcp;

/// <summary>
/// Kết quả tool MCP ở dạng văn bản: đây là thứ mô hình ĐỌC, nên phải gọn (≤ 20 dòng, ≤ 30.000 ký tự)
/// và phải nói rõ còn bao nhiêu dòng bị cắt — không thì mô hình tưởng 20 dòng là tất cả.
/// </summary>
public class McpResultFormatterTests
{
    private static ChatTool Tool(string name) => ChatTools.Find(name)!;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Envelope(int items, int total)
    {
        var rows = Enumerable.Range(1, items).Select(i => new { id = i, fullName = $"Khách {i}", revenue = i * 1000 });
        return JsonSerializer.SerializeToElement(new
        {
            section = "customers", title = "Khách hàng", count = items, total,
            period = new { startDate = "2026-11-01", endDate = "2026-11-30" },
            summary = new { revenue = 123456789 },
            items = rows,
        });
    }

    [Fact]
    public void Cat_toi_da_20_dong_va_bao_con_bao_nhieu()
    {
        var text = McpResultFormatter.ToText(Tool("customers"), Envelope(25, 25));
        Assert.Contains("Khách hàng", text);
        Assert.Contains("2026-11-01", text);
        Assert.Contains("123456789", text);
        Assert.Contains("| Khách 20 |", text);
        Assert.DoesNotContain("Khách 21", text);
        Assert.Contains("còn 5 dòng", text);

        // Upstream phân trang: trang chỉ có 20 dòng nhưng tổng 340 → báo còn 320.
        var paged = McpResultFormatter.ToText(Tool("customers"), Envelope(20, 340));
        Assert.Contains("còn 320 dòng", paged);
        Assert.Contains("pageSize=20", paged);   // chỉ hiện 20 dòng → gợi ý phân trang theo 20
    }

    [Fact]
    public void Ky_co_nhan_in_nhan_va_ngay_ISO()
    {
        var env = Json("""{"title":"T","period":{"startDate":"2026-11-01T00:00:00","endDate":"2026-11-30T00:00:00","label":"01/11/2026 - 30/11/2026"},"items":[]}""");
        var text = McpResultFormatter.ToText(Tool("cashflow"), env);
        Assert.Contains("Kỳ: 01/11/2026 - 30/11/2026 (2026-11-01 → 2026-11-30)", text);
        Assert.DoesNotContain("T00:00:00", text);
    }

    [Fact]
    public void Khong_qua_30000_ky_tu()
    {
        var big = "x".PadRight(5000, 'x');
        var env = JsonSerializer.SerializeToElement(new
        {
            title = "Khách hàng", total = 20,
            items = Enumerable.Range(1, 20).Select(i => new { id = i, a = big, b = big, c = big }),
        });
        var text = McpResultFormatter.ToText(Tool("customers"), env);
        Assert.True(text.Length <= McpResultFormatter.MaxChars, $"len={text.Length}");
    }

    [Fact]
    public void Envelope_rong_tra_cau_khong_co_du_lieu()
    {
        var env = Json("""{"section":"customers","title":"Khách hàng","count":0,"total":0,"items":[]}""");
        var text = McpResultFormatter.ToText(Tool("customers"), env);
        Assert.Contains("Không có dữ liệu", text);
        Assert.DoesNotContain("|", text);
    }

    [Fact]
    public void Envelope_hong_khong_nem()
    {
        foreach (var bad in new[] { Json("\"lỗi\""), Json("42"), Json("null"), default })
        {
            var text = McpResultFormatter.ToText(Tool("cashflow"), bad);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.Contains("không đọc được", text);
        }
    }

    [Fact]
    public void Mang_tron_nhu_list_markets_duoc_coi_la_items()
    {
        var arr = Json("""[{"id":1,"name":"Nhật Bản"},{"id":2,"name":"Nội địa - Miền Nam"}]""");
        var text = McpResultFormatter.ToText(Tool("list_markets"), arr);
        Assert.Contains("Nhật Bản", text);
        var s = McpResultFormatter.ToStructured(arr);
        Assert.Equal(JsonValueKind.Object, s.ValueKind);
        Assert.Equal(2, s.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void Structured_giu_truong_khac_va_cat_items_con_20()
    {
        var s = McpResultFormatter.ToStructured(Envelope(25, 25));
        Assert.Equal(20, s.GetProperty("items").GetArrayLength());
        Assert.Equal(25, s.GetProperty("total").GetInt32());
        Assert.Equal("Khách hàng", s.GetProperty("title").GetString());
        Assert.Equal(123456789, s.GetProperty("summary").GetProperty("revenue").GetInt64());
    }

    [Fact]
    public void O_bang_khong_pha_markdown()
    {
        var env = Json("""{"title":"T","items":[{"name":"A | B\nC","tags":["x","y"],"n":null}]}""");
        var text = McpResultFormatter.ToText(Tool("customers"), env);
        Assert.Contains("A \\| B C", text);
        Assert.Contains("[\"x\",\"y\"]", text);
    }

    [Fact]
    public void Dung_nhan_cot_tieng_Viet_khi_envelope_co_columns()
    {
        var env = Json("""{"title":"T","columns":{"fullName":"Họ tên"},"items":[{"fullName":"Lan","x":1}]}""");
        var text = McpResultFormatter.ToText(Tool("customers"), env);
        Assert.Contains("Họ tên", text);
    }
}
