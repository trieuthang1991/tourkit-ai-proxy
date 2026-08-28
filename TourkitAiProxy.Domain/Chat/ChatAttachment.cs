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
    string? Name = null,
    long? Size = null,
    string? Url = null,
    string? FileId = null,
    double? Lat = null,
    double? Lon = null)
{
    /// Có thứ gì để tải/hiện không. Sticker Telegram không tên không cỡ vẫn tải được.
    public bool HasFile => !string.IsNullOrWhiteSpace(Url) || !string.IsNullOrWhiteSpace(FileId);
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
    public static IReadOnlyList<ChatFile> Read(ChatChannel kenh, ChatKind loai, string? json, short chieu)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ChatFile>();
        JsonNode? goc;
        try { goc = JsonNode.Parse(json); } catch { return Array.Empty<ChatFile>(); }
        if (goc is null) return Array.Empty<ChatFile>();

        // ĐÃ SOI VỀ KHO RIÊNG. Nhận ra trước mọi thứ khác, vì lúc này hình dạng KHÔNG còn là
        // của kênh nữa — nó đã chuẩn hoá rồi (xem ChatMediaMirror).
        //
        // Dùng khoá "tk" làm dấu nhận: gói tin của mọi kênh đều không có khoá đó, nên không
        // nhầm được. Rẽ theo chieu/kenh thay vì theo dấu này thì tin CŨ (chưa soi) và tin MỚI
        // (đã soi) cùng chiều, cùng kênh, mà hình dạng khác nhau — không phân biệt nổi.
        if (goc is JsonObject g && g["tk"] is not null && g["tep"] is JsonArray daSoi)
        {
            var ra = new List<ChatFile>();
            foreach (var f in daSoi.OfType<JsonNode>())
            {
                var url = Str(f["url"]);
                if (string.IsNullOrWhiteSpace(url)) continue;
                ra.Add(new ChatFile(Name: Str(f["ten"]), Size: Num(f["kich"]), Url: url));
            }
            return ra;
        }

        if (chieu == 1)
        {
            var o = goc.AsObject();
            return new[] { new ChatFile(Name: Str(o["ten"]), Size: Num(o["kich"]), Url: Str(o["url"])) };
        }

        return kenh switch
        {
            ChatChannel.Telegram => ReadTelegram(goc, loai),
            // Instagram dùng CHUNG hình dạng đính kèm của Meta: attachments[] = [{type,payload}].
            ChatChannel.Messenger or ChatChannel.Instagram => ReadMeta(goc),
            ChatChannel.Zalo => ReadZalo(goc),
            ChatChannel.WhatsApp => ReadWhatsApp(goc),
            ChatChannel.TikTok => ReadTikTok(goc),
            _ => Array.Empty<ChatFile>(),
        };
    }

    // Telegram gói mỗi loại một kiểu, và KHÔNG cho đường dẫn — chỉ cho file_id.
    private static IReadOnlyList<ChatFile> ReadTelegram(JsonNode goc, ChatKind loai)
    {
        // Ảnh: mảng nhiều cỡ của CÙNG một ảnh. Lấy cỡ LỚN NHẤT (Telegram xếp nhỏ trước) — lấy
        // nhầm cỡ nhỏ thì nhân viên soi hoá đơn hay ảnh hộ chiếu khách gửi sẽ không đọc nổi chữ.
        if (loai == ChatKind.Image && goc is JsonArray cacCo && cacCo.Count > 0)
        {
            var to = cacCo.OfType<JsonNode>()
                .OrderByDescending(x => Num(x["file_size"]) ?? Num(x["width"]) ?? 0)
                .First();
            return new[] { new ChatFile(FileId: Str(to["file_id"]), Size: Num(to["file_size"])) };
        }

        var o = goc.AsObject();
        if (loai == ChatKind.Location)
            return new[] { new ChatFile(Lat: Dec(o["latitude"]), Lon: Dec(o["longitude"])) };

        var id = Str(o["file_id"]);
        if (string.IsNullOrWhiteSpace(id)) return Array.Empty<ChatFile>();
        return new[] { new ChatFile(Name: Str(o["file_name"]), Size: Num(o["file_size"]), FileId: id) };
    }

    /// <summary>
    /// WhatsApp: <c>{ id, mime_type, filename?, caption? }</c> — <b>chỉ có mã tệp, KHÔNG có URL</b>.
    ///
    /// <para>⚠️ Và khác Telegram ở một điểm nữa: đường tải của WhatsApp <b>đòi khoá xác thực</b> chứ
    /// không chỉ giấu khoá trong đường dẫn. Nên ảnh khách gửi bắt buộc đi qua máy chủ mình, không có
    /// cách nào đưa thẳng cho trình duyệt.</para>
    /// </summary>
    private static IReadOnlyList<ChatFile> ReadWhatsApp(JsonNode goc)
    {
        var o = goc.AsObject();
        // Vị trí gói riêng, không có mã tệp.
        if (o["latitude"] is not null || o["longitude"] is not null)
            return new[] { new ChatFile(Lat: Dec(o["latitude"]), Lon: Dec(o["longitude"])) };

        var id = Str(o["id"]);
        if (string.IsNullOrWhiteSpace(id)) return Array.Empty<ChatFile>();
        return new[] { new ChatFile(Name: Str(o["filename"]), FileId: id) };
    }

    /// <summary>
    /// TikTok: <c>{ media_url }</c> — cho sẵn đường tải, khác hẳn WhatsApp và Telegram.
    /// </summary>
    private static IReadOnlyList<ChatFile> ReadTikTok(JsonNode goc)
    {
        var url = Str(goc.AsObject()["media_url"]);
        return string.IsNullOrWhiteSpace(url)
            ? Array.Empty<ChatFile>() : new[] { new ChatFile(Url: url) };
    }
    /// <summary>
    /// Messenger/Instagram: <c>attachments[] = [{ type, payload: { url } }]</c>, hoặc
    /// <c>payload {coordinates}</c> cho vị trí.
    ///
    /// <para>⚠️ <b>Meta gửi TRÙNG cùng một tệp khi khách gửi nhãn dán.</b> Đo trên dữ liệu thật
    /// (28/08/2026): một cái like gửi về HAI mục, cùng URL, cùng <c>sticker_id</c>, chỉ khác
    /// <c>type</c> — một <c>"image"</c> và một <c>"sticker"</c>. Meta trả kèm bản <c>image</c>
    /// cho các tích hợp đời cũ đọc được.</para>
    ///
    /// <para>Không lọc thì khách gửi một cái like, hộp thư hiện hai — và nhân viên tưởng khách
    /// bấm nhầm hai lần. Lọc theo URL: khách gửi nhiều ảnh thật thì mỗi ảnh một URL khác nhau
    /// nên không bị gộp oan.</para>
    /// </summary>
    private static IReadOnlyList<ChatFile> ReadMeta(JsonNode goc)
    {
        if (goc is not JsonArray ds) return Array.Empty<ChatFile>();
        var ra = new List<ChatFile>();
        var daCo = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in ds.OfType<JsonNode>())
        {
            var p = m["payload"];
            if (p is null) continue;
            var toaDo = p["coordinates"];
            if (toaDo is not null)
            {
                ra.Add(new ChatFile(Lat: Dec(toaDo["lat"]), Lon: Dec(toaDo["long"])));
                continue;
            }
            var url = Str(p["url"]);
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (!daCo.Add(url!)) continue;   // Meta gửi trùng — xem ghi chú ở trên
            ra.Add(new ChatFile(Name: Str(m["title"]), Url: url));
        }
        return ra;
    }

    // Zalo: attachments[] = [{ type, payload: { url, thumbnail, name, size } }].
    private static IReadOnlyList<ChatFile> ReadZalo(JsonNode goc)
    {
        if (goc is not JsonArray ds) return Array.Empty<ChatFile>();
        var ra = new List<ChatFile>();
        foreach (var m in ds.OfType<JsonNode>())
        {
            var p = m["payload"];
            if (p is null) continue;
            if (p["latitude"] is not null || p["lat"] is not null)
            {
                ra.Add(new ChatFile(Lat: Dec(p["latitude"] ?? p["lat"]),
                                    Lon: Dec(p["longitude"] ?? p["long"])));
                continue;
            }
            // Sticker Zalo dùng `thumbnail` chứ không có `url`.
            var url = Str(p["url"]) ?? Str(p["thumbnail"]);
            if (!string.IsNullOrWhiteSpace(url))
                ra.Add(new ChatFile(Name: Str(p["name"]), Size: Num(p["size"]), Url: url));
        }
        return ra;
    }

    private static string? Str(JsonNode? n)
    {
        var s = n?.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // Cỡ tệp có kênh trả số, có kênh trả chuỗi — nhận cả hai thay vì tin vào một kiểu.
    private static long? Num(JsonNode? n)
        => n is null ? null : long.TryParse(n.ToString(), out var v) ? v : null;

    private static double? Dec(JsonNode? n)
        => n is null ? null
           : double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}
