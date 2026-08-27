using System.Text.Json.Nodes;
using TourkitAiProxy.Services.Chat.Channels;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Mẫu tin đã duyệt — đường DUY NHẤT nhắn cho khách khi cửa sổ trả lời tự do đã đóng.
///
/// <para>Chỗ sai ở đây <b>hỏng im lặng</b>: nền tảng vẫn nhận tin, vẫn trả về thành công, mà khách
/// nhận được tin có các ô hoán chỗ cho nhau. Không lỗi nào hiện ra, và không ai phát hiện cho tới
/// khi khách gọi lại hỏi "sao báo tôi đi ngày 2 mà vé ghi ngày 9".</para>
/// </summary>
public class ChatTemplateTests
{
    private static JsonArray Khoi(string json) => JsonNode.Parse(json)!.AsArray();

    [Fact]
    public void Cho_trong_dem_lai_tu_dau_trong_TUNG_khoi()
    {
        // Meta đánh {{1}} riêng cho tiêu đề và riêng cho thân tin. Gộp chung một dãy số là điền
        // lệch ô — mà lượt gọi vẫn thành công nên không ai biết.
        var (slots, xem) = MetaTemplateParser.ReadComponents(Khoi("""
        [
          {"type":"HEADER","format":"TEXT","text":"Xác nhận tour {{1}}"},
          {"type":"BODY","text":"Chào {{1}}, tour khởi hành ngày {{2}}. Mã đặt chỗ: {{3}}."}
        ]
        """));

        Assert.Equal(4, slots.Count);
        Assert.Equal(new[] { "header:1", "body:1", "body:2", "body:3" }, slots.Select(x => x.Key));

        // Bản xem trước lấy từ THÂN tin, không phải tiêu đề — thân mới là câu khách đọc.
        Assert.StartsWith("Chào {{1}}", xem);
    }

    [Fact]
    public void Vi_du_cua_Meta_co_HAI_hinh_dang_khac_nhau()
    {
        // body_text là mảng-trong-mảng, header_text là mảng phẳng. Chỉ xử một hình dạng thì nửa số
        // mẫu mất phần gợi ý — nhân viên nhìn ô "Ô 1" trống không đoán nổi phải điền gì.
        var (slots, _) = MetaTemplateParser.ReadComponents(Khoi("""
        [
          {"type":"HEADER","format":"TEXT","text":"Tour {{1}}",
           "example":{"header_text":["Nhật Bản 5N4Đ"]}},
          {"type":"BODY","text":"Chào {{1}}, ngày đi {{2}}.",
           "example":{"body_text":[["Anh Minh","02/09/2026"]]}}
        ]
        """));

        Assert.Equal("Nhật Bản 5N4Đ", slots.Single(x => x.Key == "header:1").Sample);
        Assert.Equal("Anh Minh", slots.Single(x => x.Key == "body:1").Sample);
        Assert.Equal("02/09/2026", slots.Single(x => x.Key == "body:2").Sample);
    }

    [Fact]
    public void Nut_va_chan_trang_KHONG_sinh_o_dien()
    {
        // Nút có {{1}} riêng của nó (tham số URL) nhưng đi theo cơ chế khác hẳn. Gom vào cùng danh
        // sách ô chữ là dựng ra một ô nhân viên điền vào mà chẳng tới đâu.
        var (slots, _) = MetaTemplateParser.ReadComponents(Khoi("""
        [
          {"type":"BODY","text":"Đơn {{1}} đã xác nhận."},
          {"type":"FOOTER","text":"TourKit"},
          {"type":"BUTTONS","buttons":[{"type":"URL","text":"Xem đơn","url":"https://a.b/{{1}}"}]}
        ]
        """));

        Assert.Equal(new[] { "body:1" }, slots.Select(x => x.Key));
    }

