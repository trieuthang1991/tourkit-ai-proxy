// Services/Chat/Channels/MetaMessagingParser.cs
using System.Text.Json.Nodes;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Services.Chat.Channels;

/// <summary>
/// Bóc gói webhook của <b>nền tảng nhắn tin Meta</b> — dùng chung cho Messenger và Instagram.
///
/// <para><b>Vì sao một lớp cho hai kênh.</b> Hai kênh này đi CÙNG một hợp đồng: cùng hình dạng
/// <c>entry[] × messaging[]</c>, cùng <c>mid</c>, cùng <c>is_echo</c>, cùng cách gói đính kèm,
/// cùng kiểu ký <c>X-Hub-Signature-256</c>. Chép ra hai bản là hai bản lệch nhau — mà lệch ở đây
/// thì hỏng im lặng: một kênh nhận được cảm xúc, kênh kia thì không, và không có lỗi nào hiện ra.
/// Cùng lý do R2 và S3 dùng chung một lớp lưu trữ.</para>
///
/// <para><b>Chỗ hai kênh KHÁC nhau thì KHÔNG nằm ở đây</b> — nằm ở từng bộ nối: đường gửi
/// (<c>graph.facebook.com</c> với <c>?access_token=</c> · <c>graph.instagram.com</c> với header
/// <c>Bearer</c>), khoá kiểm chữ ký, và cách báo lại trạng thái tin đã gửi.</para>
///
/// <para>Hàm THUẦN, không chạm mạng, có test trên gói tin thật.</para>
/// </summary>
public static class MetaMessagingParser
{
    /// <param name="kenh">Kênh gắn cho mọi sự kiện bóc ra — gói tin không tự nói nó là kênh nào
    /// (trường <c>object</c> có nói, nhưng đường webhook đã biết trước rồi).</param>
    public static IReadOnlyList<InboundChatEvent> Read(string rawBody, ChatChannel kenh)
    {
        var ra = new List<InboundChatEvent>();
        JsonNode? goc;
        try { goc = JsonNode.Parse(rawBody); } catch { return ra; }

        // Gói LỊCH SỬ do MetaHistoryImporter tự xếp vào hàng đợi — không phải webhook của Meta.
        // Nhận ra trước rồi đi đường riêng: hình dạng của Graph khác hẳn hình dạng webhook.
        if (goc?["tourkit_lich_su"] is { } manh) return DocLichSu(manh, kenh);

        // Meta gói nhiều sự kiện trong một lần gọi: entry[] × messaging[]. Bóc thiếu vòng lặp là
        // mất tin khi khách nhắn dồn.
        if (goc?["entry"] is not JsonArray entries) return ra;
        foreach (var e in entries)
        {
            // BÌNH LUẬN dưới bài viết đi ở "changes", không phải "messaging". Bỏ sót nhánh này là
            // toàn bộ bình luận rơi im lặng — webhook vẫn trả 200 nên không lỗi nào hiện ra.
            if (e?["changes"] is JsonArray thayDoi)
                DocBinhLuan(thayDoi, e["id"]?.ToString(), e["time"]?.ToString(), kenh, ra);

            if (e?["messaging"] is not JsonArray ms) continue;
            foreach (var m in ms)
            {
                if (m is null) continue;

                // Meta báo trạng thái tin MÌNH đã gửi bằng hai gói riêng, không nằm trong "message":
                //   delivery: {"mids":[…], "watermark": <ms>}  — đã tới máy khách
                //   read:     {"watermark": <ms>}              — khách đã đọc
                // Dùng watermark chứ không dùng mids: "read" không có mids, đi chung một đường thì
                // ít code hơn và hai loại không lệch hành vi.
                //
                // ⚠️ Người gửi ở hai gói này là KHÁCH (ngược với tin echo). Lấy nhầm recipient là
                // đánh dấu vào hội thoại của chính Trang mình — tức là không hội thoại nào cả.
                var tt = m["delivery"] is not null ? ChatState.Delivered
                       : m["read"] is not null ? ChatState.Seen
                       : (ChatState?)null;
                if (tt is { } trangThai)
                {
                    var uidM = m["sender"]?["id"]?.ToString();
                    var goiTt = m[trangThai == ChatState.Delivered ? "delivery" : "read"];
                    var wm = goiTt?["watermark"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uidM) && long.TryParse(wm, out var wms))
                    {
                        var mocLuc = DateTimeOffset.FromUnixTimeMilliseconds(wms).UtcDateTime;
                        ra.Add(new(kenh, uidM!, null, ChatKind.Text, null, null,
                            mocLuc, Watermark: new(trangThai, mocLuc)));
                        continue;
                    }

                    // ⚠️ Instagram báo "đã xem" KHÁC Messenger: {"read":{"mid":"<tin cuối đã đọc>"}},
                    // KHÔNG có watermark. Đọc theo lối Messenger thì giá trị ra null và sự kiện rơi
                    // im lặng — dấu tích Instagram đứng mãi ở "đã gửi" mà không lỗi nào hiện ra.
                    //
                    // Mốc thời gian phải tra ngược từ chính tin đó (chạm CSDL, không làm được ở hàm
                    // thuần này) nên chỉ chuyển mã tin sang cho lõi. Lấy tạm `timestamp` của gói cho
                    // nhanh là đánh dấu THỪA: mọi tin gửi trước lúc đó bị coi là đã xem, kể cả tin
                    // khách chưa hề mở — tức nói dối nhân viên.
                    var maTinDoc = goiTt?["mid"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uidM) && !string.IsNullOrWhiteSpace(maTinDoc))
                    {
                        var lucBao = long.TryParse(m["timestamp"]?.ToString(), out var tsBao)
                            ? DateTimeOffset.FromUnixTimeMilliseconds(tsBao).UtcDateTime : DateTime.UtcNow;
                        ra.Add(new(kenh, uidM!, null, ChatKind.Text, null, null, lucBao,
                            Watermark: new(trangThai, default, maTinDoc)));
                    }
                    continue;
                }

                // Cảm xúc: KHÔNG phải tin mới, mà gắn vào một tin đã có.
                //
                //   {"sender":{"id":<khách>},"recipient":{"id":<Trang>},
                //    "reaction":{"mid":<tin bị thả>,"action":"react"|"unreact",
                //                "emoji":"❤","reaction":"love"}}
                //
                // ⚠️ "unreact" là GỠ cảm xúc, và lúc đó Meta KHÔNG gửi kèm emoji. Xử lý chung một
                // nhánh với "react" mà không đọc action là cảm xúc đã gỡ vẫn hiện mãi.
                if (m["reaction"] is { } cx)
                {
                    var uidCx = m["sender"]?["id"]?.ToString();
                    var midCx = cx["mid"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uidCx) && !string.IsNullOrWhiteSpace(midCx))
                    {
                        var lucCx = long.TryParse(m["timestamp"]?.ToString(), out var tsCx)
                            ? DateTimeOffset.FromUnixTimeMilliseconds(tsCx).UtcDateTime : DateTime.UtcNow;
                        ra.Add(new(kenh, uidCx!, null, ChatKind.Text, null, null, lucCx,
                            Reaction: new(midCx!, cx["emoji"]?.ToString(), cx["reaction"]?.ToString(),
                                cx["action"]?.ToString() == "unreact")));
                    }
                    continue;
                }

