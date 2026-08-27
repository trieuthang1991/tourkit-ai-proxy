// Services/Chat/Inbox/ChatAttachment.cs
using System.Text.Json.Nodes;

namespace TourkitAiProxy.Domain.Chat;

/// <summary>
/// Một tệp khách gửi kèm, đã chuẩn hoá về CÙNG một hình dạng cho cả ba kênh.
///
/// <para>Chuẩn hoá ở máy chủ chứ không ở giao diện: mỗi kênh gói tệp một kiểu khác nhau, để giao
/// diện tự bóc thì cùng một đoạn phân tích phải viết lại bằng JavaScript, không test được, và mỗi
/// lần thêm kênh là sửa hai nơi.</para>
/// </summary>
/// <param name="Ten">Tên tệp nếu kênh cho biết. Ảnh thường không có tên.</param>
/// <param name="Kich">Cỡ tệp theo byte, nếu kênh cho biết.</param>
/// <param name="Url">Đường dẫn tải thẳng. Zalo và Messenger cho sẵn; Telegram thì không.</param>
/// <param name="FileId">Mã tệp Telegram. Phải đổi sang đường dẫn bằng bot token, mà token thì
/// không được lọt ra trình duyệt, nên phải đi qua máy chủ.</param>
public record ChatFile(
    string? Ten = null,
    long? Kich = null,
    string? Url = null,
    string? FileId = null,
    double? Lat = null,
    double? Lon = null)
{
    /// Có thứ gì để tải/hiện không. Sticker Telegram không tên không cỡ vẫn tải được.
    public bool CoTep => !string.IsNullOrWhiteSpace(Url) || !string.IsNullOrWhiteSpace(FileId);
}

/// <summary>Bóc cột <c>attachment</c> (jsonb) thành danh sách <see cref="ChatFile"/>. Hàm THUẦN.</summary>
public static class ChatAttachment
{
    /// <summary>
    /// Bóc theo kênh. JSON hỏng hay rỗng thì trả danh sách rỗng — <b>không ném</b>: một tin nhắn
    /// đính kèm lạ không được phép làm hỏng cả khung chat.
    /// </summary>
    /// <param name="chieu">0=khách gửi (bóc theo định dạng RIÊNG của từng kênh) — 1=mình gửi (đọc
    /// thẳng, vì <c>ChatOutboxWorker</c> tự ghi theo MỘT hình dạng chuẩn <see cref="ChatFile"/>
    /// lúc gửi đi, không phải bóc lại định dạng gốc của kênh).</param>
    public static IReadOnlyList<ChatFile> Doc(ChatChannel kenh, ChatKind loai, string? json, short chieu)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ChatFile>();
        JsonNode? goc;
        try { goc = JsonNode.Parse(json); } catch { return Array.Empty<ChatFile>(); }
        if (goc is null) return Array.Empty<ChatFile>();

        if (chieu == 1)
        {
            var o = goc.AsObject();
            return new[] { new ChatFile(Ten: Chu(o["ten"]), Kich: So(o["kich"]), Url: Chu(o["url"])) };
        }

        return kenh switch
        {
            ChatChannel.Telegram => DocTelegram(goc, loai),
            // Instagram dùng CHUNG hình dạng đính kèm của Meta: attachments[] = [{type,payload}].
            ChatChannel.Messenger or ChatChannel.Instagram => DocMeta(goc),
            ChatChannel.Zalo => DocZalo(goc),
            _ => Array.Empty<ChatFile>(),
        };
    }

    // Telegram gói mỗi loại một kiểu, và KHÔNG cho đường dẫn — chỉ cho file_id.
    private static IReadOnlyList<ChatFile> DocTelegram(JsonNode goc, ChatKind loai)
    {
        // Ảnh: mảng nhiều cỡ của CÙNG một ảnh. Lấy cỡ LỚN NHẤT (Telegram xếp nhỏ trước) — lấy
        // nhầm cỡ nhỏ thì nhân viên soi hoá đơn hay ảnh hộ chiếu khách gửi sẽ không đọc nổi chữ.
        if (loai == ChatKind.Anh && goc is JsonArray cacCo && cacCo.Count > 0)
        {
            var to = cacCo.OfType<JsonNode>()
                .OrderByDescending(x => So(x["file_size"]) ?? So(x["width"]) ?? 0)
                .First();
            return new[] { new ChatFile(FileId: Chu(to["file_id"]), Kich: So(to["file_size"])) };
        }

        var o = goc.AsObject();
        if (loai == ChatKind.ViTri)
            return new[] { new ChatFile(Lat: SoThuc(o["latitude"]), Lon: SoThuc(o["longitude"])) };

        var id = Chu(o["file_id"]);
        if (string.IsNullOrWhiteSpace(id)) return Array.Empty<ChatFile>();
        return new[] { new ChatFile(Ten: Chu(o["file_name"]), Kich: So(o["file_size"]), FileId: id) };
    }

    // Messenger: attachments[] = [{ type, payload: { url } }] hoặc payload {lat,long} cho vị trí.
    private static IReadOnlyList<ChatFile> DocMeta(JsonNode goc)
    {
        if (goc is not JsonArray ds) return Array.Empty<ChatFile>();
        var ra = new List<ChatFile>();
        foreach (var m in ds.OfType<JsonNode>())
        {
            var p = m["payload"];
            if (p is null) continue;
            var toaDo = p["coordinates"];
            if (toaDo is not null)
            {
                ra.Add(new ChatFile(Lat: SoThuc(toaDo["lat"]), Lon: SoThuc(toaDo["long"])));
                continue;
            }
            var url = Chu(p["url"]);
            if (!string.IsNullOrWhiteSpace(url)) ra.Add(new ChatFile(Ten: Chu(m["title"]), Url: url));
        }
        return ra;
    }

    // Zalo: attachments[] = [{ type, payload: { url, thumbnail, name, size } }].
    private static IReadOnlyList<ChatFile> DocZalo(JsonNode goc)
    {
        if (goc is not JsonArray ds) return Array.Empty<ChatFile>();
        var ra = new List<ChatFile>();
        foreach (var m in ds.OfType<JsonNode>())
        {
            var p = m["payload"];
            if (p is null) continue;
            if (p["latitude"] is not null || p["lat"] is not null)
            {
                ra.Add(new ChatFile(Lat: SoThuc(p["latitude"] ?? p["lat"]),
                                    Lon: SoThuc(p["longitude"] ?? p["long"])));
                continue;
            }
            // Sticker Zalo dùng `thumbnail` chứ không có `url`.
            var url = Chu(p["url"]) ?? Chu(p["thumbnail"]);
            if (!string.IsNullOrWhiteSpace(url))
                ra.Add(new ChatFile(Ten: Chu(p["name"]), Kich: So(p["size"]), Url: url));
        }
        return ra;
    }

    private static string? Chu(JsonNode? n)
    {
        var s = n?.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // Cỡ tệp có kênh trả số, có kênh trả chuỗi — nhận cả hai thay vì tin vào một kiểu.
    private static long? So(JsonNode? n)
        => n is null ? null : long.TryParse(n.ToString(), out var v) ? v : null;

    private static double? SoThuc(JsonNode? n)
        => n is null ? null
           : double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}
