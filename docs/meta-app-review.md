# Xác minh & duyệt ứng dụng Meta — hồ sơ TourKit AI

> **Ứng dụng:** TourKit AI · `1416202417025806`
> **Cập nhật:** 29/08/2026

Tài liệu này để **dán thẳng vào hồ sơ**. Phần tiếng Anh là nội dung nộp cho Meta (người duyệt
đọc tiếng Anh); phần tiếng Việt là giải thích cho người trong nhà.

⚠️ **Mọi câu trả lời dưới đây mô tả luồng dữ liệu THẬT trong mã.** Sửa luồng (thêm kênh, đổi nơi
lưu, đổi nhà cung cấp AI) thì phải sửa cả tài liệu này — người duyệt đối chiếu lời khai với những
gì ứng dụng thật sự làm, và khai sai bị phạt nặng hơn nhiều so với khai thiếu.

---

## Thứ tự làm — sai thứ tự là làm lại

```
1. Business Portfolio  ──►  2. Nộp App Review  ──►  3. Xác minh doanh nghiệp  ──►  4. Tech Provider
   (gom app + Page,          (xin từng quyền)       (Meta ĐÒI thì mới mở)        (nếu Meta hỏi)
    điền chi tiết pháp nhân)
```

Xác minh gắn với **doanh nghiệp**, không gắn với app. Xác minh xong cho tài khoản cá nhân rồi mới
lập công ty là bỏ đi làm lại từ đầu.

⚠️ **Không xác minh trước được — và đây là chỗ mất thời gian nhất.** Trung tâm bảo mật hiện đúng
câu *"Tổ chức của bạn không cần xác minh"* kèm hai link **Meta đã xác minh** / **Ủy quyền và xác
minh**, không có nút bắt đầu nào. Rất dễ tưởng mình vào nhầm chỗ rồi đi tìm vòng quanh (đã mất một
buổi vì đúng chuyện này, 29/08/2026).

Meta chỉ mở xác minh **khi có thứ đòi đến nó**: nộp App Review xin một quyền nâng cao xong, hồ sơ
mới hiện yêu cầu xác minh và mở luồng nộp giấy tờ.

⚠️ **Hai link nó gợi ý đều KHÔNG phải thứ cần:**
- **Meta đã xác minh** (Meta Verified) — gói **trả phí hàng tháng**, tick xanh + hỗ trợ ưu tiên.
  Không mở khoá quyền nào cho app. Đừng mua.
- **Ủy quyền và xác minh** — dành cho quảng cáo chủ đề chính trị/xã hội. Không liên quan.

Vẫn phải điền **Chi tiết về doanh nghiệp** (tên pháp lý, địa chỉ, điện thoại, website) ở
*Cài đặt → Thông tin doanh nghiệp* TRƯỚC khi nộp App Review — bốn ô đó chính là thứ Meta đem đối
chiếu với ĐKKD khi luồng xác minh mở ra, và mặc định chúng TRỐNG.

---

## Bước 1 — Business Portfolio

### 1.1 · Tạo danh mục

`business.facebook.com` → **Tạo tài khoản**. Ba ô nó hỏi:

| Ô | Điền gì |
|---|---|
| **Tên doanh nghiệp** | ⚠️ **Tên pháp nhân ĐẦY ĐỦ, y hệt ĐKKD.** Đây chính là chuỗi Meta đem đi đối chiếu giấy tờ ở bước 2 |
| Tên bạn | Họ tên thật, khớp giấy tờ tuỳ thân |
| Email doanh nghiệp | `hotro@tourkit.vn` — đừng dùng Gmail |

**Ô đầu tiên là chỗ hỏng nhiều nhất.** Gõ "TourKit" cho gọn thì bước xác minh sẽ trả về vì không
khớp ĐKKD, mà đổi tên danh mục sau khi đã nộp hồ sơ thì rất phiền. Mở ĐKKD ra chép đúng từng chữ,
kể cả phần "Công ty TNHH" hay "Công ty Cổ phần".

### 1.2 · Đưa app vào danh mục

Hai đường, chọn một:

- **Từ phía app** (dễ hơn): `developers.facebook.com/apps/1416202417025806/settings/basic/`
  → kéo xuống mục **Xác minh doanh nghiệp** → chọn danh mục vừa tạo.
- **Từ phía danh mục**: `Cài đặt doanh nghiệp` → `Tài khoản` → `Ứng dụng` → **Thêm** → nhập
  App ID `1416202417025806`.

Bạn phải đang là **Admin của app** thì mới đưa được.

⚠️ Đưa app vào rồi thì **gỡ ra rất khó**. Chắc chắn đúng danh mục trước khi bấm.

### 1.3 · Đưa Page tourkitvn vào

