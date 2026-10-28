# Tích hợp Chat nội bộ vào trang web (CRM, màn đơn hàng…)

Tài liệu cho lập trình viên: nhúng widget chat nội bộ của TRAV-AI vào một trang web khác (CRM web
tourkit, trang quản lý đơn…), gắn nút **Chat** lên từng đơn hàng / khách / công ty, và bắt sự kiện tin mới.

Trong chính app TRAV-AI thì widget **tự nhúng** sẵn (không phải làm gì), xem §8 nếu muốn thêm nút Chat vào
một trang khác của app.

Bật tính năng trên máy chủ trước: [trien-khai-chat-noi-bo.md](trien-khai-chat-noi-bo.md).

```
 Trình duyệt (trang CRM)                         Máy chủ CRM              TRAV-AI (tourkit-ai.tourkit.vn)
 ─────────────────────────                       ───────────              ───────────────────────────────
                                                 ① lấy mã phiên  ───────► /api/v1/sso/register-code (HMAC)
                                                                  ───────► /api/v1/sso/exchange → mã phiên
 ② <script internal-chat.js data-session="…"> ◄── render vào HTML
 ③ TkInternalChat.openEntity('order', 'DH-0142') ───────────────────────► /api/v1/chat/internal/* (X-Session-Id)
 ④ nhận tin tức thời  ◄──────────────────────────────────────────────── /api/v1/chat/internal/events (SSE)
```

| | |
|---|---|
| Script | `https://tourkit-ai.tourkit.vn/internal-chat.js` |
| API | `https://tourkit-ai.tourkit.vn/api/v1/chat/internal/*` (chi tiết: [api-surface.md § Chat nội bộ](api-surface.md)) |
| Xác thực | Mã phiên TRAV-AI (header `X-Session-Id`, widget tự gửi) |
| CORS | Mở mọi origin: nhúng từ tên miền nào cũng được |
| Giao diện | Shadow DOM, không đụng CSS của trang; JS thuần, không cần jQuery/React |

---

## 1. Lấy mã phiên TRAV-AI cho người đang đăng nhập CRM

Widget cần **mã phiên TRAV-AI của đúng người đang dùng CRM** (`data-session`). Mọi tin gửi đi mang tên
người này, nên tuyệt đối không dùng chung một mã cho nhiều người.

CRM không giữ mật khẩu người dùng, nên lấy mã phiên qua **SSO chiều CRM → TRAV-AI** đã có sẵn (cùng secret
HMAC với SSO hiện tại, không gửi mật khẩu):

1. Máy chủ CRM ký JSON `{tenant, username, redirect, iat}` bằng HMAC-SHA256 (khoá = secret SSO dùng chung,
   bên TRAV-AI là `Sso:Secret`), gửi `POST /api/v1/sso/register-code` kèm header `X-Sign` → nhận `exchangeUrl`.
2. Máy chủ CRM gọi `GET exchangeUrl` **không theo chuyển hướng**. Phản hồi 302 có `Set-Cookie: tk_sso=<mã phiên>`.
   Giá trị cookie chính là mã phiên. Cookie rỗng = người này không tạo được phiên (sai tenant/username).
3. Lưu mã vào `Session` của CRM (hết hạn cùng phiên đăng nhập CRM) để không gọi lại ở mỗi trang.

`tenant` là tên miền CRM của công ty (vd `abc.tourkit.vn`, chính là `Request.Url.Host`), `username` là tên
đăng nhập CRM. Người chưa từng vào TRAV-AI được tạo phiên tự động ở bước 2, **với điều kiện** TourKit.Api
đã bật cấp vé SSO (`sso-token`); chưa bật thì cookie rỗng, người đó phải đăng nhập TRAV-AI một lần trước.

Mã mẫu .NET Framework 4.8 (đặt ở chỗ dùng chung của CRM, cạnh `SsoController`):