    [Fact]
    public void Tham_so_gui_di_dung_THU_TU_so_chu_khong_theo_thu_tu_dien()
    {
        // Meta KHÔNG đọc tên khoá — nó ghép theo vị trí trong mảng. Đây là chỗ sai chết người:
        // sắp nhầm thứ tự là tin gửi đi với các ô hoán chỗ, mà API vẫn trả về thành công.
        var mau = new ChatTemplate("id", "xac_nhan_tour", "vi", "UTILITY", "APPROVED", new[]
        {
            new ChatTemplateSlot("body:2", "Ô 2", null),
            new ChatTemplateSlot("header:1", "Ô 1 (tiêu đề)", null),
            new ChatTemplateSlot("body:1", "Ô 1", null),
        }, null);

        var ra = MetaTemplateParser.BuildComponents(mau, new Dictionary<string, string>
        {
            ["header:1"] = "Nhật Bản",
            ["body:1"] = "Anh Minh",
            ["body:2"] = "02/09/2026",
        });

        // Tiêu đề đứng trước thân, đúng như mẫu khai.
        Assert.Equal(2, ra.Count);
        Assert.Equal("header", ra[0]!["type"]!.ToString());
        Assert.Equal("body", ra[1]!["type"]!.ToString());

        var than = ra[1]!["parameters"]!.AsArray();
        Assert.Equal("Anh Minh", than[0]!["text"]!.ToString());
        Assert.Equal("02/09/2026", than[1]!["text"]!.ToString());
    }

    [Fact]
    public void O_thieu_gia_tri_di_thanh_RONG_chu_khong_bi_bo()
    {
        // Bỏ hẳn một phần tử là mọi ô phía sau tụt lên một chỗ — hỏng nặng hơn hẳn một ô trống.
        var mau = new ChatTemplate("id", "m", "vi", null, "APPROVED", new[]
        {
            new ChatTemplateSlot("body:1", "Ô 1", null),
            new ChatTemplateSlot("body:2", "Ô 2", null),
            new ChatTemplateSlot("body:3", "Ô 3", null),
        }, null);

        var than = MetaTemplateParser
            .BuildComponents(mau, new Dictionary<string, string> { ["body:1"] = "A", ["body:3"] = "C" })[0]!
            ["parameters"]!.AsArray();

        Assert.Equal(3, than.Count);
        Assert.Equal("", than[1]!["text"]!.ToString());
        Assert.Equal("C", than[2]!["text"]!.ToString());
    }

    [Fact]
    public void Mau_chua_duyet_thi_KHONG_san_sang_gui()
    {
        // Vẫn trả về để giao diện nói rõ "đang chờ duyệt" — giấu đi thì người dùng tưởng mẫu bị mất
        // và đăng ký lại một mẫu trùng.
        Assert.False(new ChatTemplate("i", "n", "vi", null, "PENDING", [], null).SendReady);
        Assert.False(new ChatTemplate("i", "n", "vi", null, "REJECTED", [], null).SendReady);
        Assert.True(new ChatTemplate("i", "n", "vi", null, "APPROVED", [], null).SendReady);
        Assert.True(new ChatTemplate("i", "n", "vi", null, "approved", [], null).SendReady);
    }

    [Fact]
    public void Zalo_chuan_hoa_so_dien_thoai_ve_dang_84()
    {
        // ZNS đòi dạng 84…, còn CRM lưu 0… và đôi khi có dấu cách hoặc dấu chấm. Gửi nguyên si là
        // Zalo từ chối với câu lỗi không hề nhắc tới định dạng.
        Assert.Equal("84912345678", ZaloChatAdapter.ChuanHoaSo("0912345678"));
        Assert.Equal("84912345678", ZaloChatAdapter.ChuanHoaSo("0912 345 678"));
        Assert.Equal("84912345678", ZaloChatAdapter.ChuanHoaSo("0912.345.678"));
        Assert.Equal("84912345678", ZaloChatAdapter.ChuanHoaSo("84912345678"));
        Assert.Equal("84912345678", ZaloChatAdapter.ChuanHoaSo("+84 912 345 678"));
    }
}
