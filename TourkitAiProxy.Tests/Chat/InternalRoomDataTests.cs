using System.Text.Json;
using TourkitAiProxy.Domain.Chat;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Tham số tự do trang nhúng gửi kèm phòng theo bản ghi (vd đường dẫn xem lại đơn). Lưu nguyên để trả lại
/// cho mọi thành viên — nên phải chặn ở cửa: chỉ object, có trần cỡ, và <c>url</c> chỉ nhận http(s) đầy đủ
/// (widget biến nó thành nút bấm; "javascript:" lọt qua là chạy mã trên máy người khác).
/// </summary>
public class InternalRoomDataTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Khong_truyen_thi_khong_co_gi()
    {
        Assert.Equal((null, null), InternalRoomData.Normalize(null));
        Assert.Equal((null, null), InternalRoomData.Normalize(J("null")));
    }

    [Fact]
    public void Object_bat_ky_giu_nguyen_noi_dung()
    {
        var (json, err) = InternalRoomData.Normalize(J("""{ "url": "https://crm.vn/don/142", "trangThai": "Đã cọc", "so": 3, "tags": ["a"] }"""));
        Assert.Null(err);
        var back = J(json!);
        Assert.Equal("https://crm.vn/don/142", back.GetProperty("url").GetString());
        Assert.Equal("Đã cọc", back.GetProperty("trangThai").GetString());
        Assert.Equal(3, back.GetProperty("so").GetInt32());
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"chuoi\"")]
    [InlineData("42")]
    public void Khong_phai_object_thi_bao_loi(string raw)
        => Assert.NotNull(InternalRoomData.Normalize(J(raw)).Error);

    [Fact]
    public void Qua_tran_co_thi_bao_loi()
    {
        var big = "{\"x\":\"" + new string('a', InternalRoomData.MaxBytes) + "\"}";
        Assert.NotNull(InternalRoomData.Normalize(J(big)).Error);
    }

    [Fact]
    public void Tran_tinh_theo_byte_chu_co_dau_ton_hon()
    {
        // 1500 chữ "ệ" = 4500 byte UTF-8 dù chỉ 1500 ký tự.
        var vn = "{\"x\":\"" + new string('ệ', 1500) + "\"}";
        Assert.NotNull(InternalRoomData.Normalize(J(vn)).Error);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/don/142")]
    [InlineData("ftp://crm.vn/x")]
    [InlineData("data:text/html,hi")]
    [InlineData("")]
    public void Url_chi_nhan_http_day_du(string url)
        => Assert.NotNull(InternalRoomData.Normalize(J(JsonSerializer.Serialize(new { url }))).Error);

    [Fact]
    public void Url_khong_phai_chuoi_thi_bao_loi()
        => Assert.NotNull(InternalRoomData.Normalize(J("""{ "url": 123 }""")).Error);

    [Theory]
    [InlineData("https://crm.tourkit.vn/booking-ticket/13807")]
    [InlineData("http://localhost:5080/deals?id=1")]
    public void Url_http_hop_le(string url)
        => Assert.Null(InternalRoomData.Normalize(J(JsonSerializer.Serialize(new { url }))).Error);
}