```csharp
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Configuration;
using System.Web.Script.Serialization;

public static class TravAiChatSession
{
    private const string Base = "https://tourkit-ai.tourkit.vn";
    private const string SessionKey = "TravAiChatSid";

    /// Mã phiên TRAV-AI của người đang đăng nhập; null = không lấy được (widget sẽ không hiện).
    public static string Get(HttpContext ctx, string username)
    {
        var cached = ctx.Session[SessionKey] as string;
        if (!string.IsNullOrEmpty(cached)) return cached;
        try
        {
            var sid = Issue(ctx.Request.Url.Host, username);
            if (!string.IsNullOrEmpty(sid)) ctx.Session[SessionKey] = sid;
            return sid;
        }
        catch (Exception ex)
        {
            // TRAV-AI lỗi thì trang CRM vẫn phải mở bình thường, chỉ mất ô chat.
            System.Diagnostics.Trace.TraceWarning("TravAiChatSession: " + ex.Message);
            return null;
        }
    }

    private static string Issue(string tenant, string username)
    {
        var secret = WebConfigurationManager.AppSettings["SsoSecret"];   // = Sso:Secret bên TRAV-AI
        var body = new JavaScriptSerializer().Serialize(new
        {
            tenant, username, redirect = "/",
            iat = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds,
        });

        // ① register-code (ký trên ĐÚNG chuỗi byte gửi đi)
        var reg = (HttpWebRequest)WebRequest.Create(Base + "/api/v1/sso/register-code");
        reg.Method = "POST";
        reg.ContentType = "application/json";
        reg.Headers.Add("X-Sign", HmacHex(body, secret));
        reg.Timeout = 8000;
        var bytes = Encoding.UTF8.GetBytes(body);
        using (var s = reg.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
        string exchangeUrl;
        using (var r = (HttpWebResponse)reg.GetResponse())
        using (var sr = new StreamReader(r.GetResponseStream()))
            exchangeUrl = Regex.Match(sr.ReadToEnd(), "\"exchangeUrl\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        if (string.IsNullOrEmpty(exchangeUrl)) return null;

        // ② exchange — KHÔNG theo 302, đọc cookie tk_sso
        var ex = (HttpWebRequest)WebRequest.Create(Regex.Unescape(exchangeUrl));   // bỏ escape JSON (+…)
        ex.AllowAutoRedirect = false;
        ex.Timeout = 8000;
        using (var r = (HttpWebResponse)ex.GetResponse())
        {
            var m = Regex.Match(r.Headers["Set-Cookie"] ?? "", "tk_sso=([^;]*)");
            return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
        }
    }

    private static string HmacHex(string body, string secret)
    {
        using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
        {
            var sb = new StringBuilder();
            foreach (var b in h.ComputeHash(Encoding.UTF8.GetBytes(body))) sb.Append(b.ToString("x2"));
            return sb.ToString();   // hex chữ thường, khớp HmacHex bên TRAV-AI
        }
    }
}
```

> **Bảo mật:** mã phiên là chìa khoá đăng nhập TRAV-AI của người đó (dùng được mọi API TRAV-AI họ có quyền).
> Chỉ render vào trang của chính họ; không ghi log, không đưa vào URL, không gửi cho người khác.
> Secret SSO chỉ nằm trên máy chủ CRM, không bao giờ xuống trình duyệt.

## 2. Nhúng script

Đặt **một lần** ở trang khung chung (master page / layout) của CRM, trước `</body>`:

```aspx
<% var tkChatSid = TravAiChatSession.Get(Context, CurrentUser.UserName); %>
<% if (!string.IsNullOrEmpty(tkChatSid)) { %>
<script async src="https://tourkit-ai.tourkit.vn/internal-chat.js"
        data-session="<%= HttpUtility.HtmlAttributeEncode(tkChatSid) %>"
        data-order-url="/order-detail?id={id}"
        data-position="br"></script>
<% } %>
```

Nút chat hiện ở góc phải dưới; số tin chưa đọc hiện ngay trên nút.

![Nút chat ở góc phải dưới](images/trien-khai-chat-noi-bo/02-nut-chat-goc-phai.png)

Tuỳ chọn `data-*`:

| Thuộc tính | Ý nghĩa |
|---|---|
| `data-session` | **Bắt buộc.** Mã phiên TRAV-AI (§1) |
| `data-position` | `br` (phải dưới, mặc định) hoặc `bl` (trái dưới) |
| `data-z-index` | Lớp chồng, mặc định `2147483000`. Hạ xuống nếu che modal của CRM |
| `data-order-url` | Mẫu link mở đơn, `{id}` thay bằng mã đơn → phòng đơn hàng có nút **Mở đơn** (mở tab mới) |
| `data-deal-url` | Như trên cho cơ hội → phòng cơ hội có nút **Mở cơ hội** |
| `data-launcher` | `false` = ẩn nút nổi, CRM tự đặt nút riêng rồi gọi `TkInternalChat.open()` (§5) |
| `data-page-url` | Đường dẫn trang Chat riêng (CRM: `/chat`). Có thì ô chat nhỏ và khung có nút **Mở trong trang Chat** → `{url}?room={id}`; `TkInternalChat.openPage(roomId?)` |
| `data-page-mount` | Id phần tử trên CHÍNH trang Chat. Có thì widget vẽ vào đó thành 3 cột (danh sách · phòng · thông tin, ≥1200px), 2 cột + ngăn kéo thông tin (760–1199px), 1 cột (<760px); không nút nổi, không ô nhỏ; `?room=` trên địa chỉ = phòng đang mở. Phần tử phải có chiều cao |