                // Nguồn khách đến. Meta gắn nó vào BA chỗ khác nhau tuỳ đường khách vào:
                //   messaging_referrals -> m.referral        (khách đã từng nhắn, quay lại qua QR/liên kết)
                //   messaging_postbacks -> m.postback.referral (lần ĐẦU bấm "Bắt đầu" từ quảng cáo)
                //   messaging_optins    -> m.optin.ref
                // Chỉ đọc một chỗ là mất phần lớn ca — mà mất là mất vĩnh viễn, không tra lại được.
                //
                // ⚠️ Chú thích ba dòng trên có từ đầu, nhưng mã CHỈ đọc hai chỗ đầu tới 28/08/2026 —
                // nhánh optin nằm trong danh sách đăng ký bên Meta suốt thời gian đó mà không ai bóc.
                // Đúng kiểu hỏng không có lỗi: gói tin tới, bị bỏ qua, và "khách đến từ đâu" mất luôn.
                var nguon = m["referral"] ?? m["postback"]?["referral"];
                ChatReferral? tuDau =
                    nguon is not null
                        ? new(nguon["source"]?.ToString(), nguon["ref"]?.ToString(),
                              nguon["ad_id"]?.ToString())
                    // Gói optin CHỈ có `ref`, không có source lẫn ad_id (khách bấm nút "Gửi tới
                    // Messenger" trên web của công ty). Tự đặt nhãn nguồn để báo cáo phân biệt được
                    // đường này với quảng cáo — để trống thì mọi optin dồn vào nhóm "không rõ".
                    : m["optin"]?["ref"]?.ToString() is { Length: > 0 } refOptin
                        ? new("OPTIN", refOptin, null)
                        : null;