`Cài đặt doanh nghiệp` → `Tài khoản` → `Trang` → **Thêm**.

Menu này có ba lựa chọn, chọn nhầm là hỏng:

| Lựa chọn | Dùng khi |
|---|---|
| **Thêm Trang** | ✅ Trang của **chính công ty bạn** — đây là cái cần chọn cho `tourkitvn` |
| Yêu cầu quyền truy cập vào Trang | Trang của **khách hàng** mà bạn xin quyền quản lý hộ |
| Tạo Trang mới | Không dùng |

Chọn nhầm "Yêu cầu quyền truy cập" cho Trang của chính mình là gửi một lời mời lơ lửng không ai
duyệt. Bạn phải đang là **Admin của Trang** (Editor không đủ).

### 1.4 · Thêm ít nhất 2 admin

`Cài đặt doanh nghiệp` → `Người dùng` → `Người` → **Thêm** → cấp **Quyền truy cập toàn bộ**.

Người được mời phải **mở email và bấm chấp nhận** thì mới có hiệu lực.

**Vì sao bắt buộc:** toàn bộ tin nhắn của mọi công ty khách hàng đi qua đúng một app này. Danh mục
chỉ có một admin, mà tài khoản đó bị khoá — vì lý do chẳng liên quan: một bài đăng bị báo cáo,
đăng nhập lạ chỗ, bị hack — thì **không ai lấy lại được**. Quy trình khôi phục quyền quản trị
doanh nghiệp của Meta nổi tiếng là chậm và hay không đi tới đâu.

Meta cũng bắt admin danh mục **bật xác thực hai lớp**; bật luôn cho cả hai người ngay từ đầu.

### 1.5 · Đổi email liên hệ của app

`developers.facebook.com/apps/1416202417025806/settings/basic/` → ô **Email liên hệ**.

Hiện đang là `trieuthangtin18a1@gmail.com`, đổi sang **`hotro@tourkit.vn`**.

Không phải chuyện hình thức: đây là địa chỉ Meta gửi **cảnh báo app sắp bị vô hiệu hoá**, thông
báo vi phạm chính sách và hạn chót xử lý. Gửi vào hộp thư cá nhân là đúng lúc cần thì không ai
thấy. Và xác minh doanh nghiệp cũng soi email theo tên miền — Gmail hay bị hỏi thêm giấy tờ.

---

## Bước 2 — Xác minh doanh nghiệp

`business.facebook.com` → **Cài đặt doanh nghiệp** → **Trung tâm bảo mật** → *Bắt đầu xác minh*.

### Giấy tờ (Việt Nam)

| Cần | Ghi chú |
|---|---|
| Giấy chứng nhận **đăng ký doanh nghiệp** | Bản scan màu, đọc rõ số và tên |
| **Mã số thuế** | Thường nằm luôn trên ĐKKD |
| Số điện thoại doanh nghiệp | Meta gọi hoặc nhắn mã xác minh |
| Website | `travelai.vn` — phải sống và có nhắc tên công ty |

### Bốn lý do bị trả về hay gặp nhất

1. **Tên không khớp từng chữ.** Trên ĐKKD là *"Công ty TNHH ..."* thì trong Business Portfolio
   phải ghi y hệt — không rút gọn thành "TourKit", không bỏ "Công ty TNHH".
2. **Địa chỉ viết tắt khác nhau.** "P." với "Phường", "Q.1" với "Quận 1" — Meta so máy móc.
3. **Số điện thoại không nhận được mã.** Dùng số công ty đang hoạt động, có người nghe.
4. **Website không nhắc tên pháp nhân.** Thêm tên công ty + địa chỉ vào chân trang `travelai.vn`.

Thường mất **2–5 ngày làm việc**.

---

## Bước 3 — Xác minh quyền truy cập (Tech Provider)

Meta hỏi vì sao bạn cần chạm dữ liệu của doanh nghiệp khác. Trả lời:

> TourKit AI is a multi-tenant SaaS inbox for Vietnamese travel agencies. Each agency connects
> **its own** Facebook Page through Facebook Login. We never access Pages we do not have explicit,
> revocable consent for. The agency remains the data controller for its customers' conversations;
> TourKit AI acts as a processor that stores and displays those conversations to that agency's own
> staff, and drafts suggested replies.

⚠️ **Không quay lại được** sau khi thành Tech Provider. Kèm rà soát định kỳ và yêu cầu bảo vệ dữ
liệu chặt hơn.

---

## Bước 4 — App Review từng quyền

### `pages_show_list`

> **How will you use this permission?**
>
> After an agency signs in with Facebook, we call `/me/accounts` to show them the list of Pages
> they manage, so they can choose which Page to connect to their TourKit AI inbox. We store only
> the Page id and Page access token for the Page they explicitly select. Pages they do not select
> are discarded and never stored.