Script nạp `async`. Muốn gọi API ngay khi trang tải thì chờ widget sẵn sàng:

```js
function whenChatReady(fn) {
  if (window.TkInternalChat) return fn(window.TkInternalChat);
  window.addEventListener('tk-internal-chat-ready', () => fn(window.TkInternalChat), { once: true });
}
```

Đăng xuất CRM: gọi `TkInternalChat.destroy()` trước khi chuyển trang, và xoá `Session["TravAiChatSid"]`.

## 3. Gắn nút Chat vào đơn hàng

Mỗi đơn hàng có **đúng một phòng chat** (khoá `order:<mã đơn>`).

**Bấm Chat KHÔNG tạo phòng ngay** (chống bấm nhầm). `openEntity` hỏi máy chủ trước (chỉ đọc):

| Tình huống | Widget làm gì | `openEntity` trả |
|---|---|---|
| Phòng đã có, mình đang ở trong | Mở thẳng phòng, đủ lịch sử | `{ roomId: 123 }` |
| Phòng chưa có | Mở **ô nháp** "Chưa có cuộc trò chuyện nào" | `{ roomId: null, draft: true }` |
| Phòng đã có, mình KHÔNG ở trong (chưa từng ở, tự rời hay bị bỏ) + có quyền `CHAT_XEM_ALL` | Mở phòng ở chế độ **chỉ xem** (không ô soạn, không nút tham gia) | `{ roomId: 123 }` |
| Phòng đã có, mình KHÔNG ở trong + không có `CHAT_XEM_ALL` | Không mở gì | reject `Error("… nhờ trưởng nhóm thêm vào để nhắn")` |
| Không có quyền (phòng cơ hội) | Không mở gì | reject `Error("Bạn không có quyền xem cơ hội này")` |

Phòng chỉ được tạo khi **gửi tin đầu tiên** trong ô nháp; `members`, `title`, `owner` dùng đúng lúc tạo đó. Đóng
ô nháp khi chưa gửi là không để lại gì. **Phòng đã tạo xong thì không ai tự vào bằng nút Chat** (chốt 28/10/2026),
muốn vào phải được trưởng nhóm thêm (§9).

```html
<button type="button" class="btn btn-default btn-sm tk-chat-order"
        data-order-id="DH-0142" data-order-title="Nhật Bản mùa lá đỏ"
        data-members="12,34" style="display:none">
  <i class="fa fa-comments"></i> Chat
</button>

<script>
  // Hiện nút chỉ khi widget đã nạp (người này có phiên + tính năng đang bật).
  whenChatReady(function () { $('.tk-chat-order').show(); });

  $(document).on('click', '.tk-chat-order', function () {
    var b = $(this);
    var members = String(b.data('members') || '').split(',').map(Number).filter(Boolean);
    window.TkInternalChat
      .openEntity('order', String(b.data('order-id')), {
        title: b.data('order-title'), members: members,
        data: { url: location.origin + '/order-detail?id=' + b.data('order-id') }   // nút "Xem lại" trong phòng
      })
      .catch(function (e) { alert('Không mở được chat: ' + e.message); });
  });
</script>
```

| Tham số | Ý nghĩa |
|---|---|
| `type` | `'order'` (đơn hàng), `'company'` (công ty/khách đoàn), `'deal'` (cơ hội bán hàng) |
| mã | Mã bản ghi, chuỗi. Cùng mã = cùng phòng |
| `title` | Tên hiển thị. Phòng đơn hàng hiện là `Đơn <mã> · <title>` |
| `members` | Mã người dùng CRM (`UserId`) cần có mặt. **Chỉ áp lúc phòng vừa tạo** (cùng người bấm). Người không có trong danh bạ nhân viên (đại lý / CTV) bị bỏ ra, không làm hỏng lần tạo |
| `owner` + `ownerSig` | **Tuỳ chọn (order / company).** Trưởng nhóm = người tạo bản ghi, CRM ký HMAC (§9.3). Thiếu / sai chữ ký → người tạo phòng làm trưởng nhóm |
| `data` | **Tuỳ chọn.** Object tự do (tối đa 4 KB), lưu theo phòng, mọi thành viên đọc lại được qua `room.data` (§6). Có `data.url` (http/https đầy đủ) thì đầu phòng có nút **Xem lại** mở đường dẫn đó |
| `opts.in` | `'dock'` (ô chat nhỏ nổi, mặc định) hoặc `'panel'` (mở trong khung danh sách) |

