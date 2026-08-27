// Services/Chat/Channels/MetaTemplateParser.cs
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Đọc và dựng <b>mẫu tin đã duyệt của Meta</b> — dùng chung cho WhatsApp và Messenger.
///
/// <para><b>Vì sao một lớp cho hai kênh.</b> Cả hai đi CÙNG hình dạng mẫu: mảng
/// <c>components[]</c> với các khối <c>HEADER</c>/<c>BODY</c>/<c>BUTTONS</c>, chỗ trống đánh dấu
/// <c>{{1}}</c>, và tham số gửi đi theo khối kèm mảng <c>parameters[]</c>. Chép ra hai bản là hai
/// bản lệch nhau — mà lệch ở đây thì hỏng im lặng: Meta nhận tin, khách nhận được tin có các ô
/// hoán chỗ cho nhau, và không có lỗi nào hiện ra. Cùng lý do <see cref="MetaMessagingParser"/> tồn tại.</para>
///
/// <para>Chỗ hai kênh KHÁC nhau nằm ở từng bộ nối: WhatsApp đọc mẫu trên <b>tài khoản doanh
/// nghiệp</b> còn Messenger đọc trên <b>Trang</b>; WhatsApp gửi <c>type: "template"</c> ở thân
/// ngoài cùng còn Messenger gói trong <c>message.template</c> kèm <c>messaging_type: "UTILITY"</c>.</para>
///
/// <para>Hàm THUẦN, không chạm mạng, có test.</para>
/// </summary>
public static class MetaTemplateParser
{
    /// <summary>Chỉ hai khối này có ô điền dạng chữ. Nút và chân trang thì không.</summary>
    private static readonly string[] KhoiCoODien = { "header", "body" };

    /// <summary>
    /// Đọc <c>components[]</c> ra danh sách chỗ trống + bản xem trước.
    ///
    /// <para>⚠️ Meta đánh chỗ trống <c>{{1}}</c>, <c>{{2}}</c>… <b>đếm lại từ đầu trong TỪNG khối</b>:
    /// tiêu đề có <c>{{1}}</c> riêng, thân tin có <c>{{1}}</c> riêng. Gộp chung một dãy số là điền
    /// lệch ô — và Meta vẫn nhận, nên khách nhận tin sai chứ không phải lỗi hiện ra. Vì thế khoá
    /// mang cả tên khối: <c>body:1</c>, <c>header:1</c>.</para>
    /// </summary>
    public static (List<ChatTemplateSlot> Slots, string? Preview) ReadComponents(JsonArray? khoi)
    {
        var slots = new List<ChatTemplateSlot>();
        string? xemTruoc = null;
        if (khoi is null) return (slots, null);

        foreach (var k in khoi.OfType<JsonNode>())
        {
            var loai = (k["type"]?.ToString() ?? "").ToLowerInvariant();
            if (!KhoiCoODien.Contains(loai)) continue;

            var chu = k["text"]?.ToString();
            if (string.IsNullOrWhiteSpace(chu)) continue;
            if (loai == "body") xemTruoc = chu;

            // Ví dụ Meta kèm theo có HAI hình dạng: body_text là mảng-trong-mảng
            // [["Anh Minh","2/9"]], còn header_text là mảng phẳng ["Anh Minh"]. Chỉ xử một hình
            // dạng thì nửa số mẫu mất phần gợi ý — mà nhân viên nhìn ô "Ô 1" trống thì không đoán
            // nổi phải điền tên khách hay mã tour.
            var vd = k["example"]?[loai == "header" ? "header_text" : "body_text"] as JsonArray;
            var vdList = vd?.FirstOrDefault() as JsonArray ?? vd;

            foreach (Match m in Regex.Matches(chu!, @"\{\{(\d+)\}\}"))
            {
                var so = m.Groups[1].Value;
                var khoa = $"{loai}:{so}";
                if (slots.Any(x => x.Key == khoa)) continue;

                var mauVd = int.TryParse(so, out var i) && vdList is not null && i >= 1 && i - 1 < vdList.Count
                    ? vdList[i - 1]?.ToString()
                    : null;
                slots.Add(new(khoa, $"Ô {so}" + (loai == "header" ? " (tiêu đề)" : ""), mauVd));
            }
        }

        return (slots, xemTruoc);
    }

    /// <summary>
    /// Dựng <c>components[]</c> để GỬI.
    ///
    /// <para>⚠️ Meta <b>không đọc tên khoá</b> — nó ghép tham số theo VỊ TRÍ trong mảng. Sắp sai
    /// thứ tự là tin gửi đi với các ô hoán chỗ cho nhau, mà lượt gọi vẫn thành công.</para>
    ///
    /// <para>Ô thiếu giá trị đi thành chuỗi rỗng thay vì bị bỏ: bỏ một phần tử là mọi ô phía sau
    /// tụt lên một chỗ, hỏng nặng hơn hẳn một ô trống.</para>
    /// </summary>
    public static JsonArray BuildComponents(ChatTemplate mau, IReadOnlyDictionary<string, string> giaTri)
    {
        var ra = new JsonArray();

        // Giữ đúng thứ tự khối như mẫu khai (header trước body) — Meta không đòi, nhưng đọc log
        // lúc truy vết mà thứ tự nhảy lung tung thì mất thời gian vô ích.
        foreach (var nhom in mau.Slots.GroupBy(x => TenKhoi(x.Key))
                                      .OrderBy(g => Array.IndexOf(KhoiCoODien, g.Key)))
        {
            var ds = new JsonArray();
            foreach (var slot in nhom.OrderBy(x => SoThuTu(x.Key)))
                ds.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = giaTri.GetValueOrDefault(slot.Key, ""),
                });

            if (ds.Count > 0)
                ra.Add(new JsonObject { ["type"] = nhom.Key, ["parameters"] = ds });
        }

        return ra;
    }

    private static string TenKhoi(string khoa)
    {
        var i = khoa.IndexOf(':');
        return i < 0 ? khoa : khoa[..i];
    }

    private static int SoThuTu(string khoa)
    {
        var i = khoa.IndexOf(':');
        return i >= 0 && int.TryParse(khoa[(i + 1)..], out var n) ? n : 0;
    }
}