                // Khách bấm NÚT. Ghi lại bằng CHỮ TRÊN NÚT (title) chứ không phải payload kỹ
                // thuật: nhân viên đọc lại hội thoại phải thấy đúng thứ khách nhìn thấy, không
                // phải một chuỗi mã như "MENU_TOUR_DA_NANG".
                if (m["postback"] is { } pb)
                {
                    var uidPb = m["sender"]?["id"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uidPb))
                    {
                        var lucPb = long.TryParse(m["timestamp"]?.ToString(), out var tsPb)
                            ? DateTimeOffset.FromUnixTimeMilliseconds(tsPb).UtcDateTime : DateTime.UtcNow;
                        ra.Add(new(kenh, uidPb!, pb["mid"]?.ToString(), ChatKind.Text,
                            pb["title"]?.ToString() ?? pb["payload"]?.ToString(), null, lucPb,
                            Referral: tuDau));
                    }
                    continue;
                }

                // Gói CHỈ CÓ nguồn, không kèm tin (khách mở cuộc trò chuyện từ quảng cáo nhưng
                // chưa gõ gì). Vẫn phải ghi nhận — đây chính là lúc duy nhất Meta nói nguồn.
                if (m["message"] is null && tuDau is not null)
                {
                    var uidRf = m["sender"]?["id"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uidRf))
                        ra.Add(new(kenh, uidRf!, null, ChatKind.Text, null, null,
                            DateTime.UtcNow, Referral: tuDau));
                    continue;
                }

                var msg = m["message"];
                if (msg is null) continue;   // opt-in… — chưa dùng

                // is_echo = tin do CHÍNH trang gửi. Nhân viên trả lời từ Trang hoặc từ ứng dụng
                // Meta Business thì mình chỉ biết qua đây — bỏ là hộp thư thiếu nửa cuộc trò chuyện.
                var vong = msg["is_echo"]?.GetValue<bool>() ?? false;
                var uid = vong ? m["recipient"]?["id"]?.ToString() : m["sender"]?["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(uid)) continue;

                var loai = ChatKind.Text;
                string? att = null;
                if (msg["attachments"] is JsonArray a && a.Count > 0)
                {
                    att = a.ToJsonString();

                    // ⚠️ NHÃN DÁN phải nhận ra trước, và phải quét CẢ MẢNG chứ không chỉ mục đầu.
                    //
                    // Đo trên dữ liệu thật (28/08/2026): khách gửi một cái like thì Meta trả HAI
                    // mục cùng URL — mục đầu type="image", mục sau type="sticker". Chỉ nhìn mục
                    // đầu thì mọi nhãn dán đều bị ghi là ẢNH, rồi giao diện vẽ nó to bằng tấm ảnh
                    // khách chụp hộ chiếu.
                    var laNhanDan = a.OfType<JsonNode>().Any(x =>
                        x["type"]?.ToString() == "sticker" || x["payload"]?["sticker_id"] is not null);

                    loai = laNhanDan ? ChatKind.Sticker : a[0]?["type"]?.ToString() switch
                    {
                        "image" => ChatKind.Image,
                        "audio" => ChatKind.Audio,
                        "video" or "file" => ChatKind.File,
                        "location" => ChatKind.Location,
                        _ => ChatKind.File,
                    };
                }

                var luc = long.TryParse(m["timestamp"]?.ToString(), out var ts)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime : DateTime.UtcNow;

                ra.Add(new(kenh, uid!, msg["mid"]?.ToString(), loai,
                    msg["text"]?.ToString(), att, luc, IsEcho: vong, Referral: tuDau));
            }
        }
        return ra;
    }

    /// <summary>
    /// Bóc một mảnh hội thoại CŨ lấy về từ Graph.
    ///
    /// <para><b>Hình dạng khác hẳn webhook</b> nên không dùng lại đường trên được: Graph trả
    /// <c>{id, message, from:{id,name}, created_time, attachments:{data:[…]}}</c>, còn webhook
    /// trả <c>messaging[].message.{mid,text,attachments[]}</c>. Ép chung một hàm là hai hình
    /// dạng cùng phải chiều theo nhau rồi cả hai cùng khó đọc.</para>
    ///
    /// <para>Graph trả tin <b>mới nhất trước</b>. Không đảo lại thì hộp thư vẫn hiện đúng thứ tự
    /// (sắp theo thời điểm khi đọc ra), nên ở đây không đảo — thứ tự trong danh sách này không
    /// mang ý nghĩa gì.</para>
    /// </summary>
    private static IReadOnlyList<InboundChatEvent> DocLichSu(JsonNode manh, ChatChannel kenh)
    {
        var ra = new List<InboundChatEvent>();
        var maKhach = manh["khach"]?.ToString();
        if (string.IsNullOrWhiteSpace(maKhach)) return ra;

        var ten = manh["ten"]?.ToString();
        if (manh["tin"] is not JsonArray ds) return ra;

        foreach (var m in ds.OfType<JsonNode>())
        {
            var maTin = m["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(maTin)) continue;

            // Chiều tin đọc từ from.id: khác khách nghĩa là Trang mình đã gửi. So với "cuaToi"
            // thì hỏng ở Instagram — id trong from KHÔNG phải chuỗi "me" mình gửi lên.
            var laCuaMinh = m["from"]?["id"]?.ToString() != maKhach;
            var (loai, att) = DocDinhKemLichSu(m["attachments"]?["data"] as JsonArray);
            var chu = m["message"]?.ToString();

            // Tin chỉ có đính kèm thì message rỗng — bỏ hẳn là mất tin. Giữ lại, dòng chữ để trống,
            // giao diện tự hiện thẻ tệp.
            if (string.IsNullOrWhiteSpace(chu) && att is null) continue;

            ra.Add(new(kenh, maKhach!, maTin, loai, chu, att, LucGuiLichSu(m),
                IsEcho: laCuaMinh,
                DisplayName: laCuaMinh ? ten : (m["from"]?["name"]?.ToString() ?? ten),
                IsHistory: true));
        }

        return ra;
    }

    /// <summary>
    /// Đính kèm ở Graph nằm ở BA chỗ khác nhau tuỳ loại tệp: ảnh ở <c>image_data.url</c>, video ở
    /// <c>video_data.url</c>, còn tệp và âm thanh ở <c>file_url</c> ngoài cùng. Chỉ đọc một chỗ là
    /// mất hẳn hai loại kia mà không có lỗi nào.
    /// </summary>
    private static (ChatKind Loai, string? Att) DocDinhKemLichSu(JsonArray? ds)
    {
        var d = ds?.OfType<JsonNode>().FirstOrDefault();
        if (d is null) return (ChatKind.Text, null);

        var mime = d["mime_type"]?.ToString() ?? "";
        var url = d["image_data"]?["url"]?.ToString()
               ?? d["video_data"]?["url"]?.ToString()
               ?? d["file_url"]?.ToString();
        if (string.IsNullOrWhiteSpace(url)) return (ChatKind.Text, null);

        var loai = mime.StartsWith("image/", StringComparison.Ordinal) || d["image_data"] is not null
            ? ChatKind.Image
            : mime.StartsWith("audio/", StringComparison.Ordinal) ? ChatKind.Audio : ChatKind.File;

        return (loai, new JsonObject
        {
            ["ten"] = d["name"]?.ToString(),
            ["kich"] = d["size"]?.ToString(),
            ["url"] = url,
        }.ToJsonString());
    }

    /// <summary>
    /// Bóc bình luận dưới bài viết. Hai kênh, <b>hai hình dạng khác hẳn nhau</b> — đây là chỗ duy
    /// nhất trong lớp này mà Messenger và Instagram KHÔNG dùng chung hợp đồng:
    ///
    /// <list type="bullet">
    ///   <item><b>Facebook</b> — <c>field: "feed"</c>, và <c>feed</c> chở đủ thứ: đăng bài, thả
    ///     tim, chia sẻ, ảnh mới. Phải lọc <c>item == "comment"</c>, không thì mỗi lượt thả tim
    ///     cũng thành một dòng trong hộp thư. Có <c>verb</c> nên biết được thêm/sửa/xoá.</item>
    ///   <item><b>Instagram</b> — <c>field: "comments"</c>, chỉ chở bình luận mới. KHÔNG có
    ///     <c>verb</c>, cũng không có sự kiện sửa/xoá nào cả.</item>
    /// </list>
    ///
    /// <para><b>Bỏ qua bình luận của CHÍNH mình</b> (<c>from.id</c> trùng mã Trang): đó là câu
    /// người trực vừa trả lời, vọng ngược về. Ghi lại thành bình luận của khách là hộp thư tự nói
    /// chuyện với nó, và mỗi lượt trả lời lại đội thêm một tin chưa đọc.</para>
    /// </summary>
    private static void DocBinhLuan(JsonArray thayDoi, string? maTrang, string? mocEntry,
        ChatChannel kenh, List<InboundChatEvent> ra)
    {
        foreach (var c in thayDoi)
        {
            var truong = c?["field"]?.ToString();
            var v = c?["value"];
            if (v is null) continue;

            var laFacebook = truong == "feed";
            var laInstagram = truong == "comments";
            if (!laFacebook && !laInstagram) continue;

            // "feed" chở cả đăng bài / thả tim / chia sẻ — chỉ lấy đúng bình luận.
            if (laFacebook && v["item"]?.ToString() != "comment") continue;

            var maBinhLuan = (laFacebook ? v["comment_id"] : v["id"])?.ToString();
            if (string.IsNullOrWhiteSpace(maBinhLuan)) continue;

            var nguoi = v["from"]?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(nguoi)) continue;

            // Bình luận của chính Trang mình — xem ghi chú ở phần tóm tắt.
            if (!string.IsNullOrWhiteSpace(maTrang) && nguoi == maTrang) continue;

            var verb = laFacebook ? v["verb"]?.ToString() : "add";
            var chu = (laFacebook ? v["message"] : v["text"])?.ToString();

            if (verb is "remove" or "edited")
            {
                ra.Add(new(kenh, nguoi!, null, ChatKind.Text, null, null, DateTime.UtcNow,
                    Surface: ChatSurface.Comment,
                    CommentChange: new(maBinhLuan!, chu, verb == "remove")));
                continue;
            }

            // Mã BÀI VIẾT. Instagram để trong media.id; thiếu nó thì không biết bình luận thuộc
            // bài nào — bỏ qua còn hơn dồn mọi bài vào chung một hội thoại.
            var maBai = (laFacebook ? v["post_id"] : v["media"]?["id"])?.ToString();
            if (string.IsNullOrWhiteSpace(maBai)) continue;

            ra.Add(new(kenh, nguoi!, maBinhLuan, ChatKind.Text, chu, null,
                LucBinhLuan(v, mocEntry),
                DisplayName: (laFacebook ? v["from"]?["name"] : v["from"]?["username"])?.ToString(),
                Surface: ChatSurface.Comment,
                ThreadId: maBai,
                ParentExternalId: NullNeuRong(v["parent_id"]?.ToString())));
        }
    }

    /// <summary>
    /// Facebook để mốc ở <c>created_time</c> của chính bình luận; Instagram không có, phải mượn
    /// <c>entry.time</c>. Cả hai đều tính bằng GIÂY (không phải mili-giây như bên tin nhắn) —
    /// nhầm đơn vị là mọi bình luận nhảy sang năm 1970 và nằm sai chỗ trong dòng thời gian.
    /// </summary>
    private static DateTime LucBinhLuan(JsonNode v, string? mocEntry)
    {
        var giay = v["created_time"]?.ToString() ?? mocEntry;
        return long.TryParse(giay, out var s) && s > 0
            ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime
            : DateTime.UtcNow;
    }

    private static string? NullNeuRong(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Bóc ba trường lỗi của Graph ra khỏi thân trả về.
    ///
    /// <para><b>Vì sao cần cả ba.</b> Chỉ <c>code</c> thôi là không đủ: mã 200 của Meta vừa nghĩa
    /// là "thiếu quyền" vừa là chỗ họ nhét ca <b>khách tự tắt nhận tin</b> vào, và chỉ
    /// <c>error_subcode</c> mới tách được hai ca đó. Còn <c>type</c> là lưới vét cuối — Meta đẻ mã
    /// mới liên tục nhưng <c>OAuthException</c> thì giữ nguyên tên qua nhiều năm.</para>
    ///
    /// <para>Dùng chung cho Messenger, Instagram và WhatsApp vì cả ba trả lỗi cùng một hình dạng
    /// <c>{"error":{"code":…,"error_subcode":…,"type":…}}</c> — dù <b>bảng mã thì khác nhau</b>,
    /// nên chỗ gọi vẫn phải chọn đúng <c>ChannelFailures.FromMeta</c> hay <c>FromWhatsApp</c>.</para>
    /// </summary>
    public static (int? Code, long? SubCode, string? Type) ReadErrorFields(JsonObject? than)
    {
        var e = than?["error"];
        if (e is null) return (null, null, null);

        int? ma = int.TryParse(e["code"]?.ToString(), out var m) ? m : null;
        long? maPhu = long.TryParse(e["error_subcode"]?.ToString(), out var mp) ? mp : null;
        return (ma, maPhu, e["type"]?.ToString());
    }

    /// <summary>
    /// <c>created_time</c> của Graph là ISO-8601 kèm múi giờ. Đọc hỏng thì lấy giờ hiện tại —
    /// thà một tin sai chỗ còn hơn cả mảnh hội thoại biến mất.
    /// </summary>
    private static DateTime LucGuiLichSu(JsonNode m)
        => DateTimeOffset.TryParse(m["created_time"]?.ToString(), out var luc)
            ? luc.UtcDateTime
            : DateTime.UtcNow;
}