**Truyền thêm tham số (`data`).** Muốn kèm gì thì kèm: đường dẫn, trạng thái, mã khách… Widget chỉ dùng
`url`, phần còn lại CRM tự đọc lại khi cần:

```js
TkInternalChat.openEntity('order', 'DH-0142', {
  title: 'Nhật Bản mùa lá đỏ',
  data: { url: 'https://crm.cty.vn/order-detail?id=142', trangThai: 'Đã cọc', khach: 'Nguyễn Văn A' }
});
```

- Lần đầu: lưu cùng lúc tạo phòng (gửi tin đầu tiên).
- Mở lại với `data` **khác** cái đang lưu: thay, và mọi người đang mở phòng thấy nút đổi ngay. Giống thì không
  ghi gì (so theo nội dung, không theo thứ tự khoá). Không truyền `data` thì giữ nguyên cái đang có.
- `url` phải là đường dẫn đầy đủ `http(s)://…`; `javascript:`, đường dẫn tương đối… bị từ chối (reject `Error`).
- Có `data.url` thì nút **Xem lại** thắng `data-order-url` / `setEntityLinks`.

Muốn tự mở bản ghi bằng mã (vd mở CRM có đăng nhập sẵn) thay cho mẫu link:

```js
TkInternalChat.setEntityLinks({
  order: function (id) { window.open('/order-detail?id=' + encodeURIComponent(id), '_blank'); },
  deal:  function (id) { window.open('/booking-ticket/' + encodeURIComponent(id), '_blank'); }
});
```

Chỉ có **tên** người phụ trách, không có mã? Dò bằng `findUserIds`:

```js
TkInternalChat.findUserIds('Trần Thu Hà, Nguyễn Minh').then(function (r) {
  // r.ids = [12, 34]; r.unmatched = các tên không dò được (trùng tên thì KHÔNG đoán)
  return TkInternalChat.openEntity('order', 'DH-0142', { title: 'Nhật Bản', members: r.ids });
});
```

Bấm Chat trên một bản ghi chưa có phòng (ảnh từ màn Cơ hội, đơn hàng hiện y hệt):

![Ô nháp: gửi tin đầu tiên mới tạo phòng](images/trien-khai-chat-noi-bo/10-o-nhap.png)

Gửi tin đầu tiên xong, ô nháp thành phòng thật:

![Phòng chat của một bản ghi](images/trien-khai-chat-noi-bo/07-phong-cua-co-hoi.png)

**Quyền vào phòng:**

Mọi loại phòng đều cần quyền chat CRM trước (`CHAT_XEM` hoặc `CHAT_XEM_ALL`, §9.1). Thêm vào đó:

| Loại | Ai TẠO được phòng (gửi tin đầu tiên) | Vào phòng đã có |
|---|---|---|
| `deal` | Người xem được cơ hội đó trên CRM (`CH_XEM_ALL`: mọi cơ hội; `CH_XEM`: cơ hội mình tạo hoặc phụ trách). Người phụ trách tự được thêm lúc tạo | Chỉ khi trưởng nhóm thêm |
| `order`, `company` | Nhân viên bấm Chat trên bản ghi (CRM chỉ hiện nút cho người xem được đơn đó) | Chỉ khi trưởng nhóm thêm |
| `key` | Ai biết khoá (xem §4) | Chỉ khi trưởng nhóm thêm |

## 4. Các kiểu phòng khác

```js
TkInternalChat.openDirect(12);                                     // 1-1 với nhân viên mã 12
TkInternalChat.openGroup({ title: 'Đoàn Nhật', members: [12, 34] }); // nhóm MỚI mỗi lần gọi
TkInternalChat.openByKey('nhom_marketing', { title: 'Nhóm Marketing', members: [12, 34, 56] });
```

- **1-1 và nhóm** không cần truyền khoá: máy chủ tự sinh. A mở với B hay B mở với A luôn ra cùng một phòng 1-1.
- **`openByKey`**: khoá tự đặt (chữ, số, `_ - . :`, tối đa 100 ký tự). Chưa có thì tạo, có rồi thì mở đúng
  phòng đó. Hợp cho phòng chung cố định ("nhom_cong_ty", "nhom_marketing") hoặc bản ghi chưa có kiểu riêng
  ("phieuthu_8812"). Không kiểm quyền: đừng dùng cho chuyện cần giữ kín.

