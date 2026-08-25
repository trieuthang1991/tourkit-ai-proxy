using System.Text.Json;
using TourkitAiProxy.Services.NccImport;
using Xunit;

namespace TourkitAiProxy.Tests;

/// <summary>
/// Cột `providers.dataServices` bên web là MẢNG thành viên liên hệ; web deserialize bằng
/// JsonConvert.DeserializeObject&lt;List&lt;dataServices&gt;&gt;() và KHÔNG bắt lỗi ở ProviderAction
/// (thêm/sửa/xoá thẻ HDV, giấy giới thiệu, dashboard đếm thẻ). Ghi object {contactName, contactPhone}
/// vào đây từng làm web nổ JsonSerializationException.
/// </summary>
public class NccQuoteMapperContactTests
{
    private static string? Map(string quoteJson)
        => NccQuoteMapper.ToCreateProvider(JsonDocument.Parse(quoteJson).RootElement, 10, null).DataServices;

    [Fact]
    public void DataServices_la_mang_dung_key_cua_web()
    {
        var json = Map("""
            { "supplier": { "name": "KS ABC", "contactName": "Trần Minh Quân", "contactPhone": "0236 3888 999" } }
            """);

        Assert.Equal(
            """[{"_name_member":"Trần Minh Quân","_position_member":"","_birthday_member":"","_phone_member":"0236 3888 999","_email_member":""}]""",
            json);
    }

    [Fact]
    public void DataServices_parse_duoc_thanh_mang_va_khong_escape_tieng_viet()
    {
        var json = Map("""
            { "supplier": { "name": "KS ABC", "contactName": "Lê Thị Hoà", "contactPhone": "0905 111 222" } }
            """)!;

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);   // web cần ARRAY, không phải object
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("Lê Thị Hoà", doc.RootElement[0].GetProperty("_name_member").GetString());
        Assert.DoesNotContain("\\u", json);                              // giữ nguyên tiếng Việt như Provider.js
    }

    [Fact]
    public void Chi_co_ten_hoac_chi_co_sdt_van_ra_mang_1_phan_tu()
    {
        var chiTen = Map("""{ "supplier": { "name": "KS ABC", "contactName": "Quân" } }""")!;
        Assert.Contains("\"_name_member\":\"Quân\"", chiTen);
        Assert.Contains("\"_phone_member\":\"\"", chiTen);

        var chiSdt = Map("""{ "supplier": { "name": "KS ABC", "contactPhone": "0236 3888 999" } }""")!;
        Assert.Contains("\"_name_member\":\"\"", chiSdt);
        Assert.Contains("\"_phone_member\":\"0236 3888 999\"", chiSdt);
    }

    [Fact]
    public void Khong_co_lien_he_thi_null_de_khong_ghi_chuoi_rong_vao_DB()
    {
        Assert.Null(Map("""{ "supplier": { "name": "KS ABC" } }"""));
        Assert.Null(Map("""{ "supplier": { "name": "KS ABC", "contactName": "  ", "contactPhone": "" } }"""));
    }
}
