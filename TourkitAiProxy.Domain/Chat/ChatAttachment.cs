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
            // OfType<JsonObject> chứ không phải OfType<JsonNode>: phần tử là chuỗi/số thì đánh
            // chỉ mục ["url"] lên nó sẽ ném, và cả màn hình hội thoại trả 500 vì một dòng rác.
            foreach (var f in daSoi.OfType<JsonObject>())
            {
                var url = Str(f["url"]);
                if (string.IsNullOrWhiteSpace(url)) continue;
                ra.Add(new ChatFile(Name: Str(f["ten"]), Size: Num(f["kich"]), Url: url));
            }
            return ra;
        }

        // Tin GỬI ĐI do mình dựng: một object {ten, kich, url}.
        //
        // ⚠️ Phải kiểm hình dạng, KHÔNG ép kiểu thẳng. Tin chiều ra không phải lúc nào cũng do
        // mình dựng: tin "tiếng vọng" (nhân viên trả lời từ app của kênh, hoặc tin cũ kéo về) đi
        // thẳng từ nhà cung cấp nên mang hình dạng CỦA KÊNH — với Pancake là một MẢNG. Ép
        // .AsObject() lên mảng thì .NET ném, và vì hàm này chạy lúc dựng màn hình hội thoại, lỗi
        // hiện ra thành GET /chat/conversations/{id} → 500: bấm vào hội thoại là trang trắng.
        // Gặp thật 05/11/2026 với tin lịch sử Pancake.
        //
        // Không khớp thì RƠI XUỐNG nhánh theo kênh bên dưới — đó mới là nơi đọc được hình dạng gốc.
        if (chieu == 1 && goc is JsonObject raNgoai)
            return new[] { new ChatFile(Name: Str(raNgoai["ten"]), Size: Num(raNgoai["kich"]),
                                        Url: Str(raNgoai["url"])) };

        return kenh switch
        {
            ChatChannel.Telegram => ReadTelegram(goc, loai),
            // Instagram dùng CHUNG hình dạng đính kèm của Meta: attachments[] = [{type,payload}].
            ChatChannel.Messenger or ChatChannel.Instagram => ReadMeta(goc),
            ChatChannel.Zalo => ReadZalo(goc),
            ChatChannel.WhatsApp => ReadWhatsApp(goc),
            ChatChannel.TikTok => ReadTikTok(goc),
            ChatChannel.Pancake => ReadPancake(goc),
            _ => Array.Empty<ChatFile>(),
        };
    }

    /// <summary>
    /// Pancake: <c>attachments[] = [{type, title, description, link, image:{url}}]</c>.
    ///
    /// <para>Đường dẫn nằm ở <b>hai chỗ</b> tuỳ loại: ảnh thì <c>image.url</c>, còn liên kết/tệp
    /// thì <c>link</c>. Chỉ đọc một chỗ là mất hẳn loại kia — không lỗi, chỉ là nhân viên thấy một
    /// đính kèm không bấm được.</para>
    ///
    /// <para>Pancake <b>không cho kích thước tệp</b> (chỉ có kích thước ảnh tính bằng điểm ảnh),
    /// nên <c>Size</c> để trống. Bịa một con số ở đây là nói dối trên giao diện.</para>
    /// </summary>
    /// <summary>
    /// Những <c>attachments[].type</c> của Pancake <b>KHÔNG phải tệp</b> — đo trên dữ liệu thật
    /// 05/11/2026, 15 loại trong đó quá nửa số lượt là mấy thứ này:
    ///
    /// <list type="bullet">
    /// <item><c>replied_message</c> (119 lượt) — tin được trích dẫn, chở nguyên một tin khác bên trong.</item>
    /// <item><c>reaction</c> (82) — thả cảm xúc lên tin cũ.</item>
    /// <item><c>system_message</c> · <c>zalo_system_message</c> (51) — thông báo của nền tảng.</item>
    /// <item><c>styled_message</c> (9) — <b>chỉ là chữ in đậm/nghiêng</b> của chính tin đó.</item>
    /// <item><c>ad_click</c> · <c>template</c> · <c>pzl_chat_recommended</c> · <c>address</c> ·
    ///       <c>chat.webcontent</c> — sự kiện quảng cáo, nút bấm, nhật ký cuộc gọi, thẻ ngân hàng.</item>
    /// </list>
    ///
    /// <para><b>Không lọc thì hỏng hai đường.</b> Một: tin chữ bình thường có kèm
    /// <c>styled_message</c> bị đoán thành tin TỆP, hộp thư hiện một đính kèm rỗng không bấm được.
    /// Hai: tin chỉ có <c>reaction</c> bị dựng thành một dòng trắng trong hội thoại.</para>
    ///
    /// <para>Dùng danh sách CẤM chứ không dùng danh sách cho phép: loại tệp mới của Pancake (hay
    /// của một nền tảng nó mới gom thêm) vẫn chạy được, chỉ cần nó có đường dẫn http.</para>
    /// </summary>
    private static readonly HashSet<string> PancakeKhongPhaiTep = new(StringComparer.OrdinalIgnoreCase)
    {
        "replied_message", "reaction", "system_message", "zalo_system_message",
        "styled_message", "ad_click", "template", "pzl_chat_recommended",
        "address",
        // chat.webcontent KHÔNG nằm trong danh sách cấm: thẻ ngân hàng Zalo đi qua loại này và
        // mang một mã QR chuyển khoản — thứ khách thật sự cần quét. Xem DuongDanPancake.
    };

    /// <summary>
    /// Lọc ra những phần tử <c>attachments[]</c> THẬT SỰ là tệp. <b>Hàm thuần</b>, dùng chung cho
    /// cả chỗ hiển thị (ở đây) lẫn chỗ đoán loại tin (<c>PancakeChatAdapter</c>) — hai nơi lọc
    /// khác nhau thì tin hiện ra một kiểu còn được phân loại một kiểu.
    /// </summary>
    public static List<JsonObject> LocTepPancake(JsonArray? ds)
    {
        var ra = new List<JsonObject>();
        if (ds is null) return ra;
        foreach (var a in ds)
        {
            if (a is not JsonObject o) continue;
            var loai = o["type"]?.ToString();
            if (loai is not null && PancakeKhongPhaiTep.Contains(loai)) continue;
            if (DuongDanPancake(o) is null) continue;   // không có đường dẫn thì không phải tệp
            ra.Add(o);
        }
        return ra;
    }

    /// <summary>
    /// Đường dẫn tệp trong một phần tử <c>attachments[]</c> của Pancake — <b>nằm ở bốn chỗ khác
    /// nhau tuỳ loại</b>: ảnh ở <c>url</c>/<c>origin_url</c>, sticker ở <c>link</c>, bài chia sẻ ở
    /// <c>media.image.src</c>. Đọc thiếu một chỗ là mất hẳn loại đó mà không có lỗi nào.
    /// </summary>
    private static string? DuongDanPancake(JsonObject o)
    {
        var url = Str(o["url"]) ?? Str(o["origin_url"]) ?? Str(o["link"])
               ?? Str((o["image"] as JsonObject)?["url"])
               ?? Str(((o["media"] as JsonObject)?["image"] as JsonObject)?["src"])
               // Thẻ ngân hàng Zalo (chat.webcontent · zinstant.bankcard): ảnh đáng giữ là mã QR
               // chuyển khoản, không phải hình nền hay logo ngân hàng đi kèm.
               ?? Str((o["bank_card"] as JsonObject)?["qr_code"]);
        // Phải là đường dẫn http thật. Vài loại mang "url" là chuỗi rỗng hoặc "https:" cụt
        // (trường thumb_url của document chẳng hạn) — nhận vào là hộp thư hiện nút bấm chết.
        return url is not null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
               && url.Length > "https://".Length ? url : null;
    }

    private static IReadOnlyList<ChatFile> ReadPancake(JsonNode goc)
    {
        if (goc is not JsonArray ds) return Array.Empty<ChatFile>();

        var ra = new List<ChatFile>();
        foreach (var a in LocTepPancake(ds))
        {
            // LocTepPancake đã chắc chắn a là object VÀ có đường dẫn http — xem ở đó.
            var url = DuongDanPancake(a)!;
            // Tên tệp: document có "name" thật (vd "Phản hồi về Chuyển sever.pdf"), liên kết có
            // "name", bài chia sẻ có "title"/"description". Ảnh không có tên — để trống, giao diện
            // tự hiện ảnh chứ không cần nhãn.
            ra.Add(new ChatFile(
                Name: Str(a["name"]) ?? Str(a["title"]) ?? Str(a["description"])
                      ?? (a["bank_card"] is JsonObject ? "Mã QR chuyển khoản" : null),
                Size: null, Url: url));
        }
        return ra;
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