## 5. Toàn bộ hàm và sự kiện

Mọi hàm `open*` trả `Promise<{ roomId }>`, hỏng thì reject `Error` (thông điệp tiếng Việt, hiện thẳng được).
`openEntity` / `openByKey` trả `{ roomId: null, draft: true }` khi mở ô nháp (xem §3).

| Hàm | Việc |
|---|---|
| `open()` / `close()` / `toggle()` | Mở / đóng khung danh sách cuộc trò chuyện |
| `openEntity(type, id, {title, members, data}, opts)` | Phòng theo đơn / công ty / cơ hội (§3) |
| `openDirect(userId, opts)` | Nhắn 1-1 |
| `openGroup({title, members}, opts)` | Tạo nhóm mới |
| `openByKey(key, {title, members, data}, opts)` | Phòng khoá tự đặt (`data` như §3) |
| `setEntityLinks({ order: fn(id), deal: fn(id) })` | Hàm mở bản ghi cho nút **Mở đơn** / **Mở cơ hội** |
| `openRoom(roomId, opts)` | Mở phòng đã có theo mã phòng |
| `findUserIds(names)` | Dò mã người theo tên → `{ ids, unmatched }` |
| `renameRoom(roomId, title)` | Đổi tên (không áp cho 1-1) |
| `addMembers(roomId, [ids])` | Thêm người — **chỉ trưởng nhóm** (§9.2) |
| `removeMember(roomId, id)` / `leaveRoom(roomId)` | Bỏ người khác: chỉ trưởng nhóm. Tự rời: ai cũng được (trưởng nhóm rời → quyền sang người vào sớm nhất còn lại) |
| `transferOwner(roomId, userId)` | Trao quyền trưởng nhóm (không áp phòng order / company) |
| `deleteRoom(roomId)` | Xoá phòng với mọi người — chỉ trưởng nhóm |
| `openPage(roomId?)` | Sang trang Chat riêng (cần `data-page-url`, §9.5) |
| `hideRoom(roomId)` | "Xoá phía tôi" (ẩn với riêng mình) |
| `closeRoom(roomId)` | Đóng ô chat của phòng |
| `getUnread()` | Tổng tin chưa đọc |
| `setLauncherVisible(bool)` | Hiện / ẩn nút nổi |
| `destroy()` | Gỡ hẳn widget (đăng xuất) |

Sự kiện: `TkInternalChat.on(tên, hàm)` trả về hàm huỷ đăng ký.

| Sự kiện | Dữ liệu | Dùng để |
|---|---|---|
| `ready` | `{ me }` (mã người hiện tại) | Hiện các nút Chat |
| `unread` | số tin chưa đọc | Badge riêng trên menu CRM |
| `message` | `{ roomId, messageId }` | Tin mới tới |
| `room` | `{ roomId }` | Phòng đổi tên / thành viên |
| `error` | `{ message }` | Ghi log / báo lỗi |

Ví dụ: tự đặt nút trên thanh menu CRM thay cho nút nổi (`data-launcher="false"`):

```html
<a href="#" id="crmChatMenu"><i class="fa fa-comments"></i> Chat <span class="badge" id="crmChatBadge"></span></a>
<script>
  whenChatReady(function (chat) {
    $('#crmChatMenu').on('click', function (e) { e.preventDefault(); chat.toggle(); });
    chat.on('unread', function (n) { $('#crmChatBadge').text(n > 99 ? '99+' : (n || '')); });
  });
</script>
```

## 6. Gọi API trực tiếp (không dùng widget)

Khi cần làm việc ở máy chủ (vd tự tạo phòng đơn hàng lúc tạo đơn), gọi REST với header `X-Session-Id`:

Chỉ HỎI phòng của một bản ghi (không tạo gì, kiểm quyền cơ hội ngay):

```http
GET https://tourkit-ai.tourkit.vn/api/v1/chat/internal/rooms/lookup?entityType=order&entityId=DH-0142
X-Session-Id: <mã phiên>
```

→ `{ "id": 20154 | null, "member": true | false, "roomKey": "order:DH-0142", "data": {…} | null }`
(`data` chỉ trả cho thành viên phòng).

Tạo (hoặc vào) phòng, nên gọi ngay trước khi gửi tin đầu tiên:

```http
POST https://tourkit-ai.tourkit.vn/api/v1/chat/internal/rooms
X-Session-Id: <mã phiên>
Content-Type: application/json

{ "entityType": "order", "entityId": "DH-0142", "title": "Nhật Bản mùa lá đỏ", "memberUserIds": [12, 34],
  "data": { "url": "https://crm.cty.vn/order-detail?id=142" } }
```

