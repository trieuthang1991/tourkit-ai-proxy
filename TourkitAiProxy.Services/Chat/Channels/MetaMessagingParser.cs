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
    public static IReadOnlyList<InboundChatEvent> Boc(string rawBody, ChatChannel kenh)
    {
        var ra = new List<InboundChatEvent>();
        JsonNode? goc;
        try { goc = JsonNode.Parse(rawBody); } catch { return ra; }

        // Meta gói nhiều sự kiện trong một lần gọi: entry[] × messaging[]. Bóc thiếu vòng lặp là
        // mất tin khi khách nhắn dồn.
        if (goc?["entry"] is not JsonArray entries) return ra;
        foreach (var e in entries)
        {
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
                var tt = m["delivery"] is not null ? ChatState.DaNhan
                       : m["read"] is not null ? ChatState.DaXem
                       : (ChatState?)null;
                if (tt is { } trangThai)
                {
                    var uidM = m["sender"]?["id"]?.ToString();
                    var wm = m[trangThai == ChatState.DaNhan ? "delivery" : "read"]?["watermark"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uidM) && long.TryParse(wm, out var wms))
                    {
                        var mocLuc = DateTimeOffset.FromUnixTimeMilliseconds(wms).UtcDateTime;
                        ra.Add(new(kenh, uidM!, null, ChatKind.Chu, null, null,
                            mocLuc, Watermark: new(trangThai, mocLuc)));
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
                        ra.Add(new(kenh, uidCx!, null, ChatKind.Chu, null, null, lucCx,
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
                var nguon = m["referral"] ?? m["postback"]?["referral"];
                ChatReferral? tuDau = nguon is null ? null : new(
                    nguon["source"]?.ToString(), nguon["ref"]?.ToString(), nguon["ad_id"]?.ToString());

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
                        ra.Add(new(kenh, uidPb!, pb["mid"]?.ToString(), ChatKind.Chu,
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
                        ra.Add(new(kenh, uidRf!, null, ChatKind.Chu, null, null,
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

                var loai = ChatKind.Chu;
                string? att = null;
                if (msg["attachments"] is JsonArray a && a.Count > 0)
                {
                    att = a.ToJsonString();
                    loai = a[0]?["type"]?.ToString() switch
                    {
                        "image" => ChatKind.Anh,
                        "audio" => ChatKind.AmThanh,
                        "video" or "file" => ChatKind.Tep,
                        "location" => ChatKind.ViTri,
                        _ => ChatKind.Tep,
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
}