### `pages_messaging`

> **How will you use this permission?**
>
> This is the core of the product. Once an agency connects a Page, we subscribe to that Page's
> messaging webhooks so incoming customer messages appear in the agency's shared inbox. Agency
> staff read and reply from our web app; replies are delivered through the Send API as the Page.
> An optional AI assistant drafts suggested replies, which the agency can enable or disable per
> conversation. Messages are stored so staff can see conversation history, hand over between
> shifts, and resolve customer disputes.

### `pages_manage_metadata`

> **How will you use this permission?**
>
> We use it solely to subscribe and unsubscribe the connected Page to our webhook
> (`/subscribed_apps`). Subscribing is required for the Page to deliver messages to us;
> unsubscribing runs when the agency disconnects the Page, so we stop receiving their data
> immediately. We do not change Page settings, posts, or any other metadata.

### `business_management`

> **How will you use this permission?**
>
> Agencies that manage their Pages through a Business Portfolio cannot be enumerated by
> `/me/accounts` alone — the call returns an empty list without this permission, even though the
> login succeeds. We request it only to resolve which Pages a signing-in agency is authorised to
> manage, so they can pick the right one. We do not create, modify or delete any business assets.

*(Ghi chú nội bộ: đây đúng là cái bẫy đã mất gần một buổi hôm 26/08 — cấp quyền thành công,
không lỗi, mà `/me/accounts` trả rỗng.)*

---

## Video quay màn hình

Đây là chỗ bị trả về nhiều nhất. Meta đòi thấy **thao tác thật**, không nhận ảnh ghép hay slide.

### Kịch bản — quay một mạch, không cắt

1. Mở `travelai.vn`, đăng nhập vào TourKit AI *(chuẩn bị sẵn tài khoản thử cho người duyệt)*
2. Vào **Hộp thư chat** → bấm **Kết nối kênh** → **Kết nối Facebook**
3. **Quay rõ màn hình đồng ý của Facebook** — thấy được danh sách quyền đang xin. *Bắt buộc.*
4. Chọn Page **tourkitvn** → quay lại ứng dụng, thấy Page đã nối
5. Lấy điện thoại nhắn một tin tới Page đó
6. Tin hiện trong hộp thư → gõ trả lời → quay sang điện thoại cho thấy khách **nhận được**
7. Bấm **Gỡ nối** để cho thấy dữ liệu ngừng chảy khi agency thôi dùng

### Ba lỗi làm hỏng video

- Thiếu bước 3 (màn hình đồng ý) — gần như chắc chắn bị trả về
- Quay bằng tài khoản Admin của app: người duyệt muốn thấy luồng của **người dùng thường**
- Không cho thấy dữ liệu **dùng vào việc gì** sau khi cấp quyền

---

## Đường dẫn dán vào Settings → Basic

```
Privacy Policy URL     https://travelai.vn/privacy
App Domains            travelai.vn
Website                https://travelai.vn
Terms of Service URL   (tuỳ chọn — bỏ trống vẫn lên Live được)
```

**User Data Deletion** — Meta cho chọn một trong hai. Chọn *Callback URL*, nó mạnh hơn hẳn:

| Kiểu | Điền | Khi nào dùng |
|---|---|---|
| **Data Deletion Callback URL** | `https://travelai.vn/api/v1/chat/webhook/meta/data-deletion` | ✅ Nên chọn — xoá TỰ ĐỘNG ngay khi người dùng gỡ ứng dụng, và trả mã tra cứu |
| Data Deletion Instructions URL | `https://travelai.vn/privacy#xoa-du-lieu` | Dự phòng nếu callback trục trặc |

Đường callback kiểm chữ ký `signed_request` bằng App Secret trước khi xoá bất cứ thứ gì — không
kiểm là dựng sẵn cửa cho người ngoài xoá dữ liệu khách của mọi công ty. Xoá xong nó trả về
`{ url, confirmation_code }`; người dùng mở `url` đó xem đã xoá những gì.

Cả hai đường trên **đã sống, mở được không cần đăng nhập** — kiểm ngày 29/08/2026.

---

## Kiểm trước khi bấm nộp

- [ ] Business Portfolio có app + Page + ≥2 admin
- [ ] Tên/địa chỉ khớp **từng chữ** với ĐKKD
- [ ] `contact_email` là `hotro@tourkit.vn`, không phải Gmail
- [ ] Hai đường dẫn chính sách mở được ở **cửa sổ ẩn danh** (không đăng nhập)
- [ ] Có tài khoản thử cho người duyệt, còn hạn
- [ ] Video có màn hình đồng ý của Facebook
- [ ] Bốn câu trả lời quyền đã dán, không sửa thành lời hứa quá tay