→ `{ "id": 20154, "roomKey": "order:DH-0142", "created": true }`. Gửi tin: `POST /rooms/{id}/messages`
`{ "body": "…" }`. Thay tham số của phòng: `PUT /rooms/{id}/data` `{ "data": {…} | null }` (thành viên phòng;
giống giá trị đang có thì không ghi). `GET /rooms` trả `data` của từng phòng. Danh sách đầy đủ:
[api-surface.md § Chat nội bộ](api-surface.md).

## 7. Kiểm tra sau khi tích hợp

1. Mở trang CRM, xem nguồn trang: thẻ `<script … internal-chat.js data-session="…">` có mặt, `data-session` không rỗng.
2. Nút chat hiện ở góc. Không hiện → mở Console, lọc `tk-chat`.
3. Bấm **Chat** trên một đơn: ô chat mở tên `Đơn <mã> · …`. Bấm lại: cùng phòng, không sinh phòng mới.
4. Người thứ hai (trình duyệt khác) bấm Chat trên **cùng đơn**: vào đúng phòng đó, thấy tin của người thứ nhất.

| Triệu chứng | Nguyên nhân | Xử lý |
|---|---|---|
| Không có thẻ script trong trang | `TravAiChatSession.Get` trả null | Xem log CRM; `register-code` trả 401 = secret không khớp `Sso:Secret`; cookie `tk_sso` rỗng = sai `tenant`/`username` |
| Console: `[tk-chat] phiên hết hạn — widget tắt` | Mã phiên đã bị đăng xuất / hết hạn | Xoá `Session["TravAiChatSid"]` rồi tải lại trang để lấy mã mới |
| Mở chat báo "Tính năng chat nội bộ đang tắt" | Cờ chưa bật hoặc chưa khởi động lại TRAV-AI | [trien-khai-chat-noi-bo.md](trien-khai-chat-noi-bo.md) bước 4 |
| `openEntity` báo "Có người không thuộc công ty: 99" | `members` có mã không phải nhân viên công ty này | Dùng `UserId` CRM của đúng công ty |
| Ô chat nằm dưới modal của CRM | z-index | Đặt `data-z-index` thấp hơn modal |

## 8. Thêm nút Chat vào một trang khác trong app TRAV-AI

Trong app TRAV-AI, widget đã tự nhúng ([core/internal-chat-loader.js](../wwwroot/core/internal-chat-loader.js)),
chỉ cần gọi API. Làm theo mẫu `DealChatButton` ở [pages/deals.jsx](../wwwroot/pages/deals.jsx): nút chỉ hiện khi
widget đã nạp, bấm thì `openEntity`:

```jsx
function OrderChatButton({ order, pushToast }) {
  const [on, setOn] = React.useState(() => !!window.TkInternalChat);
  React.useEffect(() => {
    if (on) return undefined;
    const ready = () => setOn(true);
    window.addEventListener('tk-internal-chat-ready', ready);
    return () => window.removeEventListener('tk-internal-chat-ready', ready);
  }, [on]);
  if (!on) return null;
  return (
    <button type="button" className="btn btn-ghost btn-sm" onClick={(e) => {
      e.stopPropagation();
      window.TkInternalChat.openEntity('order', String(order.code), { title: order.tourName })
        .catch((err) => pushToast?.('Không mở được chat: ' + err.message, 'warn'));
    }}>
      <Icon name="users" size={13} /> Chat
    </button>
  );
}
```

---

## 9. Phân quyền, trưởng nhóm và luồng vào phòng (chốt 27–28/10/2026)

Mục này là **luật nghiệp vụ** của chat nội bộ. Máy chủ TRAV-AI là chốt cuối; widget và CRM chỉ ẩn / hiện nút theo
cùng luật để người dùng không bấm vào chỗ sẽ bị từ chối.

### 9.1 Quyền chat (CRM → TRAV-AI)

| Quyền CRM (tick theo phòng ban) | Mã | Thấy gì |
|---|---|---|
| Xem đoạn chat mình được phân công | `CHAT_XEM` | Chỉ các cuộc **mình đang ở** |
| Xem tất cả đoạn chat | `CHAT_XEM_ALL` | **Mọi** cuộc của công ty. Cuộc mình không ở → **chỉ xem** (không soạn, không cảm xúc, không cấu hình, không tự vào) |
| Không tick cả hai | — | **Không có chat**: CRM ẩn nút Chat (header, đơn hàng), trang `/chat` báo chưa được phân quyền; mọi API `/api/v1/chat/internal/*` trả **403** |

Không phụ thuộc phân quyền nhóm / chi nhánh / quyền đơn hàng. Tài khoản admin cũng theo đúng hai ô tick này.

**Nơi kiểm (một hàm cho mỗi câu hỏi, không chép tay mã quyền):**

| Câu hỏi | TRAV-AI | CRM |
|---|---|---|
| Được dùng chat không | `SessionAuth.CanUseInternalChatAsync` (gọi trong `InternalChatEndpoints.GuardAsync` → mọi endpoint) | `TravAiChatSession.CanUse` (Koji.Master, action `internal-chat`, `Pages/InternalChat.aspx`) |
| Có "xem tất cả" không | `SessionAuth.IsQuanTriChatAsync` — **nơi DUY NHẤT** đọc `CHAT_XEM_ALL` (`ChatScopeGuardTests` đếm) | `UIHelper.CheckCacheFunction("CHAT_XEM_ALL")` |
| App TRAV-AI có nạp widget không | `wwwroot/core/internal-chat-loader.js` đọc cache quyền `tourkit_tk_perms` | — |

**Đồng bộ quyền khi đổi trên CRM.** Cả hai bên đọc quyền từ cùng bảng phân quyền phòng ban, mỗi bên giữ một bản:

- CRM: `Session["SSListFunction"]` (từ `permission.json`).
- TRAV-AI: `TkSession.Permissions`, nạp qua TourKit.Api `GET /api/auth/permissions`, **nạp lại** khi:
  1. người dùng đăng nhập lại từ CRM (SSO exchange → `TkSessionStore.RefreshPermissionsAsync`), hoặc
  2. quá **5 phút** từ lần nạp trước (`TkSessionStore.EnsurePermissionsAsync`, `PermsTtl`). Lỗi khi nạp lại → giữ quyền cũ.

Trước 28/10/2026 quyền chỉ nạp một lần rồi đứng yên (phiên SSO được dùng lại) — bỏ tick "Xem tất cả" mà vẫn xem
được tất cả. Sự kiện realtime (`/events`) chốt phạm vi lúc kết nối, nên tab đang mở cần F5 / tự nối lại mới đổi theo.

### 9.2 Thành viên: ai vào phòng bằng đường nào

**Phòng đã tạo xong thì KHÔNG ai tự vào** — kể cả người chưa từng ở, người tự rời hay bị bỏ ra.

| Đường vào | Ai | Ghi chú |
|---|---|---|
| Lúc **tạo** phòng (gửi tin đầu tiên / tạo nhóm) | Người tạo + `members` truyền vào + trưởng nhóm CRM ký | `CreateOrGetRoomAsync` chỉ thêm khi `created`; phòng đã có thì không thêm ai |
| **Trưởng nhóm thêm** (`POST /rooms/{id}/members`) | Chỉ trưởng nhóm | Đường DUY NHẤT vào phòng đã có. Người đã rời / bị bỏ được thêm lại thì vào lại |
| Phòng đơn cũ: người tạo đơn CHƯA TỪNG ở | Hệ thống tự thêm làm trưởng nhóm khi mở từ CRM | `SyncOwnerCoreAsync` (một lần, chỉ khi chưa có dòng thành viên nào) |

Phòng đã có mà mình không ở:

- `GET /rooms/lookup` trả `member: false`, `canView` (có `CHAT_XEM_ALL`), `leftRoom` (từng ở rồi ra — chỉ để chọn câu báo).
- `POST /rooms` với phòng đó → **403** "nhờ trưởng nhóm thêm vào".
- Widget: `canView` → mở **chỉ xem**; không → báo lỗi, không mở ô nháp.

Bỏ người: `RemoveMemberAsync` — bỏ NGƯỜI KHÁC chỉ trưởng nhóm (ghi `chat_room_members.removed_by` = người bỏ); tự
rời ai cũng được (`removed_by` = NULL). Trưởng nhóm rời → quyền sang người vào phòng sớm nhất còn lại (cùng câu SQL).

### 9.3 Trưởng nhóm (`chat_conversations.owner_user_id`)

| Loại phòng | Trưởng nhóm | Trao quyền tay |
|---|---|---|
| Nhóm (`group`), cơ hội (`deal`), khoá tự do (`key`) | Người tạo phòng | Có (`POST /rooms/{id}/owner`) |
| Đơn hàng (`order`), công ty (`company`) | **Người tạo bản ghi** ở CRM (`INS_UID`; người tạo nghỉ / tài khoản hệ thống → người phụ trách) | Không — bám người tạo bản ghi, mở lại từ CRM là đồng bộ |
| 1-1 (`direct`) | Không có | — |

Chỉ trưởng nhóm: **thêm người**, bỏ người khác, trao quyền, xoá phòng (`DELETE /rooms/{id}`: xoá mềm, mọi người rời,
khoá phòng đổi đuôi `#xoa:{id}` để mở lại bản ghi ra phòng mới). Thành viên thường: nhắn, đổi tên, tự rời.

**Chữ ký trưởng nhóm phòng đơn.** CRM (`TravAiChatSession.SignRoomOwner`) ký
`HMAC-SHA256(SsoSecret, "{host}|order:{tour_type}-{id}|{owner}")` hex thường, widget chuyển nguyên
(`openEntity(..., { owner, ownerSig })`), TRAV-AI kiểm bằng `InternalRoomOwnerSignature.Verify` với `Sso:Secret`.
Không tin số client tự khai: không có chữ ký thì ai cũng tự xưng trưởng nhóm để xoá người / xoá phòng.

### 9.4 Phòng chat của đơn (CRM `internal-chat` → `tour-room`)

| Loại đơn | Bảng | Thành viên kéo sẵn lúc tạo phòng (theo thứ tự ưu tiên) |
|---|---|---|
| GIT, tour lẻ, VMB, khách sạn, visa, xe… | `tours` | Người tạo, phụ trách, theo dõi, quản lý (`ManagerIds`), người tạo các đơn hàng của tour |
| **FIT** (nút Chat gửi `type=2`) | **`tour_samples`** (FIT KHÔNG có trong `tours`) | Người tạo, **nhân viên điều hành** (`IdsNguoiTheoDoi`), toàn bộ sale của tour (`tour_sellers`) |

- Khoá phòng: `order:{tour_type}-{id}` (FIT: `order:2-{id}` — bảng `tours` không dùng loại 2 nên không trùng).
- Lọc mọi loại đơn (`InternalChatAction.ChatMembers`): bỏ người nghỉ việc, tài khoản hệ thống, **đại lý**
  (`typeuser = 2`) và **CTV** (phòng ban `1004`). Trần 200 người.
- Sale / điều hành được thêm vào đơn **sau khi** phòng đã tạo không tự vào phòng — trưởng nhóm thêm.

### 9.5 Trang Chat riêng `/chat`

- CRM: route `chat` → `Pages/InternalChat.aspx` có `#tkChatPage`; `internalChat.js` gắn `data-page-url="/chat"` và
  `data-page-mount="tkChatPage"` → widget vẽ **3 cột** (danh sách | phòng | thông tin) từ 1200px, 2 cột + ngăn kéo
  thông tin từ 760px, 1 cột trên điện thoại. Nút Chat trên header mở trang này.
- `?room={id}` trên địa chỉ = phòng đang mở (tải lại / gửi link vẫn đúng phòng).
- Ô chat nhỏ ở các màn khác có nút **Mở trong trang Chat** → `/chat?room={id}`.
- Ô tìm bọc `<form role="search" autocomplete="off">` để trình duyệt không tự điền tên đăng nhập.

### 9.6 Khi triển khai

1. DB chat (chạy trước, tránh khoá bảng hội thoại khách lúc proxy khởi động — migration trong `ChatDb` vẫn tự chạy):

   ```sql
   CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_conv_internal_no_owner ON chat_conversations (id)
     WHERE channel = 7 AND owner_user_id IS NULL AND entity_type IS DISTINCT FROM 'direct';
   CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_conv_internal_activity
     ON chat_conversations (tenant_id, last_activity_at DESC, id DESC) WHERE channel = 7 AND deleted_utc IS NULL;
   ```

   Các cột mới (`owner_user_id`, `deleted_utc`, `chat_room_members.removed_by`) nullable, không default → chỉ đổi
   metadata, chạy tức thì.
2. Deploy TRAV-AI trước, CRM sau. CRM: **tăng `Version`** trong `Configs/AppSettings.config` — script / CSS chat nạp
   qua `?v=`, không tăng thì trình duyệt giữ bản cũ (đã gây lỗi trưởng nhóm sai 27/10/2026).
3. Kiểm: log TRAV-AI có `ChatDb schema OK`; tài khoản không tick quyền chat không thấy nút Chat; tài khoản chỉ có
   `CHAT_XEM` không thấy cuộc đã rời.

---

**Chưa kiểm thử:** luồng lấy mã phiên ở §1 dùng đúng hai endpoint SSO đang chạy nhưng **chưa được gọi thử từ
CRM**; hãy thử trên staging trước (tenant `staging.tourkit.vn`).
